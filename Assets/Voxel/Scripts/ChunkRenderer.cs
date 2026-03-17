using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Static mesh builder — greedy mesher + mesh upload helpers.
///
/// Two callers:
///   Runtime LOD 0:  VoxelWorld uses BuildChunkMeshJob (Burst) instead, not this class.
///   Runtime LOD 1+: VoxelWorld calls BuildRegionMeshData on a background Task.Run thread.
///   Editor preview: VoxelWorldEditor calls BuildMeshData (sync) then CreateMesh.
///
/// All methods that build mesh data are thread-safe (ThreadStatic buffers for async paths,
/// static buffers for the sync editor path).
/// </summary>
public static class MeshBuilder
{
    // ── Neighbour directions (index matches BuildChunkMeshJob direction convention) ──
    // 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z
    public static readonly Vector3Int[] NeighbourDirs =
    {
        Vector3Int.right, Vector3Int.left,
        Vector3Int.up,    Vector3Int.down,
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    public static readonly Vector3Int[] RegionNeighbourDirs =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.up,           // no vertical region neighbours — will be null
        Vector3Int.down,
        new Vector3Int(0, 0,  1),
        new Vector3Int(0, 0, -1),
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies pre-built WritableMeshData to a Mesh via O(1) pointer handoff.
    /// Must be called on the main thread. Returns null for empty meshes.
    /// Consumes the data — do not use it again.
    /// </summary>
    public static Mesh CreateMesh(WritableMeshData data) => data?.Apply();

    /// <summary>
    /// Builds a WritableMeshData from a flat voxel byte[] on any thread.
    /// Used by the editor sync path and as a fallback.
    /// Returns null if the chunk has no visible faces.
    /// neighbours[0..5] = +X,-X,+Y,-Y,+Z,-Z adjacent chunk voxels (may be null = air).
    /// </summary>
    public static WritableMeshData BuildMeshData(byte[] voxels, byte[][] neighbours,
                                                  int lodLevel = 0)
    {
        int step  = 1 << lodLevel;
        int cells = VoxelChunk.Size / step;
        EnsureThreadLocalBuffers(cells * cells);

        GreedyMeshFace(voxels, neighbours[0], 0, 1, 2, Vector3.right,   false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFace(voxels, neighbours[1], 0, 1, 2, Vector3.left,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFace(voxels, neighbours[2], 1, 2, 0, Vector3.up,      false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFace(voxels, neighbours[3], 1, 2, 0, Vector3.down,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFace(voxels, neighbours[4], 2, 0, 1, Vector3.forward, false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFace(voxels, neighbours[5], 2, 0, 1, Vector3.back,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);

        int s      = VoxelChunk.Size;
        var bounds = new Bounds(new Vector3(s * .5f, s * .5f, s * .5f), new Vector3(s, s, s));
        return PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, bounds);
    }

    /// <summary>
    /// Builds a region mesh on any thread. Returns null if the region has no visible faces.
    /// step = voxels per LOD cell (2/4/8 for LOD 1/2/3).
    /// </summary>
    public static WritableMeshData BuildRegionMeshData(RegionData region,
                                                        RegionData[] neighbours, int step)
    {
        int maskSize = Mathf.Max(
            (region.VoxelSizeY / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeY / step));
        EnsureThreadLocalBuffers(maskSize);

        GreedyMeshFaceRegion(region, neighbours[0], 0, 1, 2, Vector3.right,   false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[1], 0, 1, 2, Vector3.left,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[2], 1, 2, 0, Vector3.up,      false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[3], 1, 2, 0, Vector3.down,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[4], 2, 0, 1, Vector3.forward, false, _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);
        GreedyMeshFaceRegion(region, neighbours[5], 2, 0, 1, Vector3.back,    true,  _vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, _maskTS, step);

        float sx = region.VoxelSizeX, sy = region.VoxelSizeY, sz = region.VoxelSizeZ;
        var bounds = new Bounds(new Vector3(sx * .5f, sy * .5f, sz * .5f), new Vector3(sx, sy, sz));
        return PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, bounds);
    }

    // ── Thread-local buffers (one set per worker thread) ─────────────────────

    [ThreadStatic] private static List<Vector3> _vertsTS;
    [ThreadStatic] private static List<Vector3> _normsTS;
    [ThreadStatic] private static List<Vector2> _uvsTS;
    [ThreadStatic] private static List<Vector2> _uv2sTS;
    [ThreadStatic] private static List<int>     _trisTS;
    [ThreadStatic] private static byte[]        _maskTS;

    private static void EnsureThreadLocalBuffers(int minMaskSize)
    {
        if (_vertsTS == null) { _vertsTS = new(); _normsTS = new(); _uvsTS = new(); _uv2sTS = new(); _trisTS = new(); }
        _vertsTS.Clear(); _normsTS.Clear(); _uvsTS.Clear(); _uv2sTS.Clear(); _trisTS.Clear();
        if (_maskTS == null || _maskTS.Length < minMaskSize)
            _maskTS = new byte[minMaskSize];
    }

    // ── Chunk greedy mesher (byte[] voxel data) ───────────────────────────────

    private static void GreedyMeshFace(
        byte[] voxels, byte[] neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> tris, byte[] mask, int step)
    {
        int size  = VoxelChunk.Size;
        int cells = size / step;

        for (int slice = 0; slice < cells; slice++)
        {
            // ── Build mask ────────────────────────────────────────────────────
            int pos0 = 0, pos1 = 0, pos2 = 0;
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
                // For top faces at LOD>0, scan down within the step cell to find
                // the topmost solid block so we get surface type (grass/snow) not buried type.
                byte here;
                if (sliceAxis == 1 && !backFace && step > 1)
                {
                    here = BlockType.Air;
                    for (int dy = step - 1; dy >= 0; dy--)
                    {
                        pos0 = pos1 = pos2 = 0;
                        SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                                slice * step + dy, u * step, v * step);
                        byte b = voxels[pos0 + pos1 * size + pos2 * size * size];
                        if (b != BlockType.Air) { here = b; break; }
                    }
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            slice * step, u * step, v * step);
                    here = voxels[pos0 + pos1 * size + pos2 * size * size];
                }

                if (here == 0) { mask[u + v * cells] = 0; continue; }

                int  neighborCell = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborCell >= 0 && neighborCell < cells)
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell * step, u * step, v * step);
                    neighborSolid = voxels[pos0 + pos1 * size + pos2 * size * size] != 0;
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell < 0 ? size - step : 0, u * step, v * step);
                    neighborSolid = neighbour != null &&
                                    neighbour[pos0 + pos1 * size + pos2 * size * size] != 0;
                    if (here == BlockType.Water && neighbour == null)
                        neighborSolid = true;
                }

