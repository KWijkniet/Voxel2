using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Voxel.Core;
using Voxel.Generation;

namespace Voxel.World
{
    /// <summary>
    /// Main entry point for the voxel world system.
    /// Manages chunk lifecycle and coordinates all subsystems.
    /// </summary>
    public class VoxelWorld : MonoBehaviour
    {
        [Header("World Settings")]
        [SerializeField] private int seed = 12345;
        [SerializeField] private TerrainSettings terrainSettings = TerrainSettings.Default;

        [Header("Rendering")]
        [SerializeField] private Material voxelMaterial;

        [Header("Test Settings")]
        [SerializeField] private int testWorldSize = 2; // 2x2x2 chunks = 32x32x32 voxels
        [SerializeField] private bool generateOnStart = true;

        [Header("Debug")]
        [SerializeField] private bool showChunkBounds = false;

        private Dictionary<int3, Chunk> chunks = new Dictionary<int3, Chunk>();
        private BlockRegistry blockRegistry;
        private Transform chunksParent;

        private void Awake()
        {
            // Create default block registry
            blockRegistry = BlockRegistry.CreateDefault();

            // Create parent object for organization
            chunksParent = new GameObject("Chunks").transform;
            chunksParent.parent = transform;
        }

        private void Start()
        {
            if (generateOnStart)
            {
                GenerateTestWorld();
            }
        }

        private void OnDestroy()
        {
            // Clean up all chunks
            foreach (var chunk in chunks.Values)
            {
                chunk.Dispose();
            }
            chunks.Clear();
        }

        /// <summary>
        /// Generate a small test world (32x32x32 voxels by default).
        /// </summary>
        [ContextMenu("Generate Test World")]
        public void GenerateTestWorld()
        {
            // Clear existing chunks
            foreach (var chunk in chunks.Values)
            {
                chunk.Dispose();
            }
            chunks.Clear();

            // Ensure we have a material
            if (voxelMaterial == null)
            {
                voxelMaterial = CreateDefaultMaterial();
            }

            // Generate chunks
            int halfSize = testWorldSize / 2;
            for (int x = -halfSize; x < halfSize + (testWorldSize % 2); x++)
            {
                for (int y = 0; y < testWorldSize; y++) // Start from y=0 for ground level
                {
                    for (int z = -halfSize; z < halfSize + (testWorldSize % 2); z++)
                    {
                        int3 coord = new int3(x, y, z);
                        CreateChunk(coord);
                    }
                }
            }

            Debug.Log($"Generated {chunks.Count} chunks ({testWorldSize * Constants.CHUNK_SIZE}^3 voxels)");
        }

        /// <summary>
        /// Create and initialize a chunk at the given coordinate.
        /// </summary>
        public Chunk CreateChunk(int3 coord)
        {
            if (chunks.ContainsKey(coord))
            {
                Debug.LogWarning($"Chunk at {coord} already exists");
                return chunks[coord];
            }

            var chunk = new Chunk(coord);

            // Generate terrain
            chunk.Generate(seed, terrainSettings);

            // Generate mesh
            chunk.GenerateMesh(blockRegistry);

            // Create visual representation
            chunk.CreateGameObject(voxelMaterial, chunksParent);

            chunks[coord] = chunk;
            return chunk;
        }

        /// <summary>
        /// Get chunk at coordinate, or null if not loaded.
        /// </summary>
        public Chunk GetChunk(int3 coord)
        {
            chunks.TryGetValue(coord, out var chunk);
            return chunk;
        }

        /// <summary>
        /// Get voxel at world position.
        /// </summary>
        public ushort GetVoxel(float3 worldPos)
        {
            int3 chunkCoord = ChunkCoord.WorldToChunk(worldPos);
            var chunk = GetChunk(chunkCoord);
            if (chunk == null) return Constants.BLOCK_AIR;

            int3 localPos = ChunkCoord.VoxelToLocal(ChunkCoord.WorldToVoxel(worldPos));
            return chunk.GetVoxel(localPos);
        }

        /// <summary>
        /// Set voxel at world position.
        /// </summary>
        public void SetVoxel(float3 worldPos, ushort blockType)
        {
            int3 chunkCoord = ChunkCoord.WorldToChunk(worldPos);
            var chunk = GetChunk(chunkCoord);
            if (chunk == null) return;

            int3 localPos = ChunkCoord.VoxelToLocal(ChunkCoord.WorldToVoxel(worldPos));
            chunk.SetVoxel(localPos, blockType);

            // Regenerate mesh
            chunk.GenerateMesh(blockRegistry);
            chunk.UpdateMesh();
        }

        /// <summary>
        /// Create a default vertex color material for testing.
        /// </summary>
        private Material CreateDefaultMaterial()
        {
            // Try to find our custom vertex color shader
            var shader = Shader.Find("Voxel/VertexColor");
            if (shader == null)
            {
                // Fallback to URP Lit
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                // Final fallback to standard shader
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogError("Could not find suitable shader for voxel material");
                return null;
            }

            var material = new Material(shader);
            material.name = "Voxel Material (Generated)";

            return material;
        }

        private void OnDrawGizmos()
        {
            if (!showChunkBounds || chunks == null) return;

            Gizmos.color = Color.yellow;
            foreach (var chunk in chunks.Values)
            {
                float3 pos = ChunkCoord.ChunkToWorld(chunk.Coord);
                Vector3 center = new Vector3(pos.x, pos.y, pos.z) + Vector3.one * Constants.CHUNK_WORLD_SIZE * 0.5f;
                Vector3 size = Vector3.one * Constants.CHUNK_WORLD_SIZE;
                Gizmos.DrawWireCube(center, size);
            }
        }
    }
}
