# VoxelWorld Subsystem Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the 1,504-line `VoxelWorld.cs` monolith into a coordinator plus 5 focused plain C# classes without changing any runtime behaviour.

**Architecture:** `VoxelWorld` retains all shared state (dictionaries, sets, NativeArrays) and passes references to subsystem instances at construction time. Subsystems contain the behaviour; `VoxelWorld` is the wiring and Unity lifecycle. A `WorldFlags` wrapper object replaces `ref bool` fields that can't be stored as C# fields.

**Tech Stack:** Unity 2022+, URP, C#, Unity.Jobs / Burst, Unity.Collections (NativeArray/NativeList), System.Threading.Tasks, SemaphoreSlim.

---

> **Testing note:** This project has no NUnit test suite. Each task's verification step is:
> 1. Open the Unity Editor — confirm **zero compiler errors/warnings** in the Console.
> 2. Enter Play mode — confirm chunks load visually, player can move, LOD transitions work.
> 3. Check the OnGUI overlay (top-left) shows non-zero chunk counts.
> These three checks replace unit tests throughout this plan.

---

## File Map

| Status | Path | Role |
|---|---|---|
| **Modify** | `Assets/Voxel/Scripts/VoxelWorld.cs` | Shrinks from 1504 → ~300 lines; becomes coordinator |
| **Create** | `Assets/Voxel/Scripts/VoxelCoords.cs` | Static coord helpers + `Vector3Ext` |
| **Create** | `Assets/Voxel/Scripts/WorldFlags.cs` | Shared `DrawListDirty` + `NeedsMoreRequests` wrapper |
| **Create** | `Assets/Voxel/Scripts/VoxelMeshRenderer.cs` | Draw lists, `Graphics.DrawMesh`, stale-mesh eviction |
| **Create** | `Assets/Voxel/Scripts/RegionManager.cs` | LOD1+ region builds via `Task.Run`, semaphore, CTS |
| **Create** | `Assets/Voxel/Scripts/ChunkPipelineProcessor.cs` | LOD0 Burst pipeline + V2 decoration gate |
| **Create** | `Assets/Voxel/Scripts/ChunkStreamer.cs` | Desired-set building, eviction, load decisions |

All other files are **untouched**.

---

## Spec Reference

`docs/superpowers/specs/2026-03-20-voxelworld-subsystem-extraction-design.md`

---

## Task 1: Create `VoxelCoords.cs` — static coordinate helpers

The seven private static coordinate helpers at the bottom of `VoxelWorld.cs` (lines ~1449–1503)
plus the `Vector3Ext` extension class move into a new static class. This is the lowest-risk
first step — pure move, no logic change.

**Files:**
- Create: `Assets/Voxel/Scripts/VoxelCoords.cs`
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Create `VoxelCoords.cs`**

```csharp
using UnityEngine;

public static class VoxelCoords
{
    public static Vector3Int WorldToChunkCoord(Vector3 p)
    {
        int s = VoxelChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / s), 0, Mathf.FloorToInt(p.z / s));
    }

    public static Vector3Int ChunkToRegionCoord(Vector3Int chunk, int lodLevel)
    {
        int size = 1 << lodLevel;
        return new Vector3Int(
            Mathf.FloorToInt(chunk.x / (float)size),
            lodLevel,
            Mathf.FloorToInt(chunk.z / (float)size));
    }

    public static Vector3Int RegionBaseChunkCoord(Vector3Int regionCoord)
    {
        int size = 1 << regionCoord.y;
        return new Vector3Int(regionCoord.x * size, 0, regionCoord.z * size);
    }

    public static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    public static Vector3 RegionWorldPos(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s, 0, regionCoord.z * s);
    }

    public static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s + s * .5f, c.y * s + s * .5f, c.z * s + s * .5f);
    }

    public static Vector3 RegionCenterWorld(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s + s * .5f, 0, regionCoord.z * s + s * .5f);
    }
}

internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
```

- [ ] **Step 2: In `VoxelWorld.cs`, replace every call to the private helpers with `VoxelCoords.*`**

Find all usages of the private static helpers in `VoxelWorld.cs` and update them:
```
WorldToChunkCoord(...)        → VoxelCoords.WorldToChunkCoord(...)
ChunkToRegionCoord(...)       → VoxelCoords.ChunkToRegionCoord(...)
RegionBaseChunkCoord(...)     → VoxelCoords.RegionBaseChunkCoord(...)
ChunkToWorldPos(...)          → VoxelCoords.ChunkToWorldPos(...)
RegionWorldPos(...)           → VoxelCoords.RegionWorldPos(...)
ChunkCenterWorld(...)         → VoxelCoords.ChunkCenterWorld(...)
RegionCenterWorld(...)        → VoxelCoords.RegionCenterWorld(...)
```

- [ ] **Step 3: Delete the private static helpers and `Vector3Ext` from `VoxelWorld.cs`**

Remove lines ~1449–1503 (the seven private static methods and `Vector3Ext` at the bottom).

- [ ] **Step 4: Verify — compile + play**

Open Unity Editor. Console must show zero errors. Enter Play mode; confirm chunks load and OnGUI overlay shows chunk counts.

- [ ] **Step 5: Commit**

```bash
git add Assets/Voxel/Scripts/VoxelCoords.cs Assets/Voxel/Scripts/VoxelWorld.cs Assets/Voxel/Scripts/VoxelCoords.cs.meta
git commit -m "refactor: extract VoxelCoords static helpers from VoxelWorld"
```

---

## Task 2: Create `WorldFlags.cs`

A tiny shared wrapper that replaces the two `bool` fields that need to be mutated by multiple
subsystems (`_drawListDirty`, `_needsMoreRequests`). Using a reference-type object avoids the
C# restriction on storing `ref` as a field.

**Files:**
- Create: `Assets/Voxel/Scripts/WorldFlags.cs`

- [ ] **Step 1: Create `WorldFlags.cs`**

```csharp
/// <summary>
/// Shared mutable flags passed by reference to all subsystems that need to
/// read or write DrawListDirty or NeedsMoreRequests.
/// All access is on the main thread only.
/// </summary>
internal sealed class WorldFlags
{
    public bool DrawListDirty;
    public bool NeedsMoreRequests;
}
```

- [ ] **Step 2: In `VoxelWorld.cs`, replace the two bool fields with a `WorldFlags` instance**

```csharp
// Replace:
private bool _drawListDirty   = true;
// and:
private bool _needsMoreRequests;

// With:
private readonly WorldFlags _flags = new WorldFlags { DrawListDirty = true };
```

Update all read/write sites in `VoxelWorld.cs` (they'll move to subsystems later, but for now
update them so it compiles):
```
_drawListDirty      → _flags.DrawListDirty
_needsMoreRequests  → _flags.NeedsMoreRequests
```

