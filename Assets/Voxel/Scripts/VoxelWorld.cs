using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages an infinite procedurally generated voxel world.
/// Chunk data generation and mesh building run on background threads.
/// Only GameObject creation and Mesh upload happen on the main thread.
///
/// Frustum culling: chunks beyond the close radius are only requested
/// when they fall inside the camera frustum.
/// Distance sorting: nearest chunks are always requested first.
/// Both position changes and camera rotation re-evaluate pending requests.
/// </summary>
public class VoxelWorld : MonoBehaviour
{
    [Header("World")]
    [Tooltip("Radius in chunks to keep loaded around the player.")]
    public int viewDistance   = 4;
    [Tooltip("Number of chunk layers stacked vertically (each layer = 16 voxels).")]
    public int verticalChunks = 3;
    public Transform player;

    [Header("Terrain")]
    public float noiseScale    = 0.04f;
    public int   terrainHeight = 32;
    public int   baseHeight    = 8;

    [Header("Rendering")]
    public Material chunkMaterial;

    [Header("Async")]
    [Tooltip("Max background tasks building chunks simultaneously.")]
    public int maxConcurrentTasks = 4;
    [Tooltip("Max chunks applied to the scene per frame (spread the main-thread cost).")]
    public int maxApplyPerFrame   = 2;

    [Header("Culling")]
    [Tooltip("Chunks within this radius (in chunks) are always loaded regardless of frustum.")]
    public int frustumBypassRadius = 2;
    [Tooltip("Camera forward dot-product threshold that triggers a re-evaluation (0.98 ≈ 11°).")]
    [Range(0.9f, 1f)]
    public float cameraRotationThreshold = 0.98f;
    [Tooltip("Disable chunk GameObjects that leave the frustum each frame (re-enabled on re-entry). " +
             "Unity already skips rendering off-screen meshes at the GPU level; enable this to also " +
             "save CPU transform/shadow overhead for out-of-view chunks.")]
    public bool disableOutsideFrustum = true;

    // ── Storage ───────────────────────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks    = new();
    private readonly Dictionary<Vector3Int, GameObject>   _renderers = new();

    // All coords that should exist based on player position (rebuilt on position change)
    private readonly HashSet<Vector3Int> _desiredCoords = new();

    // ── Async pipeline ────────────────────────────────────────────────────────

    private readonly HashSet<Vector3Int>           _inFlight   = new();
    private readonly ConcurrentQueue<ChunkBuildResult> _readyQueue = new();
    private SemaphoreSlim _semaphore;

