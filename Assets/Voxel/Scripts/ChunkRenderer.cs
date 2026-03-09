using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a greedy mesh from a PaletteChunk and assigns it to this GameObject.
/// A neighbour-lookup delegate is used to cull faces that are covered by adjacent chunks.
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

    /// <param name="chunk">This chunk's voxel data.</param>
    /// <param name="coord">This chunk's coordinate in the world grid.</param>
    /// <param name="getNeighbour">Delegate that returns a neighbouring PaletteChunk by coord, or null if unloaded.</param>
    /// <param name="material">Material to render with.</param>
    public void Render(PaletteChunk chunk, Vector3Int coord,
                       Func<Vector3Int, PaletteChunk> getNeighbour, Material material)
    {
        if (_meshFilter   == null) _meshFilter   = GetComponent<MeshFilter>();
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

        _meshRenderer.sharedMaterial = material;
        _meshFilter.sharedMesh = BuildMesh(chunk, coord, getNeighbour);
    }

    public void Clear()
    {
        if (_meshFilter != null) _meshFilter.sharedMesh = null;
    }

    // ── Greedy mesher ─────────────────────────────────────────────────────────

    // Neighbour directions indexed by face: +X,-X,+Y,-Y,+Z,-Z
    private static readonly Vector3Int[] NeighbourDirs =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.up,
        Vector3Int.down,
        new Vector3Int(0, 0,  1),
        new Vector3Int(0, 0, -1),
    };

    // Reusable buffers — cleared before each mesh build, eliminating per-chunk GC allocations
    private static readonly List<Vector3> _verts = new List<Vector3>();
    private static readonly List<Vector3> _norms = new List<Vector3>();
    private static readonly List<Vector2> _uvs   = new List<Vector2>();
    private static readonly List<int>     _tris  = new List<int>();
    // One mask per face direction — size² bytes, reused across all slices and all chunks
    private static readonly byte[]        _mask  = new byte[PaletteChunk.Size * PaletteChunk.Size];

    private static Mesh BuildMesh(PaletteChunk chunk, Vector3Int coord,
                                   Func<Vector3Int, PaletteChunk> getNeighbour)
    {
        _verts.Clear(); _norms.Clear(); _uvs.Clear(); _tris.Clear();

        // Pre-fetch the 6 neighbours (null = unloaded, treat boundary as air)
        var neighbours = new PaletteChunk[6];
        for (int i = 0; i < 6; i++)
            neighbours[i] = getNeighbour(coord + NeighbourDirs[i]);

        //                         sliceAxis uAxis vAxis  normal          backFace
        GreedyMeshFace(chunk, neighbours[0], 0, 1, 2, Vector3.right,   false); // +X
        GreedyMeshFace(chunk, neighbours[1], 0, 1, 2, Vector3.left,    true);  // -X
        GreedyMeshFace(chunk, neighbours[2], 1, 2, 0, Vector3.up,      false); // +Y
        GreedyMeshFace(chunk, neighbours[3], 1, 2, 0, Vector3.down,    true);  // -Y
        GreedyMeshFace(chunk, neighbours[4], 2, 0, 1, Vector3.forward, false); // +Z
        GreedyMeshFace(chunk, neighbours[5], 2, 0, 1, Vector3.back,    true);  // -Z

        var mesh = new Mesh { name = "Chunk" };
        mesh.indexFormat = IndexFormat.UInt16;
        mesh.SetVertices(_verts);
        mesh.SetNormals(_norms);
        mesh.SetUVs(0, _uvs);
        mesh.SetTriangles(_tris, 0);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return mesh;
    }

    /// <param name="neighbour">The chunk directly adjacent in the face's normal direction (may be null).</param>
    private static void GreedyMeshFace(
        PaletteChunk chunk, PaletteChunk neighbour,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace)
    {
        int size = PaletteChunk.Size;
        // _mask is reused — no allocation. It is fully overwritten each slice before being read.
        var pos  = new int[3];

        for (int slice = 0; slice < size; slice++)
        {
            // Build mask: store block type where a face is visible, 0 where not
            for (int v = 0; v < size; v++)
            for (int u = 0; u < size; u++)
            {
                pos[sliceAxis] = slice;
                pos[uAxis]     = u;
                pos[vAxis]     = v;
                byte here = chunk.GetBlock(pos[0], pos[1], pos[2]);
                if (here == BlockType.Air) { _mask[u + v * size] = 0; continue; }

                // Check the voxel on the other side of this face
                int neighborSlice = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborSlice >= 0 && neighborSlice < size)
                {
                    // Within the same chunk
                    pos[sliceAxis] = neighborSlice;
                    neighborSolid = chunk.IsSolid(pos[0], pos[1], pos[2]);
                }
                else
                {
                    // Cross-chunk boundary — ask the neighbour chunk
                    pos[sliceAxis] = neighborSlice < 0 ? size - 1 : 0;
                    neighborSolid  = neighbour != null && neighbour.IsSolid(pos[0], pos[1], pos[2]);
                }

                _mask[u + v * size] = neighborSolid ? (byte)0 : here;
            }

            // Greedy merge — only expand across cells of the same block type
            for (int v = 0; v < size; v++)
            for (int u = 0; u < size; )
            {
                byte blockType = _mask[u + v * size];
                if (blockType == BlockType.Air) { u++; continue; }

                int w = 1;
                while (u + w < size && _mask[u + w + v * size] == blockType) w++;

                int h = 1;
                bool heightDone = false;
                while (v + h < size && !heightDone)
                {
                    for (int k = 0; k < w; k++)
                        if (_mask[u + k + (v + h) * size] != blockType) { heightDone = true; break; }
                    if (!heightDone) h++;
                }

                pos[sliceAxis] = slice + (backFace ? 0 : 1);
                pos[uAxis]     = u;
                pos[vAxis]     = v;

                var corner = new Vector3(pos[0], pos[1], pos[2]);
                var du = Vector3.zero; du[uAxis] = w;
                var dv = Vector3.zero; dv[vAxis] = h;

                int idx = _verts.Count;
                _verts.Add(corner);
                _verts.Add(corner + du);
                _verts.Add(corner + du + dv);
                _verts.Add(corner + dv);

                _norms.Add(normalVec); _norms.Add(normalVec);
                _norms.Add(normalVec); _norms.Add(normalVec);

                float tileU = (blockType - 1 + 0.5f) / BlockType.AtlasTileCount;
                _uvs.Add(new Vector2(tileU, 0f)); _uvs.Add(new Vector2(tileU, 0f));
                _uvs.Add(new Vector2(tileU, 1f)); _uvs.Add(new Vector2(tileU, 1f));

                if (backFace)
                {
                    _tris.Add(idx); _tris.Add(idx+2); _tris.Add(idx+1);
                    _tris.Add(idx); _tris.Add(idx+3); _tris.Add(idx+2);
                }
                else
                {
                    _tris.Add(idx); _tris.Add(idx+1); _tris.Add(idx+2);
                    _tris.Add(idx); _tris.Add(idx+2); _tris.Add(idx+3);
                }

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    _mask[u + uu + (v + vv) * size] = 0;

                u += w;
            }
        }
    }
}
