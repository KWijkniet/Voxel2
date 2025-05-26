using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Custom.Voxels.Helpers;

namespace Custom.Voxels.Jobs
{
    [BurstCompile]
    internal struct CalculateChunkJob : IJob
    {
        [ReadOnly] public int3 size;
        [ReadOnly] public NativeArray<int> heightmap;

        public NativeArray<byte> voxels;

        public void Execute()
        {
            Unity.Mathematics.Random random = new Unity.Mathematics.Random(1234);
            for (int x = 0; x < size.x; x++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    for (int z = 0; z < size.z; z++)
                    {
                        int index = MathematicsHelper.XYZToIndex(x, y, z, size);
                        
                        // Calculate voxel type
                        voxels[index] = (byte) (random.NextBool() ? 1 : 0);
                    }
                }
            }
        }
    }
}