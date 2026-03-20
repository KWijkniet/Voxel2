using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

internal sealed class ChunkStreamer
{
    private readonly VoxelWorld _world;
    private readonly WorldFlags _flags;
    private readonly HashSet<Vector3Int> _desiredCoords;
    private readonly HashSet<Vector3Int> _desiredRegions;
    private readonly HashSet<Vector3Int> _pendingCoords;
    private readonly HashSet<Vector3Int> _inFlight;
    private readonly Dictionary<Vector3Int, VoxelChunk> _chunks;
    private readonly Dictionary<Vector3Int, RegionData>  _regions;
    private readonly Dictionary<Vector3Int, Mesh>        _chunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _regionMeshes;
    private readonly Action<Vector3Int> _unloadChunkMesh;
    private readonly Action<Vector3Int> _unloadRegionMesh;

    // Setter-injected to avoid circular constructor args
    public ChunkPipelineProcessor Pipeline { private get; set; }
    public RegionManager          Regions  { private get; set; }

    private int _lastLodLevels    = -1;
    private int _lastViewDistance = -1;

    public int PendingCount => _pendingCoords.Count;

    // Scratch lists — owned by ChunkStreamer
    private readonly List<Vector3Int> _scratchUnloadC    = new();
    private readonly List<Vector3Int> _scratchUnloadR    = new();
    private readonly List<Vector3Int> _scratchEvictC     = new();
    private readonly List<Vector3Int> _scratchEvictR     = new();
    private readonly List<Vector3Int> _scratchRequestC   = new();
    private readonly List<Vector3Int> _scratchDataCoords = new();
    private readonly List<(float dist, Vector3Int coord)> _scratchRegions   = new();
    private readonly List<(float dist, Vector3Int coord)> _scratchChunkSort = new();

    public ChunkStreamer(
        VoxelWorld world,
        WorldFlags flags,
        HashSet<Vector3Int> desiredCoords,
        HashSet<Vector3Int> desiredRegions,
        HashSet<Vector3Int> pendingCoords,
        HashSet<Vector3Int> inFlight,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        Dictionary<Vector3Int, RegionData>  regions,
        Dictionary<Vector3Int, Mesh>        chunkMeshes,
        Dictionary<Vector3Int, Mesh>        regionMeshes,
        Action<Vector3Int> unloadChunkMesh,
        Action<Vector3Int> unloadRegionMesh)
    {
        _world            = world;
        _flags            = flags;
        _desiredCoords    = desiredCoords;
        _desiredRegions   = desiredRegions;
        _pendingCoords    = pendingCoords;
        _inFlight         = inFlight;
        _chunks           = chunks;
        _regions          = regions;
        _chunkMeshes      = chunkMeshes;
        _regionMeshes     = regionMeshes;
        _unloadChunkMesh  = unloadChunkMesh;
        _unloadRegionMesh = unloadRegionMesh;
    }

