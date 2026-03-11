using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections.Concurrent;

/// <summary>
/// Terrain generation settings. Serializable struct so it can be safely
/// captured by value and passed to background threads.
/// </summary>
[System.Serializable]
public struct TerrainSettings
{
    // ── Water ─────────────────────────────────────────────────────────────────
    public int seaLevel;

    // ── Height ranges ─────────────────────────────────────────────────────────
    public int   baseHeight;      // minimum solid ground
    public int   plainsHeight;    // max height above baseHeight in plains biome
    public int   mountainHeight;  // max height above baseHeight in mountain biome
    public int   oceanDepth;      // how far below seaLevel the ocean floor reaches

    // ── FBM ───────────────────────────────────────────────────────────────────
    public float noiseScale;      // base frequency (smaller = larger features)
    public int   octaves;         // detail layers
    public float persistence;     // amplitude falloff per octave (0–1)
    public float lacunarity;      // frequency growth per octave (>1)

    // ── Biome ─────────────────────────────────────────────────────────────────
    public float biomeScale;      // frequency of biome transitions (very low)

    // ── Domain warp ───────────────────────────────────────────────────────────
    public float warpStrength;    // max coordinate displacement in voxels
    public float warpScale;       // frequency of the warp noise

    // ── Block layering ────────────────────────────────────────────────────────
    public int dirtDepth;         // dirt layers below the surface
    public int sandBeachWidth;    // surface height above seaLevel that stays sand
    public int snowAltitude;      // surface height above which snow replaces grass
}

/// <summary>
/// Pure-math terrain generator. All methods are static and thread-safe —
/// safe to call from background Task threads.
///
/// Pipeline per column (worldX, worldZ):
///   1. Domain warp   — distort coords to break noise regularity
///   2. FBM           — multi-octave Perlin → normalised [0,1]
///   3. Biome noise   — separate low-frequency noise → [0,1]
///   4. Biome blend   — lerp between ocean / plains / hills / mountain curves
///   5. Block select  — surface, depth, altitude → block type
/// </summary>
public static class TerrainGenerator
{
    // ── Surface height cache ──────────────────────────────────────────────────
    // Avoids recomputing the expensive FBM+warp pipeline for the same (worldX, worldZ)
    // when adjacent chunks load concurrently and share boundary column heights.
    // Key = worldX << 32 | (uint)worldZ.  Only full-detail results are cached
    // (low-detail may differ and are faster to recompute anyway).
    // Call ClearSurfaceCache() when the player chunk changes so stale entries don't
    // accumulate across large movements.

    private static readonly ConcurrentDictionary<long, int> _surfaceCache = new();

    public static void ClearSurfaceCache() => _surfaceCache.Clear();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the surface height (in world voxels) at (worldX, worldZ).
    /// lowDetail = true uses fewer noise octaves — safe for region backing chunks
    /// that are only sampled every 4+ voxels. Saves ~40% of noise calls.
    /// </summary>
    public static int GetSurface(int worldX, int worldZ, in TerrainSettings s, bool lowDetail = false)
    {
        long cacheKey = (long)worldX << 32 | (uint)worldZ;
        if (!lowDetail && _surfaceCache.TryGetValue(cacheKey, out int cached)) return cached;

        float wx = worldX, wz = worldZ;

        // 1. Domain warp — one octave is enough for region-distance chunks
        if (s.warpStrength > 0f)
        {
            int warpOctaves = lowDetail ? 1 : 2;
            float dX = FBM(wx * s.warpScale,         wz * s.warpScale,         warpOctaves, 0.5f, 2f);
            float dZ = FBM(wx * s.warpScale + 3.71f, wz * s.warpScale + 1.57f, warpOctaves, 0.5f, 2f);
            wx += (dX - 0.5f) * s.warpStrength * 2f;
            wz += (dZ - 0.5f) * s.warpStrength * 2f;
        }

        // 2. FBM terrain noise → [0,1]
        int terrainOctaves = lowDetail ? Mathf.Max(2, s.octaves - 2) : s.octaves;
        float fbm = FBM(wx * s.noiseScale, wz * s.noiseScale, terrainOctaves, s.persistence, s.lacunarity);

        // 3. Biome — two octaves sufficient for region-distance transitions
        int biomeOctaves = lowDetail ? 2 : 3;
        float biome = FBM(worldX * s.biomeScale + 100.3f, worldZ * s.biomeScale + 100.7f, biomeOctaves, 0.6f, 2f);

        // 4. Per-biome height curves
        float oceanH  = s.seaLevel  - s.oceanDepth  + fbm * s.oceanDepth * 0.5f;
        float plainsH = s.baseHeight + fbm * s.plainsHeight;
        float hillsH  = s.baseHeight + fbm * (s.plainsHeight + s.mountainHeight) * 0.5f;
        float mountH  = s.baseHeight + Mathf.Pow(fbm, 1.4f) * s.mountainHeight; // sharper peaks

        float height;
        if      (biome < 0.25f) height = Mathf.Lerp(oceanH,  plainsH, biome / 0.25f);
        else if (biome < 0.50f) height = Mathf.Lerp(plainsH, hillsH,  (biome - 0.25f) / 0.25f);
        else if (biome < 0.75f) height = Mathf.Lerp(hillsH,  mountH,  (biome - 0.50f) / 0.25f);
        else                    height = mountH;

        int result = Mathf.RoundToInt(height);
        if (!lowDetail) _surfaceCache.TryAdd(cacheKey, result);
        return result;
    }

