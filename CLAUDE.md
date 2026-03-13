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

## Scripts

### `BlockType.cs`
Static constants: `Air=0`, `Stone=1`, `Dirt=2`, `Grass=3`, `Sand=4`, `Water=5`, `Snow=6`.
`AtlasTileCount = 6` — texture atlas has 6 horizontal tiles (Air not included).
UV calc: `tileU = (blockType - 1 + 0.5f) / AtlasTileCount`

### `PaletteChunk.cs`
16³ voxel chunk with **palette compression**. Bit-packed `uint[]` array; width grows automatically:
- 1–2 palette entries → 1 bit/voxel (512 B)
- 3–4 → 2 bits/voxel (1 KB)
- 5–16 → 4 bits/voxel (2 KB)
- 17–256 → 8 bits/voxel (4 KB)

Key methods:
- `GetBlock(x,y,z)` / `SetBlock(x,y,z,type)` — normal access with bounds check
- `IsSolid(x,y,z)` — bounds-checked solid test
- `FillAll(type)` — O(n/32) uniform fill
- `SeedPalette(params byte[])` — **call before generation** to pre-register all terrain block types and widen `_data` once. Avoids incremental `GrowIfNeeded` repacks mid-fill (each repack = 4096 reads + 4096 writes).
- `IsSolidUnchecked(x,y,z)` — no bounds check; used by greedy mesher inner loop where coords are guaranteed in-range

Voxel index layout: `x + y*16 + z*256` (Y-major within the Z-slice).

### `RegionData.cs`
Groups `HSize×verticalChunks×HSize` (4×vc×4) `PaletteChunk`s into one voxel-addressable block.
- `VoxelSizeX/Y/Z` = 64 × (vc×16) × 64
- `IsComplete` — true once all chunks are set via `SetChunk`
- `GetBlock(x,y,z)` — bounds-checked, delegates to the correct child chunk
- `GetBlockUnchecked(x,y,z)` / `IsSolidUnchecked(x,y,z)` — skip the 6-comparison bounds check; used in greedy mesher hot path where coords are guaranteed in-bounds

### `TerrainGenerator.cs`
**Pure static, thread-safe** terrain math. Used both in Burst jobs and managed fallback.

Pipeline per column (worldX, worldZ):
1. **Domain warp** — offset coords with a separate noise pass to break regularity
2. **FBM** — multi-octave Perlin → normalised `[0,1]`
3. **Biome noise** — separate low-frequency noise → `[0,1]`
4. **Biome blend** — lerp between ocean / plains / hills / mountain height curves
5. **Block select** — surface / depth / altitude → block type (Grass, Dirt, Stone, Sand, Water, Snow)

**Surface height cache** (`ConcurrentDictionary<long, int>`): avoids recomputing the expensive FBM+warp pipeline for the same `(worldX, worldZ)` across adjacent chunks loading concurrently. Key = `worldX << 32 | (uint)worldZ`. Cache is cleared on player position change (`ClearSurfaceCache()`).

**`GenerateChunkJob : IJob`** — Burst-compiled job. Receives `TerrainSettings` by value and writes into a `NativeArray<byte>` (4096 bytes). Scheduled from `VoxelWorld.RequestChunk`, completed + drained in `ProcessCompletedGenerationJobs`.

### `ChunkRenderer.cs`
Greedy mesher + mesh upload. Attached to chunk/region GameObjects.

**`MeshData`** (class, `IDisposable`) — wraps a `Mesh.MeshDataArray` produced on a background thread. Call `Dispose()` if it will never be applied (cancelled task). `ApplyAndDisposeWritableMeshData` disposes the array automatically; do not call `Dispose()` after applying.

**Two rendering paths:**
- **Sync** (`Render`) — editor/main-thread only. Uses static `List<T>` buffers, calls `UploadMesh(List<>...)`.
- **Async** (`BuildMeshData` / `BuildRegionMeshData`) — safe to call from any thread. Uses `[ThreadStatic]` `List<T>` buffers. Calls `BuildMeshDataFromBuffers` which allocates a `Mesh.MeshDataArray` (via `Mesh.AllocateWritableMeshData`) on the background thread and packs all vertex/index data into it.

