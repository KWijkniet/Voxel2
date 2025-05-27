using Custom.Importer;
using Custom.Voxels.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEditor.PlayerSettings;
using static UnityEngine.GraphicsBuffer;

namespace Custom.Voxels.Generators
{
    internal class GreedyMesh
    {
        private int3 size;
        private NativeArray<byte> voxels;

        private NativeList<float3> vertices;
        private NativeList<int> triangles;
        private NativeList<float2> uvs;

        private NativeArray<byte> visited;
        private NativeList<GreedyVoxel> greedyVoxels;

        public GreedyMesh(int3 size, NativeArray<byte> voxels, NativeList<float3> vertices, NativeList<int> triangles, NativeList<float2> uvs)
        {
            this.size = size;
            this.voxels = voxels;

            this.vertices = vertices;
            this.triangles = triangles;
            this.uvs = uvs;
            this.greedyVoxels = new(Allocator.Temp);

            // Calculate greedy voxels
            CalculateGreedy(new int3(1, 0, 0), new int3(0, 0, 1));
            this.visited.Dispose();

            // Build mesh
            BuildMesh();
            this.greedyVoxels.Dispose();
        }

        private void CalculateGreedy(int3 dir, int3 facing)
        {
            if (visited.IsCreated) visited.Dispose();
            visited = new(size.x * size.y * size.z, Allocator.Temp);

            for (int z = 0; z < size.x; z++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    for (int x = 0; x < size.z; x++)
                    {
                        // Validate current voxel
                        if (IsValid(new int3(x, y, z), facing) == 0) continue;
                        int posIndex = MathematicsHelper.XYZToIndex(x, y, z, size);

                        // Start current progress
                        visited[posIndex] = 1;
                        byte currWidth = 1;
                        byte currHeight = 1;

                        // Set direction values
                        byte isY = (byte) (dir.y != 0 ? 1 : 0);
                        byte isX = (byte) (isY == 1 ? 1 : (byte) (dir.x != 0 ? 1 : 0));
                        Debug.Log("isX: " + isX);
                        // Loop over x axis first
                        while (
                            (isX == 1 ? x : z) + currWidth < (isX == 1 ? size.x : size.z) &&
                            IsValidNeighbour(
                                new int3(x, y, z),
                                new int3(x + (isX == 1 ? currWidth : 0), y, z + (isX == 0 ? currWidth : 0)),
                                facing
                            ) == 1
                        )
                        {
                            int visitedIndex = MathematicsHelper.XYZToIndex(x + (isX == 1 ? currWidth : 0), y, z + (isX == 0 ? currWidth : 0), size);
                            visited[visitedIndex] = 1;
                            currWidth++;
                        }

                        //// Loop over y axis
                        //for (int rh = (isY == 0 ? y : z) + 1; rh < (isY == 1 ? size.z : size.y); rh++)
                        //{
                        //    byte validRow = 1;

                        //    // Validate row
                        //    int rwStart = (isX == 1 ? x : z);
                        //    int rwEnd = rwStart + currWidth;

                        //    for (int rw = rwStart; rw < rwEnd; rw++)
                        //    {
                        //        int vx = isX == 1 ? rw : x;
                        //        int vy = isY == 0 ? rh : y;
                        //        int vz = isX == 0 ? rw : z;
                        //        if (isY == 1) vz = rh;

                        //        if (IsValidNeighbour(new int3(x, y, z), new int3(vx, vy, vz), facing) == 0)
                        //        {
                        //            validRow = 0;
                        //            break;
                        //        }
                        //    }

                        //    // Update visited if row is valid
                        //    if (validRow == 1)
                        //    {
                        //        currHeight++;
                        //        for (int rw = rwStart; rw < rwEnd; rw++)
                        //        {
                        //            int vx = isX == 1 ? rw : x;
                        //            int vy = isY == 0 ? rh : y;
                        //            int vz = isX == 0 ? rw : z;
                        //            if (isY == 1) vz = rh;

                        //            int visitedIndex = MathematicsHelper.XYZToIndex(vx, vy, vz, size);
                        //            visited[visitedIndex] = 1;
                        //        }
                        //    }
                        //    else
                        //    {
                        //        break;
                        //    }
                        //}

                        //Store valid Greedy voxel data
                        greedyVoxels.Add(new GreedyVoxel
                        {
                            id = voxels[posIndex],
                            pos = new int3(x, y, z),
                            size = new int2(currWidth, currHeight),
                            facing = facing
                        });
                    }
                }
            }
        }

