using System.Collections.Generic;
using UnityEngine;

public static class MeshGenerator
{
    private static byte[] voxels;
    private static int size;
    private static List<Vector3> vertices = new List<Vector3>();
    private static List<int> triangles = new List<int>();
    private static List<Vector2> uvs = new List<Vector2>();

    // Directions for neighbor checking
    private static Vector3Int[] faceOffsets =
    {
        new Vector3Int( 0,  0,  1), // Front
        new Vector3Int( 0,  0, -1), // Back
        new Vector3Int( 0,  1,  0), // Top
        new Vector3Int( 0, -1,  0), // Bottom
        new Vector3Int( 1,  0,  0), // Right
        new Vector3Int(-1,  0,  0)  // Left
    };

    // Face vertices relative to voxel position
    private static Vector3[,] faceQuads =
    {
        { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1), new Vector3(1, 1, 1) }, // Front
        { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) }, // Back
        { new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 0), new Vector3(1, 1, 0) }, // Top
        { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1) }, // Bottom
        { new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 1, 0) }, // Right
        { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(0, 1, 1) }  // Left
    };

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

    public static Mesh GenerateMesh(byte[] _voxels, int _size)
    {
        size = _size;
        voxels = _voxels;
        vertices.Clear();
        triangles.Clear();
        uvs.Clear();

        for (int i = 0; i < voxels.Length; i++) {
            Vector3Int pos = Helpers.IndexToCoordinates(i, size);
            if (voxels[i] == 1)
            {
                AddVoxel(pos.x, pos.y, pos.z);
            }
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.RecalculateNormals();

        return mesh;
    }

    private static void AddVoxel(int x, int y, int z)
    {
        for (int i = 0; i < 6; i++)
        {
            int nx = x + faceOffsets[i].x;
            int ny = y + faceOffsets[i].y;
            int nz = z + faceOffsets[i].z;

            if (GetVoxel(nx, ny, nz, size) == 0) // If neighbor is air, draw face
            {
                int vertexIndex = vertices.Count;

                for (int j = 0; j < 4; j++)
                {
                    vertices.Add(new Vector3(x, y, z) + faceQuads[i, j]);
                    uvs.Add(new Vector2(j % 2, j / 2));
                }

                for (int j = 0; j < 6; j++)
                {
                    triangles.Add(vertexIndex + faceTriangles[i, j]);
                }
            }
        }
    }

    private static byte GetVoxel(int x, int y, int z, int size)
    {
        if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size)
            return 0; // Treat out-of-bounds as empty space
        return voxels[Helpers.CoordinatesToIndex(x, y, z, size)];
    }
}
