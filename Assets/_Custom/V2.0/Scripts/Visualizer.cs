using System.Drawing;
using UnityEngine;
using Custom.Voxels.Helpers;
using System.Collections.Generic;

namespace Custom.Voxels
{
    public class Visualizer : MonoBehaviour
    {
        public Material mat;
        public bool showDebug = false;
        public int chunksX = 1;
        public int chunksZ = 1;

        private List<Chunk> chunks;

        private void Start()
        {
            Database database = new Database();
            WorldSettings.RENDERPARAMS = new RenderParams(mat)
            {
                instanceID = 0,
                layer = 1,
                receiveShadows = true,
                renderingLayerMask = 1,
                rendererPriority = 1,
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On
            };

            chunks = new List<Chunk>();
            for (int x = 0; x < chunksX; x++)
            {
                for (int z = 0; z < chunksZ; z++)
                {
                    chunks.Add(new Chunk(new Vector3(x * WorldSettings.SIZE.x, 0, z * WorldSettings.SIZE.z)));
                }
            }
        }

        private void Update()
        {
            if (chunks == null || chunks.Count <= 0) return;

            foreach (Chunk item in chunks)
            {
                item.Update();
            }
        }

        void OnDrawGizmos()
        {
            if (!showDebug || chunks == null || chunks.Count <= 0) return;

            foreach (Chunk item in chunks)
            {
                UnityEngine.Color color = UnityEngine.Color.white;
                if (item.hasGenerated)
                {
                    color = UnityEngine.Color.green;
                }
                else if (item.hasCalculated)
                {
                    color = UnityEngine.Color.blue;
                }
                Gizmos.color = color;
                Gizmos.DrawWireCube(item.pos + Vector3.one * (WorldSettings.SIZE.x / 2), new Vector3(WorldSettings.SIZE.x, WorldSettings.SIZE.y, WorldSettings.SIZE.z));
            }
        }
    }
}