    /// <summary>
    /// Returns the block type at a world position given a pre-computed surface height.
    /// Call GetSurface once per column and reuse for all Y values in that column.
    /// </summary>
    public static byte GetBlock(int worldX, int worldY, int worldZ, int surface, in TerrainSettings s)
    {
        if (worldY > surface)
            return worldY <= s.seaLevel ? BlockType.Water : BlockType.Air;

        int  depth   = surface - worldY;           // 0 = surface voxel
        bool nearSea = surface <= s.seaLevel + s.sandBeachWidth;

        if (depth == 0)
        {
            if (nearSea)                    return BlockType.Sand;
            if (worldY >= s.snowAltitude)   return BlockType.Snow;
            return BlockType.Grass;
        }

        if (depth <= s.dirtDepth)
            return nearSea ? BlockType.Sand : BlockType.Dirt;

        return BlockType.Stone;
    }

    /// <summary>Convenience overload — computes surface internally (slower per-voxel).</summary>
    public static byte GetBlock(int worldX, int worldY, int worldZ, in TerrainSettings s)
        => GetBlock(worldX, worldY, worldZ, GetSurface(worldX, worldZ, s), s);

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Fractal Brownian Motion — sums octaves of Perlin noise, normalised to [0,1].
    /// Octave i is sampled with a per-octave offset to avoid lattice alignment.
    /// </summary>
    private static float FBM(float x, float z, int octaves, float persistence, float lacunarity)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            value     += Mathf.PerlinNoise(x * frequency + i * 7.31f,
                                           z * frequency + i * 5.17f) * amplitude;
            norm      += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return value / norm;
    }
}

/// <summary>
/// Burst-compiled terrain generation job. One job per chunk.
/// Results are written to Blocks (flat byte array, index = x + y*16 + z*256).
///
/// Schedule on the main thread via job.Schedule(); poll handle.IsCompleted each Update;
/// call handle.Complete() then read Blocks to build the PaletteChunk.
///
/// Uses noise.cnoise (Unity.Mathematics Classic Perlin, range [-1,1] normalised to [0,1]).
/// Terrain shape is visually equivalent to the managed path but not sample-identical.
/// </summary>
[BurstCompile]
public struct GenerateChunkJob : IJob
{
    public TerrainSettings   Settings;
    public int3              ChunkCoord;
    public bool              LowDetail;
    [WriteOnly] public NativeArray<byte> Blocks;

    private const int Size       = 16;   // PaletteChunk.Size
    private const int VoxelCount = 4096; // Size³

