using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Voxel.Core;
using Voxel.Generation;
using Voxel.Meshing;

namespace Voxel.World
{
    /// <summary>
    /// Manages chunk lifecycle: loading, generation, meshing, and unloading.
    /// Uses priority queues and batch processing for smooth performance.
    /// </summary>
    public class ChunkManager : System.IDisposable
    {
        // All loaded chunks
        private Dictionary<int3, ManagedChunk> chunks = new Dictionary<int3, ManagedChunk>();

        // Processing queues
        private List<int3> generateQueue = new List<int3>();
        private List<int3> meshQueue = new List<int3>();
        private List<int3> unloadQueue = new List<int3>();

        // Active jobs
        private List<ChunkJob> activeJobs = new List<ChunkJob>();

        // Settings
        private readonly int seed;
        private readonly TerrainSettings terrainSettings;
        private readonly Material material;
        private readonly Transform parent;
        private readonly BlockRegistry blockRegistry;

        // Batch processing limits
        public int MaxGeneratePerFrame { get; set; } = 4;
        public int MaxMeshPerFrame { get; set; } = 8;
        public int MaxUnloadPerFrame { get; set; } = 16;
        public int MaxActiveJobs { get; set; } = 8;

        // Distances (in chunks, squared for faster comparison)
        public int RenderDistanceSq { get; set; } = 64; // 8 chunks
        public int UnloadDistanceSq { get; set; } = 100; // 10 chunks

        // Stats
        public int LoadedChunkCount => chunks.Count;
        public int QueuedGenerateCount => generateQueue.Count;
        public int QueuedMeshCount => meshQueue.Count;
        public int ActiveJobCount => activeJobs.Count;

        /// <summary>
        /// Wrapper for chunk with additional state tracking.
        /// </summary>
        private class ManagedChunk
        {
            public Chunk Chunk;
            public bool IsGenerating;
            public bool IsMeshing;
            public JobHandle GenerateHandle;
            public JobHandle MeshHandle;
            public MeshData MeshData;
        }

        /// <summary>
        /// Tracks an active job.
        /// </summary>
        private struct ChunkJob
        {
            public int3 Coord;
            public JobHandle Handle;
            public bool IsGenerate; // true = generate, false = mesh
            public MeshData MeshData;
        }

        public ChunkManager(int seed, TerrainSettings terrainSettings, Material material, BlockRegistry blockRegistry, Transform parent = null)
        {
            this.seed = seed;
            this.terrainSettings = terrainSettings;
            this.material = material;
            this.blockRegistry = blockRegistry;
            this.parent = parent;
        }

        /// <summary>
        /// Update chunk loading/unloading based on player position.
        /// Call this every frame.
        /// </summary>
        public void Update(float3 playerPosition)
        {
            int3 playerChunk = ChunkCoord.WorldToChunk(playerPosition);

            // Complete finished jobs
            ProcessCompletedJobs();

            // Queue chunks that need loading
            QueueChunksAroundPlayer(playerChunk);

            // Queue chunks that need unloading
            QueueChunksForUnload(playerChunk);

            // Process queues
            ProcessGenerateQueue(playerChunk);
            ProcessMeshQueue(playerChunk);
            ProcessUnloadQueue();
        }

        /// <summary>
        /// Force immediate generation of a chunk (synchronous).
        /// </summary>
        public Chunk GetOrCreateChunkImmediate(int3 coord)
        {
            if (chunks.TryGetValue(coord, out var managed))
            {
                // Complete any pending jobs
                if (managed.IsGenerating)
                {
                    managed.GenerateHandle.Complete();
                    managed.IsGenerating = false;
                }
                if (managed.IsMeshing)
                {
                    managed.MeshHandle.Complete();
                    FinishMeshing(managed);
                }
                return managed.Chunk;
            }

            // Create new chunk synchronously
            var chunk = new Chunk(coord);
            chunk.Generate(seed, terrainSettings);
            chunk.GenerateMesh(blockRegistry, useGreedyMeshing: true);
            chunk.CreateGameObject(material, parent);

            managed = new ManagedChunk { Chunk = chunk };
            chunks[coord] = managed;

            return chunk;
        }

