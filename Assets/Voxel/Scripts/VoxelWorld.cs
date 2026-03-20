using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;
using UnityEngine.Profiling;

/// <summary>
/// Variable-size LOD rendering:
///
///   LOD 0 (within AlignedViewDistance):
///     Individual 16³ chunk meshes, full-detail. Drawn via Graphics.DrawMesh.
///     Pipeline: GenerateChunkJob (Burst) → BuildChunkMeshJob (Burst, chained dependency).
///     No Task.Run, no SemaphoreSlim. Unity job system saturates all CPU cores.
///
///   LOD L (ring L, beyond LOD L-1):
///     Regions of (1&lt;&lt;L)×(1&lt;&lt;L) chunks, sampled at step (1&lt;&lt;L) voxels/cell.
///     Pipeline: region data assembled from VoxelChunk.Blocks → Task.Run mesh build.
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
    public int   forestHeight   = 28;
    public int   mountainHeight = 80;
    public int   tundraHeight   = 12;
    public int   desertDuneHeight = 7;
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

    [Header("Terrain — Biomes")]
    public float tempScale               = 0.003f;
    public float humidityScale           = 0.004f;
    public float mountainBiomeScale      = 0.003f;
    [Range(0f, 1f)]
    public float mountainBiomeThreshold  = 0.62f;

    [Header("Terrain — Domain Warp")]
    public float warpStrength = 25f;
    public float warpScale    = 0.005f;

    [Header("Terrain — Block Layers")]
    public int dirtDepth        = 3;
    public int sandBeachWidth   = 3;
    public int snowAltitude     = 75;
    public int desertSandDepth  = 6;
    public int tundraFrozenDepth = 3;

    [Header("Rendering")]
    public Material chunkMaterial;
    public Material transparentMaterial;

    [Header("Performance")]
    [Tooltip("Max LOD 0 chunk pipelines (terrain+mesh jobs) in flight at once. " +
             "Higher = more parallelism but more memory. Recommend: CPU cores × 2.")]
    public int maxPipelinesInFlight = 32;
    [Tooltip("Max LOD 0 chunk pipelines applied (meshes created) per frame.")]
    public int maxApplyPerFrame     = 12;
    [Tooltip("Max new pipeline submissions per UpdateLoadedChunks call.")]
    public int maxRequestsPerUpdate = 32;
    [Tooltip("Semaphore slots for region (LOD 1+) mesh-build tasks.")]
    public int maxRegionTasks = 8;
    [Tooltip("Max chunks per IJobParallelFor terrain dispatch. Smaller = lower first-chunk latency " +
             "(mesh jobs for earlier sub-batches start before later terrain finishes). " +
             "Recommend: CPU core count (typically 4–16).")]
    public int terrainBatchSize = 8;

    [Header("LOD")]
    [Range(0, 3)]
    public int lodLevels = 2;

    [Header("Terrain V2")]
    [Tooltip("Use the 5-channel spline terrain generator instead of the classic FBM generator.")]
    public bool useV2Generator = false;

    [Header("Terrain V2 — Core")]
    public int   v2SeaLevel     = 20;
    public float v2MaxAmplitude = 180f;

    [Header("Terrain V2 — Frequencies")]
    public float v2CScale    = 0.001f;
    public float v2EScale    = 0.003f;
    public float v2PVScale   = 0.005f;
    public float v2TScale    = 0.002f;
    public float v2HScale    = 0.002f;
    [Range(1, 8)]
    public int   v2PVOctaves = 5;

    [Header("Terrain V2 — Rivers")]
    public float v2RiverThreshold  = 0.04f;
    public float v2RiverMaskScale  = 0.002f;
    public int   v2RiverCarveDepth = 6;
    public float v2RiverErosionMin = 0.35f;

    [Header("Terrain V2 — Terrace Cliffs")]
    public float v2TerraceStep        = 0f;
    public float v2TerraceErosionMin  = 0.3f;
    public float v2TerraceErosionMax  = 0.6f;

    [Header("Terrain V2 — Splines")]
    [Tooltip("Continentalness (C) → base height offset. X = C value [-1..1], Y = height offset (voxels).")]
    public Vector2[] v2CSplinePoints;
    [Tooltip("Erosion (E) → PV height scale. X = E value [0..1], Y = scale [0..1].")]
    public Vector2[] v2ESplinePoints;

    [Header("Terrain V2 — Biomes")]
    [Tooltip("Biome definitions. Leave empty to use built-in defaults.")]
    public BiomeDef[] v2Biomes;

    [Header("Terrain V2 — Trees")]
    [Tooltip("Tree configs per biome. Leave empty to use built-in defaults.")]
    public TreeConfig[] v2TreeConfigs;
    [Tooltip("Max decoration passes (tree placement) per frame.")]
    public int maxDecorationsPerFrame = 4;

    // ── Derived ───────────────────────────────────────────────────────────────

    public int AlignedViewDistance
    {
        get { int snap = 1 << Mathf.Max(1, lodLevels); return ((viewDistance + snap - 1) / snap) * snap; }
    }

    private int MaxRadius => AlignedViewDistance * (1 << lodLevels);

    // ── LOD 0 storage ─────────────────────────────────────────────────────────

    // VoxelChunk wraps a Persistent NativeArray<byte>. We own the lifetime.
    private readonly Dictionary<Vector3Int, VoxelChunk> _chunks      = new();
    private readonly Dictionary<Vector3Int, Mesh>        _chunkMeshes      = new();
    private readonly Dictionary<Vector3Int, Mesh>        _transChunkMeshes = new();
    private readonly WorldFlags _flags = new WorldFlags { DrawListDirty = true };
    private VoxelMeshRenderer _renderer;
    private readonly HashSet<Vector3Int>                  _desiredCoords  = new();
    private readonly HashSet<Vector3Int>                  _inFlight       = new();
    // Desired coords that are neither meshed nor in-flight. Kept in sync incrementally
    // so GatherAndSortLOD0 iterates only actionable work instead of all desiredCoords.
    private readonly HashSet<Vector3Int>                  _pendingCoords  = new();

    // ── V2 generator NativeArrays (Persistent, allocated in Start, disposed in OnDestroy) ──

    private NativeArray<float2>   _v2CSpline;
    private NativeArray<float2>   _v2ESpline;
    private NativeArray<BiomeDef> _v2Biomes;
    private NativeArray<TreeConfig>  _v2TreeConfigs;

    private ChunkPipelineProcessor _pipeline;

    // Scratch lists to avoid per-frame allocations in hot loops
    private readonly List<Vector3Int> _scratchUnloadC    = new();
    private readonly List<Vector3Int> _scratchUnloadR    = new();
    private readonly List<Vector3Int> _scratchEvictC     = new();
    private readonly List<Vector3Int> _scratchEvictR     = new();
    private readonly List<Vector3Int> _scratchRequestC   = new();
    private readonly List<Vector3Int> _scratchDataCoords = new(); // data-only region chunk requests
    private readonly List<(float dist, Vector3Int coord)> _scratchRegions   = new();
    private readonly List<(float dist, Vector3Int coord)> _scratchChunkSort = new();

    // ── LOD 1+ storage (regions) ──────────────────────────────────────────────

    private readonly Dictionary<Vector3Int, RegionData>  _regions          = new();
    private readonly Dictionary<Vector3Int, Mesh>         _regionMeshes      = new();
    private readonly Dictionary<Vector3Int, Mesh>         _transRegionMeshes = new();
    private readonly Dictionary<Vector3Int, Mesh>         _staleRegionMeshes = new();
    private readonly Dictionary<Vector3Int, Mesh>         _staleChunkMeshes  = new();
    private readonly HashSet<Vector3Int>                  _desiredRegions   = new();
    private RegionManager _regionMgr;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Reset()
    {
        // Called when the component is first added; sets V2 spline defaults in the Inspector.
        v2CSplinePoints = SplineUtils.DefaultContinentalnessSpline;
        v2ESplinePoints = SplineUtils.DefaultErosionSpline;
    }

    private void OnValidate()
    {
        // Auto-populate V2 tree configs in the Inspector so the array isn't empty by default.
        if (v2TreeConfigs == null || v2TreeConfigs.Length == 0)
            v2TreeConfigs = TreeConfig.CreateDefaults(v2SeaLevel, snowAltitude);
    }

    private void Start()
    {
        _renderer = new VoxelMeshRenderer(
            this,
            _chunkMeshes, _transChunkMeshes,
            _regionMeshes, _transRegionMeshes,
            _staleChunkMeshes, _staleRegionMeshes,
            _flags);

        _regionMgr = new RegionManager(
            this, _regions,
            _regionMeshes, _transRegionMeshes,
            _staleRegionMeshes, _staleChunkMeshes,
            _desiredRegions, _flags);

        // Wire back-reference
        _renderer.RegionMgr = _regionMgr;

        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        AllocateV2NativeArrays();

        _pipeline = new ChunkPipelineProcessor(
            this,
            _v2CSpline, _v2ESpline, _v2Biomes, _v2TreeConfigs,
            _chunks, _chunkMeshes, _transChunkMeshes,
            _staleChunkMeshes, _staleRegionMeshes,
            _inFlight, _desiredCoords, _pendingCoords,
            _flags,
            onChunkReady: _regionMgr.TryFeedChunkIntoRegion);

        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        _lastPlayerChunkBacking  = VoxelCoords.WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        UpdateLoadedChunks(true);
    }

    private void AllocateV2NativeArrays()
    {
        var cPts = (v2CSplinePoints != null && v2CSplinePoints.Length > 0)
            ? v2CSplinePoints : SplineUtils.DefaultContinentalnessSpline;
        var ePts = (v2ESplinePoints != null && v2ESplinePoints.Length > 0)
            ? v2ESplinePoints : SplineUtils.DefaultErosionSpline;

        _v2CSpline = new NativeArray<float2>(cPts.Length, Allocator.Persistent);
        for (int i = 0; i < cPts.Length; i++)
            _v2CSpline[i] = new float2(cPts[i].x, cPts[i].y);

        _v2ESpline = new NativeArray<float2>(ePts.Length, Allocator.Persistent);
        for (int i = 0; i < ePts.Length; i++)
            _v2ESpline[i] = new float2(ePts[i].x, ePts[i].y);

        var biomes = (v2Biomes != null && v2Biomes.Length > 0)
            ? v2Biomes : BiomeDef.CreateDefaults();
        _v2Biomes  = new NativeArray<BiomeDef>(biomes.Length, Allocator.Persistent);
        _v2Biomes.CopyFrom(biomes);

        if (v2TreeConfigs == null || v2TreeConfigs.Length == 0)
            v2TreeConfigs = TreeConfig.CreateDefaults(v2SeaLevel, snowAltitude);
        _v2TreeConfigs = new NativeArray<TreeConfig>(v2TreeConfigs, Allocator.Persistent);
    }

    private void OnDestroy()
    {
        // Cancel region tasks
        _regionMgr.CancelCts();

        // Complete and dispose all in-flight Burst pipelines
        _pipeline.CompleteAndDisposeAll();

        // Dispose all stored VoxelChunks
        foreach (var kvp in _chunks) kvp.Value.Dispose();
        _chunks.Clear();

        foreach (var kvp in _staleChunkMeshes)  if (kvp.Value != null) Destroy(kvp.Value);
        foreach (var kvp in _staleRegionMeshes) if (kvp.Value != null) Destroy(kvp.Value);
        foreach (var kvp in _transChunkMeshes)  if (kvp.Value != null) Destroy(kvp.Value);
        foreach (var kvp in _transRegionMeshes) if (kvp.Value != null) Destroy(kvp.Value);
        _staleChunkMeshes.Clear();  _staleRegionMeshes.Clear();
        _transChunkMeshes.Clear();  _transRegionMeshes.Clear();

        if (_v2CSpline.IsCreated) _v2CSpline.Dispose();
        if (_v2ESpline.IsCreated) _v2ESpline.Dispose();
        if (_v2Biomes.IsCreated)  _v2Biomes.Dispose();
        if (_v2TreeConfigs.IsCreated) _v2TreeConfigs.Dispose();

        _regionMgr.CompleteAndDispose();
    }

    private void Update()
    {
        Profiler.BeginSample("VoxelWorld.ChangeDetection");
        bool lodChanged = CheckLodSettingsChanged();
        bool posChanged = CheckPositionChanged();

        bool anyDrained = _regionMgr.DrainCancelledRegions();
        Profiler.EndSample();

        if (posChanged || lodChanged)
        {
            _renderer.EvictStaleMeshes();

            Profiler.BeginSample("VoxelWorld.OnPositionChanged");
            _regionMgr.ResetCancellation();

            _pipeline.MarkAllDiscarded();
            _pipeline.ClearDecorationState();

            if (posChanged)
                TerrainGenerator.ClearSurfaceCache();
            Profiler.EndSample();

            Profiler.BeginSample("VoxelWorld.UpdateLoadedChunks(pos)");
            UpdateLoadedChunks(true);
            Profiler.EndSample();
        }

        Profiler.BeginSample("VoxelWorld.ProcessCompletedPipelines");
        bool pipelinesApplied = _pipeline.ProcessCompletedPipelines();
        Profiler.EndSample();

        Profiler.BeginSample("VoxelWorld.ApplyReadyRegions");
        bool regionsApplied = _regionMgr.ApplyReadyRegions();
        Profiler.EndSample();

        if ((pipelinesApplied || regionsApplied || anyDrained) && _flags.NeedsMoreRequests)
        {
            Profiler.BeginSample("VoxelWorld.UpdateLoadedChunks(cascade)");
            UpdateLoadedChunks(false);
            Profiler.EndSample();
        }

        Profiler.BeginSample("VoxelWorld.DrawAllMeshes");
        _renderer.DrawAllMeshes();
        Profiler.EndSample();
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(10, 10, 400, 200),
            $"Desired chunks:  {_desiredCoords.Count}\n" +
            $"Pending:         {_pendingCoords.Count}\n" +
            $"In-flight:       {_inFlight.Count}\n" +
            $"Pipelines:       {_pipeline.PipelineCount}\n" +
            $"Chunk meshes:    {_chunkMeshes.Count}\n" +
            $"Player chunk:    {_lastPlayerChunkBacking}\n" +
            $"ViewDist (aln):  {AlignedViewDistance}");
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunkBacking = new Vector3Int(int.MaxValue, 0, 0);
    public  Vector3Int LastPlayerChunk
    {
        get => _lastPlayerChunkBacking;
        set => _lastPlayerChunkBacking = value;
    }
    private int        _lastLodLevels    = -1;
    private int        _lastViewDistance = -1;

    private bool CheckLodSettingsChanged()
    {
        if (lodLevels == _lastLodLevels && viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        _lastPlayerChunkBacking  = new Vector3Int(int.MaxValue, 0, 0);
        return true;
    }

    private bool CheckPositionChanged()
    {
        var cur = VoxelCoords.WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        if (cur == _lastPlayerChunkBacking) return false;
        _lastPlayerChunkBacking = cur;
        return true;
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    private void UpdateLoadedChunks(bool positionChanged)
    {
        if (positionChanged)
        {
            Profiler.BeginSample("ULC.BuildDesiredSets");
            _desiredCoords.Clear();
            _desiredRegions.Clear();
            int vd = AlignedViewDistance;

            // LOD 0 zone
            for (int x = -(vd - 1); x <= vd - 1; x++)
            for (int z = -(vd - 1); z <= vd - 1; z++)
            for (int y = 0; y < verticalChunks; y++)
                _desiredCoords.Add(new Vector3Int(_lastPlayerChunkBacking.x + x, y, _lastPlayerChunkBacking.z + z));

            // LOD 1+ rings
            for (int lod = 1; lod <= lodLevels; lod++)
            {
                int hSize       = 1 << lod;
                int innerRadius = vd * (1 << (lod - 1));
                int outerRadius = vd * (1 << lod);
                int playerRX    = Mathf.FloorToInt(_lastPlayerChunkBacking.x / (float)hSize);
                int playerRZ    = Mathf.FloorToInt(_lastPlayerChunkBacking.z / (float)hSize);
                int maxRegionR  = outerRadius / hSize + 1;

                for (int rx = -maxRegionR; rx <= maxRegionR; rx++)
                for (int rz = -maxRegionR; rz <= maxRegionR; rz++)
                {
                    var regionCoord = new Vector3Int(playerRX + rx, lod, playerRZ + rz);
                    var baseChunk   = VoxelCoords.RegionBaseChunkCoord(regionCoord);
                    int nearestX    = Mathf.Clamp(_lastPlayerChunkBacking.x, baseChunk.x, baseChunk.x + hSize - 1);
                    int nearestZ    = Mathf.Clamp(_lastPlayerChunkBacking.z, baseChunk.z, baseChunk.z + hSize - 1);
                    int chebDist    = Mathf.Max(Mathf.Abs(nearestX - _lastPlayerChunkBacking.x),
                                                Mathf.Abs(nearestZ - _lastPlayerChunkBacking.z));

                    // Use farthest corner for the inner-boundary test: a region is excluded only
                    // when ALL of its chunks fall inside the LOD0 zone (farDist < innerRadius).
                    // Using nearestDist caused a 1-chunk gap on negative axes because floor-div
                    // shifts the nearest corner one step inside the LOD0 boundary there.
                    int farX = Mathf.Max(Mathf.Abs(baseChunk.x - _lastPlayerChunkBacking.x),
                                         Mathf.Abs(baseChunk.x + hSize - 1 - _lastPlayerChunkBacking.x));
                    int farZ = Mathf.Max(Mathf.Abs(baseChunk.z - _lastPlayerChunkBacking.z),
                                         Mathf.Abs(baseChunk.z + hSize - 1 - _lastPlayerChunkBacking.z));
                    int farDist = Mathf.Max(farX, farZ);

                    if (chebDist >= outerRadius || farDist < innerRadius) continue;
                    _desiredRegions.Add(regionCoord);
                }
            }
            Profiler.EndSample();

            Profiler.BeginSample("ULC.UnloadMeshes");
            // Unload out-of-range chunk meshes
            _scratchUnloadC.Clear();
            foreach (var c in _chunkMeshes.Keys)
                if (!_desiredCoords.Contains(c)) _scratchUnloadC.Add(c);
            foreach (var c in _scratchUnloadC) _renderer.UnloadChunkMesh(c);

            // Unload out-of-range region meshes
            _scratchUnloadR.Clear();
            foreach (var r in _regionMeshes.Keys)
                if (!_desiredRegions.Contains(r)) _scratchUnloadR.Add(r);
            foreach (var r in _scratchUnloadR) _renderer.UnloadRegionMesh(r);
            Profiler.EndSample();

            Profiler.BeginSample("ULC.EvictData");
            // Evict chunk data (and dispose NativeArrays)
            int evictR = MaxRadius + 2;
            _scratchEvictC.Clear();
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - _lastPlayerChunkBacking.x) > evictR ||
                    Mathf.Abs(c.z - _lastPlayerChunkBacking.z) > evictR) _scratchEvictC.Add(c);
            foreach (var c in _scratchEvictC)
            { _chunks[c].Dispose(); _chunks.Remove(c); }

            // Evict region data
            _scratchEvictR.Clear();
            foreach (var r in _regions.Keys)
                if (!_desiredRegions.Contains(r)) _scratchEvictR.Add(r);
            foreach (var r in _scratchEvictR) _regions.Remove(r);
            Profiler.EndSample();

            // Rebuild pending set: desired coords that still need a LOD0 mesh and aren't in-flight.
            // Done once per position change; maintained incrementally by SubmitSubBatch and
            // ProcessCompletedPipelines, so cascade calls never re-iterate _desiredCoords.
            _pendingCoords.Clear();
            foreach (var c in _desiredCoords)
                if (!_chunkMeshes.ContainsKey(c) && !_inFlight.Contains(c))
                    _pendingCoords.Add(c);
        }

        var playerPos = player != null ? player.position : Vector3.zero;

        // ── Request LOD 0 chunks ──────────────────────────────────────────────

        // Compute availability first — if pipeline slots are full there is nothing to submit
        // and we can skip the entire HashSet iteration + sort.
        int lod0Budget    = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int lod0Avail     = Mathf.Min(lod0Budget, Mathf.Max(0, maxPipelinesInFlight - _pipeline.PipelineCount));
        int lod0Submitted = 0;

        Profiler.BeginSample("ULC.GatherAndSortLOD0");
        _scratchRequestC.Clear();
        if (lod0Avail > 0)
        {
            Profiler.BeginSample("LOD0.Gather");
            _scratchChunkSort.Clear();
            foreach (var coord in _pendingCoords)
                // Skip coords currently in-flight as data-only (they stay in _pendingCoords until
                // their data pipeline completes, then will be re-gathered for a mesh pipeline).
                if (!_inFlight.Contains(coord))
                    _scratchChunkSort.Add((VoxelCoords.ChunkCenterWorld(coord).sqrMagnitude_To(playerPos), coord));
            Profiler.EndSample();

            if (_scratchChunkSort.Count > 1)
            {
                Profiler.BeginSample("LOD0.Sort");
                _scratchChunkSort.Sort((a, b) => a.dist.CompareTo(b.dist));
                Profiler.EndSample();
            }

            Profiler.BeginSample("LOD0.Extract");
            int extractCount = Mathf.Min(lod0Avail, _scratchChunkSort.Count);
            for (int i = 0; i < extractCount; i++)
                _scratchRequestC.Add(_scratchChunkSort[i].coord);
            Profiler.EndSample();
        }
        Profiler.EndSample();

        int lod0Count  = _scratchRequestC.Count;
        lod0Submitted  = lod0Count;

        if (lod0Count > 0)
        {
            Profiler.BeginSample("ULC.SubmitLOD0Batch");
            _pipeline.SubmitChunkBatch(_scratchRequestC, buildMesh: true);
            Profiler.EndSample();
        }

        // ── Process desired regions ───────────────────────────────────────────
        Profiler.BeginSample("ULC.GatherAndSortRegions");
        _scratchRegions.Clear();
        foreach (var r in _desiredRegions)
        {
            if (_regionMgr.IsRegionInFlight(r) || _regionMeshes.ContainsKey(r)) continue;
            _scratchRegions.Add((VoxelCoords.RegionCenterWorld(r).sqrMagnitude_To(playerPos), r));
        }
        _scratchRegions.Sort((a, b) => a.dist.CompareTo(b.dist));
        Profiler.EndSample();

        int regionBudget    = Mathf.Max(4, maxRequestsPerUpdate / 2);
        int regionSubmitted = 0;

        // Collect data-only chunk requests from all regions, then submit as one batch
        _scratchDataCoords.Clear();

        Profiler.BeginSample("ULC.FillRegionData");
        foreach (var (_, regionCoord) in _scratchRegions)
        {
            int hSize = 1 << regionCoord.y;

            if (!_regions.TryGetValue(regionCoord, out var region))
            {
                region = new RegionData(verticalChunks, hSize);
                _regions[regionCoord] = region;
            }
            else if (regionSubmitted >= regionBudget)
            {
                if (region.IsComplete) _regionMgr.RequestRegionMesh(regionCoord, region);
                continue;
            }

            var baseChunk = VoxelCoords.RegionBaseChunkCoord(regionCoord);
            for (int lcx = 0; lcx < hSize; lcx++)
            for (int lcy = 0; lcy < verticalChunks; lcy++)
            for (int lcz = 0; lcz < hSize; lcz++)
            {
                if (region.HasChunk(lcx, lcy, lcz)) continue;
                var chunkCoord = new Vector3Int(baseChunk.x + lcx, lcy, baseChunk.z + lcz);
                if (_chunks.TryGetValue(chunkCoord, out var existing))
                {
                    var bytes = new byte[VoxelChunk.VoxelCount];
                    existing.CopyTo(bytes);
                    region.SetChunk(lcx, lcy, lcz, bytes);
                }
                else if (!_inFlight.Contains(chunkCoord) && regionSubmitted < regionBudget
                         && !_scratchDataCoords.Contains(chunkCoord))
                {
                    _scratchDataCoords.Add(chunkCoord);
                    regionSubmitted++;
                }
            }

            if (region.IsComplete) _regionMgr.RequestRegionMesh(regionCoord, region);
        }
        Profiler.EndSample();

        // Submit all data-only region chunks as a single batch
        if (_scratchDataCoords.Count > 0)
        {
            Profiler.BeginSample("ULC.SubmitRegionDataBatch");
            _pipeline.SubmitChunkBatch(_scratchDataCoords, buildMesh: false);
            Profiler.EndSample();
        }

        _flags.NeedsMoreRequests = (lod0Submitted >= lod0Budget) || (regionSubmitted >= regionBudget);
    }

    // ── Terrain settings ──────────────────────────────────────────────────────

    public TerrainSettings GetTerrainSettings() => new TerrainSettings
    {
        seaLevel               = seaLevel,
        baseHeight             = baseHeight,
        plainsHeight           = plainsHeight,
        forestHeight           = forestHeight,
        mountainHeight         = mountainHeight,
        tundraHeight           = tundraHeight,
        desertDuneHeight       = desertDuneHeight,
        oceanDepth             = oceanDepth,
        noiseScale             = noiseScale,
        octaves                = octaves,
        persistence            = persistence,
        lacunarity             = lacunarity,
        tempScale              = tempScale,
        humidityScale          = humidityScale,
        mountainBiomeScale     = mountainBiomeScale,
        mountainBiomeThreshold = mountainBiomeThreshold,
        warpStrength           = warpStrength,
        warpScale              = warpScale,
        dirtDepth              = dirtDepth,
        sandBeachWidth         = sandBeachWidth,
        snowAltitude           = snowAltitude,
        desertSandDepth        = desertSandDepth,
        tundraFrozenDepth      = tundraFrozenDepth,
    };

    public TerrainSettingsV2 GetTerrainSettingsV2() => new TerrainSettingsV2
    {
        SeaLevel           = v2SeaLevel,
        MaxAmplitude       = v2MaxAmplitude,
        CScale             = v2CScale,
        EScale             = v2EScale,
        PVScale            = v2PVScale,
        TScale             = v2TScale,
        HScale             = v2HScale,
        PVOctaves          = v2PVOctaves,
        RiverThreshold     = v2RiverThreshold,
        RiverMaskScale     = v2RiverMaskScale,
        RiverCarveDepth    = v2RiverCarveDepth,
        RiverErosionMin    = v2RiverErosionMin,
        TerraceStep        = v2TerraceStep,
        TerraceErosionMin  = v2TerraceErosionMin,
        TerraceErosionMax  = v2TerraceErosionMax,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    public bool TryGetChunk(Vector3Int coord, out VoxelChunk chunk) =>
        _chunks.TryGetValue(coord, out chunk);

    public RegionData GetRegion(Vector3Int regionCoord) => _regionMgr.GetRegion(regionCoord);

}
