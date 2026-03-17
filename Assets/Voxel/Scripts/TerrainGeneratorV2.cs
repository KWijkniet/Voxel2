using UnityEngine;

/// <summary>
/// Main-thread terrain generator using 5 independent noise channels and spline-driven height.
/// All methods are pure functions — no global state, safe to call from any thread context
/// (uses Mathf.PerlinNoise, which is main-thread only; Burst job wrappers come in a later pass).
/// </summary>
public static class TerrainGeneratorV2
{
    // ── Channel sample ─────────────────────────────────────────────────────────

    /// <summary>All channel values and derived data for a single world column.</summary>
    public struct ChannelSample
    {
        /// <summary>Continentalness ∈ [-1, 1]. Negative = ocean, positive = inland.</summary>
        public float C;
        /// <summary>Erosion ∈ [0, 1]. Low = rugged, high = flat.</summary>
        public float E;
        /// <summary>Peaks &amp; Valleys ∈ [0, 1]. High = peaks (ridged multifractal).</summary>
        public float PV;
        /// <summary>Temperature ∈ [0, 1]. Low = cold, high = hot.</summary>
        public float T;
        /// <summary>Humidity ∈ [0, 1]. Low = dry, high = wet.</summary>
        public float H;
        /// <summary>River mask ∈ [0, 1]. Low values indicate river channels.</summary>
        public float RiverMask;

        /// <summary>Height before terrace/river modifications (world voxels).</summary>
        public float HeightRaw;
        /// <summary>Height after terrace and river carving (world voxels).</summary>
        public float HeightFinal;

        /// <summary>Weight per biome entry, normalized to sum to 1.</summary>
        public float[] BiomeWeights;
        /// <summary>Index of the biome with the highest weight.</summary>
        public int DominantBiome;
    }

