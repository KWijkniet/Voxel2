# Voxel2 — Project Knowledge

## Tech Stack
- **Engine**: Unity, URP pipeline
- **Language**: C#
- **Scripts**: `Assets/Voxel/Scripts/` (all gameplay/world code)
- **Editor scripts**: `Assets/Voxel/Editor/` (`VoxelWorldEditor.cs`, `VoxelMaterialGenerator.cs`)
- **Job system**: Unity Burst (`Unity.Jobs`, `Unity.Burst`, `Unity.Mathematics`, `Unity.Collections`)
- **Threading**: `System.Threading.Tasks.Task`, `SemaphoreSlim`, `ConcurrentQueue`

---

## Architecture Overview

### Two-Tier LOD World

```
LOD 0  (within viewDistance)   → individual 16³ chunk GameObjects, full-detail mesh
LOD 1+ (beyond viewDistance)   → 4×vc×4 chunk Regions, one GO per region, downsampled mesh
```

Example: `viewDistance=8`, `lodLevels=2` → radii 8 → 16 → 32 chunks = up to ~512 m.

`AlignedViewDistance` rounds `viewDistance` up to the nearest `RegionData.HSize` (4) multiple so the LOD 0 / region boundary always falls on a region edge (prevents mesh overlap).

### Chunk Coordinates
- Chunk coord `(cx, cy, cz)` = integer grid, one unit = one 16-voxel chunk
- World position = `ChunkToWorldPos(coord)` = coord × 16
- Region coord = `ChunkToRegionCoord(chunkCoord)` = `floor(chunkCoord / HSize)`
- Only X and Z wrap around the player; Y is always `0..verticalChunks-1`

---

### Frustum culling
`UpdateFrustumVisibility` calls `GeometryUtility.TestPlanesAABB` for every renderer every frame that content changes. `frustumBypassRadius` is a close-range sphere that always stays visible regardless of frustum (prevents pop-in of chunks around the player).

---

## Adding a New Block Type
1. Add constant to `BlockType.cs` and increment `AtlasTileCount` if it needs a texture tile.
2. Add the block to `SeedPalette(...)` calls in `VoxelWorld.BuildPaletteChunkFromBlocks` and `GenerateChunkData`.
3. Add generation logic in `TerrainGenerator`.
4. Add the tile to the texture atlas material.