- [ ] **Step 3: Verify — compile + play**

Zero console errors. Play mode: chunks load, OnGUI shows counts.

- [ ] **Step 4: Commit**

```bash
git add Assets/Voxel/Scripts/WorldFlags.cs Assets/Voxel/Scripts/WorldFlags.cs.meta Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: introduce WorldFlags wrapper for shared bool flags"
```

---

## Task 3: Extract `VoxelMeshRenderer`

Moves draw-list management, `Graphics.DrawMesh`, stale-mesh eviction, and
`UnloadChunkMesh`/`UnloadRegionMesh` out of `VoxelWorld`.

> **Naming note:** Unity has a built-in `MeshRenderer` component. Our class is named
> `VoxelMeshRenderer` to avoid shadowing it.

**Files:**
- Create: `Assets/Voxel/Scripts/VoxelMeshRenderer.cs`
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Create `VoxelMeshRenderer.cs` with constructor and all fields**

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns draw lists and drives Graphics.DrawMesh each frame.
/// Also manages stale-mesh eviction and the UnloadChunkMesh / UnloadRegionMesh lifecycle.
/// All methods must be called from the main thread only.
/// </summary>
internal sealed class VoxelMeshRenderer
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<Vector3Int, Mesh> _chunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _transChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _regionMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _transRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleRegionMeshes;
    private readonly WorldFlags _flags;
    private RegionManager _regionMgr; // set after construction

    private readonly List<(Mesh mesh, Matrix4x4 trs)> _chunkDrawList       = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _transChunkDrawList  = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _regionDrawList      = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)> _transRegionDrawList = new();
    private Matrix4x4 _cachedL2W = Matrix4x4.zero;

    // Own scratch lists — separate from ChunkStreamer scratch lists
    private readonly List<Vector3Int> _scratchStaleChunks  = new();
    private readonly List<Vector3Int> _scratchStaleRegions = new();

    public VoxelMeshRenderer(
        VoxelWorld world,
        Dictionary<Vector3Int, Mesh> chunkMeshes,
        Dictionary<Vector3Int, Mesh> transChunkMeshes,
        Dictionary<Vector3Int, Mesh> regionMeshes,
        Dictionary<Vector3Int, Mesh> transRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleChunkMeshes,
        Dictionary<Vector3Int, Mesh> staleRegionMeshes,
        WorldFlags flags)
    {
        _world             = world;
        _chunkMeshes       = chunkMeshes;
        _transChunkMeshes  = transChunkMeshes;
        _regionMeshes      = regionMeshes;
        _transRegionMeshes = transRegionMeshes;
        _staleChunkMeshes  = staleChunkMeshes;
        _staleRegionMeshes = staleRegionMeshes;
        _flags             = flags;
    }

    /// <summary>Set after construction to avoid circular constructor arguments.</summary>
    public RegionManager RegionMgr { set => _regionMgr = value; }
```

- [ ] **Step 2: Add `DrawAllMeshes()` — move from `VoxelWorld.DrawAllMeshes` (lines ~1341–1389)**

```csharp
    public void DrawAllMeshes()
    {
        if (_world.chunkMaterial == null) return;
        var localToWorld = _world.transform.localToWorldMatrix;

        if (_flags.DrawListDirty || localToWorld != _cachedL2W)
        {
            _cachedL2W = localToWorld;
            _flags.DrawListDirty = false;

            _chunkDrawList.Clear();
            foreach (var kvp in _staleChunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));
            foreach (var kvp in _chunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));

            _regionDrawList.Clear();
            foreach (var kvp in _staleRegionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));
            foreach (var kvp in _regionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));

            _transChunkDrawList.Clear();
            foreach (var kvp in _transChunkMeshes)
                if (kvp.Value != null)
                    _transChunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.ChunkToWorldPos(kvp.Key))));

            _transRegionDrawList.Clear();
            foreach (var kvp in _transRegionMeshes)
                if (kvp.Value != null)
                    _transRegionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(VoxelCoords.RegionWorldPos(kvp.Key))));
        }

        var transMat = _world.transparentMaterial != null ? _world.transparentMaterial : _world.chunkMaterial;
        int layer = _world.gameObject.layer;
        foreach (var (mesh, trs) in _chunkDrawList)
            Graphics.DrawMesh(mesh, trs, _world.chunkMaterial, layer);
        foreach (var (mesh, trs) in _regionDrawList)
            Graphics.DrawMesh(mesh, trs, _world.chunkMaterial, layer);
        foreach (var (mesh, trs) in _transChunkDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
        foreach (var (mesh, trs) in _transRegionDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
    }
```

- [ ] **Step 3: Add `UnloadChunkMesh()` and `UnloadRegionMesh()` — move from `VoxelWorld` (lines ~1285–1307)**

```csharp
    public void UnloadChunkMesh(Vector3Int coord)
    {
        if (_chunkMeshes.TryGetValue(coord, out var mesh))
        {
            _chunkMeshes.Remove(coord);
            if (mesh != null) _staleChunkMeshes[coord] = mesh;
            _flags.DrawListDirty = true;
        }
        if (_transChunkMeshes.TryGetValue(coord, out var tmesh))
        {
            _transChunkMeshes.Remove(coord);
            if (tmesh != null) Object.Destroy(tmesh);
            _flags.DrawListDirty = true;
        }
    }

    public void UnloadRegionMesh(Vector3Int r)
    {
        if (_regionMeshes.TryGetValue(r, out var mesh))
        {
            _regionMeshes.Remove(r);
            if (mesh != null) _staleRegionMeshes[r] = mesh;
            _flags.DrawListDirty = true;
        }
        if (_transRegionMeshes.TryGetValue(r, out var tmesh))
        {
            _transRegionMeshes.Remove(r);
            if (tmesh != null) Object.Destroy(tmesh);
            _flags.DrawListDirty = true;
        }
    }
```

- [ ] **Step 4: Add `EvictStaleMeshes()` — move from `VoxelWorld.EvictStaleMeshes` (lines ~1309–1339)**

```csharp
    /// <summary>
    /// Distance-based eviction of stale chunk meshes. Delegates stale region eviction
    /// to RegionManager. Must be called from the main thread.
    /// </summary>
    public void EvictStaleMeshes()
    {
        int vd = _world.AlignedViewDistance;
        var pc = _world.LastPlayerChunk;

        _scratchStaleChunks.Clear();
        foreach (var c in _staleChunkMeshes.Keys)
            if (Mathf.Abs(c.x - pc.x) > vd + 2 || Mathf.Abs(c.z - pc.z) > vd + 2)
                _scratchStaleChunks.Add(c);
        foreach (var c in _scratchStaleChunks)
        {
            Object.Destroy(_staleChunkMeshes[c]);
            _staleChunkMeshes.Remove(c);
            _flags.DrawListDirty = true;
        }

        _regionMgr?.EvictStaleRegionMeshes();
    }
}
```

- [ ] **Step 5: Wire `VoxelMeshRenderer` into `VoxelWorld`**

In `VoxelWorld.cs`:

a) Add field:
```csharp
private VoxelMeshRenderer _renderer;
```

b) In `Start()`, after existing setup, construct the renderer:
```csharp
_renderer = new VoxelMeshRenderer(
    this,
    _chunkMeshes, _transChunkMeshes,
    _regionMeshes, _transRegionMeshes,
    _staleChunkMeshes, _staleRegionMeshes,
    _flags);
