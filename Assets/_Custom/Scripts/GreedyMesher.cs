using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Custom.Importer;

public class GreedyMeshData
{
    public int x, y, z;
    public int width, height;
    public Vector3Int direction;
    public byte type;

    public GreedyMeshData(int x, int y, int z, int width, int height, Vector3Int direction, byte type)
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

public class GreedyMesher
{
    private Database database;
    private byte[] voxelData;
    private int width, height, depth;

    private List<GreedyMeshData> meshData;

    public GreedyMesher(Database database, byte[] voxelData, int width, int height, int depth)
    {
        this.database = database;
        this.voxelData = voxelData;
        this.width = width;
        this.height = height;
        this.depth = depth;
        this.meshData = new List<GreedyMeshData>();
    }

    public Mesh GenerateMesh()
    {
        meshData.Clear();
        bool[] visited;

        // Front
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;
                    
                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x, y, z + 1) != 0 && database.GetVoxelData(GetVoxel(x, y, z + 1)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (x + currWidth < width && GetVoxel(x + currWidth, y, z) != 0 && !visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] && GetVoxel(x + currWidth, y, z + 1) == 0 && GetVoxel(x + currWidth, y, z) == voxelType && database.GetVoxelData(GetVoxel(x + currWidth, y, z)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] = true;
                        currWidth++;
                    }

                    for (int ry = y + 1; ry < height; ry++)
                    {
                        bool rowGood = true;
                        for (int rx = x; rx < x + currWidth; rx++)
                        {
                            if (GetVoxel(rx, ry, z) == 0 || visited[Helpers.CoordinatesToIndex(rx, ry, z, width)] || GetVoxel(rx, ry, z + 1) != 0 || GetVoxel(rx, ry, z) != voxelType || database.GetVoxelData(GetVoxel(rx, ry, z)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rx = x; rx < x + currWidth; rx++)
                            {
                                visited[Helpers.CoordinatesToIndex(rx, ry, z, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(0, 0, 1), voxelType));
                }
            }
        }

        // Back
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;

                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x, y, z - 1) != 0 && database.GetVoxelData(GetVoxel(x, y, z - 1)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (x + currWidth < width && GetVoxel(x + currWidth, y, z) != 0 && !visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] && GetVoxel(x + currWidth, y, z - 1) == 0 && GetVoxel(x + currWidth, y, z) == voxelType && database.GetVoxelData(GetVoxel(x + currWidth, y, z)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] = true;
                        currWidth++;
                    }

                    for (int ry = y + 1; ry < height; ry++)
                    {
                        bool rowGood = true;
                        for (int rx = x; rx < x + currWidth; rx++)
                        {
                            if (GetVoxel(rx, ry, z) == 0 || visited[Helpers.CoordinatesToIndex(rx, ry, z, width)] || GetVoxel(rx, ry, z - 1) != 0 || GetVoxel(rx, ry, z) != voxelType || database.GetVoxelData(GetVoxel(rx, ry, z)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rx = x; rx < x + currWidth; rx++)
                            {
                                visited[Helpers.CoordinatesToIndex(rx, ry, z, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(0, 0, -1), voxelType));
                }
            }
        }

        // Right
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;

                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x + 1, y, z) != 0 && database.GetVoxelData(GetVoxel(x + 1, y, z)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (z + currWidth < width && GetVoxel(x, y, z + currWidth) != 0 && !visited[Helpers.CoordinatesToIndex(x, y, z + currWidth, width)] && GetVoxel(x + 1, y, z  + currWidth) == 0 && GetVoxel(x, y, z + currWidth) == voxelType && database.GetVoxelData(GetVoxel(x, y, z + currWidth)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x, y, z + currWidth, width)] = true;
                        currWidth++;
                    }

