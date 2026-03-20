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
    private readonly NativeArray<float2>     _v2CSpline;
    private readonly NativeArray<float2>     _v2ESpline;
    private readonly NativeArray<BiomeDef>   _v2Biomes;
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

    /// <summary>
    /// Submits coords in sub-batches of <see cref="VoxelWorld.terrainBatchSize"/>.
    /// Each sub-batch gets one GenerateChunksBatchJob (IJobParallelFor) for terrain, then
    /// individual BuildChunkMeshJob instances chained to that sub-batch's terrain handle.
    /// Splitting prevents head-of-line blocking: mesh jobs for sub-batch 0 can start as soon
    /// as its terrain finishes, while sub-batch 1's terrain is still in flight.
    /// </summary>
    public void SubmitChunkBatch(List<Vector3Int> coords, bool buildMesh)
    {
        if (coords.Count == 0) return;

        int n         = coords.Count;
        int subSize   = math.max(1, _world.terrainBatchSize);
        var settings  = _world.GetTerrainSettings();

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
        if (_world.useV2Generator)
        {
            var terrainJob = new GenerateChunksBatchJobV2
            {
                Settings           = _world.GetTerrainSettingsV2(),
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
            if (_world.useV2Generator && buildMesh)
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

            if (!buildMesh || _world.useV2Generator)
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
    public bool ProcessCompletedPipelines()
    {
        _scratchCompleted.Clear();
        for (int i = 0; i < _pipelines.Count; i++)
            if (_pipelines[i].Handle.IsCompleted)
                _scratchCompleted.Add(i);

        if (_scratchCompleted.Count == 0) return false;

        // Sort: non-discarded closest-first; discarded last (always drained without budget cost)
        var pc = _world.LastPlayerChunk;
        _scratchCompleted.Sort((a, b) =>
        {
            var pa = _pipelines[a]; var pb = _pipelines[b];
            if (pa.Discarded != pb.Discarded) return pa.Discarded ? 1 : -1;
            int da = Mathf.Max(Mathf.Abs(pa.Coord.x - pc.x), Mathf.Abs(pa.Coord.z - pc.z));
            int db = Mathf.Max(Mathf.Abs(pb.Coord.x - pc.x), Mathf.Abs(pb.Coord.z - pc.z));
            return da.CompareTo(db);
        });

        int  budget    = _world.maxApplyPerFrame;
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

            // For mesh-only pipelines (BuildMesh=true, Batch=null) p.Voxels is a temporary
            // copy used by the mesh job. We must NOT write it back: neighbouring decorations
            // may have placed cross-boundary tree blocks into _chunks[coord] after
            // SubmitMeshOnly was called, and the snapshot would silently destroy them.
            VoxelChunk chunk;
            if (p.BuildMesh && p.Batch == null)
            {
                if (p.Voxels.IsCreated) p.Voxels.Dispose();
                _chunks.TryGetValue(p.Coord, out chunk); // use live (decorated) data
            }
            else
            {
                // Copy voxels to an owned NativeArray for VoxelChunk storage.
                // p.Voxels may be a sub-array view into a BatchBuffer; copying ensures VoxelChunk
                // lifetime is independent of the batch buffer.
                var ownedVoxels = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                                         NativeArrayOptions.UninitializedMemory);
                NativeArray<byte>.Copy(p.Voxels, ownedVoxels, VoxelChunk.VoxelCount);
                if (p.Batch != null) p.Batch.Release(); else if (p.Voxels.IsCreated) p.Voxels.Dispose();
                chunk = new VoxelChunk { Blocks = ownedVoxels };
                if (_chunks.TryGetValue(p.Coord, out var old)) old.Dispose();
                _chunks[p.Coord] = chunk;
            }

            // V2 terrain-only pipeline completes → queue for decoration gate
            if (_world.useV2Generator && !p.BuildMesh)
            {
                _awaitingDecoration.Add(p.Coord);
                // Keep coord in _inFlight until SubmitMeshOnly completes (or data-only done in TryDecorateReady)
                // DO NOT call _inFlight.Remove or _onChunkReady here
                _flags.NeedsMoreRequests = true;
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
                { Object.Destroy(stale); _staleChunkMeshes.Remove(p.Coord); }
                // Remove stale regions of any LOD that contained this chunk
                for (int lod = 1; lod <= _world.lodLevels; lod++)
                {
                    var rc = VoxelCoords.ChunkToRegionCoord(p.Coord, lod);
                    if (_staleRegionMeshes.TryGetValue(rc, out var sr))
                    { Object.Destroy(sr); _staleRegionMeshes.Remove(rc); }
                }
                _flags.DrawListDirty = true;
            }
            else
                DisposeMeshLists(ref p);

            _inFlight.Remove(p.Coord);
            _onChunkReady(p.Coord, chunk);

            _flags.NeedsMoreRequests = true;
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
        if (!_world.useV2Generator || _awaitingDecoration.Count == 0) return;

        var ready = new List<Vector3Int>();
        foreach (var coord in _awaitingDecoration)
        {
            if (!ChunkDecorator.AllNeighboursReady(coord, _chunks)) continue;
            // Also require the chunk above so trees extending upward aren't cut off.
            var aboveCoord = new Vector3Int(coord.x, coord.y + 1, coord.z);
            if (coord.y + 1 < _world.verticalChunks && !_chunks.ContainsKey(aboveCoord)) continue;
            ready.Add(coord);
        }

        int decorated = 0;
        foreach (var coord in ready)
        {
            if (decorated >= _world.maxDecorationsPerFrame) break;

            ChunkDecorator.Decorate(coord, _chunks, _world.v2TreeConfigs, _v2Biomes, _world.GetTerrainSettingsV2(), _world.verticalChunks);
            _awaitingDecoration.Remove(coord);
            decorated++;

            // Feed into region AFTER decoration so region gets decorated voxels
            _onChunkReady(coord, _chunks[coord]);

            bool wantsMesh = _needsMeshAfterDecoration.Remove(coord)
                          && _desiredCoords.Contains(coord)
                          && !_chunkMeshes.ContainsKey(coord);

            if (wantsMesh)
                SubmitMeshOnly(coord); // coord stays in _inFlight until mesh pipeline completes
            else
                _inFlight.Remove(coord); // data-only — fully done

            _flags.NeedsMoreRequests = true;
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

    // ── Lifecycle ─────────────────────────────────────────────────────────────

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
