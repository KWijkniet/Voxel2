using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;

/// <summary>
/// Main-thread tree placement pass. Called after all 8 horizontal neighbours are terrain-ready.
/// Surface height is found by scanning the actual voxel data — no noise recompute needed.
///
/// Hash function MUST stay identical to StubHash() in GenerateChunksBatchJobV2.cs (added in Task 6).
/// </summary>
public static class ChunkDecorator
{
    private static readonly Vector3Int[] HNeighbours =
    {
        new(-1,0,-1), new(0,0,-1), new(1,0,-1),
        new(-1,0, 0),              new(1,0, 0),
        new(-1,0, 1), new(0,0, 1), new(1,0, 1),
    };

    public static bool AllNeighboursReady(Vector3Int coord, Dictionary<Vector3Int, VoxelChunk> chunks)
    {
        foreach (var off in HNeighbours)
            if (!chunks.ContainsKey(coord + off)) return false;
        return true;
    }

    /// <summary>
    /// Places trees for all 9 chunks in the 3×3 grid centred on coord.
    /// All 9 chunks guaranteed to be in _chunks with no live job handles.
    /// </summary>
    public static void Decorate(
        Vector3Int coord,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        TreeConfig[] treeConfigs,
        NativeArray<BiomeDef> biomes,
        TerrainSettingsV2 settings)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            PlaceTreesForChunk(new Vector3Int(coord.x + dx, coord.y, coord.z + dz),
                               chunks, treeConfigs, biomes, settings);
    }

    static void PlaceTreesForChunk(
        Vector3Int sourceCoord,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        TreeConfig[] treeConfigs,
        NativeArray<BiomeDef> biomes,
        TerrainSettingsV2 settings)
    {
        if (!chunks.TryGetValue(sourceCoord, out var sourceChunk)) return;
        int baseX = sourceCoord.x * 16;
        int baseZ = sourceCoord.z * 16;
        int baseY = sourceCoord.y * 16;

        for (int lz = 0; lz < 16; lz++)
        for (int lx = 0; lx < 16; lx++)
        {
            int worldX = baseX + lx;
            int worldZ = baseZ + lz;

            // Find terrain surface by scanning the voxel column top-to-bottom
            int surfaceY = FindSurface(sourceChunk, lx, lz, baseY, sourceCoord, chunks);
            if (surfaceY < 0) continue; // no surface in this chunk layer

            uint hash    = Hash(worldX, worldZ);
            int  biomeIdx = GetDominantBiome(worldX, worldZ, biomes, settings);
            if (biomeIdx < 0 || biomeIdx >= treeConfigs.Length) continue;

            TreeConfig cfg = treeConfigs[biomeIdx];
            if (cfg.Species == TreeSpecies.None) continue;
            if (surfaceY < cfg.MinAltitude || surfaceY > cfg.MaxAltitude) continue;
            if (surfaceY <= settings.SeaLevel) continue;

            uint period = (uint)Mathf.RoundToInt(1f / Mathf.Max(cfg.Density, 1e-6f));
            if (hash % period != 0) continue;

            // Forest biome: split Oak/Birch 50/50 by hash bit
            TreeSpecies species = cfg.Species;
            if (species == TreeSpecies.Oak && (hash & 1u) == 1u)
                species = TreeSpecies.Birch;

            int variantCount = TreeShapes.HeightVariantCount(species);
            if (variantCount == 0) continue;
            int variant = (int)((hash >> 8) % (uint)variantCount);
            var offsets  = TreeShapes.Get(species, variant);

            foreach (var off in offsets)
            {
                var targetWorld = new Vector3Int(worldX + off.pos.x, surfaceY + 1 + off.pos.y, worldZ + off.pos.z);
                var targetCoord = WorldToChunkCoord(targetWorld);
                if (!chunks.TryGetValue(targetCoord, out var targetChunk)) continue;
                var origin = targetCoord * 16;
                targetChunk.SetBlock(targetWorld, origin, off.block);
                chunks[targetCoord] = targetChunk; // VoxelChunk is a struct — write back after mutation
            }
        }
    }

    /// <summary>
    /// Finds the surface Y by scanning the voxel column from top to bottom.
    /// Returns world Y of the topmost solid (non-air, non-water) block where the block above is air.
    /// Returns -1 if no surface found in this chunk layer.
    /// </summary>
    static int FindSurface(VoxelChunk chunk, int lx, int lz, int chunkBaseY,
                            Vector3Int sourceCoord, Dictionary<Vector3Int, VoxelChunk> chunks)
    {
        for (int ly = 15; ly >= 0; ly--)
        {
            byte b = chunk.GetBlock(lx, ly, lz);
            if (b == BlockType.Air || b == BlockType.Water) continue;

            bool topIsAir;
            if (ly < 15)
            {
                byte above = chunk.GetBlock(lx, ly + 1, lz);
                topIsAir = above == BlockType.Air || above == BlockType.Water;
            }
            else
            {
                // Top of chunk — check the chunk above
                var aboveCoord = new Vector3Int(sourceCoord.x, sourceCoord.y + 1, sourceCoord.z);
                if (chunks.TryGetValue(aboveCoord, out var aboveChunk))
                {
                    byte above = aboveChunk.GetBlock(lx, 0, lz);
                    topIsAir = above == BlockType.Air || above == BlockType.Water;
                }
                else
                    topIsAir = true; // chunk above not loaded → assume open air
            }

            if (topIsAir) return chunkBaseY + ly;
        }
        return -1;
    }

    static Vector3Int WorldToChunkCoord(Vector3Int world) => new(
        world.x >= 0 ? world.x / 16 : (world.x - 15) / 16,
        world.y >= 0 ? world.y / 16 : (world.y - 15) / 16,
        world.z >= 0 ? world.z / 16 : (world.z - 15) / 16);

    // ── Biome determination (noise — main thread only) ────────────────────────
    // Note: uses Mathf.PerlinNoise; the Burst job uses noise.cnoise. Results may
    // differ slightly at biome edges, causing occasional tree-type mismatches there.
    // This is an acceptable visual trade-off; surface heights are NOT computed here.

    static int GetDominantBiome(int worldX, int worldZ, NativeArray<BiomeDef> biomes, TerrainSettingsV2 s)
    {
        float c = FBM(worldX * s.CScale + 73.3f,  worldZ * s.CScale + 41.7f,  3) * 2f - 1f;
        float t = FBM(worldX * s.TScale + 317.7f, worldZ * s.TScale + 189.5f, 2);
        float h = FBM(worldX * s.HScale + 419.3f, worldZ * s.HScale + 261.7f, 2);

        int   best  = 0;
        float bestW = 0f;
        for (int b = 0; b < biomes.Length; b++)
        {
            float w = AxisWeight(biomes[b].MinC, biomes[b].MaxC, biomes[b].BlendC, c)
                    * AxisWeight(biomes[b].MinT, biomes[b].MaxT, biomes[b].BlendT, t)
                    * AxisWeight(biomes[b].MinH, biomes[b].MaxH, biomes[b].BlendH, h);
            if (w > bestW) { bestW = w; best = b; }
        }
        if (bestW <= 0f) return -1;
        return best;
    }

    static float AxisWeight(float min, float max, float blend, float v)
    {
        float lower = blend > 0f ? Smoothstep(min - blend, min, v) : v >= min ? 1f : 0f;
        float upper = blend > 0f ? 1f - Smoothstep(max, max + blend, v) : v <= max ? 1f : 0f;
        return lower * upper;
    }

    static float Smoothstep(float edge0, float edge1, float x)
    {
        float range = edge1 - edge0;
        if (Mathf.Abs(range) < 1e-6f) return x >= edge1 ? 1f : 0f;
        float t = Mathf.Clamp01((x - edge0) / range);
        return t * t * (3f - 2f * t);
    }

    static float FBM(float x, float z, int octaves, float persistence = 0.5f, float lacunarity = 2f)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            value     += Mathf.PerlinNoise(x * frequency + i * 7.31f, z * frequency + i * 5.17f) * amplitude;
            norm      += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return norm > 0f ? value / norm : 0f;
    }

    /// <summary>
    /// Deterministic column hash. MUST be kept byte-for-byte identical to
    /// StubHash() in GenerateChunksBatchJobV2.cs.
    /// </summary>
    public static uint Hash(int worldX, int worldZ)
    {
        uint h = (uint)(worldX * 374761393 + worldZ * 668265263);
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;
        return h;
    }
}
