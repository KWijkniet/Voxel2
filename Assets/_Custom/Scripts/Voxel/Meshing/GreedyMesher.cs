using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxel.Core;

namespace Voxel.Meshing
{
    /// <summary>
    /// Greedy mesh generator - merges adjacent faces of the same type into larger quads.
    /// Reduces polygon count by ~80% compared to naive meshing.
    /// Reference: https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/
    /// </summary>
    public static class GreedyMesher
    {
        /// <summary>
        /// Generate mesh data for a chunk using greedy meshing.
        /// </summary>
        public static MeshData GenerateMesh(NativeArray<ushort> voxels)
        {
            int maxFaces = Constants.CHUNK_VOLUME * 6;
            int maxVertices = maxFaces * 4;
            int maxTriangles = maxFaces * 6;

            var meshData = new MeshData(maxVertices, maxTriangles, Allocator.TempJob);

            var job = new GreedyMeshJob
            {
                voxels = voxels,
                vertices = meshData.vertices,
                normals = meshData.normals,
                colors = meshData.colors,
                triangles = meshData.triangles,
                vertexCount = meshData.vertexCountArray,
                triangleCount = meshData.triangleCountArray
            };

            job.Run();

            meshData.vertexCount = meshData.vertexCountArray[0];
            meshData.triangleCount = meshData.triangleCountArray[0];

            return meshData;
        }

        /// <summary>
        /// Schedule greedy mesh generation job.
        /// </summary>
        public static JobHandle ScheduleGenerateMesh(
            NativeArray<ushort> voxels,
            MeshData meshData,
            JobHandle dependency = default)
        {
            var job = new GreedyMeshJob
            {
                voxels = voxels,
                vertices = meshData.vertices,
                normals = meshData.normals,
                colors = meshData.colors,
                triangles = meshData.triangles,
                vertexCount = meshData.vertexCountArray,
                triangleCount = meshData.triangleCountArray
            };

            return job.Schedule(dependency);
        }
    }

    /// <summary>
    /// Burst-compiled greedy mesh generation job.
    /// </summary>
    [BurstCompile]
    public struct GreedyMeshJob : IJob
    {
        [ReadOnly] public NativeArray<ushort> voxels;

        [NativeDisableParallelForRestriction]
        public NativeArray<float3> vertices;

        [NativeDisableParallelForRestriction]
        public NativeArray<float3> normals;

        [NativeDisableParallelForRestriction]
        public NativeArray<float4> colors;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> triangles;

        public NativeArray<int> vertexCount;
        public NativeArray<int> triangleCount;

        public void Execute()
        {
            int vCount = 0;
            int tCount = 0;

            // Process each of the 6 face directions separately
            // This ensures correct face orientation

            // +X faces (Right)
            ProcessFaces(0, 1, 2, 1, ref vCount, ref tCount);
            // -X faces (Left)
            ProcessFaces(0, 1, 2, -1, ref vCount, ref tCount);
            // +Y faces (Top)
            ProcessFaces(1, 2, 0, 1, ref vCount, ref tCount);
            // -Y faces (Bottom)
            ProcessFaces(1, 2, 0, -1, ref vCount, ref tCount);
            // +Z faces (Front)
            ProcessFaces(2, 0, 1, 1, ref vCount, ref tCount);
            // -Z faces (Back)
            ProcessFaces(2, 0, 1, -1, ref vCount, ref tCount);

            vertexCount[0] = vCount;
            triangleCount[0] = tCount;
        }

