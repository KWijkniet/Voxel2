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
        private Neighbours neighbours;

        public GreedyMesh(int3 size, NativeArray<byte> voxels, NativeList<float3> vertices, NativeList<int> triangles, NativeList<float2> uvs, Neighbours neighbours)
        {
            this.size = size;
            this.voxels = voxels;

            this.vertices = vertices;
            this.triangles = triangles;
            this.uvs = uvs;
            this.greedyVoxels = new(Allocator.Temp);
            this.neighbours = neighbours;

            // Calculate greedy voxels
            CalculateGreedy(new int3(1, 0, 0), new int3(0, 0, 1));      // Back
            CalculateGreedy(new int3(1, 0, 0), new int3(0, 0, -1));     // Front
            CalculateGreedy(new int3(0, 0, 1), new int3(1, 0, 0));      // Right
            CalculateGreedy(new int3(0, 0, 1), new int3(-1, 0, 0));     // Left
            CalculateGreedy(new int3(0, 1, 0), new int3(0, 1, 0));      // Top
            CalculateGreedy(new int3(0, 1, 0), new int3(0, -1, 0));     // Bottom
            this.visited.Dispose();

            // Build mesh
            BuildMesh();
            this.greedyVoxels.Dispose();
        }

        private void CalculateGreedy(int3 dir, int3 facing)
        {
            if (visited.IsCreated) visited.Dispose();
            visited = new(size.x * size.y * size.z, Allocator.Temp);

            int sizeSquared = size.x * size.y * size.z;
            for (int i = 0; i < sizeSquared; i++)
            {
                int3 pos = MathematicsHelper.IndexToXYZ(i, size);
                int x = pos.x;
                int y = pos.y;
                int z = pos.z;

                // Validate current voxel
                if (IsValid(new int3(x, y, z), facing) == 0) continue;

                // Start current progress
                visited[i] = 1;
                byte currWidth = 1;
                byte currHeight = 1;

                // Set direction values
                byte isY = (byte)(dir.y != 0 ? 1 : 0);
                byte isX = (byte)(isY == 1 ? 1 : (byte)(dir.x != 0 ? 1 : 0));

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

                // Loop over y axis
                for (int rh = (isY == 0 ? y : z) + 1; rh < (isY == 1 ? size.z : size.y); rh++)
                {
                    byte validRow = 1;

                    // Validate row
                    int rwStart = (isX == 1 ? x : z);
                    int rwEnd = rwStart + currWidth;

                    for (int rw = rwStart; rw < rwEnd; rw++)
                    {
                        int vx = isX == 1 ? rw : x;
                        int vy = isY == 0 ? rh : y;
                        int vz = isX == 0 ? rw : z;
                        if (isY == 1) vz = rh;

                        if (IsValidNeighbour(new int3(x, y, z), new int3(vx, vy, vz), facing) == 0)
                        {
                            validRow = 0;
                            break;
                        }
                    }

                    // Update visited if row is valid
                    if (validRow == 1)
                    {
                        currHeight++;
                        for (int rw = rwStart; rw < rwEnd; rw++)
                        {
                            int vx = isX == 1 ? rw : x;
                            int vy = isY == 0 ? rh : y;
                            int vz = isX == 0 ? rw : z;
                            if (isY == 1) vz = rh;

                            int visitedIndex = MathematicsHelper.XYZToIndex(vx, vy, vz, size);
                            visited[visitedIndex] = 1;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                //Store valid Greedy voxel data
                greedyVoxels.Add(new GreedyVoxel
                {
                    id = voxels[i],
                    pos = new int3(x, y, z),
                    size = new int2(currWidth, currHeight),
                    facing = facing
                });
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
            byte neighbourId = 0;
            int facingIndex = MathematicsHelper.XYZToIndex(pos.x + facing.x, pos.y + facing.y, pos.z + facing.z, size);
            if (facingIndex < 0 || facingIndex > size.x * size.y * size.z)
            {
                // Outside current chunk
                neighbourId = GetNeighbouringVoxel(pos + facing);
                if (neighbourId == 0) return 1;
            }

            // Validate id
            byte facingId = neighbourId != 0 ? neighbourId : voxels[facingIndex];
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
            byte neighbourId = 0;
            int facingIndex = MathematicsHelper.XYZToIndex(nextPos.x + facing.x, nextPos.y + facing.y, nextPos.z + facing.z, size);
            if (facingIndex < 0 || facingIndex > size.x * size.y * size.z)
            {
                // Outside current chunk
                neighbourId = GetNeighbouringVoxel(nextPos + facing);
                if (neighbourId == 0) return 1;
            }

            // Validate id
            byte facingId = neighbourId != 0 ? neighbourId : voxels[facingIndex];
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
            foreach (GreedyVoxel voxel in greedyVoxels)
            {
                byte id = voxel.id;
                int3 pos = voxel.pos;
                int width = voxel.size.x;
                int height = voxel.size.y;

                int vertexIndex = vertices.Length;

                // Precompute for convenience
                int x = pos.x;
                int y = pos.y;
                int z = pos.z;

                // RIGHT face (+X)
                if (voxel.facing.Equals(new int3(1, 0, 0)))
                {
                    vertices.Add(new float3(x + 1, y, z));
                    vertices.Add(new float3(x + 1, y + height, z));
                    vertices.Add(new float3(x + 1, y, z + width));
                    vertices.Add(new float3(x + 1, y + height, z + width));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 4); // index 4 = Right
                }

                // LEFT face (-X)
                else if (voxel.facing.Equals(new int3(-1, 0, 0)))
                {
                    vertices.Add(new float3(x, y, z + width));
                    vertices.Add(new float3(x, y + height, z + width));
                    vertices.Add(new float3(x, y, z));
                    vertices.Add(new float3(x, y + height, z));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 5); // index 5 = Left
                }

                // TOP face (+Y)
                else if (voxel.facing.Equals(new int3(0, 1, 0)))
                {
                    vertices.Add(new float3(x, y + 1, z + height));
                    vertices.Add(new float3(x + width, y + 1, z + height));
                    vertices.Add(new float3(x, y + 1, z));
                    vertices.Add(new float3(x + width, y + 1, z));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 2); // index 2 = Top
                }

                // BOTTOM face (-Y)
                else if (voxel.facing.Equals(new int3(0, -1, 0)))
                {
                    vertices.Add(new float3(x, y, z + height));
                    vertices.Add(new float3(x, y, z));
                    vertices.Add(new float3(x + width, y, z + height));
                    vertices.Add(new float3(x + width, y, z));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 3); // index 3 = Bottom
                }

                // BACK face (+Z)
                else if (voxel.facing.Equals(new int3(0, 0, 1)))
                {
                    vertices.Add(new float3(x, y, z + 1));
                    vertices.Add(new float3(x + width, y, z + 1));
                    vertices.Add(new float3(x, y + height, z + 1));
                    vertices.Add(new float3(x + width, y + height, z + 1));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 0); // index 0 = Back
                }

                // FRONT face (-Z)
                else if (voxel.facing.Equals(new int3(0, 0, -1)))
                {
                    vertices.Add(new float3(x + width, y, z));
                    vertices.Add(new float3(x + width, y + height, z));
                    vertices.Add(new float3(x, y, z));
                    vertices.Add(new float3(x, y + height, z));

                    AddUVs(width, height);
                    AddTriangles(vertexIndex, 1); // index 1 = Front
                }
            }
        }

        // Helper to add UVs
        private void AddUVs(int width, int height)
        {
            uvs.Add(new float2(0, 0));
            uvs.Add(new float2(0, height));
            uvs.Add(new float2(width, 0));
            uvs.Add(new float2(width, height));
        }

        // Helper to add face triangles
        private void AddTriangles(int vertexIndex, int faceIndex)
        {
            int baseIndex = faceIndex * 6;
            for (int i = 0; i < 6; i++)
            {
                triangles.Add(vertexIndex + faceTriangles[baseIndex + i]);
            }
        }

        private byte GetNeighbouringVoxel(int3 pos)
        {
            int3 dir = new(0, 0, 0);

            if (pos.x >= size.x)
            {
                pos.x -= size.x;
                dir.x = 1;
            }
            else if (pos.x < 0)
            {
                pos.x += size.x;
                dir.x = -1;
            }

            if (pos.y >= size.y)
            {
                pos.y -= size.y;
                dir.y = 1;
            }
            else if (pos.y < 0)
            {
                pos.y += size.y;
                dir.y = -1;
            }

            if (pos.z >= size.z)
            {
                pos.z -= size.z;
                dir.z = 1;
            }
            else if (pos.z < 0)
            {
                pos.z += size.z;
                dir.z = -1;
            }
            return GetNeighbouringChunkVoxel(dir, pos);
        }

        private byte GetNeighbouringChunkVoxel(int3 dir, int3 pos)
        {
            NativeArray<byte> res = default;
            if (dir.x > 0) res = neighbours.xPos;
            else if (dir.x < 0) res = neighbours.xNeg;
            
            if (dir.y > 0) res = neighbours.yPos;
            else if (dir.y < 0) res = neighbours.yNeg;

            if (dir.z > 0) res = neighbours.zPos;
            else if (dir.z < 0) res = neighbours.zNeg;

            if (!res.IsCreated || res.Length <= 0) return 0;
            else return res[MathematicsHelper.XYZToIndex(pos.x, pos.y, pos.z, size)];
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
