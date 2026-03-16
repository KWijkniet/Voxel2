using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Variable-size LOD rendering:
///
///   LOD 0 (within AlignedViewDistance):
///     Individual 16³ chunk meshes, full-detail. Drawn via Graphics.DrawMesh — no GameObjects.
///
///   LOD L (ring L, beyond LOD L-1):
///     Regions of (1&lt;&lt;L)×(1&lt;&lt;L) chunks, sampled at step (1&lt;&lt;L) voxels/cell.
///     Every LOD mesh always has ~16×16 XZ cells → constant GPU cost per mesh.
///     e.g. LOD 1 = 2×2 chunks / step 2, LOD 2 = 4×4 / step 4, LOD 3 = 8×8 / step 8.
///
/// Example: viewDistance=8, lodLevels=3 → 8→16→32→64 chunk radii = 1024 m max.
/// </summary>
public class VoxelWorld : MonoBehaviour
{
    [Header("World")]
    public int       viewDistance   = 8;
    public int       verticalChunks = 3;
    public Transform player;

    [Header("Terrain — Water")]
    public int seaLevel       = 20;

    [Header("Terrain — Heights")]
    public int   baseHeight     = 2;
    public int   plainsHeight   = 22;
    public int   mountainHeight = 80;
    public int   oceanDepth     = 12;

    [Header("Terrain — Noise")]
    [Tooltip("Base frequency (smaller = larger features)")]
    public float noiseScale  = 0.004f;
    [Range(1, 8)]
    public int   octaves     = 5;
    [Range(0f, 1f)]
    public float persistence = 0.5f;
    [Range(1f, 4f)]
    public float lacunarity  = 2.0f;

    [Header("Terrain — Biome")]
    [Tooltip("Scale of biome transitions (very low = large biomes)")]
    public float biomeScale = 0.0008f;

    [Header("Terrain — Domain Warp")]
    [Tooltip("Max coordinate warp in voxels (0 = disable)")]
    public float warpStrength = 25f;
    public float warpScale    = 0.005f;

    [Header("Terrain — Block Layers")]
    public int dirtDepth      = 4;
    public int sandBeachWidth = 3;
    public int snowAltitude   = 75;

    [Header("Rendering")]
    public Material chunkMaterial;

    [Header("Async")]
    public int maxConcurrentTasks  = 4;
    public int maxApplyPerFrame    = 6;
    [Tooltip("Max new tasks submitted per UpdateLoadedChunks call. " +
             "Half goes to LOD 0 mesh tasks (closest first), half to region data tasks. " +
             "Lower = tighter prioritization; higher = loads more at once.")]
    public int maxRequestsPerUpdate = 16;

    [Header("LOD")]
    [Tooltip("Number of LOD levels for region rendering. Each level doubles the view radius.")]
    [Range(0, 3)]
    public int lodLevels = 2;


    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>
    /// viewDistance rounded up to the nearest (1&lt;&lt;lodLevels) multiple.
    /// Ensures all LOD zone boundaries land exactly on a region edge for every level,
    /// so no region ever straddles the LOD 0 / LOD 1 boundary.
    /// e.g. lodLevels=2 → snap=4: viewDistance=6 → 8.  lodLevels=3 → snap=8: vd=6 → 8.
    /// </summary>
    public int AlignedViewDistance
    {
        get { int snap = 1 << Mathf.Max(1, lodLevels); return ((viewDistance + snap - 1) / snap) * snap; }
    }

    private int MaxRadius => AlignedViewDistance * (1 << lodLevels);

    // ── LOD 0 storage (individual chunks) ─────────────────────────────────────

    private readonly Dictionary<Vector3Int, PaletteChunk> _chunks        = new();
    private readonly Dictionary<Vector3Int, Mesh>         _chunkMeshes   = new();
    private readonly HashSet<Vector3Int>                  _desiredCoords = new();
    private readonly HashSet<Vector3Int>                  _inFlight      = new();
    private readonly ConcurrentQueue<ChunkBuildResult>    _readyQueue    = new();

