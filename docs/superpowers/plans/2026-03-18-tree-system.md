# Tree System Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add biome-aware voxel trees (Oak, Birch, Pine, Dead) to the world with correct cross-chunk canopy placement and LOD-aware distance stubs.

**Architecture:** Two-stage pipeline: terrain job runs first (data-only), then `ChunkDecorator` places trees on the main thread once all 8 horizontal neighbors are loaded (surface found by scanning the actual voxel column — no noise recompute needed), then a mesh-only job runs on the decorated voxels. LOD 1+ regions get lightweight tree stubs inline in the Burst terrain job, driven by a `NativeArray<TreeConfig>` so stub positions always match LOD 0 decoration.

**Tech Stack:** Unity URP, C#, Unity.Jobs + Unity.Burst, Unity.Collections (NativeArray, NativeList), Unity.Mathematics

---

## File Map

| File | Status | Role |
|---|---|---|
| `Assets/Voxel/Scripts/BlockType.cs` | Modify | Add Log/Leaves/PineNeedles, tile indices, update IsTransparent, AtlasTileCount→14 |
| `Assets/Voxel/Scripts/VoxelChunk.cs` | Modify | Add SetBlock helper |
| `Assets/Voxel/Scripts/TreeConfig.cs` | **Create** | TreeSpecies enum, TreeConfig struct (blittable), CreateDefaults() |
| `Assets/Voxel/Scripts/TreeShapes.cs` | **Create** | Pre-baked BlockOffset arrays per species/height |
| `Assets/Voxel/Scripts/ChunkDecorator.cs` | **Create** | Hash(), Decorate(), PlaceTree(), FindSurface() via voxel scan |
| `Assets/Voxel/Scripts/ChunkMeshJob.cs` | Modify | Face-direction UV for Log (line 154 area) |
| `Assets/Voxel/Scripts/VoxelWorld.cs` | Modify | Two-stage pipeline; decoration gate; SubmitMeshOnly; TryDecorateReady |
| `Assets/Voxel/Scripts/GenerateChunksBatchJobV2.cs` | Modify | LOD stub post-pass; NativeArray<TreeConfig> job field |
| `Assets/Voxel/Editor/VoxelWorldEditor.cs` | Modify | Expose v2TreeConfigs in Inspector |

---

## Task 1: Block Types + VoxelChunk Helper

**Files:**
- Modify: `Assets/Voxel/Scripts/BlockType.cs`
- Modify: `Assets/Voxel/Scripts/VoxelChunk.cs`

- [ ] **Step 1: Update BlockType.cs**

Replace the entire file with:

```csharp
public static class BlockType
{
    public const byte Air         = 0;
    public const byte Stone       = 1;
    public const byte Dirt        = 2;
    public const byte Grass       = 3;
    public const byte Sand        = 4;
    public const byte Water       = 5;
    public const byte Snow        = 6;
    public const byte Sandstone   = 7;
    public const byte FrozenDirt  = 8;
    public const byte Ice         = 9;
    public const byte PackedIce   = 10;
    public const byte Log         = 11;
    public const byte Leaves      = 12;
    public const byte PineNeedles = 13;

    // Number of atlas texture tiles. Log occupies two tiles (LogTop + LogSide).
    // Atlas layout: [Stone|Dirt|Grass|Sand|Water|Snow|Sandstone|FrozenDirt|Ice|PackedIce|LogTop|LogSide|Leaves|PineNeedles]
    public const int AtlasTileCount = 14;

    // Explicit tile indices for Log (Log does NOT follow the blockType-1 formula).
    public const int LogTopTile  = 10; // top/bottom faces
    public const int LogSideTile = 11; // side faces
    // Leaves=12, PineNeedles=13 both follow blockType-1 formula.

    /// <summary>Returns true for alpha-blended block types. Add new transparent types here.</summary>
    public static bool IsTransparent(byte b) => b == Water || b == Leaves || b == PineNeedles;
}
```

- [ ] **Step 2: Add SetBlock to VoxelChunk.cs**

Add `using UnityEngine;` at the top if not already present. After the `GetBlock` method add:

