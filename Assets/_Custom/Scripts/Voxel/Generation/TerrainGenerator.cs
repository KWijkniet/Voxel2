using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxel.Core;

namespace Voxel.Generation
{
    /// <summary>
    /// Generates terrain using Perlin noise heightmap.
    /// Uses Burst-compiled jobs for performance.
    /// </summary>
    public static class TerrainGenerator
    {
        /// <summary>
        /// Generate terrain for a chunk synchronously (for testing).
        /// </summary>
        public static void Generate(ref ChunkData chunk, int seed, TerrainSettings settings)
        {
            var job = new GenerateTerrainJob
            {
                voxels = chunk.voxels,
                chunkCoord = chunk.chunkCoord,
                seed = seed,
                baseHeight = settings.baseHeight,
                heightAmplitude = settings.heightAmplitude,
                frequency = settings.frequency
            };

            job.Run();
            chunk.isGenerated = true;
        }

        /// <summary>
        /// Schedule terrain generation job (for async processing).
        /// </summary>
        public static JobHandle ScheduleGenerate(ref ChunkData chunk, int seed, TerrainSettings settings, JobHandle dependency = default)
        {
            var job = new GenerateTerrainJob
            {
                voxels = chunk.voxels,
                chunkCoord = chunk.chunkCoord,
                seed = seed,
                baseHeight = settings.baseHeight,
                heightAmplitude = settings.heightAmplitude,
                frequency = settings.frequency
            };

            return job.Schedule(dependency);
        }
    }

    /// <summary>
    /// Terrain generation settings.
    /// </summary>
    [System.Serializable]
    public struct TerrainSettings
    {
        public float baseHeight;      // Base terrain height in voxels
        public float heightAmplitude; // Height variation
        public float frequency;       // Noise frequency (lower = smoother terrain)

        public static TerrainSettings Default => new TerrainSettings
        {
            baseHeight = 8f,
            heightAmplitude = 6f,
            frequency = 0.05f
        };
    }

    /// <summary>
    /// Burst-compiled job for terrain generation.
    /// </summary>
    [BurstCompile]
    public struct GenerateTerrainJob : IJob
    {
        public NativeArray<ushort> voxels;

        [ReadOnly] public int3 chunkCoord;
        [ReadOnly] public int seed;
        [ReadOnly] public float baseHeight;
        [ReadOnly] public float heightAmplitude;
        [ReadOnly] public float frequency;

        public void Execute()
        {
            // Calculate world position offset for this chunk
            int3 worldOffset = chunkCoord * Constants.CHUNK_SIZE;

            // Generate heightmap and fill voxels
            for (int z = 0; z < Constants.CHUNK_SIZE; z++)
            {
                for (int x = 0; x < Constants.CHUNK_SIZE; x++)
                {
                    // Calculate world XZ position
                    float worldX = (worldOffset.x + x) * frequency;
                    float worldZ = (worldOffset.z + z) * frequency;

                    // Sample Perlin noise for height
                    float noiseValue = noise.cnoise(new float2(worldX + seed, worldZ + seed));

                    // Normalize from [-1,1] to [0,1]
                    noiseValue = (noiseValue + 1f) * 0.5f;

                    // Calculate height in voxels
                    int height = (int)(baseHeight + noiseValue * heightAmplitude);

                    // Fill column
                    for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                    {
                        int worldY = worldOffset.y + y;
                        int index = ChunkData.ToIndex(x, y, z);

                        if (worldY < height - 3)
                        {
                            // Stone layer (deep underground)
                            voxels[index] = Constants.BLOCK_STONE;
                        }
                        else if (worldY < height)
                        {
                            // Dirt layer (subsurface)
                            voxels[index] = Constants.BLOCK_DIRT;
                        }
                        else if (worldY == height)
                        {
                            // Grass layer (surface) - only if above 0
                            voxels[index] = worldY >= 0 ? Constants.BLOCK_GRASS : Constants.BLOCK_DIRT;
                        }
                        else
                        {
                            // Air above surface
                            voxels[index] = Constants.BLOCK_AIR;
                        }
                    }
                }
            }
        }
    }
}
