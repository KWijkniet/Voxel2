using System.Diagnostics;
using System.Text;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Spawns a single chunk using the same pipeline as VoxelWorld and records
/// wall-clock timings for every major step. Attach to any GameObject in the scene,
/// assign the VoxelWorld reference (for terrain settings + material), then press Play
/// or use the context menu "Run Benchmark".
///
/// Results are shown as an on-screen overlay and printed to the console.
/// </summary>
public class ChunkBenchmark : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Used to pull terrain settings and chunk material. Must be in the scene.")]
    public VoxelWorld world;

    [Tooltip("Which chunk coordinate to generate.")]
    public Vector3Int chunkCoord = Vector3Int.zero;

    [Tooltip("Run automatically when the scene starts.")]
    public bool runOnStart = true;

    // ── Results ───────────────────────────────────────────────────────────────

    private string  _report    = "Press Play or right-click → Run Benchmark.";
    private bool    _ran       = false;
    private Vector2 _scroll;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (runOnStart) RunBenchmark();
    }

    // ── Benchmark ─────────────────────────────────────────────────────────────

    [ContextMenu("Run Benchmark")]
    public void RunBenchmark()
    {
        if (world == null)
        {
            _report = "[ChunkBenchmark] ERROR: VoxelWorld reference is not set.";
            UnityEngine.Debug.LogError(_report);
            return;
        }

        var sb     = new StringBuilder();
        var total  = Stopwatch.StartNew();

        sb.AppendLine("=== Chunk Benchmark ===");
        sb.AppendLine($"Coord: {chunkCoord}   Unity version: {Application.unityVersion}");
        sb.AppendLine();

        // ── Step 1: schedule Burst terrain job ───────────────────────────────

        TerrainSettings settings = world.GetTerrainSettings();
        int size     = PaletteChunk.Size;
        var blocks   = new NativeArray<byte>(size * size * size, Allocator.Persistent);

        var scheduleTimer = Stopwatch.StartNew();

        var job = new GenerateChunkJob
        {
            Settings   = settings,
            ChunkCoord = new int3(chunkCoord.x, chunkCoord.y, chunkCoord.z),
            LowDetail  = false,
            Blocks     = blocks,
        };
        var handle = job.Schedule();
        JobHandle.ScheduleBatchedJobs(); // flush job queue immediately

        scheduleTimer.Stop();
        AppendLine(sb, "1. Job schedule",   scheduleTimer);

        // ── Step 2: wait for Burst terrain job to complete ───────────────────

        var executeTimer = Stopwatch.StartNew();
        handle.Complete();
        executeTimer.Stop();
        AppendLine(sb, "2. Terrain gen (Burst, incl. worker wait)", executeTimer);

        // ── Step 3: build PaletteChunk from raw block data ───────────────────

        var paletteTimer = Stopwatch.StartNew();
        var chunk = BuildPaletteChunk(blocks);
        paletteTimer.Stop();
        blocks.Dispose();

        int nonAirVoxels = CountNonAir(chunk);
        AppendLine(sb, "3. Palette chunk build (SeedPalette + SetBlock × 4096)", paletteTimer,
                   $"{nonAirVoxels} / 4096 non-air voxels");

        // ── Step 4: greedy mesh generation ───────────────────────────────────

        // Neighbours are null here (standalone benchmark); real world would load them.
        var neighbours  = new PaletteChunk[6];
        var meshTimer   = Stopwatch.StartNew();
        var meshData    = ChunkRenderer.BuildMeshData(chunk, neighbours, 0);
        meshTimer.Stop();

        int vertCount = meshData.IsEmpty ? 0 : meshData.Vertices.Length;
        int triCount  = meshData.IsEmpty ? 0 : meshData.Triangles.Length / 3;
        AppendLine(sb, "4. Greedy mesh generation", meshTimer,
                   $"{vertCount} verts, {triCount} tris");

        // ── Step 5: GameObject + mesh upload (main thread) ───────────────────

        var uploadTimer = Stopwatch.StartNew();

        var go = new GameObject($"Benchmark Chunk {chunkCoord.x},{chunkCoord.y},{chunkCoord.z}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(
            chunkCoord.x * size, chunkCoord.y * size, chunkCoord.z * size);
        go.AddComponent<ChunkRenderer>().ApplyMeshData(meshData, world.chunkMaterial);

        uploadTimer.Stop();
        AppendLine(sb, "5. Mesh upload + GameObject (main thread)", uploadTimer);

        // ── Total ─────────────────────────────────────────────────────────────

        total.Stop();
        sb.AppendLine();
        sb.AppendLine($"  TOTAL: {total.Elapsed.TotalMilliseconds:F3} ms");
        sb.AppendLine();
        sb.AppendLine("Note: neighbours were null — outer faces not culled.");
        sb.AppendLine("Step 2 includes time the main thread waited for the worker thread.");

        _report = sb.ToString();
        _ran    = true;
        UnityEngine.Debug.Log(_report);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AppendLine(StringBuilder sb, string label, Stopwatch sw,
                                   string extra = null)
    {
        string time = $"{sw.Elapsed.TotalMilliseconds,8:F3} ms";
        sb.Append($"  {time}  {label}");
        if (extra != null) sb.Append($"  ({extra})");
        sb.AppendLine();
    }

    /// <summary>Replicates VoxelWorld.BuildPaletteChunkFromBlocks.</summary>
    private static PaletteChunk BuildPaletteChunk(NativeArray<byte> blocks)
    {
        var chunk = new PaletteChunk();
        chunk.SeedPalette(BlockType.Stone, BlockType.Dirt, BlockType.Grass,
                          BlockType.Sand,  BlockType.Water, BlockType.Snow);
        int s = PaletteChunk.Size;
        for (int z = 0; z < s; z++)
        for (int x = 0; x < s; x++)
        for (int y = 0; y < s; y++)
        {
            byte b = blocks[x + y * s + z * s * s];
            if (b != BlockType.Air) chunk.SetBlock(x, y, z, b);
        }
        return chunk;
    }

    private static int CountNonAir(PaletteChunk chunk)
    {
        int count = 0;
        int s = PaletteChunk.Size;
        for (int z = 0; z < s; z++)
        for (int x = 0; x < s; x++)
        for (int y = 0; y < s; y++)
            if (chunk.GetBlock(x, y, z) != BlockType.Air) count++;
        return count;
    }

    // ── On-screen overlay ─────────────────────────────────────────────────────

    private void OnGUI()
    {
        float w = Mathf.Min(620f, Screen.width - 20f);
        float h = Mathf.Min(320f, Screen.height - 20f);
        GUI.Box(new Rect(10, 10, w, h), GUIContent.none);

        GUILayout.BeginArea(new Rect(14, 14, w - 8, h - 8));
        _scroll = GUILayout.BeginScrollView(_scroll);
        GUILayout.Label(_report);
        GUILayout.EndScrollView();

        if (!_ran && GUILayout.Button("Run Benchmark"))
            RunBenchmark();
        GUILayout.EndArea();
    }
}
