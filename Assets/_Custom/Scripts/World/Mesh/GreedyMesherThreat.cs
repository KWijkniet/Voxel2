using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Custom.Importer;
using Unity.Collections;
using Unity.Mathematics;

public class GreedyMeshThreatData
{
    public int x, y, z;
    public int width, height;
    public Vector3Int direction;
    public byte type;

    public GreedyMeshThreatData(int x, int y, int z, int width, int height, Vector3Int direction, byte type)
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

public class GreedyMesherThreat
{
    private NativeArray<byte> voxelData;
    private int width, height, depth;
    private float3 chunkPos;

    private List<GreedyMeshThreatData> meshThreatData;

    public GreedyMesherThreat(NativeArray<byte> voxelData, int width, int height, int depth, float3 pos)
    {
        this.chunkPos = pos;
        this.voxelData = voxelData;
        this.width = width;
        this.height = height;
        this.depth = depth;
        this.meshThreatData = new List<GreedyMeshThreatData>();
    }

    public MeshBuffers GenerateMesh(MeshBuffers meshBuffer, bool isTransparent)
    {
        meshThreatData.Clear();

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

        MeshThreatData solidMeshThreatData = new MeshThreatData();
        MeshThreatData transparentMeshThreatData = new MeshThreatData();

        foreach (GreedyMeshThreatData data in meshThreatData)
        {
            JSONData vData = Database.GetVoxelData(data.type);
            MeshThreatData target = vData.isTransparent && isTransparent ? transparentMeshThreatData : solidMeshThreatData;

            switch (vData.type)
            {
                case "Voxel":
                    BuildVoxel(data, vData, target);
                    break;
                case "Liquid":
                    BuildLiquid(data, vData, target);
                    break;
                default:
                    break;
            }
        }

        if (isTransparent)
        {
            meshBuffer.Vertices = Helpers.ConvertToNativeList(transparentMeshThreatData.vertices, meshBuffer.allocator);
            meshBuffer.Triangles = Helpers.ConvertToNativeList(transparentMeshThreatData.triangles, meshBuffer.allocator);
            meshBuffer.UVs = Helpers.ConvertToNativeList(transparentMeshThreatData.uvs, meshBuffer.allocator);
        }
        else
        {
            foreach (var item in solidMeshThreatData.vertices)
            {
                meshBuffer.Vertices.Add(item);
            }
            foreach (var item in solidMeshThreatData.triangles)
            {
                meshBuffer.Triangles.Add(item);
            }
            foreach (var item in solidMeshThreatData.uvs)
            {
                meshBuffer.UVs.Add(item);
            }
        }

        return meshBuffer;
    }

    private bool ShouldSkip(int x, int y, int z, Vector3Int dir, Vector3Int facing, bool[] visited)
    {
        int visitedIndex = Helpers.CoordinatesToIndex(x, y, z, width, height, depth);
        if (visitedIndex == -1) return true;
        if (visited[visitedIndex]) return true;

        // Current
        int voxelIndex = Helpers.CoordinatesToIndex(x, y, z, width, height, depth);
        if (voxelIndex == -1) return true; 
        byte voxelType = voxelData[voxelIndex];
        if (voxelType == 0) return true;

        JSONData jsonVoxel = Database.GetVoxelData(voxelType);
        if (!jsonVoxel.canGreedyMesh) return true;

        // Facing
        int facingIndex = Helpers.CoordinatesToIndex(x + facing.x, y + facing.y, z + facing.z, width, height, depth);
        byte facingVoxel;

        if (facingIndex == -1) facingVoxel = GetNeighbourVoxel(x + facing.x, y + facing.y, z + facing.z); // OUTSIDE OF CHUNK
        else facingVoxel = voxelData[facingIndex];

        JSONData facingData = Database.GetVoxelData(facingVoxel);
        if (facingVoxel != 0) 
        {
            if (facingData.isTransparent == jsonVoxel.isTransparent) { return true; } //  && facingVoxel == voxelType && facingData.canGreedyMesh
            if (facingData.isTransparent != jsonVoxel.isTransparent && jsonVoxel.isTransparent) { return true; }
        }

        return false;
    }