                    for (int ry = y + 1; ry < height; ry++)
                    {
                        bool rowGood = true;
                        for (int rz = z; rz < z + currWidth; rz++)
                        {
                            if (GetVoxel(x, ry, rz) == 0 || visited[Helpers.CoordinatesToIndex(x, ry, rz, width)] || GetVoxel(x + 1, ry, rz) != 0 || GetVoxel(x, ry, rz) != voxelType || database.GetVoxelData(GetVoxel(x, ry, rz)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rz = z; rz < z + currWidth; rz++)
                            {
                                visited[Helpers.CoordinatesToIndex(x, ry, rz, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(1, 0, 0), voxelType));
                }
            }
        }

        // Left
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;

                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x - 1, y, z) != 0 && database.GetVoxelData(GetVoxel(x - 1, y, z)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (z + currWidth < width && GetVoxel(x, y, z + currWidth) != 0 && !visited[Helpers.CoordinatesToIndex(x, y, z + currWidth, width)] && GetVoxel(x - 1, y, z  + currWidth) == 0 && GetVoxel(x, y, z + currWidth) == voxelType && database.GetVoxelData(GetVoxel(x, y, z + currWidth)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x, y, z + currWidth, width)] = true;
                        currWidth++;
                    }

                    for (int ry = y + 1; ry < height; ry++)
                    {
                        bool rowGood = true;
                        for (int rz = z; rz < z + currWidth; rz++)
                        {
                            if (GetVoxel(x, ry, rz) == 0 || visited[Helpers.CoordinatesToIndex(x, ry, rz, width)] || GetVoxel(x - 1, ry, rz) != 0 || GetVoxel(x, ry, rz) != voxelType || database.GetVoxelData(GetVoxel(x, ry, rz)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rz = z; rz < z + currWidth; rz++)
                            {
                                visited[Helpers.CoordinatesToIndex(x, ry, rz, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(-1, 0, 0), voxelType));
                }
            }
        }

        // Top
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;

                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x, y + 1, z) != 0 && database.GetVoxelData(GetVoxel(x, y + 1, z)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (x + currWidth < width && GetVoxel(x + currWidth, y, z) != 0 && !visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] && GetVoxel(x + currWidth, y + 1, z) == 0 && GetVoxel(x + currWidth, y, z) == voxelType && database.GetVoxelData(GetVoxel(x + currWidth, y, z)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] = true;
                        currWidth++;
                    }

                    for (int rz = z + 1; rz < depth; rz++)
                    {
                        bool rowGood = true;
                        for (int rx = x; rx < x + currWidth; rx++)
                        {
                            if (GetVoxel(rx, y, rz) == 0 || visited[Helpers.CoordinatesToIndex(rx, y, rz, width)] || GetVoxel(rx, y + 1, rz) != 0 || GetVoxel(rx, y, rz) != voxelType || database.GetVoxelData(GetVoxel(rx, y, rz)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rx = x; rx < x + currWidth; rx++)
                            {
                                visited[Helpers.CoordinatesToIndex(rx, y, rz, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(0, 1, 0), voxelType));
                }
            }
        }

        // Bottom
        visited = new bool[width * height * depth];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte voxelType = GetVoxel(x, y, z);
                    JSONData voxelData = database.GetVoxelData(voxelType);
                    bool isTransparent = voxelType > 0 && voxelData.isTransparent;

                    if (voxelData.type != "Voxel" || visited[Helpers.CoordinatesToIndex(x, y, z, width)] || GetVoxel(x, y, z) == 0 || (GetVoxel(x, y - 1, z) != 0 && database.GetVoxelData(GetVoxel(x, y - 1, z)).isTransparent == isTransparent)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    while (x + currWidth < width && GetVoxel(x + currWidth, y, z) != 0 && !visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] && GetVoxel(x + currWidth, y - 1, z) == 0 && GetVoxel(x + currWidth, y, z) == voxelType && database.GetVoxelData(GetVoxel(x + currWidth, y, z)).isTransparent == isTransparent)
                    {
                        visited[Helpers.CoordinatesToIndex(x + currWidth, y, z, width)] = true;
                        currWidth++;
                    }

                    for (int rz = z + 1; rz < depth; rz++)
                    {
                        bool rowGood = true;
                        for (int rx = x; rx < x + currWidth; rx++)
                        {
                            if (GetVoxel(rx, y, rz) == 0 || visited[Helpers.CoordinatesToIndex(rx, y, rz, width)] || GetVoxel(rx, y - 1, rz) != 0 || GetVoxel(rx, y, rz) != voxelType || database.GetVoxelData(GetVoxel(rx, y, rz)).isTransparent != isTransparent)
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rx = x; rx < x + currWidth; rx++)
                            {
                                visited[Helpers.CoordinatesToIndex(rx, y, rz, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, new Vector3Int(0, -1, 0), voxelType));
                }
            }
        }

