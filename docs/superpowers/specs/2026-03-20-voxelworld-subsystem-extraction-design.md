# VoxelWorld Subsystem Extraction — Design Spec

**Date:** 2026-03-20
**Status:** Approved

---

## Problem

`VoxelWorld.cs` has grown to 1,504 lines and handles too many concerns in a single file:
Inspector serialisation, world streaming, LOD0 burst-job pipeline, V2 decoration gate,
LOD1+ region tasks, mesh rendering, coordinate math, and terrain settings assembly.
This makes the file hard to read, navigate, and extend.

---

## Approach

Extract behaviour from `VoxelWorld` into **5 focused plain C# classes** (not MonoBehaviours).
`VoxelWorld` retains all shared state (dictionaries, sets) and passes references to subsystems
at construction time (Option C). No existing files outside `VoxelWorld.cs` are modified.

---

## File Structure

| File | Est. lines | Responsibility |
|---|---|---|
| `VoxelWorld.cs` | ~300 | Coordinator: shared state, Inspector fields, Unity lifecycle, subsystem wiring |
| `VoxelCoords.cs` | ~60 | Static coordinate utilities + `Vector3Ext` |
| `ChunkStreamer.cs` | ~300 | Desired-set building, eviction, load decisions |
| `ChunkPipelineProcessor.cs` | ~500 | LOD0 burst pipeline + V2 decoration gate |
| `RegionManager.cs` | ~250 | LOD1+ region data, `Task.Run` mesh builds, semaphore, cancellation |
| `MeshRenderer.cs` | ~150 | Draw lists, `Graphics.DrawMesh`, stale-mesh eviction |

---

## Shared State Ownership

`VoxelWorld` owns all shared collections and scalar state, and passes references into subsystems.

```
// Chunk data
Dictionary<Vector3Int, VoxelChunk>  _chunks
Dictionary<Vector3Int, Mesh>        _chunkMeshes, _transChunkMeshes
Dictionary<Vector3Int, Mesh>        _staleChunkMeshes

// Region data
Dictionary<Vector3Int, RegionData>  _regions
Dictionary<Vector3Int, Mesh>        _regionMeshes, _transRegionMeshes
Dictionary<Vector3Int, Mesh>        _staleRegionMeshes

// Coordination sets
HashSet<Vector3Int>  _inFlight, _desiredCoords, _desiredRegions, _pendingCoords
HashSet<Vector3Int>  _awaitingDecoration, _needsMeshAfterDecoration

// Shared positional state — used by ChunkStreamer (writes), RegionManager, MeshRenderer (read)
// Kept in VoxelWorld and exposed as public LastPlayerChunk so all subsystems can access it
// via their world reference without circular dependencies.
Vector3Int _lastPlayerChunk   (public property LastPlayerChunk)

// Frame flags — wrapped in WorldFlags to avoid ref-field restrictions
WorldFlags _flags  { DrawListDirty, NeedsMoreRequests }

// V2 NativeArrays (Persistent, allocated in Start, disposed in OnDestroy after all subsystems torn down)
NativeArray<float2>     _v2CSpline, _v2ESpline
NativeArray<BiomeDef>   _v2Biomes
NativeArray<TreeConfig>  _v2TreeConfigs
```

### `WorldFlags` wrapper

`ref bool` cannot be stored as a field in C#. A single shared object is passed to all subsystems
that need to read or write the two frame-level flags:

```csharp
internal sealed class WorldFlags
{
    public bool DrawListDirty;
    public bool NeedsMoreRequests;
}
```

---

## Subsystem Wiring

Subsystems are constructed in `VoxelWorld.Start`. Two subsystems need back-references set
after construction to avoid circular constructor arguments:

```csharp
_streamer.Pipeline = _pipeline;
_streamer.Regions  = _regionMgr;
```

The `onChunkReady` callback wires pipeline → region without direct coupling:

```csharp
_pipeline = new ChunkPipelineProcessor(..., onChunkReady: _regionMgr.TryFeedChunkIntoRegion);
```

`onChunkReady` is called from **both** `ProcessCompletedPipelines` and `TryDecorateReady`
wherever the code currently calls `TryFeedChunkIntoRegion`.

---

## `VoxelWorld` (coordinator)

### Retains
- All `[Header]` Inspector fields (terrain V1 + V2, rendering, performance, LOD)
- `AlignedViewDistance`, `MaxRadius` properties
- `public Vector3Int LastPlayerChunk` — written by `ChunkStreamer`, read by all subsystems
- `GetTerrainSettings()`, `GetTerrainSettingsV2()`
- `TryGetChunk()`, `GetRegion()` — public API; `GetRegion` forwards to `_regionMgr.GetRegion()`
- `OnValidate()`, `Reset()`, `OnGUI()`
- `AllocateV2NativeArrays()`

