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
| 11 | `Log` | No | 2 tiles: `LogTop` (top/bottom faces), `LogSide` (4 side faces) |
| 12 | `Leaves` | Yes | 1 tile |
| 13 | `PineNeedles` | Yes | 1 tile |

`Leaves` and `PineNeedles` join the existing transparent draw pass (currently used by Water). `Log` requires face-dependent UV selection in the mesh builder: top/bottom faces use `LogTop` tile, side faces use `LogSide` tile. This is a targeted extension — only Log blocks trigger the face-direction UV lookup.

---

## 2. Tree Shapes

A new `TreeShapes.cs` static class stores pre-baked offset arrays (`Vector3Int[]`) for each species. No runtime geometry math during placement — decoration is pure lookup + write.

| Species | Trunk | Canopy | Blocks |
|---|---|---|---|
| **Oak** | 1×1, 5–7 blocks tall (hash-varied) | Sphere ~3–4 radius, centered 1 below top | Log + Leaves |
| **Birch** | 1×1, 7–9 blocks tall | Small oval ~2 radius at top | Log + Leaves |
| **Pine** | 1×1, 8–12 blocks tall | Stacked shrinking rings (cone), widest at bottom | Log + PineNeedles |
| **Dead** | 1×1, 3–5 blocks tall | A few stub log branches, no leaves | Log only |

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
| Mountains | Pine | 0.02 | MaxAltitude = snow line |
| All others | None | — | |

`TreeConfig[]` is serialized on `VoxelWorld` alongside `BiomeDef[]`, one entry per biome, tunable from the Inspector.

---

## 4. Decoration Pass Architecture (LOD 0)

### New files
- `ChunkDecorator.cs` — static class, all tree placement logic

### VoxelWorld additions
- `HashSet<Vector3Int> _decorationReady` — terrain done, waiting for neighbors
- `HashSet<Vector3Int> _decorationDone` — decorated, eligible for meshing
- `TreeConfig[] v2TreeConfigs` — serialized field

### Chunk state machine
```
TerrainReady → AwaitingDecoration → DecorationDone → MeshScheduled
```

Meshes are only scheduled for chunks in `_decorationDone`. This is the only gating change to `ProcessCompletedPipelines`.

### Per-frame decoration loop (in `ProcessCompletedPipelines`)
1. Terrain job completes → voxels copied to `_chunks[coord]` → coord added to `_decorationReady`
2. For each coord in `_decorationReady`: check all 8 horizontal neighbors exist in `_chunks`
3. If yes → `ChunkDecorator.Decorate(coord, ...)` → move to `_decorationDone`
4. Schedule mesh job only for chunks in `_decorationDone`

### `ChunkDecorator.Decorate()`
```
For each of 9 chunks in 3×3 grid centered on coord:
  For each of 256 columns (16×16):
    Compute worldX, worldZ
    hash     = Hash(worldX, worldZ)
    biome    = GetDominantBiome(worldX, worldZ, settings, biomes)  // noise recompute
    config   = treeConfigs[biome]
    if !SpawnCheck(hash, config.Density) → skip
    surfaceY = ComputeSurface(worldX, worldZ, settings)             // noise recompute
    if surfaceY < config.MinAltitude || surfaceY > config.MaxAltitude → skip
    species  = ResolveSpecies(config, hash)
    height   = ResolveHeight(species, hash)
    PlaceTree(worldX, surfaceY, worldZ, species, height, _chunks)

PlaceTree():
  For each (offset, blockType) in TreeShapes[species][height]:
    targetWorld = (worldX + offset.x, surfaceY + offset.y, worldZ + offset.z)
    targetChunk = WorldToChunkCoord(targetWorld)
    if _chunks.ContainsKey(targetChunk):
      _chunks[targetChunk].SetBlock(targetWorld, blockType)
```

Cross-chunk canopy writes are safe because the 3×3 neighbor gate guarantees all 9 chunks are in `_chunks` and not yet meshed.

---

## 5. LOD Tree Stubs (LOD 1+ Regions)

Tree stubs are generated inside `GenerateChunksBatchJobV2` as a Burst-compatible post-pass after normal block assignment. No decoration gate, no cross-chunk writes, no neighbor dependency.

**Per-column stub logic:**
- Same hash + density check as CPU decorator (identical `Hash()` function → same spawn positions)
- Same `ComputeSurface()` call (already computed for this column)
- Place Log blocks from `surfaceY` to `surfaceY + stubHeight` (stubHeight = half real height)
- Place one flat ring of Leaves/PineNeedles at `surfaceY + stubHeight + 1`
- Clamp to chunk bounds — no overflow

**Result:** At LOD 1+ distances, forests appear as small colored columns above the terrain silhouette. Spawn positions are identical to LOD 0 (same hash), so the LOD transition boundary is seamless.

---

## Files Changed / Created

| File | Change |
|---|---|
| `BlockType.cs` | Add Log (11), Leaves (12), PineNeedles (13) |
| `TreeShapes.cs` | **New** — pre-baked offset arrays per species/height |
| `TreeConfig.cs` | **New** — struct + `TreeSpecies` enum + defaults |
| `ChunkDecorator.cs` | **New** — static decoration logic |
| `VoxelWorld.cs` | Add decoration state sets, `TreeConfig[]` field, gate mesh scheduling on decoration |
| `GenerateChunksBatchJobV2.cs` | Add Burst tree stub pass |
| `BuildChunkMeshJob.cs` | Face-dependent UV for Log; add Leaves/PineNeedles to transparent pass |
| `VoxelWorldEditor.cs` | Expose `TreeConfig[]` in Inspector |

---

## Out of Scope

- Animated leaves (wind shader)
- Leaf particle effects
- Tree colliders
- Fallen logs or roots
- Saving/loading player-modified trees (handled by existing chunk save system)
