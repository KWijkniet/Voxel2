using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class DetailMesher
{
    private Database database;
    private byte[] voxelData;
    private int width, height, depth;

    public DetailMesher(Database database, byte[] voxelData, int width, int height, int depth)
    {
        this.database = database;
        this.voxelData = voxelData;
        this.width = width;
        this.height = height;
        this.depth = depth;
    }

    public Mesh GenerateMesh()
    {
        Mesh mesh = new Mesh();
        mesh.Clear();
        mesh.name = "Detail Mesh";
        mesh.subMeshCount = 2;
        // Create voxel mesh
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uvs);
        
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
