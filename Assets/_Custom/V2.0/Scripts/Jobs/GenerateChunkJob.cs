using Custom.Voxels.Generators;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Custom.Voxels.Jobs
{
    [BurstCompile]
    internal struct GenerateChunkJob : IJob
    {
        [ReadOnly] public int3 size;
        [ReadOnly] public NativeArray<byte> voxels;
        [ReadOnly] public byte generationMode;
        [ReadOnly] public Neighbours neighbours;
        [ReadOnly] public int divider;

        public NativeList<float3> vertices;
        public NativeList<int> triangles;
        public NativeList<float2> uvs;

        public void Execute()
        {
            if (generationMode == 0) new VoxelMesh(size, voxels, vertices, triangles, uvs, neighbours);
            if (generationMode == 1) new GreedyMesh(size, voxels, vertices, triangles, uvs, neighbours);
            if (generationMode == 2)
            {
                NativeArray<byte> lodVoxels = new LevelOfDetail(size, voxels, divider).DownSample();
                int3 newSize = divider == 1 ? size : new int3(size.x / divider, size.y / divider, size.z / divider);
                new GreedyMesh(newSize, lodVoxels, vertices, triangles, uvs, neighbours, divider);
                lodVoxels.Dispose();

                if (divider != 1)
                {
                    for (int i = 0; i < vertices.Length; i++) vertices[i] *= divider;
                }
            }
        }
    }
}

public struct Neighbours
{
    [ReadOnly] public NativeArray<byte> xPos, xNeg, yPos, yNeg, zPos, zNeg;
}