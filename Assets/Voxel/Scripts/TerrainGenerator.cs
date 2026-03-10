using UnityEngine;

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
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the surface height (in world voxels) at (worldX, worldZ).</summary>
    public static int GetSurface(int worldX, int worldZ, in TerrainSettings s)
    {
        float wx = worldX, wz = worldZ;

        // 1. Domain warp — two independent noise channels shift the sample point
        if (s.warpStrength > 0f)
        {
            float dX = FBM(wx * s.warpScale,         wz * s.warpScale,         2, 0.5f, 2f);
            float dZ = FBM(wx * s.warpScale + 3.71f, wz * s.warpScale + 1.57f, 2, 0.5f, 2f);
            wx += (dX - 0.5f) * s.warpStrength * 2f;
            wz += (dZ - 0.5f) * s.warpStrength * 2f;
        }

        // 2. FBM terrain noise → [0,1]
        float fbm = FBM(wx * s.noiseScale, wz * s.noiseScale, s.octaves, s.persistence, s.lacunarity);

        // 3. Biome — deliberately offset so it doesn't correlate with terrain noise
        float biome = FBM(worldX * s.biomeScale + 100.3f, worldZ * s.biomeScale + 100.7f, 3, 0.6f, 2f);

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

        return Mathf.RoundToInt(height);
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