        /// <summary>
        /// Get a chunk if it exists and is fully loaded.
        /// </summary>
        public Chunk GetChunk(int3 coord)
        {
            if (chunks.TryGetValue(coord, out var managed) && !managed.IsGenerating && !managed.IsMeshing)
            {
                return managed.Chunk;
            }
            return null;
        }

        /// <summary>
        /// Check if a chunk exists (may still be loading).
        /// </summary>
        public bool HasChunk(int3 coord)
        {
            return chunks.ContainsKey(coord);
        }

        private void QueueChunksAroundPlayer(int3 playerChunk)
        {
            int range = (int)math.sqrt(RenderDistanceSq) + 1;

            for (int y = -range; y <= range; y++)
            {
                for (int z = -range; z <= range; z++)
                {
                    for (int x = -range; x <= range; x++)
                    {
                        int3 coord = playerChunk + new int3(x, y, z);
                        int distSq = ChunkCoord.DistanceSquared(coord, playerChunk);

                        if (distSq <= RenderDistanceSq && !chunks.ContainsKey(coord) && !generateQueue.Contains(coord))
                        {
                            generateQueue.Add(coord);
                        }
                    }
                }
            }

            // Sort by distance (closest first)
            generateQueue.Sort((a, b) =>
                ChunkCoord.DistanceSquared(a, playerChunk).CompareTo(ChunkCoord.DistanceSquared(b, playerChunk)));
        }

        private void QueueChunksForUnload(int3 playerChunk)
        {
            foreach (var kvp in chunks)
            {
                int distSq = ChunkCoord.DistanceSquared(kvp.Key, playerChunk);
                if (distSq > UnloadDistanceSq && !unloadQueue.Contains(kvp.Key))
                {
                    unloadQueue.Add(kvp.Key);
                }
            }
        }

        private void ProcessGenerateQueue(int3 playerChunk)
        {
            int processed = 0;
            while (processed < MaxGeneratePerFrame && generateQueue.Count > 0 && activeJobs.Count < MaxActiveJobs)
            {
                int3 coord = generateQueue[0];
                generateQueue.RemoveAt(0);

                if (chunks.ContainsKey(coord)) continue;

                // Create chunk and start async generation
                var chunk = new Chunk(coord);
                var managed = new ManagedChunk
                {
                    Chunk = chunk,
                    IsGenerating = true
                };

                // Schedule generation job
                var job = new GenerateTerrainJob
                {
                    voxels = chunk.Voxels,
                    chunkCoord = coord,
                    seed = seed,
                    baseHeight = terrainSettings.baseHeight,
                    heightAmplitude = terrainSettings.heightAmplitude,
                    frequency = terrainSettings.frequency
                };

                managed.GenerateHandle = job.Schedule();
                chunks[coord] = managed;

                activeJobs.Add(new ChunkJob
                {
                    Coord = coord,
                    Handle = managed.GenerateHandle,
                    IsGenerate = true
                });

                processed++;
            }
        }

        private void ProcessMeshQueue(int3 playerChunk)
        {
            // Sort mesh queue by distance
            meshQueue.Sort((a, b) =>
                ChunkCoord.DistanceSquared(a, playerChunk).CompareTo(ChunkCoord.DistanceSquared(b, playerChunk)));

            int processed = 0;
            while (processed < MaxMeshPerFrame && meshQueue.Count > 0 && activeJobs.Count < MaxActiveJobs)
            {
                int3 coord = meshQueue[0];
                meshQueue.RemoveAt(0);

                if (!chunks.TryGetValue(coord, out var managed)) continue;
                if (managed.IsMeshing) continue;

                // Allocate mesh data
                int maxFaces = Constants.CHUNK_VOLUME * 6;
                managed.MeshData = new MeshData(maxFaces * 4, maxFaces * 6, Allocator.TempJob);

                // Schedule mesh job
                managed.MeshHandle = GreedyMesher.ScheduleGenerateMesh(
                    managed.Chunk.Voxels,
                    managed.MeshData
                );
                managed.IsMeshing = true;

                activeJobs.Add(new ChunkJob
                {
                    Coord = coord,
                    Handle = managed.MeshHandle,
                    IsGenerate = false,
                    MeshData = managed.MeshData
                });

                processed++;
            }
        }