    public void Execute()
    {
        TerrainSettings s = Settings;
        int offsetX = ChunkCoord.x * Size;
        int offsetY = ChunkCoord.y * Size;
        int offsetZ = ChunkCoord.z * Size;
        int chunkTop = offsetY + Size - 1;

        // Fast-path: sample 4 corners to skip fully-solid and fully-air chunks
        int minSurface = int.MaxValue, maxSurface = int.MinValue;
        for (int cz = 0; cz <= Size; cz += Size)
        for (int cx = 0; cx <= Size; cx += Size)
        {
            int h = GetSurface(offsetX + cx, offsetZ + cz, s, LowDetail);
            if (h < minSurface) minSurface = h;
            if (h > maxSurface) maxSurface = h;
        }

        if (chunkTop < minSurface - s.dirtDepth)
        {
            for (int i = 0; i < VoxelCount; i++) Blocks[i] = BlockType.Stone;
            return;
        }
        if (offsetY > maxSurface && offsetY > s.seaLevel)
        {
            for (int i = 0; i < VoxelCount; i++) Blocks[i] = BlockType.Air;
            return;
        }

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int surface = GetSurface(offsetX + x, offsetZ + z, s, LowDetail);
            for (int y = 0; y < Size; y++)
                Blocks[x + y * Size + z * Size * Size] =
                    GetBlock(offsetX + x, offsetY + y, offsetZ + z, surface, s);
        }
    }

    // ── Burst-compatible terrain helpers ─────────────────────────────────────
    // These mirror TerrainGenerator's managed methods but use Unity.Mathematics
    // instead of UnityEngine.Mathf so Burst can compile and vectorise them.

    private static int GetSurface(int worldX, int worldZ, TerrainSettings s, bool lowDetail)
    {
        float wx = worldX, wz = worldZ;

        if (s.warpStrength > 0f)
        {
            int warpOctaves = lowDetail ? 1 : 2;
            float dX = FBM(wx * s.warpScale,         wz * s.warpScale,         warpOctaves, 0.5f, 2f);
            float dZ = FBM(wx * s.warpScale + 3.71f, wz * s.warpScale + 1.57f, warpOctaves, 0.5f, 2f);
            wx += (dX - 0.5f) * s.warpStrength * 2f;
            wz += (dZ - 0.5f) * s.warpStrength * 2f;
        }

        int terrainOctaves = lowDetail ? math.max(2, s.octaves - 2) : s.octaves;
        float fbm = FBM(wx * s.noiseScale, wz * s.noiseScale, terrainOctaves, s.persistence, s.lacunarity);

        int biomeOctaves = lowDetail ? 2 : 3;
        float biome = FBM(worldX * s.biomeScale + 100.3f, worldZ * s.biomeScale + 100.7f, biomeOctaves, 0.6f, 2f);

        float oceanH  = s.seaLevel  - s.oceanDepth  + fbm * s.oceanDepth * 0.5f;
        float plainsH = s.baseHeight + fbm * s.plainsHeight;
        float hillsH  = s.baseHeight + fbm * (s.plainsHeight + s.mountainHeight) * 0.5f;
        float mountH  = s.baseHeight + math.pow(fbm, 1.4f) * s.mountainHeight;

        float height;
        if      (biome < 0.25f) height = math.lerp(oceanH,  plainsH, biome / 0.25f);
        else if (biome < 0.50f) height = math.lerp(plainsH, hillsH,  (biome - 0.25f) / 0.25f);
        else if (biome < 0.75f) height = math.lerp(hillsH,  mountH,  (biome - 0.50f) / 0.25f);
        else                    height = mountH;

        return (int)math.round(height);
    }

    private static byte GetBlock(int worldX, int worldY, int worldZ, int surface, TerrainSettings s)
    {
        if (worldY > surface)
            return worldY <= s.seaLevel ? BlockType.Water : BlockType.Air;

        int  depth   = surface - worldY;
        bool nearSea = surface <= s.seaLevel + s.sandBeachWidth;

        if (depth == 0)
        {
            if (nearSea)                  return BlockType.Sand;
            if (worldY >= s.snowAltitude) return BlockType.Snow;
            return BlockType.Grass;
        }

        if (depth <= s.dirtDepth)
            return nearSea ? BlockType.Sand : BlockType.Dirt;

        return BlockType.Stone;
    }

    /// <summary>
    /// FBM using noise.cnoise (Classic Perlin, range [-1,1] normalised to [0,1]).
    /// Octave offsets match the managed path to produce comparable terrain shapes.
    /// </summary>
    private static float FBM(float x, float z, int octaves, float persistence, float lacunarity)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float2 pos = new float2(x * frequency + i * 7.31f, z * frequency + i * 5.17f);
            value     += (noise.cnoise(pos) + 1f) * 0.5f * amplitude;
            norm      += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return value / norm;
    }
}