    private bool ValidNeighbour(int x1, int y1, int z1, int x2, int y2, int z2, Vector3Int facing, bool[] visited)
    {
        int visitedIndex = Helpers.CoordinatesToIndex(x2, y2, z2, width, height, depth);
        if (visitedIndex >= 0 && visited[visitedIndex]) return false;

        // Current
        int voxelIndex = Helpers.CoordinatesToIndex(x1, y1, z1, width, height, depth);
        if (voxelIndex == -1) return false;

        byte voxelType = voxelData[voxelIndex];
        JSONData jsonVoxel = Database.GetVoxelData(voxelType);
        if (!jsonVoxel.canGreedyMesh){ return false;}

        // Neighbour
        int neighbourIndex = Helpers.CoordinatesToIndex(x2, y2, z2, width, height, depth);
        if (voxelIndex == -1) return false;

        byte neighbour = voxelData[neighbourIndex];
        if (neighbour == 0) return false;
        if (neighbour != voxelType) return false;

        JSONData neighbourData = Database.GetVoxelData(neighbour);
        if(neighbourData.isTransparent != jsonVoxel.isTransparent) return false;

        // Facing
        int facingIndex = Helpers.CoordinatesToIndex(x2 + facing.x, y2 + facing.y, z2 + facing.z, width, height, depth);
        if (facingIndex >= 0)
        {
            byte facingVoxel = voxelData[facingIndex];
            if (facingVoxel != 0) 
            {
                JSONData facingData = Database.GetVoxelData(facingVoxel);
                if (facingData.isTransparent == jsonVoxel.isTransparent && facingData.canGreedyMesh) { return false; }
                if (facingData.isTransparent != jsonVoxel.isTransparent && jsonVoxel.isTransparent) { return false; }
            }
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
                    int currentIndex = Helpers.CoordinatesToIndex(x, y, z, width, height, depth);
                    if (currentIndex == -1) continue;

                    visited[currentIndex] = true;

                    int currWidth = 1;
                    int currHeight = 1;

                    bool isY = dir.y != 0;
                    bool isX = isY ? true : dir.x != 0;
                    while ((isX ? x : z) + currWidth < width && ValidNeighbour(x, y, z, x + (isX ? currWidth : 0), y, z + (!isX ? currWidth : 0), facing, visited))
                    {
                        int visitedIndex = Helpers.CoordinatesToIndex(x + (isX ? currWidth : 0), y, z + (!isX ? currWidth : 0), width, height, depth);
                        if (visitedIndex == -1) break;

                        visited[visitedIndex] = true;
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
                                int visitedIndex = Helpers.CoordinatesToIndex(!isX ? x : rx, !isY ? ry : y, !isY ? isX ? z : rx : ry, width, height, depth);
                                if (visitedIndex == -1) break;
                                
                                visited[visitedIndex] = true;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    meshThreatData.Add(new GreedyMeshThreatData(x, y, z, currWidth, currHeight, facing, voxelData[currentIndex]));
                }
            }
        }
    }

    private void BuildVoxel(GreedyMeshThreatData data, JSONData vData, MeshThreatData target)
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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, uRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, uRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, uRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, uRepeat, vData.GetId()));

            for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[3, j]);
        }
    }

    private void BuildLiquid(GreedyMeshThreatData data, JSONData vData, MeshThreatData target)
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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, vRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(uRepeat, vRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector3(0, uRepeat, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, 0, vData.GetId()));
            target.uvs.Add(new Vector3(vRepeat, uRepeat, vData.GetId()));

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
            target.uvs.Add(new Vector3(0, 0, vData.GetId()));
            target.uvs.Add(new Vector4(vRepeat, 0, vData.GetId(), 1));
            target.uvs.Add(new Vector4(0, uRepeat, vData.GetId(), 1));
            target.uvs.Add(new Vector4(vRepeat, uRepeat, vData.GetId(), 1));

            for (int j = 0; j < 6; j++) target.triangles.Add(vertexIndex + faceTriangles[3, j]);
        }
    }

    private byte GetNeighbourVoxel(int x, int y, int z)
    {
        // Remove current
        Chunk chunk = World.Instance.GetChunkThread((int) chunkPos.x - x, (int) chunkPos.y - y, (int) chunkPos.z - z);
        if (chunk == null) return 0;

        NativeArray<byte> voxels = chunk.GetVoxelData();
        int index = Helpers.CoordinatesToIndex(x, y, z, width, height, depth);
        if (index < 0) return 1;

        return voxels[index];
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

public class MeshThreatData
{
    public List<Vector3> vertices = new List<Vector3>();
    public List<int> triangles = new List<int>();
    public List<Vector3> uvs = new List<Vector3>();
}
