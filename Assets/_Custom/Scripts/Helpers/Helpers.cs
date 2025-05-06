using System;
using System.Threading.Tasks;
using UnityEngine;

public class Helpers
{
    public static int CoordinatesToIndex(int x, int y, int z, int width = 16, int height = 16, int depth = 16) {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return -1;
        return x + y * width + z * width * height;
    }

    public static Vector3Int IndexToCoordinates(int index, int width, int height, int depth) {
        // return new Vector3Int(index / (size * size), (index / size) % size, index % size);
        int z = index % width; // X-coordinate
        int y = (index / width) % height; // Y-coordinate
        int x = index / (width * height); // Z-coordinate
        
        return new Vector3Int(x, y, z);
    }

    public static async Task Sleep(float delay)
    {
        await Task.Delay(TimeSpan.FromSeconds(delay));
    }

    public static Mesh ConvertToMesh(string name, MeshBuffers solid, MeshBuffers transparent)
    {
        Mesh mesh = new Mesh
        {
            name = "Chunk Mesh ("+name+")",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        mesh.MarkDynamic();

        int solidVertCount = solid.Vertices.Length;
        int transVertCount = transparent.Vertices.Length;

        // Merge vertices and uvs
        var vertices = new Vector3[solidVertCount + transVertCount];
        var uvs = new Vector2[vertices.Length];

        for (int i = 0; i < solidVertCount; i++)
        {
            vertices[i] = solid.Vertices[i];
            uvs[i] = solid.UVs[i];
        }
        for (int i = 0; i < transVertCount; i++)
        {
            vertices[solidVertCount + i] = transparent.Vertices[i];
            uvs[solidVertCount + i] = transparent.UVs[i];
        }

        // Merge triangles with offset
        var solidTriangles = new int[solid.Triangles.Length];
        for (int i = 0; i < solid.Triangles.Length; i++)
            solidTriangles[i] = solid.Triangles[i];

        var transparentTriangles = new int[transparent.Triangles.Length];
        for (int i = 0; i < transparent.Triangles.Length; i++)
            transparentTriangles[i] = transparent.Triangles[i] + solidVertCount;

        // Assign to mesh
        mesh.Clear();
        mesh.subMeshCount = 2;
        mesh.vertices = vertices;
        mesh.SetTriangles(solidTriangles, 0);
        mesh.SetTriangles(transparentTriangles, 1);
        mesh.uv = uvs;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }
}