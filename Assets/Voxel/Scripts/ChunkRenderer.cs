using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Raw mesh arrays produced on a background thread (sync/editor path only).
/// Passed to ApplyMeshData on the main thread to create the Unity Mesh.
/// Bounds are pre-computed so the main thread can skip RecalculateBounds.
/// </summary>
public sealed class MeshData
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector2[] UVs;
    public readonly int[]     Triangles;
    public readonly Bounds    Bounds;

    public MeshData(Vector3[] v, Vector3[] n, Vector2[] u, int[] t, Bounds bounds)
    { Vertices = v; Normals = n; UVs = u; Triangles = t; Bounds = bounds; }

    public bool IsEmpty => Vertices.Length == 0;
}

/// <summary>
/// Holds greedy-mesh data (managed arrays) built on a background thread.
/// Call Apply() on the main thread to pack into GPU memory and get a ready Mesh.
/// AllocateWritableMeshData is main-thread-only, so packing happens in Apply().
/// Call Discard() to abandon without uploading (GC cleans up the managed arrays).
/// </summary>
public sealed class WritableMeshData
{
    private readonly Vector3[] _vertices;
    private readonly Vector3[] _normals;
    private readonly Vector2[] _uvs;
    private readonly int[]     _triangles;
    public  readonly Bounds    Bounds;

    internal WritableMeshData(Vector3[] v, Vector3[] n, Vector2[] u, int[] t, Bounds bounds)
    { _vertices = v; _normals = n; _uvs = u; _triangles = t; Bounds = bounds; }

    /// <summary>
    /// Packs vertex data into GPU memory and uploads the mesh. Must be called on the main thread.
    /// AllocateWritableMeshData is main-thread-only; all greedy-mesh CPU work already happened
    /// on the background thread and is stored here as managed arrays.
    /// </summary>
    public Mesh Apply()
    {
        bool use32 = _vertices.Length > ushort.MaxValue;
        var mda = Mesh.AllocateWritableMeshData(1);
        var md  = mda[0];

        md.SetVertexBufferParams(_vertices.Length,
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 2));
        md.SetIndexBufferParams(_triangles.Length, use32 ? IndexFormat.UInt32 : IndexFormat.UInt16);

        var positions = md.GetVertexData<Vector3>(0);
        var normals   = md.GetVertexData<Vector3>(1);
        var uvCoords  = md.GetVertexData<Vector2>(2);
        for (int i = 0; i < _vertices.Length; i++) { positions[i] = _vertices[i]; normals[i] = _normals[i]; uvCoords[i] = _uvs[i]; }

        if (use32) { var idx = md.GetIndexData<int>();    for (int i = 0; i < _triangles.Length; i++) idx[i] = _triangles[i]; }
        else       { var idx = md.GetIndexData<ushort>(); for (int i = 0; i < _triangles.Length; i++) idx[i] = (ushort)_triangles[i]; }

