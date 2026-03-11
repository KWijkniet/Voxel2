# Voxel2 Optimization Report

Generated: 2026-03-11

## Overview

Scanned all 8 scripts in `Assets/Voxel/Scripts/`. Issues are ordered by severity.

---

## Critical Issues

### 1. No Burst / Job System for Terrain Generation
**File:** `TerrainGenerator.cs`, `VoxelWorld.cs`

All terrain generation runs on managed `ThreadPool` threads via `Task.Run`. No `[BurstCompile]`, no `NativeArray`, no `IJob`. Burst + Unity Job System could give 3–10x speedup on the noise loops via SIMD vectorization and aggressive inlining.

**Fix:** Port `GenerateChunkData` to a `[BurstCompile] struct GenerateChunkJob : IJob`. Replace `Mathf.PerlinNoise` with `noise.cnoise` from `Unity.Mathematics` (Classic Perlin, normalised to [0,1]). Schedule jobs from the main thread; track `JobHandle`s; complete and read results in `Update()`. Mesh building stays on `Task.Run` (managed, uses `List<T>`).

**Impact:** Terrain generation is the dominant CPU cost per chunk. This is the highest-value fix.

---

### 2. `BuildMeshData()` Allocates 4 Lists on Every Call
**File:** `ChunkRenderer.cs` — `BuildMeshData()` (line 76–85), `BuildRegionMeshData()` (line 270–282)

```csharp
var verts = new List<Vector3>(); // NEW every call
var norms = new List<Vector3>(); // NEW every call
var uvs   = new List<Vector2>(); // NEW every call
var tris  = new List<int>();     // NEW every call
```

The sync path already uses `[ThreadStatic]`-style static buffers. The async path allocates fresh lists + `.ToArray()` copies on every call. With 4–8 concurrent mesh tasks this creates sustained GC pressure and can cause frame hitches.

**Fix:** Add `[ThreadStatic]` Lists and a `[ThreadStatic]` mask array. Null-check and initialise per-thread on first use. Reuse across calls on each worker thread.

---

### 3. O(MaxRadius²) Iteration in `UpdateLoadedChunks()`
**File:** `VoxelWorld.cs` — line 254–281

```csharp
for (int x = -maxR; x <= maxR; x++)   // e.g. -32 to +32
for (int z = -maxR; z <= maxR; z++)   // 4225 iterations @ lodLevels=2
{
    // Per-chunk: region coord lookup + RegionContainsLod0Chunk check
}
```

The outer ring is iterated chunk-by-chunk only to group chunks into region coordinates — a redundant level of indirection. `RegionContainsLod0Chunk` (Clamp + distance math) runs once per chunk offset in the outer zone.

**Before:** 4 225 iterations at `viewDistance=8, lodLevels=2`; 16 641 at `lodLevels=3`.
**After:** ~586 / ~1 450 iterations (~7–11× reduction).

**Fix:** Split the loop into two passes:
1. LOD 0 zone: iterate chunk offsets in `[-(vd-1), vd-1]` — no region logic needed.
2. LOD 1+ zone: iterate **region coords** directly in `[-maxRegionR, maxRegionR]`. Much fewer iterations; `RegionContainsLod0Chunk` still called per-region for boundary detection.

---

### 4. Surface Height Recomputed Across Neighbouring Chunks
**File:** `TerrainGenerator.cs` — `GetSurface()`

`GetSurface()` runs multi-octave FBM + domain warp (the most expensive per-column operation) with no caching. Adjacent chunks loading concurrently redundantly recompute the same `(worldX, worldZ)` surface heights at shared boundaries.

**Fix:** Add a `static ConcurrentDictionary<long, int>` keyed by packed `(worldX << 32 | worldZ)`. Cache only full-detail results (low-detail is cheaper and may differ). Expose `ClearSurfaceCache()` called from `VoxelWorld` when the player chunk changes.

---

## High Priority Issues

### 5. Region Mesh Tasks Ignore Cancellation Token
**File:** `VoxelWorld.cs` — `RequestRegionMesh()` (line 517)

```csharp
await _semaphore.WaitAsync(); // no cancellation token!
```

LOD 0 chunk tasks pass `_generationCts.Token` to `WaitAsync`. Region tasks do not. After a player teleport, stale region tasks continue blocking semaphore slots, delaying new high-priority tasks.

**Fix:** Pass `_generationCts.Token` to `_semaphore.WaitAsync(token)` (1 line change).

---

### 6. Sort with Redundant Distance Recalculation
**File:** `VoxelWorld.cs` — lines 317–319, 364–366

`ChunkCenterWorld(a)` and `ChunkCenterWorld(b)` are called on **both** sides of each comparison inside `List.Sort`. Each comparison computes 2–4 multiplications and a `sqrMagnitude`. With 50–100 pending chunks, this is hundreds of redundant calculations.