        private byte IsValid(int3 pos, int3 facing)
        {
            // Validate position
            int posIndex = MathematicsHelper.XYZToIndex(pos.x, pos.y, pos.z, size);
            if (posIndex < 0 || posIndex >= size.x * size.y * size.z) return 0;
            if (visited[posIndex] == 1) return 0;

            // Validate id
            byte id = voxels[posIndex];
            if (id == 0) return 0;

            // Validate type
            VoxelEntry? voxel = Database.GetEntry(id);
            if (voxel == null || voxel?.canGreedyMesh == 0 || voxel?.type != VoxelType.Voxel) return 0;

            // Validate facing position
            int facingIndex = MathematicsHelper.XYZToIndex(pos.x + facing.x, pos.y + facing.y, pos.z + facing.z, size);
            if (facingIndex < 0 || facingIndex > size.x * size.y * size.z) return 1; // Outside current chunk

            // Validate id
            byte facingId = voxels[facingIndex];
            if (facingId == 0) return 1;

            // Validate type
            VoxelEntry? facingVoxel = Database.GetEntry(facingId);
            if (facingVoxel == null) return 1;
            if (facingVoxel?.type == VoxelType.Voxel && voxel?.isTransparent == facingVoxel?.isTransparent) return 0;

            return 1;
        }

        private byte IsValidNeighbour(int3 currPos, int3 nextPos, int3 facing)
        {
            int currPosIndex = MathematicsHelper.XYZToIndex(currPos.x, currPos.y, currPos.z, size);
            byte currentId = voxels[currPosIndex];
            //VoxelEntry? currVoxel = Database.GetEntry(currentId);

            // Validate position
            int nextPosIndex = MathematicsHelper.XYZToIndex(nextPos.x, nextPos.y, nextPos.z, size);
            if (nextPosIndex < 0 || nextPosIndex >= size.x * size.y * size.z) return 0;
            if (visited[nextPosIndex] == 1) return 0;

            // Validate id
            byte nextId = voxels[nextPosIndex];
            if (nextId == 0 || currentId != nextId) return 0;

            // Validate type
            VoxelEntry? nextVoxel = Database.GetEntry(nextId);
            if (nextVoxel == null || nextVoxel?.canGreedyMesh == 0 || nextVoxel?.type != VoxelType.Voxel) return 0;

            // Validate facing position
            int facingIndex = MathematicsHelper.XYZToIndex(nextPos.x + facing.x, nextPos.y + facing.y, nextPos.z + facing.z, size);
            if (facingIndex < 0 || facingIndex > size.x * size.y * size.z) return 1; // Outside current chunk

            // Validate id
            byte facingId = voxels[facingIndex];
            if (facingId == 0) return 1;
            if (facingId == nextId) return 0;

            // Validate type
            VoxelEntry? facingVoxel = Database.GetEntry(facingId);
            if (facingVoxel == null) return 1;
            if (facingVoxel?.type == VoxelType.Voxel && nextVoxel?.isTransparent == facingVoxel?.isTransparent) return 0;

            return 1;
        }

        private void BuildMesh()
        {
            Debug.Log("Greedy Voxels Length: " + greedyVoxels.Length);
            foreach (GreedyVoxel voxel in greedyVoxels)
            {
                byte id = voxel.id;
                int3 pos = voxel.pos;
                int width = voxel.size.x;
                int height = voxel.size.y;
                if (pos.x + width > size.x) Debug.Log(pos.x + ", " + pos.y + ": " + width);

                if (voxel.facing.Equals(new int3(0, 0, 1)))
                {
                    int vertexIndex = vertices.Length;

                    // Set vertices
                    vertices.Add(new float3(pos.x, pos.y, pos.z) + new float3(1, 0, 0));
                    vertices.Add(new float3(pos.x, pos.y, voxel.pos.z) + new float3(1, height, 0));
                    vertices.Add(new float3(pos.x, pos.y, pos.z) + new float3(1, 0, width));
                    vertices.Add(new float3(pos.x, pos.y, pos.z) + new float3(1, height, width));

                    // Add UVs with tiling
                    uvs.Add(new float2(0, 0));
                    uvs.Add(new float2(0, height));
                    uvs.Add(new float2(width, 0));
                    uvs.Add(new float2(width, height));

                    for (int j = 0; j < 6; j++) triangles.Add(vertexIndex + faceTriangles[6 * 4 + j]);
                }
            }
        }

        private static int[] faceTriangles = {
            0, 1, 2, 1, 3, 2, // Back
            0, 2, 1, 1, 2, 3, // Front
            0, 1, 2, 1, 3, 2, // Top
            0, 1, 2, 1, 3, 2, // Bottom
            0, 1, 2, 2, 1, 3, // Right
            0, 1, 2, 1, 3, 2  // Left
        };
    }

    internal struct GreedyVoxel
    {
        public byte id;
        public int3 pos;
        public int2 size;
        public int3 facing;
    }
}
