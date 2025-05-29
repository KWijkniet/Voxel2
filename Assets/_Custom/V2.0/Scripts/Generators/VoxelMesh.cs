using Custom.Voxels.Helpers;
using Custom.Voxels.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Custom.Voxels.Generators
{
    internal class VoxelMesh
    {
        private int3 size;
        private NativeArray<byte> voxels;

        private NativeList<float3> vertices;
        private NativeList<int> triangles;
        private NativeList<float2> uvs;

        public VoxelMesh(int3 size, NativeArray<byte> voxels, NativeList<float3> vertices, NativeList<int> triangles, NativeList<float2> uvs)
        {
            this.size = size;
            this.voxels = voxels;

            this.vertices = vertices;
            this.triangles = triangles;
            this.uvs = uvs;

            int sizeSquared = size.x * size.y * size.z;
            for (int i = 0; i < sizeSquared; i++)
            {
                if (voxels[i] == 0) continue; // Air
                float3 pos = MathematicsHelper.IndexToXYZ(i, size);

                // Check each face — if neighbor is air or out of bounds, add face
                TryAddFace(pos, new int3(-1, 0, 0), CubeFace.Left);   // -X
                TryAddFace(pos, new int3(1, 0, 0), CubeFace.Right);  // +X
                TryAddFace(pos, new int3(0, -1, 0), CubeFace.Bottom); // -Y
                TryAddFace(pos, new int3(0, 1, 0), CubeFace.Top);     // +Y
                TryAddFace(pos, new int3(0, 0, -1), CubeFace.Back);   // -Z
                TryAddFace(pos, new int3(0, 0, 1), CubeFace.Front);   // +Z
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