// RegionMgr wired in Task 4
```

c) Replace `DrawAllMeshes()` call in `Update()`:
```csharp
// Replace: DrawAllMeshes();
_renderer.DrawAllMeshes();
```

d) Replace `EvictStaleMeshes()` call in `Update()`:
```csharp
// Replace: EvictStaleMeshes();
_renderer.EvictStaleMeshes();
```

e) In `UnloadChunkMesh` / `UnloadRegionMesh` call sites within `VoxelWorld` (called from
`UpdateLoadedChunks` scratch loops), replace with `_renderer.UnloadChunkMesh(c)` /
`_renderer.UnloadRegionMesh(r)`.

f) Remove the now-empty private methods `DrawAllMeshes`, `UnloadChunkMesh`,
`UnloadRegionMesh`, `EvictStaleMeshes`, and the four private draw list fields and
`_cachedL2W` from `VoxelWorld`.

- [ ] **Step 6: Verify — compile + play**

Zero console errors. Play mode: world renders identically. Stale meshes evict when player moves.

- [ ] **Step 7: Commit**

```bash
git add Assets/Voxel/Scripts/VoxelMeshRenderer.cs Assets/Voxel/Scripts/VoxelMeshRenderer.cs.meta Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: extract VoxelMeshRenderer from VoxelWorld"
```

---

## Task 4: Extract `RegionManager`

Moves the LOD1+ region pipeline: `Task.Run` mesh builds, semaphore, cancellation token,
`TryFeedChunkIntoRegion`, `ApplyReadyRegions`, `RequestRegionMesh`, and region mesh lifecycle.

**Files:**
- Create: `Assets/Voxel/Scripts/RegionManager.cs`
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Create `RegionManager.cs` with the `RegionBuildResult` struct and constructor**

```csharp
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

internal sealed class RegionManager
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<Vector3Int, VoxelChunk>  _chunks;
    private readonly Dictionary<Vector3Int, RegionData>  _regions;
    private readonly Dictionary<Vector3Int, Mesh>        _regionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _transRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _staleRegionMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _staleChunkMeshes;
    private readonly HashSet<Vector3Int>                 _desiredRegions;
    private readonly WorldFlags                          _flags;

    private readonly HashSet<Vector3Int>                _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult> _regionReadyQueue = new();
    private readonly ConcurrentQueue<Vector3Int>        _cancelledRegions = new();
    private SemaphoreSlim           _regionSemaphore;
    private CancellationTokenSource _regionCts = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int       RegionCoord;
        public readonly WritableMeshData MeshData;
        public readonly WritableMeshData TransMeshData;
        public RegionBuildResult(Vector3Int r, WritableMeshData m, WritableMeshData t)
        { RegionCoord = r; MeshData = m; TransMeshData = t; }
    }

    // Own scratch lists for region stale eviction
    private readonly List<Vector3Int> _scratchStaleRegions = new();

    public RegionManager(
        VoxelWorld world,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        Dictionary<Vector3Int, RegionData> regions,
        Dictionary<Vector3Int, Mesh> regionMeshes,
        Dictionary<Vector3Int, Mesh> transRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleRegionMeshes,
        Dictionary<Vector3Int, Mesh> staleChunkMeshes,
        HashSet<Vector3Int> desiredRegions,
        WorldFlags flags)
    {
        _world             = world;
        _chunks            = chunks;
        _regions           = regions;
        _regionMeshes      = regionMeshes;
        _transRegionMeshes = transRegionMeshes;
        _staleRegionMeshes = staleRegionMeshes;
        _staleChunkMeshes  = staleChunkMeshes;
        _desiredRegions    = desiredRegions;
        _flags             = flags;
        _regionSemaphore   = new SemaphoreSlim(world.maxRegionTasks, world.maxRegionTasks);
    }
```

- [ ] **Step 2: Add `TryFeedChunkIntoRegion`, `RequestRegionMesh`, `ApplyReadyRegions`**

Move these three methods verbatim from `VoxelWorld.cs`, replacing `_lastPlayerChunk` with
`_world.LastPlayerChunk` and `AlignedViewDistance` with `_world.AlignedViewDistance`:

```csharp
    public void TryFeedChunkIntoRegion(Vector3Int coord, VoxelChunk chunk)
    {
        int dist     = Mathf.Max(Mathf.Abs(coord.x - _world.LastPlayerChunk.x),
                                  Mathf.Abs(coord.z - _world.LastPlayerChunk.z));
        int lod      = 0;
        int boundary = _world.AlignedViewDistance;
        while (lod < _world.lodLevels && dist >= boundary) { lod++; boundary *= 2; }
        if (lod == 0) return;

        var regionCoord = VoxelCoords.ChunkToRegionCoord(coord, lod);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - VoxelCoords.RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        var bytes = new byte[VoxelChunk.VoxelCount];
        chunk.CopyTo(bytes);
        region.SetChunk(local.x, local.y, local.z, bytes);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionMeshes.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region);
    }

    public void RequestRegionMesh(Vector3Int regionCoord, RegionData region)
    {
        _regionInFlight.Add(regionCoord);
        int step = 1 << regionCoord.y;

        var neighbours = new RegionData[6];
        var dirs = MeshBuilder.RegionNeighbourDirs;
        neighbours[0] = GetRegion(regionCoord + dirs[0]);
        neighbours[1] = GetRegion(regionCoord + dirs[1]);
        neighbours[4] = GetRegion(regionCoord + dirs[4]);
        neighbours[5] = GetRegion(regionCoord + dirs[5]);

        var token = _regionCts.Token;
        Task.Run(async () =>
        {
            try { await _regionSemaphore.WaitAsync(token); }
            catch (OperationCanceledException) { _cancelledRegions.Enqueue(regionCoord); return; }
            try
            {
                var (mesh, transMesh) = MeshBuilder.BuildRegionMeshData(region, neighbours, step);
                _regionReadyQueue.Enqueue(new RegionBuildResult(regionCoord, mesh, transMesh));
            }
            finally { _regionSemaphore.Release(); }
        });
    }

    public bool ApplyReadyRegions()
    {
        int  applied  = 0;
        bool anyAdded = false;
        while (applied < _world.maxApplyPerFrame && _regionReadyQueue.TryDequeue(out var result))
        {
            _regionInFlight.Remove(result.RegionCoord);

            if (!_regionMeshes.ContainsKey(result.RegionCoord)
                && _desiredRegions.Contains(result.RegionCoord))
            {
                _regionMeshes[result.RegionCoord]      = MeshBuilder.CreateMesh(result.MeshData);
                _transRegionMeshes[result.RegionCoord] = MeshBuilder.CreateMesh(result.TransMeshData);

                if (_staleRegionMeshes.TryGetValue(result.RegionCoord, out var stale))
                { Object.Destroy(stale); _staleRegionMeshes.Remove(result.RegionCoord); }

                var baseC = VoxelCoords.RegionBaseChunkCoord(result.RegionCoord);
                int hs    = 1 << result.RegionCoord.y;
                for (int cx = baseC.x; cx < baseC.x + hs; cx++)
                for (int cz = baseC.z; cz < baseC.z + hs; cz++)
                for (int cy = 0; cy < _world.verticalChunks; cy++)
                {
                    var c = new Vector3Int(cx, cy, cz);
                    if (_staleChunkMeshes.TryGetValue(c, out var sc))
                    { Object.Destroy(sc); _staleChunkMeshes.Remove(c); }
                }
                _flags.DrawListDirty = true;
                anyAdded = true;
            }
            else
            {
                result.MeshData?.Discard();
                result.TransMeshData?.Discard();
            }
            applied++;
        }
        return anyAdded;
    }
