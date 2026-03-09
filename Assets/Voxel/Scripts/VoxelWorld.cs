using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages an infinite procedurally generated voxel world.
/// Chunk data generation and mesh building run on background threads.
/// Only GameObject creation and Mesh upload happen on the main thread.
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

    // ── Storage ───────────────────────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks    = new();
    private readonly Dictionary<Vector3Int, GameObject>   _renderers = new();

    // ── Async pipeline ────────────────────────────────────────────────────────

    // Coords currently being built on a background thread
    private readonly HashSet<Vector3Int> _inFlight = new();

    // Results ready to be applied on the main thread
    private readonly ConcurrentQueue<ChunkBuildResult> _readyQueue = new();

    // Limits how many background tasks run simultaneously
    private SemaphoreSlim _semaphore;

    private readonly struct ChunkBuildResult
    {
        public readonly Vector3Int  Coord;
        public readonly PaletteChunk Chunk;
        public readonly MeshData    MeshData;
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, MeshData m)
        { Coord = c; Chunk = ch; MeshData = m; }
    }

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);

        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        UpdateLoadedChunks();
    }

    private void Update()
    {
        UpdateLoadedChunks();
        ApplyReadyChunks();
    }

    // ── Chunk streaming ───────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk = new Vector3Int(int.MaxValue, 0, 0);

    private void UpdateLoadedChunks()
    {
        var playerChunk = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (playerChunk == _lastPlayerChunk) return;
        _lastPlayerChunk = playerChunk;

        var desired = new HashSet<Vector3Int>();

        for (int x = -viewDistance; x <= viewDistance; x++)
        for (int z = -viewDistance; z <= viewDistance; z++)
        for (int y = 0; y < verticalChunks; y++)
            desired.Add(new Vector3Int(playerChunk.x + x, y, playerChunk.z + z));

        // Request new chunks
        foreach (var coord in desired)
            if (!_renderers.ContainsKey(coord) && !_inFlight.Contains(coord))
                RequestChunk(coord);

        // Unload GameObjects out of range
        var toUnload = new List<Vector3Int>();
        foreach (var coord in _renderers.Keys)
            if (!desired.Contains(coord)) toUnload.Add(coord);
        foreach (var coord in toUnload) UnloadChunk(coord);

        // Evict chunk data beyond the keep radius
        int evictRadius = viewDistance + 2;
        var toEvict = new List<Vector3Int>();
        foreach (var coord in _chunks.Keys)
            if (Mathf.Abs(coord.x - playerChunk.x) > evictRadius ||
                Mathf.Abs(coord.z - playerChunk.z) > evictRadius)
                toEvict.Add(coord);
        foreach (var coord in toEvict) _chunks.Remove(coord);
    }

    private void RequestChunk(Vector3Int coord)
    {
        _inFlight.Add(coord);

        // Snapshot neighbours that are already available (main-thread read is safe here)
        var neighbours = ChunkRenderer.FetchNeighbours(coord, GetChunk);

        // Capture fields needed on the background thread (no MonoBehaviour access there)
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
            finally
            {
                _semaphore.Release();
            }
        });
    }

    // Apply up to maxApplyPerFrame ready chunks per frame on the main thread
    private void ApplyReadyChunks()
    {
        int applied = 0;
        while (applied < maxApplyPerFrame && _readyQueue.TryDequeue(out var result))
        {
            _inFlight.Remove(result.Coord);

            // Store data even if the chunk was unloaded while in-flight
            _chunks[result.Coord] = result.Chunk;

            // Only spawn a GO if the chunk is still within view
            if (!_renderers.ContainsKey(result.Coord) && IsDesired(result.Coord))
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

    private bool IsDesired(Vector3Int coord)
    {
        var playerChunk = _lastPlayerChunk;
        return coord.y >= 0 && coord.y < verticalChunks
            && Mathf.Abs(coord.x - playerChunk.x) <= viewDistance
            && Mathf.Abs(coord.z - playerChunk.z) <= viewDistance;
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_renderers.TryGetValue(coord, out var go))
        {
            Destroy(go);
            _renderers.Remove(coord);
        }
    }

    // ── Terrain generation (background-thread safe — pure C#) ─────────────────

    private static PaletteChunk GenerateChunkData(Vector3Int coord,
                                                   float noiseScale, int terrainHeight, int baseHeight)
    {
        var chunk    = new PaletteChunk();
        int offsetX  = coord.x * PaletteChunk.Size;
        int offsetY  = coord.y * PaletteChunk.Size;
        int offsetZ  = coord.z * PaletteChunk.Size;

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
        int size = PaletteChunk.Size;
        return new Vector3(coord.x * size, coord.y * size, coord.z * size);
    }
}
