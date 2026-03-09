using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages an infinite procedurally generated voxel world.
/// Stores chunk data in a HashMap and streams chunks around the player.
/// </summary>
public class VoxelWorld : MonoBehaviour
{
    [Header("World")]
    [Tooltip("Radius in chunks to keep loaded around the player.")]
    public int viewDistance    = 4;
    [Tooltip("Number of chunk layers stacked vertically (each layer = 16 voxels).")]
    public int verticalChunks  = 3;
    public Transform player;

    [Header("Terrain")]
    public float noiseScale    = 0.04f;
    public int   terrainHeight = 32;   // max surface height in voxels
    public int   baseHeight    = 8;    // minimum stone floor height

    [Header("Rendering")]
    public Material chunkMaterial;

    // ── Storage ───────────────────────────────────────────────────────────────

    // Chunk data — persists even when chunk is unloaded from view
    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks
        = new Dictionary<Vector3Int, PaletteChunk>();

    // Active GameObjects for visible chunks
    private readonly Dictionary<Vector3Int, GameObject> _renderers
        = new Dictionary<Vector3Int, GameObject>();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        UpdateLoadedChunks();
    }

    private void Update()
    {
        UpdateLoadedChunks();
    }

    // ── Chunk streaming ───────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk = new Vector3Int(int.MaxValue, 0, 0);

    private void UpdateLoadedChunks()
    {
        var playerChunk = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (playerChunk == _lastPlayerChunk) return;
        _lastPlayerChunk = playerChunk;

        var desired = new HashSet<Vector3Int>();

        // Collect desired chunks — horizontal streaming + all vertical layers
        for (int x = -viewDistance; x <= viewDistance; x++)
        for (int z = -viewDistance; z <= viewDistance; z++)
        for (int y = 0; y < verticalChunks; y++)
        {
            var coord = new Vector3Int(playerChunk.x + x, y, playerChunk.z + z);
            desired.Add(coord);
        }

        // Load new chunks
        foreach (var coord in desired)
            if (!_renderers.ContainsKey(coord))
                LoadChunk(coord);

        // Unload out-of-range chunk GameObjects
        var toUnload = new List<Vector3Int>();
        foreach (var coord in _renderers.Keys)
            if (!desired.Contains(coord))
                toUnload.Add(coord);

        foreach (var coord in toUnload)
            UnloadChunk(coord);

        // Evict chunk data that is far enough away that we won't need it soon.
        // Keep a 2-chunk buffer beyond view distance so nearby chunks don't regenerate on re-entry.
        int evictRadius = viewDistance + 2;
        var toEvict = new List<Vector3Int>();
        foreach (var coord in _chunks.Keys)
        {
            if (Mathf.Abs(coord.x - playerChunk.x) > evictRadius ||
                Mathf.Abs(coord.z - playerChunk.z) > evictRadius)
                toEvict.Add(coord);
        }
        foreach (var coord in toEvict)
            _chunks.Remove(coord);
    }

    private void LoadChunk(Vector3Int coord)
    {
        // Generate data if not already in the HashMap
        if (!_chunks.TryGetValue(coord, out var chunk))
        {
            chunk = GenerateChunk(coord);
            _chunks[coord] = chunk;
        }

        // Spawn a GameObject and render the mesh
        var go = new GameObject($"Chunk {coord.x},{coord.y},{coord.z}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = ChunkToWorldPos(coord);

        var renderer = go.AddComponent<ChunkRenderer>();
        renderer.Render(chunk, coord, GetChunk, chunkMaterial);

        _renderers[coord] = go;
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_renderers.TryGetValue(coord, out var go))
        {
            Destroy(go);
            _renderers.Remove(coord);
        }
        // Data is kept in _chunks until evicted by the radius check above
    }

    // ── Terrain generation ────────────────────────────────────────────────────

    private PaletteChunk GenerateChunk(Vector3Int coord)
    {
        var chunk = new PaletteChunk();
        int worldOffsetX = coord.x * PaletteChunk.Size;
        int worldOffsetY = coord.y * PaletteChunk.Size;
        int worldOffsetZ = coord.z * PaletteChunk.Size;

        for (int z = 0; z < PaletteChunk.Size; z++)
        for (int x = 0; x < PaletteChunk.Size; x++)
        {
            int worldX = worldOffsetX + x;
            int worldZ = worldOffsetZ + z;

            float noise   = Mathf.PerlinNoise(worldX * noiseScale, worldZ * noiseScale);
            int   surface = baseHeight + Mathf.RoundToInt(noise * terrainHeight);

            for (int y = 0; y < PaletteChunk.Size; y++)
            {
                int worldY = worldOffsetY + y;

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

    /// <summary>Returns the chunk at the given coord if it has been generated, otherwise null.</summary>
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