**Fix:** Pre-compute a `Dictionary<Vector3Int, float>` of `distSq` before sorting, then sort by lookup.

---

### 7. Temp `List` Allocations in Unload Loop
**File:** `VoxelWorld.cs` — lines 284–307

```csharp
var toUnloadC = new List<Vector3Int>(); // NEW every position change
var toUnloadR = new List<Vector3Int>(); // NEW every position change
var toEvictC  = new List<Vector3Int>(); // NEW every position change
var toEvictR  = new List<Vector3Int>(); // NEW every position change
```

Four lists created on every position change. Reuse persistent `List` fields, cleared each call.

---

### 8. `RegionContainsLod0Chunk()` Called Inside the Iteration Loop
**File:** `VoxelWorld.cs` — `UpdateLoadedChunks`, line 271

Called per-chunk offset in the outer zone (thousands of times). Each call does Clamp + Abs + Max + comparison. With the Critical #3 fix (iterating by region instead), this is called once per region, not once per chunk — automatically resolved.

---

## Medium Priority Issues

### 9. `PaletteChunk.GrowIfNeeded()` Repacks All 4096 Voxels Per New Block Type
**File:** `PaletteChunk.cs` — line 136

Each new block type encountered during generation triggers a full 4096-voxel read/rewrite. On typical terrain (stone, dirt, grass, sand, water, snow = 6 types + air = 3 bit-width transitions), this runs 2–3 times per chunk.

**Fix:** Pre-populate the palette with all known terrain block types before generation. Since the generator's type set is fixed, `_bitsPerIndex` can be set to 4 upfront (covering 16 types), bypassing all growth operations.

---

### 10. `RegionData.GetBlock()` Overhead in Greedy Mesher Inner Loop
**File:** `RegionData.cs` — `GetBlock()` (line 45)

Called up to 512k times per region mesh. Each call: 6 bounds checks + integer division + modulo + null-coalescing + managed method call. The null-coalescing `??` adds an extra branch.

**Fix:** Inline the hot path or provide a bounds-checked-once accessor. Consider checking bounds once per greedy slice instead of per-voxel.

---

## Minor Issues

### 11. `VoxelChunk.cs` Appears Abandoned
Uses old `byte[]` storage (no palette compression), has a simpler greedy mesher, and is not referenced anywhere in `VoxelWorld.cs`. Remove to avoid confusion.

### 12. `GreedyMeshFace()` Allocates `int[3]` Per Face
**File:** `ChunkRenderer.cs` — line 159
```csharp
var pos = new int[3]; // 6 allocations per chunk mesh build
```
Replace with three local `int` variables and index-based dispatch.

### 13. Magic Numbers
`0.5f` offsets for chunk centers, hard-coded `65535` index format threshold, and repeated `PaletteChunk.Size` arithmetic. Extract into named constants.

---

## Fix Priority & Impact Table

| # | Issue | Severity | Estimated Gain |
|---|-------|----------|---------------|
| 1 | Burst/Job System for generation | Critical | 3–10× chunk gen throughput |
| 2 | ThreadStatic mesh buffers | Critical | Eliminates GC pressure during streaming |
| 3 | O(n²) → O(region²) iteration | Critical | 7–11× fewer iterations per position change |
| 4 | Surface height cache | Critical | Eliminates cross-chunk noise duplication |
| 5 | Region task cancellation token | High | Frees semaphore slots on player movement |
| 6 | Pre-compute sort distances | High | Minor CPU on movement |
| 7 | Reuse unload lists | High | Minor GC reduction |
| 8 | RegionContainsLod0Chunk in loop | Medium | Auto-fixed by #3 |
| 9 | PaletteChunk pre-seeded palette | Medium | 2–3 fewer repacks per chunk |
| 10 | RegionData.GetBlock hot path | Medium | Region mesher speed |
| 11 | Remove VoxelChunk.cs | Low | Code clarity |
| 12 | int[3] per face | Low | Negligible |
| 13 | Magic numbers | Low | Maintainability |

---

## Required Unity Packages (for Critical Fix #1)

- `com.unity.burst` (≥ 1.8)
- `com.unity.collections` (≥ 1.4)
- `com.unity.jobs` (included with Collections)
- `com.unity.mathematics` (≥ 1.2)

These are standard packages available in Unity Package Manager and are likely already present if the project targets Unity 2022+.

> **Note on terrain appearance:** The Burst job replaces `Mathf.PerlinNoise` with `noise.cnoise` from `Unity.Mathematics` (also Classic Perlin noise, normalised to [0,1]). Terrain shape will be visually similar but not pixel-perfect identical to the original.
