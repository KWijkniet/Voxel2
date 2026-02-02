using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxel.Core;

namespace Voxel.Meshing
{
    /// <summary>
    /// LOD (Level of Detail) mesh generator.
    /// Downsamples voxel data and generates lower-poly meshes for distant chunks.
    /// </summary>
    public static class LODMesher
    {
        /// <summary>
        /// LOD levels with their downsample factors.
        /// </summary>
        public enum LODLevel
        {
            LOD0 = 1,   // Full resolution (16x16x16)
            LOD1 = 2,   // Half resolution (8x8x8)
            LOD2 = 4,   // Quarter resolution (4x4x4)
            LOD3 = 8,   // Eighth resolution (2x2x2)
            LOD4 = 16   // Sixteenth resolution (1x1x1 - single voxel)
        }

        /// <summary>
        /// Generate mesh at specified LOD level.
        /// </summary>
        public static MeshData GenerateMesh(NativeArray<ushort> voxels, LODLevel lod)
        {
            int scale = (int)lod;

            if (scale == 1)
            {
                // Full resolution - use standard greedy mesher
                return GreedyMesher.GenerateMesh(voxels);
            }

            // Downsample and mesh
            int lodSize = Constants.CHUNK_SIZE / scale;
            int lodVolume = lodSize * lodSize * lodSize;

            var downsampledVoxels = new NativeArray<ushort>(lodVolume, Allocator.TempJob);

            // Downsample
            var downsampleJob = new DownsampleJob
            {
                sourceVoxels = voxels,
                destVoxels = downsampledVoxels,
                scale = scale,
                lodSize = lodSize
            };
            downsampleJob.Run();

            // Generate mesh from downsampled data
            int maxFaces = lodVolume * 6;
            var meshData = new MeshData(maxFaces * 4, maxFaces * 6, Allocator.TempJob);

            var meshJob = new LODMeshJob
            {
                voxels = downsampledVoxels,
                lodSize = lodSize,
                scale = scale,
                vertices = meshData.vertices,
                normals = meshData.normals,
                colors = meshData.colors,
                triangles = meshData.triangles,
                vertexCount = meshData.vertexCountArray,
                triangleCount = meshData.triangleCountArray
            };
            meshJob.Run();

            meshData.vertexCount = meshData.vertexCountArray[0];
            meshData.triangleCount = meshData.triangleCountArray[0];

            downsampledVoxels.Dispose();

            return meshData;
        }

        /// <summary>
        /// Schedule LOD mesh generation job.
        /// </summary>
        public static JobHandle ScheduleGenerateMesh(
            NativeArray<ushort> voxels,
            LODLevel lod,
            MeshData meshData,
            NativeArray<ushort> downsampledVoxels,
            JobHandle dependency = default)
        {
            int scale = (int)lod;

            if (scale == 1)
            {
                return GreedyMesher.ScheduleGenerateMesh(voxels, meshData, dependency);
            }

            int lodSize = Constants.CHUNK_SIZE / scale;

            var downsampleJob = new DownsampleJob
            {
                sourceVoxels = voxels,
                destVoxels = downsampledVoxels,
                scale = scale,
                lodSize = lodSize
            };
            var downsampleHandle = downsampleJob.Schedule(dependency);

            var meshJob = new LODMeshJob
            {
                voxels = downsampledVoxels,
                lodSize = lodSize,
                scale = scale,
                vertices = meshData.vertices,
                normals = meshData.normals,
                colors = meshData.colors,
                triangles = meshData.triangles,
                vertexCount = meshData.vertexCountArray,
                triangleCount = meshData.triangleCountArray
            };

            return meshJob.Schedule(downsampleHandle);
        }