        /// <summary>
        /// Process all faces for one direction using greedy meshing.
        /// </summary>
        /// <param name="d">Primary axis (0=X, 1=Y, 2=Z)</param>
        /// <param name="u">First perpendicular axis</param>
        /// <param name="v">Second perpendicular axis</param>
        /// <param name="dir">Direction: 1 for positive, -1 for negative</param>
        private void ProcessFaces(int d, int u, int v, int dir, ref int vCount, ref int tCount)
        {
            var mask = new NativeArray<ushort>(Constants.CHUNK_SIZE * Constants.CHUNK_SIZE, Allocator.Temp);

            int3 normal = int3.zero;
            normal[d] = dir;

            // Sweep through slices perpendicular to axis d
            for (int slice = 0; slice < Constants.CHUNK_SIZE; slice++)
            {
                // Build mask for this slice
                int n = 0;
                for (int vPos = 0; vPos < Constants.CHUNK_SIZE; vPos++)
                {
                    for (int uPos = 0; uPos < Constants.CHUNK_SIZE; uPos++)
                    {
                        int3 pos = int3.zero;
                        pos[d] = slice;
                        pos[u] = uPos;
                        pos[v] = vPos;

                        // Get current block and neighbor in the direction we're checking
                        ushort currentBlock = GetVoxel(pos);

                        int3 neighborPos = pos;
                        neighborPos[d] += dir;
                        ushort neighborBlock = GetVoxel(neighborPos);

                        // Face is visible if current is solid and neighbor is air
                        // (for positive dir) or neighbor is solid and current is air (for negative dir)
                        if (dir > 0)
                        {
                            // Positive direction: face on the + side of solid block
                            mask[n] = (currentBlock != Constants.BLOCK_AIR && neighborBlock == Constants.BLOCK_AIR)
                                ? currentBlock : (ushort)0;
                        }
                        else
                        {
                            // Negative direction: face on the - side of solid block
                            mask[n] = (currentBlock != Constants.BLOCK_AIR && neighborBlock == Constants.BLOCK_AIR)
                                ? currentBlock : (ushort)0;
                        }

                        n++;
                    }
                }

                // Generate quads using greedy algorithm
                n = 0;
                for (int vPos = 0; vPos < Constants.CHUNK_SIZE; vPos++)
                {
                    for (int uPos = 0; uPos < Constants.CHUNK_SIZE;)
                    {
                        ushort block = mask[n];
                        if (block != 0)
                        {
                            // Find width (expand in u direction)
                            int w = 1;
                            while (uPos + w < Constants.CHUNK_SIZE && mask[n + w] == block)
                            {
                                w++;
                            }

                            // Find height (expand in v direction)
                            int h = 1;
                            bool done = false;
                            while (vPos + h < Constants.CHUNK_SIZE && !done)
                            {
                                for (int k = 0; k < w; k++)
                                {
                                    if (mask[n + k + h * Constants.CHUNK_SIZE] != block)
                                    {
                                        done = true;
                                        break;
                                    }
                                }
                                if (!done) h++;
                            }

                            // Create quad
                            int3 pos = int3.zero;
                            pos[d] = slice;
                            pos[u] = uPos;
                            pos[v] = vPos;

                            // Offset position for the face location
                            if (dir > 0)
                            {
                                pos[d] += 1; // Face is on the + side of the voxel
                            }

                            int3 du = int3.zero;
                            int3 dv = int3.zero;
                            du[u] = w;
                            dv[v] = h;

                            float3 worldPos = new float3(pos.x, pos.y, pos.z) * Constants.VOXEL_SCALE;
                            float3 worldDu = new float3(du.x, du.y, du.z) * Constants.VOXEL_SCALE;
                            float3 worldDv = new float3(dv.x, dv.y, dv.z) * Constants.VOXEL_SCALE;
                            float3 worldNormal = new float3(normal.x, normal.y, normal.z);
                            float4 color = GetBlockColor(block);

                            AddQuad(worldPos, worldDu, worldDv, worldNormal, color, dir > 0, ref vCount, ref tCount);

                            // Clear mask for processed faces
                            for (int l = 0; l < h; l++)
                            {
                                for (int k = 0; k < w; k++)
                                {
                                    mask[n + k + l * Constants.CHUNK_SIZE] = 0;
                                }
                            }

                            uPos += w;
                            n += w;
                        }
                        else
                        {
                            uPos++;
                            n++;
                        }
                    }
                }
            }

            mask.Dispose();
        }

        private ushort GetVoxel(int3 pos)
        {
            if (pos.x < 0 || pos.x >= Constants.CHUNK_SIZE ||
                pos.y < 0 || pos.y >= Constants.CHUNK_SIZE ||
                pos.z < 0 || pos.z >= Constants.CHUNK_SIZE)
            {
                return Constants.BLOCK_AIR;
            }
            return voxels[ChunkData.ToIndex(pos)];
        }

        private void AddQuad(float3 pos, float3 du, float3 dv, float3 normal, float4 color, bool facingPositive, ref int vCount, ref int tCount)
        {
            int startVertex = vCount;

            // Vertices in clockwise order when viewed from outside (Unity front-face winding)
            if (facingPositive)
            {
                // For +X, +Y, +Z faces - clockwise when viewed from positive axis
                vertices[vCount] = pos;
                vertices[vCount + 1] = pos + du;
                vertices[vCount + 2] = pos + du + dv;
                vertices[vCount + 3] = pos + dv;
            }
            else
            {
                // For -X, -Y, -Z faces - clockwise when viewed from negative axis
                vertices[vCount] = pos;
                vertices[vCount + 1] = pos + dv;
                vertices[vCount + 2] = pos + du + dv;
                vertices[vCount + 3] = pos + du;
            }

            // Set normals and colors
            for (int i = 0; i < 4; i++)
            {
                normals[vCount + i] = normal;
                colors[vCount + i] = color;
            }

            vCount += 4;

            // Two triangles (clockwise winding for Unity)
            triangles[tCount] = startVertex;
            triangles[tCount + 1] = startVertex + 1;
            triangles[tCount + 2] = startVertex + 2;
            triangles[tCount + 3] = startVertex;
            triangles[tCount + 4] = startVertex + 2;
            triangles[tCount + 5] = startVertex + 3;

            tCount += 6;
        }

        private float4 GetBlockColor(ushort blockType)
        {
            switch (blockType)
            {
                case Constants.BLOCK_STONE:
                    return new float4(0.5f, 0.5f, 0.5f, 1f);
                case Constants.BLOCK_DIRT:
                    return new float4(0.55f, 0.35f, 0.2f, 1f);
                case Constants.BLOCK_GRASS:
                    return new float4(0.3f, 0.7f, 0.2f, 1f);
                default:
                    return new float4(1f, 0f, 1f, 1f);
            }
        }
    }
}