    private readonly struct ChunkBuildResult
    {
        public readonly Vector3Int   Coord;
        public readonly PaletteChunk Chunk;
        public readonly MeshData     MeshData;
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, MeshData m)
        { Coord = c; Chunk = ch; MeshData = m; }
    }

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

        if (posChanged || rotChanged)
            UpdateLoadedChunks(positionChanged: posChanged);

        UpdateFrustumVisibility();
        ApplyReadyChunks();
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk  = new Vector3Int(int.MaxValue, 0, 0);
    private Vector3    _lastCameraForward = Vector3.forward;

    private bool CheckPositionChanged()
    {
        var current = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (current == _lastPlayerChunk) return false;
        _lastPlayerChunk = current;
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

    // ── Chunk streaming ───────────────────────────────────────────────────────

    private void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            // Rebuild the desired set from the new player position
            _desiredCoords.Clear();
            for (int x = -viewDistance; x <= viewDistance; x++)
            for (int z = -viewDistance; z <= viewDistance; z++)
            for (int y = 0; y < verticalChunks; y++)
                _desiredCoords.Add(new Vector3Int(_lastPlayerChunk.x + x, y, _lastPlayerChunk.z + z));

            // Unload GameObjects that left the desired set
            var toUnload = new List<Vector3Int>();
            foreach (var coord in _renderers.Keys)
                if (!_desiredCoords.Contains(coord)) toUnload.Add(coord);
            foreach (var coord in toUnload) UnloadChunk(coord);

            // Evict chunk data beyond the keep radius
            int evictRadius = viewDistance + 2;
            var toEvict = new List<Vector3Int>();
            foreach (var coord in _chunks.Keys)
                if (Mathf.Abs(coord.x - _lastPlayerChunk.x) > evictRadius ||
                    Mathf.Abs(coord.z - _lastPlayerChunk.z) > evictRadius)
                    toEvict.Add(coord);
            foreach (var coord in toEvict) _chunks.Remove(coord);
        }

        // Collect coords that still need to be requested
        var toRequest = new List<Vector3Int>();
        foreach (var coord in _desiredCoords)
            if (!_renderers.ContainsKey(coord) && !_inFlight.Contains(coord))
                toRequest.Add(coord);

        if (toRequest.Count == 0) return;

        // ── Distance sort (nearest first) ─────────────────────────────────────
        var playerPos = player != null ? player.position : Vector3.zero;
        toRequest.Sort((a, b) =>
            ChunkCenterWorld(a).sqrMagnitude_To(playerPos)
            .CompareTo(ChunkCenterWorld(b).sqrMagnitude_To(playerPos)));

        // ── Frustum culling ───────────────────────────────────────────────────
        Plane[] frustumPlanes = null;
        var cam = Camera.main;
        if (cam != null)
            frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

        float bypassRadiusWorld = frustumBypassRadius * PaletteChunk.Size;
        float bypassRadiusSq    = bypassRadiusWorld * bypassRadiusWorld;

        foreach (var coord in toRequest)
        {
            var   center  = ChunkCenterWorld(coord);
            float distSq  = (center - playerPos).sqrMagnitude;
            bool  inRange = distSq <= bypassRadiusSq;

            if (inRange || frustumPlanes == null ||
                GeometryUtility.TestPlanesAABB(frustumPlanes, ChunkBounds(coord)))
            {
                RequestChunk(coord);
            }
        }
    }

    // Runs every frame. Enables/disables chunk GOs based on current frustum.
    private void UpdateFrustumVisibility()
    {
        if (!disableOutsideFrustum || _renderers.Count == 0) return;

        var cam = Camera.main;
        if (cam == null) return;

        var    planes      = GeometryUtility.CalculateFrustumPlanes(cam);
        var    playerPos   = player != null ? player.position : Vector3.zero;
        float  bypassSq    = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        foreach (var kvp in _renderers)
        {
            var   center    = ChunkCenterWorld(kvp.Key);
            float distSq    = (center - playerPos).sqrMagnitude;
            bool  inBypass  = distSq <= bypassSq;
            bool  inFrustum = inBypass || GeometryUtility.TestPlanesAABB(planes, ChunkBounds(kvp.Key));

            var go = kvp.Value;
            if (go.activeSelf != inFrustum)
                go.SetActive(inFrustum);
        }
    }

    private void RequestChunk(Vector3Int coord)
    {
        _inFlight.Add(coord);

        var neighbours      = ChunkRenderer.FetchNeighbours(coord, GetChunk);
        float noiseScaleCopy    = noiseScale;
        int   terrainHeightCopy = terrainHeight;
        int   baseHeightCopy    = baseHeight;

        Task.Run(async () =>
        {
            await _semaphore.WaitAsync();
            try
            {
                var chunk    = GenerateChunkData(coord, noiseScaleCopy, terrainHeightCopy, baseHeightCopy);
                var meshData = ChunkRenderer.BuildMeshData(chunk, neighbours);
                _readyQueue.Enqueue(new ChunkBuildResult(coord, chunk, meshData));
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
                var go = new GameObject($"Chunk {result.Coord.x},{result.Coord.y},{result.Coord.z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = ChunkToWorldPos(result.Coord);

                var renderer = go.AddComponent<ChunkRenderer>();
                renderer.ApplyMeshData(result.MeshData, chunkMaterial);

                _renderers[result.Coord] = go;
            }

            applied++;
        }
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_renderers.TryGetValue(coord, out var go))
        {
            Destroy(go);
            _renderers.Remove(coord);
        }
    }

    // ── Terrain generation (background-thread safe) ───────────────────────────

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
            Mathf.FloorToInt(worldPos.x / size),
            0,
            Mathf.FloorToInt(worldPos.z / size));
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
    {
        float s = PaletteChunk.Size;
        return new Bounds(ChunkCenterWorld(coord), Vector3.one * s);
    }
}

// Tiny extension to avoid allocating a lambda for sqrMagnitude comparison
internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