### `Update` shape after extraction
```csharp
private void Update()
{
    bool lodChanged = _streamer.CheckLodSettingsChanged();
    bool posChanged = _streamer.CheckPositionChanged();
    bool drained    = _regionMgr.DrainCancelledRegions();

    if (posChanged || lodChanged)
        HandleWorldMoved(posChanged);

    bool pipelinesApplied = _pipeline.ProcessCompletedPipelines();
    bool regionsApplied   = _regionMgr.ApplyReadyRegions();

    if ((pipelinesApplied || regionsApplied || drained) && _flags.NeedsMoreRequests)
        _streamer.UpdateLoadedChunks(false);

    _renderer.DrawAllMeshes();
}
```

### `HandleWorldMoved` (new private helper)
Consolidates the position-change response currently inlined in `Update`:
1. `_regionMgr.ResetCancellation()` — cancel + recreate CTS for new player position
2. Discard all in-flight pipelines (loop `_pipeline.MarkAllDiscarded()`)
3. `_pipeline.ClearDecorationState()`
4. If `posChanged`: `TerrainGenerator.ClearSurfaceCache()`
5. `_renderer.EvictStaleMeshes()`
6. `_streamer.UpdateLoadedChunks(true)`

### `OnGUI`
Reads counts via subsystem properties:
```csharp
$"Pipelines: {_pipeline.PipelineCount}\n"
$"Pending:   {_streamer.PendingCount}\n"
```
`ChunkPipelineProcessor` exposes `int PipelineCount => _pipelines.Count`.
`ChunkStreamer` exposes `int PendingCount => _pendingCoords.Count`.

### `OnDestroy` — required teardown order
Order matters: Burst jobs must complete before NativeArrays they read are disposed.
```
1. _regionMgr.CancelCts()                  // cancel Task.Run tasks; does NOT recreate CTS
2. _pipeline.CompleteAndDisposeAll()        // complete all Burst jobs, dispose NativeArrays
3. foreach chunk in _chunks: chunk.Dispose()
4. _chunks.Clear()
5. Destroy all stale/active meshes
6. _regionMgr.CompleteAndDispose()         // dispose _regionSemaphore + _regionCts
7. _v2CSpline.Dispose(); _v2ESpline.Dispose(); _v2Biomes.Dispose(); _v2TreeConfigs.Dispose()
```
V2 NativeArrays are disposed **last** because in-flight Burst jobs may still be reading them
until step 2 completes.

`CancelCts()` is a separate method from `ResetCancellation()`. `ResetCancellation()` cancels
and recreates the CTS (called on player move). `CancelCts()` only cancels — used in OnDestroy
where the old CTS will be disposed in step 6 and no new one is needed.

---

## `VoxelCoords` (static)

Extracted from the private helpers at the bottom of `VoxelWorld`. All call sites update to
`VoxelCoords.*`. `Vector3Ext` moves to the same file.

```csharp
public static class VoxelCoords
{
    public static Vector3Int WorldToChunkCoord(Vector3 p)
    public static Vector3Int ChunkToRegionCoord(Vector3Int chunk, int lodLevel)
    public static Vector3Int RegionBaseChunkCoord(Vector3Int regionCoord)
    public static Vector3    ChunkToWorldPos(Vector3Int c)
    public static Vector3    RegionWorldPos(Vector3Int regionCoord)
    public static Vector3    ChunkCenterWorld(Vector3Int c)
    public static Vector3    RegionCenterWorld(Vector3Int regionCoord)
}
```

---

## `ChunkStreamer`

### Constructor parameters
- `VoxelWorld world` — settings: `viewDistance`, `verticalChunks`, `lodLevels`,
  `AlignedViewDistance`, `player`, `maxPipelinesInFlight`, `maxRequestsPerUpdate`
- `WorldFlags flags` — writes `NeedsMoreRequests`
- `HashSet<Vector3Int> desiredCoords, desiredRegions, pendingCoords, inFlight`
- `Dictionary<Vector3Int, VoxelChunk> chunks`
- `Dictionary<Vector3Int, RegionData> regions` — for EvictChunkData (removal) and
  GatherAndSubmitRegions (TryGetValue / new RegionData allocation)
- `Dictionary<Vector3Int, Mesh> chunkMeshes, regionMeshes`
- `Action<Vector3Int> unloadChunkMesh, unloadRegionMesh` — callbacks into `MeshRenderer`

### Setter-injected after construction
- `ChunkPipelineProcessor Pipeline`
- `RegionManager Regions`

