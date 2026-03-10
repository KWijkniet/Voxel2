using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Two-tier LOD rendering:
///
///   LOD 0 (within viewDistance):
///     Individual 16³ chunk GameObjects, full-detail mesh.
///
///   LOD 1+ (beyond viewDistance, within MaxRadius):
///     4×vc×4 chunk groups (Regions) rendered as ONE GameObject per group.
///     Step = 4 voxels/cell at LOD 1, 8 at LOD 2, 16 at LOD 3.
///     Reduces draw calls from ~4096 to ~256 at the outer ring.
///
/// Example: viewDistance=8, lodLevels=2 → 8→16→32 chunk radii = 512 m max.
/// </summary>
public class VoxelWorld : MonoBehaviour
{
    [Header("World")]
    public int       viewDistance   = 8;
    public int       verticalChunks = 3;
    public Transform player;

    [Header("Terrain")]
    public float noiseScale    = 0.04f;
    public int   terrainHeight = 32;
    public int   baseHeight    = 8;

    [Header("Rendering")]
    public Material chunkMaterial;

    [Header("Async")]
    public int maxConcurrentTasks = 4;
    public int maxApplyPerFrame   = 2;

    [Header("LOD")]
    [Tooltip("Number of LOD levels for region rendering. Each level doubles the view radius.")]
    [Range(0, 3)]
    public int lodLevels = 2;

    [Header("Culling")]
    public int   frustumBypassRadius     = 2;
    [Range(0.9f, 1f)]
    public float cameraRotationThreshold = 0.98f;
    public bool  disableOutsideFrustum   = true;

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>
    /// viewDistance rounded up to the nearest RegionData.HSize (4) multiple.
    /// Ensures the LOD 0 / region boundary always falls on a region edge,
    /// preventing a region mesh from overlapping with individual chunk GOs.
    /// e.g. viewDistance=6 → AlignedViewDistance=8, viewDistance=8 → 8.
    /// </summary>
    public int AlignedViewDistance
    {
        get { int h = RegionData.HSize; return ((viewDistance + h - 1) / h) * h; }
    }

    private int MaxRadius => AlignedViewDistance * (1 << lodLevels);

    // ── LOD 0 storage (individual chunks) ─────────────────────────────────────

    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks        = new();
    private readonly Dictionary<Vector3Int, GameObject>   _renderers     = new();
    private readonly HashSet<Vector3Int>                  _desiredCoords = new();
    private readonly HashSet<Vector3Int>                  _inFlight      = new();
    private readonly ConcurrentQueue<ChunkBuildResult>    _readyQueue    = new();

