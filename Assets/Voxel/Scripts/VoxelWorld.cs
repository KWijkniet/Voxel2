using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
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
    private ChunkStreamer _streamer;

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

        _streamer = new ChunkStreamer(
            this, _flags,
            _desiredCoords, _desiredRegions, _pendingCoords, _inFlight,
            _chunks, _regions, _chunkMeshes, _regionMeshes,
            _renderer.UnloadChunkMesh, _renderer.UnloadRegionMesh);
        _streamer.Pipeline = _pipeline;
        _streamer.Regions  = _regionMgr;
        _streamer.CheckLodSettingsChanged(); // prime _lastLodLevels/_lastViewDistance

        _lastPlayerChunkBacking  = VoxelCoords.WorldToChunkCoord(player != null ? player.position : Vector3.zero);
        _streamer.UpdateLoadedChunks(true);
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
        bool lodChanged = _streamer.CheckLodSettingsChanged();
        bool posChanged = _streamer.CheckPositionChanged();

        bool anyDrained = _regionMgr.DrainCancelledRegions();
        Profiler.EndSample();

        if (posChanged || lodChanged)
            HandleWorldMoved(posChanged);

        Profiler.BeginSample("VoxelWorld.ProcessCompletedPipelines");
        bool pipelinesApplied = _pipeline.ProcessCompletedPipelines();
        Profiler.EndSample();

        Profiler.BeginSample("VoxelWorld.ApplyReadyRegions");
        bool regionsApplied = _regionMgr.ApplyReadyRegions();
        Profiler.EndSample();

        if ((pipelinesApplied || regionsApplied || anyDrained) && _flags.NeedsMoreRequests)
        {
            Profiler.BeginSample("VoxelWorld.UpdateLoadedChunks(cascade)");
            _streamer.UpdateLoadedChunks(false);
            Profiler.EndSample();
        }

        Profiler.BeginSample("VoxelWorld.DrawAllMeshes");
        _renderer.DrawAllMeshes();
        Profiler.EndSample();
    }

    private void HandleWorldMoved(bool positionChanged)
    {
        // Order matches original VoxelWorld.Update: EvictStaleMeshes runs first, before CTS reset.
        _renderer.EvictStaleMeshes();
        _regionMgr.ResetCancellation();
        _pipeline.MarkAllDiscarded();
        _pipeline.ClearDecorationState();
        if (positionChanged) TerrainGenerator.ClearSurfaceCache();
        _streamer.UpdateLoadedChunks(true);
    }

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

    private Vector3Int _lastPlayerChunkBacking = new Vector3Int(int.MaxValue, 0, 0);
    public  Vector3Int LastPlayerChunk
    {
        get => _lastPlayerChunkBacking;
        set => _lastPlayerChunkBacking = value;
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