    private readonly struct ChunkBuildResult
    {
        public readonly Vector3Int       Coord;
        public readonly PaletteChunk     Chunk;
        public readonly WritableMeshData MeshData; // null = data-only (for region use)
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, WritableMeshData m)
        { Coord = c; Chunk = ch; MeshData = m; }
    }

    // Tracks scheduled Burst generation jobs until their JobHandle is complete.
    private struct PendingGenerationJob
    {
        public Vector3Int        Coord;
        public JobHandle         Handle;
        public NativeArray<byte> Blocks;
        public bool              BuildMesh;
        public PaletteChunk[]    Neighbours; // pre-fetched for mesh build; null if !BuildMesh
        public bool              Discarded;  // true when position changed before job completed
    }
    private readonly List<PendingGenerationJob> _pendingGenerationJobs = new();

    // ── LOD 1+ storage (regions) ──────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, RegionData>  _regions       = new();
    private readonly Dictionary<Vector3Int, Mesh>         _regionMeshes  = new();
    private readonly HashSet<Vector3Int>                  _desiredRegions   = new();
    private readonly HashSet<Vector3Int>                  _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult>   _regionReadyQueue = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int       RegionCoord;
        public readonly WritableMeshData MeshData;
        public RegionBuildResult(Vector3Int r, WritableMeshData m) { RegionCoord = r; MeshData = m; }
    }

    private SemaphoreSlim _semaphore;
    // True when the last UpdateLoadedChunks hit the request budget — more batches are needed
    private bool _needsMoreRequests;

    // Cancels all tasks that are waiting for the semaphore when the player moves.
    // Tasks already executing are unaffected and complete normally.
    private CancellationTokenSource _generationCts = new CancellationTokenSource();

    // Background tasks signal here when cancelled so the main thread can clean _inFlight / _regionInFlight.
    private readonly ConcurrentQueue<Vector3Int> _cancelledCoords       = new();
    private readonly ConcurrentQueue<Vector3Int> _cancelledRegionCoords = new();

    // Persistent scratch lists reused in UpdateLoadedChunks to avoid per-call allocations.
    private readonly List<Vector3Int> _scratchUnloadC       = new();
    private readonly List<Vector3Int> _scratchUnloadR       = new();
    private readonly List<Vector3Int> _scratchEvictC        = new();
    private readonly List<Vector3Int> _scratchEvictR        = new();
    private readonly List<Vector3Int> _scratchRequestC      = new();
    private readonly List<Vector3Int> _scratchRegionsToProc = new();
    private readonly List<int>        _scratchCompletedIdx  = new();
    private readonly List<int>        _scratchRemoveIdx     = new();
    // Persistent distance-sort scratch — avoids per-frame Dictionary allocation in UpdateLoadedChunks.
    private readonly Dictionary<Vector3Int, float> _scratchChunkDist  = new();
    private readonly Dictionary<Vector3Int, float> _scratchRegionDist = new();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        // Pre-seed change-detection state so the first Update() doesn't see
        // everything as "changed" and wastefully cancel all jobs we're about to start.
        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        _lastPlayerChunk  = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        UpdateLoadedChunks(true);
    }

    private void OnDestroy()
    {
        _generationCts.Cancel();
        _generationCts.Dispose();
        // Complete and dispose all in-flight Burst jobs to avoid NativeArray leaks.
        foreach (var pj in _pendingGenerationJobs)
        {
            pj.Handle.Complete();
            pj.Blocks.Dispose();
        }
        _pendingGenerationJobs.Clear();
    }

    private void Update()
    {
        // LOD must be checked first: it resets _lastPlayerChunk to a sentinel,
        // and CheckPositionChanged must then overwrite it with the real value
        // before UpdateLoadedChunks runs — preventing an Abs(int.MinValue) overflow.
        bool lodChanged  = CheckLodSettingsChanged();
        bool posChanged  = CheckPositionChanged();

        // Remove cancelled task markers so those coords can be re-requested
        bool anyDrained = false;
        while (_cancelledCoords.TryDequeue(out var cancelled))
        {
            _inFlight.Remove(cancelled);
            anyDrained = true;
        }
        while (_cancelledRegionCoords.TryDequeue(out var cancelledR))
        {
            _regionInFlight.Remove(cancelledR);
            anyDrained = true;
        }

        if (posChanged || lodChanged)
        {
            // Cancel all tasks waiting for the semaphore — they're for the old position
            // and would block new high-priority tasks for the current position.
            _generationCts.Cancel();
            _generationCts.Dispose();
            _generationCts = new CancellationTokenSource();

            // Mark all pending Burst generation jobs as discarded.
            // We cannot cancel them mid-execution; they will complete naturally
            // and be disposed in ProcessCompletedGenerationJobs without enqueuing results.
            for (int i = 0; i < _pendingGenerationJobs.Count; i++)
            {
                var pj = _pendingGenerationJobs[i];
                pj.Discarded = true;
                _pendingGenerationJobs[i] = pj;
            }

            if (posChanged) TerrainGenerator.ClearSurfaceCache();
            UpdateLoadedChunks(posChanged || lodChanged);
        }

        // Collect any Burst generation jobs that completed this frame and kick off
        // their mesh builds (or enqueue data-only results) before ApplyReadyChunks.
        ProcessCompletedGenerationJobs();

        bool chunksApplied  = ApplyReadyChunks();
        bool regionsApplied = ApplyReadyRegions();

        if ((chunksApplied || regionsApplied || anyDrained) && _needsMoreRequests)
            UpdateLoadedChunks(false);

        // Submit all stored meshes to the renderer this frame.
        // Unity automatically frustum-culls each mesh using its pre-computed bounds.
        DrawAllMeshes();
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(10, 10, 400, 160),
            $"Desired chunks:  {_desiredCoords.Count}\n" +
            $"In-flight:       {_inFlight.Count}\n" +
            $"Pending Burst:   {_pendingGenerationJobs.Count}\n" +
            $"Ready queue:     {_readyQueue.Count}\n" +
            $"Chunk meshes:    {_chunkMeshes.Count}\n" +
            $"Player chunk:    {_lastPlayerChunk}\n" +
            $"ViewDist (aln):  {AlignedViewDistance}");
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk   = new Vector3Int(int.MaxValue, 0, 0);
    private int        _lastLodLevels     = -1;
    private int        _lastViewDistance  = -1;

    private bool CheckLodSettingsChanged()
    {
        if (lodLevels == _lastLodLevels && viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        // Force full rebuild by resetting the player chunk tracker
        _lastPlayerChunk  = new Vector3Int(int.MaxValue, 0, 0);
        return true;
    }

    private bool CheckPositionChanged()
    {
        var cur = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (cur == _lastPlayerChunk) return false;
        _lastPlayerChunk = cur;
        return true;
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    private void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            // Rebuild desired sets
            _desiredCoords.Clear();
            _desiredRegions.Clear();
            int maxR = MaxRadius;
            int vd   = AlignedViewDistance;

            // ── Pass 1: LOD 0 zone — iterate chunk offsets in [-vd+1, vd-1] ──────
            // Matches the original condition: Mathf.Max(|x|, |z|) < AlignedViewDistance.
            // No region logic needed here; the entire square is LOD 0 individual chunks.
            for (int x = -(vd - 1); x <= vd - 1; x++)
            for (int z = -(vd - 1); z <= vd - 1; z++)
            {
                int ax = _lastPlayerChunk.x + x;
                int az = _lastPlayerChunk.z + z;
                for (int y = 0; y < verticalChunks; y++)
                    _desiredCoords.Add(new Vector3Int(ax, y, az));
            }

            // ── Pass 2: LOD 1+ zones — one ring per LOD level ────────────────────
            // At LOD level L: regions are (1<<L)×(1<<L) chunks, covering chunk Chebyshev
            // distance [vd*(1<<(L-1)), vd*(1<<L)). AlignedViewDistance is a multiple of
            // (1<<lodLevels) so all zone boundaries land exactly on region edges —
            // no region ever straddles the LOD 0/1 boundary (no stitching needed).
            for (int lod = 1; lod <= lodLevels; lod++)
            {
                int hSize       = 1 << lod;
                int innerRadius = vd * (1 << (lod - 1));
                int outerRadius = vd * (1 << lod);
                int playerRX    = Mathf.FloorToInt(_lastPlayerChunk.x / (float)hSize);
                int playerRZ    = Mathf.FloorToInt(_lastPlayerChunk.z / (float)hSize);
                int maxRegionR  = outerRadius / hSize + 1;

                for (int rx = -maxRegionR; rx <= maxRegionR; rx++)
                for (int rz = -maxRegionR; rz <= maxRegionR; rz++)
                {
                    var regionCoord = new Vector3Int(playerRX + rx, lod, playerRZ + rz);
                    var baseChunk   = RegionBaseChunkCoord(regionCoord);
                    int nearestX    = Mathf.Clamp(_lastPlayerChunk.x, baseChunk.x, baseChunk.x + hSize - 1);
                    int nearestZ    = Mathf.Clamp(_lastPlayerChunk.z, baseChunk.z, baseChunk.z + hSize - 1);
                    int chebDist    = Mathf.Max(Mathf.Abs(nearestX - _lastPlayerChunk.x),
                                                Mathf.Abs(nearestZ - _lastPlayerChunk.z));

                    if (chebDist >= outerRadius) continue; // beyond this LOD's outer edge
                    if (chebDist <  innerRadius) continue; // inner edge — closer LOD covers it

                    _desiredRegions.Add(regionCoord);
                }
            }

            // Unload out-of-range chunk meshes
            _scratchUnloadC.Clear();
            foreach (var c in _chunkMeshes.Keys)
                if (!_desiredCoords.Contains(c)) _scratchUnloadC.Add(c);
            foreach (var c in _scratchUnloadC) UnloadChunk(c);

            // Unload out-of-range region meshes
            _scratchUnloadR.Clear();
            foreach (var r in _regionMeshes.Keys)
                if (!_desiredRegions.Contains(r)) _scratchUnloadR.Add(r);
            foreach (var r in _scratchUnloadR) UnloadRegion(r);

            // Evict chunk data
            int evictR = MaxRadius + 2;
            _scratchEvictC.Clear();
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - _lastPlayerChunk.x) > evictR ||
                    Mathf.Abs(c.z - _lastPlayerChunk.z) > evictR) _scratchEvictC.Add(c);
            foreach (var c in _scratchEvictC) _chunks.Remove(c);

            // Evict region data
            _scratchEvictR.Clear();
            foreach (var r in _regions.Keys)
                if (!_desiredRegions.Contains(r)) _scratchEvictR.Add(r);
            foreach (var r in _scratchEvictR) _regions.Remove(r);
        }

        // ── Request LOD 0 chunks ──────────────────────────────────────────────
        _scratchRequestC.Clear();
        foreach (var coord in _desiredCoords)
            if (!_chunkMeshes.ContainsKey(coord) && !_inFlight.Contains(coord))
                _scratchRequestC.Add(coord);
        var toRequestC = _scratchRequestC;

        var playerPos = player != null ? player.position : Vector3.zero;

        // Pre-compute squared distances once; Sort's comparator would otherwise call
        // ChunkCenterWorld on both sides of every comparison — O(n log n) redundant calls.
        _scratchChunkDist.Clear();
        foreach (var c in toRequestC)
            _scratchChunkDist[c] = ChunkCenterWorld(c).sqrMagnitude_To(playerPos);
        toRequestC.Sort((a, b) => _scratchChunkDist[a].CompareTo(_scratchChunkDist[b]));

        // Limit submissions per call so the closest chunks always get priority.
        // toRequestC is already distance-sorted, so breaking early drops far chunks.
        int lod0Budget = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int lod0Submitted = 0;

        foreach (var coord in toRequestC)
        {
            if (lod0Submitted >= lod0Budget) break;
            RequestChunk(coord, buildMesh: true);
            lod0Submitted++;
        }

        // ── Process desired regions (sorted by distance) ──────────────────────────
        _scratchRegionsToProc.Clear();
        var regionsToProcess = _scratchRegionsToProc;
        foreach (var regionCoord in _desiredRegions)
        {
            // Step is invariant for a given regionCoord (1 << regionCoord.y),
            // so any existing mesh is already at the correct step.
            if (_regionInFlight.Contains(regionCoord)) continue;
            if (_regionMeshes.ContainsKey(regionCoord)) continue;

            regionsToProcess.Add(regionCoord);
        }

        // Nearest regions first — pre-compute distances to avoid redundant calls in Sort.
        _scratchRegionDist.Clear();
        foreach (var r in regionsToProcess)
            _scratchRegionDist[r] = RegionCenterWorld(r).sqrMagnitude_To(playerPos);
        regionsToProcess.Sort((a, b) => _scratchRegionDist[a].CompareTo(_scratchRegionDist[b]));

        // Region data requests use the other half of the budget.
        // Without a cap, a single UpdateLoadedChunks call could submit 50 regions × 48 chunks
        // = 2400 data tasks, starving LOD 0 mesh tasks of semaphore slots.
        int regionDataBudget = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int regionDataSubmitted = 0;

        foreach (var regionCoord in regionsToProcess)
        {
            int hSize = 1 << regionCoord.y; // chunks per region side at this LOD level

            if (!_regions.TryGetValue(regionCoord, out var region))
            {
                // First visit: create region and immediately feed all already-available chunk data.
                region = new RegionData(verticalChunks, hSize);
                _regions[regionCoord] = region;
            }
            else if (regionDataSubmitted >= regionDataBudget)
            {
                // Return visit with no budget: skip the inner chunk loop.
                // TryFeedChunkIntoRegion (called from ApplyReadyChunks) handles feeding as
                // data tasks complete, and triggers RequestRegionMesh when the region is full.
                if (region.IsComplete) RequestRegionMesh(regionCoord, region);
                continue;
            }

            // Feed already-available chunk data; request the rest (within budget)
            var baseChunk = RegionBaseChunkCoord(regionCoord);
            for (int lcx = 0; lcx < hSize; lcx++)
            for (int lcy = 0; lcy < verticalChunks; lcy++)
            for (int lcz = 0; lcz < hSize; lcz++)
            {
                if (region.HasChunk(lcx, lcy, lcz)) continue;
                var chunkCoord = new Vector3Int(baseChunk.x + lcx, lcy, baseChunk.z + lcz);
                if (_chunks.TryGetValue(chunkCoord, out var existing))
                    region.SetChunk(lcx, lcy, lcz, existing);
                else if (!_inFlight.Contains(chunkCoord) && regionDataSubmitted < regionDataBudget)
                {
                    RequestChunk(chunkCoord, buildMesh: false);
                    regionDataSubmitted++;
                }
            }

            if (region.IsComplete)
                RequestRegionMesh(regionCoord, region);
        }

        // If either budget was fully spent, there are likely more items waiting.
        // Update() will call UpdateLoadedChunks(false) again once tasks complete.
        _needsMoreRequests = (lod0Submitted >= lod0Budget) || (regionDataSubmitted >= regionDataBudget);
    }

    // ── LOD 0 chunk tasks ─────────────────────────────────────────────────────

    private void RequestChunk(Vector3Int coord, bool buildMesh)
    {
        _inFlight.Add(coord);
        var existing = GetChunk(coord);

        if (existing != null)
        {
            // Data already present — skip generation, build mesh directly if needed.
            if (!buildMesh)
            {
                // Re-enqueue so ApplyReadyChunks can call TryFeedChunkIntoRegion.
                _inFlight.Remove(coord);
                _readyQueue.Enqueue(new ChunkBuildResult(coord, existing, null));
                return;
            }

            var neighbours = ChunkRenderer.FetchNeighbours(coord, GetChunk);
            var token = _generationCts.Token;
            Task.Run(async () =>
            {
                try { await _semaphore.WaitAsync(token); }
                catch (OperationCanceledException) { _cancelledCoords.Enqueue(coord); return; }
                try
                {
                    var mesh = ChunkRenderer.BuildMeshData(existing, neighbours, 0);
                    _readyQueue.Enqueue(new ChunkBuildResult(coord, existing, mesh));
                }
                finally { _semaphore.Release(); }
            });
            return;
        }

        // Schedule a Burst-compiled terrain generation job.
        // Jobs are scheduled on the main thread and run on Unity's job worker threads.
        // ProcessCompletedGenerationJobs() polls IsCompleted each Update.
        int size   = PaletteChunk.Size;
        var blocks = new NativeArray<byte>(size * size * size, Allocator.Persistent);
        var job    = new GenerateChunkJob
        {
            Settings   = GetTerrainSettings(),
            ChunkCoord = new int3(coord.x, coord.y, coord.z),
            LowDetail  = !buildMesh,
            Blocks     = blocks,
        };
        var handle     = job.Schedule();
        var neighbours2 = buildMesh ? ChunkRenderer.FetchNeighbours(coord, GetChunk) : null;

        _pendingGenerationJobs.Add(new PendingGenerationJob
        {
            Coord      = coord,
            Handle     = handle,
            Blocks     = blocks,
            BuildMesh  = buildMesh,
            Neighbours = neighbours2,
            Discarded  = false,
        });
    }

    // Returns true if any new GameObjects were added to the scene this frame.
    private bool ApplyReadyChunks()
    {
        int applied = 0;
        bool anyAdded = false;
        while (applied < maxApplyPerFrame && _readyQueue.TryDequeue(out var result))
        {
            _inFlight.Remove(result.Coord);
            _chunks[result.Coord] = result.Chunk;

            // LOD 0 mesh — drawn via Graphics.DrawMesh each frame.
            // Store null as a sentinel for empty chunks (no visible faces) so they are
            // not re-requested on every UpdateLoadedChunks call.  DrawAllMeshes already
            // skips null entries.  Coords NOT in _desiredCoords are discarded outright.
            if (_desiredCoords.Contains(result.Coord))
            {
                if (!_chunkMeshes.ContainsKey(result.Coord))
                {
                    var mesh = result.MeshData != null ? ChunkRenderer.CreateMesh(result.MeshData) : null;
                    _chunkMeshes[result.Coord] = mesh; // null = empty chunk sentinel
                    anyAdded = true;
                }
                else
                {
                    result.MeshData?.Discard();
                }
            }
            else
            {
                result.MeshData?.Discard();
            }

            // Always feed this chunk's data into its region — boundary chunks (at exactly
            // viewDistance) are processed as LOD 0 above but also belong to an adjacent region.
            // Without this, the region stays incomplete until the next UpdateLoadedChunks call.
            TryFeedChunkIntoRegion(result.Coord, result.Chunk);

            applied++;
        }
        return anyAdded;
    }

    // ── Burst job completion ──────────────────────────────────────────────────

    /// <summary>
    /// Called every Update. Drains any Burst generation jobs whose JobHandle
    /// is now complete, builds the PaletteChunk from the NativeArray result,
    /// and either enqueues a data-only result or kicks off an async mesh build.
    /// Discarded jobs (player moved) are disposed without enqueueing.
    ///
    /// Completed jobs are sorted closest-first before processing so the per-frame
    /// budget always goes to the nearest chunks, not whichever happened to finish last.
    /// </summary>
    private void ProcessCompletedGenerationJobs()
    {
        // Collect indices of all completed jobs.
        _scratchCompletedIdx.Clear();
        for (int i = 0; i < _pendingGenerationJobs.Count; i++)
            if (_pendingGenerationJobs[i].Handle.IsCompleted)
                _scratchCompletedIdx.Add(i);

        if (_scratchCompletedIdx.Count == 0) return;

        // Sort: non-discarded closest-first so the budget always goes to the nearest chunks.
        // Discarded jobs are partitioned to the end — they are always drained (no budget cost).
        var pc = _lastPlayerChunk;
        _scratchCompletedIdx.Sort((a, b) =>
        {
            var ja = _pendingGenerationJobs[a];
            var jb = _pendingGenerationJobs[b];
            if (ja.Discarded != jb.Discarded) return ja.Discarded ? 1 : -1; // discarded last
            int da = Mathf.Max(Mathf.Abs(ja.Coord.x - pc.x), Mathf.Abs(ja.Coord.z - pc.z));
            int db = Mathf.Max(Mathf.Abs(jb.Coord.x - pc.x), Mathf.Abs(jb.Coord.z - pc.z));
            return da.CompareTo(db);
        });

        int tasksBudget = maxApplyPerFrame;
        _scratchRemoveIdx.Clear();

        foreach (int idx in _scratchCompletedIdx)
        {
            var pj = _pendingGenerationJobs[idx];

            // Discarded jobs are cheap (Dispose + enqueue) — always drain regardless of budget.
            // Only non-discarded jobs that kick off background tasks count against the budget.
            if (!pj.Discarded && tasksBudget <= 0) continue;

            pj.Handle.Complete(); // Required even when IsCompleted — finalises the job
            _scratchRemoveIdx.Add(idx);

            if (pj.Discarded)
            {
                pj.Blocks.Dispose();
                _cancelledCoords.Enqueue(pj.Coord); // cleans up _inFlight in Update's drain loop
                continue;
            }

            tasksBudget--;

            // Rent a pooled byte[4096] from ArrayPool to avoid a managed allocation per job.
            // CopyTo is equivalent to ToArray() but writes into the pre-existing buffer.
            // Dispose NativeArray immediately to free unmanaged memory.
            var blocksCopy = ArrayPool<byte>.Shared.Rent(PaletteChunk.Size * PaletteChunk.Size * PaletteChunk.Size);
            pj.Blocks.CopyTo(blocksCopy);
            pj.Blocks.Dispose();

            // _inFlight cleanup is intentionally deferred to ApplyReadyChunks so the coord
            // stays in _inFlight while the background task is running, preventing duplicate
            // generation requests for the same coord.

            if (!pj.BuildMesh)
            {
                var dataCoord  = pj.Coord;
                var dataBlocks = blocksCopy;
                Task.Run(() =>
                {
                    try
                    {
                        var chunk = BuildPaletteChunkFromBlocks(dataBlocks);
                        _readyQueue.Enqueue(new ChunkBuildResult(dataCoord, chunk, null));
                    }
                    catch (Exception e) { UnityEngine.Debug.LogError($"[VoxelWorld] Data build {dataCoord}: {e.Message}"); _cancelledCoords.Enqueue(dataCoord); }
                    finally { ArrayPool<byte>.Shared.Return(dataBlocks); }
                });
                continue;
            }

            // Kick off chunk build + mesh build on a background thread (semaphore-limited, cancellable).
            var coord      = pj.Coord;
            var neighbours = pj.Neighbours;
            var token      = _generationCts.Token;
            var jobBlocks  = blocksCopy;
            Task.Run(async () =>
            {
                // BuildPaletteChunkFromBlocks uses jobBlocks then returns it to the pool via finally.
                // The finally always runs — even when catch does an early return — so one Return is enough.
                PaletteChunk chunk;
                try { chunk = BuildPaletteChunkFromBlocks(jobBlocks); }
                catch (Exception e) { UnityEngine.Debug.LogError($"[VoxelWorld] Palette build {coord}: {e.Message}"); _cancelledCoords.Enqueue(coord); return; }
                finally { ArrayPool<byte>.Shared.Return(jobBlocks); }

                try { await _semaphore.WaitAsync(token); }
                catch (OperationCanceledException) { _cancelledCoords.Enqueue(coord); return; }

                try
                {
                    var mesh = ChunkRenderer.BuildMeshData(chunk, neighbours, 0);
                    _readyQueue.Enqueue(new ChunkBuildResult(coord, chunk, mesh));
                }
                catch (Exception e) { UnityEngine.Debug.LogError($"[VoxelWorld] Mesh build {coord}: {e.Message}"); _cancelledCoords.Enqueue(coord); }
                finally { _semaphore.Release(); }
            });
        }

        // Remove processed entries. Sort descending so higher indices are removed first,
        // keeping all lower indices valid throughout.
        _scratchRemoveIdx.Sort((a, b) => b.CompareTo(a));
        foreach (int idx in _scratchRemoveIdx)
            _pendingGenerationJobs.RemoveAt(idx);
    }

    private static PaletteChunk BuildPaletteChunkFromBlocks(byte[] blocks)
    {
        var chunk = new PaletteChunk();
        // Pre-seed all terrain types so GrowIfNeeded never repacks 4096 voxels mid-fill.
        chunk.SeedPalette(BlockType.Stone, BlockType.Dirt, BlockType.Grass,
                          BlockType.Sand,  BlockType.Water, BlockType.Snow);
        // BulkLoad is ~3× faster than 4096 individual SetBlock calls: skips the
        // palette-registration branch and GrowIfNeeded check on every voxel.
        chunk.BulkLoad(blocks);
        return chunk;
    }

    private void TryFeedChunkIntoRegion(Vector3Int coord, PaletteChunk chunk)
    {
        // Determine which LOD level zone this chunk sits in by Chebyshev distance.
        int dist     = Mathf.Max(Mathf.Abs(coord.x - _lastPlayerChunk.x),
                                  Mathf.Abs(coord.z - _lastPlayerChunk.z));
        int lod      = 0;
        int boundary = AlignedViewDistance;
        while (lod < lodLevels && dist >= boundary) { lod++; boundary *= 2; }
        if (lod == 0) return; // LOD 0 chunk — not part of any region

        var regionCoord = ChunkToRegionCoord(coord, lod);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        region.SetChunk(local.x, local.y, local.z, chunk);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionMeshes.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region);
    }

    // ── LOD 1+ region tasks ───────────────────────────────────────────────────

    private void RequestRegionMesh(Vector3Int regionCoord, RegionData region)
    {
        _regionInFlight.Add(regionCoord);
        int step = 1 << regionCoord.y; // step is invariant: 2/4/8 for LOD 1/2/3

        // Only horizontal neighbours — ±X and ±Z. Vertical (±Y) entries stay null since
        // regions span the full world height. Note: adding dirs[2/3] would change the lod
        // component of the coord, returning a different-level region which is not a neighbour.
        var neighbours = new RegionData[6];
        var dirs = ChunkRenderer.RegionNeighbourDirs;
        neighbours[0] = GetRegion(regionCoord + dirs[0]);
        neighbours[1] = GetRegion(regionCoord + dirs[1]);
        neighbours[4] = GetRegion(regionCoord + dirs[4]);
        neighbours[5] = GetRegion(regionCoord + dirs[5]);

        var token = _generationCts.Token; // snapshot — immune to later CTS replacement
        Task.Run(async () =>
        {
            try { await _semaphore.WaitAsync(token); }
            catch (OperationCanceledException)
            {
                // Notify main thread to remove this region from _regionInFlight so it
                // can be re-requested at the new position if still desired.
                _cancelledRegionCoords.Enqueue(regionCoord);
                return;
            }
            try
            {
                var mesh = ChunkRenderer.BuildRegionMeshData(region, neighbours, step);
                _regionReadyQueue.Enqueue(new RegionBuildResult(regionCoord, mesh));
            }
            finally { _semaphore.Release(); }
        });
    }

    private bool ApplyReadyRegions()
    {
        int applied = 0;
        bool anyAdded = false;
        while (applied < maxApplyPerFrame && _regionReadyQueue.TryDequeue(out var result))
        {
            _regionInFlight.Remove(result.RegionCoord);

            if (!_regionMeshes.ContainsKey(result.RegionCoord)
                && _desiredRegions.Contains(result.RegionCoord))
            {
                var mesh = ChunkRenderer.CreateMesh(result.MeshData); // O(1) pointer handoff
                _regionMeshes[result.RegionCoord] = mesh; // null = empty region sentinel
                anyAdded = true;
            }
            else
            {
                result.MeshData?.Discard(); // region no longer desired — free unmanaged memory
            }

            applied++;
        }
        return anyAdded;
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (_chunkMeshes.TryGetValue(coord, out var mesh)) { Destroy(mesh); _chunkMeshes.Remove(coord); }
    }

    private void UnloadRegion(Vector3Int r)
    {
        if (_regionMeshes.TryGetValue(r, out var mesh)) { Destroy(mesh); _regionMeshes.Remove(r); }
    }

    // ── Draw meshes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Submits all loaded chunk and region meshes to Unity's renderer each frame via
    /// Graphics.DrawMesh. No GameObjects are used. Unity automatically frustum-culls
    /// each mesh using its pre-computed bounds (set during background mesh build).
    /// </summary>
    private void DrawAllMeshes()
    {
        if (chunkMaterial == null) return;
        var localToWorld = transform.localToWorldMatrix;

        foreach (var kvp in _chunkMeshes)
        {
            if (kvp.Value == null) continue;
            Graphics.DrawMesh(kvp.Value,
                              localToWorld * Matrix4x4.Translate(ChunkToWorldPos(kvp.Key)),
                              chunkMaterial, gameObject.layer);
        }

        foreach (var kvp in _regionMeshes)
        {
            if (kvp.Value == null) continue;
            Graphics.DrawMesh(kvp.Value,
                              localToWorld * Matrix4x4.Translate(RegionWorldPos(kvp.Key)),
                              chunkMaterial, gameObject.layer);
        }
    }

    // ── Terrain generation ────────────────────────────────────────────────────

    public TerrainSettings GetTerrainSettings() => new TerrainSettings
    {
        seaLevel       = seaLevel,
        baseHeight     = baseHeight,
        plainsHeight   = plainsHeight,
        mountainHeight = mountainHeight,
        oceanDepth     = oceanDepth,
        noiseScale     = noiseScale,
        octaves        = octaves,
        persistence    = persistence,
        lacunarity     = lacunarity,
        biomeScale     = biomeScale,
        warpStrength   = warpStrength,
        warpScale      = warpScale,
        dirtDepth      = dirtDepth,
        sandBeachWidth = sandBeachWidth,
        snowAltitude   = snowAltitude,
    };

    private static PaletteChunk GenerateChunkData(Vector3Int coord, in TerrainSettings s,
                                                   bool lowDetail = false)
    {
        var chunk   = new PaletteChunk();
        chunk.SeedPalette(BlockType.Stone, BlockType.Dirt, BlockType.Grass,
                          BlockType.Sand,  BlockType.Water, BlockType.Snow);
        int offsetX = coord.x * PaletteChunk.Size;
        int offsetY = coord.y * PaletteChunk.Size;
        int offsetZ = coord.z * PaletteChunk.Size;
        int chunkTop = offsetY + PaletteChunk.Size - 1;

        // ── Fast-path: chunk is entirely above or below terrain ───────────────
        // Sample the 4 corners (4 noise calls) to get a conservative surface range.
        // This skips the full 256-column pass for the majority of aerial and deep chunks.
        int minSurface = int.MaxValue, maxSurface = int.MinValue;
        for (int cz = 0; cz <= PaletteChunk.Size; cz += PaletteChunk.Size)
        for (int cx = 0; cx <= PaletteChunk.Size; cx += PaletteChunk.Size)
        {
            int h = TerrainGenerator.GetSurface(offsetX + cx, offsetZ + cz, s, lowDetail);
            if (h < minSurface) minSurface = h;
            if (h > maxSurface) maxSurface = h;
        }

        // All voxels below min surface minus dirt depth → solid stone
        if (chunkTop < minSurface - s.dirtDepth)
        {
            chunk.FillAll(BlockType.Stone);
            return chunk;
        }

        // All voxels above max surface and above sea level → empty air (default)
        if (offsetY > maxSurface && offsetY > s.seaLevel)
            return chunk;

        // ── Full per-column pass ──────────────────────────────────────────────
        for (int z = 0; z < PaletteChunk.Size; z++)
        for (int x = 0; x < PaletteChunk.Size; x++)
        {
            int surface = TerrainGenerator.GetSurface(offsetX + x, offsetZ + z, s, lowDetail);

            for (int y = 0; y < PaletteChunk.Size; y++)
            {
                byte block = TerrainGenerator.GetBlock(offsetX + x, offsetY + y, offsetZ + z, surface, s);
                if (block != BlockType.Air)
                    chunk.SetBlock(x, y, z, block);
            }
        }
        return chunk;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public PaletteChunk GetChunk(Vector3Int coord) =>
        _chunks.TryGetValue(coord, out var c) ? c : null;

    public RegionData GetRegion(Vector3Int regionCoord) =>
        _regions.TryGetValue(regionCoord, out var r) ? r : null;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Vector3Int WorldToChunkCoord(Vector3 p)
    {
        int s = PaletteChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / s), 0, Mathf.FloorToInt(p.z / s));
    }

    /// <summary>
    /// Maps a chunk coord to the region coord at the given LOD level.
    /// LOD level is stored in regionCoord.y so all levels share the same dictionaries.
    /// </summary>
    private static Vector3Int ChunkToRegionCoord(Vector3Int chunk, int lodLevel)
    {
        int size = 1 << lodLevel;
        return new Vector3Int(
            Mathf.FloorToInt(chunk.x / (float)size),
            lodLevel,
            Mathf.FloorToInt(chunk.z / (float)size));
    }

    /// <summary>Returns the minimum-corner chunk coord of a region (uses regionCoord.y as LOD level).</summary>
    private static Vector3Int RegionBaseChunkCoord(Vector3Int regionCoord)
    {
        int size = 1 << regionCoord.y;
        return new Vector3Int(regionCoord.x * size, 0, regionCoord.z * size);
    }

    private static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = PaletteChunk.Size; return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    private static Vector3 RegionWorldPos(Vector3Int regionCoord)
    {
        float s = PaletteChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s, 0, regionCoord.z * s);
    }

    private static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = PaletteChunk.Size;
        return new Vector3(c.x * s + s * 0.5f, c.y * s + s * 0.5f, c.z * s + s * 0.5f);
    }

    private static Vector3 RegionCenterWorld(Vector3Int regionCoord)
    {
        float s = PaletteChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s + s * 0.5f, 0, regionCoord.z * s + s * 0.5f);
    }

    private static Bounds ChunkBounds(Vector3Int c) =>
        new Bounds(ChunkCenterWorld(c), Vector3.one * PaletteChunk.Size);

    private static Bounds RegionBounds(Vector3Int regionCoord)
    {
        float s = PaletteChunk.Size * (1 << regionCoord.y);
        return new Bounds(RegionCenterWorld(regionCoord), new Vector3(s, s, s));
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
