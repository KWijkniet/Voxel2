using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled parallel terrain generation job using the V2 5-channel spline system.
/// Mirrors the structure of <see cref="GenerateChunksBatchJob"/> with V2 settings and biome logic.
/// </summary>
[BurstCompile]
public struct GenerateChunksBatchJobV2 : IJobParallelFor
{
    public TerrainSettingsV2 Settings;

    [ReadOnly] public NativeArray<int3>     Coords;
    [ReadOnly] public NativeArray<float2>   CSpline;
    [ReadOnly] public NativeArray<float2>   ESpline;
    [ReadOnly] public NativeArray<BiomeDef> Biomes;
    [ReadOnly] public NativeArray<TreeConfig> TreeConfigs;

    public bool LowDetail;

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
        TerrainSettingsV2 s     = Settings;
        int3 coord   = Coords[i];
        int  offsetX = coord.x * Size;
        int  offsetY = coord.y * Size;
        int  offsetZ = coord.z * Size;
        int  chunkTop = offsetY + Size - 1;
        int  baseIdx  = i * VoxelCount;

        // ── Chunk-level early-exit ────────────────────────────────────────────

        int h00 = GetSurface(offsetX,        offsetZ,        s);
        int h10 = GetSurface(offsetX + Size, offsetZ,        s);
        int h01 = GetSurface(offsetX,        offsetZ + Size, s);
        int h11 = GetSurface(offsetX + Size, offsetZ + Size, s);
        int minCorner = math.min(math.min(h00, h10), math.min(h01, h11));
        int maxCorner = math.max(math.max(h00, h10), math.max(h01, h11));

        // Find max sub-surface depth across biomes for all-stone threshold
        int deepThreshold = 0;
        for (int b = 0; b < Biomes.Length; b++)
            if (Biomes[b].SubSurfaceDepth > deepThreshold)
                deepThreshold = Biomes[b].SubSurfaceDepth;

        if (chunkTop < minCorner - deepThreshold)
        {
            for (int j = 0; j < VoxelCount; j++) AllBlocks[baseIdx + j] = BlockType.Stone;
            return;
        }
        if (offsetY > maxCorner && offsetY > s.SeaLevel)
        {
            for (int j = 0; j < VoxelCount; j++) AllBlocks[baseIdx + j] = BlockType.Air;
            return;
        }

        // ── Per-column surface + biome pass ───────────────────────────────────

        var surfaces = new NativeArray<int> (Size * Size, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        var dominants = new NativeArray<int>(Size * Size, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int col = x + z * Size;
            int wx  = offsetX + x;
            int wz  = offsetZ + z;

            float c  = SampleC(wx, wz, s);
            float e  = SampleE(wx, wz, s);
            float pv = SamplePV(wx, wz, s);
            float t  = SampleT(wx, wz, s);
            float h  = SampleH(wx, wz, s);
            float rv = SampleRiver(wx, wz, s);

            int   dom;
            float heightBias;
            ComputeBiomeInfo(wx, wz, c, t, h, out dom, out heightBias);

            float continentalOffset = SplineEval(CSpline, c);
            float erosionScale      = SplineEval(ESpline, e);
            float heightF = s.SeaLevel + continentalOffset
                          + pv * erosionScale * s.MaxAmplitude + heightBias;

            if (!LowDetail && s.TerraceStep > 0f
                && e > s.TerraceErosionMin && e < s.TerraceErosionMax)
                heightF = math.floor(heightF / s.TerraceStep) * s.TerraceStep;

            if (rv < s.RiverThreshold && e > s.RiverErosionMin)
                heightF -= s.RiverCarveDepth;

            surfaces[col]  = (int)math.round(heightF);
            dominants[col] = dom;
        }

        // ── Per-voxel block assignment ────────────────────────────────────────

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int col     = x + z * Size;
            int surface = surfaces[col];
            int dom     = dominants[col];
            for (int y = 0; y < Size; y++)
                AllBlocks[baseIdx + x + y * Size + z * Size * Size] =
                    GetBlock(offsetX + x, offsetY + y, surface, dom, s);
        }

        dominants.Dispose();
        surfaces.Dispose();

