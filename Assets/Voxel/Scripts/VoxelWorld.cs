using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages an infinite procedurally generated voxel world with LOD.
///
/// LOD radius is geometric: each level doubles the previous radius.
///   LOD 0: 0 … viewDistance chunks          (step 1 — full detail)
///   LOD 1: viewDistance … viewDistance*2    (step 2)
///   LOD 2: viewDistance*2 … viewDistance*4  (step 4)
///   LOD 3: viewDistance*4 … viewDistance*8  (step 8)
///
/// Example: viewDistance=8, lodLevels=3 → max visible radius = 64 chunks = 1024 m.
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
    [Tooltip("Number of LOD levels beyond LOD 0. Each level doubles the view radius.")]
    [Range(0, 3)]
    public int lodLevels = 2;

    [Header("Culling")]
    public int   frustumBypassRadius     = 2;
    [Range(0.9f, 1f)]
    public float cameraRotationThreshold = 0.98f;
    public bool  disableOutsideFrustum   = true;

    // ── Storage ───────────────────────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks      = new();
    private readonly Dictionary<Vector3Int, GameObject>   _renderers   = new();
    private readonly Dictionary<Vector3Int, int>          _rendererLod = new(); // current LOD of each GO
    private readonly HashSet<Vector3Int>                  _desiredCoords = new();

    // ── Async pipeline ────────────────────────────────────────────────────────

    private readonly HashSet<Vector3Int>               _inFlight   = new();
    private readonly ConcurrentQueue<ChunkBuildResult> _readyQueue = new();
    private SemaphoreSlim _semaphore;

    private readonly struct ChunkBuildResult
    {
        public readonly Vector3Int   Coord;
        public readonly PaletteChunk Chunk;
        public readonly MeshData     MeshData;
        public readonly int          LodLevel;
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, MeshData m, int lod)
        { Coord = c; Chunk = ch; MeshData = m; LodLevel = lod; }
    }

    // ── Derived values ────────────────────────────────────────────────────────

    // Maximum chunk radius considering all LOD levels: viewDistance * 2^lodLevels
    private int MaxRadius => viewDistance * (1 << lodLevels);

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _lastCameraForward = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
        UpdateLoadedChunks(positionChanged: true);
    }

    private void Update()
    {
        bool posChanged = CheckPositionChanged();
        bool rotChanged = CheckRotationChanged();
        if (posChanged || rotChanged) UpdateLoadedChunks(positionChanged: posChanged);
        UpdateFrustumVisibility();
        ApplyReadyChunks();
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk  = new Vector3Int(int.MaxValue, 0, 0);
    private Vector3    _lastCameraForward = Vector3.forward;

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

    // ── LOD ───────────────────────────────────────────────────────────────────

    /// <summary>Returns the LOD level (0 = full detail) for a chunk at the given coord.</summary>
    private int GetLodLevel(Vector3Int coord)
    {
        int dx   = Mathf.Abs(coord.x - _lastPlayerChunk.x);
        int dz   = Mathf.Abs(coord.z - _lastPlayerChunk.z);
        int dist = Mathf.Max(dx, dz); // Chebyshev distance in chunk-space

        int radius = viewDistance;
        for (int lod = 0; lod <= lodLevels; lod++)
        {
            if (dist <= radius) return lod;
            radius *= 2;
        }
        return lodLevels; // beyond all levels — shouldn't happen if desired set is correct
    }

    // ── Chunk streaming ───────────────────────────────────────────────────────

    private void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            // Rebuild desired set to cover all LOD radii
            _desiredCoords.Clear();
            int maxR = MaxRadius;
            for (int x = -maxR; x <= maxR; x++)
            for (int z = -maxR; z <= maxR; z++)
            for (int y = 0; y < verticalChunks; y++)
                _desiredCoords.Add(new Vector3Int(_lastPlayerChunk.x + x, y, _lastPlayerChunk.z + z));

            // Unload GOs no longer desired
            var toUnload = new List<Vector3Int>();
            foreach (var coord in _renderers.Keys)
                if (!_desiredCoords.Contains(coord)) toUnload.Add(coord);
            foreach (var coord in toUnload) UnloadChunk(coord);

            // Evict chunk data beyond keep radius
            int evictR = MaxRadius + 2;
            var toEvict = new List<Vector3Int>();
            foreach (var coord in _chunks.Keys)
                if (Mathf.Abs(coord.x - _lastPlayerChunk.x) > evictR ||
                    Mathf.Abs(coord.z - _lastPlayerChunk.z) > evictR)
                    toEvict.Add(coord);
            foreach (var coord in toEvict) _chunks.Remove(coord);
        }

        // ── Determine which chunks need (re)loading ───────────────────────────
        var toRequest = new List<(Vector3Int coord, int lod)>();

        foreach (var coord in _desiredCoords)
        {
            int wantedLod = GetLodLevel(coord);

            bool alreadyCorrect = _rendererLod.TryGetValue(coord, out int currentLod)
                                  && currentLod == wantedLod;
            bool inFlight = _inFlight.Contains(coord);

            if (!alreadyCorrect && !inFlight)
            {
                // Re-mesh if LOD changed (remove the outdated GO, re-queue)
                if (_renderers.ContainsKey(coord))
                    UnloadChunk(coord);

                toRequest.Add((coord, wantedLod));
            }
        }

        if (toRequest.Count == 0) return;

        // ── Distance sort (nearest first) ─────────────────────────────────────
        var playerPos = player != null ? player.position : Vector3.zero;
        toRequest.Sort((a, b) =>
            ChunkCenterWorld(a.coord).sqrMagnitude_To(playerPos)
            .CompareTo(ChunkCenterWorld(b.coord).sqrMagnitude_To(playerPos)));

        // ── Frustum culling ───────────────────────────────────────────────────
        Plane[] frustumPlanes = null;
        var cam = Camera.main;
        if (cam != null) frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

        float bypassSq = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        foreach (var (coord, lod) in toRequest)
        {
            var   center = ChunkCenterWorld(coord);
            float distSq = (center - playerPos).sqrMagnitude;
            if (distSq <= bypassSq || frustumPlanes == null ||
                GeometryUtility.TestPlanesAABB(frustumPlanes, ChunkBounds(coord)))
            {
                RequestChunk(coord, lod);
            }
        }
    }

    private void RequestChunk(Vector3Int coord, int lodLevel)
    {
        _inFlight.Add(coord);

        // Data may already be cached — we only need to re-mesh at new LOD
        PaletteChunk existingChunk = GetChunk(coord);
        var neighbours      = ChunkRenderer.FetchNeighbours(coord, GetChunk);
        float noiseScaleCopy    = noiseScale;
        int   terrainHeightCopy = terrainHeight;
        int   baseHeightCopy    = baseHeight;

        Task.Run(async () =>
        {
            await _semaphore.WaitAsync();
            try
            {
                var chunk    = existingChunk ?? GenerateChunkData(coord, noiseScaleCopy, terrainHeightCopy, baseHeightCopy);
                var meshData = ChunkRenderer.BuildMeshData(chunk, neighbours, lodLevel);
                _readyQueue.Enqueue(new ChunkBuildResult(coord, chunk, meshData, lodLevel));
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

            if (!_renderers.ContainsKey(result.Coord) && _desiredCoords.Contains(result.Coord))
            {
                int wantedLod = GetLodLevel(result.Coord);
                // Discard if a fresher task has already superseded this one
                if (result.LodLevel != wantedLod && _inFlight.Contains(result.Coord))
                { applied++; continue; }

                var go = new GameObject($"Chunk {result.Coord.x},{result.Coord.y},{result.Coord.z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = ChunkToWorldPos(result.Coord);

                var renderer = go.AddComponent<ChunkRenderer>();
                renderer.ApplyMeshData(result.MeshData, chunkMaterial);

                _renderers[result.Coord]   = go;
                _rendererLod[result.Coord] = result.LodLevel;
            }

            applied++;
        }
    }

    // ── Frustum visibility ────────────────────────────────────────────────────

    private void UpdateFrustumVisibility()
    {
        if (!disableOutsideFrustum || _renderers.Count == 0) return;
        var cam = Camera.main;
        if (cam == null) return;

        var   planes    = GeometryUtility.CalculateFrustumPlanes(cam);
        var   playerPos = player != null ? player.position : Vector3.zero;
        float bypassSq  = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        foreach (var kvp in _renderers)
        {
            var   center    = ChunkCenterWorld(kvp.Key);
            float distSq    = (center - playerPos).sqrMagnitude;
            bool  visible   = distSq <= bypassSq || GeometryUtility.TestPlanesAABB(planes, ChunkBounds(kvp.Key));
            var   go        = kvp.Value;
            if (go.activeSelf != visible) go.SetActive(visible);
        }
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_renderers.TryGetValue(coord, out var go)) { Destroy(go); _renderers.Remove(coord); }
        _rendererLod.Remove(coord);
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
            int   worldX  = offsetX + x;
            int   worldZ  = offsetZ + z;
            float noise   = Mathf.PerlinNoise(worldX * noiseScale, worldZ * noiseScale);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        int size = PaletteChunk.Size;
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / size), 0, Mathf.FloorToInt(worldPos.z / size));
    }

    private static Vector3 ChunkToWorldPos(Vector3Int coord)
    {
        float s = PaletteChunk.Size;
        return new Vector3(coord.x * s, coord.y * s, coord.z * s);
    }

    private static Vector3 ChunkCenterWorld(Vector3Int coord)
    {
        float s = PaletteChunk.Size;
        return new Vector3(coord.x * s + s * 0.5f, coord.y * s + s * 0.5f, coord.z * s + s * 0.5f);
    }

    private static Bounds ChunkBounds(Vector3Int coord)
        => new Bounds(ChunkCenterWorld(coord), Vector3.one * PaletteChunk.Size);
}

internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
