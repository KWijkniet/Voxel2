using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

internal sealed class RegionManager
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<Vector3Int, RegionData>  _regions;
    private readonly Dictionary<Vector3Int, Mesh>        _regionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _transRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _staleRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _staleChunkMeshes;
    private readonly HashSet<Vector3Int>                 _desiredRegions;
    private readonly WorldFlags                          _flags;

    private readonly HashSet<Vector3Int>                _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult> _regionReadyQueue = new();
    private readonly ConcurrentQueue<Vector3Int>        _cancelledRegions = new();
    private SemaphoreSlim           _regionSemaphore;
    private CancellationTokenSource _regionCts = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int       RegionCoord;
        public readonly WritableMeshData MeshData;
        public readonly WritableMeshData TransMeshData;
        public RegionBuildResult(Vector3Int r, WritableMeshData m, WritableMeshData t)
        { RegionCoord = r; MeshData = m; TransMeshData = t; }
    }

    // Own scratch lists for region stale eviction
    private readonly List<Vector3Int> _scratchStaleRegions = new();

    public RegionManager(
        VoxelWorld world,
        Dictionary<Vector3Int, RegionData> regions,
        Dictionary<Vector3Int, Mesh> regionMeshes,
        Dictionary<Vector3Int, Mesh> transRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleChunkMeshes,
        HashSet<Vector3Int> desiredRegions,
        WorldFlags flags)
    {
        _world             = world;
        _regions           = regions;
        _regionMeshes      = regionMeshes;
        _transRegionMeshes = transRegionMeshes;
        _staleRegionMeshes = staleRegionMeshes;
        _staleChunkMeshes  = staleChunkMeshes;
        _desiredRegions    = desiredRegions;
        _flags             = flags;
        _regionSemaphore   = new SemaphoreSlim(world.maxRegionTasks, world.maxRegionTasks);
    }

    public void TryFeedChunkIntoRegion(Vector3Int coord, VoxelChunk chunk)
    {
        int dist     = Mathf.Max(Mathf.Abs(coord.x - _world.LastPlayerChunk.x),
                                  Mathf.Abs(coord.z - _world.LastPlayerChunk.z));
        int lod      = 0;
        int boundary = _world.AlignedViewDistance;
        while (lod < _world.lodLevels && dist >= boundary) { lod++; boundary *= 2; }
        if (lod == 0) return;

        var regionCoord = VoxelCoords.ChunkToRegionCoord(coord, lod);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - VoxelCoords.RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        var bytes = new byte[VoxelChunk.VoxelCount];
        chunk.CopyTo(bytes);
        region.SetChunk(local.x, local.y, local.z, bytes);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionMeshes.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region);
    }

    public void RequestRegionMesh(Vector3Int regionCoord, RegionData region)
    {
        _regionInFlight.Add(regionCoord);
        int step = 1 << regionCoord.y;

        var neighbours = new RegionData[6];
        var dirs = MeshBuilder.RegionNeighbourDirs;
        neighbours[0] = GetRegion(regionCoord + dirs[0]);
        neighbours[1] = GetRegion(regionCoord + dirs[1]);
        neighbours[4] = GetRegion(regionCoord + dirs[4]);
        neighbours[5] = GetRegion(regionCoord + dirs[5]);

        var token = _regionCts.Token;
        Task.Run(async () =>
        {
            try { await _regionSemaphore.WaitAsync(token); }
            catch (OperationCanceledException) { _cancelledRegions.Enqueue(regionCoord); return; }
            try
            {
                var (mesh, transMesh) = MeshBuilder.BuildRegionMeshData(region, neighbours, step);
                _regionReadyQueue.Enqueue(new RegionBuildResult(regionCoord, mesh, transMesh));
            }
            finally { _regionSemaphore.Release(); }
        });
    }

    public bool ApplyReadyRegions()
    {
        int  applied  = 0;
        bool anyAdded = false;
        while (applied < _world.maxApplyPerFrame && _regionReadyQueue.TryDequeue(out var result))
        {
            _regionInFlight.Remove(result.RegionCoord);

            if (!_regionMeshes.ContainsKey(result.RegionCoord)
                && _desiredRegions.Contains(result.RegionCoord))
            {
                _regionMeshes[result.RegionCoord]      = MeshBuilder.CreateMesh(result.MeshData);
                _transRegionMeshes[result.RegionCoord] = MeshBuilder.CreateMesh(result.TransMeshData);

                if (_staleRegionMeshes.TryGetValue(result.RegionCoord, out var stale))
                { Object.Destroy(stale); _staleRegionMeshes.Remove(result.RegionCoord); }

                var baseC = VoxelCoords.RegionBaseChunkCoord(result.RegionCoord);
                int hs    = 1 << result.RegionCoord.y;
                for (int cx = baseC.x; cx < baseC.x + hs; cx++)
                for (int cz = baseC.z; cz < baseC.z + hs; cz++)
                for (int cy = 0; cy < _world.verticalChunks; cy++)
                {
                    var c = new Vector3Int(cx, cy, cz);
                    if (_staleChunkMeshes.TryGetValue(c, out var sc))
                    { Object.Destroy(sc); _staleChunkMeshes.Remove(c); }
                }
                _flags.DrawListDirty = true;
                anyAdded = true;
            }
            else
            {
                result.MeshData?.Discard();
                result.TransMeshData?.Discard();
            }
            applied++;
        }
        return anyAdded;
    }

    public bool DrainCancelledRegions()
    {
        bool any = false;
        while (_cancelledRegions.TryDequeue(out var r))
        { _regionInFlight.Remove(r); any = true; }
        return any;
    }

    public bool IsRegionInFlight(Vector3Int regionCoord) => _regionInFlight.Contains(regionCoord);

    /// <summary>Cancel and recreate CTS. Called when the player moves.</summary>
    public void ResetCancellation()
    {
        _regionCts.Cancel();
        _regionCts.Dispose();
        _regionCts = new CancellationTokenSource();
    }

    /// <summary>Cancel only — no recreate. Called from OnDestroy.</summary>
    public void CancelCts() => _regionCts.Cancel();

    public RegionData GetRegion(Vector3Int regionCoord) =>
        _regions.TryGetValue(regionCoord, out var r) ? r : null;

    /// <summary>Distance-based eviction of stale region meshes. Called by VoxelMeshRenderer.</summary>
    public void EvictStaleRegionMeshes()
    {
        int vd = _world.AlignedViewDistance;
        var pc = _world.LastPlayerChunk;

        _scratchStaleRegions.Clear();
        foreach (var r in _staleRegionMeshes.Keys)
        {
            int lod         = r.y;
            int outerRadius = vd * (1 << lod) + (1 << lod);
            var baseC  = VoxelCoords.RegionBaseChunkCoord(r);
            int hs     = 1 << lod;
            int nearX  = Mathf.Clamp(pc.x, baseC.x, baseC.x + hs - 1);
            int nearZ  = Mathf.Clamp(pc.z, baseC.z, baseC.z + hs - 1);
            int dist   = Mathf.Max(Mathf.Abs(nearX - pc.x), Mathf.Abs(nearZ - pc.z));
            if (dist > outerRadius) _scratchStaleRegions.Add(r);
        }
        foreach (var r in _scratchStaleRegions)
        { Object.Destroy(_staleRegionMeshes[r]); _staleRegionMeshes.Remove(r); _flags.DrawListDirty = true; }
    }

    /// <summary>Dispose semaphore and CTS. Call after CompleteAndDisposeAll in OnDestroy.</summary>
    public void CompleteAndDispose()
    {
        _regionSemaphore.Dispose();
        _regionCts.Dispose();
    }
}
