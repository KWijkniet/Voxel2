using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace Custom.Voxels
{
    internal class WorldSettings
    {
        public static readonly int3 SIZE = new(16, 16, 16);
        public static RenderParams RENDERPARAMS;
    }
}
