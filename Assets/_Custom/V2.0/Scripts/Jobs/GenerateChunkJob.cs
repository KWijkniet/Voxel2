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

        public NativeList<float3> vertices;
        public NativeList<int> triangles;
        public NativeList<float2> uvs;

        public void Execute()
        {
            //new VoxelMesh(size, voxels, vertices, triangles, uvs);
            new GreedyMesh(size, voxels, vertices, triangles, uvs);
        }
    }
}
    