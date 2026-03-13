using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Two-tier LOD rendering:
///
///   LOD 0 (within viewDistance):
///     Individual 16³ chunk meshes, full-detail. Drawn via Graphics.DrawMesh — no GameObjects.
///
///   LOD 1+ (beyond viewDistance, within MaxRadius):
///     4×vc×4 chunk groups (Regions) rendered as ONE mesh per group via Graphics.DrawMesh.
///     Step = 4 voxels/cell at LOD 1, 8 at LOD 2, 16 at LOD 3.
///     Reduces draw calls from ~4096 to ~256 at the outer ring.
///
/// Example: viewDistance=8, lodLevels=2 → 8→16→32 chunk radii = 512 m max.
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

    [Header("Culling")]
    public int   frustumBypassRadius     = 2;
    [Range(0.9f, 1f)]
    public float cameraRotationThreshold = 0.98f;

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>
    /// viewDistance rounded up to the nearest RegionData.HSize (4) multiple.
    /// Ensures the LOD 0 / region boundary always falls on a region edge,
    /// preventing a region mesh from overlapping with individual chunk GOs.
    /// e.g. viewDistance=6 → AlignedViewDistance=8, viewDistance=8 → 8.
    /// </summary>
    public int AlignedViewDistance
    {
        get { int h = RegionData.HSize; return ((viewDistance + h - 1) / h) * h; }
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
        public readonly Vector3Int   Coord;
        public readonly PaletteChunk Chunk;
        public readonly MeshData     MeshData; // null = data-only (for region use)
        public ChunkBuildResult(Vector3Int c, PaletteChunk ch, MeshData m)
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
    private readonly Dictionary<Vector3Int, int>          _regionStep    = new();
    private readonly HashSet<Vector3Int>                  _desiredRegions   = new();
    private readonly HashSet<Vector3Int>                  _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult>   _regionReadyQueue = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int RegionCoord;
        public readonly MeshData   MeshData;
        public readonly int        Step;
        public RegionBuildResult(Vector3Int r, MeshData m, int s) { RegionCoord = r; MeshData = m; Step = s; }
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

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _lastCameraForward = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
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
        bool rotChanged  = CheckRotationChanged();

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

        if (posChanged || rotChanged || lodChanged)
        {
            // Cancel all tasks waiting for the semaphore — they're for the old position
            // and would block new high-priority tasks for the current position.
            if (posChanged || lodChanged)
            {
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
            }
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

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk   = new Vector3Int(int.MaxValue, 0, 0);
    private Vector3    _lastCameraForward = Vector3.forward;
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

    private bool CheckRotationChanged()
    {
        var cam = Camera.main;
        if (cam == null) return false;
        var fwd = cam.transform.forward;
        if (Vector3.Dot(fwd, _lastCameraForward) >= cameraRotationThreshold) return false;
        _lastCameraForward = fwd;
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

            // ── Pass 2: LOD 1+ zone — iterate region coords directly ──────────────
            // Iterates (2*maxRegionR+1)² regions instead of (2*maxR+1)² chunk offsets,
            // giving ~7–11× fewer iterations. RegionContainsLod0Chunk is now called once
            // per region (boundary detection) rather than once per chunk offset.
            int h            = RegionData.HSize;
            int playerRegionX = Mathf.FloorToInt(_lastPlayerChunk.x / (float)h);
            int playerRegionZ = Mathf.FloorToInt(_lastPlayerChunk.z / (float)h);
            int maxRegionR    = maxR / h + 1; // +1 to cover partial regions at the boundary

            for (int rx = -maxRegionR; rx <= maxRegionR; rx++)
            for (int rz = -maxRegionR; rz <= maxRegionR; rz++)
            {
                var regionCoord = new Vector3Int(playerRegionX + rx, 0, playerRegionZ + rz);
                var baseChunk   = RegionBaseChunkCoord(regionCoord);

                // Exclude regions whose nearest chunk exceeds MaxRadius.
                int nearestX = Mathf.Clamp(_lastPlayerChunk.x, baseChunk.x, baseChunk.x + h - 1);
                int nearestZ = Mathf.Clamp(_lastPlayerChunk.z, baseChunk.z, baseChunk.z + h - 1);
                if (Mathf.Max(Mathf.Abs(nearestX - _lastPlayerChunk.x),
                              Mathf.Abs(nearestZ - _lastPlayerChunk.z)) > maxR) continue;

                if (RegionContainsLod0Chunk(regionCoord))
                {
                    // Boundary region: render its chunks as individual LOD 0 GOs to
                    // prevent overlap with the adjacent region mesh.
                    for (int lcx = 0; lcx < h; lcx++)
                    for (int lcy = 0; lcy < verticalChunks; lcy++)
                    for (int lcz = 0; lcz < h; lcz++)
                        _desiredCoords.Add(new Vector3Int(baseChunk.x + lcx, lcy, baseChunk.z + lcz));
                }
                else
                {
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
        var chunkDistSq = new Dictionary<Vector3Int, float>(toRequestC.Count);
        foreach (var c in toRequestC)
            chunkDistSq[c] = ChunkCenterWorld(c).sqrMagnitude_To(playerPos);
        toRequestC.Sort((a, b) => chunkDistSq[a].CompareTo(chunkDistSq[b]));

        Plane[] planes = null;
        var cam = Camera.main;
        if (cam != null) planes = GeometryUtility.CalculateFrustumPlanes(cam);
        float bypassSq = (frustumBypassRadius * PaletteChunk.Size) * (frustumBypassRadius * (float)PaletteChunk.Size);

        // Limit submissions per call so the closest in-frustum chunks always get priority.
        // toRequestC is already distance-sorted, so breaking early drops far chunks.
        int lod0Budget = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int lod0Submitted = 0;

        foreach (var coord in toRequestC)
        {
            if (lod0Submitted >= lod0Budget) break;
            var   center = ChunkCenterWorld(coord);
            float distSq = (center - playerPos).sqrMagnitude;
            if (distSq <= bypassSq || planes == null ||
                GeometryUtility.TestPlanesAABB(planes, ChunkBounds(coord)))
            {
                RequestChunk(coord, buildMesh: true);
                lod0Submitted++;
            }
        }

        // ── Process desired regions (sorted by distance, frustum culled) ─────────
        _scratchRegionsToProc.Clear();
        var regionsToProcess = _scratchRegionsToProc;
        foreach (var regionCoord in _desiredRegions)
        {
            // Skip if already rendered at correct step or being built
            if (_regionInFlight.Contains(regionCoord)) continue;
            if (_regionMeshes.ContainsKey(regionCoord))
            {
                int wantedStep = GetRegionStep(regionCoord);
                if (_regionStep.TryGetValue(regionCoord, out int cur) && cur == wantedStep) continue;
            }

            // Frustum cull — regions are always beyond the bypass radius so no bypass check needed
            if (planes != null && !GeometryUtility.TestPlanesAABB(planes, RegionBounds(regionCoord)))
                continue;

            regionsToProcess.Add(regionCoord);
        }

        // Nearest regions first — pre-compute distances to avoid redundant calls in Sort.
        var regionDistSq = new Dictionary<Vector3Int, float>(regionsToProcess.Count);
        foreach (var r in regionsToProcess)
            regionDistSq[r] = RegionCenterWorld(r).sqrMagnitude_To(playerPos);
        regionsToProcess.Sort((a, b) => regionDistSq[a].CompareTo(regionDistSq[b]));

        // Region data requests use the other half of the budget.
        // Without a cap, a single UpdateLoadedChunks call could submit 50 regions × 48 chunks
        // = 2400 data tasks, starving LOD 0 mesh tasks of semaphore slots.
        int regionDataBudget = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int regionDataSubmitted = 0;

        foreach (var regionCoord in regionsToProcess)
        {
            int wantedStep = GetRegionStep(regionCoord);

            // Re-mesh if LOD step changed
            if (_regionMeshes.ContainsKey(regionCoord))
                UnloadRegion(regionCoord);

            if (!_regions.TryGetValue(regionCoord, out var region))
            {
                region = new RegionData(verticalChunks);
                _regions[regionCoord] = region;
            }

            // Feed already-available chunk data; request the rest (within budget)
            var baseChunk = RegionBaseChunkCoord(regionCoord);
            for (int lcx = 0; lcx < RegionData.HSize; lcx++)
            for (int lcy = 0; lcy < verticalChunks; lcy++)
            for (int lcz = 0; lcz < RegionData.HSize; lcz++)
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
                RequestRegionMesh(regionCoord, region, wantedStep);
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

            if (result.MeshData != null)
            {
                // LOD 0 mesh (no GameObject — drawn via Graphics.DrawMesh each frame)
                if (!_chunkMeshes.ContainsKey(result.Coord) && _desiredCoords.Contains(result.Coord))
                {
                    var mesh = ChunkRenderer.CreateMesh(result.MeshData);
                    if (mesh != null) _chunkMeshes[result.Coord] = mesh;
                    anyAdded = true;
                }
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

            // Copy NativeArray to managed byte[] on the main thread (fast memcpy of 4096 bytes),
            // then dispose the NativeArray immediately to free unmanaged memory.
            // BuildPaletteChunkFromBlocks runs on a background thread so the main thread
            // is not blocked by 4096 SetBlock calls × N completed jobs per frame.
            var blocksCopy = pj.Blocks.ToArray();
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
                    catch { _cancelledCoords.Enqueue(dataCoord); }
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
                PaletteChunk chunk;
                try { chunk = BuildPaletteChunkFromBlocks(jobBlocks); }
                catch { _cancelledCoords.Enqueue(coord); return; }

                try { await _semaphore.WaitAsync(token); }
                catch (OperationCanceledException) { _cancelledCoords.Enqueue(coord); return; }

                try
                {
                    var mesh = ChunkRenderer.BuildMeshData(chunk, neighbours, 0);
                    _readyQueue.Enqueue(new ChunkBuildResult(coord, chunk, mesh));
                }
                catch { _cancelledCoords.Enqueue(coord); }
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
        int size  = PaletteChunk.Size;
        for (int z = 0; z < size; z++)
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            byte b = blocks[x + y * size + z * size * size];
            if (b != BlockType.Air)
                chunk.SetBlock(x, y, z, b);
        }
        return chunk;
    }

    private void TryFeedChunkIntoRegion(Vector3Int coord, PaletteChunk chunk)
    {
        var regionCoord = ChunkToRegionCoord(coord);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        region.SetChunk(local.x, local.y, local.z, chunk);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionMeshes.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region, GetRegionStep(regionCoord));
    }

    // ── LOD 1+ region tasks ───────────────────────────────────────────────────

    private void RequestRegionMesh(Vector3Int regionCoord, RegionData region, int step)
    {
        _regionInFlight.Add(regionCoord);
        var neighbours = new RegionData[6];
        var dirs = ChunkRenderer.RegionNeighbourDirs;
        neighbours[0] = GetRegion(regionCoord + dirs[0]);
        neighbours[1] = GetRegion(regionCoord + dirs[1]);
        // [2] and [3] (±Y) stay null — regions span full height
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
                _regionReadyQueue.Enqueue(new RegionBuildResult(regionCoord, mesh, step));
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
                var mesh = ChunkRenderer.CreateMesh(result.MeshData);
                if (mesh != null) _regionMeshes[result.RegionCoord] = mesh;
                _regionStep[result.RegionCoord] = result.Step;
                anyAdded = true;
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
        _regionStep.Remove(r);
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

    private static Vector3Int ChunkToRegionCoord(Vector3Int c) =>
        new Vector3Int(Mathf.FloorToInt(c.x / (float)RegionData.HSize), 0,
                       Mathf.FloorToInt(c.z / (float)RegionData.HSize));

    private static Vector3Int RegionBaseChunkCoord(Vector3Int r) =>
        new Vector3Int(r.x * RegionData.HSize, 0, r.z * RegionData.HSize);

    private static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = PaletteChunk.Size; return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    private static Vector3 RegionWorldPos(Vector3Int r)
    {
        float s = PaletteChunk.Size * RegionData.HSize; return new Vector3(r.x * s, 0, r.z * s);
    }

    private static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = PaletteChunk.Size;
        return new Vector3(c.x * s + s * 0.5f, c.y * s + s * 0.5f, c.z * s + s * 0.5f);
    }

    private static Vector3 RegionCenterWorld(Vector3Int r)
    {
        float hs = PaletteChunk.Size * RegionData.HSize;
        return new Vector3(r.x * hs + hs * 0.5f, 0, r.z * hs + hs * 0.5f);
    }

    private static Bounds ChunkBounds(Vector3Int c) =>
        new Bounds(ChunkCenterWorld(c), Vector3.one * PaletteChunk.Size);

    private static Bounds RegionBounds(Vector3Int r)
    {
        float hs = PaletteChunk.Size * RegionData.HSize;
        return new Bounds(RegionCenterWorld(r), new Vector3(hs, hs, hs));
    }

    /// <summary>
    /// Returns true if the region's nearest chunk to the player is within the LOD 0 zone.
    /// Such regions straddle the LOD boundary and must be rendered as individual LOD 0
    /// chunks rather than a single region mesh, to avoid overlap with adjacent LOD 0 chunks.
    /// </summary>
    private bool RegionContainsLod0Chunk(Vector3Int regionCoord)
    {
        int h  = RegionData.HSize;
        int px = _lastPlayerChunk.x, pz = _lastPlayerChunk.z;
        int nx = Mathf.Clamp(px, regionCoord.x * h, regionCoord.x * h + h - 1);
        int nz = Mathf.Clamp(pz, regionCoord.z * h, regionCoord.z * h + h - 1);
        return Mathf.Max(Mathf.Abs(nx - px), Mathf.Abs(nz - pz)) < AlignedViewDistance;
    }

    private int GetRegionStep(Vector3Int regionCoord)
    {
        var baseChunk = RegionBaseChunkCoord(regionCoord);
        int dist = Mathf.Max(Mathf.Abs(baseChunk.x - _lastPlayerChunk.x),
                             Mathf.Abs(baseChunk.z - _lastPlayerChunk.z));
        int radius = AlignedViewDistance; int step = 4;
        for (int lod = 1; lod <= lodLevels; lod++)
        {
            if (dist <= radius * 2) return step;
            radius *= 2; step *= 2;
        }
        return step;
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
