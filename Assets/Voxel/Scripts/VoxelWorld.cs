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
    private readonly List<(Mesh mesh, Matrix4x4 trs)>    _chunkDrawList        = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)>    _transChunkDrawList   = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)>    _regionDrawList       = new();
    private readonly List<(Mesh mesh, Matrix4x4 trs)>    _transRegionDrawList  = new();
    private bool         _drawListDirty   = true;
    private Matrix4x4    _cachedL2W       = Matrix4x4.zero;
    private readonly HashSet<Vector3Int>                  _desiredCoords  = new();
    private readonly HashSet<Vector3Int>                  _inFlight       = new();
    // V2 decoration gate — chunks waiting for all 8 horizontal neighbours before tree placement
    private readonly HashSet<Vector3Int> _awaitingDecoration       = new();
    private readonly HashSet<Vector3Int> _needsMeshAfterDecoration = new();
    // Desired coords that are neither meshed nor in-flight. Kept in sync incrementally
    // so GatherAndSortLOD0 iterates only actionable work instead of all desiredCoords.
    private readonly HashSet<Vector3Int>                  _pendingCoords  = new();

    // ── V2 generator NativeArrays (Persistent, allocated in Start, disposed in OnDestroy) ──

    private NativeArray<float2>   _v2CSpline;
    private NativeArray<float2>   _v2ESpline;
    private NativeArray<BiomeDef> _v2Biomes;
    private NativeArray<TreeConfig>  _v2TreeConfigs;

    // ── LOD 0 pipeline (terrain IJob → mesh IJob, chained) ───────────────────

    /// <summary>
    /// Owns the flat NativeArray&lt;byte&gt; (count*VoxelCount bytes) produced by
    /// GenerateChunksBatchJob. Released when all pipelines in the batch complete.
    /// </summary>
    private class BatchBuffer
    {
        public NativeArray<byte> Data;
        /// <summary>
        /// Chunk coords passed to GenerateChunksBatchJob. Stored here (Persistent) to avoid
        /// TempJob 4-frame lifetime warnings when the terrain job doesn't complete quickly.
        /// </summary>
        public NativeArray<int3> Coords;
        /// <summary>
        /// Per-batch surface height cache. Scoped to this batch's lifetime so capacity never
        /// accumulates across player movement. Sized for count × 18 × 18 columns (16³ chunk
        /// plus a 1-voxel border on each face) with 2× headroom for the hash map load factor.
        /// </summary>
        public NativeParallelHashMap<long, int> SurfaceCache;
        public int Pending;

        public BatchBuffer(int count)
        {
            Data    = new NativeArray<byte>(count * VoxelChunk.VoxelCount, Allocator.Persistent,
                                            NativeArrayOptions.UninitializedMemory);
            Coords  = new NativeArray<int3>(count, Allocator.Persistent,
                                            NativeArrayOptions.UninitializedMemory);
            SurfaceCache = new NativeParallelHashMap<long, int>(count * 18 * 18 * 2, Allocator.Persistent);
            Pending = count;
        }

        public void Release()
        {
            if (--Pending > 0) return;
            if (Data.IsCreated)         Data.Dispose();
            if (Coords.IsCreated)       Coords.Dispose();
            if (SurfaceCache.IsCreated) SurfaceCache.Dispose();
        }
    }

    /// <summary>
    /// One per in-flight LOD 0 chunk. The terrain job writes to Voxels; the mesh job
    /// reads Voxels (and neighbour snapshots) and appends to the NativeLists.
    /// Handle is the mesh job's handle (which implicitly waits for terrain).
    /// For data-only requests (BuildMesh=false) Handle is the terrain handle directly.
    /// </summary>
    private struct ChunkPipeline
    {
        public Vector3Int Coord;
        public JobHandle  Handle;        // mesh handle (or terrain handle if !BuildMesh)

        /// <summary>
        /// Sub-array view into Batch.Data when Batch != null; owned Persistent allocation otherwise.
        /// Do NOT call Dispose() on this directly — call Batch.Release() or Voxels.Dispose() based on Batch.
        /// </summary>
        public NativeArray<byte> Voxels;

        /// <summary>
        /// Non-null when terrain was generated via GenerateChunksBatchJob.
        /// Call Batch.Release() on pipeline completion to manage shared buffer lifetime.
        /// </summary>
        public BatchBuffer Batch;

        // Only valid when BuildMesh = true:
        public NativeArray<byte>[] NeighbourSnapshots; // 6 × VoxelCount, owned
        public NativeList<float3>  MeshVerts;
        public NativeList<float3>  MeshNorms;
        public NativeList<float2>  MeshUVs;
        public NativeList<float2>  MeshUV2s;
        public NativeList<int>     MeshTris;
        // Transparent geometry (water, glass, …)
        public NativeList<float3>  TransVerts;
        public NativeList<float3>  TransNorms;
        public NativeList<float2>  TransUVs;
        public NativeList<float2>  TransUV2s;
        public NativeList<int>     TransTris;

        public bool BuildMesh;
        public bool Discarded;
    }

    private readonly List<ChunkPipeline> _pipelines = new();

    // Scratch lists to avoid per-frame allocations in hot loops
    private readonly List<int>        _scratchCompleted  = new();
    private readonly List<int>        _scratchRemove     = new();
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
    private readonly HashSet<Vector3Int>                  _regionInFlight   = new();
    private readonly ConcurrentQueue<RegionBuildResult>   _regionReadyQueue = new();
    private readonly ConcurrentQueue<Vector3Int>          _cancelledRegions = new();

    private readonly struct RegionBuildResult
    {
        public readonly Vector3Int       RegionCoord;
        public readonly WritableMeshData MeshData;
        public readonly WritableMeshData TransMeshData;
        public RegionBuildResult(Vector3Int r, WritableMeshData m, WritableMeshData t)
        { RegionCoord = r; MeshData = m; TransMeshData = t; }
    }

    private SemaphoreSlim           _regionSemaphore;
    private CancellationTokenSource _regionCts = new();
    private bool _needsMoreRequests;

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Reset()
    {
        // Called when the component is first added; sets V2 spline defaults in the Inspector.
        v2CSplinePoints = SplineUtils.DefaultContinentalnessSpline;
        v2ESplinePoints = SplineUtils.DefaultErosionSpline;
    }

    private void Start()
    {
        _regionSemaphore = new SemaphoreSlim(maxRegionTasks, maxRegionTasks);

        if (chunkMaterial == null)
            chunkMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        AllocateV2NativeArrays();

        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
        _lastPlayerChunk  = WorldToChunkCoord(player != null ? player.position : Vector3.zero);
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
        _regionCts.Cancel();
        _regionCts.Dispose();

        // Complete and dispose all in-flight Burst pipelines
        var batchesToDispose = new System.Collections.Generic.HashSet<BatchBuffer>();
        for (int i = 0; i < _pipelines.Count; i++)
        {
            var p = _pipelines[i];
            p.Handle.Complete();
            if (p.Batch != null)
                batchesToDispose.Add(p.Batch);  // disposed below after loop
            else if (p.Voxels.IsCreated)
                p.Voxels.Dispose();
            if (p.BuildMesh)
            {
                if (p.NeighbourSnapshots != null)
                    foreach (var n in p.NeighbourSnapshots) n.Dispose();
                if (p.MeshVerts.IsCreated) p.MeshVerts.Dispose();
                if (p.MeshNorms.IsCreated) p.MeshNorms.Dispose();
                if (p.MeshUVs.IsCreated)   p.MeshUVs.Dispose();
                if (p.MeshUV2s.IsCreated)  p.MeshUV2s.Dispose();
                if (p.MeshTris.IsCreated)  p.MeshTris.Dispose();
                if (p.TransVerts.IsCreated) p.TransVerts.Dispose();
                if (p.TransNorms.IsCreated) p.TransNorms.Dispose();
                if (p.TransUVs.IsCreated)   p.TransUVs.Dispose();
                if (p.TransUV2s.IsCreated)  p.TransUV2s.Dispose();
                if (p.TransTris.IsCreated)  p.TransTris.Dispose();
            }
        }
        foreach (var b in batchesToDispose)
        {
            if (b.Data.IsCreated)         b.Data.Dispose();
            if (b.Coords.IsCreated)       b.Coords.Dispose();
            if (b.SurfaceCache.IsCreated) b.SurfaceCache.Dispose();
        }
        _pipelines.Clear();

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
    }

    private void Update()
    {
        Profiler.BeginSample("VoxelWorld.ChangeDetection");
        bool lodChanged = CheckLodSettingsChanged();
        bool posChanged = CheckPositionChanged();

        bool anyDrained = false;
        while (_cancelledRegions.TryDequeue(out var r))
        { _regionInFlight.Remove(r); anyDrained = true; }
        Profiler.EndSample();

        if (posChanged || lodChanged)
        {
            EvictStaleMeshes();

            Profiler.BeginSample("VoxelWorld.OnPositionChanged");
            _regionCts.Cancel();
            _regionCts.Dispose();
            _regionCts = new CancellationTokenSource();

            for (int i = 0; i < _pipelines.Count; i++)
            {
                var p = _pipelines[i]; p.Discarded = true; _pipelines[i] = p;
            }

            // Clear V2 decoration gate — coords in _awaitingDecoration are in _inFlight
            if (useV2Generator)
            {
                foreach (var c in _awaitingDecoration) _inFlight.Remove(c);
                _awaitingDecoration.Clear();
                _needsMeshAfterDecoration.Clear();
            }

            if (posChanged)
                TerrainGenerator.ClearSurfaceCache();
            Profiler.EndSample();

            Profiler.BeginSample("VoxelWorld.UpdateLoadedChunks(pos)");
            UpdateLoadedChunks(true);
            Profiler.EndSample();
        }

        Profiler.BeginSample("VoxelWorld.ProcessCompletedPipelines");
        bool pipelinesApplied = ProcessCompletedPipelines();
        Profiler.EndSample();

        Profiler.BeginSample("VoxelWorld.ApplyReadyRegions");
        bool regionsApplied = ApplyReadyRegions();
        Profiler.EndSample();

        if ((pipelinesApplied || regionsApplied || anyDrained) && _needsMoreRequests)
        {
            Profiler.BeginSample("VoxelWorld.UpdateLoadedChunks(cascade)");
            UpdateLoadedChunks(false);
            Profiler.EndSample();
        }

        Profiler.BeginSample("VoxelWorld.DrawAllMeshes");
        DrawAllMeshes();
        Profiler.EndSample();
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(10, 10, 400, 200),
            $"Desired chunks:  {_desiredCoords.Count}\n" +
            $"Pending:         {_pendingCoords.Count}\n" +
            $"In-flight:       {_inFlight.Count}\n" +
            $"Pipelines:       {_pipelines.Count}\n" +
            $"Chunk meshes:    {_chunkMeshes.Count}\n" +
            $"Player chunk:    {_lastPlayerChunk}\n" +
            $"ViewDist (aln):  {AlignedViewDistance}");
    }

    // ── Change detection ──────────────────────────────────────────────────────

    private Vector3Int _lastPlayerChunk  = new Vector3Int(int.MaxValue, 0, 0);
    private int        _lastLodLevels    = -1;
    private int        _lastViewDistance = -1;

    private bool CheckLodSettingsChanged()
    {
        if (lodLevels == _lastLodLevels && viewDistance == _lastViewDistance) return false;
        _lastLodLevels    = lodLevels;
        _lastViewDistance = viewDistance;
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
            Profiler.BeginSample("ULC.BuildDesiredSets");
            _desiredCoords.Clear();
            _desiredRegions.Clear();
            int vd = AlignedViewDistance;

            // LOD 0 zone
            for (int x = -(vd - 1); x <= vd - 1; x++)
            for (int z = -(vd - 1); z <= vd - 1; z++)
            for (int y = 0; y < verticalChunks; y++)
                _desiredCoords.Add(new Vector3Int(_lastPlayerChunk.x + x, y, _lastPlayerChunk.z + z));

            // LOD 1+ rings
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

                    // Use farthest corner for the inner-boundary test: a region is excluded only
                    // when ALL of its chunks fall inside the LOD0 zone (farDist < innerRadius).
                    // Using nearestDist caused a 1-chunk gap on negative axes because floor-div
                    // shifts the nearest corner one step inside the LOD0 boundary there.
                    int farX = Mathf.Max(Mathf.Abs(baseChunk.x - _lastPlayerChunk.x),
                                         Mathf.Abs(baseChunk.x + hSize - 1 - _lastPlayerChunk.x));
                    int farZ = Mathf.Max(Mathf.Abs(baseChunk.z - _lastPlayerChunk.z),
                                         Mathf.Abs(baseChunk.z + hSize - 1 - _lastPlayerChunk.z));
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
            foreach (var c in _scratchUnloadC) UnloadChunkMesh(c);

            // Unload out-of-range region meshes
            _scratchUnloadR.Clear();
            foreach (var r in _regionMeshes.Keys)
                if (!_desiredRegions.Contains(r)) _scratchUnloadR.Add(r);
            foreach (var r in _scratchUnloadR) UnloadRegionMesh(r);
            Profiler.EndSample();

            Profiler.BeginSample("ULC.EvictData");
            // Evict chunk data (and dispose NativeArrays)
            int evictR = MaxRadius + 2;
            _scratchEvictC.Clear();
            foreach (var c in _chunks.Keys)
                if (Mathf.Abs(c.x - _lastPlayerChunk.x) > evictR ||
                    Mathf.Abs(c.z - _lastPlayerChunk.z) > evictR) _scratchEvictC.Add(c);
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
        int lod0Avail     = Mathf.Min(lod0Budget, Mathf.Max(0, maxPipelinesInFlight - _pipelines.Count));
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
                    _scratchChunkSort.Add((ChunkCenterWorld(coord).sqrMagnitude_To(playerPos), coord));
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
            SubmitChunkBatch(_scratchRequestC, buildMesh: true);
            Profiler.EndSample();
        }

        // ── Process desired regions ───────────────────────────────────────────
        Profiler.BeginSample("ULC.GatherAndSortRegions");
        _scratchRegions.Clear();
        foreach (var r in _desiredRegions)
        {
            if (_regionInFlight.Contains(r) || _regionMeshes.ContainsKey(r)) continue;
            _scratchRegions.Add((RegionCenterWorld(r).sqrMagnitude_To(playerPos), r));
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
                if (region.IsComplete) RequestRegionMesh(regionCoord, region);
                continue;
            }

            var baseChunk = RegionBaseChunkCoord(regionCoord);
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

            if (region.IsComplete) RequestRegionMesh(regionCoord, region);
        }
        Profiler.EndSample();

        // Submit all data-only region chunks as a single batch
        if (_scratchDataCoords.Count > 0)
        {
            Profiler.BeginSample("ULC.SubmitRegionDataBatch");
            SubmitChunkBatch(_scratchDataCoords, buildMesh: false);
            Profiler.EndSample();
        }

        _needsMoreRequests = (lod0Submitted >= lod0Budget) || (regionSubmitted >= regionBudget);
    }

    // ── LOD 0 chunk pipeline ──────────────────────────────────────────────────

    /// <summary>
    /// Submits coords in sub-batches of <see cref="terrainBatchSize"/>.
    /// Each sub-batch gets one GenerateChunksBatchJob (IJobParallelFor) for terrain, then
    /// individual BuildChunkMeshJob instances chained to that sub-batch's terrain handle.
    /// Splitting prevents head-of-line blocking: mesh jobs for sub-batch 0 can start as soon
    /// as its terrain finishes, while sub-batch 1's terrain is still in flight.
    /// </summary>
    private void SubmitChunkBatch(List<Vector3Int> coords, bool buildMesh)
    {
        if (coords.Count == 0) return;

        int n         = coords.Count;
        int subSize   = math.max(1, terrainBatchSize);
        var settings  = GetTerrainSettings();

        for (int start = 0; start < n; start += subSize)
        {
            int subCount = math.min(subSize, n - start);
            SubmitSubBatch(coords, start, subCount, buildMesh, settings);
        }

        JobHandle.ScheduleBatchedJobs();
    }

    private void SubmitSubBatch(List<Vector3Int> coords, int offset, int count,
                                bool buildMesh, TerrainSettings settings)
    {
        var batch = new BatchBuffer(count); // allocates batch.Data and batch.Coords as Persistent

        for (int i = 0; i < count; i++)
        {
            _inFlight.Add(coords[offset + i]);
            // Only remove from pending for mesh pipelines. Data-only pipelines may target boundary
            // chunks that also need a LOD0 mesh; removing them here would permanently skip them.
            if (buildMesh) _pendingCoords.Remove(coords[offset + i]);
            batch.Coords[i] = new int3(coords[offset + i].x, coords[offset + i].y, coords[offset + i].z);
        }

        JobHandle terrainHandle;
        if (useV2Generator)
        {
            var terrainJob = new GenerateChunksBatchJobV2
            {
                Settings           = GetTerrainSettingsV2(),
                Coords             = batch.Coords,
                LowDetail          = !buildMesh,
                AllBlocks          = batch.Data,
                SurfaceCache       = batch.SurfaceCache,
                SurfaceCacheWriter = batch.SurfaceCache.AsParallelWriter(),
                CSpline            = _v2CSpline,
                ESpline            = _v2ESpline,
                Biomes             = _v2Biomes,
                TreeConfigs        = _v2TreeConfigs,
            };
            terrainHandle = terrainJob.Schedule(count, 1);

            // V2 LOD 0 chunks: track which need mesh after decoration
            if (useV2Generator && buildMesh)
                for (int i = 0; i < count; i++)
                    _needsMeshAfterDecoration.Add(coords[offset + i]);
        }
        else
        {
            var terrainJob = new GenerateChunksBatchJob
            {
                Settings           = settings,
                Coords             = batch.Coords,
                LowDetail          = !buildMesh,
                AllBlocks          = batch.Data,
                SurfaceCache       = batch.SurfaceCache,
                SurfaceCacheWriter = batch.SurfaceCache.AsParallelWriter(),
            };
            terrainHandle = terrainJob.Schedule(count, 1);
        }
        // batch.Coords is Persistent — disposed by BatchBuffer.Release() when all pipelines complete

        for (int i = 0; i < count; i++)
        {
            var coord  = coords[offset + i];
            var voxels = batch.Data.GetSubArray(i * VoxelChunk.VoxelCount, VoxelChunk.VoxelCount);

            if (!buildMesh || useV2Generator)
            {
                _pipelines.Add(new ChunkPipeline
                {
                    Coord     = coord,
                    Handle    = terrainHandle,
                    Voxels    = voxels,
                    Batch     = batch,
                    BuildMesh = false,
                });
                continue;
            }

            var snapshots  = SnapshotNeighbours(coord, out int neighbourMask);
            var verts      = new NativeList<float3>(4096, Allocator.Persistent);
            var norms      = new NativeList<float3>(4096, Allocator.Persistent);
            var uvs        = new NativeList<float2>(4096, Allocator.Persistent);
            var uv2s       = new NativeList<float2>(4096, Allocator.Persistent);
            var tris       = new NativeList<int>   (6144, Allocator.Persistent);
            var tVerts     = new NativeList<float3>(512,  Allocator.Persistent);
            var tNorms     = new NativeList<float3>(512,  Allocator.Persistent);
            var tUvs       = new NativeList<float2>(512,  Allocator.Persistent);
            var tUv2s      = new NativeList<float2>(512,  Allocator.Persistent);
            var tTris      = new NativeList<int>   (768,  Allocator.Persistent);

            var meshJob = new BuildChunkMeshJob
            {
                Voxels         = voxels,
                N_PX           = snapshots[0], N_NX = snapshots[1],
                N_PY           = snapshots[2], N_NY = snapshots[3],
                N_PZ           = snapshots[4], N_NZ = snapshots[5],
                NeighbourMask  = neighbourMask,
                Step           = 1,
                Vertices       = verts,
                Normals        = norms,
                UVs            = uvs,
                UV2s           = uv2s,
                Triangles      = tris,
                TransVertices  = tVerts,
                TransNormals   = tNorms,
                TransUVs       = tUvs,
                TransUV2s      = tUv2s,
                TransTriangles = tTris,
            };
            var meshHandle = meshJob.Schedule(terrainHandle);

            _pipelines.Add(new ChunkPipeline
            {
                Coord              = coord,
                Handle             = meshHandle,
                Voxels             = voxels,
                Batch              = batch,
                NeighbourSnapshots = snapshots,
                MeshVerts          = verts,
                MeshNorms          = norms,
                MeshUVs            = uvs,
                MeshUV2s           = uv2s,
                MeshTris           = tris,
                TransVerts         = tVerts,
                TransNorms         = tNorms,
                TransUVs           = tUvs,
                TransUV2s          = tUv2s,
                TransTris          = tTris,
                BuildMesh          = true,
            });
        }
    }

    /// <summary>
    /// Polls all pending pipelines. Completed ones are applied directly (no ConcurrentQueue needed).
    /// Discarded pipelines are drained without creating meshes.
    /// Budget (maxApplyPerFrame) applied only to non-discarded mesh-building pipelines.
    /// Returns true if any pipeline was processed (used to trigger UpdateLoadedChunks cascading).
    /// </summary>
    private bool ProcessCompletedPipelines()
    {
        _scratchCompleted.Clear();
        for (int i = 0; i < _pipelines.Count; i++)
            if (_pipelines[i].Handle.IsCompleted)
                _scratchCompleted.Add(i);

        if (_scratchCompleted.Count == 0) return false;

        // Sort: non-discarded closest-first; discarded last (always drained without budget cost)
        var pc = _lastPlayerChunk;
        _scratchCompleted.Sort((a, b) =>
        {
            var pa = _pipelines[a]; var pb = _pipelines[b];
            if (pa.Discarded != pb.Discarded) return pa.Discarded ? 1 : -1;
            int da = Mathf.Max(Mathf.Abs(pa.Coord.x - pc.x), Mathf.Abs(pa.Coord.z - pc.z));
            int db = Mathf.Max(Mathf.Abs(pb.Coord.x - pc.x), Mathf.Abs(pb.Coord.z - pc.z));
            return da.CompareTo(db);
        });

        int  budget    = maxApplyPerFrame;
        bool anyApplied = false;
        _scratchRemove.Clear();

        foreach (int idx in _scratchCompleted)
        {
            var p = _pipelines[idx];
            if (!p.Discarded && p.BuildMesh && budget <= 0) continue;

            p.Handle.Complete();
            _scratchRemove.Add(idx);

            if (p.Discarded)
            {
                // Free all native memory, remove from _inFlight
                if (p.Batch != null) p.Batch.Release(); else if (p.Voxels.IsCreated) p.Voxels.Dispose();
                DisposeMeshLists(ref p);
                _inFlight.Remove(p.Coord);
                _awaitingDecoration.Remove(p.Coord);
                _needsMeshAfterDecoration.Remove(p.Coord);
                // Coord may still be desired under the new player position — re-queue it.
                if (p.BuildMesh && _desiredCoords.Contains(p.Coord) && !_chunkMeshes.ContainsKey(p.Coord))
                    _pendingCoords.Add(p.Coord);
                continue;
            }

            if (p.BuildMesh) budget--;

            // Copy voxels to an owned NativeArray for VoxelChunk storage.
            // p.Voxels may be a sub-array view into a BatchBuffer; copying ensures VoxelChunk
            // lifetime is independent of the batch buffer.
            var ownedVoxels = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                                     NativeArrayOptions.UninitializedMemory);
            NativeArray<byte>.Copy(p.Voxels, ownedVoxels, VoxelChunk.VoxelCount);

            // Release batch reference (or dispose solo-owned voxels)
            if (p.Batch != null) p.Batch.Release(); else if (p.Voxels.IsCreated) p.Voxels.Dispose();

            var chunk = new VoxelChunk { Blocks = ownedVoxels };
            if (_chunks.TryGetValue(p.Coord, out var old)) old.Dispose();
            _chunks[p.Coord] = chunk;

            // V2 terrain-only pipeline completes → queue for decoration gate
            if (useV2Generator && !p.BuildMesh)
            {
                _awaitingDecoration.Add(p.Coord);
                // Keep coord in _inFlight until SubmitMeshOnly completes (or data-only done in TryDecorateReady)
                // DO NOT call _inFlight.Remove or TryFeedChunkIntoRegion here
                _needsMoreRequests = true;
                anyApplied = true;
                continue;
            }

            // Dispose neighbour snapshots
            if (p.NeighbourSnapshots != null)
                foreach (var n in p.NeighbourSnapshots) n.Dispose();

            // Apply mesh (or null sentinel for empty/data-only)
            if (p.BuildMesh && _desiredCoords.Contains(p.Coord) && !_chunkMeshes.ContainsKey(p.Coord))
            {
                _chunkMeshes[p.Coord]      = CreateMeshFromLists(ref p, transparent: false);
                _transChunkMeshes[p.Coord] = CreateMeshFromLists(ref p, transparent: true);
                // Remove same-coord stale chunk
                if (_staleChunkMeshes.TryGetValue(p.Coord, out var stale))
                { Destroy(stale); _staleChunkMeshes.Remove(p.Coord); }
                // Remove stale regions of any LOD that contained this chunk
                for (int lod = 1; lod <= lodLevels; lod++)
                {
                    var rc = ChunkToRegionCoord(p.Coord, lod);
                    if (_staleRegionMeshes.TryGetValue(rc, out var sr))
                    { Destroy(sr); _staleRegionMeshes.Remove(rc); }
                }
                _drawListDirty = true;
            }
            else
                DisposeMeshLists(ref p);

            _inFlight.Remove(p.Coord);
            TryFeedChunkIntoRegion(p.Coord, chunk);

            _needsMoreRequests = true;
            anyApplied = true;
        }

        // Remove processed entries (descending so lower indices remain valid)
        _scratchRemove.Sort((a, b) => b.CompareTo(a));
        foreach (int idx in _scratchRemove)
            _pipelines.RemoveAt(idx);

        TryDecorateReady();
        return anyApplied;
    }

    // transparent=false → opaque lists; transparent=true → trans lists.
    // Disposes all lists (both opaque and trans) when transparent=true (called second).
    private static Mesh CreateMeshFromLists(ref ChunkPipeline p, bool transparent)
    {
        var srcVerts = transparent ? p.TransVerts : p.MeshVerts;
        var srcNorms = transparent ? p.TransNorms : p.MeshNorms;
        var srcUVs   = transparent ? p.TransUVs   : p.MeshUVs;
        var srcUV2s  = transparent ? p.TransUV2s  : p.MeshUV2s;
        var srcTris  = transparent ? p.TransTris  : p.MeshTris;

        Mesh mesh = null;
        if (srcVerts.IsCreated && srcVerts.Length > 0)
        {
            bool use32 = srcVerts.Length > ushort.MaxValue;
            var  mda   = Mesh.AllocateWritableMeshData(1);
            var  md    = mda[0];

            md.SetVertexBufferParams(srcVerts.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 3));
            md.SetIndexBufferParams(srcTris.Length, use32 ? IndexFormat.UInt32 : IndexFormat.UInt16);

            md.GetVertexData<float3>(0).CopyFrom(srcVerts.AsArray());
            md.GetVertexData<float3>(1).CopyFrom(srcNorms.AsArray());
            md.GetVertexData<float2>(2).CopyFrom(srcUVs.AsArray());
            md.GetVertexData<float2>(3).CopyFrom(srcUV2s.AsArray());

            if (use32) { md.GetIndexData<int>().CopyFrom(srcTris.AsArray()); }
            else { var idx = md.GetIndexData<ushort>(); for (int i = 0; i < srcTris.Length; i++) idx[i] = (ushort)srcTris[i]; }

            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, srcTris.Length),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            mesh = new Mesh { name = transparent ? "ChunkTrans" : "Chunk" };
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            int s = VoxelChunk.Size;
            mesh.bounds = new Bounds(new Vector3(s * .5f, s * .5f, s * .5f), new Vector3(s, s, s));
        }

        // Dispose all lists once both meshes have been extracted (on the second call)
        if (transparent) DisposeMeshLists(ref p);
        return mesh;
    }

    private static void DisposeMeshLists(ref ChunkPipeline p)
    {
        if (!p.BuildMesh) return;
        if (p.MeshVerts.IsCreated)  p.MeshVerts.Dispose();
        if (p.MeshNorms.IsCreated)  p.MeshNorms.Dispose();
        if (p.MeshUVs.IsCreated)    p.MeshUVs.Dispose();
        if (p.MeshUV2s.IsCreated)   p.MeshUV2s.Dispose();
        if (p.MeshTris.IsCreated)   p.MeshTris.Dispose();
        if (p.TransVerts.IsCreated) p.TransVerts.Dispose();
        if (p.TransNorms.IsCreated) p.TransNorms.Dispose();
        if (p.TransUVs.IsCreated)   p.TransUVs.Dispose();
        if (p.TransUV2s.IsCreated)  p.TransUV2s.Dispose();
        if (p.TransTris.IsCreated)  p.TransTris.Dispose();
    }

    // ── V2 decoration pipeline ────────────────────────────────────────────────

    private void TryDecorateReady()
    {
        if (!useV2Generator || _awaitingDecoration.Count == 0) return;

        var ready = new List<Vector3Int>();
        foreach (var coord in _awaitingDecoration)
            if (ChunkDecorator.AllNeighboursReady(coord, _chunks))
                ready.Add(coord);

        int decorated = 0;
        foreach (var coord in ready)
        {
            if (decorated >= maxDecorationsPerFrame) break;

            ChunkDecorator.Decorate(coord, _chunks, v2TreeConfigs, _v2Biomes, GetTerrainSettingsV2());
            _awaitingDecoration.Remove(coord);
            decorated++;

            // Feed into region AFTER decoration so region gets decorated voxels
            TryFeedChunkIntoRegion(coord, _chunks[coord]);

            bool wantsMesh = _needsMeshAfterDecoration.Remove(coord)
                          && _desiredCoords.Contains(coord)
                          && !_chunkMeshes.ContainsKey(coord);

            if (wantsMesh)
                SubmitMeshOnly(coord); // coord stays in _inFlight until mesh pipeline completes
            else
                _inFlight.Remove(coord); // data-only — fully done

            _needsMoreRequests = true;
        }
    }

    private void SubmitMeshOnly(Vector3Int coord)
    {
        if (!_chunks.TryGetValue(coord, out var chunk)) { _inFlight.Remove(coord); return; }

        var snapshots = SnapshotNeighbours(coord, out int neighbourMask);
        var voxelsCopy = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                               NativeArrayOptions.UninitializedMemory);
        NativeArray<byte>.Copy(chunk.Blocks, voxelsCopy, VoxelChunk.VoxelCount);

        var verts  = new NativeList<float3>(4096, Allocator.Persistent);
        var norms  = new NativeList<float3>(4096, Allocator.Persistent);
        var uvs    = new NativeList<float2>(4096, Allocator.Persistent);
        var uv2s   = new NativeList<float2>(4096, Allocator.Persistent);
        var tris   = new NativeList<int>   (6144, Allocator.Persistent);
        var tVerts = new NativeList<float3>(512,  Allocator.Persistent);
        var tNorms = new NativeList<float3>(512,  Allocator.Persistent);
        var tUvs   = new NativeList<float2>(512,  Allocator.Persistent);
        var tUv2s  = new NativeList<float2>(512,  Allocator.Persistent);
        var tTris  = new NativeList<int>   (768,  Allocator.Persistent);

        var meshJob = new BuildChunkMeshJob
        {
            Voxels         = voxelsCopy,
            N_PX           = snapshots[0], N_NX = snapshots[1],
            N_PY           = snapshots[2], N_NY = snapshots[3],
            N_PZ           = snapshots[4], N_NZ = snapshots[5],
            NeighbourMask  = neighbourMask,
            Step           = 1,
            Vertices       = verts,
            Normals        = norms,
            UVs            = uvs,
            UV2s           = uv2s,
            Triangles      = tris,
            TransVertices  = tVerts,
            TransNormals   = tNorms,
            TransUVs       = tUvs,
            TransUV2s      = tUv2s,
            TransTriangles = tTris,
        };

        _pipelines.Add(new ChunkPipeline
        {
            Coord              = coord,
            Handle             = meshJob.Schedule(),
            Voxels             = voxelsCopy,
            NeighbourSnapshots = snapshots,
            BuildMesh          = true,
            MeshVerts          = verts,
            MeshNorms          = norms,
            MeshUVs            = uvs,
            MeshUV2s           = uv2s,
            MeshTris           = tris,
            TransVerts         = tVerts,
            TransNorms         = tNorms,
            TransUVs           = tUvs,
            TransUV2s          = tUv2s,
            TransTris          = tTris,
        });
        // Batch is null — ProcessCompletedPipelines disposes voxelsCopy via p.Voxels.Dispose()
        // when the pipeline completes (existing code handles null Batch).
    }

    // ── Neighbour snapshots ───────────────────────────────────────────────────

    /// <summary>
    /// Copies the six neighbour VoxelChunks into Persistent NativeArrays for the mesh job.
    /// Unloaded neighbours produce zeroed arrays (all Air). Returns bit-mask of loaded neighbours.
    /// </summary>
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
                neighbour.Blocks.CopyTo(n); // native memcpy
                loadedMask |= (1 << i);
            }
            snapshots[i] = n;
        }
        return snapshots;
    }

    // ── Region feeding ────────────────────────────────────────────────────────

    private void TryFeedChunkIntoRegion(Vector3Int coord, VoxelChunk chunk)
    {
        int dist     = Mathf.Max(Mathf.Abs(coord.x - _lastPlayerChunk.x),
                                  Mathf.Abs(coord.z - _lastPlayerChunk.z));
        int lod      = 0;
        int boundary = AlignedViewDistance;
        while (lod < lodLevels && dist >= boundary) { lod++; boundary *= 2; }
        if (lod == 0) return;

        var regionCoord = ChunkToRegionCoord(coord, lod);
        if (!_regions.TryGetValue(regionCoord, out var region)) return;

        var local = coord - RegionBaseChunkCoord(regionCoord);
        if (region.HasChunk(local.x, local.y, local.z)) return;

        var bytes = new byte[VoxelChunk.VoxelCount];
        chunk.CopyTo(bytes);
        region.SetChunk(local.x, local.y, local.z, bytes);

        if (region.IsComplete && _desiredRegions.Contains(regionCoord)
            && !_regionInFlight.Contains(regionCoord)
            && !_regionMeshes.ContainsKey(regionCoord))
            RequestRegionMesh(regionCoord, region);
    }

    // ── LOD 1+ region tasks (Task.Run) ────────────────────────────────────────

    private void RequestRegionMesh(Vector3Int regionCoord, RegionData region)
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

    private bool ApplyReadyRegions()
    {
        int  applied  = 0;
        bool anyAdded = false;
        while (applied < maxApplyPerFrame && _regionReadyQueue.TryDequeue(out var result))
        {
            _regionInFlight.Remove(result.RegionCoord);

            if (!_regionMeshes.ContainsKey(result.RegionCoord)
                && _desiredRegions.Contains(result.RegionCoord))
            {
                _regionMeshes[result.RegionCoord]      = MeshBuilder.CreateMesh(result.MeshData);
                _transRegionMeshes[result.RegionCoord] = MeshBuilder.CreateMesh(result.TransMeshData);
                // Remove same-coord stale region
                if (_staleRegionMeshes.TryGetValue(result.RegionCoord, out var stale))
                { Destroy(stale); _staleRegionMeshes.Remove(result.RegionCoord); }
                // Remove stale LOD0 chunks that fall within this region's footprint
                var baseC = RegionBaseChunkCoord(result.RegionCoord);
                int hs    = 1 << result.RegionCoord.y;
                for (int cx = baseC.x; cx < baseC.x + hs; cx++)
                for (int cz = baseC.z; cz < baseC.z + hs; cz++)
                for (int cy = 0; cy < verticalChunks; cy++)
                {
                    var c = new Vector3Int(cx, cy, cz);
                    if (_staleChunkMeshes.TryGetValue(c, out var sc))
                    { Destroy(sc); _staleChunkMeshes.Remove(c); }
                }
                _drawListDirty = true;
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

    // ── Mesh lifecycle ────────────────────────────────────────────────────────

    private void UnloadChunkMesh(Vector3Int coord)
    {
        if (_chunkMeshes.TryGetValue(coord, out var mesh))
        {
            _chunkMeshes.Remove(coord);
            if (mesh != null) _staleChunkMeshes[coord] = mesh;
            _drawListDirty = true;
        }
        if (_transChunkMeshes.TryGetValue(coord, out var tmesh))
        { _transChunkMeshes.Remove(coord); if (tmesh != null) Destroy(tmesh); _drawListDirty = true; }
    }

    private void UnloadRegionMesh(Vector3Int r)
    {
        if (_regionMeshes.TryGetValue(r, out var mesh))
        {
            _regionMeshes.Remove(r);
            if (mesh != null) _staleRegionMeshes[r] = mesh;
            _drawListDirty = true;
        }
        if (_transRegionMeshes.TryGetValue(r, out var tmesh))
        { _transRegionMeshes.Remove(r); if (tmesh != null) Destroy(tmesh); _drawListDirty = true; }
    }

    private void EvictStaleMeshes()
    {
        int vd = AlignedViewDistance;

        // Stale LOD0 chunks: evict once they're outside the LOD0 zone with a small buffer
        // so they persist briefly while new chunks load in, but don't accumulate indefinitely.
        _scratchUnloadC.Clear();
        foreach (var c in _staleChunkMeshes.Keys)
            if (Mathf.Abs(c.x - _lastPlayerChunk.x) > vd + 2 ||
                Mathf.Abs(c.z - _lastPlayerChunk.z) > vd + 2)
                _scratchUnloadC.Add(c);
        foreach (var c in _scratchUnloadC)
        { Destroy(_staleChunkMeshes[c]); _staleChunkMeshes.Remove(c); _drawListDirty = true; }

        // Stale regions: evict once outside their LOD ring's outer radius with a buffer
        _scratchUnloadR.Clear();
        foreach (var r in _staleRegionMeshes.Keys)
        {
            int lod         = r.y;
            int outerRadius = vd * (1 << lod) + (1 << lod);
            var baseC  = RegionBaseChunkCoord(r);
            int hs     = 1 << lod;
            int nearX  = Mathf.Clamp(_lastPlayerChunk.x, baseC.x, baseC.x + hs - 1);
            int nearZ  = Mathf.Clamp(_lastPlayerChunk.z, baseC.z, baseC.z + hs - 1);
            int dist   = Mathf.Max(Mathf.Abs(nearX - _lastPlayerChunk.x),
                                   Mathf.Abs(nearZ - _lastPlayerChunk.z));
            if (dist > outerRadius) _scratchUnloadR.Add(r);
        }
        foreach (var r in _scratchUnloadR)
        { Destroy(_staleRegionMeshes[r]); _staleRegionMeshes.Remove(r); _drawListDirty = true; }
    }

    private void DrawAllMeshes()
    {
        if (chunkMaterial == null) return;
        var localToWorld = transform.localToWorldMatrix;

        if (_drawListDirty || localToWorld != _cachedL2W)
        {
            _cachedL2W = localToWorld;
            _drawListDirty = false;

            _chunkDrawList.Clear();
            foreach (var kvp in _staleChunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(ChunkToWorldPos(kvp.Key))));
            foreach (var kvp in _chunkMeshes)
                if (kvp.Value != null)
                    _chunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(ChunkToWorldPos(kvp.Key))));

            _regionDrawList.Clear();
            foreach (var kvp in _staleRegionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(RegionWorldPos(kvp.Key))));
            foreach (var kvp in _regionMeshes)
                if (kvp.Value != null)
                    _regionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(RegionWorldPos(kvp.Key))));

            _transChunkDrawList.Clear();
            foreach (var kvp in _transChunkMeshes)
                if (kvp.Value != null)
                    _transChunkDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(ChunkToWorldPos(kvp.Key))));

            _transRegionDrawList.Clear();
            foreach (var kvp in _transRegionMeshes)
                if (kvp.Value != null)
                    _transRegionDrawList.Add((kvp.Value, localToWorld * Matrix4x4.Translate(RegionWorldPos(kvp.Key))));
        }

        var transMat = transparentMaterial != null ? transparentMaterial : chunkMaterial;
        int layer = gameObject.layer;
        foreach (var (mesh, trs) in _chunkDrawList)
            Graphics.DrawMesh(mesh, trs, chunkMaterial, layer);
        foreach (var (mesh, trs) in _regionDrawList)
            Graphics.DrawMesh(mesh, trs, chunkMaterial, layer);
        // Transparent pass — drawn after opaque so blending works correctly
        foreach (var (mesh, trs) in _transChunkDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
        foreach (var (mesh, trs) in _transRegionDrawList)
            Graphics.DrawMesh(mesh, trs, transMat, layer);
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

    public RegionData GetRegion(Vector3Int regionCoord) =>
        _regions.TryGetValue(regionCoord, out var r) ? r : null;

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private static Vector3Int WorldToChunkCoord(Vector3 p)
    {
        int s = VoxelChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / s), 0, Mathf.FloorToInt(p.z / s));
    }

    private static Vector3Int ChunkToRegionCoord(Vector3Int chunk, int lodLevel)
    {
        int size = 1 << lodLevel;
        return new Vector3Int(
            Mathf.FloorToInt(chunk.x / (float)size),
            lodLevel,
            Mathf.FloorToInt(chunk.z / (float)size));
    }

    private static Vector3Int RegionBaseChunkCoord(Vector3Int regionCoord)
    {
        int size = 1 << regionCoord.y;
        return new Vector3Int(regionCoord.x * size, 0, regionCoord.z * size);
    }

    private static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    private static Vector3 RegionWorldPos(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s, 0, regionCoord.z * s);
    }

    private static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s + s * .5f, c.y * s + s * .5f, c.z * s + s * .5f);
    }

    private static Vector3 RegionCenterWorld(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s + s * .5f, 0, regionCoord.z * s + s * .5f);
    }
}

// ── Extension helper ──────────────────────────────────────────────────────────

internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