### Owns
- Writes `world.LastPlayerChunk` (via public setter)
- `_lastLodLevels`, `_lastViewDistance`
- Scratch lists: `_scratchUnloadC/R`, `_scratchEvictC/R`, `_scratchChunkSort`,
  `_scratchRegions`, `_scratchRequestC`, `_scratchDataCoords`

### Exposed properties
- `int PendingCount => _pendingCoords.Count`

### Methods
```
CheckLodSettingsChanged() → bool
CheckPositionChanged()    → bool
UpdateLoadedChunks(bool positionChanged)
  └─ BuildDesiredSets()
  └─ UnloadOutOfRangeMeshes()    — calls unloadChunkMesh / unloadRegionMesh callbacks
  └─ EvictChunkData()            — disposes VoxelChunk NativeArrays; removes from _chunks / _regions
  └─ RebuildPendingCoords()      — rebuilds _pendingCoords after position change
  └─ GatherAndSubmitLOD0()       — sorts _pendingCoords, calls Pipeline.SubmitChunkBatch()
  └─ GatherAndSubmitRegions()    — checks Regions.IsRegionInFlight(r) to skip in-progress builds;
                                   calls Regions.RequestRegionMesh() for complete regions
```

---

## `ChunkPipelineProcessor`

### Constructor parameters
- `VoxelWorld world` — settings: `terrainBatchSize`, `maxApplyPerFrame`, `maxDecorationsPerFrame`,
  `useV2Generator`, `AlignedViewDistance`, `lodLevels`, `verticalChunks`
- `NativeArray<float2> v2CSpline, v2ESpline`
- `NativeArray<BiomeDef> v2Biomes`
- `NativeArray<TreeConfig> v2TreeConfigs`
- `Dictionary<Vector3Int, VoxelChunk> chunks`
- `Dictionary<Vector3Int, Mesh> chunkMeshes, transChunkMeshes`
- `Dictionary<Vector3Int, Mesh> staleChunkMeshes, staleRegionMeshes`
- `HashSet<Vector3Int> inFlight, desiredCoords, pendingCoords`
- `WorldFlags flags` — writes `DrawListDirty`, `NeedsMoreRequests`
- `Action<Vector3Int, VoxelChunk> onChunkReady` — replaces all `TryFeedChunkIntoRegion` calls;
  called from **both** `ProcessCompletedPipelines` and `TryDecorateReady`

### Owns (moved from VoxelWorld)
- `List<ChunkPipeline> _pipelines`
- `HashSet<Vector3Int> _awaitingDecoration, _needsMeshAfterDecoration`
- `BatchBuffer` inner class
- `ChunkPipeline` struct
- Scratch lists: `_scratchCompleted`, `_scratchRemove`

### Exposed properties
- `int PipelineCount => _pipelines.Count`

### Methods
```
SubmitChunkBatch(List<Vector3Int> coords, bool buildMesh)
SubmitSubBatch(...)
ProcessCompletedPipelines()           → bool
  └─ calls onChunkReady(coord, chunk) instead of TryFeedChunkIntoRegion
TryDecorateReady()
  └─ calls onChunkReady(coord, chunk) instead of TryFeedChunkIntoRegion
SubmitMeshOnly(Vector3Int coord)
SnapshotNeighbours(Vector3Int coord, out int loadedMask) → NativeArray<byte>[]
MarkAllDiscarded()                    — sets Discarded=true on all in-flight pipelines
ClearDecorationState()                — for each coord in _awaitingDecoration, removes it from
                                        _inFlight; then clears _awaitingDecoration +
                                        _needsMeshAfterDecoration
CompleteAndDisposeAll()               — completes all Burst jobs, disposes all NativeArrays;
                                        must be called BEFORE V2 NativeArray disposal in OnDestroy
CreateMeshFromLists(ref ChunkPipeline, bool transparent) → Mesh   [static]
DisposeMeshLists(ref ChunkPipeline)                               [static]
```

---

## `RegionManager`

### Constructor parameters
- `VoxelWorld world` — settings: `verticalChunks`, `lodLevels`, `maxApplyPerFrame`,
  `maxRegionTasks`; reads `world.LastPlayerChunk` and `world.AlignedViewDistance`
  in `TryFeedChunkIntoRegion` to determine which LOD ring a chunk belongs to
- `Dictionary<Vector3Int, VoxelChunk> chunks`
- `Dictionary<Vector3Int, RegionData> regions`
- `Dictionary<Vector3Int, Mesh> regionMeshes, transRegionMeshes`
- `Dictionary<Vector3Int, Mesh> staleRegionMeshes, staleChunkMeshes`
- `HashSet<Vector3Int> desiredRegions`
- `WorldFlags flags` — writes `DrawListDirty`