```csharp
/// <summary>Writes a block at a world position. chunkOrigin = coord * 16.</summary>
public void SetBlock(Vector3Int worldPos, Vector3Int chunkOrigin, byte block)
{
    int lx = worldPos.x - chunkOrigin.x;
    int ly = worldPos.y - chunkOrigin.y;
    int lz = worldPos.z - chunkOrigin.z;
    if (lx < 0 || ly < 0 || lz < 0 || lx >= Size || ly >= Size || lz >= Size) return;
    Blocks[lx + ly * Size + lz * Size * Size] = block;
}
```

- [ ] **Step 3: Verify no compile errors in Unity**

- [ ] **Step 4: Commit**

```bash
git add Assets/Voxel/Scripts/BlockType.cs Assets/Voxel/Scripts/VoxelChunk.cs
git commit -m "feat: add Log/Leaves/PineNeedles block types and VoxelChunk.SetBlock"
```

---

## Task 2: TreeConfig + TreeShapes

**Files:**
- Create: `Assets/Voxel/Scripts/TreeConfig.cs`
- Create: `Assets/Voxel/Scripts/TreeShapes.cs`

- [ ] **Step 1: Create TreeConfig.cs**

`TreeConfig` must be blittable (unmanaged) so it can live in a `NativeArray` inside the Burst job. All fields are value types — this is satisfied.

```csharp
using System;
using UnityEngine;

public enum TreeSpecies { None, Oak, Birch, Pine, Dead }

[Serializable]
public struct TreeConfig
{
    public TreeSpecies Species;
    [Range(0f, 1f)] public float Density;
    public int MinAltitude;
    public int MaxAltitude;

    /// <summary>
    /// Default tree configs indexed by biome ID (matches BiomeDef.CreateDefaults() order):
    /// 0=DeepOcean 1=FrozenOcean 2=Ocean 3=Beach 4=Plains 5=Forest
    /// 6=Desert 7=Taiga 8=Tundra 9=Mountains
    /// </summary>
    public static TreeConfig[] CreateDefaults(int seaLevel, int snowAltitude) => new[]
    {
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.Oak,  Density = 0.01f, MinAltitude = seaLevel + 1, MaxAltitude = snowAltitude - 1 }, // Plains
        new TreeConfig { Species = TreeSpecies.Oak,  Density = 0.06f, MinAltitude = seaLevel + 1, MaxAltitude = snowAltitude - 1 }, // Forest (Oak/Birch split in decorator)
        new TreeConfig { Species = TreeSpecies.Dead, Density = 0.004f, MinAltitude = seaLevel + 1, MaxAltitude = 255 },             // Desert
        new TreeConfig { Species = TreeSpecies.Pine, Density = 0.05f,  MinAltitude = seaLevel + 1, MaxAltitude = 255 },             // Taiga
        new TreeConfig { Species = TreeSpecies.Dead, Density = 0.003f, MinAltitude = seaLevel + 1, MaxAltitude = 255 },             // Tundra
        new TreeConfig { Species = TreeSpecies.Pine, Density = 0.02f,  MinAltitude = seaLevel + 1, MaxAltitude = snowAltitude - 1 },// Mountains (MaxAltitude gates above snow line)
    };
}
```

- [ ] **Step 2: Create TreeShapes.cs**

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pre-baked voxel tree shapes. Each species has multiple height variants.
/// Height variant is selected via Hash % HeightVariantCount(species).
/// </summary>
public static class TreeShapes
{
    public struct BlockOffset
    {
        public Vector3Int pos;
        public byte block;
    }

    public static readonly BlockOffset[][] Oak;
    public static readonly BlockOffset[][] Birch;
    public static readonly BlockOffset[][] Pine;
    public static readonly BlockOffset[][] Dead;

    static TreeShapes()
    {
        Oak   = new[] { BuildOak(5),   BuildOak(6),   BuildOak(7) };
        Birch = new[] { BuildBirch(7), BuildBirch(8), BuildBirch(9) };
        Pine  = new[] { BuildPine(8),  BuildPine(9),  BuildPine(10), BuildPine(11), BuildPine(12) };
        Dead  = new[] { BuildDead(3),  BuildDead(4),  BuildDead(5) };
    }

