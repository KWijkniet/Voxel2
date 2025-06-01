using Custom.Importer;
using Custom.Voxels.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    internal class LevelOfDetail
    {
        private int3 size;
        private int divider;
        private NativeArray<byte> voxels;

        public LevelOfDetail(int3 size, NativeArray<byte> voxels, int divider)
        {
            this.size = size;
            this.divider = divider;
            this.voxels = voxels;
        }

        public NativeArray<byte> DownSample()
        {
            int3 newSize = new int3(size.x / divider, size.y / divider, size.z / divider);
            NativeArray<byte> lowRes = new(newSize.x * newSize.y * newSize.z, Allocator.Temp);

            if (divider == 1)
            {
                lowRes.CopyFrom(voxels);
                return lowRes;
            }

            for (int x = 0; x < newSize.x; x++)
            {
                for (int y = 0; y < newSize.y; y++)
                {
                    for (int z = 0; z < newSize.z; z++)
                    {
                        int solidCount = 0;

                        for (int dz = 0; dz < divider; dz++)
                        {
                            for (int dy = 0; dy < divider; dy++)
                            {
                                for (int dx = 0; dx < divider; dx++)
                                {
                                    int srcX = x * divider + dx;
                                    int srcY = y * divider + dy;
                                    int srcZ = z * divider + dz;

                                    int srcIndex = MathematicsHelper.XYZToIndex(srcX, srcY, srcZ, size);
                                    if (voxels[srcIndex] > 0)
                                    {
                                        solidCount++;
                                    }
                                }
                            }
                        }

                        int destIndex = MathematicsHelper.XYZToIndex(x, y, z, newSize);
                        lowRes[destIndex] = (byte)(solidCount >= 3 ? 1 : 0);
                    }
                }
            }

            return lowRes;
        }
    }
}
