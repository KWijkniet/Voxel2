using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
    public int   baseHeight;       // minimum solid ground
    public int   plainsHeight;     // max terrain amplitude in plains biome
    public int   forestHeight;     // max terrain amplitude in forest biome
    public int   mountainHeight;   // max terrain amplitude in mountain biome
    public int   tundraHeight;     // max terrain amplitude in tundra biome
    public int   desertDuneHeight; // max dune amplitude in desert biome
    public int   oceanDepth;       // how far below seaLevel the ocean floor reaches

    // ── FBM ───────────────────────────────────────────────────────────────────
    public float noiseScale;       // base frequency (smaller = larger features)
    public int   octaves;          // detail layers
    public float persistence;      // amplitude falloff per octave (0–1)
    public float lacunarity;       // frequency growth per octave (>1)

    // ── Biome axes ────────────────────────────────────────────────────────────
    // Temperature (0 = cold/tundra, 1 = hot/desert) and humidity (0 = dry, 1 = wet).
    // Desert and Tundra are at opposite ends of the temperature axis — never adjacent.
    // Mountain is a separate override noise, independent of temperature/humidity.
    public float tempScale;                // temperature noise frequency
    public float humidityScale;            // humidity noise frequency
    public float mountainBiomeScale;       // mountain override noise frequency
    public float mountainBiomeThreshold;   // mountain noise above this → mountain biome

    // ── Domain warp ───────────────────────────────────────────────────────────
    public float warpStrength;     // max coordinate displacement in voxels
    public float warpScale;        // frequency of the warp noise

    // ── Block layering ─────────────────────────────────────────────────────────
    public int dirtDepth;          // dirt layers below surface (plains/forest/mountain)
    public int sandBeachWidth;     // surface height above seaLevel that stays sandy beach
    public int snowAltitude;       // height above which snow replaces grass on peaks
    public int desertSandDepth;    // sand layers in desert before sandstone
    public int tundraFrozenDepth;  // frozen dirt layers in tundra before stone
}

/// <summary>
/// Pure-math terrain generator. All methods are static and thread-safe.
///
/// Biome system — two noise axes:
///   Temperature (0=cold, 1=hot): Tundra → Plains/Forest → Desert
///   Humidity    (0=dry,  1=wet): Plains → Forest (within temperate band)
///   Mountain: separate override noise; when above threshold, overrides T/H biome.
///
/// Five land biomes: Plains, Forest, Desert, Tundra, Mountain.
/// Height curves blend by weight; block rules use the dominant biome.
/// Snow applies above snowAltitude in all non-desert biomes.
/// Ocean forms naturally wherever blended height falls below seaLevel.
/// </summary>
public static class TerrainGenerator
{
    // ── Surface height cache ──────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<long, int> _surfaceCache = new();

    public static void ClearSurfaceCache() => _surfaceCache.Clear();

