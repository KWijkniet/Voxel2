using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VoxelChunk : MonoBehaviour
{
    public const int Size = 16;

    // 1 = solid, 0 = air
    private byte[] _voxels = new byte[Size * Size * Size];

    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;

    private void Awake()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshRenderer = GetComponent<MeshRenderer>();
    }

    // Fill with solid voxels and build the mesh
    public void Generate()
    {
        EnsureComponents();
        FillSolid();
        BuildMesh();
    }

    public void Clear()
    {
        EnsureComponents();
        System.Array.Clear(_voxels, 0, _voxels.Length);
        _meshFilter.sharedMesh = null;
    }

    private void EnsureComponents()
    {
        if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
        if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();

        // Assign a default material if none is set
        if (_meshRenderer.sharedMaterial == null)
            _meshRenderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
    }

    private void FillSolid()
    {
        for (int i = 0; i < _voxels.Length; i++)
            _voxels[i] = 1;
    }

    private bool IsSolid(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Size || y >= Size || z >= Size)
            return false;
        return _voxels[x + y * Size + z * Size * Size] != 0;
    }

    private void BuildMesh()
    {
        var vertices  = new System.Collections.Generic.List<Vector3>();
        var normals   = new System.Collections.Generic.List<Vector3>();
        var uvs       = new System.Collections.Generic.List<Vector2>();
        var triangles = new System.Collections.Generic.List<int>();

        // Greedy mesh each axis in both directions
        //                      sliceAxis  uAxis  vAxis  normal          backFace
        GreedyMeshFace(vertices, normals, uvs, triangles, 0, 1, 2, Vector3.right,   false); // +X
        GreedyMeshFace(vertices, normals, uvs, triangles, 0, 1, 2, Vector3.left,    true);  // -X
        GreedyMeshFace(vertices, normals, uvs, triangles, 1, 2, 0, Vector3.up,      false); // +Y
        GreedyMeshFace(vertices, normals, uvs, triangles, 1, 2, 0, Vector3.down,    true);  // -Y
        GreedyMeshFace(vertices, normals, uvs, triangles, 2, 0, 1, Vector3.forward, false); // +Z
        GreedyMeshFace(vertices, normals, uvs, triangles, 2, 0, 1, Vector3.back,    true);  // -Z

        var mesh = new Mesh { name = "VoxelChunk" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16; // max 65535, safe for 16^3
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true); // free CPU copy after upload to GPU
        _meshFilter.sharedMesh = mesh;
    }

    // Greedy mesher for one face direction.
    // sliceAxis: axis perpendicular to the face (0=X,1=Y,2=Z)
    // uAxis/vAxis: the two tangent axes
    // backFace: face points in the negative sliceAxis direction
    private void GreedyMeshFace(
        System.Collections.Generic.List<Vector3> vertices,
        System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<Vector2> uvs,
        System.Collections.Generic.List<int> triangles,
        int sliceAxis, int uAxis, int vAxis, Vector3 normalVec, bool backFace)
    {
        var mask = new bool[Size * Size];
        var pos  = new int[3];

        for (int slice = 0; slice < Size; slice++)
        {
            // Build mask: true where this voxel is solid and its neighbor (in normal dir) is air
            for (int v = 0; v < Size; v++)
            for (int u = 0; u < Size; u++)
            {
                pos[sliceAxis] = slice;
                pos[uAxis]     = u;
                pos[vAxis]     = v;
                bool here = IsSolid(pos[0], pos[1], pos[2]);

                pos[sliceAxis] = slice + (backFace ? -1 : 1);
                bool neighbor = IsSolid(pos[0], pos[1], pos[2]);

                mask[u + v * Size] = here && !neighbor;
            }

            // Greedy merge
            for (int v = 0; v < Size; v++)
            for (int u = 0; u < Size; )
            {
                if (!mask[u + v * Size]) { u++; continue; }

                // Expand width
                int w = 1;
                while (u + w < Size && mask[u + w + v * Size]) w++;

                // Expand height
                int h = 1;
                bool heightDone = false;
                while (v + h < Size && !heightDone)
                {
                    for (int k = 0; k < w; k++)
                        if (!mask[u + k + (v + h) * Size]) { heightDone = true; break; }
                    if (!heightDone) h++;
                }

                // Emit quad
                pos[sliceAxis] = slice + (backFace ? 0 : 1);
                pos[uAxis]     = u;
                pos[vAxis]     = v;

                var corner = new Vector3(pos[0], pos[1], pos[2]);
                var du = Vector3.zero; du[uAxis] = w;
                var dv = Vector3.zero; dv[vAxis] = h;

                var v0 = corner;
                var v1 = corner + du;
                var v2 = corner + du + dv;
                var v3 = corner + dv;

                int idx = vertices.Count;
                vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);
                normals.Add(normalVec); normals.Add(normalVec); normals.Add(normalVec); normals.Add(normalVec);
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(w, 0));
                uvs.Add(new Vector2(w, h)); uvs.Add(new Vector2(0, h));

                // Winding order flipped for back faces
                if (backFace)
                {
                    triangles.Add(idx); triangles.Add(idx+2); triangles.Add(idx+1);
                    triangles.Add(idx); triangles.Add(idx+3); triangles.Add(idx+2);
                }
                else
                {
                    triangles.Add(idx); triangles.Add(idx+1); triangles.Add(idx+2);
                    triangles.Add(idx); triangles.Add(idx+2); triangles.Add(idx+3);
                }

                // Clear merged cells from mask
                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    mask[u + uu + (v + vv) * Size] = false;

                u += w;
            }
        }
    }
}
