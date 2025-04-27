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

        // Back
        CalculateGreedy(new Vector3Int(1, 0, 0), new Vector3Int(0, 0, 1));
        // Front
        CalculateGreedy(new Vector3Int(-1, 0, 0), new Vector3Int(0, 0, -1));
        // Right
        CalculateGreedy(new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0));
        // Left
        CalculateGreedy(new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0));
        // Top
        CalculateGreedy(new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 0));
        // Bottom
        CalculateGreedy(new Vector3Int(0, -1, 0), new Vector3Int(0, -1, 0));

        MeshData solidMeshData = new MeshData();
        MeshData transparentMeshData = new MeshData();

        foreach (GreedyMeshData data in meshData)
        {
            JSONData voxelData = database.GetVoxelData(data.type);
            MeshData target = voxelData.isTransparent ? transparentMeshData : solidMeshData;

            switch (voxelData.type)
            {
                case "Voxel":
                    BuildVoxel(data, voxelData, target);
                    break;
                case "Liquid":
                    BuildLiquid(data, voxelData, target);
                    break;
                default:
                    break;
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

    private bool ShouldSkip(int x, int y, int z, Vector3Int dir, Vector3Int facing, bool[] visited)
    {
        if (visited[Helpers.CoordinatesToIndex(x, y, z, width)]){ return true;}

        // Current
        byte voxelType = GetVoxel(x, y, z);
        if (voxelType == 0){ return true;}

        JSONData voxelData = database.GetVoxelData(voxelType);
        if (!voxelData.canGreedyMesh){ return true;}

        // Facing
        byte facingVoxel = GetVoxel(x + facing.x, y + facing.y, z + facing.z);
        JSONData facingData = database.GetVoxelData(facingVoxel);
        if (facingVoxel != 0) 
        {
            if (facingData.isTransparent == voxelData.isTransparent) { return true; } //  && facingVoxel == voxelType && facingData.canGreedyMesh
            if (facingData.isTransparent != voxelData.isTransparent && voxelData.isTransparent) { return true; }
        }

        return false;
    }

    private bool ValidNeighbour(int x1, int y1, int z1, int x2, int y2, int z2, Vector3Int facing, bool[] visited)
    {
        int visitedIndex = Helpers.CoordinatesToIndex(x2, y2, z2, width);
        if (visitedIndex >= 0 && visited[visitedIndex]) return false;

        // Current
        byte voxelType = GetVoxel(x1, y1, z1);
        JSONData voxelData = database.GetVoxelData(voxelType);
        if (!voxelData.canGreedyMesh){ return false;}

        // Neighbour
        byte neighbour = GetVoxel(x2, y2, z2);
        if (neighbour == 0) return false;
        if (neighbour != voxelType) return false;

        JSONData neighbourData = database.GetVoxelData(neighbour);
        if(neighbourData.isTransparent != voxelData.isTransparent) return false;

        // Facing
        byte facingVoxel = GetVoxel(x2 + facing.x, y2 + facing.y, z2 + facing.z);
        JSONData facingData = database.GetVoxelData(facingVoxel);
        if (facingVoxel != 0) 
        {
            if (facingData.isTransparent == voxelData.isTransparent && facingData.canGreedyMesh) { return false; }
            if (facingData.isTransparent != voxelData.isTransparent && voxelData.isTransparent) { return false; }
        }

        return true;
    }

    private void CalculateGreedy(Vector3Int dir, Vector3Int facing)
    {
        bool[] visited = new bool[width * height * depth];
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (ShouldSkip(x, y, z, dir, facing, visited)) continue;
                    visited[Helpers.CoordinatesToIndex(x, y, z, width)] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    bool isY = dir.y != 0;
                    bool isX = isY ? true : dir.x != 0;
                    while ((isX ? x : z) + currWidth < width && ValidNeighbour(x, y, z, x + (isX ? currWidth : 0), y, z + (!isX ? currWidth : 0), facing, visited))
                    {
                        visited[Helpers.CoordinatesToIndex(x + (isX ? currWidth : 0), y, z + (!isX ? currWidth : 0), width)] = true;
                        currWidth++;
                    }

                    for (int ry = (!isY ? y : z) + 1; ry < height; ry++)
                    {
                        bool rowGood = true;
                        for (int rx = (isX ? x : z); rx < (isX ? x : z) + currWidth; rx++)
                        {
                            if (!ValidNeighbour(x, y, z, !isX ? x : rx, !isY ? ry : y, !isY ? isX ? z : rx : ry, facing, visited))
                            {
                                rowGood = false;
                                break;
                            }
                        }

                        if (rowGood)
                        {
                            currHeight++;
                            for (int rx = (isX ? x : z); rx < (isX ? x : z) + currWidth; rx++)
                            {
                                visited[Helpers.CoordinatesToIndex(!isX ? x : rx, !isY ? ry : y, !isY ? isX ? z : rx : ry, width)] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    meshData.Add(new GreedyMeshData(x, y, z, currWidth, currHeight, facing, GetVoxel(x, y, z)));
                }
            }
        }
    }

    private byte GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return 0;
        return voxelData[x + y * width + z * width * height];
    }

    private void BuildVoxel(GreedyMeshData data, JSONData voxelData, MeshData target)
    {
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

    private void BuildLiquid(GreedyMeshData data, JSONData voxelData, MeshData target)
    {
        float uRepeat = data.width;
        float vRepeat = data.height;
        float liquidOffset = 1f / 16f * 2f;

        // Front
        if (data.direction == new Vector3Int(0, 0, 1))
        {
            int vertexIndex = target.vertices.Count;

            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 1));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 0, 1));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height - liquidOffset, 1));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, data.height - liquidOffset, 1));

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
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, data.height - liquidOffset, 0));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 0, 0));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height - liquidOffset, 0));

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
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, data.height - liquidOffset, 0));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, 0, data.width));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(1, data.height - liquidOffset, data.width));

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
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height - liquidOffset, 0));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, data.height - liquidOffset, data.width));

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

            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 1 - liquidOffset, data.height));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 1 - liquidOffset, data.height));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(0, 1 - liquidOffset, 0));
            target.vertices.Add(new Vector3(data.x, data.y, data.z) + new Vector3(data.width, 1 - liquidOffset, 0));

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