    // ── Noise ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fractional Brownian Motion. Same octave-offset convention as the original
    /// TerrainGenerator (i*7.31, i*5.17) for decorrelated octaves.
    /// Returns normalized value in approximately [0, 1].
    /// </summary>
    private static float FBM(float x, float z, int octaves,
                              float persistence = 0.5f, float lacunarity = 2f)
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
        return norm > 0f ? value / norm : 0f;
    }

    /// <summary>
    /// Ridged multifractal noise. Returns approximately [0, 1] where 1 = sharp peak crest.
    /// Uses (1 - |2*perlin - 1|)^2 per octave to produce ridge features.
    /// </summary>
    private static float RidgedFBM(float x, float z, int octaves)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            // Remap Perlin [0,1] → [-1,1] → abs → invert for ridge
            float raw    = Mathf.PerlinNoise(x * frequency + i * 7.31f,
                                             z * frequency + i * 5.17f);
            float signal = 1f - Mathf.Abs(raw * 2f - 1f);
            signal      *= signal;                          // sharpen peaks

            value     += signal * amplitude;
            norm      += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return norm > 0f ? value / norm : 0f;
    }

    // ── Biome weights ─────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the weight of a single biome on one noise axis.
    /// Weight ramps from 0 → 1 over [min-blend, min] and from 1 → 0 over [max, max+blend].
    /// If blend == 0, edges are hard step functions.
    /// </summary>
    private static float AxisWeight(float min, float max, float blend, float value)
    {
        float lower = blend > 0f
            ? Mathf.SmoothStep(min - blend, min, value)
            : value >= min ? 1f : 0f;
        float upper = blend > 0f
            ? 1f - Mathf.SmoothStep(max, max + blend, value)
            : value <= max ? 1f : 0f;
        return lower * upper;
    }

    /// <summary>
    /// Returns a normalized weight array (length = biomes.Length) for the given
    /// continentalness, temperature, and humidity values.
    /// Erosion (E) is a terrain-shape channel — it does not affect biome placement.
    /// </summary>
    public static float[] GetBiomeWeights(float c, float t, float h, BiomeDef[] biomes)
    {
        var weights = new float[biomes.Length];
        float sum   = 0f;

        for (int i = 0; i < biomes.Length; i++)
        {
            ref readonly var b = ref biomes[i];
            float w = AxisWeight(b.MinC, b.MaxC, b.BlendC, c)
                    * AxisWeight(b.MinT, b.MaxT, b.BlendT, t)
                    * AxisWeight(b.MinH, b.MaxH, b.BlendH, h);
            weights[i] = w;
            sum        += w;
        }

        if (sum < 1e-5f)
        {
            // Fallback: uniform — no biome matched; assign equal weight to all
            float eq = 1f / biomes.Length;
            for (int i = 0; i < biomes.Length; i++) weights[i] = eq;
        }
        else
        {
            for (int i = 0; i < biomes.Length; i++) weights[i] /= sum;
        }

        return weights;
    }

    private static int DominantBiomeIndex(float[] weights)
    {
        int best = 0;
        for (int i = 1; i < weights.Length; i++)
            if (weights[i] > weights[best]) best = i;
        return best;
    }

    // ── Core sampling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Samples all five noise channels and computes the full height pipeline for
    /// a single world column (worldX, worldZ). Returns a <see cref="ChannelSample"/>
    /// containing raw channel values, intermediate and final heights, and biome weights.
    /// </summary>
    public static ChannelSample SampleChannels(
        int worldX, int worldZ,
        Vector2[] cSpline, Vector2[] eSpline,
        BiomeDef[] biomes, in TerrainSettingsV2 s)
    {
        float wx = worldX, wz = worldZ;

        // Non-integer x+z offsets per channel — avoids gradient-zero lattice points
        // and keeps all channels decorrelated in both axes.
        float c  = FBM(wx * s.CScale  + 73.3f,  wz * s.CScale  + 41.7f,  3) * 2f - 1f;
        float e  = FBM(wx * s.EScale  + 151.9f, wz * s.EScale  + 83.1f,  3);
        float pv = RidgedFBM(wx * s.PVScale + 237.5f, wz * s.PVScale + 129.3f, s.PVOctaves);
        float t  = FBM(wx * s.TScale  + 317.7f, wz * s.TScale  + 189.5f, 2);
        float h  = FBM(wx * s.HScale  + 419.3f, wz * s.HScale  + 261.7f, 2);
        float rv = FBM(wx * s.RiverMaskScale + 533.1f, wz * s.RiverMaskScale + 337.9f, 2);

        // Biome weights (C, T, H only; E is terrain shape, not biome placement)
        float[] weights  = GetBiomeWeights(c, t, h, biomes);
        int     dominant = DominantBiomeIndex(weights);

        // Weighted biome height bias
        float heightBias = 0f;
        for (int i = 0; i < biomes.Length; i++)
            heightBias += weights[i] * biomes[i].HeightBias;

        // Height pipeline
        float continentalOffset = SplineUtils.Evaluate(cSpline, c);
        float erosionScale      = SplineUtils.Evaluate(eSpline, e);

        float heightRaw   = s.SeaLevel + continentalOffset
                          + pv * erosionScale * s.MaxAmplitude + heightBias;
        float heightFinal = heightRaw;

        // Terrace cliffs (applied before river carving)
        if (s.TerraceStep > 0f && e > s.TerraceErosionMin && e < s.TerraceErosionMax)
            heightFinal = Mathf.Floor(heightFinal / s.TerraceStep) * s.TerraceStep;

        // River carving
        if (rv < s.RiverThreshold && e > s.RiverErosionMin)
            heightFinal -= s.RiverCarveDepth;

        return new ChannelSample
        {
            C           = c,
            E           = e,
            PV          = pv,
            T           = t,
            H           = h,
            RiverMask   = rv,
            HeightRaw   = heightRaw,
            HeightFinal = heightFinal,
            BiomeWeights = weights,
            DominantBiome = dominant,
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the surface Y for the given world column.</summary>
    public static int GetSurface(
        int worldX, int worldZ,
        Vector2[] cSpline, Vector2[] eSpline,
        BiomeDef[] biomes, in TerrainSettingsV2 s)
    {
        return Mathf.RoundToInt(
            SampleChannels(worldX, worldZ, cSpline, eSpline, biomes, s).HeightFinal);
    }

    /// <summary>Returns the index of the dominant biome at a world column.</summary>
    public static int GetDominantBiome(
        int worldX, int worldZ,
        BiomeDef[] biomes, in TerrainSettingsV2 s)
    {
        float wx = worldX, wz = worldZ;
        float c = FBM(wx * s.CScale + 73.3f,  wz * s.CScale + 41.7f,  3) * 2f - 1f;
        float t = FBM(wx * s.TScale + 317.7f, wz * s.TScale + 189.5f, 2);
        float h = FBM(wx * s.HScale + 419.3f, wz * s.HScale + 261.7f, 2);
        var weights = GetBiomeWeights(c, t, h, biomes);
        return DominantBiomeIndex(weights);
    }

    /// <summary>
    /// Returns the block type for a given world voxel, given a pre-computed surface height
    /// and dominant biome index.
    /// </summary>
    public static byte GetBlock(
        int worldX, int worldY, int worldZ,
        int surface, int biomeIndex,
        BiomeDef[] biomes, in TerrainSettingsV2 s)
    {
        // Above surface — air or water fill
        if (worldY > surface)
            return worldY <= s.SeaLevel ? BlockType.Water : BlockType.Air;

        // Fallback if biome index is out of range
        if (biomeIndex < 0 || biomeIndex >= biomes.Length)
            return BlockType.Stone;

        var biome = biomes[biomeIndex];
        int depth = surface - worldY;

        // Altitude-based snow overrides the normal surface block
        if (depth == 0 && biome.SnowAltitude >= 0 && worldY >= biome.SnowAltitude)
            return BlockType.Snow;

        if (depth == 0)                         return biome.SurfaceBlock;
        if (depth <= biome.SubSurfaceDepth)     return biome.SubSurfaceBlock;
        return biome.DeepBlock;
    }
}