    public static BlockOffset[] Get(TreeSpecies species, int variant) => species switch
    {
        TreeSpecies.Oak   => Oak  [variant % Oak.Length],
        TreeSpecies.Birch => Birch[variant % Birch.Length],
        TreeSpecies.Pine  => Pine [variant % Pine.Length],
        TreeSpecies.Dead  => Dead [variant % Dead.Length],
        _                 => System.Array.Empty<BlockOffset>(),
    };

    public static int HeightVariantCount(TreeSpecies species) => species switch
    {
        TreeSpecies.Oak   => Oak.Length,
        TreeSpecies.Birch => Birch.Length,
        TreeSpecies.Pine  => Pine.Length,
        TreeSpecies.Dead  => Dead.Length,
        _                 => 0,
    };

    static BlockOffset[] BuildOak(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight;
        for (int dy = -1; dy <= 2; dy++)
        {
            int radius = (dy == -1 || dy == 2) ? 2 : 3;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && dy < 0) continue;
                if (Mathf.Abs(x) + Mathf.Abs(z) > radius + 1) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, top + dy, z), block = BlockType.Leaves });
            }
        }
        return list.ToArray();
    }

    static BlockOffset[] BuildBirch(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight;
        for (int dy = -1; dy <= 2; dy++)
        {
            int radius = dy == 0 ? 2 : 1;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && dy < 0) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, top + dy, z), block = BlockType.Leaves });
            }
        }
        return list.ToArray();
    }

    static BlockOffset[] BuildPine(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int layers = trunkHeight - 2;
        for (int layer = 0; layer < layers; layer++)
        {
            int y      = trunkHeight - 1 - layer;
            int radius = Mathf.Max(1, layers - layer - 1);
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && layer == 0) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, y, z), block = BlockType.PineNeedles });
            }
        }
        list.Add(new BlockOffset { pos = new Vector3Int(0, trunkHeight, 0), block = BlockType.PineNeedles });
        return list.ToArray();
    }

    static BlockOffset[] BuildDead(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight - 1;
        list.Add(new BlockOffset { pos = new Vector3Int( 1, top,     0), block = BlockType.Log });
        list.Add(new BlockOffset { pos = new Vector3Int(-1, top - 1, 0), block = BlockType.Log });
        list.Add(new BlockOffset { pos = new Vector3Int( 0, top - 1, 1), block = BlockType.Log });
        return list.ToArray();
    }
}
```

- [ ] **Step 3: Verify Unity compiles**

- [ ] **Step 4: Commit**

```bash
git add Assets/Voxel/Scripts/TreeConfig.cs Assets/Voxel/Scripts/TreeShapes.cs
git commit -m "feat: add TreeConfig and TreeShapes data definitions"
```

---

## Task 3: ChunkDecorator

**Files:**
- Create: `Assets/Voxel/Scripts/ChunkDecorator.cs`

**Key design decision:** Surface height is found by scanning the actual voxel column (top non-air, non-water block where the block directly above is air). This avoids any noise-function mismatch between `Mathf.PerlinNoise` (main thread) and `noise.cnoise` (Burst job) — tree trunks are always placed exactly at the terrain surface. Biome determination still uses noise (T and H channels only), which may have minor discrepancies at biome edges; this is an acceptable visual trade-off.

- [ ] **Step 1: Create ChunkDecorator.cs**

```csharp
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// Main-thread tree placement pass. Called after all 8 horizontal neighbours are terrain-ready.
/// Surface height is found by scanning the actual voxel data — no noise recompute needed.
///
/// Hash function MUST stay identical to StubHash() in GenerateChunksBatchJobV2.cs.
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

            uint hash   = Hash(worldX, worldZ);
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

            int variant = (int)((hash >> 8) % (uint)TreeShapes.HeightVariantCount(species));
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
        Mathf.FloorToInt(world.x / 16f),
        Mathf.FloorToInt(world.y / 16f),
        Mathf.FloorToInt(world.z / 16f));

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
        return best;
    }

    static float AxisWeight(float min, float max, float blend, float v)
    {
        if (v < min - blend || v > max + blend) return 0f;
        float lo = Mathf.Clamp01((v - (min - blend)) / Mathf.Max(blend, 1e-5f));
        float hi = Mathf.Clamp01(((max + blend) - v)  / Mathf.Max(blend, 1e-5f));
        return lo * hi;
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
```

- [ ] **Step 2: Verify Unity compiles**

- [ ] **Step 3: Commit**

```bash
git add Assets/Voxel/Scripts/ChunkDecorator.cs
git commit -m "feat: add ChunkDecorator for two-pass tree placement"
```

---

## Task 4: Mesh Builder — Log UV

**Files:**
- Modify: `Assets/Voxel/Scripts/ChunkMeshJob.cs` (around line 154)

`Leaves` and `PineNeedles` automatically route to the transparent pass via `IsTransparent` — no other mesh changes needed for those. Only Log needs explicit UV handling.

- [ ] **Step 1: Replace the texSlice lines in GreedyFace**

Find:
```csharp
float texSlice = blockType - 1;
u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));
u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));
```

Replace with:
```csharp
float texSlice;
if (blockType == BlockType.Log)
    // normalVec is +Y or -Y for top/bottom faces, otherwise it's a side face
    texSlice = math.abs(normalVec.y) > 0.5f ? BlockType.LogTopTile : BlockType.LogSideTile;
