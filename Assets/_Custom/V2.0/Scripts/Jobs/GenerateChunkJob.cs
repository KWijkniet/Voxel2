using Custom.Voxels.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Custom.Voxels.Jobs.GenerateChunkJob;

namespace Custom.Voxels.Jobs
{
    [BurstCompile]
    internal struct GenerateChunkJob : IJob
    {
        [ReadOnly] public int3 size;
        [ReadOnly] public NativeArray<byte> voxels;

        public NativeList<float3> vertices;
        public NativeList<int> triangles;
        public NativeList<float2> uvs;

        public void Execute()
        {
            for (int x = 0; x < size.x; x++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    for (int z = 0; z < size.z; z++)
                    {
                        int index = MathematicsHelper.XYZToIndex(x, y, z, size);
                        if (voxels[index] == 0) continue; // Air

                        float3 pos = new float3(x, y, z);

                        // Check each face — if neighbor is air or out of bounds, add face
                        TryAddFace(pos, new int3(-1, 0, 0), CubeFace.Left);   // -X
                        TryAddFace(pos, new int3(1, 0, 0), CubeFace.Right);  // +X
                        TryAddFace(pos, new int3(0, -1, 0), CubeFace.Bottom); // -Y
                        TryAddFace(pos, new int3(0, 1, 0), CubeFace.Top);     // +Y
                        TryAddFace(pos, new int3(0, 0, -1), CubeFace.Back);   // -Z
                        TryAddFace(pos, new int3(0, 0, 1), CubeFace.Front);   // +Z
                    }
                }
            }
        }

        private void TryAddFace(float3 pos, int3 normal, CubeFace face)
        {
            int3 neighbor = (int3)pos + normal;
            if (!InBounds(neighbor) || voxels[MathematicsHelper.XYZToIndex(neighbor.x, neighbor.y, neighbor.z, size)] == 0)
            {
                AddQuad(pos, face);
            }
        }

        private void AddQuad(float3 pos, CubeFace face)
        {
            int vertStart = vertices.Length;
            int faceIndex = (int)face * 4;

            for (int i = 0; i < 4; i++)
            {
                vertices.Add(pos + faceVertices[faceIndex + i]);
                uvs.Add(faceuvs[i]);
            }

            triangles.Add(vertStart + 0);
            triangles.Add(vertStart + 1);
            triangles.Add(vertStart + 2);
            triangles.Add(vertStart + 2);
            triangles.Add(vertStart + 1);
            triangles.Add(vertStart + 3);
        }

        private bool InBounds(int3 p)
        {
            return p.x >= 0 && p.y >= 0 && p.z >= 0 &&
                   p.x < size.x && p.y < size.y && p.z < size.z;
        }

        public enum CubeFace { Left, Right, Bottom, Top, Back, Front }

        private static readonly float3[] faceVertices = new float3[6 * 4]
        {
            // Left (-X)
            new float3(0, 0, 0), new float3(0, 0, 1), new float3(0, 1, 0), new float3(0, 1, 1),
            // Right (+X)
            new float3(1, 0, 1), new float3(1, 0, 0), new float3(1, 1, 1), new float3(1, 1, 0),
            // Bottom (-Y)
            new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 0, 1), new float3(1, 0, 1),
            // Top (+Y)
            new float3(0, 1, 1), new float3(1, 1, 1), new float3(0, 1, 0), new float3(1, 1, 0),
            // Back (-Z)
            new float3(1, 0, 0), new float3(0, 0, 0), new float3(1, 1, 0), new float3(0, 1, 0),
            // Front (+Z)
            new float3(0, 0, 1), new float3(1, 0, 1), new float3(0, 1, 1), new float3(1, 1, 1),
        };

        private static readonly float2[] faceuvs = new float2[4]
        {
            new float2(0, 0),
            new float2(1, 0),
            new float2(0, 1),
            new float2(1, 1),
        };
    }
}