    private readonly struct ChunkBuildResult
    {
        public readonly Vector3Int   Coord;
        public readonly PaletteChunk Chunk;
        public readonly MeshData     MeshData; // null = data-only (for region use)
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, MeshData m)
        { Coord = c; Chunk = ch; MeshData = m; }
    }

    // ── LOD 1+ storage (regions) ──────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, RegionData>  _regions          = new();
    private readonly Dictionary<Vector3Int, GameObject>  _regionRenderers  = new();
    private readonly Dictionary<Vector3Int, int>          _regionStep       = new();
    private readonly HashSet<Vector3Int>                  _desiredRegions   = new();
    private readonly HashSet<Vector3Int>                  _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult>   _regionReadyQueue = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int RegionCoord;
        public readonly MeshData   MeshData;
        public readonly int        Step;
        public RegionBuildResult(Vector3Int r, MeshData m, int s) { RegionCoord = r; MeshData = m; Step = s; }
    }

    private SemaphoreSlim _semaphore;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _lastCameraForward = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
        UpdateLoadedChunks(true);
    }

    private void Update()
    {
        // LOD must be checked first: it resets _lastPlayerChunk to a sentinel,
        // and CheckPositionChanged must then overwrite it with the real value
        // before UpdateLoadedChunks runs — preventing an Abs(int.MinValue) overflow.
        bool lodChanged  = CheckLodSettingsChanged();
        bool posChanged  = CheckPositionChanged();
        bool rotChanged  = CheckRotationChanged();
        if (posChanged || rotChanged || lodChanged) UpdateLoadedChunks(posChanged || lodChanged);
        UpdateFrustumVisibility();
        ApplyReadyChunks();
        ApplyReadyRegions();
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk   = new Vector3Int(int.MaxValue, 0, 0);
    private Vector3    _lastCameraForward = Vector3.forward;
    private int        _lastLodLevels     = -1;
    private int        _lastViewDistance  = -1;

    private bool CheckLodSettingsChanged()
    {
        if (lodLevels == _lastLodLevels && viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        // Force full rebuild by resetting the player chunk tracker
        _lastPlayerChunk  = new Vector3Int(int.MaxValue, 0, 0);
        return true;
    }

    private bool CheckPositionChanged()
    {
        var cur = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (cur == _lastPlayerChunk) return false;
        _lastPlayerChunk = cur;
        return true;
    }

    private bool CheckRotationChanged()
    {
        var cam = Camera.main;
        if (cam == null) return false;
        var fwd = cam.transform.forward;
        if (Vector3.Dot(fwd, _lastCameraForward) >= cameraRotationThreshold) return false;
        _lastCameraForward = fwd;
        return true;
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    private void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            // Rebuild desired sets
            _desiredCoords.Clear();
            _desiredRegions.Clear();
            int maxR = MaxRadius;

            for (int x = -maxR; x <= maxR; x++)
            for (int z = -maxR; z <= maxR; z++)
            {
                int dist = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
                var absChunk = new Vector3Int(_lastPlayerChunk.x + x, 0, _lastPlayerChunk.z + z);

                if (dist < AlignedViewDistance)
                {
                    for (int y = 0; y < verticalChunks; y++)
                        _desiredCoords.Add(new Vector3Int(absChunk.x, y, absChunk.z));
                }
                else
                {
                    // A region straddles the LOD boundary if any of its chunks are
                    // within AlignedViewDistance of the player. Render the whole region
                    // as individual LOD 0 chunks to prevent overlap with the region mesh.
                    var regionCoord = ChunkToRegionCoord(absChunk);
                    if (RegionContainsLod0Chunk(regionCoord))
                    {
                        for (int y = 0; y < verticalChunks; y++)
                            _desiredCoords.Add(new Vector3Int(absChunk.x, y, absChunk.z));
                    }
                    else
                    {
                        _desiredRegions.Add(regionCoord);
                    }
                }
            }

            // Unload out-of-range individual GOs
            var toUnloadC = new List<Vector3Int>();
            foreach (var c in _renderers.Keys)
                if (!_desiredCoords.Contains(c)) toUnloadC.Add(c);
            foreach (var c in toUnloadC) UnloadChunk(c);

            // Unload out-of-range region GOs
            var toUnloadR = new List<Vector3Int>();
            foreach (var r in _regionRenderers.Keys)
                if (!_desiredRegions.Contains(r)) toUnloadR.Add(r);
            foreach (var r in toUnloadR) UnloadRegion(r);

            // Evict chunk data
            int evictR = MaxRadius + 2;
            var toEvictC = new List<Vector3Int>();
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - _lastPlayerChunk.x) > evictR ||
                    Mathf.Abs(c.z - _lastPlayerChunk.z) > evictR) toEvictC.Add(c);
            foreach (var c in toEvictC) _chunks.Remove(c);

            // Evict region data
            var toEvictR = new List<Vector3Int>();
            foreach (var r in _regions.Keys)
                if (!_desiredRegions.Contains(r)) toEvictR.Add(r);
            foreach (var r in toEvictR) _regions.Remove(r);
        }

        // ── Request LOD 0 chunks ──────────────────────────────────────────────
        var toRequestC = new List<Vector3Int>();
        foreach (var coord in _desiredCoords)
            if (!_renderers.ContainsKey(coord) && !_inFlight.Contains(coord))
                toRequestC.Add(coord);

        var playerPos = player != null ? player.position : Vector3.zero;
        toRequestC.Sort((a, b) =>
            ChunkCenterWorld(a).sqrMagnitude_To(playerPos)
            .CompareTo(ChunkCenterWorld(b).sqrMagnitude_To(playerPos)));

        Plane[] planes = null;
        var cam = Camera.main;
        if (cam != null) planes = GeometryUtility.CalculateFrustumPlanes(cam);
        float bypassSq = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        foreach (var coord in toRequestC)
        {
            var   center = ChunkCenterWorld(coord);
            float distSq = (center - playerPos).sqrMagnitude;
            if (distSq <= bypassSq || planes == null ||
                GeometryUtility.TestPlanesAABB(planes, ChunkBounds(coord)))
                RequestChunk(coord, buildMesh: true);
        }

        // ── Process desired regions (sorted by distance, frustum culled) ─────────
        var regionsToProcess = new List<Vector3Int>();
        foreach (var regionCoord in _desiredRegions)
        {
            // Skip if already rendered at correct step or being built
            if (_regionInFlight.Contains(regionCoord)) continue;
            if (_regionRenderers.ContainsKey(regionCoord))
            {
                int wantedStep = GetRegionStep(regionCoord);
                if (_regionStep.TryGetValue(regionCoord, out int cur) && cur == wantedStep) continue;
            }

            // Frustum cull — regions are always beyond the bypass radius so no bypass check needed
            if (planes != null && !GeometryUtility.TestPlanesAABB(planes, RegionBounds(regionCoord)))
                continue;

            regionsToProcess.Add(regionCoord);
        }

        // Nearest regions first — ensures the closest LOD ring fills in before distant ones
        regionsToProcess.Sort((a, b) =>
            RegionCenterWorld(a).sqrMagnitude_To(playerPos)
            .CompareTo(RegionCenterWorld(b).sqrMagnitude_To(playerPos)));

        foreach (var regionCoord in regionsToProcess)
        {
            int wantedStep = GetRegionStep(regionCoord);

            // Re-mesh if LOD step changed
            if (_regionRenderers.ContainsKey(regionCoord))
            {
                UnloadRegion(regionCoord);
            }

            if (!_regions.TryGetValue(regionCoord, out var region))
            {
                region = new RegionData(verticalChunks);
                _regions[regionCoord] = region;
            }

            // Feed already-available chunk data; request the rest
            var baseChunk = RegionBaseChunkCoord(regionCoord);
            for (int lcx = 0; lcx < RegionData.HSize; lcx++)
            for (int lcy = 0; lcy < verticalChunks; lcy++)
            for (int lcz = 0; lcz < RegionData.HSize; lcz++)
            {
                if (region.HasChunk(lcx, lcy, lcz)) continue;
                var chunkCoord = new Vector3Int(baseChunk.x + lcx, lcy, baseChunk.z + lcz);
                if (_chunks.TryGetValue(chunkCoord, out var existing))
                    region.SetChunk(lcx, lcy, lcz, existing);
                else if (!_inFlight.Contains(chunkCoord))
                    RequestChunk(chunkCoord, buildMesh: false);
            }

            if (region.IsComplete)
                RequestRegionMesh(regionCoord, region, wantedStep);
        }
    }

    // ── LOD 0 chunk tasks ─────────────────────────────────────────────────────

    private void RequestChunk(Vector3Int coord, bool buildMesh)
    {
        _inFlight.Add(coord);
        var existing   = GetChunk(coord);
        var neighbours = buildMesh ? ChunkRenderer.FetchNeighbours(coord, GetChunk) : null;
        float ns = noiseScale; int th = terrainHeight; int bh = baseHeight;

        Task.Run(async () =>
        {
            await _semaphore.WaitAsync();
            try
            {
                var chunk = existing ?? GenerateChunkData(coord, ns, th, bh);
                var mesh  = buildMesh ? ChunkRenderer.BuildMeshData(chunk, neighbours, 0) : null;
                _readyQueue.Enqueue(new ChunkBuildResult(coord, chunk, mesh));
            }
            finally { _semaphore.Release(); }
        });
    }

    private void ApplyReadyChunks()
    {
        int applied = 0;
        while (applied < maxApplyPerFrame && _readyQueue.TryDequeue(out var result))
        {
            _inFlight.Remove(result.Coord);
            _chunks[result.Coord] = result.Chunk;

            if (result.MeshData != null)
            {
                // LOD 0 individual GO
                if (!_renderers.ContainsKey(result.Coord) && _desiredCoords.Contains(result.Coord))
                {
                    var go = new GameObject($"Chunk {result.Coord.x},{result.Coord.y},{result.Coord.z}");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = ChunkToWorldPos(result.Coord);
                    go.AddComponent<ChunkRenderer>().ApplyMeshData(result.MeshData, chunkMaterial);
                    _renderers[result.Coord] = go;
                }
            }

            // Always feed this chunk's data into its region — boundary chunks (at exactly
            // viewDistance) are processed as LOD 0 above but also belong to an adjacent region.
            // Without this, the region stays incomplete until the next UpdateLoadedChunks call.
            TryFeedChunkIntoRegion(result.Coord, result.Chunk);

            applied++;
        }
    }

    private void TryFeedChunkIntoRegion(Vector3Int coord, PaletteChunk chunk)
    {
        var regionCoord = ChunkToRegionCoord(coord);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        region.SetChunk(local.x, local.y, local.z, chunk);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionRenderers.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region, GetRegionStep(regionCoord));
    }

    // ── LOD 1+ region tasks ───────────────────────────────────────────────────

    private void RequestRegionMesh(Vector3Int regionCoord, RegionData region, int step)
    {
        _regionInFlight.Add(regionCoord);
        var neighbours = new RegionData[6];
        var dirs = ChunkRenderer.RegionNeighbourDirs;
        neighbours[0] = GetRegion(regionCoord + dirs[0]);
        neighbours[1] = GetRegion(regionCoord + dirs[1]);
        // [2] and [3] (±Y) stay null — regions span full height
        neighbours[4] = GetRegion(regionCoord + dirs[4]);
        neighbours[5] = GetRegion(regionCoord + dirs[5]);

        Task.Run(async () =>
        {
            await _semaphore.WaitAsync();
            try
            {
                var mesh = ChunkRenderer.BuildRegionMeshData(region, neighbours, step);
                _regionReadyQueue.Enqueue(new RegionBuildResult(regionCoord, mesh, step));
            }
            finally { _semaphore.Release(); }
        });
    }

    private void ApplyReadyRegions()
    {
        int applied = 0;
        while (applied < maxApplyPerFrame && _regionReadyQueue.TryDequeue(out var result))
        {
            _regionInFlight.Remove(result.RegionCoord);

            if (!_regionRenderers.ContainsKey(result.RegionCoord)
                && _desiredRegions.Contains(result.RegionCoord))
            {
                var go = new GameObject($"Region {result.RegionCoord.x},{result.RegionCoord.z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = RegionWorldPos(result.RegionCoord);
                go.AddComponent<ChunkRenderer>().ApplyMeshData(result.MeshData, chunkMaterial);
                _regionRenderers[result.RegionCoord] = go;
                _regionStep[result.RegionCoord]      = result.Step;
            }

            applied++;
        }
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_renderers.TryGetValue(coord, out var go)) { Destroy(go); _renderers.Remove(coord); }
    }

    private void UnloadRegion(Vector3Int r)
    {
        if (_regionRenderers.TryGetValue(r, out var go)) { Destroy(go); _regionRenderers.Remove(r); }
        _regionStep.Remove(r);
    }

    // ── Frustum visibility ────────────────────────────────────────────────────

    private void UpdateFrustumVisibility()
    {
        if (!disableOutsideFrustum) return;
        var cam = Camera.main;
        if (cam == null) return;

        var   planes    = GeometryUtility.CalculateFrustumPlanes(cam);
        var   playerPos = player != null ? player.position : Vector3.zero;
        float bypassSq  = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        foreach (var kvp in _renderers)
        {
            bool visible = (ChunkCenterWorld(kvp.Key) - playerPos).sqrMagnitude <= bypassSq
                           || GeometryUtility.TestPlanesAABB(planes, ChunkBounds(kvp.Key));
            if (kvp.Value.activeSelf != visible) kvp.Value.SetActive(visible);
        }
        foreach (var kvp in _regionRenderers)
        {
            bool visible = GeometryUtility.TestPlanesAABB(planes, RegionBounds(kvp.Key));
            if (kvp.Value.activeSelf != visible) kvp.Value.SetActive(visible);
        }
    }

    // ── Terrain generation ────────────────────────────────────────────────────

    private static PaletteChunk GenerateChunkData(Vector3Int coord,
                                                   float noiseScale, int terrainHeight, int baseHeight)
    {
        var chunk   = new PaletteChunk();
        int offsetX = coord.x * PaletteChunk.Size;
        int offsetY = coord.y * PaletteChunk.Size;
        int offsetZ = coord.z * PaletteChunk.Size;

        for (int z = 0; z < PaletteChunk.Size; z++)
        for (int x = 0; x < PaletteChunk.Size; x++)
        {
            float noise   = Mathf.PerlinNoise((offsetX + x) * noiseScale, (offsetZ + z) * noiseScale);
            int   surface = baseHeight + Mathf.RoundToInt(noise * terrainHeight);
            for (int y = 0; y < PaletteChunk.Size; y++)
            {
                int worldY = offsetY + y;
                byte block;
                if      (worldY > surface)      block = BlockType.Air;
                else if (worldY == surface)     block = BlockType.Grass;
                else if (worldY >= surface - 3) block = BlockType.Dirt;
                else                            block = BlockType.Stone;
                chunk.SetBlock(x, y, z, block);
            }
        }
        return chunk;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public PaletteChunk GetChunk(Vector3Int coord) =>
        _chunks.TryGetValue(coord, out var c) ? c : null;

    public RegionData GetRegion(Vector3Int regionCoord) =>
        _regions.TryGetValue(regionCoord, out var r) ? r : null;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Vector3Int WorldToChunkCoord(Vector3 p)
    {
        int s = PaletteChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / s), 0, Mathf.FloorToInt(p.z / s));
    }

    private static Vector3Int ChunkToRegionCoord(Vector3Int c) =>
        new Vector3Int(Mathf.FloorToInt(c.x / (float)RegionData.HSize), 0,
                       Mathf.FloorToInt(c.z / (float)RegionData.HSize));

    private static Vector3Int RegionBaseChunkCoord(Vector3Int r) =>
        new Vector3Int(r.x * RegionData.HSize, 0, r.z * RegionData.HSize);

    private static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = PaletteChunk.Size; return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    private static Vector3 RegionWorldPos(Vector3Int r)
    {
        float s = PaletteChunk.Size * RegionData.HSize; return new Vector3(r.x * s, 0, r.z * s);
    }

    private static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = PaletteChunk.Size;
        return new Vector3(c.x * s + s * 0.5f, c.y * s + s * 0.5f, c.z * s + s * 0.5f);
    }

    private static Vector3 RegionCenterWorld(Vector3Int r)
    {
        float hs = PaletteChunk.Size * RegionData.HSize;
        return new Vector3(r.x * hs + hs * 0.5f, 0, r.z * hs + hs * 0.5f);
    }

    private static Bounds ChunkBounds(Vector3Int c) =>
        new Bounds(ChunkCenterWorld(c), Vector3.one * PaletteChunk.Size);

    private static Bounds RegionBounds(Vector3Int r)
    {
        float hs = PaletteChunk.Size * RegionData.HSize;
        return new Bounds(RegionCenterWorld(r), new Vector3(hs, hs, hs));
    }

    /// <summary>
    /// Returns true if the region's nearest chunk to the player is within the LOD 0 zone.
    /// Such regions straddle the LOD boundary and must be rendered as individual LOD 0
    /// chunks rather than a single region mesh, to avoid overlap with adjacent LOD 0 chunks.
    /// </summary>
    private bool RegionContainsLod0Chunk(Vector3Int regionCoord)
    {
        int h  = RegionData.HSize;
        int px = _lastPlayerChunk.x, pz = _lastPlayerChunk.z;
        int nx = Mathf.Clamp(px, regionCoord.x * h, regionCoord.x * h + h - 1);
        int nz = Mathf.Clamp(pz, regionCoord.z * h, regionCoord.z * h + h - 1);
        return Mathf.Max(Mathf.Abs(nx - px), Mathf.Abs(nz - pz)) < AlignedViewDistance;
    }

    private int GetRegionStep(Vector3Int regionCoord)
    {
        var baseChunk = RegionBaseChunkCoord(regionCoord);
        int dist = Mathf.Max(Mathf.Abs(baseChunk.x - _lastPlayerChunk.x),
                             Mathf.Abs(baseChunk.z - _lastPlayerChunk.z));
        int radius = AlignedViewDistance; int step = 4;
        for (int lod = 1; lod <= lodLevels; lod++)
        {
            if (dist <= radius * 2) return step;
            radius *= 2; step *= 2;
        }
        return step;
    }
}

internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
