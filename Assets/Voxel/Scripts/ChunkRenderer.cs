using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Static mesh builder — greedy mesher + mesh upload helpers.
///
/// Each Build* call returns a (opaque, transparent) pair of WritableMeshData.
/// Either element may be null when there is no geometry of that type.
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
        Vector3Int.up,
        Vector3Int.down,
        new Vector3Int(0, 0,  1),
        new Vector3Int(0, 0, -1),
    };

    // ── Public API ────────────────────────────────────────────────────────────

    public static Mesh CreateMesh(WritableMeshData data) => data?.Apply();

    /// <summary>
    /// Builds opaque + transparent WritableMeshData from a flat voxel byte[].
    /// neighbours[0..5] = +X,-X,+Y,-Y,+Z,-Z (may be null = air).
    /// </summary>
    public static (WritableMeshData opaque, WritableMeshData trans) BuildMeshData(
        byte[] voxels, byte[][] neighbours, int lodLevel = 0)
    {
        int step  = 1 << lodLevel;
        int cells = VoxelChunk.Size / step;
        EnsureThreadLocalBuffers(cells * cells);

        GreedyMeshFace(voxels, neighbours[0], 0, 1, 2, Vector3.right,   false, step);
        GreedyMeshFace(voxels, neighbours[1], 0, 1, 2, Vector3.left,    true,  step);
        GreedyMeshFace(voxels, neighbours[2], 1, 2, 0, Vector3.up,      false, step);
        GreedyMeshFace(voxels, neighbours[3], 1, 2, 0, Vector3.down,    true,  step);
        GreedyMeshFace(voxels, neighbours[4], 2, 0, 1, Vector3.forward, false, step);
        GreedyMeshFace(voxels, neighbours[5], 2, 0, 1, Vector3.back,    true,  step);

        int s      = VoxelChunk.Size;
        var bounds = new Bounds(new Vector3(s * .5f, s * .5f, s * .5f), new Vector3(s, s, s));
        return (PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, bounds),
                PackToWritableMesh(_tVertsTS, _tNormsTS, _tUvsTS, _tUv2sTS, _tTrisTS, bounds));
    }

    /// <summary>
    /// Builds opaque + transparent WritableMeshData for a LOD region.
    /// step = voxels per LOD cell (2/4/8 for LOD 1/2/3).
    /// </summary>
    public static (WritableMeshData opaque, WritableMeshData trans) BuildRegionMeshData(
        RegionData region, RegionData[] neighbours, int step)
    {
        int maskSize = Mathf.Max(
            (region.VoxelSizeY / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeZ / step),
            (region.VoxelSizeX / step) * (region.VoxelSizeY / step));
        EnsureThreadLocalBuffers(maskSize);

        GreedyMeshFaceRegion(region, neighbours[0], 0, 1, 2, Vector3.right,   false, step);
        GreedyMeshFaceRegion(region, neighbours[1], 0, 1, 2, Vector3.left,    true,  step);
        GreedyMeshFaceRegion(region, neighbours[2], 1, 2, 0, Vector3.up,      false, step);
        GreedyMeshFaceRegion(region, neighbours[3], 1, 2, 0, Vector3.down,    true,  step);
        GreedyMeshFaceRegion(region, neighbours[4], 2, 0, 1, Vector3.forward, false, step);
        GreedyMeshFaceRegion(region, neighbours[5], 2, 0, 1, Vector3.back,    true,  step);

        float sx = region.VoxelSizeX, sy = region.VoxelSizeY, sz = region.VoxelSizeZ;
        var bounds = new Bounds(new Vector3(sx * .5f, sy * .5f, sz * .5f), new Vector3(sx, sy, sz));
        return (PackToWritableMesh(_vertsTS, _normsTS, _uvsTS, _uv2sTS, _trisTS, bounds),
                PackToWritableMesh(_tVertsTS, _tNormsTS, _tUvsTS, _tUv2sTS, _tTrisTS, bounds));
    }

    // ── Thread-local buffers ──────────────────────────────────────────────────
    // Opaque
    [ThreadStatic] private static List<Vector3> _vertsTS;
    [ThreadStatic] private static List<Vector3> _normsTS;
    [ThreadStatic] private static List<Vector2> _uvsTS;
    [ThreadStatic] private static List<Vector2> _uv2sTS;
    [ThreadStatic] private static List<int>     _trisTS;
    // Transparent
    [ThreadStatic] private static List<Vector3> _tVertsTS;
    [ThreadStatic] private static List<Vector3> _tNormsTS;
    [ThreadStatic] private static List<Vector2> _tUvsTS;
    [ThreadStatic] private static List<Vector2> _tUv2sTS;
    [ThreadStatic] private static List<int>     _tTrisTS;

    [ThreadStatic] private static byte[] _maskTS;

    private static void EnsureThreadLocalBuffers(int minMaskSize)
    {
        if (_vertsTS  == null) { _vertsTS  = new(); _normsTS  = new(); _uvsTS  = new(); _uv2sTS  = new(); _trisTS  = new(); }
        if (_tVertsTS == null) { _tVertsTS = new(); _tNormsTS = new(); _tUvsTS = new(); _tUv2sTS = new(); _tTrisTS = new(); }
        _vertsTS.Clear();  _normsTS.Clear();  _uvsTS.Clear();  _uv2sTS.Clear();  _trisTS.Clear();
        _tVertsTS.Clear(); _tNormsTS.Clear(); _tUvsTS.Clear(); _tUv2sTS.Clear(); _tTrisTS.Clear();
        if (_maskTS == null || _maskTS.Length < minMaskSize)
            _maskTS = new byte[minMaskSize];
    }

    // ── Chunk greedy mesher ───────────────────────────────────────────────────

    private static void GreedyMeshFace(
        byte[] voxels, byte[] neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace, int step)
    {
        int size  = VoxelChunk.Size;
        int cells = size / step;

        for (int slice = 0; slice < cells; slice++)
        {
            int pos0 = 0, pos1 = 0, pos2 = 0;
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
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

                if (here == 0) { _maskTS[u + v * cells] = 0; continue; }

                byte neighborVoxel;
                int  neighborCell = slice + (backFace ? -1 : 1);

                if (neighborCell >= 0 && neighborCell < cells)
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell * step, u * step, v * step);
                    neighborVoxel = voxels[pos0 + pos1 * size + pos2 * size * size];
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell < 0 ? size - step : 0, u * step, v * step);
                    neighborVoxel = neighbour != null
                        ? neighbour[pos0 + pos1 * size + pos2 * size * size]
                        : BlockType.Air;
                    if (here == BlockType.Water && neighbour == null)
                        neighborVoxel = BlockType.Water;
                }

                _maskTS[u + v * cells] = IsFaceHidden(here, neighborVoxel) ? (byte)0 : here;
            }

            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells;)
            {
                byte blockType = _maskTS[u + v * cells];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cells && _maskTS[u + w + v * cells] == blockType) w++;

                int h = 1; bool done = false;
                while (v + h < cells && !done)
                {
                    for (int k = 0; k < w; k++)
                        if (_maskTS[u + k + (v + h) * cells] != blockType) { done = true; break; }
                    if (!done) h++;
                }

                pos0 = pos1 = pos2 = 0;
                SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                        slice * step + (backFace ? 0 : step), u * step, v * step);

                EmitQuad(blockType, normalVec, backFace,
                         new Vector3(pos0, pos1, pos2), uAxis, vAxis, w, h, step);

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    _maskTS[u + uu + (v + vv) * cells] = 0;

                u += w;
            }
        }
    }

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
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace, int step)
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

                if (here == BlockType.Air) { _maskTS[u + v * cellsU] = 0; continue; }

                byte neighborVoxel;
                int  neighborCell = slice + (backFace ? -1 : 1);

                if (neighborCell >= 0 && neighborCell < cellsSlice)
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell * step, u * step, v * step);
                    neighborVoxel = region.GetBlockUnchecked(pos0, pos1, pos2);
                }
                else
                {
                    pos0 = pos1 = pos2 = 0;
                    SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                            neighborCell < 0 ? sizes[sliceAxis] - step : 0, u * step, v * step);
                    neighborVoxel = neighbour != null
                        ? neighbour.GetBlockUnchecked(pos0, pos1, pos2)
                        : BlockType.Air;
                    if (here == BlockType.Water && neighbour == null)
                        neighborVoxel = BlockType.Water;
                }

                _maskTS[u + v * cellsU] = IsFaceHidden(here, neighborVoxel) ? (byte)0 : here;
            }

            for (int v = 0; v < cellsV; v++)
            for (int u = 0; u < cellsU;)
            {
                byte blockType = _maskTS[u + v * cellsU];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < cellsU && _maskTS[u + w + v * cellsU] == blockType) w++;

                int h = 1; bool done = false;
                while (v + h < cellsV && !done)
                {
                    for (int k = 0; k < w; k++)
                        if (_maskTS[u + k + (v + h) * cellsU] != blockType) { done = true; break; }
                    if (!done) h++;
                }

                pos0 = pos1 = pos2 = 0;
                SetAxis(ref pos0, ref pos1, ref pos2, sliceAxis, uAxis, vAxis,
                        slice * step + (backFace ? 0 : step), u * step, v * step);

                EmitQuad(blockType, normalVec, backFace,
                         new Vector3(pos0, pos1, pos2), uAxis, vAxis, w, h, step);

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    _maskTS[u + uu + (v + vv) * cellsU] = 0;

                u += w;
            }
        }
    }

    // ── Shared quad emitter ───────────────────────────────────────────────────

    private static void EmitQuad(byte blockType, Vector3 normalVec, bool backFace,
                                  Vector3 corner, int uAxis, int vAxis, int w, int h, int step)
    {
        bool isTrans = BlockType.IsTransparent(blockType);
        var verts = isTrans ? _tVertsTS : _vertsTS;
        var norms = isTrans ? _tNormsTS : _normsTS;
        var uvs   = isTrans ? _tUvsTS   : _uvsTS;
        var uv2s  = isTrans ? _tUv2sTS  : _uv2sTS;
        var tris  = isTrans ? _tTrisTS  : _trisTS;

        var du = Vector3.zero; du[uAxis] = w * step;
        var dv = Vector3.zero; dv[vAxis] = h * step;

        int idx = verts.Count;
        verts.Add(corner); verts.Add(corner + du);
        verts.Add(corner + du + dv); verts.Add(corner + dv);
        norms.Add(normalVec); norms.Add(normalVec);
        norms.Add(normalVec); norms.Add(normalVec);

        uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(w, 0));
        uvs.Add(new Vector2(w, h)); uvs.Add(new Vector2(0, h));

        float texSlice = blockType - 1;
        var sliceUV = new Vector2(texSlice, 0f);
        uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV); uv2s.Add(sliceUV);

        if (backFace)
        { tris.Add(idx); tris.Add(idx+2); tris.Add(idx+1); tris.Add(idx); tris.Add(idx+3); tris.Add(idx+2); }
        else
        { tris.Add(idx); tris.Add(idx+1); tris.Add(idx+2); tris.Add(idx); tris.Add(idx+2); tris.Add(idx+3); }
    }

    // Returns true when the face between `here` and `neighbor` should be culled.
    private static bool IsFaceHidden(byte here, byte neighbor)
    {
        if (neighbor == BlockType.Air) return false;
        if (BlockType.IsTransparent(here))
            return neighbor == here || !BlockType.IsTransparent(neighbor);
        return !BlockType.IsTransparent(neighbor);
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

    public void Discard() { }
}