        MeshData solidMeshData = new MeshData();
        MeshData transparentMeshData = new MeshData();

        foreach (GreedyMeshData data in meshData)
        {
            JSONData voxelData = database.GetVoxelData(data.type);
            MeshData target = voxelData.isTransparent ? transparentMeshData : solidMeshData;
            Debug.Log("Voxel data: " + voxelData.displayName);
            
            float uRepeat = data.width;
            float vRepeat = data.height;

            // Front
            if (data.direction == new Vector3Int(0, 0, 1))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 1));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 0, 1));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height, 1));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, data.height, 1));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 2));
                target.uvs.Add(new Vector4(uRepeat, 0, voxelData.GetId(), 2));
                target.uvs.Add(new Vector4(0, vRepeat, voxelData.GetId(), 2));
                target.uvs.Add(new Vector4(uRepeat, vRepeat, voxelData.GetId(), 2));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[0, j]);
            }

            // Back
            if (data.direction == new Vector3Int(0, 0, -1))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 0, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, data.height, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height, 0));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 3));
                target.uvs.Add(new Vector4(0, vRepeat, voxelData.GetId(), 3));
                target.uvs.Add(new Vector4(uRepeat, 0, voxelData.GetId(), 3));
                target.uvs.Add(new Vector4(uRepeat, vRepeat, voxelData.GetId(), 3));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[1, j]);
            }

            // Right
            if (data.direction == new Vector3Int(1, 0, 0))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, 0, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, data.height, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, 0, data.width));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, data.height, data.width));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 4));
                target.uvs.Add(new Vector4(0, vRepeat, voxelData.GetId(), 4));
                target.uvs.Add(new Vector4(uRepeat, 0, voxelData.GetId(), 4));
                target.uvs.Add(new Vector4(uRepeat, vRepeat, voxelData.GetId(), 4));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[4, j]);
            }

            // Left
            if (data.direction == new Vector3Int(-1, 0, 0))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, data.width));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height, data.width));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 5));
                target.uvs.Add(new Vector4(uRepeat, 0, voxelData.GetId(), 5));
                target.uvs.Add(new Vector4(0, vRepeat, voxelData.GetId(), 5));
                target.uvs.Add(new Vector4(uRepeat, vRepeat, voxelData.GetId(), 5));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[5, j]);
            }

            // Top
            if (data.direction == new Vector3Int(0, 1, 0))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 1, data.height));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 1, data.height));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 1, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 1, 0));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 0));
                target.uvs.Add(new Vector4(0, uRepeat, voxelData.GetId(), 0));
                target.uvs.Add(new Vector4(vRepeat, 0, voxelData.GetId(), 0));
                target.uvs.Add(new Vector4(vRepeat, uRepeat, voxelData.GetId(), 0));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[2, j]);
            }

            // Bottom
            if (data.direction == new Vector3Int(0, -1, 0))
            {
                int vertexIndex = target.vertices.Count;

                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, data.height));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 0));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 0, data.height));
                target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 0, 0));

                // Add UVs with tiling
                target.uvs.Add(new Vector4(0, 0, voxelData.GetId(), 1));
                target.uvs.Add(new Vector4(vRepeat, 0, voxelData.GetId(), 1));
                target.uvs.Add(new Vector4(0, uRepeat, voxelData.GetId(), 1));
                target.uvs.Add(new Vector4(vRepeat, uRepeat, voxelData.GetId(), 1));

                for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[3, j]);
            }
        }

        Mesh mesh = new Mesh();
        mesh.Clear();
        mesh.MarkDynamic();
        mesh.name = "Greedy Mesh";
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
}

public class MeshData
{
    public List<Vector3> vertices = new List<Vector3>();
    public List<int> triangles = new List<int>();
    public List<Vector4> uvs = new List<Vector4>();
}
