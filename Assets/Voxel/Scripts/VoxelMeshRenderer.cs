using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns draw lists and drives Graphics.DrawMesh each frame.
/// Also manages stale-mesh eviction and the UnloadChunkMesh / UnloadRegionMesh lifecycle.
/// All methods must be called from the main thread only.
/// </summary>
internal sealed class VoxelMeshRenderer
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<Vector3Int, Mesh> _chunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _transChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _vegChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _regionMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _transRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleRegionMeshes;
    private readonly WorldFlags _flags;
    private RegionManager _regionMgr; // set after construction

    private readonly List<(Mesh mesh, Matrix4x4 trs)> _chunkDrawList       = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _transChunkDrawList  = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _vegDrawList         = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _regionDrawList      = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _transRegionDrawList = new();
    private Matrix4x4 _cachedL2W = Matrix4x4.zero;

    // Own scratch lists — separate from ChunkStreamer scratch lists
    private readonly List<Vector3Int> _scratchStaleChunks  = new();

    public VoxelMeshRenderer(
        VoxelWorld world,
        Dictionary<Vector3Int, Mesh> chunkMeshes,
        Dictionary<Vector3Int, Mesh> transChunkMeshes,
        Dictionary<Vector3Int, Mesh> vegChunkMeshes,
        Dictionary<Vector3Int, Mesh> regionMeshes,
        Dictionary<Vector3Int, Mesh> transRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleChunkMeshes,
        Dictionary<Vector3Int, Mesh> staleRegionMeshes,
        WorldFlags flags)
    {
        _world             = world;
        _chunkMeshes       = chunkMeshes;
        _transChunkMeshes  = transChunkMeshes;
        _vegChunkMeshes    = vegChunkMeshes;
        _regionMeshes      = regionMeshes;
        _transRegionMeshes = transRegionMeshes;
        _staleChunkMeshes  = staleChunkMeshes;
        _staleRegionMeshes = staleRegionMeshes;
        _flags             = flags;
    }

    /// <summary>Set after construction to avoid circular constructor arguments.</summary>
    public RegionManager RegionMgr { set => _regionMgr = value; }

    public void DrawAllMeshes()
    {
        if (_world.chunkMaterial == null) return;
        var localToWorld = _world.transform.localToWorldMatrix;

        if (_flags.DrawListDirty || localToWorld != _cachedL2W)
        {
            _cachedL2W = localToWorld;
            _flags.DrawListDirty = false;

            _chunkDrawList.Clear();
            foreach (var kvp in _staleChunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));
            foreach (var kvp in _chunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));

            _regionDrawList.Clear();
            foreach (var kvp in _staleRegionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));
            foreach (var kvp in _regionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));

            _transChunkDrawList.Clear();
            foreach (var kvp in _transChunkMeshes)
                if (kvp.Value != null)
                    _transChunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));

            _vegDrawList.Clear();
            foreach (var kvp in _vegChunkMeshes)
                if (kvp.Value != null)
                    _vegDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));

            _transRegionDrawList.Clear();
            foreach (var kvp in _transRegionMeshes)
                if (kvp.Value != null)
                    _transRegionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));
        }

        var transMat = _world.transparentMaterial != null ? _world.transparentMaterial : _world.chunkMaterial;
        var vegMat   = _world.vegetationMaterial;
        int layer = _world.gameObject.layer;
        foreach (var (mesh, trs) in _chunkDrawList)
            Graphics.DrawMesh(mesh, trs, _world.chunkMaterial, layer);
        foreach (var (mesh, trs) in _regionDrawList)
            Graphics.DrawMesh(mesh, trs, _world.chunkMaterial, layer);
        foreach (var (mesh, trs) in _transChunkDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
        foreach (var (mesh, trs) in _transRegionDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
        if (vegMat != null)
            foreach (var (mesh, trs) in _vegDrawList)
                Graphics.DrawMesh(mesh, trs, vegMat, layer);
    }

    public void UnloadChunkMesh(Vector3Int coord)
    {
        if (_chunkMeshes.TryGetValue(coord, out var mesh))
        {
            _chunkMeshes.Remove(coord);
            if (mesh != null) _staleChunkMeshes[coord] = mesh;
            _flags.DrawListDirty = true;
        }
        if (_transChunkMeshes.TryGetValue(coord, out var tmesh))
        {
            _transChunkMeshes.Remove(coord);
            if (tmesh != null) Object.Destroy(tmesh);
            _flags.DrawListDirty = true;
        }
        if (_vegChunkMeshes.TryGetValue(coord, out var vmesh))
        {
            _vegChunkMeshes.Remove(coord);
            if (vmesh != null) Object.Destroy(vmesh);
            _flags.DrawListDirty = true;
        }
    }

    public void UnloadRegionMesh(Vector3Int r)
    {
        if (_regionMeshes.TryGetValue(r, out var mesh))
        {
            _regionMeshes.Remove(r);
            if (mesh != null) _staleRegionMeshes[r] = mesh;
            _flags.DrawListDirty = true;
        }
        if (_transRegionMeshes.TryGetValue(r, out var tmesh))
        {
            _transRegionMeshes.Remove(r);
            if (tmesh != null) Object.Destroy(tmesh);
            _flags.DrawListDirty = true;
        }
    }

    /// <summary>
    /// Distance-based eviction of stale chunk meshes. Delegates stale region eviction
    /// to RegionManager. Must be called from the main thread.
    /// </summary>
    public void EvictStaleMeshes()
    {
        int vd = _world.AlignedViewDistance;
        var pc = _world.LastPlayerChunk;

        _scratchStaleChunks.Clear();
        foreach (var c in _staleChunkMeshes.Keys)
            if (Mathf.Abs(c.x - pc.x) > vd + 2 || Mathf.Abs(c.z - pc.z) > vd + 2)
                _scratchStaleChunks.Add(c);
        foreach (var c in _scratchStaleChunks)
        {
            Object.Destroy(_staleChunkMeshes[c]);
            _staleChunkMeshes.Remove(c);
            _flags.DrawListDirty = true;
        }

        _regionMgr?.EvictStaleRegionMeshes();
    }
}