### Owns (moved from VoxelWorld)
- `HashSet<Vector3Int> _regionInFlight`
- `ConcurrentQueue<RegionBuildResult> _regionReadyQueue`
- `ConcurrentQueue<Vector3Int> _cancelledRegions`
- `SemaphoreSlim _regionSemaphore`
- `CancellationTokenSource _regionCts`
- `RegionBuildResult` struct

### Note on settings access
All settings accessed from `world` are read at call-time via the reference — never cached at
construction — so Inspector changes at runtime are always reflected correctly.

### Methods
```
TryFeedChunkIntoRegion(Vector3Int coord, VoxelChunk chunk)
  — reads world.LastPlayerChunk + world.AlignedViewDistance to determine LOD ring
RequestRegionMesh(Vector3Int regionCoord, RegionData region)
ApplyReadyRegions()          → bool
DrainCancelledRegions()      → bool
IsRegionInFlight(Vector3Int regionCoord) → bool   — called by ChunkStreamer.GatherAndSubmitRegions
ResetCancellation()          — cancel old CTS, dispose it, allocate new CTS; called on player move
CancelCts()                  — cancel old CTS only (no recreate); called from VoxelWorld.OnDestroy
GetRegion(Vector3Int coord)  → RegionData
UnloadRegionMesh(Vector3Int regionCoord)
EvictStaleRegionMeshes()     — called by MeshRenderer.EvictStaleMeshes; calls Destroy on main thread
CompleteAndDispose()         — disposes _regionSemaphore + _regionCts; called after CompleteAndDisposeAll
```

### Threading note
`EvictStaleRegionMeshes` and `ApplyReadyRegions` both call `Destroy()` and must only be called
from the main thread. The `Task.Run` lambda in `RequestRegionMesh` only enqueues to
`_regionReadyQueue` and `_cancelledRegions` (both `ConcurrentQueue`) — it never touches
Unity objects or the main-thread dictionaries.

---

## `MeshRenderer`

### Constructor parameters
- `VoxelWorld world` — `chunkMaterial`, `transparentMaterial`, `AlignedViewDistance`,
  `lodLevels`, `transform`, `gameObject.layer`
- `Dictionary<Vector3Int, Mesh> chunkMeshes, transChunkMeshes, regionMeshes, transRegionMeshes`
- `Dictionary<Vector3Int, Mesh> staleChunkMeshes, staleRegionMeshes`
- `WorldFlags flags` — reads and resets `DrawListDirty`
- `RegionManager regionMgr` — `EvictStaleMeshes` delegates region eviction to
  `regionMgr.EvictStaleRegionMeshes()`

### Owns
- `List<(Mesh, Matrix4x4)> _chunkDrawList, _transChunkDrawList, _regionDrawList, _transRegionDrawList`
- `Matrix4x4 _cachedL2W`
- Own scratch lists for eviction loops (separate from ChunkStreamer scratch lists):
  `_scratchStaleChunks`, `_scratchStaleRegions`

### Methods
```
DrawAllMeshes()
EvictStaleMeshes()
  └─ evicts _staleChunkMeshes by distance (uses _scratchStaleChunks)
  └─ calls regionMgr.EvictStaleRegionMeshes() for region stales
UnloadChunkMesh(Vector3Int coord)    — registered as callback in ChunkStreamer; main thread only
UnloadRegionMesh(Vector3Int coord)   — registered as callback in ChunkStreamer; main thread only
```

---

## Threading Safety

No changes to threading model. All rules from the existing code carry over:

- All shared dictionaries and sets are touched only on the main thread
- `ConcurrentQueue` used for cross-thread region results and cancellations (unchanged)
- `[ThreadStatic]` buffers in `MeshBuilder` are unchanged
- `NativeArray` lifetimes managed identically — no new allocations introduced
- `Destroy()` is only called from main-thread code paths (`MeshRenderer`, `RegionManager.ApplyReadyRegions`)
- Settings read from `world` at call-time (not cached) to avoid stale Inspector values at runtime

---

## Out of Scope

- `ChunkRenderer.cs` (`MeshBuilder` + `WritableMeshData`) — stays as-is
- `TerrainGenerator.cs`, `TerrainGeneratorV2.cs`, `ChunkDecorator.cs` — unchanged
- All other scripts — unchanged
- No behaviour changes; this is a pure structural refactor

---

## Success Criteria

- `VoxelWorld.cs` under 350 lines
- Each new file has a single clear responsibility
- All existing editor and runtime behaviour identical
- No new allocations or threading model changes
- Unity Editor compiles without errors or warnings
