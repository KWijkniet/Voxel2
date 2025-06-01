using System.Drawing;
using UnityEngine;
using Custom.Voxels.Helpers;
using System.Collections.Generic;
using Unity.Mathematics;
using static UnityEditor.PlayerSettings;

namespace Custom.Voxels
{
    public class Visualizer : MonoBehaviour
    {
        public Material mat;
        public bool showDebug = false;
        //public int chunksX = 1;
        //public int chunksZ = 1;
        public byte generationMode = 0;
        public Transform player;

        [Range(0, 30)]
        public int renderDistance;
        [Range(0, 32)]
        public int calculateDistance;
        [Range(0, 30)]
        public int lod0 = 0;
        [Range(0, 30)]
        public int lod1 = 0;
        [Range(0, 30)]
        public int lod2 = 0;

        private Vector3 lastpos;
        private int3 centerChunkPos;

        private void Start()
        {
            Database database = new Database();
            WorldSettings.camera = Camera.main;
            WorldSettings.emptyVoxels = new Unity.Collections.NativeArray<byte>(0, Unity.Collections.Allocator.Persistent);
            WorldSettings.RENDERPARAMS = new RenderParams(mat)
            {
                instanceID = 0,
                layer = 1,
                receiveShadows = true,
                renderingLayerMask = 1,
                rendererPriority = 1,
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On
            };

            WorldSettings.renderDistance = renderDistance;
            WorldSettings.preloadDistance = calculateDistance;
            WorldSettings.lodRange.Add(new System.Tuple<int, int>(lod0 * lod0, 1));
            WorldSettings.lodRange.Add(new System.Tuple<int, int>(lod1 * lod1, 2));
            WorldSettings.lodRange.Add(new System.Tuple<int, int>(lod2 * lod2, 4));

            lastpos = player.position;
            centerChunkPos = new int3((int)player.position.x, (int)player.position.y, (int)player.position.z);
            SetChunksAround(centerChunkPos);

            foreach (Chunk item in WorldSettings.chunks.GetAll())
            {
                item.LoadNeighbours();
            }
        }

        private void OnApplicationQuit()
        {
            WorldSettings.chunks.Clear();
            if (WorldSettings.emptyVoxels.IsCreated) WorldSettings.emptyVoxels.Dispose();

            foreach (Chunk item in WorldSettings.chunks.GetAll())
            {
                item.Dispose();
            }
        }

        private void Update()
        {
            if (WorldSettings.chunks.Count() <= 0) return;
            WorldSettings.cameraPlanes = GeometryUtility.CalculateFrustumPlanes(WorldSettings.camera);
            WorldSettings.chunks.UpdateBatched();

            if (Vector3.Distance(lastpos, player.position) > WorldSettings.SIZE.x * 2)
            {
                lastpos = player.position;
                centerChunkPos = new int3((int)player.position.x, (int)player.position.y, (int)player.position.z);
                SetChunksAround(centerChunkPos);
            }
        }

        private void SetChunksAround(int3 center)
        {
            int range = WorldSettings.preloadDistance;
            int rangeSq = range * range;

            int3 centerChunkPos = center / WorldSettings.SIZE;
            int y = 0; // Fixed height

            for (int x = -range; x <= range; x++)
            {
                for (int z = -range; z <= range; z++)
                {
                    int distSq = x * x + z * z;
                    if (distSq <= rangeSq)
                    {
                        int3 offset = new int3(x, y, z);
                        int3 chunkCoord = centerChunkPos + offset;
                        int3 pos = chunkCoord * WorldSettings.SIZE;

                        WorldSettings.chunks.SetChunk(pos, new Chunk(
                            pos,
                            generationMode
                        ));
                    }
                }
            }
            WorldSettings.chunks.EnqueueChunksForUpdate(center);
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