    public bool CheckLodSettingsChanged()
    {
        if (_world.lodLevels == _lastLodLevels && _world.viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = _world.lodLevels;
        _lastViewDistance = _world.viewDistance;
        _world.LastPlayerChunk = new Vector3Int(int.MaxValue, 0, 0);
        return true;
    }

    public bool CheckPositionChanged()
    {
        var cur = VoxelCoords.WorldToChunkCoord(_world.player != null ? _world.player.position : Vector3.zero);
        if (cur == _world.LastPlayerChunk) return false;
        _world.LastPlayerChunk = cur;
        return true;
    }

    public void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            Profiler.BeginSample("ULC.BuildDesiredSets");
            _desiredCoords.Clear();
            _desiredRegions.Clear();
            int vd = _world.AlignedViewDistance;

            // LOD 0 zone
            for (int x = -(vd - 1); x <= vd - 1; x++)
            for (int z = -(vd - 1); z <= vd - 1; z++)
            for (int y = 0; y < _world.verticalChunks; y++)
                _desiredCoords.Add(new Vector3Int(_world.LastPlayerChunk.x + x, y, _world.LastPlayerChunk.z + z));

            // LOD 1+ rings
            for (int lod = 1; lod <= _world.lodLevels; lod++)
            {
                int hSize       = 1 << lod;
                int innerRadius = vd * (1 << (lod - 1));
                int outerRadius = vd * (1 << lod);
                int playerRX    = Mathf.FloorToInt(_world.LastPlayerChunk.x / (float)hSize);
                int playerRZ    = Mathf.FloorToInt(_world.LastPlayerChunk.z / (float)hSize);
                int maxRegionR  = outerRadius / hSize + 1;

                for (int rx = -maxRegionR; rx <= maxRegionR; rx++)
                for (int rz = -maxRegionR; rz <= maxRegionR; rz++)
                {
                    var regionCoord = new Vector3Int(playerRX + rx, lod, playerRZ + rz);
                    var baseChunk   = VoxelCoords.RegionBaseChunkCoord(regionCoord);
                    int nearestX    = Mathf.Clamp(_world.LastPlayerChunk.x, baseChunk.x, baseChunk.x + hSize - 1);
                    int nearestZ    = Mathf.Clamp(_world.LastPlayerChunk.z, baseChunk.z, baseChunk.z + hSize - 1);
                    int chebDist    = Mathf.Max(Mathf.Abs(nearestX - _world.LastPlayerChunk.x),
                                                Mathf.Abs(nearestZ - _world.LastPlayerChunk.z));

                    // Use farthest corner for the inner-boundary test: a region is excluded only
                    // when ALL of its chunks fall inside the LOD0 zone (farDist < innerRadius).
                    // Using nearestDist caused a 1-chunk gap on negative axes because floor-div
                    // shifts the nearest corner one step inside the LOD0 boundary there.
                    int farX = Mathf.Max(Mathf.Abs(baseChunk.x - _world.LastPlayerChunk.x),
                                         Mathf.Abs(baseChunk.x + hSize - 1 - _world.LastPlayerChunk.x));
                    int farZ = Mathf.Max(Mathf.Abs(baseChunk.z - _world.LastPlayerChunk.z),
                                         Mathf.Abs(baseChunk.z + hSize - 1 - _world.LastPlayerChunk.z));
                    int farDist = Mathf.Max(farX, farZ);

                    if (chebDist >= outerRadius || farDist < innerRadius) continue;
                    _desiredRegions.Add(regionCoord);
                }
            }
            Profiler.EndSample();

            Profiler.BeginSample("ULC.UnloadMeshes");
            // Unload out-of-range chunk meshes
            _scratchUnloadC.Clear();
            foreach (var c in _chunkMeshes.Keys)
                if (!_desiredCoords.Contains(c)) _scratchUnloadC.Add(c);
            foreach (var c in _scratchUnloadC) _unloadChunkMesh(c);

            // Unload out-of-range region meshes
            _scratchUnloadR.Clear();
            foreach (var r in _regionMeshes.Keys)
                if (!_desiredRegions.Contains(r)) _scratchUnloadR.Add(r);
            foreach (var r in _scratchUnloadR) _unloadRegionMesh(r);
            Profiler.EndSample();

            Profiler.BeginSample("ULC.EvictData");
            // Evict chunk data (and dispose NativeArrays)
            int evictR = _world.AlignedViewDistance * (1 << _world.lodLevels) + 2;
            _scratchEvictC.Clear();
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - _world.LastPlayerChunk.x) > evictR ||
                    Mathf.Abs(c.z - _world.LastPlayerChunk.z) > evictR) _scratchEvictC.Add(c);
            foreach (var c in _scratchEvictC)
            { _chunks[c].Dispose(); _chunks.Remove(c); }

            // Evict region data
            _scratchEvictR.Clear();
            foreach (var r in _regions.Keys)
                if (!_desiredRegions.Contains(r)) _scratchEvictR.Add(r);
            foreach (var r in _scratchEvictR) _regions.Remove(r);
            Profiler.EndSample();

            // Rebuild pending set: desired coords that still need a LOD0 mesh and aren't in-flight.
            // Done once per position change; maintained incrementally by SubmitSubBatch and
            // ProcessCompletedPipelines, so cascade calls never re-iterate _desiredCoords.
            _pendingCoords.Clear();
            foreach (var c in _desiredCoords)
                if (!_chunkMeshes.ContainsKey(c) && !_inFlight.Contains(c))
                    _pendingCoords.Add(c);
        }

        var playerPos = _world.player != null ? _world.player.position : Vector3.zero;

        // ── Request LOD 0 chunks ──────────────────────────────────────────────

        // Compute availability first — if pipeline slots are full there is nothing to submit
        // and we can skip the entire HashSet iteration + sort.
        int lod0Budget    = Mathf.Max(4, _world.maxRequestsPerUpdate / 2);
        int lod0Avail     = Mathf.Min(lod0Budget, Mathf.Max(0, _world.maxPipelinesInFlight - Pipeline.PipelineCount));
        int lod0Submitted = 0;

        Profiler.BeginSample("ULC.GatherAndSortLOD0");
        _scratchRequestC.Clear();
        if (lod0Avail > 0)
        {
            Profiler.BeginSample("LOD0.Gather");
            _scratchChunkSort.Clear();
            foreach (var coord in _pendingCoords)
                // Skip coords currently in-flight as data-only (they stay in _pendingCoords until
                // their data pipeline completes, then will be re-gathered for a mesh pipeline).
                if (!_inFlight.Contains(coord))
                    _scratchChunkSort.Add((VoxelCoords.ChunkCenterWorld(coord).sqrMagnitude_To(playerPos), coord));
            Profiler.EndSample();

            if (_scratchChunkSort.Count > 1)
            {
                Profiler.BeginSample("LOD0.Sort");
                _scratchChunkSort.Sort((a, b) => a.dist.CompareTo(b.dist));
                Profiler.EndSample();
            }

            Profiler.BeginSample("LOD0.Extract");
            int extractCount = Mathf.Min(lod0Avail, _scratchChunkSort.Count);
            for (int i = 0; i < extractCount; i++)
                _scratchRequestC.Add(_scratchChunkSort[i].coord);
            Profiler.EndSample();
        }
        Profiler.EndSample();

        int lod0Count  = _scratchRequestC.Count;
        lod0Submitted  = lod0Count;

        if (lod0Count > 0)
        {
            Profiler.BeginSample("ULC.SubmitLOD0Batch");
            Pipeline.SubmitChunkBatch(_scratchRequestC, buildMesh: true);
            Profiler.EndSample();
        }

        // ── Process desired regions ───────────────────────────────────────────
        Profiler.BeginSample("ULC.GatherAndSortRegions");
        _scratchRegions.Clear();
        foreach (var r in _desiredRegions)
        {
            if (Regions.IsRegionInFlight(r) || _regionMeshes.ContainsKey(r)) continue;
            _scratchRegions.Add((VoxelCoords.RegionCenterWorld(r).sqrMagnitude_To(playerPos), r));
        }
        _scratchRegions.Sort((a, b) => a.dist.CompareTo(b.dist));
        Profiler.EndSample();

        int regionBudget    = Mathf.Max(4, _world.maxRequestsPerUpdate / 2);
        int regionSubmitted = 0;

        // Collect data-only chunk requests from all regions, then submit as one batch
        _scratchDataCoords.Clear();

        Profiler.BeginSample("ULC.FillRegionData");
        foreach (var (_, regionCoord) in _scratchRegions)
        {
            int hSize = 1 << regionCoord.y;

            if (!_regions.TryGetValue(regionCoord, out var region))
            {
                region = new RegionData(_world.verticalChunks, hSize);
                _regions[regionCoord] = region;
            }
            else if (regionSubmitted >= regionBudget)
            {
                if (region.IsComplete) Regions.RequestRegionMesh(regionCoord, region);
                continue;
            }

            var baseChunk = VoxelCoords.RegionBaseChunkCoord(regionCoord);
            for (int lcx = 0; lcx < hSize; lcx++)
            for (int lcy = 0; lcy < _world.verticalChunks; lcy++)
            for (int lcz = 0; lcz < hSize; lcz++)
            {
                if (region.HasChunk(lcx, lcy, lcz)) continue;
                var chunkCoord = new Vector3Int(baseChunk.x + lcx, lcy, baseChunk.z + lcz);
                if (_chunks.TryGetValue(chunkCoord, out var existing))
                {
                    var bytes = new byte[VoxelChunk.VoxelCount];
                    existing.CopyTo(bytes);
                    region.SetChunk(lcx, lcy, lcz, bytes);
                }
                else if (!_inFlight.Contains(chunkCoord) && regionSubmitted < regionBudget
                         && !_scratchDataCoords.Contains(chunkCoord))
                {
                    _scratchDataCoords.Add(chunkCoord);
                    regionSubmitted++;
                }
            }

            if (region.IsComplete) Regions.RequestRegionMesh(regionCoord, region);
        }
        Profiler.EndSample();

        // Submit all data-only region chunks as a single batch
        if (_scratchDataCoords.Count > 0)
        {
            Profiler.BeginSample("ULC.SubmitRegionDataBatch");
            Pipeline.SubmitChunkBatch(_scratchDataCoords, buildMesh: false);
            Profiler.EndSample();
        }

        _flags.NeedsMoreRequests = (lod0Submitted >= lod0Budget) || (regionSubmitted >= regionBudget);
    }
}