else
    texSlice = blockType - 1;
u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));
u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));
```

`normalVec` is the `float3` parameter passed into `GreedyFace`. Each of the 6 calls in `Execute()` passes a fixed known normal, so at merge time the normal is constant for the whole face pass — no per-face lookup needed.

- [ ] **Step 2: Verify Unity compiles**

- [ ] **Step 3: Add atlas tiles in Unity**

Open the texture atlas in the Project window. Add tiles in order: `LogTop` (index 10), `LogSide` (index 11), `Leaves` (index 12), `PineNeedles` (index 13). Run **VoxelWorld → Generate Material** to rebuild. Verify the material now has 14 tiles.

- [ ] **Step 4: Commit**

```bash
git add Assets/Voxel/Scripts/ChunkMeshJob.cs
git commit -m "feat: Log face-direction UV; Leaves/PineNeedles transparent via IsTransparent"
```

---

## Task 5: VoxelWorld Two-Stage Pipeline

**Files:**
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

The pipeline changes from one stage (terrain+mesh chained) to two stages for V2 LOD 0 chunks:
1. **Stage 1**: terrain-only job → voxels stored → `_awaitingDecoration`
2. **Stage 2**: `TryDecorateReady()` decorates → `SubmitMeshOnly()` → mesh job → mesh created

`coord` stays in `_inFlight` across both stages to prevent re-submission.

- [ ] **Step 1: Add new fields**

After the `_inFlight` declaration (line ~155), add:

```csharp
// V2 decoration gate
private readonly HashSet<Vector3Int> _awaitingDecoration       = new();
private readonly HashSet<Vector3Int> _needsMeshAfterDecoration = new();
```

Add the serialized field after `v2Biomes`:
```csharp
[Header("Terrain V2 — Trees")]
[Tooltip("Tree configs per biome. Leave empty to use built-in defaults.")]
public TreeConfig[] v2TreeConfigs;

[Tooltip("Max decoration passes (tree placement) per frame.")]
public int maxDecorationsPerFrame = 4;
```

- [ ] **Step 2: Initialise v2TreeConfigs**

In `AllocateV2NativeArrays()` (or wherever `v2Biomes` defaults are applied), after setting up `_v2Biomes`, add:

```csharp
if (v2TreeConfigs == null || v2TreeConfigs.Length == 0)
    v2TreeConfigs = TreeConfig.CreateDefaults(v2SeaLevel, snowAltitude);
