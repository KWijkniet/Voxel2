# Tree System Design
**Date:** 2026-03-18
**Project:** Voxel2 (Unity URP, Burst job terrain)

---

## Overview

Add biome-aware voxel trees to the world with correct cross-chunk canopy placement and LOD-aware distance representation. Trees appear at all render distances: full geometry at LOD 0 (within `viewDistance`), simplified stubs at LOD 1+ (region meshes).

---

## 1. New Block Types

Three new entries in `BlockType.cs`:

| ID | Name | Transparent | Atlas tiles |
|---|---|---|---|
| 11 | `Log` | No | 2 tiles: `LogTop` (index 10, top/bottom faces), `LogSide` (index 11, 4 side faces) |
| 12 | `Leaves` | Yes | 1 tile (index 12) |
| 13 | `PineNeedles` | Yes | 1 tile (index 13) |

`AtlasTileCount` increases from 10 to **14**.

`IsTransparent(byte b)` updated to: `b == Water || b == Leaves || b == PineNeedles`

**Log breaks the 1:1 `blockType - 1` tile index formula.** The mesh builder uses face-direction UV selection for Log: when emitting a Log face, if the face normal is ±Y use tile 10 (`LogTop`), otherwise use tile 11 (`LogSide`). The greedy mesher already runs separate passes per axis/direction, so the normal is known at merge time — this is a targeted single-line change per pass, not a full rewrite.

`Leaves` and `PineNeedles` join the existing transparent draw pass alongside Water.

---

## 2. Tree Shapes

A new `TreeShapes.cs` static class stores pre-baked offset arrays for each species. Inside `ChunkDecorator` (main-thread CPU code) offsets use `Vector3Int`. Inside `GenerateChunksBatchJobV2` (Burst) a `NativeArray<int3>` mirror is passed as a job field — `TreeShapes` exposes a static method to convert to `int3[]` for this purpose.

No runtime geometry math during placement — decoration is pure lookup + write.

| Species | Trunk | Canopy | Blocks |
|---|---|---|---|
| **Oak** | 1×1, 5–7 blocks tall (hash-varied) | Sphere ~3–4 radius, centered 1 below top | Log + Leaves |
| **Birch** | 1×1, 7–9 blocks tall | Small oval ~2 radius at top | Log + Leaves |
| **Pine** | 1×1, 8–12 blocks tall | Stacked shrinking rings (cone), widest at bottom | Log + PineNeedles |
| **Dead** | 1×1, 3–5 blocks tall | A few stub Log branches, no leaves | Log only |

Height variation within each species range is determined by `Hash(worldX, worldZ) % heightRange`. Shapes are stored as arrays indexed by height so each variant is pre-baked.

---

## 3. Biome Tree Configuration

A new `TreeConfig` struct (CPU-only, not Burst-compatible):

```csharp
struct TreeConfig {
    TreeSpecies Species;   // Oak, Birch, Pine, Dead, None
    float       Density;   // probability per column (e.g. 0.02 = 2%)
    int         MinAltitude;
    int         MaxAltitude;
}
```

Default assignments:

| Biome | Species | Density | Notes |
|---|---|---|---|
| Plains | Oak | 0.01 | Sparse |
| Forest | Oak / Birch (50/50 by hash) | 0.06 | Dense |
| Taiga | Pine | 0.05 | |
| Desert | Dead | 0.004 | Very rare |
| Tundra | Dead | 0.003 | Very rare |
| Mountains | Pine | 0.02 | MaxAltitude = snow line; above snow line no trees spawn (config.Species = None for that altitude check) |
| All others | None | — | |

`TreeConfig[]` is serialized on `VoxelWorld` alongside `BiomeDef[]`, one entry per biome, tunable from the Inspector.

---

## 4. Hash Function

Both the decoration pass and LOD stub use the **same** static hash function to guarantee spawn positions match across LOD levels:

```csharp
static uint Hash(int worldX, int worldZ) {
    uint h = (uint)(worldX * 374761393 + worldZ * 668265263);
    h ^= h >> 13;
    h *= 1274126177u;
    h ^= h >> 16;
    return h;
}
```

