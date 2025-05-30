using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Custom.Voxels
{
    internal class WorldSettings
    {
        public static readonly int3 SIZE = new(16, 16, 16);
        public static RenderParams RENDERPARAMS;
        public static Camera camera;
        public static Plane[] cameraPlanes;
        public static ChunkManager chunks = new ChunkManager();
        public static NativeArray<byte> emptyVoxels;

        public static int3[] neighbourPositions = new int3[]
        {
            new int3( 16,  0,  0),
            new int3(-16,  0,  0),
            new int3(  0, 16,  0),
            new int3(  0, -16, 0),
            new int3(  0,  0, 16),
            new int3(  0,  0, -16)
        };
    }
}