        private void ProcessUnloadQueue()
        {
            int processed = 0;
            while (processed < MaxUnloadPerFrame && unloadQueue.Count > 0)
            {
                int3 coord = unloadQueue[0];
                unloadQueue.RemoveAt(0);

                if (chunks.TryGetValue(coord, out var managed))
                {
                    // Complete any pending jobs
                    if (managed.IsGenerating)
                    {
                        managed.GenerateHandle.Complete();
                    }
                    if (managed.IsMeshing)
                    {
                        managed.MeshHandle.Complete();
                        managed.MeshData.Dispose();
                    }

                    managed.Chunk.Dispose();
                    chunks.Remove(coord);
                }

                processed++;
            }
        }

        private void ProcessCompletedJobs()
        {
            for (int i = activeJobs.Count - 1; i >= 0; i--)
            {
                var job = activeJobs[i];

                if (!job.Handle.IsCompleted) continue;

                job.Handle.Complete();

                if (!chunks.TryGetValue(job.Coord, out var managed))
                {
                    // Chunk was unloaded while job was running
                    if (!job.IsGenerate && job.MeshData.vertices.IsCreated)
                    {
                        job.MeshData.Dispose();
                    }
                    activeJobs.RemoveAt(i);
                    continue;
                }

                if (job.IsGenerate)
                {
                    // Generation complete, queue for meshing
                    managed.IsGenerating = false;
                    meshQueue.Add(job.Coord);
                }
                else
                {
                    // Meshing complete, apply to chunk
                    FinishMeshing(managed);
                }

                activeJobs.RemoveAt(i);
            }
        }

        private void FinishMeshing(ManagedChunk managed)
        {
            managed.IsMeshing = false;

            var meshData = managed.MeshData;
            int vertexCount = meshData.vertexCountArray[0];
            int triangleCount = meshData.triangleCountArray[0];

            if (vertexCount > 0)
            {
                // Create Unity mesh
                var mesh = new Mesh();
                mesh.name = $"Chunk_{managed.Chunk.Coord.x}_{managed.Chunk.Coord.y}_{managed.Chunk.Coord.z}";

                var vertices = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var colors = new Color[vertexCount];
                var triangles = new int[triangleCount];

                for (int i = 0; i < vertexCount; i++)
                {
                    vertices[i] = meshData.vertices[i];
                    normals[i] = meshData.normals[i];
                    colors[i] = new Color(
                        meshData.colors[i].x,
                        meshData.colors[i].y,
                        meshData.colors[i].z,
                        meshData.colors[i].w
                    );
                }

                for (int i = 0; i < triangleCount; i++)
                {
                    triangles[i] = meshData.triangles[i];
                }

                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.colors = colors;
                mesh.triangles = triangles;
                mesh.RecalculateBounds();

                // Apply to chunk GameObject
                managed.Chunk.CreateGameObject(material, parent);
                managed.Chunk.SetMesh(mesh);
            }
            else
            {
                // Empty chunk (all air)
                managed.Chunk.CreateGameObject(material, parent);
            }

            meshData.Dispose();
            managed.MeshData = default;
        }

        public void Dispose()
        {
            // Complete all active jobs
            foreach (var job in activeJobs)
            {
                job.Handle.Complete();
                if (!job.IsGenerate && job.MeshData.vertices.IsCreated)
                {
                    job.MeshData.Dispose();
                }
            }
            activeJobs.Clear();

            // Dispose all chunks
            foreach (var managed in chunks.Values)
            {
                if (managed.MeshData.vertices.IsCreated)
                {
                    managed.MeshData.Dispose();
                }
                managed.Chunk.Dispose();
            }
            chunks.Clear();

            generateQueue.Clear();
            meshQueue.Clear();
            unloadQueue.Clear();
        }
    }
}