Spawn check: `Hash(worldX, worldZ) % SpawnPeriod < 1` where `SpawnPeriod = (uint)Mathf.RoundToInt(1f / density)` (rounded, not truncated — `1f/0.06` = 16.67 truncates to 16 which shifts density by ~4%).

This function lives in `ChunkDecorator.cs` as a `static` method and is **duplicated verbatim** as a `static` method in `GenerateChunksBatchJobV2` for Burst compatibility (Burst cannot call managed static methods from external classes). Both copies must be kept byte-for-byte identical — a comment in each file should reference the other. If they drift, LOD 0 and LOD 1+ tree positions diverge at the seam.

---

## 5. Decoration Pass Architecture (LOD 0)

### New files
- `ChunkDecorator.cs` — static class, all tree placement logic (managed C#, main thread only)

### VoxelWorld additions
- `HashSet<Vector3Int> _awaitingDecoration` — terrain done, waiting for 8 horizontal neighbors
- `HashSet<Vector3Int> _decorationDone` — decorated, eligible for mesh scheduling
- `TreeConfig[] v2TreeConfigs` — serialized field

### Chunk state machine
```
[terrain job completes]
       ↓
  _awaitingDecoration   ← added in ProcessCompletedPipelines after Handle.Complete()
       ↓  (all 8 horizontal neighbors in _chunks)
  _decorationDone       ← ChunkDecorator.Decorate() called; chunk removed from _awaitingDecoration
       ↓
  mesh job scheduled    ← unchanged from current flow
```

Chunks in `_awaitingDecoration` whose neighbors are in the LOD 1+ zone will still eventually enter `_chunks` (data-only pipeline chunks are also copied to `_chunks` in `ProcessCompletedPipelines`), so the gate does not deadlock under normal loading. Chunks at the absolute load radius edge whose outer neighbors are never requested remain in `_awaitingDecoration` indefinitely but are unloaded with those neighbors when the player moves away — no explicit timeout is required.

### Per-frame decoration check (in `ProcessCompletedPipelines`)

After copying terrain voxels to `_chunks[coord]`:
1. Add `coord` to `_awaitingDecoration`
2. For each coord in `_awaitingDecoration`: check all 8 horizontal neighbors are in `_chunks`
3. If yes → `ChunkDecorator.Decorate(coord, _chunks, v2TreeConfigs, biomes, settings)` → move to `_decorationDone`

Budget: process up to `maxDecorationsPerFrame` decorations per frame (analogous to existing `maxApplyPerFrame` limit). Default: **4 per frame** — each decoration call iterates 9×256 = 2304 columns with noise recompute, so 4 keeps frame cost bounded.

### `ChunkDecorator.Decorate()` — settings access

`TerrainSettingsV2` is a blittable value-type struct. It is passed by value to `Decorate()`. Surface height is **recomputed from noise** (not from `SurfaceCache`, which is scoped to a `BatchBuffer` and already disposed by decoration time). Biome is also recomputed via `TerrainGeneratorV2.GetDominantBiome()`. Since both the terrain job and the decorator call the same deterministic noise functions with the same inputs, recomputed surface heights are guaranteed to be identical to those used during terrain generation — tree trunks will always be placed exactly at the terrain surface.

### `ChunkDecorator.Decorate()` — placement logic

The 3×3 grid centered on `coord` covers exactly the 8 horizontal neighbors plus `coord` itself — consistent with the "all 8 neighbors in `_chunks`" gate above.

```
For each of 9 chunks in 3×3 grid centered on coord:
  For each of 256 columns (16×16):
    worldX, worldZ = column world position
    hash     = Hash(worldX, worldZ)
    biome    = TerrainGeneratorV2.GetDominantBiome(worldX, worldZ, settings, biomes)
    config   = treeConfigs[biome]
    if config.Species == None → skip
    if !SpawnCheck(hash, config.Density) → skip
    surfaceY = TerrainGeneratorV2.ComputeSurfaceHeight(worldX, worldZ, settings)
    if surfaceY < config.MinAltitude || surfaceY > config.MaxAltitude → skip
    species  = ResolveSpecies(config, hash)
    height   = ResolveHeight(species, hash)
    PlaceTree(worldX, surfaceY, worldZ, species, height, _chunks)

PlaceTree():
  For each (offset, blockType) in TreeShapes.Get(species, height):
    targetWorld = (worldX + offset.x, surfaceY + offset.y, worldZ + offset.z)
    targetChunk = WorldToChunkCoord(targetWorld)
    if _chunks.ContainsKey(targetChunk):
      _chunks[targetChunk].SetBlock(targetWorld, blockType)
```

`VoxelChunk.SetBlock(Vector3Int worldPos, byte block)` converts world position to local voxel index via `(worldPos - chunkOrigin)` and writes to `Blocks[x + z*16 + y*256]`. If `VoxelChunk` does not already expose this helper, it is added as part of this feature.

Cross-chunk canopy writes are safe because:
- All 9 chunks in the 3×3 are guaranteed to be in `_chunks` (decoration gate)
- `_chunks[coord]` is added only after `Handle.Complete()` (pipeline fully done, no job holds the NativeArray)
- Decoration runs on the main thread, so no concurrent writes

---

## 6. LOD Tree Stubs (LOD 1+ Regions)

Tree stubs are generated inside `GenerateChunksBatchJobV2` as a Burst-compatible post-pass after normal block assignment. No decoration gate, no cross-chunk writes, no neighbor dependency.

**Burst compatibility:** `TreeShapes` offset arrays are converted to `NativeArray<int3>` and passed as a job field (`[ReadOnly] NativeArray<int3> StubOffsets`). `Vector3Int` is not used inside the job.

**Per-column stub logic:**
- Same `Hash(worldX, worldZ)` function and `SpawnCheck` as CPU decorator → identical spawn positions
- Same `ComputeSurface()` call (already computed for this column in the terrain pass)
- Place `Log` blocks from `surfaceY` to `surfaceY + stubHeight` (stubHeight = half full tree height)
- Place one flat ring of `Leaves` or `PineNeedles` at `surfaceY + stubHeight + 1`
- **Clamped to chunk bounds** — no cross-chunk writes from within the Burst job (parallel writes to other chunks' indices would be a data race). Trees near a chunk edge will have their canopy truncated at the LOD 1+ boundary; this is acceptable at region distances where individual blocks are sub-pixel.

**Result:** At LOD 1+ distances, forests appear as small colored columns above the terrain silhouette. Spawn positions are identical to LOD 0, so the LOD transition boundary is seamless.

---

## Files Changed / Created

| File | Change |
|---|---|
| `BlockType.cs` | Add Log (11), Leaves (12), PineNeedles (13); update `IsTransparent`; update `AtlasTileCount` to 14 |
| `TreeShapes.cs` | **New** — pre-baked offset arrays per species/height; `Vector3Int[]` for CPU, `int3[]` converter for Burst |
| `TreeConfig.cs` | **New** — `TreeConfig` struct, `TreeSpecies` enum, `CreateDefaults()` |
| `ChunkDecorator.cs` | **New** — static decoration logic, `Hash()`, `SpawnCheck()`, `PlaceTree()` |
| `VoxelWorld.cs` | Add `_awaitingDecoration`, `_decorationDone` sets; `TreeConfig[] v2TreeConfigs`; gate mesh scheduling on `_decorationDone`; call decorator in `ProcessCompletedPipelines` |
| `GenerateChunksBatchJobV2.cs` | Add Burst stub pass; `NativeArray<int3> StubOffsets` job field; duplicate `Hash()` as static method |
| `BuildChunkMeshJob.cs` | Face-direction UV selection for Log (top/bottom → tile 10, sides → tile 11); add Leaves/PineNeedles to transparent pass |
| `VoxelWorldEditor.cs` | Expose `TreeConfig[]` in Inspector |

---

## Out of Scope

- Animated leaves (wind shader)
- Leaf particle effects
- Tree colliders
- Fallen logs or roots
- Invalidating already-built neighbor meshes after decoration (decoration gate prevents this scenario during normal generation)