        // ── LOD tree stubs (LowDetail/region chunks only) ────────────────────
        // StubHash MUST match ChunkDecorator.Hash — see ChunkDecorator.cs.
        if (LowDetail && TreeConfigs.Length > 0)
        {
            for (int lz = 0; lz < Size; lz++)
            for (int lx = 0; lx < Size; lx++)
            {
                int worldX = offsetX + lx;
                int worldZ = offsetZ + lz;

                float c  = SampleC(worldX, worldZ, s);
                float t  = SampleT(worldX, worldZ, s);
                float bh = SampleH(worldX, worldZ, s);

                int   dom  = 0;
                float bestW = 0f;
                for (int b = 0; b < Biomes.Length; b++)
                {
                    float w = AxisWeight(Biomes[b].MinC, Biomes[b].MaxC, Biomes[b].BlendC, c)
                            * AxisWeight(Biomes[b].MinT, Biomes[b].MaxT, Biomes[b].BlendT, t)
                            * AxisWeight(Biomes[b].MinH, Biomes[b].MaxH, Biomes[b].BlendH, bh);
                    if (w > bestW) { bestW = w; dom = b; }
                }

                if (dom >= TreeConfigs.Length) continue;
                TreeConfig cfg = TreeConfigs[dom];
                if (cfg.Species == TreeSpecies.None) continue;

                uint period = (uint)math.max(1, (int)math.round(1f / math.max(cfg.Density, 1e-6f)));
                uint h = StubHash(worldX, worldZ);
                if (h % period != 0) continue;

                int surface = GetSurface(worldX, worldZ, s);
                if (surface <= s.SeaLevel) continue;
                if (surface < cfg.MinAltitude || surface > cfg.MaxAltitude) continue;

                byte leafBlock = cfg.Species == TreeSpecies.Pine ? BlockType.PineNeedles
                               : cfg.Species == TreeSpecies.Dead  ? BlockType.Air
                               : BlockType.Leaves;
                int stubTrunkH = cfg.Species == TreeSpecies.Pine ? 5
                               : cfg.Species == TreeSpecies.Dead  ? 2
                               : cfg.Species == TreeSpecies.Birch ? 4 : 3;

                for (int ty = 1; ty <= stubTrunkH; ty++)
                {
                    int wy = surface + ty;
                    if (wy < offsetY || wy >= offsetY + Size) continue;
                    AllBlocks[baseIdx + lx + (wy - offsetY) * Size + lz * Size * Size] = BlockType.Log;
                }

                if (leafBlock != BlockType.Air)
                {
                    int wy = surface + stubTrunkH + 1;
                    if (wy >= offsetY && wy < offsetY + Size)
                    {
                        int ly2 = wy - offsetY;
                        AllBlocks[baseIdx + lx       + ly2 * Size + lz       * Size * Size] = leafBlock;
                        if (lx + 1 < Size) AllBlocks[baseIdx + (lx+1) + ly2 * Size + lz       * Size * Size] = leafBlock;
                        if (lx - 1 >= 0)   AllBlocks[baseIdx + (lx-1) + ly2 * Size + lz       * Size * Size] = leafBlock;
                        if (lz + 1 < Size) AllBlocks[baseIdx + lx       + ly2 * Size + (lz+1) * Size * Size] = leafBlock;
                        if (lz - 1 >= 0)   AllBlocks[baseIdx + lx       + ly2 * Size + (lz-1) * Size * Size] = leafBlock;
                    }
                }
            }
        }
    }

    // ── Cache-aware surface height ────────────────────────────────────────────

    private int GetSurface(int worldX, int worldZ, TerrainSettingsV2 s)
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

    private int ComputeSurface(int worldX, int worldZ, TerrainSettingsV2 s)
    {
        float c  = SampleC(worldX, worldZ, s);
        float e  = SampleE(worldX, worldZ, s);
        float pv = SamplePV(worldX, worldZ, s);
        float t  = SampleT(worldX, worldZ, s);
        float h  = SampleH(worldX, worldZ, s);
        float rv = SampleRiver(worldX, worldZ, s);

        int   dom;
        float heightBias;
        ComputeBiomeInfo(worldX, worldZ, c, t, h, out dom, out heightBias);

        float continentalOffset = SplineEval(CSpline, c);
        float erosionScale      = SplineEval(ESpline, e);
        float heightF = s.SeaLevel + continentalOffset
                      + pv * erosionScale * s.MaxAmplitude + heightBias;

        if (!LowDetail && s.TerraceStep > 0f
            && e > s.TerraceErosionMin && e < s.TerraceErosionMax)
            heightF = math.floor(heightF / s.TerraceStep) * s.TerraceStep;

        if (rv < s.RiverThreshold && e > s.RiverErosionMin)
            heightF -= s.RiverCarveDepth;

        return (int)math.round(heightF);
    }

    // ── Channel samplers ─────────────────────────────────────────────────────

    private static float SampleC(int wx, int wz, TerrainSettingsV2 s)
        => FBM(wx * s.CScale + 73.3f, wz * s.CScale + 41.7f, 3) * 2f - 1f; // remap → [-1,1]

    private static float SampleE(int wx, int wz, TerrainSettingsV2 s)
        => FBM(wx * s.EScale + 151.9f, wz * s.EScale + 83.1f, 3);

    private static float SamplePV(int wx, int wz, TerrainSettingsV2 s)
        => RidgedFBM(wx * s.PVScale + 237.5f, wz * s.PVScale + 129.3f, s.PVOctaves);

    private static float SampleT(int wx, int wz, TerrainSettingsV2 s)
        => FBM(wx * s.TScale + 317.7f, wz * s.TScale + 189.5f, 2);

    private static float SampleH(int wx, int wz, TerrainSettingsV2 s)
        => FBM(wx * s.HScale + 419.3f, wz * s.HScale + 261.7f, 2);

    private static float SampleRiver(int wx, int wz, TerrainSettingsV2 s)
        => FBM(wx * s.RiverMaskScale + 533.1f, wz * s.RiverMaskScale + 337.9f, 2);

    // ── Noise ─────────────────────────────────────────────────────────────────

    private static float FBM(float x, float z, int octaves,
                              float persistence = 0.5f, float lacunarity = 2f)
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
        return norm > 0f ? value / norm : 0f;
    }

    private static float RidgedFBM(float x, float z, int octaves)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float2 pos   = new float2(x * frequency + i * 7.31f, z * frequency + i * 5.17f);
            float  raw   = math.abs(noise.cnoise(pos));   // [0,1]
            float  signal = 1f - raw;
            signal       *= signal;                        // sharpen peaks
            value        += signal * amplitude;
            norm         += amplitude;
            amplitude    *= 0.5f;
            frequency    *= 2f;
        }
        return norm > 0f ? value / norm : 0f;
    }

    // ── Spline ────────────────────────────────────────────────────────────────

    private static float SplineEval(in NativeArray<float2> pts, float t)
    {
        if (t <= pts[0].x)               return pts[0].y;
        if (t >= pts[pts.Length - 1].x)  return pts[pts.Length - 1].y;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            if (t < pts[i + 1].x)
            {
                float span = pts[i + 1].x - pts[i].x;
                float frac = span > 0f ? (t - pts[i].x) / span : 0f;
                return math.lerp(pts[i].y, pts[i + 1].y, frac);
            }
        }
        return pts[pts.Length - 1].y;
    }

    // ── Biome ─────────────────────────────────────────────────────────────────

    private void ComputeBiomeInfo(int wx, int wz, float c, float t, float h,
                                  out int dominantBiome, out float heightBias)
    {
        dominantBiome = 0;
        float sumW  = 0f;
        float bestW = 0f;
        heightBias  = 0f;

        // First pass: sum weights, find dominant
        for (int b = 0; b < Biomes.Length; b++)
        {
            float w = AxisWeight(Biomes[b].MinC, Biomes[b].MaxC, Biomes[b].BlendC, c)
                    * AxisWeight(Biomes[b].MinT, Biomes[b].MaxT, Biomes[b].BlendT, t)
                    * AxisWeight(Biomes[b].MinH, Biomes[b].MaxH, Biomes[b].BlendH, h);
            sumW += w;
            if (w > bestW) { bestW = w; dominantBiome = b; }
        }

        float invSum = sumW > 1e-5f ? 1f / sumW : 1f / math.max(Biomes.Length, 1);

        // Second pass: weighted height bias
        for (int b = 0; b < Biomes.Length; b++)
        {
            float w = AxisWeight(Biomes[b].MinC, Biomes[b].MaxC, Biomes[b].BlendC, c)
                    * AxisWeight(Biomes[b].MinT, Biomes[b].MaxT, Biomes[b].BlendT, t)
                    * AxisWeight(Biomes[b].MinH, Biomes[b].MaxH, Biomes[b].BlendH, h);
            heightBias += (w * invSum) * Biomes[b].HeightBias;
        }
    }

    private static float AxisWeight(float min, float max, float blend, float value)
    {
        float lower = blend > 0f
            ? Smoothstep(min - blend, min, value)
            : value >= min ? 1f : 0f;
        float upper = blend > 0f
            ? 1f - Smoothstep(max, max + blend, value)
            : value <= max ? 1f : 0f;
        return lower * upper;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float range = edge1 - edge0;
        if (math.abs(range) < 1e-6f) return x >= edge1 ? 1f : 0f;
        float t = math.clamp((x - edge0) / range, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Deterministic column hash for LOD tree stub placement.
    /// MUST be kept byte-for-byte identical to ChunkDecorator.Hash() in ChunkDecorator.cs (Task 3).
    /// </summary>
    private static uint StubHash(int worldX, int worldZ)
    {
        uint h = (uint)(worldX * 374761393 + worldZ * 668265263);
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;
        return h;
    }

    // ── Block assignment ─────────────────────────────────────────────────────

    private byte GetBlock(int worldX, int worldY, int surface, int biomeIndex,
                          TerrainSettingsV2 s)
    {
        if (worldY > surface)
            return worldY <= s.SeaLevel ? BlockType.Water : BlockType.Air;

        if (biomeIndex < 0 || biomeIndex >= Biomes.Length)
            return BlockType.Stone;

        BiomeDef biome = Biomes[biomeIndex];
        int depth = surface - worldY;

        if (depth == 0 && biome.SnowAltitude >= 0 && worldY >= biome.SnowAltitude)
            return BlockType.Snow;

        if (depth == 0)                     return biome.SurfaceBlock;
        if (depth <= biome.SubSurfaceDepth) return biome.SubSurfaceBlock;
        return biome.DeepBlock;
    }
}
