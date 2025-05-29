using System.Drawing;
using UnityEngine;
using Custom.Voxels.Helpers;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Custom.Voxels
{
    public class Visualizer : MonoBehaviour
    {
        public Material mat;
        public bool showDebug = false;
        public int chunksX = 1;
        public int chunksZ = 1;
        public byte generationMode = 0;

        private void Start()
        {
            Database database = new Database();
            WorldSettings.camera = Camera.main;
            WorldSettings.RENDERPARAMS = new RenderParams(mat)
            {
                instanceID = 0,
                layer = 1,
                receiveShadows = true,
                renderingLayerMask = 1,
                rendererPriority = 1,
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On
            };

            for (int x = 0; x < chunksX; x++)
            {
                for (int z = 0; z < chunksZ; z++)
                {
                    int3 pos = new(
                        (int) transform.position.x + x * WorldSettings.SIZE.x,
                        (int) transform.position.y + 0,
                        (int) transform.position.z + z * WorldSettings.SIZE.z
                    );
                    WorldSettings.chunks.SetChunk(pos, new Chunk(
                        pos,
                        generationMode
                    ));
                }
            }

            foreach (Chunk item in WorldSettings.chunks.GetAll())
            {
                item.LoadNeighbours();
            }
        }

        private void OnApplicationQuit()
        {
            WorldSettings.chunks.Clear();
        }

        private void Update()
        {
            if (WorldSettings.chunks.Count() <= 0) return;
            WorldSettings.cameraPlanes = GeometryUtility.CalculateFrustumPlanes(WorldSettings.camera);

            foreach (Chunk item in WorldSettings.chunks.GetAll())
            {
                item.Update();
            }
        }

        void OnDrawGizmos()
        {
            if (!showDebug || WorldSettings.chunks.Count() <= 0) return;

            foreach (Chunk item in WorldSettings.chunks.GetAll())
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
                Gizmos.DrawWireCube(MathematicsHelper.Int3ToVector3(item.pos) + Vector3.one * (WorldSettings.SIZE.x / 2), new Vector3(WorldSettings.SIZE.x, WorldSettings.SIZE.y, WorldSettings.SIZE.z));
            }
        }
    }
}
