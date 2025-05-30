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

        public NativeList<float3> vertices;
        public NativeList<int> triangles;
        public NativeList<float2> uvs;

        public void Execute()
        {
            if (generationMode == 0) new VoxelMesh(size, voxels, vertices, triangles, uvs, neighbours);
            if (generationMode == 1) new GreedyMesh(size, voxels, vertices, triangles, uvs, neighbours);
        }
    }
}

public struct Neighbours
{
    [ReadOnly] public NativeArray<byte> xPos, xNeg, yPos, yNeg, zPos, zNeg;
}