```

- [ ] **Step 3: Add helper and lifecycle methods**

```csharp
    public bool DrainCancelledRegions()
    {
        bool any = false;
        while (_cancelledRegions.TryDequeue(out var r))
        { _regionInFlight.Remove(r); any = true; }
        return any;
    }

    public bool IsRegionInFlight(Vector3Int regionCoord) => _regionInFlight.Contains(regionCoord);

    /// <summary>Cancel and recreate CTS. Called when the player moves.</summary>
    public void ResetCancellation()
    {
        _regionCts.Cancel();
        _regionCts.Dispose();
        _regionCts = new CancellationTokenSource();
    }

    /// <summary>Cancel only — no recreate. Called from OnDestroy.</summary>
    public void CancelCts() => _regionCts.Cancel();

    public RegionData GetRegion(Vector3Int regionCoord) =>
        _regions.TryGetValue(regionCoord, out var r) ? r : null;

    public void UnloadRegionMesh(Vector3Int r)
    {
        if (_regionMeshes.TryGetValue(r, out var mesh))
        {
            _regionMeshes.Remove(r);
            if (mesh != null) _staleRegionMeshes[r] = mesh;
            _flags.DrawListDirty = true;
        }
        if (_transRegionMeshes.TryGetValue(r, out var tmesh))
        {
            _transRegionMeshes.Remove(r);
            if (tmesh != null) Object.Destroy(tmesh);
            _flags.DrawListDirty = true;
        }
    }

    /// <summary>Distance-based eviction of stale region meshes. Called by VoxelMeshRenderer.</summary>
    public void EvictStaleRegionMeshes()
    {
        int vd = _world.AlignedViewDistance;
        var pc = _world.LastPlayerChunk;

        _scratchStaleRegions.Clear();
        foreach (var r in _staleRegionMeshes.Keys)
        {
            int lod         = r.y;
            int outerRadius = vd * (1 << lod) + (1 << lod);
            var baseC  = VoxelCoords.RegionBaseChunkCoord(r);
            int hs     = 1 << lod;
            int nearX  = Mathf.Clamp(pc.x, baseC.x, baseC.x + hs - 1);
            int nearZ  = Mathf.Clamp(pc.z, baseC.z, baseC.z + hs - 1);
            int dist   = Mathf.Max(Mathf.Abs(nearX - pc.x), Mathf.Abs(nearZ - pc.z));
            if (dist > outerRadius) _scratchStaleRegions.Add(r);
        }
        foreach (var r in _scratchStaleRegions)
        { Object.Destroy(_staleRegionMeshes[r]); _staleRegionMeshes.Remove(r); _flags.DrawListDirty = true; }
    }

    /// <summary>Dispose semaphore and CTS. Call after CompleteAndDisposeAll in OnDestroy.</summary>
    public void CompleteAndDispose()
    {
        _regionSemaphore.Dispose();
        _regionCts.Dispose();
    }
}
```

- [ ] **Step 4: Wire `RegionManager` into `VoxelWorld`**

a) Add field:
```csharp
private RegionManager _regionMgr;
```

b) Remove **only** the fields that have moved into `RegionManager` (its owned state).
The shared mesh dicts stay declared in `VoxelWorld` and are passed by reference.

```csharp
// Remove from VoxelWorld (these move to RegionManager):
private readonly HashSet<Vector3Int>                 _regionInFlight    = new();
private readonly ConcurrentQueue<RegionBuildResult>  _regionReadyQueue  = new();
private readonly ConcurrentQueue<Vector3Int>         _cancelledRegions  = new();
private SemaphoreSlim           _regionSemaphore;
private CancellationTokenSource _regionCts = new();
private readonly struct RegionBuildResult { ... }   // remove entire nested struct

// KEEP in VoxelWorld (shared dicts — passed by reference to subsystems):
private readonly Dictionary<Vector3Int, Mesh> _regionMeshes      = new();
private readonly Dictionary<Vector3Int, Mesh> _transRegionMeshes = new();
private readonly Dictionary<Vector3Int, Mesh> _staleRegionMeshes = new();
```

c) In `Start()`, construct `RegionManager` (replace `_regionSemaphore = new SemaphoreSlim(...)`):
```csharp
_regionMgr = new RegionManager(
    this, _chunks, _regions,
    _regionMeshes, _transRegionMeshes,
    _staleRegionMeshes, _staleChunkMeshes,
    _desiredRegions, _flags);

// Wire back-references
_renderer.RegionMgr = _regionMgr;
```

d) In `Update()`, replace:
```csharp
// Replace:
bool drained = false;
while (_cancelledRegions.TryDequeue(out var r))
{ _regionInFlight.Remove(r); drained = true; }
// With:
bool drained = _regionMgr.DrainCancelledRegions();

