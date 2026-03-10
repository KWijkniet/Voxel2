using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Raw mesh arrays produced on a background thread.
/// Passed to ApplyMeshData on the main thread to create the Unity Mesh.
/// </summary>
public sealed class MeshData
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector2[] UVs;
    public readonly int[]     Triangles;

    public MeshData(Vector3[] v, Vector3[] n, Vector2[] u, int[] t)
    { Vertices = v; Normals = n; UVs = u; Triangles = t; }

    public bool IsEmpty => Vertices.Length == 0;
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
    /// Builds raw mesh arrays — safe to call from any thread.
    /// neighbours[0..5] = +X,-X,+Y,-Y,+Z,-Z adjacent chunks (may be null).
    /// lodLevel 0 = full detail, 1 = half, 2 = quarter, 3 = eighth.
    /// </summary>
    public static MeshData BuildMeshData(PaletteChunk chunk, PaletteChunk[] neighbours,
                                          int lodLevel = 0)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();
        var mask  = new byte[PaletteChunk.Size * PaletteChunk.Size]; // 256 bytes, covers all LOD levels
        int step  = 1 << lodLevel;

        RunGreedyMesh(chunk, neighbours, verts, norms, uvs, tris, mask, step);

        return new MeshData(verts.ToArray(), norms.ToArray(), uvs.ToArray(), tris.ToArray());
    }

    /// <summary>Applies pre-built MeshData to this renderer. Must be called on the main thread.</summary>
    public void ApplyMeshData(MeshData data, Material material)
    {
        if (_meshFilter   == null) _meshFilter   = GetComponent<MeshFilter>();
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

        _meshRenderer.sharedMaterial = material;
        if (data.IsEmpty) { _meshFilter.sharedMesh = null; return; }
        _meshFilter.sharedMesh = UploadMesh(data.Vertices, data.Normals, data.UVs, data.Triangles);
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
        var pos   = new int[3];

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

    private static Mesh UploadMesh(Vector3[] verts, Vector3[] norms, Vector2[] uvs, int[] tris)
    {
        var mesh = new Mesh { name = "Chunk" };
        mesh.indexFormat = IndexFormat.UInt16;
        mesh.SetVertices(verts); mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        return mesh;
    }
}