                mask[u + v * cells] = neighborSolid ? (byte)0 : here;
            }

            // ── Greedy merge ──────────────────────────────────────────────────
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells;)
            {
                byte blockType = mask[u + v * cells];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cells && mask[u + w + v * cells] == blockType) w++;

                int h = 1; bool done = false;
                while (v + h < cells && !done)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cells] != blockType) { done = true; break; }
                    if (!done) h++;
                }

                pos0 = pos1 = pos2 = 0;
                SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                        slice * step + (backFace ? 0 : step), u * step, v * step);

                var corner = new Vector3(pos0, pos1, pos2);
                var du = Vector3.zero; du[uAxis] = w * step;
                var dv = Vector3.zero; dv[vAxis] = h * step;

                int idx = verts.Count;
                verts.Add(corner); verts.Add(corner + du);
                verts.Add(corner + du + dv); verts.Add(corner + dv);
                norms.Add(normalVec); norms.Add(normalVec);
                norms.Add(normalVec); norms.Add(normalVec);

                // UV0: face-local tiling coords (frac() in shader tiles texture per voxel)
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(w, 0));
                uvs.Add(new Vector2(w, h)); uvs.Add(new Vector2(0, h));

                // UV1: texture array slice index
                float texSlice = blockType - 1;
                var sliceUV = new Vector2(texSlice, 0f);
                uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV);

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

    // Maps (sliceVal, uVal, vVal) onto (x, y, z) according to axis assignments.
    private static void SetAxis(ref int x, ref int y, ref int z,
                                  int sliceAxis, int uAxis, int vAxis,
                                  int sliceVal, int uVal, int vVal)
    {
        x = y = z = 0;
        Set(ref x, ref y, ref z, sliceAxis, sliceVal);
        Set(ref x, ref y, ref z, uAxis,     uVal);
        Set(ref x, ref y, ref z, vAxis,     vVal);
    }

    private static void Set(ref int x, ref int y, ref int z, int axis, int val)
    {
        if      (axis == 0) x = val;
        else if (axis == 1) y = val;
        else                z = val;
    }

    // ── Region greedy mesher ──────────────────────────────────────────────────

    private static void GreedyMeshFaceRegion(
        RegionData region, RegionData neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> tris, byte[] mask, int step)
    {
        int[] sizes = { region.VoxelSizeX, region.VoxelSizeY, region.VoxelSizeZ };
        int cellsSlice = sizes[sliceAxis] / step;
        int cellsU     = sizes[uAxis]     / step;
        int cellsV     = sizes[vAxis]     / step;

        for (int slice = 0; slice < cellsSlice; slice++)
        {
            int pos0, pos1, pos2;
            for (int v = 0; v < cellsV; v++)
            for (int u = 0; u < cellsU; u++)
            {
                // For top faces, scan down within the step cell to find the topmost
                // solid block — this ensures surface block type (grass/snow) is used
                // instead of a buried block that happens to fall on a step boundary.
                byte here;
                if (sliceAxis == 1 && !backFace)
                {
                    here = BlockType.Air;
                    for (int dy = step - 1; dy >= 0; dy--)
                    {
                        pos0 = pos1 = pos2 = 0;
                        SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                                slice * step + dy, u * step, v * step);
                        byte b = region.GetBlockUnchecked(pos0, pos1, pos2);
                        if (b != BlockType.Air) { here = b; break; }
                    }
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            slice * step, u * step, v * step);
                    here = region.GetBlockUnchecked(pos0, pos1, pos2);
                }

                if (here == BlockType.Air) { mask[u + v * cellsU] = 0; continue; }

                int  neighborCell = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborCell >= 0 && neighborCell < cellsSlice)
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell * step, u * step, v * step);
                    neighborSolid = region.IsSolidUnchecked(pos0, pos1, pos2);
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell < 0 ? sizes[sliceAxis] - step : 0, u * step, v * step);
                    neighborSolid = neighbour != null && neighbour.IsSolidUnchecked(pos0, pos1, pos2);
                    if (here == BlockType.Water && neighbour == null)
                        neighborSolid = true;
                }

                mask[u + v * cellsU] = neighborSolid ? (byte)0 : here;
            }

            for (int v = 0; v < cellsV; v++)
            for (int u = 0; u < cellsU;)
            {
                byte blockType = mask[u + v * cellsU];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cellsU && mask[u + w + v * cellsU] == blockType) w++;

                int h = 1; bool done = false;
                while (v + h < cellsV && !done)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cellsU] != blockType) { done = true; break; }
                    if (!done) h++;
                }

                pos0 = pos1 = pos2 = 0;
                SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                        slice * step + (backFace ? 0 : step), u * step, v * step);

                var corner = new Vector3(pos0, pos1, pos2);
                var du = Vector3.zero; du[uAxis] = w * step;
                var dv = Vector3.zero; dv[vAxis] = h * step;

                int idx = verts.Count;
                verts.Add(corner); verts.Add(corner + du);
                verts.Add(corner + du + dv); verts.Add(corner + dv);
                norms.Add(normalVec); norms.Add(normalVec);
                norms.Add(normalVec); norms.Add(normalVec);

                // UV0: face-local tiling coords
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(w, 0));
                uvs.Add(new Vector2(w, h)); uvs.Add(new Vector2(0, h));

                // UV1: texture array slice index
                float texSlice = blockType - 1;
                var sliceUV = new Vector2(texSlice, 0f);
                uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV);

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

    // ── Mesh upload helpers ───────────────────────────────────────────────────

    private static WritableMeshData PackToWritableMesh(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> tris, Bounds bounds)
    {
        if (verts.Count == 0) return null;
        return new WritableMeshData(verts.ToArray(), norms.ToArray(), uvs.ToArray(),
                                    uv2s.ToArray(), tris.ToArray(), bounds);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Holds greedy-mesh arrays built on a background thread.
/// Call Apply() on the main thread to upload to the GPU.
/// Call Discard() to abandon without uploading (GC cleans up the managed arrays).
/// </summary>
public sealed class WritableMeshData
{
    private readonly Vector3[] _vertices;
    private readonly Vector3[] _normals;
    private readonly Vector2[] _uvs;
    private readonly Vector2[] _uv2s;
    private readonly int[]     _triangles;
    public  readonly Bounds    Bounds;

    internal WritableMeshData(Vector3[] v, Vector3[] n, Vector2[] u, Vector2[] u2, int[] t, Bounds bounds)
    { _vertices = v; _normals = n; _uvs = u; _uv2s = u2; _triangles = t; Bounds = bounds; }

    /// <summary>Packs vertex data into GPU memory. Must be called on the main thread.</summary>
    public Mesh Apply()
    {
        bool use32 = _vertices.Length > ushort.MaxValue;
        var mda = Mesh.AllocateWritableMeshData(1);
        var md  = mda[0];

        md.SetVertexBufferParams(_vertices.Length,
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream: 3));
        md.SetIndexBufferParams(_triangles.Length, use32 ? IndexFormat.UInt32 : IndexFormat.UInt16);

        var positions = md.GetVertexData<Vector3>(0);
        var normals   = md.GetVertexData<Vector3>(1);
        var uvCoords  = md.GetVertexData<Vector2>(2);
        var uv2Coords = md.GetVertexData<Vector2>(3);
        for (int i = 0; i < _vertices.Length; i++)
        {
            positions[i] = _vertices[i];
            normals[i]   = _normals[i];
            uvCoords[i]  = _uvs[i];
            uv2Coords[i] = _uv2s[i];
        }

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

    public void Discard() { /* managed arrays — GC handles cleanup */ }
}