```

(`snowAltitude` is the existing `VoxelWorld` field at line 68.)

- [ ] **Step 3: Change V2 submission to terrain-only**

In `SubmitSubBatch`, replace the V2 branch (around line 728) with:

```csharp
if (useV2Generator)
{
    // V2 always submits terrain-only. Mesh is scheduled separately after decoration.
    if (buildMesh)
        for (int i = 0; i < count; i++)
            _needsMeshAfterDecoration.Add(coords[offset + i]);

    var terrainJob = new GenerateChunksBatchJobV2
    {
        Settings           = GetTerrainSettingsV2(),
        Coords             = batch.Coords,
        LowDetail          = !buildMesh,   // LOD 0 chunks are full-detail terrain; stubs only in LowDetail
        AllBlocks          = batch.Data,
        SurfaceCache       = batch.SurfaceCache,
        SurfaceCacheWriter = batch.SurfaceCache.AsParallelWriter(),
        CSpline            = _v2CSpline,
        ESpline            = _v2ESpline,
        Biomes             = _v2Biomes,
        TreeConfigs        = _v2TreeConfigs,   // new field — see Task 6
    };
    terrainHandle = terrainJob.Schedule(count, 1);
}
```

Then in the per-chunk pipeline loop (line ~764), change the condition so V2 always adds a terrain-only pipeline:

```csharp
if (!buildMesh || useV2Generator)
{
    _pipelines.Add(new ChunkPipeline
    {
        Coord     = coord,
        Handle    = terrainHandle,
        Voxels    = voxels,
        Batch     = batch,
        BuildMesh = false,
    });
    continue;
}
// Non-V2 mesh path unchanged below...
```

- [ ] **Step 4: Gate _inFlight removal and region feeding in ProcessCompletedPipelines**

After line 896 (`_chunks[p.Coord] = chunk;`), add:

```csharp
if (useV2Generator && !p.BuildMesh)
{
    // Stage 1 complete — queue for decoration. Keep coord in _inFlight.
    _awaitingDecoration.Add(p.Coord);
    // Don't call _inFlight.Remove or TryFeedChunkIntoRegion yet.
    // Region feeding happens after decoration in TryDecorateReady().
    _needsMoreRequests = true;
    anyApplied = true;
    continue;  // skip the rest of this pipeline's processing
}
```

Place this block BEFORE the existing `if (p.BuildMesh && ...)` mesh creation block.

- [ ] **Step 5: Add TryDecorateReady()**

Add this method to VoxelWorld:

```csharp
private void TryDecorateReady()
{
    var ready = new List<Vector3Int>();
    foreach (var coord in _awaitingDecoration)
        if (ChunkDecorator.AllNeighboursReady(coord, _chunks))
            ready.Add(coord);

    int count = 0;
    foreach (var coord in ready)
    {
        if (count >= maxDecorationsPerFrame) break;

        ChunkDecorator.Decorate(coord, _chunks, v2TreeConfigs, _v2Biomes, GetTerrainSettingsV2());
        _awaitingDecoration.Remove(coord);
        count++;

        // Feed into region NOW (after decoration, so region gets decorated voxels)
        TryFeedChunkIntoRegion(coord, _chunks[coord]);

        bool wantsMesh = _needsMeshAfterDecoration.Remove(coord)
                      && _desiredCoords.Contains(coord)
                      && !_chunkMeshes.ContainsKey(coord);

        if (wantsMesh)
            SubmitMeshOnly(coord); // coord stays in _inFlight until mesh pipeline completes
        else
            _inFlight.Remove(coord); // data-only — fully done

        _needsMoreRequests = true;
    }
}
```

Call `TryDecorateReady()` at the END of `ProcessCompletedPipelines`, just before `return anyApplied;`.

Also: for V2 terrain-only pipelines that completed above (the `continue` branch in Step 4), we already skipped `TryFeedChunkIntoRegion`. For non-V2 pipelines and V2 mesh-only pipelines, the existing `TryFeedChunkIntoRegion` call at line 923 still fires as before.

- [ ] **Step 6: Add SubmitMeshOnly()**

```csharp
private void SubmitMeshOnly(Vector3Int coord)
{
    if (!_chunks.TryGetValue(coord, out var chunk)) { _inFlight.Remove(coord); return; }

    // Snapshot voxels and neighbours — all horizontal neighbours guaranteed in _chunks
    var snapshots = SnapshotNeighbours(coord, out int neighbourMask);
    var voxelsCopy = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                            NativeArrayOptions.UninitializedMemory);
    NativeArray<byte>.Copy(chunk.Blocks, voxelsCopy, VoxelChunk.VoxelCount);

    var verts  = new NativeList<float3>(4096, Allocator.Persistent);
    var norms  = new NativeList<float3>(4096, Allocator.Persistent);
    var uvs    = new NativeList<float2>(4096, Allocator.Persistent);
    var uv2s   = new NativeList<float2>(4096, Allocator.Persistent);
    var tris   = new NativeList<int>   (6144, Allocator.Persistent);
    var tVerts = new NativeList<float3>(512,  Allocator.Persistent);
    var tNorms = new NativeList<float3>(512,  Allocator.Persistent);
    var tUvs   = new NativeList<float2>(512,  Allocator.Persistent);
    var tUv2s  = new NativeList<float2>(512,  Allocator.Persistent);
    var tTris  = new NativeList<int>   (768,  Allocator.Persistent);

    var meshJob = new BuildChunkMeshJob
    {
        Voxels        = voxelsCopy,
        N_PX = snapshots[0], N_NX = snapshots[1],
        N_PY = snapshots[2], N_NY = snapshots[3],
        N_PZ = snapshots[4], N_NZ = snapshots[5],
        NeighbourMask = neighbourMask,
        Step          = 1,
        Vertices      = verts,  Normals      = norms,  UVs      = uvs,  UV2s      = uv2s,  Triangles      = tris,
        TransVertices = tVerts, TransNormals = tNorms, TransUVs = tUvs, TransUV2s = tUv2s, TransTriangles = tTris,
    };

    _pipelines.Add(new ChunkPipeline
    {
        Coord              = coord,
        Handle             = meshJob.Schedule(),
        Voxels             = voxelsCopy,
        NeighbourSnapshots = snapshots,
        BuildMesh          = true,
        MeshVerts = verts, MeshNorms = norms, MeshUVs = uvs, MeshUV2s = uv2s, MeshTris = tris,
        TransVerts = tVerts, TransNorms = tNorms, TransUVs = tUvs, TransUV2s = tUv2s, TransTris = tTris,
    });
    // Batch is null — ProcessCompletedPipelines will dispose voxelsCopy via p.Voxels.Dispose()
    // when the pipeline completes. This is safe; the existing code at line 892 handles null Batch.
}
```

When this mesh pipeline completes in `ProcessCompletedPipelines`, the existing code:
- Copies `p.Voxels` (voxelsCopy) into a fresh `ownedVoxels`
- Disposes `p.Voxels` (voxelsCopy)
- Overwrites `_chunks[p.Coord]` (disposes the decorated chunk's Blocks, stores ownedVoxels instead)
- Creates the mesh
- Calls `_inFlight.Remove(p.Coord)` — this is correct since `p.BuildMesh = true`

No changes needed to the existing completion logic for this case.

- [ ] **Step 7: Test in Play mode**

Enter Play mode with `useV2Generator = true`. Confirm:
- Chunks load (no stall — `_awaitingDecoration` drains over time)
- No NativeArray safety errors or double-dispose exceptions
- Trees appear on the terrain surface (not floating, not buried)

- [ ] **Step 8: Commit**

```bash
git add Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "feat: two-stage V2 pipeline — terrain-only, decorate, then mesh"
```

---

## Task 6: LOD Stubs in Burst Job

**Files:**
- Modify: `Assets/Voxel/Scripts/GenerateChunksBatchJobV2.cs`

Add `NativeArray<TreeConfig>` as a job field so stub density matches the user-tunable inspector values (no hardcoded periods). Also add `StubHash()` — kept verbatim to `ChunkDecorator.Hash()`.

- [ ] **Step 1: Add TreeConfigs field to the job struct**

After the existing `[ReadOnly] public NativeArray<BiomeDef> Biomes;` field, add:

```csharp
[ReadOnly] public NativeArray<TreeConfig> TreeConfigs;
```

- [ ] **Step 2: Allocate and pass _v2TreeConfigs in VoxelWorld**

In `AllocateV2NativeArrays()` (or wherever `_v2Biomes` is allocated), add:

```csharp
// TreeConfig is blittable (all value-type fields) so NativeArray is valid
_v2TreeConfigs = new NativeArray<TreeConfig>(v2TreeConfigs, Allocator.Persistent);
```

Add the field declaration near `_v2Biomes`:
```csharp
private NativeArray<TreeConfig> _v2TreeConfigs;
```

Dispose it in `OnDestroy` / `DeallocateV2NativeArrays()` alongside `_v2Biomes`.

- [ ] **Step 3: Add StubHash + tree stub post-pass to Execute()**

After the per-voxel block assignment loop, append to `Execute()`:

```csharp
// ── LOD tree stubs (regions only) ────────────────────────────────────────
// Simplified trunk + leaf cap. StubHash MUST match ChunkDecorator.Hash — see ChunkDecorator.cs.
if (LowDetail && TreeConfigs.Length > 0)
{
    for (int lz = 0; lz < Size; lz++)
    for (int lx = 0; lx < Size; lx++)
    {
        int worldX = offsetX + lx;
        int worldZ = offsetZ + lz;

        // Get dominant biome
        float c  = SampleC(worldX, worldZ, s);
        float t  = SampleT(worldX, worldZ, s);
        float bh = SampleH(worldX, worldZ, s);
        int   dom = 0; float bestW = 0f;
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
                int ly = wy - offsetY;
                AllBlocks[baseIdx + lx + ly * Size + lz * Size * Size] = leafBlock;
                if (lx + 1 < Size) AllBlocks[baseIdx + (lx+1) + ly * Size + lz * Size * Size] = leafBlock;
                if (lx - 1 >= 0)   AllBlocks[baseIdx + (lx-1) + ly * Size + lz * Size * Size] = leafBlock;
                if (lz + 1 < Size) AllBlocks[baseIdx + lx + ly * Size + (lz+1) * Size * Size] = leafBlock;
                if (lz - 1 >= 0)   AllBlocks[baseIdx + lx + ly * Size + (lz-1) * Size * Size] = leafBlock;
            }
        }
    }
}
```

- [ ] **Step 4: Add StubHash method to the job struct**

```csharp
/// <summary>
/// Deterministic column hash for tree stub placement.
/// MUST be kept byte-for-byte identical to ChunkDecorator.Hash() in ChunkDecorator.cs.
/// </summary>
private static uint StubHash(int worldX, int worldZ)
{
    uint h = (uint)(worldX * 374761393 + worldZ * 668265263);
    h ^= h >> 13;
    h *= 1274126177u;
    h ^= h >> 16;
    return h;
}
```

- [ ] **Step 5: Verify Unity compiles; enter Play mode**

Move far enough from spawn that LOD 1+ regions render. Verify tree stubs appear as small coloured columns at distance.

- [ ] **Step 6: Commit**

```bash
git add Assets/Voxel/Scripts/GenerateChunksBatchJobV2.cs Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "feat: LOD tree stubs in Burst job driven by NativeArray<TreeConfig>"
```

---

## Task 7: Inspector

**Files:**
- Modify: `Assets/Voxel/Editor/VoxelWorldEditor.cs`

- [ ] **Step 1: Expose v2TreeConfigs and maxDecorationsPerFrame**

In the V2 biome section of the custom editor, add:

```csharp
EditorGUILayout.PropertyField(
    serializedObject.FindProperty("v2TreeConfigs"),
    new GUIContent("V2 Tree Configs"), includeChildren: true);
EditorGUILayout.PropertyField(
    serializedObject.FindProperty("maxDecorationsPerFrame"),
    new GUIContent("Max Decorations/Frame"));
```

- [ ] **Step 2: Verify fields appear in Inspector under "Terrain V2 — Trees"**

- [ ] **Step 3: Commit**

```bash
git add Assets/Voxel/Editor/VoxelWorldEditor.cs
git commit -m "feat: expose v2TreeConfigs and maxDecorationsPerFrame in Inspector"
```

---

## Final Integration Test

- [ ] Enter Play mode with `useV2Generator = true`, `viewDistance = 8`, `lodLevels = 2`
- [ ] Walk through Forest/Taiga/Plains: verify trees spawn at terrain surface with correct species
- [ ] Move to LOD 1+ distance: verify tree stubs visible in regions
- [ ] Confirm LOD 0/1 boundary has no obvious seam (positions match — same hash)
- [ ] No console errors (NativeArray safety, double-dispose, job handle leaks)
- [ ] Tune `v2TreeConfigs` density values in Inspector if density looks wrong
- [ ] Commit any tuning adjustments