        md.subMeshCount = 1;
        md.SetSubMesh(0, new SubMeshDescriptor(0, _triangles.Length),
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

        var mesh = new Mesh { name = "Chunk" };
        Mesh.ApplyAndDisposeWritableMeshData(mda, mesh,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        mesh.bounds = Bounds;
        return mesh;
    }

    /// <summary>Discard without uploading. Managed arrays are GC-collected automatically.</summary>
    public void Discard() { /* managed arrays — GC handles cleanup */ }
}

/// <summary>
/// Builds a greedy mesh from a PaletteChunk at a given LOD level.
///
/// LOD step = 2^lodLevel:
///   LOD 0 → step 1  → 16³ samples (full detail)
///   LOD 1 → step 2  → 8³  samples
///   LOD 2 → step 4  → 4³  samples
///   LOD 3 → step 8  → 2³  samples
///
/// Two paths:
///   Sync  (editor)  — Render()        uses static buffers, zero allocation.
///   Async (runtime) — BuildMeshData() uses local buffers, fully thread-safe.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ChunkRenderer : MonoBehaviour
{
    private MeshFilter   _meshFilter;
    private MeshRenderer _meshRenderer;

    private void Awake()
    {
        _meshFilter   = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Synchronous render — safe to call from the main thread only (editor use).</summary>
    public void Render(PaletteChunk chunk, Vector3Int coord,
                       Func<Vector3Int, PaletteChunk> getNeighbour, Material material,
                       int lodLevel = 0)
    {
        if (_meshFilter   == null) _meshFilter   = GetComponent<MeshFilter>();
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

        var neighbours = FetchNeighbours(coord, getNeighbour);
        int step       = 1 << lodLevel;

        _verts.Clear(); _norms.Clear(); _uvs.Clear(); _tris.Clear();
        RunGreedyMesh(chunk, neighbours, _verts, _norms, _uvs, _tris, _maskSync, step);

        _meshRenderer.sharedMaterial = material;
        _meshFilter.sharedMesh = UploadMesh(_verts, _norms, _uvs, _tris);
    }

    /// <summary>
    /// Builds a WritableMeshData on any thread — pre-packs vertices into unmanaged memory
    /// so the main thread only needs an O(1) pointer handoff via Apply().
    /// Returns null if the chunk has no visible faces (fully hidden or fully air).
    /// neighbours[0..5] = +X,-X,+Y,-Y,+Z,-Z adjacent chunks (may be null).
    /// lodLevel 0 = full detail, 1 = half, 2 = quarter, 3 = eighth.
    /// </summary>
    public static WritableMeshData BuildMeshData(PaletteChunk chunk, PaletteChunk[] neighbours,
                                                  int lodLevel = 0)
    {
        EnsureThreadLocalBuffers(PaletteChunk.Size * PaletteChunk.Size);
        int step = 1 << lodLevel;
        RunGreedyMesh(chunk, neighbours, _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        int s = PaletteChunk.Size;
        var bounds = new Bounds(new Vector3(s * .5f, s * .5f, s * .5f), new Vector3(s, s, s));
        return PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _trisTS, bounds);
    }

    /// <summary>
    /// Creates a Mesh from WritableMeshData via O(1) pointer handoff. Must be called on the main thread.
    /// Returns null if data is null (empty mesh). Consumes data — do not use it again.
    /// </summary>
    public static Mesh CreateMesh(WritableMeshData data) => data?.Apply();

    /// <summary>Creates a Unity Mesh from pre-built MeshData without a GameObject. Must be called on the main thread.</summary>
    public static Mesh CreateMesh(MeshData data)
    {
        if (data.IsEmpty) return null;
        var fmt = data.Vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        return UploadMesh(data.Vertices, data.Normals, data.UVs, data.Triangles, data.Bounds, fmt);
    }

    /// <summary>Applies pre-built MeshData to this renderer. Must be called on the main thread.</summary>
    public void ApplyMeshData(MeshData data, Material material)
    {
        if (_meshFilter   == null) _meshFilter   = GetComponent<MeshFilter>();
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

        _meshRenderer.sharedMaterial = material;
        if (data.IsEmpty) { _meshFilter.sharedMesh = null; return; }
        var fmt = data.Vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _meshFilter.sharedMesh = UploadMesh(data.Vertices, data.Normals, data.UVs, data.Triangles, data.Bounds, fmt);
    }

    public void Clear()
    {
        if (_meshFilter != null) _meshFilter.sharedMesh = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static readonly Vector3Int[] NeighbourDirs =
    {
        Vector3Int.right, Vector3Int.left,
        Vector3Int.up,    Vector3Int.down,
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    public static PaletteChunk[] FetchNeighbours(Vector3Int coord,
                                                   Func<Vector3Int, PaletteChunk> getNeighbour)
    {
        var n = new PaletteChunk[6];
        for (int i = 0; i < 6; i++) n[i] = getNeighbour(coord + NeighbourDirs[i]);
        return n;
    }

    // ── Sync-path static buffers (main thread only) ───────────────────────────

    private static readonly List<Vector3> _verts    = new List<Vector3>();
    private static readonly List<Vector3> _norms    = new List<Vector3>();
    private static readonly List<Vector2> _uvs      = new List<Vector2>();
    private static readonly List<int>     _tris     = new List<int>();
    private static readonly byte[]        _maskSync = new byte[PaletteChunk.Size * PaletteChunk.Size];

    // ── Async-path thread-local buffers (one set per worker thread) ───────────
    // [ThreadStatic] avoids allocating new List<T> instances on every BuildMeshData /
    // BuildRegionMeshData call while remaining thread-safe. Fields cannot have
    // initializers with [ThreadStatic], so each accessor null-checks on first use.

    [ThreadStatic] private static List<Vector3> _vertsTS;
    [ThreadStatic] private static List<Vector3> _normsTS;
    [ThreadStatic] private static List<Vector2> _uvsTS;
    [ThreadStatic] private static List<int>     _trisTS;
    [ThreadStatic] private static byte[]        _maskTS;

    private static void EnsureThreadLocalBuffers(int minMaskSize)
    {
        if (_vertsTS == null) { _vertsTS = new(); _normsTS = new(); _uvsTS = new(); _trisTS = new(); }
        _vertsTS.Clear(); _normsTS.Clear(); _uvsTS.Clear(); _trisTS.Clear();
        if (_maskTS == null || _maskTS.Length < minMaskSize)
            _maskTS = new byte[minMaskSize];
    }

    // ── Core greedy mesher ────────────────────────────────────────────────────

    private static void RunGreedyMesh(
        PaletteChunk chunk, PaletteChunk[] neighbours,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        byte[] mask, int step)
    {
        GreedyMeshFace(chunk, neighbours[0], 0, 1, 2, Vector3.right,   false, verts, norms, uvs, tris, mask, step);
        GreedyMeshFace(chunk, neighbours[1], 0, 1, 2, Vector3.left,    true,  verts, norms, uvs, tris, mask, step);
        GreedyMeshFace(chunk, neighbours[2], 1, 2, 0, Vector3.up,      false, verts, norms, uvs, tris, mask, step);
        GreedyMeshFace(chunk, neighbours[3], 1, 2, 0, Vector3.down,    true,  verts, norms, uvs, tris, mask, step);
        GreedyMeshFace(chunk, neighbours[4], 2, 0, 1, Vector3.forward, false, verts, norms, uvs, tris, mask, step);
        GreedyMeshFace(chunk, neighbours[5], 2, 0, 1, Vector3.back,    true,  verts, norms, uvs, tris, mask, step);
    }

    /// <summary>
    /// Greedy mesher for one face direction with LOD support.
    /// step = 2^lodLevel. Mask operates in "cell space" (size/step cells per axis).
    /// Resulting quads are scaled by step to cover the correct world area.
    /// </summary>
    private static void GreedyMeshFace(
        PaletteChunk chunk, PaletteChunk neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        byte[] mask, int step)
    {
        int size  = PaletteChunk.Size;
        int cells = size / step; // cells per axis at this LOD (16, 8, 4, 2)
        Span<int> pos = stackalloc int[3]; // stack-allocated: avoids 6 heap allocs per chunk build

        for (int slice = 0; slice < cells; slice++)
        {
            // ── Build mask in cell space ──────────────────────────────────────
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
                // Sample corner voxel of this cell
                pos[sliceAxis] = slice * step;
                pos[uAxis]     = u     * step;
                pos[vAxis]     = v     * step;

                byte here = chunk.GetBlock(pos[0], pos[1], pos[2]);
                if (here == BlockType.Air) { mask[u + v * cells] = 0; continue; }

                // Check the adjacent cell in the face's normal direction
                int neighborCell = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborCell >= 0 && neighborCell < cells)
                {
                    pos[sliceAxis] = neighborCell * step;
                    neighborSolid  = chunk.IsSolid(pos[0], pos[1], pos[2]);
                }
                else
                {
                    // Cross-chunk boundary: sample the first/last cell of the neighbour
                    pos[sliceAxis] = neighborCell < 0 ? size - step : 0;
                    neighborSolid  = neighbour != null && neighbour.IsSolid(pos[0], pos[1], pos[2]);
                    // Water at a chunk boundary with no loaded neighbour: treat as solid to prevent
                    // Z-fighting. Two chunks with water columns each render a face at the same
                    // world position when loaded out of order. The face reappears once both load.
                    if (here == BlockType.Water && neighbour == null)
                        neighborSolid = true;
                }

                mask[u + v * cells] = neighborSolid ? (byte)0 : here;
            }

            // ── Greedy merge in cell space ────────────────────────────────────
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; )
            {
                byte blockType = mask[u + v * cells];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cells && mask[u + w + v * cells] == blockType) w++;

                int h = 1; bool heightDone = false;
                while (v + h < cells && !heightDone)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cells] != blockType) { heightDone = true; break; }
                    if (!heightDone) h++;
                }