// Replace:
bool regionsApplied = ApplyReadyRegions();
// With:
bool regionsApplied = _regionMgr.ApplyReadyRegions();
```

e) In `OnDestroy()`, replace CTS cancel/dispose and semaphore with:
```csharp
_regionMgr.CancelCts();
// ... (rest of OnDestroy — CompleteAndDispose called at the end)
_regionMgr.CompleteAndDispose();
```

f) Replace `GetRegion()` public API:
```csharp
public RegionData GetRegion(Vector3Int regionCoord) => _regionMgr.GetRegion(regionCoord);
```

g) In `HandleWorldMoved` (the block in `Update` triggered by position/LOD change), replace
`_regionCts.Cancel(); _regionCts.Dispose(); _regionCts = new...` with:
```csharp
_regionMgr.ResetCancellation();
```

h) In `RequestRegionMesh` / `ApplyReadyRegions` call sites within `VoxelWorld` (from
`TryFeedChunkIntoRegion` and streaming), replace with `_regionMgr.RequestRegionMesh(...)` and
`_regionMgr.TryFeedChunkIntoRegion(...)`.

i) Remove the now-empty private methods: `RequestRegionMesh`, `ApplyReadyRegions`,
`TryFeedChunkIntoRegion` from `VoxelWorld`.

- [ ] **Step 5: Verify — compile + play**

Zero console errors. Play mode: LOD regions (distant terrain) load correctly.

- [ ] **Step 6: Commit**

```bash
git add Assets/Voxel/Scripts/RegionManager.cs Assets/Voxel/Scripts/RegionManager.cs.meta Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: extract RegionManager from VoxelWorld"
```

---

## Task 5: Extract `ChunkPipelineProcessor`

Moves the LOD0 Burst job pipeline, V2 decoration gate, mesh list creation, and all
`BatchBuffer` / `ChunkPipeline` types. This is the most complex extraction.

**Files:**
- Create: `Assets/Voxel/Scripts/ChunkPipelineProcessor.cs`
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Create `ChunkPipelineProcessor.cs` — fields, inner types, constructor**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

internal sealed class ChunkPipelineProcessor
{
    private readonly VoxelWorld _world;
    private readonly NativeArray<float2>    _v2CSpline;
    private readonly NativeArray<float2>    _v2ESpline;
    private readonly NativeArray<BiomeDef>  _v2Biomes;
    private readonly NativeArray<TreeConfig> _v2TreeConfigs;
    private readonly Dictionary<Vector3Int, VoxelChunk> _chunks;
    private readonly Dictionary<Vector3Int, Mesh> _chunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _transChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleChunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh> _staleRegionMeshes;
    private readonly HashSet<Vector3Int> _inFlight;
    private readonly HashSet<Vector3Int> _desiredCoords;
    private readonly HashSet<Vector3Int> _pendingCoords;
    private readonly WorldFlags          _flags;
    private readonly Action<Vector3Int, VoxelChunk> _onChunkReady;

    private readonly List<ChunkPipeline> _pipelines   = new();
    private readonly HashSet<Vector3Int> _awaitingDecoration       = new();
    private readonly HashSet<Vector3Int> _needsMeshAfterDecoration = new();
    private readonly List<int>           _scratchCompleted = new();
    private readonly List<int>           _scratchRemove    = new();

    public int PipelineCount => _pipelines.Count;

    // ── Inner types (moved from VoxelWorld) ──────────────────────────────────

    private class BatchBuffer
    {
        public NativeArray<byte>  Data;
        public NativeArray<int3>  Coords;
        public NativeParallelHashMap<long, int> SurfaceCache;
        public int Pending;

        public BatchBuffer(int count)
        {
            Data         = new NativeArray<byte>(count * VoxelChunk.VoxelCount, Allocator.Persistent,
                                                  NativeArrayOptions.UninitializedMemory);
            Coords       = new NativeArray<int3>(count, Allocator.Persistent,
                                                  NativeArrayOptions.UninitializedMemory);
            SurfaceCache = new NativeParallelHashMap<long, int>(count * 18 * 18 * 2, Allocator.Persistent);
            Pending      = count;
        }

        public void Release()
        {
            if (--Pending > 0) return;
            if (Data.IsCreated)         Data.Dispose();
            if (Coords.IsCreated)       Coords.Dispose();
            if (SurfaceCache.IsCreated) SurfaceCache.Dispose();
        }
    }

    private struct ChunkPipeline
    {
        public Vector3Int Coord;
        public JobHandle  Handle;
        public NativeArray<byte>   Voxels;
        public BatchBuffer         Batch;
        public NativeArray<byte>[] NeighbourSnapshots;
        public NativeList<float3>  MeshVerts,  MeshNorms;
        public NativeList<float2>  MeshUVs,    MeshUV2s;
        public NativeList<int>     MeshTris;
        public NativeList<float3>  TransVerts, TransNorms;
        public NativeList<float2>  TransUVs,   TransUV2s;
        public NativeList<int>     TransTris;
        public bool BuildMesh;
        public bool Discarded;
    }

    public ChunkPipelineProcessor(
        VoxelWorld world,
        NativeArray<float2>    v2CSpline,
        NativeArray<float2>    v2ESpline,
        NativeArray<BiomeDef>  v2Biomes,
        NativeArray<TreeConfig> v2TreeConfigs,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        Dictionary<Vector3Int, Mesh> chunkMeshes,
        Dictionary<Vector3Int, Mesh> transChunkMeshes,
        Dictionary<Vector3Int, Mesh> staleChunkMeshes,
        Dictionary<Vector3Int, Mesh> staleRegionMeshes,
        HashSet<Vector3Int> inFlight,
        HashSet<Vector3Int> desiredCoords,
        HashSet<Vector3Int> pendingCoords,
        WorldFlags flags,
        Action<Vector3Int, VoxelChunk> onChunkReady)
    {
        _world             = world;
        _v2CSpline         = v2CSpline;
        _v2ESpline         = v2ESpline;
        _v2Biomes          = v2Biomes;
        _v2TreeConfigs     = v2TreeConfigs;
        _chunks            = chunks;
        _chunkMeshes       = chunkMeshes;
        _transChunkMeshes  = transChunkMeshes;
        _staleChunkMeshes  = staleChunkMeshes;
        _staleRegionMeshes = staleRegionMeshes;
        _inFlight          = inFlight;
        _desiredCoords     = desiredCoords;
        _pendingCoords     = pendingCoords;
        _flags             = flags;
        _onChunkReady      = onChunkReady;
    }
```

- [ ] **Step 2: Move `SubmitChunkBatch` and `SubmitSubBatch` verbatim**

Copy `SubmitChunkBatch` and `SubmitSubBatch` from `VoxelWorld.cs` (~lines 726–865) into the
class. Update all field accesses: `_world.useV2Generator`, `_world.terrainBatchSize`,
`_world.GetTerrainSettings()`, `_world.GetTerrainSettingsV2()`.

The `_needsMeshAfterDecoration` tracking block inside `SubmitSubBatch` (the V2 branch) stays
in this class since it owns `_needsMeshAfterDecoration`.

For the non-V2 `buildMesh=true` path, the `SnapshotNeighbours` call becomes
`SnapshotNeighbours(coord, out int neighbourMask)` (moved to same class in Step 4).

- [ ] **Step 3: Move `ProcessCompletedPipelines` verbatim**

