using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;
using Voxel.World;

namespace Voxel.Rendering
{
    /// <summary>
    /// Batched chunk renderer using Graphics.RenderMeshInstanced.
    /// Reduces draw calls by grouping chunks with similar properties.
    /// </summary>
    public class BatchedRenderer : System.IDisposable
    {
        private Material material;
        private Camera camera;
        private FrustumCuller frustumCuller;

        // Render params for instanced rendering
        private RenderParams renderParams;

        // Matrix buffer for instancing (max 1023 instances per batch)
        private const int MaxInstancesPerBatch = 1023;
        private Matrix4x4[] matrixBuffer;

        // Stats
        public int DrawCallCount { get; private set; }
        public int VisibleChunkCount { get; private set; }
        public int CulledChunkCount { get; private set; }

        public BatchedRenderer(Material material, Camera camera)
        {
            this.material = material;
            this.camera = camera;
            this.frustumCuller = new FrustumCuller(camera);

            matrixBuffer = new Matrix4x4[MaxInstancesPerBatch];

            renderParams = new RenderParams(material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 10000),
                matProps = new MaterialPropertyBlock(),
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,
                layer = 0
            };
        }

        /// <summary>
        /// Render all visible chunks using batched instancing.
        /// Groups chunks by mesh to minimize draw calls.
        /// </summary>
        public void RenderChunks(IEnumerable<Chunk> chunks)
        {
            DrawCallCount = 0;
            VisibleChunkCount = 0;
            CulledChunkCount = 0;

            frustumCuller.UpdateFrustum();

            // Group chunks by mesh for batching
            var meshGroups = new Dictionary<Mesh, List<Chunk>>();

            foreach (var chunk in chunks)
            {
                if (chunk.State < Chunk.ChunkState.Meshed) continue;

                // Frustum culling
                if (!frustumCuller.IsChunkVisible(chunk.Coord))
                {
                    CulledChunkCount++;
                    continue;
                }

                VisibleChunkCount++;

                // Get mesh from chunk (we'd need to expose this)
                // For now, use the chunk's GameObject renderer
            }

            // Note: Full batched rendering requires restructuring chunk storage
            // to separate mesh data from GameObjects. This is a foundation for that.
        }

        /// <summary>
        /// Render a single mesh at multiple positions using instancing.
        /// </summary>
        public void RenderMeshInstanced(Mesh mesh, List<Matrix4x4> transforms)
        {
            if (mesh == null || transforms.Count == 0) return;

            int remaining = transforms.Count;
            int offset = 0;

            while (remaining > 0)
            {
                int batchSize = math.min(remaining, MaxInstancesPerBatch);

                for (int i = 0; i < batchSize; i++)
                {
                    matrixBuffer[i] = transforms[offset + i];
                }

                Graphics.RenderMeshInstanced(renderParams, mesh, 0, matrixBuffer, batchSize);
                DrawCallCount++;

                remaining -= batchSize;
                offset += batchSize;
            }
        }

        /// <summary>
        /// Render mesh at single position (for unique meshes like chunk meshes).
        /// </summary>
        public void RenderMesh(Mesh mesh, Vector3 position)
        {
            if (mesh == null) return;

            matrixBuffer[0] = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
            Graphics.RenderMeshInstanced(renderParams, mesh, 0, matrixBuffer, 1);
            DrawCallCount++;
        }

        public void Dispose()
        {
            matrixBuffer = null;
        }
    }
}