        /// <summary>
        /// Get recommended LOD level based on distance (in chunks).
        /// </summary>
        public static LODLevel GetLODForDistance(float distanceSquared)
        {
            if (distanceSquared < 4) return LODLevel.LOD0;      // 0-2 chunks
            if (distanceSquared < 64) return LODLevel.LOD1;     // 2-8 chunks
            if (distanceSquared < 256) return LODLevel.LOD2;    // 8-16 chunks
            if (distanceSquared < 1024) return LODLevel.LOD3;   // 16-32 chunks
            return LODLevel.LOD4;                                // 32+ chunks
        }

        /// <summary>
        /// Get downsampled voxel array size for LOD level.
        /// </summary>
        public static int GetLODVolumeSize(LODLevel lod)
        {
            int scale = (int)lod;
            int lodSize = Constants.CHUNK_SIZE / scale;
            return lodSize * lodSize * lodSize;
        }
    }

    /// <summary>
    /// Burst job for downsampling voxel data.
    /// Uses majority voting to determine block type.
    /// </summary>
    [BurstCompile]
    public struct DownsampleJob : IJob
    {
        [ReadOnly] public NativeArray<ushort> sourceVoxels;
        public NativeArray<ushort> destVoxels;
        public int scale;
        public int lodSize;

        public void Execute()
        {
            // Temporary storage for block type counts
            var blockCounts = new NativeArray<int>(256, Allocator.Temp);

            for (int z = 0; z < lodSize; z++)
            {
                for (int y = 0; y < lodSize; y++)
                {
                    for (int x = 0; x < lodSize; x++)
                    {
                        // Clear counts
                        for (int i = 0; i < 256; i++) blockCounts[i] = 0;

                        int solidCount = 0;
                        int totalCount = scale * scale * scale;

                        // Sample all voxels in this region
                        for (int dz = 0; dz < scale; dz++)
                        {
                            for (int dy = 0; dy < scale; dy++)
                            {
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int sx = x * scale + dx;
                                    int sy = y * scale + dy;
                                    int sz = z * scale + dz;

                                    int srcIndex = sx + sy * Constants.CHUNK_SIZE + sz * Constants.CHUNK_SIZE_SQ;
                                    ushort block = sourceVoxels[srcIndex];

                                    if (block != Constants.BLOCK_AIR)
                                    {
                                        solidCount++;
                                        if (block < 256)
                                        {
                                            blockCounts[block]++;
                                        }
                                    }
                                }
                            }
                        }

                        // Majority voting: solid if more than half are solid
                        ushort resultBlock = Constants.BLOCK_AIR;
                        if (solidCount > totalCount / 2)
                        {
                            // Find most common solid block type
                            int maxCount = 0;
                            for (int i = 1; i < 256; i++)
                            {
                                if (blockCounts[i] > maxCount)
                                {
                                    maxCount = blockCounts[i];
                                    resultBlock = (ushort)i;
                                }
                            }
                        }

                        int destIndex = x + y * lodSize + z * lodSize * lodSize;
                        destVoxels[destIndex] = resultBlock;
                    }
                }
            }