**`ApplyMeshData`** (main thread only) — calls `Mesh.ApplyAndDisposeWritableMeshData` (near-zero-cost pointer hand-off — no data copying on the main thread), then sets `mesh.bounds` from the pre-computed bounds.
`MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices` skips O(vertices) bounds recalculation and triangle validation.

**Greedy mesher**: standard greedy algorithm over 6 face directions. `step = 2^lodLevel` — at LOD 1 each cell covers 2 voxels, etc. Uses `stackalloc int[3]` for `pos` and `sizes` to avoid heap allocation per face.

Vertex buffer layout (3 separate streams):
- Stream 0: `Vector3` position (12 B)
- Stream 1: `Vector3` normal (12 B)
- Stream 2: `Vector2` uv (8 B)

Chunk mesh bounds: always `center=(8,8,8), size=(16,16,16)` — hardcoded, no RecalculateBounds.
Region mesh bounds: `center=(sx/2, sy/2, sz/2), size=(sx, sy, sz)` where `sx/sy/sz = region.VoxelSizeX/Y/Z`.

### `VoxelWorld.cs` — Main World Manager

#### Inspector Parameters
| Header | Field | Default | Notes |
|---|---|---|---|
| World | `viewDistance` | 8 | LOD 0 radius in chunks |
| World | `verticalChunks` | 3 | chunk stack height |
| Async | `maxConcurrentTasks` | 4 | semaphore limit for mesh-build tasks |
| Async | `maxApplyPerFrame` | 6 | max chunks/regions applied from ready queues per frame |
| Async | `maxRequestsPerUpdate` | 16 | max new task submissions per `UpdateLoadedChunks` call |
| LOD | `lodLevels` | 2 | 0=no regions, 1=one ring, 2=two rings, 3=three rings |

#### Key Collections
| Field | Type | Purpose |
|---|---|---|
| `_chunks` | `Dictionary<Vector3Int, PaletteChunk>` | all loaded chunk data |
| `_renderers` | `Dictionary<Vector3Int, GameObject>` | LOD 0 active GameObjects |
| `_desiredCoords` | `HashSet<Vector3Int>` | set of chunk coords that should exist |
| `_inFlight` | `HashSet<Vector3Int>` | coords currently being generated/meshed |
| `_readyQueue` | `ConcurrentQueue<ChunkBuildResult>` | background→main thread chunk results |
| `_pendingGenerationJobs` | `List<PendingGenerationJob>` | scheduled Burst jobs awaiting `Complete()` |
| `_regions` | `Dictionary<Vector3Int, RegionData>` | all loaded region data |
| `_regionRenderers` | `Dictionary<Vector3Int, GameObject>` | LOD 1+ active region GOs |
| `_cancelledCoords` | `ConcurrentQueue<Vector3Int>` | coords freed by cancelled tasks |

#### Update Loop
```
CheckLodSettingsChanged / CheckPositionChanged / CheckRotationChanged
 → if changed: UpdateLoadedChunks(positionChanged: true)
Drain _cancelledCoords → _inFlight.Remove
ProcessCompletedGenerationJobs()         ← drains completed Burst jobs
ApplyReadyChunks()                       ← applies up to maxApplyPerFrame results
ApplyReadyRegions()                      ← same for regions
 → if (chunksApplied || anyDrained) && _needsMoreRequests:
       UpdateLoadedChunks(false)         ← submit next batch of requests
UpdateFrustumVisibility()                ← TestPlanesAABB per renderer
```

