using Custom.Voxels.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Custom.Voxels
{
    internal class ChunkManager
    {
        private Hashtable chunks = new Hashtable();
        
        private Queue<Chunk> calculateQueue = new();
        private Queue<(Chunk chunk, int lod)> generateQueue = new();
        private List<Chunk> renderQueue = new();

        private const int BATCH_SIZE = 8; // Tune this per frame

        public void Clear()
        {
            chunks.Clear();
        }

        public int Count()
        {
            return chunks.Count;
        }

        public Chunk[] GetAll()
        {
            return chunks.Values.Cast<Chunk>().ToArray();
        }

        public Chunk GetChunk(int x, int y, int z)
        {
            int3 key = new int3(x, y, z);
            return GetChunk(key);
        }

        public Chunk GetChunk(int3 key)
        {
            if (chunks.ContainsKey(key))
            {
                return (Chunk)chunks[key];
            }

            return null;
        }

        public void SetChunk(int x, int y, int z, Chunk chunk)
        {
            int3 key = new int3(x, y, z);
            SetChunk(key, chunk);
        }

        public void SetChunk(int3 key, Chunk chunk)
        {
            if (!chunks.ContainsKey(key))
            {
                chunks[key] = chunk;
            }
        }

        public void UpdateBatched()
        {
            // 1. Calculate phase
            int count = 0;
            while (calculateQueue.Count > 0 && count < BATCH_SIZE)
            {
                var chunk = calculateQueue.Dequeue();
                if (chunk.hasCalculated) continue;
                
                chunk.Calculate();
                count++;
                
                if (!chunk.hasCalculated) calculateQueue.Enqueue(chunk);
            }

            // 2. Generate phase
            count = 0;
            while (generateQueue.Count > 0 && count < BATCH_SIZE)
            {
                var (chunk, lod) = generateQueue.Dequeue();
                if (chunk.hasGenerated && chunk.GetLOD() == lod) continue;

                if (chunk.hasCalculated)
                {
                    chunk.Generate(lod);
                }

                if (!chunk.hasGenerated) generateQueue.Enqueue((chunk, lod));
                count++;
            }

            // 3. Render phase
            foreach(Chunk chunk in renderQueue)
            {
                if (chunk.hasGenerated) chunk.Render();
            }
        }

        public void EnqueueChunksForUpdate(int3 pos)
        {
            int3 centerChunkPos = pos / WorldSettings.SIZE;
            int renderDistSq = WorldSettings.renderDistance * WorldSettings.renderDistance;

            Chunk[] chunks = GetChunksAround(pos, WorldSettings.preloadDistance);
            renderQueue.Clear();
            calculateQueue.Clear();
            generateQueue.Clear();

            // Sort chunks by distance to player position (chunk-space)
            var sortedChunks = chunks
                .OrderBy(c => MathematicsHelper.GetDistanceSquared(centerChunkPos, c.pos / WorldSettings.SIZE))
                .ToArray();

            foreach (var chunk in sortedChunks)
            {
                calculateQueue.Enqueue(chunk);

                int3 chunkPos = chunk.pos / WorldSettings.SIZE;
                int distSq = MathematicsHelper.GetDistanceSquared(centerChunkPos, chunkPos);
                if (distSq <= renderDistSq)
                {
                    int lod = 8;
                    foreach (var item in WorldSettings.lodRange)
                    {
                        if (distSq <= item.Item1)
                        {
                            lod = item.Item2;
                            break;
                        }
                    }

                    generateQueue.Enqueue((chunk, lod));
                    renderQueue.Add(chunk);
                }
            }
        }

        public Chunk[] GetChunksAround(int3 pos, int range)
        {
            List<Chunk> nearbyChunks = new List<Chunk>();

            int3 centerChunkPos = pos / WorldSettings.SIZE;
            int rangeSq = range * range;

            for (int x = -range; x <= range; x++)
            {
                for (int y = -range; y <= range; y++)
                {
                    for (int z = -range; z <= range; z++)
                    {
                        int3 offset = new int3(x, y, z);
                        int3 candidateChunkPos = centerChunkPos + offset;

                        // Check spherical distance
                        if (MathematicsHelper.GetDistanceSquared(centerChunkPos, candidateChunkPos) <= rangeSq)
                        {
                            int3 worldPos = candidateChunkPos * WorldSettings.SIZE;
                            Chunk chunk = GetChunk(worldPos);
                            if (chunk != null)
                            {
                                nearbyChunks.Add(chunk);
                            }
                        }
                    }
                }
            }

            return nearbyChunks.ToArray();
        }
    }
}