Copy `ProcessCompletedPipelines` from `VoxelWorld.cs` (~lines 874–994). Updates required:
- `_lastPlayerChunk` → `_world.LastPlayerChunk`
- `_world.maxApplyPerFrame` for budget
- `TryFeedChunkIntoRegion(coord, chunk)` → `_onChunkReady(coord, chunk)`
- `TryDecorateReady()` call stays (moved to same class in Step 5)
- `CreateMeshFromLists` / `DisposeMeshLists` calls stay (moved to same class in Step 6)

Also: the stale-removal block that accesses `_staleChunkMeshes` and `_staleRegionMeshes`
(lines ~967–975) uses those dicts passed in the constructor.

`_flags.DrawListDirty = true` and `_flags.NeedsMoreRequests = true` replace the direct field
sets.

- [ ] **Step 4: Move `SnapshotNeighbours` verbatim**

```csharp
    private NativeArray<byte>[] SnapshotNeighbours(Vector3Int coord, out int loadedMask)
    {
        var snapshots = new NativeArray<byte>[6];
        loadedMask = 0;
        for (int i = 0; i < 6; i++)
        {
            var n = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                           NativeArrayOptions.ClearMemory);
            var nc = coord + MeshBuilder.NeighbourDirs[i];
            if (_chunks.TryGetValue(nc, out var neighbour))
            {
                neighbour.Blocks.CopyTo(n);
                loadedMask |= (1 << i);
            }
            snapshots[i] = n;
        }
        return snapshots;
    }
```

- [ ] **Step 5: Move `TryDecorateReady` and `SubmitMeshOnly` verbatim**

Copy `TryDecorateReady` (~lines 1062–1098) and `SubmitMeshOnly` (~lines 1101–1161).
Updates:
- `_world.useV2Generator`, `_world.v2TreeConfigs`, `_world.verticalChunks`,
  `_world.maxDecorationsPerFrame`
- `ChunkDecorator.Decorate(coord, _chunks, _world.v2TreeConfigs, _v2Biomes, _world.GetTerrainSettingsV2(), _world.verticalChunks)`
- `TryFeedChunkIntoRegion(coord, _chunks[coord])` → `_onChunkReady(coord, _chunks[coord])`
- `SubmitMeshOnly(coord)` remains a call within same class

- [ ] **Step 6: Move `CreateMeshFromLists` and `DisposeMeshLists` verbatim**

These are `private static` methods. Copy them unchanged.

- [ ] **Step 7: Add lifecycle methods**

```csharp
    public void MarkAllDiscarded()
    {
        for (int i = 0; i < _pipelines.Count; i++)
        {
            var p = _pipelines[i]; p.Discarded = true; _pipelines[i] = p;
        }
    }

    /// <summary>
    /// For each coord in _awaitingDecoration, removes it from _inFlight.
    /// Then clears _awaitingDecoration and _needsMeshAfterDecoration.
    /// Must be called before UpdateLoadedChunks on player move.
    /// </summary>
    public void ClearDecorationState()
    {
        if (!_world.useV2Generator) return;
        foreach (var c in _awaitingDecoration) _inFlight.Remove(c);
        _awaitingDecoration.Clear();
        _needsMeshAfterDecoration.Clear();
    }

    /// <summary>
    /// Completes all in-flight Burst jobs and disposes all owned NativeArrays.
    /// Must be called BEFORE the V2 NativeArrays on VoxelWorld are disposed.
    /// </summary>
    public void CompleteAndDisposeAll()
    {
        var batchesToDispose = new System.Collections.Generic.HashSet<BatchBuffer>();
        for (int i = 0; i < _pipelines.Count; i++)
        {
            var p = _pipelines[i];
            p.Handle.Complete();
            if (p.Batch != null)
                batchesToDispose.Add(p.Batch);
            else if (p.Voxels.IsCreated)
                p.Voxels.Dispose();
            if (p.BuildMesh)
            {
                if (p.NeighbourSnapshots != null)
                    foreach (var n in p.NeighbourSnapshots) if (n.IsCreated) n.Dispose();
                DisposeMeshLists(ref p);
            }
        }
        foreach (var b in batchesToDispose)
        {
            if (b.Data.IsCreated)         b.Data.Dispose();
            if (b.Coords.IsCreated)       b.Coords.Dispose();
            if (b.SurfaceCache.IsCreated) b.SurfaceCache.Dispose();
        }
        _pipelines.Clear();
    }
}
```

- [ ] **Step 8: Wire `ChunkPipelineProcessor` into `VoxelWorld`**

a) Add field:
```csharp
private ChunkPipelineProcessor _pipeline;
```

b) In `Start()`, construct (after `_regionMgr` is created):
```csharp
_pipeline = new ChunkPipelineProcessor(
    this,
    _v2CSpline, _v2ESpline, _v2Biomes, _v2TreeConfigs,
    _chunks, _chunkMeshes, _transChunkMeshes,
    _staleChunkMeshes, _staleRegionMeshes,
    _inFlight, _desiredCoords, _pendingCoords,
    _flags,
    onChunkReady: _regionMgr.TryFeedChunkIntoRegion);
```

c) In `Update()`, replace:
```csharp
bool pipelinesApplied = ProcessCompletedPipelines();
// With:
bool pipelinesApplied = _pipeline.ProcessCompletedPipelines();
```

d) In `HandleWorldMoved`, replace:
```csharp
// Replace the discard loop:
for (int i = 0; i < _pipelines.Count; i++) { var p = _pipelines[i]; p.Discarded = true; _pipelines[i] = p; }
// With:
_pipeline.MarkAllDiscarded();

// Replace decoration clear block:
if (useV2Generator) { foreach (var c in _awaitingDecoration) _inFlight.Remove(c); ... }
// With:
_pipeline.ClearDecorationState();
```

e) Replace `SubmitChunkBatch(...)` calls in streaming code with `_pipeline.SubmitChunkBatch(...)`.

f) In `OnDestroy()`, replace the inline pipeline-disposal loop with a delegation call.
The existing loop starts at the `var batchesToDispose = new HashSet<BatchBuffer>()` line and
ends at `_pipelines.Clear()`. Remove that entire block and replace it with:
```csharp
_pipeline.CompleteAndDisposeAll();
```

g) Remove from `VoxelWorld`: `_pipelines`, `_awaitingDecoration`, `_needsMeshAfterDecoration`,
`BatchBuffer` class, `ChunkPipeline` struct, `_scratchCompleted`, `_scratchRemove`, and all
the private methods now in `ChunkPipelineProcessor` (including `SubmitChunkBatch`,
`SubmitSubBatch`, `ProcessCompletedPipelines`, `SnapshotNeighbours`, `TryDecorateReady`,
`SubmitMeshOnly`, `CreateMeshFromLists`, `DisposeMeshLists`).