#### `ProcessCompletedGenerationJobs` — Critical Details
- Iterates `_pendingGenerationJobs` **backward** (safe for `RemoveAt`).
- **Discarded jobs** (player moved) are always drained immediately — just `Dispose()` + `_cancelledCoords.Enqueue`. **Do not apply the `maxApplyPerFrame` cap to discarded jobs.** If you do, coords stay in `_inFlight` for many frames after a position change, blocking new desired coords from being requested.
- **Non-discarded jobs** are capped at `maxApplyPerFrame` per frame. Each:
  1. `Handle.Complete()` (finalises Burst job, required even when `IsCompleted`)
  2. `NativeArray<byte>.ToArray()` — 4096-byte managed copy on main thread (fast)
  3. `NativeArray.Dispose()` — frees unmanaged memory immediately
  4. `Task.Run` — moves `BuildPaletteChunkFromBlocks` + mesh build off main thread
- `_inFlight.Remove` is **deferred** to `ApplyReadyChunks`. The coord stays in `_inFlight` while the background task runs, preventing duplicate generation requests.
- `NativeArray` allocator is `Allocator.Persistent` (not `TempJob`) because jobs can sit in `_pendingGenerationJobs` longer than 4 frames when the budget is capped.

#### `BuildPaletteChunkFromBlocks(byte[])` — Background Thread
Called inside `Task.Run`. Creates a new `PaletteChunk`, calls `SeedPalette(Stone, Dirt, Grass, Sand, Water, Snow)` to pre-register all types, then loops through 4096 bytes with `SetBlock`. Thread-safe — no shared state.

---

## Design Decisions & Gotchas

### Why `SeedPalette` before generation
`GrowIfNeeded` repacks all 4096 voxels each time the palette crosses a bit-width boundary (1→2→4→8 bits). With 7 block types, that's 3 repacks × 4096 voxels = ~24 K operations. `SeedPalette` widens the bit array once up-front — safe because all voxels are still Air (index 0 = all-zero bits at every width).

### Why `Mesh.AllocateWritableMeshData` instead of `Mesh.SetVertices`
`SetVertices` / `SetNormals` / `SetUVs` / `SetTriangles` / `RecalculateBounds` all run on the main thread and are O(vertices). For 6 chunks + 6 regions per frame this was ~80 ms. `AllocateWritableMeshData` can be called from any thread; `ApplyAndDisposeWritableMeshData` is a near-zero-cost pointer hand-off on the main thread.

### Why `Allocator.Persistent` for Burst job `NativeArray`
`TempJob` is only valid for 4 frames. With `maxApplyPerFrame` capping how quickly completed jobs are drained, allocations can live longer than 4 frames during heavy load.

### Thread safety contract for `PaletteChunk`
- Not thread-safe for concurrent writes.
- Multiple background threads may read the **same** `PaletteChunk` concurrently (neighbour fetching for mesh build) — safe because no writes happen to existing chunks in `_chunks`.
- A new `PaletteChunk` is always built on a single background thread before being handed off.

### `_inFlight` is `HashSet` (not thread-safe)
Only ever touched on the main thread: `Add` in `RequestChunk`, `Remove` in `ApplyReadyChunks` and the `_cancelledCoords` drain loop. Never accessed from background tasks.

### Frustum culling
`UpdateFrustumVisibility` calls `GeometryUtility.TestPlanesAABB` for every renderer every frame that content changes. `frustumBypassRadius` is a close-range sphere that always stays visible regardless of frustum (prevents pop-in of chunks around the player).

### Region boundary alignment
`AlignedViewDistance` rounds up to the nearest 4. This ensures individual chunk GOs and region meshes never share the same chunk coords — preventing Z-fighting or duplicate geometry at the LOD boundary.

---

## Adding a New Block Type
1. Add constant to `BlockType.cs` and increment `AtlasTileCount` if it needs a texture tile.
2. Add the block to `SeedPalette(...)` calls in `VoxelWorld.BuildPaletteChunkFromBlocks` and `GenerateChunkData`.
3. Add generation logic in `TerrainGenerator`.
4. Add the tile to the texture atlas material.
