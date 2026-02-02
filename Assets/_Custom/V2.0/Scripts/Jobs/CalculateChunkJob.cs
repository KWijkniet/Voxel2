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
        [ReadOnly] public int3 pos;
        [ReadOnly] public NativeArray<int> heightmap;

        public NativeArray<byte> voxels;

        public void Execute()
        {
            Unity.Mathematics.Random random = new Unity.Mathematics.Random(1234);
            for (int x = 0; x < size.x; x++)
            {
                for (int z = 0; z < size.z; z++)
                {
                    int maxHeight = heightmap[x * size.x + z];
                    for (int y = 0; y < size.y; y++)
                    {
                        int index = MathematicsHelper.XYZToIndex(x, y, z, size);
                        //byte value = (byte) (noise.cnoise(new float2((pos.x + x) * 10000, (pos.z + z) * 10000)) > 0.5f ? 1 : 0);

                        // Calculate voxel type
                        if (pos.y + y <= maxHeight)
                        {
                            voxels[index] = 1;
                        }
                    }
                }
            }
        }
    }
}