using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Custom.Voxels.Helpers
{
    internal class MathematicsHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int XYZToIndex(int x, int y, int z, int3 size)
        {
            if ((uint)x >= (uint)size.x || (uint)y >= (uint)size.y || (uint)z >= (uint)size.z)
                return -1;
            return x * size.y * size.z + y * size.z + z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 IndexToXYZ(int index, int3 size)
        {
            int x = index / (size.y * size.z);
            int yz = index % (size.y * size.z);
            int y = yz / size.z;
            int z = yz % size.z;
            return new int3(x, y, z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Float3ToVector3(float3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Int3ToVector3(int3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        public static bool IsWithinDistanceSquared(int3 a, int3 b, int maxDistance)
        {
            int3 delta = a - b;
            int distSq = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
            return distSq <= maxDistance * maxDistance;
        }

        public static int GetDistanceSquared(int3 a, int3 b)
        {
            int3 delta = a - b;
            return delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        }
    }
}
