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
    internal class MeshHelper
    {
        public static Vector3[] NativeToVector3(NativeList<float3> values)
        {
            Vector3[] result = new Vector3[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float3 f3 = values[i];
                result[i] = new Vector3(f3.x, f3.y, f3.z);
            }
            return result;
        }

        public static Vector3[] NativeToVector3(NativeArray<float3> values)
        {
            Vector3[] result = new Vector3[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float3 f3 = values[i];
                result[i] = new Vector3(f3.x, f3.y, f3.z);
            }
            return result;
        }

        public static Vector2[] NativeToFloat2(NativeList<float2> values)
        {
            Vector2[] result = new Vector2[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float2 f2 = values[i];
                result[i] = new Vector2(f2.x, f2.y);
            }
            return result;
        }

        public static Vector2[] NativeToFloat2(NativeArray<float2> values)
        {
            Vector2[] result = new Vector2[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float2 f2 = values[i];
                result[i] = new Vector2(f2.x, f2.y);
            }
            return result;
        }

        public static int[] NativeToInt(NativeList<int> values)
        {
            int[] result = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = values[i];
            }
            return result;
        }

        public static int[] NativeToInt(NativeArray<int> values)
        {
            int[] result = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = values[i];
            }
            return result;
        }
    }
}