                // Convert cell coords → world coords (multiply by step)
                pos[sliceAxis] = slice * step + (backFace ? 0 : step);
                pos[uAxis]     = u     * step;
                pos[vAxis]     = v     * step;

                var corner = new Vector3(pos[0], pos[1], pos[2]);
                var du = Vector3.zero; du[uAxis] = w * step; // quad width  in world units
                var dv = Vector3.zero; dv[vAxis] = h * step; // quad height in world units

                int idx = verts.Count;
                verts.Add(corner); verts.Add(corner + du);
                verts.Add(corner + du + dv); verts.Add(corner + dv);
                norms.Add(normalVec); norms.Add(normalVec);
                norms.Add(normalVec); norms.Add(normalVec);

                float tileU = (blockType - 1 + 0.5f) / BlockType.AtlasTileCount;
                uvs.Add(new Vector2(tileU, 0f)); uvs.Add(new Vector2(tileU, 0f));
                uvs.Add(new Vector2(tileU, 1f)); uvs.Add(new Vector2(tileU, 1f));

                if (backFace)
                { tris.Add(idx); tris.Add(idx+2); tris.Add(idx+1); tris.Add(idx); tris.Add(idx+3); tris.Add(idx+2); }
                else
                { tris.Add(idx); tris.Add(idx+1); tris.Add(idx+2); tris.Add(idx); tris.Add(idx+2); tris.Add(idx+3); }

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    mask[u + uu + (v + vv) * cells] = 0;