- [ ] **Step 9: Verify — compile + play**

Zero console errors. Play mode: LOD0 chunks load. Trees appear (V2). Moving the player
triggers new chunks.

- [ ] **Step 10: Commit**

```bash
git add Assets/Voxel/Scripts/ChunkPipelineProcessor.cs Assets/Voxel/Scripts/ChunkPipelineProcessor.cs.meta Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: extract ChunkPipelineProcessor from VoxelWorld"
```

---

## Task 6: Extract `ChunkStreamer`

Moves desired-set building, eviction, and load decisions out of `VoxelWorld`.

**Files:**
- Create: `Assets/Voxel/Scripts/ChunkStreamer.cs`
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Create `ChunkStreamer.cs` — fields and constructor**

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

internal sealed class ChunkStreamer
{
    private readonly VoxelWorld _world;
    private readonly WorldFlags _flags;
    private readonly HashSet<Vector3Int> _desiredCoords;
    private readonly HashSet<Vector3Int> _desiredRegions;
    private readonly HashSet<Vector3Int> _pendingCoords;
    private readonly HashSet<Vector3Int> _inFlight;
    private readonly Dictionary<Vector3Int, VoxelChunk> _chunks;
    private readonly Dictionary<Vector3Int, RegionData>  _regions;
    private readonly Dictionary<Vector3Int, Mesh>        _chunkMeshes;
    private readonly Dictionary<Vector3Int, Mesh>        _regionMeshes;
    private readonly Action<Vector3Int> _unloadChunkMesh;
    private readonly Action<Vector3Int> _unloadRegionMesh;

    // Setter-injected to avoid circular constructor args
    public ChunkPipelineProcessor Pipeline { private get; set; }
    public RegionManager          Regions  { private get; set; }

    private int _lastLodLevels    = -1;
    private int _lastViewDistance = -1;

    public int PendingCount => _pendingCoords.Count;

    // Scratch lists — owned by ChunkStreamer
    private readonly List<Vector3Int> _scratchUnloadC    = new();
    private readonly List<Vector3Int> _scratchUnloadR    = new();
    private readonly List<Vector3Int> _scratchEvictC     = new();
    private readonly List<Vector3Int> _scratchEvictR     = new();
    private readonly List<Vector3Int> _scratchRequestC   = new();
    private readonly List<Vector3Int> _scratchDataCoords = new();
    private readonly List<(float dist, Vector3Int coord)> _scratchRegions   = new();
    private readonly List<(float dist, Vector3Int coord)> _scratchChunkSort = new();

