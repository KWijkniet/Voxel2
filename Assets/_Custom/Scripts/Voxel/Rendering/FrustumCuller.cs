using Unity.Mathematics;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Rendering
{
    /// <summary>
    /// Performs frustum culling to skip rendering chunks outside the camera view.
    /// </summary>
    public class FrustumCuller
    {
        private Plane[] frustumPlanes = new Plane[6];
        private Camera camera;

        public FrustumCuller(Camera camera)
        {
            this.camera = camera;
        }

        /// <summary>
        /// Update frustum planes from camera. Call once per frame before culling checks.
        /// </summary>
        public void UpdateFrustum()
        {
            if (camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
        }

        /// <summary>
        /// Update frustum planes from custom matrix (for shadow culling etc).
        /// </summary>
        public void UpdateFrustum(Matrix4x4 viewProjection)
        {
            GeometryUtility.CalculateFrustumPlanes(viewProjection, frustumPlanes);
        }

        /// <summary>
        /// Check if a chunk is visible in the frustum.
        /// </summary>
        public bool IsChunkVisible(int3 chunkCoord)
        {
            Bounds bounds = GetChunkBounds(chunkCoord);
            return GeometryUtility.TestPlanesAABB(frustumPlanes, bounds);
        }

        /// <summary>
        /// Check if bounds are visible in the frustum.
        /// </summary>
        public bool AreBoundsVisible(Bounds bounds)
        {
            return GeometryUtility.TestPlanesAABB(frustumPlanes, bounds);
        }

        /// <summary>
        /// Get world-space bounds for a chunk.
        /// </summary>
        public static Bounds GetChunkBounds(int3 chunkCoord)
        {
            float3 worldPos = ChunkCoord.ChunkToWorld(chunkCoord);
            Vector3 center = new Vector3(worldPos.x, worldPos.y, worldPos.z) +
                           Vector3.one * Constants.CHUNK_WORLD_SIZE * 0.5f;
            Vector3 size = Vector3.one * Constants.CHUNK_WORLD_SIZE;
            return new Bounds(center, size);
        }

        /// <summary>
        /// Batch check multiple chunks for visibility.
        /// Returns array indicating visibility for each input chunk.
        /// </summary>
        public void CheckVisibility(int3[] chunkCoords, bool[] results)
        {
            for (int i = 0; i < chunkCoords.Length; i++)
            {
                results[i] = IsChunkVisible(chunkCoords[i]);
            }
        }
    }
}