                u += w;
            }
        }
    }

    // ── Region mesher (64×vc*16×64 voxel space) ──────────────────────────────

    // Neighbour directions for regions (horizontal only — regions span full height)
    public static readonly Vector3Int[] RegionNeighbourDirs =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.up,           // no vertical region neighbours — will be null
        Vector3Int.down,
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0,-1),
    };

    /// <summary>
    /// Builds a region mesh on any thread, pre-packed into unmanaged memory.
    /// Returns null if the region has no visible faces.
    /// Neighbours are horizontally adjacent regions (+X,-X,+Z,-Z); ±Y entries are ignored.
    /// step = voxels per LOD cell (2/4/8 for LOD 1/2/3).
    /// </summary>
    public static WritableMeshData BuildRegionMeshData(RegionData region, RegionData[] neighbours, int step)
    {
        int maskSize = Mathf.Max(
            (region.VoxelSizeY / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeY / step));
        EnsureThreadLocalBuffers(maskSize);

        GreedyMeshFaceRegion(region, neighbours[0], 0, 1, 2, Vector3.right,   false, _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[1], 0, 1, 2, Vector3.left,    true,  _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[2], 1, 2, 0, Vector3.up,      false, _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[3], 1, 2, 0, Vector3.down,    true,  _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[4], 2, 0, 1, Vector3.forward, false, _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[5], 2, 0, 1, Vector3.back,    true,  _vertsTS, _normsTS, _uvsTS, _trisTS, _maskTS, step);

        float sx = region.VoxelSizeX, sy = region.VoxelSizeY, sz = region.VoxelSizeZ;
        var bounds = new Bounds(new Vector3(sx * .5f, sy * .5f, sz * .5f), new Vector3(sx, sy, sz));
        return PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _trisTS, bounds);
    }

    /// <summary>
    /// Greedy face mesher for a RegionData.
    /// Operates in the region's variable-size voxel space; mask is indexed in cell space.
    /// </summary>
    private static void GreedyMeshFaceRegion(
        RegionData region, RegionData neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        byte[] mask, int step)
    {
        Span<int> sizes = stackalloc int[3] { region.VoxelSizeX, region.VoxelSizeY, region.VoxelSizeZ };
        int cellsSlice = sizes[sliceAxis] / step;
        int cellsU     = sizes[uAxis]     / step;
        int cellsV     = sizes[vAxis]     / step;
        Span<int> pos  = stackalloc int[3]; // stack-allocated: no heap alloc per face

        for (int slice = 0; slice < cellsSlice; slice++)
        {
            // Build mask in cell space
            for (int v = 0; v < cellsV; v++)
            for (int u = 0; u < cellsU; u++)
            {
                pos[sliceAxis] = slice * step;
                pos[uAxis]     = u     * step;
                pos[vAxis]     = v     * step;

                // Coords (slice*step, u*step, v*step) are always within the region's bounds,
                // so skip the 6-comparison bounds check in the hot inner loop.
                byte here = region.GetBlockUnchecked(pos[0], pos[1], pos[2]);
                if (here == BlockType.Air) { mask[u + v * cellsU] = 0; continue; }

                int  neighborCell  = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborCell >= 0 && neighborCell < cellsSlice)
                {
                    pos[sliceAxis] = neighborCell * step;
                    neighborSolid  = region.IsSolidUnchecked(pos[0], pos[1], pos[2]);
                }
                else
                {
                    // Cross-region boundary — sample the first/last cell of the neighbour.
                    // Resulting coords are always within the neighbour's bounds (same region size).
                    pos[sliceAxis] = neighborCell < 0 ? sizes[sliceAxis] - step : 0;
                    neighborSolid  = neighbour != null && neighbour.IsSolidUnchecked(pos[0], pos[1], pos[2]);
                    // Same water boundary fix as the chunk mesher above
                    if (here == BlockType.Water && neighbour == null)
                        neighborSolid = true;
                }

                mask[u + v * cellsU] = neighborSolid ? (byte)0 : here;
            }

            // Greedy merge
            for (int v = 0; v < cellsV; v++)
            for (int u = 0; u < cellsU; )
            {
                byte blockType = mask[u + v * cellsU];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cellsU && mask[u + w + v * cellsU] == blockType) w++;

                int h = 1; bool heightDone = false;
                while (v + h < cellsV && !heightDone)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cellsU] != blockType) { heightDone = true; break; }
                    if (!heightDone) h++;
                }

                pos[sliceAxis] = slice * step + (backFace ? 0 : step);
                pos[uAxis]     = u     * step;
                pos[vAxis]     = v     * step;

                var corner = new Vector3(pos[0], pos[1], pos[2]);
                var du = Vector3.zero; du[uAxis] = w * step;
                var dv = Vector3.zero; dv[vAxis] = h * step;

                int idx = verts.Count;
                verts.Add(corner); verts.Add(corner + du);
                verts.Add(corner + du + dv); verts.Add(corner + dv);
                norms.Add(normalVec); norms.Add(normalVec);
                norms.Add(normalVec); norms.Add(normalVec);

                float tileU = (blockType - 1 + 0.5f) / BlockType.AtlasTileCount;
                uvs.Add(new Vector2(tileU, 0f)); uvs.Add(new Vector2(tileU, 0f));
                uvs.Add(new Vector2(tileU, 1f)); uvs.Add(new Vector2(tileU, 1f));

                if (backFace)
                { tris.Add(idx); tris.Add(idx+2); tris.Add(idx+1); tris.Add(idx); tris.Add(idx+3); tris.Add(idx+2); }
                else
                { tris.Add(idx); tris.Add(idx+1); tris.Add(idx+2); tris.Add(idx); tris.Add(idx+2); tris.Add(idx+3); }

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    mask[u + uu + (v + vv) * cellsU] = 0;

                u += w;
            }
        }
    }

    // ── Shared upload helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Converts thread-local lists into managed arrays on the background thread.
    /// AllocateWritableMeshData is main-thread-only, so it runs inside Apply() instead.
    /// Returns null when the vertex list is empty (no visible faces).
    /// </summary>
    private static WritableMeshData PackToWritableMesh(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris, Bounds bounds)
    {
        if (verts.Count == 0) return null;
        return new WritableMeshData(verts.ToArray(), norms.ToArray(), uvs.ToArray(), tris.ToArray(), bounds);
    }

    private static Mesh UploadMesh(List<Vector3> verts, List<Vector3> norms,
                                    List<Vector2> uvs,   List<int>     tris)
    {
        var mesh = new Mesh { name = "Chunk" };
        mesh.indexFormat = IndexFormat.UInt16;
        mesh.SetVertices(verts); mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        return mesh;
    }

    private static Mesh UploadMesh(Vector3[] verts, Vector3[] norms, Vector2[] uvs, int[] tris,
                                    Bounds bounds, IndexFormat fmt = IndexFormat.UInt16)
    {
        var mesh = new Mesh { name = "Chunk" };
        mesh.indexFormat = fmt;
        mesh.SetVertices(verts); mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        // Bounds were pre-computed on the background thread — skip the O(vertices) RecalculateBounds.
        mesh.bounds = bounds;
        mesh.UploadMeshData(true);
        return mesh;
    }
}