    public ChunkStreamer(
        VoxelWorld world,
        WorldFlags flags,
        HashSet<Vector3Int> desiredCoords,
        HashSet<Vector3Int> desiredRegions,
        HashSet<Vector3Int> pendingCoords,
        HashSet<Vector3Int> inFlight,
        Dictionary<Vector3Int, VoxelChunk> chunks,
        Dictionary<Vector3Int, RegionData>  regions,
        Dictionary<Vector3Int, Mesh>        chunkMeshes,
        Dictionary<Vector3Int, Mesh>        regionMeshes,
        Action<Vector3Int> unloadChunkMesh,
        Action<Vector3Int> unloadRegionMesh)
    {
        _world            = world;
        _flags            = flags;
        _desiredCoords    = desiredCoords;
        _desiredRegions   = desiredRegions;
        _pendingCoords    = pendingCoords;
        _inFlight         = inFlight;
        _chunks           = chunks;
        _regions          = regions;
        _chunkMeshes      = chunkMeshes;
        _regionMeshes     = regionMeshes;
        _unloadChunkMesh  = unloadChunkMesh;
        _unloadRegionMesh = unloadRegionMesh;
    }
```

- [ ] **Step 2: Move `CheckLodSettingsChanged` and `CheckPositionChanged` verbatim**

```csharp
    public bool CheckLodSettingsChanged()
    {
        if (_world.lodLevels == _lastLodLevels && _world.viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = _world.lodLevels;
        _lastViewDistance = _world.viewDistance;
        _world.LastPlayerChunk = new Vector3Int(int.MaxValue, 0, 0);
        return true;
    }

    public bool CheckPositionChanged()
    {
        var cur = VoxelCoords.WorldToChunkCoord(_world.player != null ? _world.player.position : Vector3.zero);
        if (cur == _world.LastPlayerChunk) return false;
        _world.LastPlayerChunk = cur;
        return true;
    }
```

- [ ] **Step 3: Move `UpdateLoadedChunks` and its sub-helpers verbatim**

Copy `UpdateLoadedChunks` (~lines 509–714) into the class. Update all references:
- `_lastPlayerChunk` → `_world.LastPlayerChunk`
- `AlignedViewDistance` → `_world.AlignedViewDistance`
- `verticalChunks` → `_world.verticalChunks`
- `lodLevels` → `_world.lodLevels`
- `maxRequestsPerUpdate`, `maxPipelinesInFlight` → `_world.maxRequestsPerUpdate`, `_world.maxPipelinesInFlight`
- `UnloadChunkMesh(c)` → `_unloadChunkMesh(c)`
- `UnloadRegionMesh(r)` → `_unloadRegionMesh(r)`
- `SubmitChunkBatch(...)` → `Pipeline.SubmitChunkBatch(...)`
- `RequestRegionMesh(...)` → `Regions.RequestRegionMesh(...)`
- `_regionInFlight.Contains(r)` → `Regions.IsRegionInFlight(r)`
- `_regionMeshes.ContainsKey(r)` → `_regionMeshes.ContainsKey(r)` (dict reference, unchanged)
- `_needsMoreRequests = ...` → `_flags.NeedsMoreRequests = ...`
- `ChunkCenterWorld(...)` → `VoxelCoords.ChunkCenterWorld(...)`
- `RegionCenterWorld(...)` → `VoxelCoords.RegionCenterWorld(...)`
- `RegionBaseChunkCoord(...)` → `VoxelCoords.RegionBaseChunkCoord(...)`
- `ChunkToRegionCoord(...)` → `VoxelCoords.ChunkToRegionCoord(...)`
- Also remove the `_pipelines.Count` check for pipeline budget — use `Pipeline.PipelineCount`

Also move the `_scratchUnloadC/R`, `_scratchEvictC/R`, `_scratchRequestC`, `_scratchDataCoords`,
`_scratchRegions`, `_scratchChunkSort` usages to the fields declared in the constructor above.

Close the class:
```csharp
}
```

- [ ] **Step 4: Wire `ChunkStreamer` into `VoxelWorld`**

a) Add field:
```csharp
private ChunkStreamer _streamer;
```

b) In `Start()`, construct `_streamer` (after `_renderer`, `_regionMgr`, `_pipeline`):
```csharp
_streamer = new ChunkStreamer(
    this, _flags,
    _desiredCoords, _desiredRegions, _pendingCoords, _inFlight,
    _chunks, _regions, _chunkMeshes, _regionMeshes,
    _renderer.UnloadChunkMesh, _renderer.UnloadRegionMesh);
_streamer.Pipeline = _pipeline;
_streamer.Regions  = _regionMgr;
```

c) In `Update()`, replace:
```csharp
bool lodChanged = CheckLodSettingsChanged();
bool posChanged = CheckPositionChanged();
// With:
bool lodChanged = _streamer.CheckLodSettingsChanged();
bool posChanged = _streamer.CheckPositionChanged();

// Replace UpdateLoadedChunks(...) call:
UpdateLoadedChunks(false);
// With:
_streamer.UpdateLoadedChunks(false);
```

d) **Replace** the private `_lastPlayerChunk` field in `VoxelWorld` with a public property
backed by the same field (do not remove the field first — replace the declaration in one edit):

```csharp
// Replace:
private Vector3Int _lastPlayerChunk = new Vector3Int(int.MaxValue, 0, 0);
// With:
private Vector3Int _lastPlayerChunkBacking = new Vector3Int(int.MaxValue, 0, 0);
public  Vector3Int LastPlayerChunk
{
    get => _lastPlayerChunkBacking;
    set => _lastPlayerChunkBacking = value;
}
```

Also remove: `_lastLodLevels`, `_lastViewDistance`, all scratch lists,
`CheckLodSettingsChanged`, `CheckPositionChanged`, `UpdateLoadedChunks` from `VoxelWorld`.

e) Remove the old first `UpdateLoadedChunks(true)` call in `Start()` and replace with:
```csharp
_streamer.UpdateLoadedChunks(true);
```

- [ ] **Step 5: Verify — compile + play**

Zero console errors. Full Play mode test: move player in all directions, verify LOD0 and LOD1+
load, verify OnGUI shows correct counts, verify terrain matches pre-refactor behaviour.

- [ ] **Step 6: Commit**

```bash
git add Assets/Voxel/Scripts/ChunkStreamer.cs Assets/Voxel/Scripts/ChunkStreamer.cs.meta Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: extract ChunkStreamer from VoxelWorld"
```

---

## Task 7: Final `VoxelWorld` cleanup

After all extractions, `VoxelWorld` should be a thin coordinator. This task removes any
remaining dead code and verifies the final line count.

**Files:**
- Modify: `Assets/Voxel/Scripts/VoxelWorld.cs`

- [ ] **Step 1: Consolidate `HandleWorldMoved` into a private helper if not already done**

If the position-change response is still inlined in `Update()`, extract it:

```csharp
private void HandleWorldMoved(bool positionChanged)
{
    // Order matches existing VoxelWorld.Update behaviour (EvictStaleMeshes runs first,
    // before CTS reset, consistent with lines 422-449 of the original file).
    _renderer.EvictStaleMeshes();
    _regionMgr.ResetCancellation();
    _pipeline.MarkAllDiscarded();
    _pipeline.ClearDecorationState();
    if (positionChanged) TerrainGenerator.ClearSurfaceCache();
    _streamer.UpdateLoadedChunks(true);
}
```

Update `Update()` to call `HandleWorldMoved(posChanged)`.

- [ ] **Step 2: Verify `OnGUI` reads from subsystem properties**

```csharp
private void OnGUI()
{
    GUI.Label(new Rect(10, 10, 400, 200),
        $"Desired chunks:  {_desiredCoords.Count}\n" +
        $"Pending:         {_streamer.PendingCount}\n" +
        $"In-flight:       {_inFlight.Count}\n" +
        $"Pipelines:       {_pipeline.PipelineCount}\n" +
        $"Chunk meshes:    {_chunkMeshes.Count}\n" +
        $"Player chunk:    {LastPlayerChunk}\n" +
        $"ViewDist (aln):  {AlignedViewDistance}");
}
```

- [ ] **Step 3: Verify `OnDestroy` matches the required teardown order**

```csharp
private void OnDestroy()
{
    _regionMgr.CancelCts();                           // 1. cancel tasks
    _pipeline.CompleteAndDisposeAll();                 // 2. complete Burst jobs + dispose NativeArrays
    foreach (var kvp in _chunks) kvp.Value.Dispose(); // 3. dispose chunk data
    _chunks.Clear();                                   // 4.
    // 5. destroy meshes
    foreach (var kvp in _staleChunkMeshes)  if (kvp.Value) Destroy(kvp.Value);
    foreach (var kvp in _staleRegionMeshes) if (kvp.Value) Destroy(kvp.Value);
    foreach (var kvp in _chunkMeshes)       if (kvp.Value) Destroy(kvp.Value);
    foreach (var kvp in _transChunkMeshes)  if (kvp.Value) Destroy(kvp.Value);
    foreach (var kvp in _regionMeshes)      if (kvp.Value) Destroy(kvp.Value);
    foreach (var kvp in _transRegionMeshes) if (kvp.Value) Destroy(kvp.Value);
    _regionMgr.CompleteAndDispose();                   // 6. dispose semaphore + CTS
    if (_v2CSpline.IsCreated)     _v2CSpline.Dispose();   // 7. V2 NativeArrays last
    if (_v2ESpline.IsCreated)     _v2ESpline.Dispose();
    if (_v2Biomes.IsCreated)      _v2Biomes.Dispose();
    if (_v2TreeConfigs.IsCreated) _v2TreeConfigs.Dispose();
}
```

- [ ] **Step 4: Count lines and confirm under 350**

```bash
wc -l "Assets/Voxel/Scripts/VoxelWorld.cs"
```

Expected: under 350 lines.

- [ ] **Step 5: Final verify — compile + full play test**

Zero console errors. Play mode: all behaviour identical to pre-refactor. Move player, confirm:
- LOD0 chunks load near player
- LOD1+ region meshes load at distance
- Trees appear (V2 mode)
- Player chunk coord updates in OnGUI

- [ ] **Step 6: Final commit**

```bash
git add Assets/Voxel/Scripts/VoxelWorld.cs
git commit -m "refactor: slim VoxelWorld.cs to coordinator after subsystem extraction"
```

---

## Done

All seven files should now be in place. Run a final check:

```bash
wc -l Assets/Voxel/Scripts/VoxelWorld.cs \
       Assets/Voxel/Scripts/VoxelCoords.cs \
       Assets/Voxel/Scripts/WorldFlags.cs \
       Assets/Voxel/Scripts/VoxelMeshRenderer.cs \
       Assets/Voxel/Scripts/RegionManager.cs \
       Assets/Voxel/Scripts/ChunkPipelineProcessor.cs \
       Assets/Voxel/Scripts/ChunkStreamer.cs
```

Expected: no individual file exceeds 600 lines. `VoxelWorld.cs` under 350.
