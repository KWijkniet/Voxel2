using System.Diagnostics;
using System.Text;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;

/// <summary>
/// Benchmarks the full LOD 0 chunk pipeline using the same Burst job chain as VoxelWorld:
///   1. GenerateChunkJob  (Burst terrain → NativeArray&lt;byte&gt;)
///   2. BuildChunkMeshJob (Burst greedy mesh → NativeLists)
///   3. Mesh upload       (NativeLists → Mesh.AllocateWritableMeshData → ApplyAndDispose)
///
/// Attach to any GameObject, assign the VoxelWorld reference, then press Play or
/// use the context menu "Run Benchmark".
/// </summary>
public class ChunkBenchmark : MonoBehaviour
{
    [Header("Setup")]
    public VoxelWorld world;
    public Vector3Int chunkCoord = Vector3Int.zero;
    public bool       runOnStart = true;

    private string  _report = "Press Play or right-click → Run Benchmark.";
    private bool    _ran    = false;
    private Vector2 _scroll;

    private void Start() { if (runOnStart) RunBenchmark(); }

    [ContextMenu("Run Benchmark")]
    public void RunBenchmark()
    {
        if (world == null)
        {
            _report = "[ChunkBenchmark] VoxelWorld reference not set.";
            UnityEngine.Debug.LogError(_report);
            return;
        }

        var sb    = new StringBuilder();
        var total = Stopwatch.StartNew();
        sb.AppendLine("=== Chunk Benchmark (Approach D — Burst pipeline) ===");
        sb.AppendLine($"Coord: {chunkCoord}   Unity: {Application.unityVersion}");
        sb.AppendLine();

        // ── Step 1: Schedule terrain Burst job ───────────────────────────────
        var settings = world.GetTerrainSettings();
        var voxels   = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                              NativeArrayOptions.UninitializedMemory);

        var schedTimer = Stopwatch.StartNew();
        var terrainJob = new GenerateChunkJob
        {
            Settings   = settings,
            ChunkCoord = new int3(chunkCoord.x, chunkCoord.y, chunkCoord.z),
            LowDetail  = false,
            Blocks     = voxels,
        };
        var terrainHandle = terrainJob.Schedule();

        // ── Step 2: Schedule mesh Burst job (chained dependency) ─────────────
        // Neighbours are all-zero (air) — no loaded neighbours in standalone benchmark.
        var emptyNeighbour = new NativeArray<byte>(VoxelChunk.VoxelCount, Allocator.Persistent,
                                                    NativeArrayOptions.ClearMemory);

        var verts = new NativeList<float3>(4096, Allocator.Persistent);
        var norms = new NativeList<float3>(4096, Allocator.Persistent);
        var uvs   = new NativeList<float2>(4096, Allocator.Persistent);
        var tris  = new NativeList<int>   (6144, Allocator.Persistent);

        var meshJob = new BuildChunkMeshJob
        {
            Voxels = voxels,
            N_PX = emptyNeighbour, N_NX = emptyNeighbour,
            N_PY = emptyNeighbour, N_NY = emptyNeighbour,
            N_PZ = emptyNeighbour, N_NZ = emptyNeighbour,
            NeighbourMask = 0, // no real neighbours
            Step          = 1,
            Vertices      = verts, Normals = norms, UVs = uvs, Triangles = tris,
        };
        var meshHandle = meshJob.Schedule(terrainHandle);
        JobHandle.ScheduleBatchedJobs();
        schedTimer.Stop();
        AppendLine(sb, "1. Schedule terrain + mesh jobs (chained)", schedTimer);

        // ── Step 3: Wait for both jobs to complete ───────────────────────────
        var waitTimer = Stopwatch.StartNew();
        meshHandle.Complete();
        waitTimer.Stop();
        AppendLine(sb, "2. Burst execute (terrain + greedy mesh, incl. worker wait)", waitTimer,
                   $"{verts.Length} verts, {tris.Length / 3} tris");

        // ── Step 4: Upload mesh from NativeLists ─────────────────────────────
        var uploadTimer = Stopwatch.StartNew();
        Mesh mesh = null;
        if (verts.Length > 0)
        {
            bool use32 = verts.Length > ushort.MaxValue;
            var  mda   = Mesh.AllocateWritableMeshData(1);
            var  md    = mda[0];
            md.SetVertexBufferParams(verts.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 2));
            md.SetIndexBufferParams(tris.Length, use32 ? IndexFormat.UInt32 : IndexFormat.UInt16);
            md.GetVertexData<float3>(0).CopyFrom(verts.AsArray());
            md.GetVertexData<float3>(1).CopyFrom(norms.AsArray());
            md.GetVertexData<float2>(2).CopyFrom(uvs.AsArray());
            if (use32) { md.GetIndexData<int>().CopyFrom(tris.AsArray()); }
            else       { var idx = md.GetIndexData<ushort>(); for (int i = 0; i < tris.Length; i++) idx[i] = (ushort)tris[i]; }
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, tris.Length),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh = new Mesh { name = "BenchmarkChunk" };
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            int s = VoxelChunk.Size;
            mesh.bounds = new Bounds(new Vector3(s*.5f, s*.5f, s*.5f), new Vector3(s, s, s));
        }
        uploadTimer.Stop();
        AppendLine(sb, "3. Mesh upload (NativeList → AllocateWritableMeshData → Apply)", uploadTimer);

        // ── Cleanup ──────────────────────────────────────────────────────────
        voxels.Dispose();
        emptyNeighbour.Dispose();
        verts.Dispose(); norms.Dispose(); uvs.Dispose(); tris.Dispose();

        // Create a preview GameObject
        var go = new GameObject($"Benchmark Chunk {chunkCoord.x},{chunkCoord.y},{chunkCoord.z}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(
            chunkCoord.x * VoxelChunk.Size,
            chunkCoord.y * VoxelChunk.Size,
            chunkCoord.z * VoxelChunk.Size);
        if (mesh != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh      = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = world.chunkMaterial;
        }

        total.Stop();
        sb.AppendLine();
        sb.AppendLine($"  TOTAL: {total.Elapsed.TotalMilliseconds:F3} ms");
        sb.AppendLine();
        sb.AppendLine("Note: neighbours were null (all-air). Outer faces are not culled.");
        sb.AppendLine("Step 2 includes main-thread wait for worker threads.");

        _report = sb.ToString();
        _ran    = true;
        UnityEngine.Debug.Log(_report);
    }

    private static void AppendLine(StringBuilder sb, string label, Stopwatch sw, string extra = null)
    {
        sb.Append($"  {sw.Elapsed.TotalMilliseconds,8:F3} ms  {label}");
        if (extra != null) sb.Append($"  ({extra})");
        sb.AppendLine();
    }

    private void OnGUI()
    {
        float w = Mathf.Min(640f, Screen.width - 20f);
        float h = Mathf.Min(340f, Screen.height - 20f);
        GUI.Box(new Rect(10, 10, w, h), GUIContent.none);
        GUILayout.BeginArea(new Rect(14, 14, w - 8, h - 8));
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label(_report);
        GUILayout.EndScrollView();
        if (!_ran && GUILayout.Button("Run Benchmark")) RunBenchmark();
        GUILayout.EndArea();
    }
}
