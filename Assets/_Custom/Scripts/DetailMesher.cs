using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Custom.Importer;

public class DetailMeshData
{
    public int x, y, z;
    public int width, height;
    public Vector3Int direction;
    public byte type;

    public DetailMeshData(int x, int y, int z, int width, int height, Vector3Int direction, byte type)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.width = width;
        this.height = height;
        this.direction = direction;
        this.type = type;
    }
}

public class DetailMesher
{
    private Database database;
    private byte[] voxelData;
    private int width, height, depth;

    private List<DetailMeshData> meshData;

    public DetailMesher(Database database, byte[] voxelData, int width, int height, int depth)
    {
        this.database = database;
        this.voxelData = voxelData;
        this.width = width;
        this.height = height;
        this.depth = depth;
        this.meshData = new List<DetailMeshData>();
    }

    public Mesh GenerateMesh()
    {
        meshData.Clear();
        MeshData solidMeshData = new MeshData();
        MeshData transparentMeshData = new MeshData();

        //Calculate
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte voxel = GetVoxel(x, y, z);
                    JSONData data = database.GetVoxelData(voxel);
                    MeshData target = data.isTransparent ? transparentMeshData : solidMeshData;
                    DetailMeshData detailMeshData = new DetailMeshData(x, y, z, 1, 1, Vector3Int.zero, voxel);

                    switch (data.type)
                    {
                        case "Quad":
                            BuildQuad(detailMeshData, data, target);
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.Clear();
        mesh.MarkDynamic();
        mesh.name = "Detail Mesh";
        mesh.subMeshCount = 2;
        mesh.vertices = solidMeshData.vertices.Concat(transparentMeshData.vertices).ToArray();
        mesh.SetTriangles(solidMeshData.triangles.ToArray(), 0);
        mesh.SetTriangles(transparentMeshData.triangles.Select(t => t + solidMeshData.vertices.Count).ToArray(), 1);
        mesh.SetUVs(0, solidMeshData.uvs.Concat(transparentMeshData.uvs).ToArray());
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();

        return mesh;
    }

    private byte GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return 0;
        return voxelData[x + y * width + z * width * height];
    }

    private void BuildQuad(DetailMeshData data, JSONData voxelData, MeshData target)
    {
        float offset = 0.1465f;
        float one = 1 - 0.1465f;
        float zero = 0.1465f;
        //
        int vertexIndex = target.vertices.Count;
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(zero, 0, one));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(one, 0, zero));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(zero, 1 - (offset*2), one));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(one, 1 - (offset*2), zero));
        // Add UVs with tiling
        target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(1, 0, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(0, 1, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(1, 1, voxelData.GetId(), 2));
        for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[0, j]);

        //
        vertexIndex = target.vertices.Count;
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(zero, 0, zero));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(one, 0, one));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(zero, 1 - (offset*2), zero));
        target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(one, 1 - (offset*2), one));
        // Add UVs with tiling
        target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(1, 0, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(0, 1, voxelData.GetId(), 2));
        target.uvs.Add(new Vector4(1, 1, voxelData.GetId(), 2));
        for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[0, j]);
    }

    // Corrected triangle order for outward-facing faces
    private static int[,] faceTriangles =
    {
        { 0, 1, 2, 1, 3, 2 }, // Back
        { 0, 2, 1, 1, 2, 3 }, // Front
        { 0, 1, 2, 1, 3, 2 }, // Top
        { 0, 1, 2, 1, 3, 2 }, // Bottom
        { 0, 1, 2, 2, 1, 3 }, // Right
        { 0, 1, 2, 1, 3, 2 }  // Left
    };
}