            blockCounts.Dispose();
        }
    }

    /// <summary>
    /// Burst job for generating mesh from LOD voxel data.
    /// Similar to greedy mesher but with configurable size and scaling.
    /// </summary>
    [BurstCompile]
    public struct LODMeshJob : IJob
    {
        [ReadOnly] public NativeArray<ushort> voxels;
        public int lodSize;
        public int scale;

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

            float voxelSize = Constants.VOXEL_SCALE * scale;

            // Process each of the 6 face directions
            ProcessFaces(0, 1, 2, 1, voxelSize, ref vCount, ref tCount);  // +X
            ProcessFaces(0, 1, 2, -1, voxelSize, ref vCount, ref tCount); // -X
            ProcessFaces(1, 2, 0, 1, voxelSize, ref vCount, ref tCount);  // +Y
            ProcessFaces(1, 2, 0, -1, voxelSize, ref vCount, ref tCount); // -Y
            ProcessFaces(2, 0, 1, 1, voxelSize, ref vCount, ref tCount);  // +Z
            ProcessFaces(2, 0, 1, -1, voxelSize, ref vCount, ref tCount); // -Z

            vertexCount[0] = vCount;
            triangleCount[0] = tCount;
        }

        private void ProcessFaces(int d, int u, int v, int dir, float voxelSize, ref int vCount, ref int tCount)
        {
            var mask = new NativeArray<ushort>(lodSize * lodSize, Allocator.Temp);

            int3 normal = int3.zero;
            normal[d] = dir;

            for (int slice = 0; slice < lodSize; slice++)
            {
                // Build mask
                int n = 0;
                for (int vPos = 0; vPos < lodSize; vPos++)
                {
                    for (int uPos = 0; uPos < lodSize; uPos++)
                    {
                        int3 pos = int3.zero;
                        pos[d] = slice;
                        pos[u] = uPos;
                        pos[v] = vPos;

                        ushort currentBlock = GetVoxel(pos);

                        int3 neighborPos = pos;
                        neighborPos[d] += dir;
                        ushort neighborBlock = GetVoxel(neighborPos);

                        mask[n] = (currentBlock != Constants.BLOCK_AIR && neighborBlock == Constants.BLOCK_AIR)
                            ? currentBlock : (ushort)0;

                        n++;
                    }
                }

                // Greedy meshing
                n = 0;
                for (int vPos = 0; vPos < lodSize; vPos++)
                {
                    for (int uPos = 0; uPos < lodSize;)
                    {
                        ushort block = mask[n];
                        if (block != 0)
                        {
                            int w = 1;
                            while (uPos + w < lodSize && mask[n + w] == block) w++;

                            int h = 1;
                            bool done = false;
                            while (vPos + h < lodSize && !done)
                            {
                                for (int k = 0; k < w; k++)
                                {
                                    if (mask[n + k + h * lodSize] != block)
                                    {
                                        done = true;
                                        break;
                                    }
                                }
                                if (!done) h++;
                            }

                            int3 pos = int3.zero;
                            pos[d] = slice;
                            pos[u] = uPos;
                            pos[v] = vPos;

                            if (dir > 0) pos[d] += 1;

                            int3 du = int3.zero;
                            int3 dv = int3.zero;
                            du[u] = w;
                            dv[v] = h;

                            float3 worldPos = new float3(pos.x, pos.y, pos.z) * voxelSize;
                            float3 worldDu = new float3(du.x, du.y, du.z) * voxelSize;
                            float3 worldDv = new float3(dv.x, dv.y, dv.z) * voxelSize;
                            float3 worldNormal = new float3(normal.x, normal.y, normal.z);
                            float4 color = GetBlockColor(block);

                            AddQuad(worldPos, worldDu, worldDv, worldNormal, color, dir > 0, ref vCount, ref tCount);

                            for (int l = 0; l < h; l++)
                                for (int k = 0; k < w; k++)
                                    mask[n + k + l * lodSize] = 0;

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
            if (pos.x < 0 || pos.x >= lodSize ||
                pos.y < 0 || pos.y >= lodSize ||
                pos.z < 0 || pos.z >= lodSize)
            {
                return Constants.BLOCK_AIR;
            }
            return voxels[pos.x + pos.y * lodSize + pos.z * lodSize * lodSize];
        }

        private void AddQuad(float3 pos, float3 du, float3 dv, float3 normal, float4 color, bool facingPositive, ref int vCount, ref int tCount)
        {
            int startVertex = vCount;

            if (facingPositive)
            {
                vertices[vCount] = pos;
                vertices[vCount + 1] = pos + du;
                vertices[vCount + 2] = pos + du + dv;
                vertices[vCount + 3] = pos + dv;
            }
            else
            {
                vertices[vCount] = pos;
                vertices[vCount + 1] = pos + dv;
                vertices[vCount + 2] = pos + du + dv;
                vertices[vCount + 3] = pos + du;
            }

            for (int i = 0; i < 4; i++)
            {
                normals[vCount + i] = normal;
                colors[vCount + i] = color;
            }

            vCount += 4;

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