    // ── Biome IDs ─────────────────────────────────────────────────────────────
    public const byte BiomePlains   = 0;
    public const byte BiomeForest   = 1;
    public const byte BiomeDesert   = 2;
    public const byte BiomeTundra   = 3;
    public const byte BiomeMountain = 4;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the surface height (in world voxels) at (worldX, worldZ).
    /// lowDetail = true uses fewer noise octaves for region backing chunks.
    /// </summary>
    public static int GetSurface(int worldX, int worldZ, in TerrainSettings s, bool lowDetail = false)
    {
        long cacheKey = (long)worldX << 32 | (uint)worldZ;
        if (!lowDetail && _surfaceCache.TryGetValue(cacheKey, out int cached)) return cached;

        float wx = worldX, wz = worldZ;
        if (s.warpStrength > 0f)
        {
            int warpOctaves = lowDetail ? 1 : 2;
            float dX = FBM(wx * s.warpScale,         wz * s.warpScale,         warpOctaves, 0.5f, 2f);
            float dZ = FBM(wx * s.warpScale + 3.71f, wz * s.warpScale + 1.57f, warpOctaves, 0.5f, 2f);
            wx += (dX - 0.5f) * s.warpStrength * 2f;
            wz += (dZ - 0.5f) * s.warpStrength * 2f;
        }

        int terrainOctaves = lowDetail ? Mathf.Max(2, s.octaves - 2) : s.octaves;
        float fbm = FBM(wx * s.noiseScale, wz * s.noiseScale, terrainOctaves, s.persistence, s.lacunarity);

        ComputeBiomeWeights(worldX, worldZ, s, lowDetail,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        float height =
            plainsW   * (s.baseHeight + fbm * s.plainsHeight) +
            forestW   * (s.baseHeight + fbm * s.forestHeight) +
            desertW   * (s.baseHeight + fbm * s.desertDuneHeight) +
            tundraW   * (s.baseHeight + fbm * s.tundraHeight) +
            mountainW * (s.baseHeight + Mathf.Pow(fbm, 1.4f) * s.mountainHeight);

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
        byte biome = GetDominantBiome(worldX, worldZ, s);
        return GetBlockForBiome(worldX, worldY, surface, biome, s);
    }

    /// <summary>Convenience overload — computes surface internally (slower per-voxel).</summary>
    public static byte GetBlock(int worldX, int worldY, int worldZ, in TerrainSettings s)
        => GetBlock(worldX, worldY, worldZ, GetSurface(worldX, worldZ, s), s);

    // ── Private helpers ───────────────────────────────────────────────────────

    public static byte GetDominantBiome(int worldX, int worldZ, in TerrainSettings s)
    {
        ComputeBiomeWeights(worldX, worldZ, s, false,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        byte biome = BiomePlains;
        float max = plainsW;
        if (forestW   > max) { max = forestW;   biome = BiomeForest;   }
        if (desertW   > max) { max = desertW;   biome = BiomeDesert;   }
        if (tundraW   > max) { max = tundraW;   biome = BiomeTundra;   }
        if (mountainW > max)                    biome = BiomeMountain;
        return biome;
    }

    private static void ComputeBiomeWeights(int worldX, int worldZ, in TerrainSettings s,
        bool lowDetail,
        out float plainsW, out float forestW, out float desertW,
        out float tundraW, out float mountainW)
    {
        int biomeOctaves = lowDetail ? 2 : 2; // biome noise is always low-frequency; 2 oct is enough

        float temp    = FBM(worldX * s.tempScale    + 200f, worldZ * s.tempScale,    biomeOctaves, 0.5f, 2f);
        float humid   = FBM(worldX * s.humidityScale +  50f, worldZ * s.humidityScale, biomeOctaves, 0.5f, 2f);
        float mountN  = FBM(worldX * s.mountainBiomeScale,   worldZ * s.mountainBiomeScale, 3, 0.5f, 2f);

        float threshold = s.mountainBiomeThreshold;
        mountainW = Smoothstep(threshold - 0.08f, threshold + 0.08f, mountN);

        float rem = 1f - mountainW;
        tundraW   = rem * (1f - Smoothstep(0.20f, 0.35f, temp));  // high weight when cold
        desertW   = rem * Smoothstep(0.65f, 0.80f, temp);          // high weight when hot
        float tempW = rem - tundraW - desertW;
        forestW   = tempW * Smoothstep(0.40f, 0.60f, humid);
        plainsW   = tempW - forestW;
    }

    private static byte GetBlockForBiome(int worldX, int worldY, int surface, byte biome, in TerrainSettings s)
    {
        if (worldY > surface)
            return worldY <= s.seaLevel ? BlockType.Water : BlockType.Air;

        int depth = surface - worldY;

        if (biome == BiomeDesert)
            return depth < s.desertSandDepth ? BlockType.Sand : BlockType.Sandstone;

        if (biome == BiomeTundra)
        {
            if (depth == 0)                     return BlockType.Snow;
            if (depth <= s.tundraFrozenDepth)   return BlockType.FrozenDirt;
            return BlockType.Stone;
        }

        // Plains, Forest, Mountain
        bool nearSea = surface <= s.seaLevel + s.sandBeachWidth;
        if (nearSea)
            return depth < s.dirtDepth ? BlockType.Sand : BlockType.Stone;

        if (depth == 0) return worldY >= s.snowAltitude ? BlockType.Snow : BlockType.Grass;
        if (depth <= s.dirtDepth) return BlockType.Dirt;
        return BlockType.Stone;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

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
/// Burst-compiled single-chunk terrain generation job.
/// </summary>
[BurstCompile]
public struct GenerateChunkJob : IJob
{
    public TerrainSettings   Settings;
    public int3              ChunkCoord;
    public bool              LowDetail;
    [WriteOnly] public NativeArray<byte> Blocks;

    private const int Size       = 16;
    private const int VoxelCount = 4096;

    public void Execute()
    {
        TerrainSettings s = Settings;
        int offsetX = ChunkCoord.x * Size;
        int offsetY = ChunkCoord.y * Size;
        int offsetZ = ChunkCoord.z * Size;
        int chunkTop = offsetY + Size - 1;

        int minSurface = int.MaxValue, maxSurface = int.MinValue;
        for (int cz = 0; cz <= Size; cz += Size)
        for (int cx = 0; cx <= Size; cx += Size)
        {
            int h = GetSurface(offsetX + cx, offsetZ + cz, s, LowDetail);
            if (h < minSurface) minSurface = h;
            if (h > maxSurface) maxSurface = h;
        }

        int deepThreshold = math.max(s.dirtDepth, s.desertSandDepth);
        if (chunkTop < minSurface - deepThreshold)
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
            int  surface = GetSurface(offsetX + x, offsetZ + z, s, LowDetail);
            byte biome   = GetDominantBiome(offsetX + x, offsetZ + z, s);
            for (int y = 0; y < Size; y++)
                Blocks[x + y * Size + z * Size * Size] =
                    GetBlock(offsetX + x, offsetY + y, surface, biome, s);
        }
    }

    // ── Burst-compatible terrain helpers ─────────────────────────────────────

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

        ComputeBiomeWeights(worldX, worldZ, s, lowDetail,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        float height =
            plainsW   * (s.baseHeight + fbm * s.plainsHeight) +
            forestW   * (s.baseHeight + fbm * s.forestHeight) +
            desertW   * (s.baseHeight + fbm * s.desertDuneHeight) +
            tundraW   * (s.baseHeight + fbm * s.tundraHeight) +
            mountainW * (s.baseHeight + math.pow(fbm, 1.4f) * s.mountainHeight);

        return (int)math.round(height);
    }

    private static byte GetDominantBiome(int worldX, int worldZ, TerrainSettings s)
    {
        ComputeBiomeWeights(worldX, worldZ, s, false,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        byte biome = 0; // Plains
        float max = plainsW;
        if (forestW   > max) { max = forestW;   biome = 1; }
        if (desertW   > max) { max = desertW;   biome = 2; }
        if (tundraW   > max) { max = tundraW;   biome = 3; }
        if (mountainW > max)                    biome = 4;
        return biome;
    }

    private static void ComputeBiomeWeights(int worldX, int worldZ, TerrainSettings s, bool lowDetail,
        out float plainsW, out float forestW, out float desertW,
        out float tundraW, out float mountainW)
    {
        float temp   = FBM(worldX * s.tempScale    + 200f, worldZ * s.tempScale,    2, 0.5f, 2f);
        float humid  = FBM(worldX * s.humidityScale +  50f, worldZ * s.humidityScale, 2, 0.5f, 2f);
        float mountN = FBM(worldX * s.mountainBiomeScale,   worldZ * s.mountainBiomeScale, 3, 0.5f, 2f);

        float threshold = s.mountainBiomeThreshold;
        mountainW = Smoothstep(threshold - 0.08f, threshold + 0.08f, mountN);
        float rem = 1f - mountainW;
        tundraW  = rem * (1f - Smoothstep(0.20f, 0.35f, temp));
        desertW  = rem * Smoothstep(0.65f, 0.80f, temp);
        float tempW = rem - tundraW - desertW;
        forestW  = tempW * Smoothstep(0.40f, 0.60f, humid);
        plainsW  = tempW - forestW;
    }

    private static byte GetBlock(int worldX, int worldY, int surface, byte biome, TerrainSettings s)
    {
        if (worldY > surface)
            return worldY <= s.seaLevel ? BlockType.Water : BlockType.Air;

        int depth = surface - worldY;

        if (biome == 2) // Desert
            return depth < s.desertSandDepth ? BlockType.Sand : BlockType.Sandstone;

        if (biome == 3) // Tundra
        {
            if (depth == 0)                   return BlockType.Snow;
            if (depth <= s.tundraFrozenDepth) return BlockType.FrozenDirt;
            return BlockType.Stone;
        }

        // Plains (0), Forest (1), Mountain (4)
        bool nearSea = surface <= s.seaLevel + s.sandBeachWidth;
        if (nearSea)
            return depth < s.dirtDepth ? BlockType.Sand : BlockType.Stone;

        if (depth == 0) return worldY >= s.snowAltitude ? BlockType.Snow : BlockType.Grass;
        if (depth <= s.dirtDepth) return BlockType.Dirt;
        return BlockType.Stone;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = math.clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

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

/// <summary>
/// Burst-compiled parallel terrain job. Generates N chunks in one IJobParallelFor dispatch.
/// Each parallel index i writes to AllBlocks[i*VoxelCount..(i+1)*VoxelCount-1].
/// SurfaceCache provides a thread-safe height cache shared across all parallel indices.
/// </summary>
[BurstCompile]
public struct GenerateChunksBatchJob : IJobParallelFor
{
    public  TerrainSettings Settings;
    [ReadOnly] public NativeArray<int3> Coords;
    public  bool LowDetail;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<byte> AllBlocks;

    [ReadOnly, NativeDisableContainerSafetyRestriction]
    public NativeParallelHashMap<long, int> SurfaceCache;

    [NativeDisableContainerSafetyRestriction]
    public NativeParallelHashMap<long, int>.ParallelWriter SurfaceCacheWriter;

    private const int Size       = VoxelChunk.Size;
    private const int VoxelCount = VoxelChunk.VoxelCount;

    public void Execute(int i)
    {
        TerrainSettings s    = Settings;
        int3 coord   = Coords[i];
        int  offsetX = coord.x * Size;
        int  offsetY = coord.y * Size;
        int  offsetZ = coord.z * Size;
        int  chunkTop = offsetY + Size - 1;
        int  baseIdx  = i * VoxelCount;

        int h00 = GetSurface(offsetX,        offsetZ,        s);
        int h10 = GetSurface(offsetX + Size, offsetZ,        s);
        int h01 = GetSurface(offsetX,        offsetZ + Size, s);
        int h11 = GetSurface(offsetX + Size, offsetZ + Size, s);
        int minCorner = math.min(math.min(h00, h10), math.min(h01, h11));
        int maxCorner = math.max(math.max(h00, h10), math.max(h01, h11));

        int deepThreshold = math.max(s.dirtDepth, s.desertSandDepth);
        if (chunkTop < minCorner - deepThreshold)
        {
            for (int j = 0; j < VoxelCount; j++) AllBlocks[baseIdx + j] = BlockType.Stone;
            return;
        }
        if (offsetY > maxCorner && offsetY > s.seaLevel)
        {
            for (int j = 0; j < VoxelCount; j++) AllBlocks[baseIdx + j] = BlockType.Air;
            return;
        }

        var surfaces = new NativeArray<int> (Size * Size, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        var biomes   = new NativeArray<byte>(Size * Size, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int col = x + z * Size;
            surfaces[col] = GetSurface(offsetX + x, offsetZ + z, s);
            biomes[col]   = GetDominantBiome(offsetX + x, offsetZ + z, s);
        }

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int  col     = x + z * Size;
            int  surface = surfaces[col];
            byte biome   = biomes[col];
            for (int y = 0; y < Size; y++)
                AllBlocks[baseIdx + x + y * Size + z * Size * Size] =
                    GetBlock(offsetX + x, offsetY + y, surface, biome, s);
        }

        biomes.Dispose();
        surfaces.Dispose();
    }

    // ── Cache-aware surface height lookup ─────────────────────────────────────

    private int GetSurface(int worldX, int worldZ, TerrainSettings s)
    {
        if (!LowDetail)
        {
            long key = ((long)worldX << 32) | (uint)worldZ;
            if (SurfaceCache.TryGetValue(key, out int cached)) return cached;
            int result = ComputeSurface(worldX, worldZ, s);
            SurfaceCacheWriter.TryAdd(key, result);
            return result;
        }
        return ComputeSurface(worldX, worldZ, s);
    }

    // ── Burst-compatible terrain helpers ─────────────────────────────────────

    private static int ComputeSurface(int worldX, int worldZ, TerrainSettings s)
    {
        float wx = worldX, wz = worldZ;
        if (s.warpStrength > 0f)
        {
            float dX = FBM(wx * s.warpScale,         wz * s.warpScale,         2, 0.5f, 2f);
            float dZ = FBM(wx * s.warpScale + 3.71f, wz * s.warpScale + 1.57f, 2, 0.5f, 2f);
            wx += (dX - 0.5f) * s.warpStrength * 2f;
            wz += (dZ - 0.5f) * s.warpStrength * 2f;
        }
        float fbm = FBM(wx * s.noiseScale, wz * s.noiseScale, s.octaves, s.persistence, s.lacunarity);

        ComputeBiomeWeights(worldX, worldZ, s,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        float height =
            plainsW   * (s.baseHeight + fbm * s.plainsHeight) +
            forestW   * (s.baseHeight + fbm * s.forestHeight) +
            desertW   * (s.baseHeight + fbm * s.desertDuneHeight) +
            tundraW   * (s.baseHeight + fbm * s.tundraHeight) +
            mountainW * (s.baseHeight + math.pow(fbm, 1.4f) * s.mountainHeight);

        return (int)math.round(height);
    }

    private static byte GetDominantBiome(int worldX, int worldZ, TerrainSettings s)
    {
        ComputeBiomeWeights(worldX, worldZ, s,
            out float plainsW, out float forestW, out float desertW,
            out float tundraW, out float mountainW);

        byte biome = 0; // Plains
        float max = plainsW;
        if (forestW   > max) { max = forestW;   biome = 1; }
        if (desertW   > max) { max = desertW;   biome = 2; }
        if (tundraW   > max) { max = tundraW;   biome = 3; }
        if (mountainW > max)                    biome = 4;
        return biome;
    }

    private static void ComputeBiomeWeights(int worldX, int worldZ, TerrainSettings s,
        out float plainsW, out float forestW, out float desertW,
        out float tundraW, out float mountainW)
    {
        float temp   = FBM(worldX * s.tempScale    + 200f, worldZ * s.tempScale,    2, 0.5f, 2f);
        float humid  = FBM(worldX * s.humidityScale +  50f, worldZ * s.humidityScale, 2, 0.5f, 2f);
        float mountN = FBM(worldX * s.mountainBiomeScale,   worldZ * s.mountainBiomeScale, 3, 0.5f, 2f);

        float threshold = s.mountainBiomeThreshold;
        mountainW = Smoothstep(threshold - 0.08f, threshold + 0.08f, mountN);
        float rem = 1f - mountainW;
        tundraW  = rem * (1f - Smoothstep(0.20f, 0.35f, temp));
        desertW  = rem * Smoothstep(0.65f, 0.80f, temp);
        float tempW = rem - tundraW - desertW;
        forestW  = tempW * Smoothstep(0.40f, 0.60f, humid);
        plainsW  = tempW - forestW;
    }

    private static byte GetBlock(int worldX, int worldY, int surface, byte biome, TerrainSettings s)
    {
        if (worldY > surface)
            return worldY <= s.seaLevel ? BlockType.Water : BlockType.Air;

        int depth = surface - worldY;

        if (biome == 2) // Desert
            return depth < s.desertSandDepth ? BlockType.Sand : BlockType.Sandstone;

        if (biome == 3) // Tundra
        {
            if (depth == 0)                   return BlockType.Snow;
            if (depth <= s.tundraFrozenDepth) return BlockType.FrozenDirt;
            return BlockType.Stone;
        }

        // Plains (0), Forest (1), Mountain (4)
        bool nearSea = surface <= s.seaLevel + s.sandBeachWidth;
        if (nearSea)
            return depth < s.dirtDepth ? BlockType.Sand : BlockType.Stone;

        if (depth == 0) return worldY >= s.snowAltitude ? BlockType.Snow : BlockType.Grass;
        if (depth <= s.dirtDepth) return BlockType.Dirt;
        return BlockType.Stone;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = math.clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

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
