using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Voxel.Core;
using Voxel.Generation;
using Voxel.Rendering;

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
        [SerializeField] private Camera mainCamera;

        [Header("Streaming")]
        [SerializeField] private bool useStreaming = false;
        [SerializeField] private Transform player;
        [SerializeField] private int renderDistance = 8;
        [SerializeField] private int unloadDistance = 10;

        [Header("Performance")]
        [SerializeField] private int maxGeneratePerFrame = 4;
        [SerializeField] private int maxMeshPerFrame = 8;
        [SerializeField] private bool useGreedyMeshing = true;
        [SerializeField] private bool useFrustumCulling = true;

        [Header("Test Settings")]
        [SerializeField] private int testWorldSize = 2; // 2x2x2 chunks = 32x32x32 voxels
        [SerializeField] private bool generateOnStart = true;

        [Header("Debug")]
        [SerializeField] private bool showChunkBounds = false;
        [SerializeField] private bool showStats = true;

        // Simple mode (non-streaming)
        private Dictionary<int3, Chunk> chunks = new Dictionary<int3, Chunk>();
        private BlockRegistry blockRegistry;
        private Transform chunksParent;

        // Streaming mode
        private ChunkManager chunkManager;
        private FrustumCuller frustumCuller;

        // Stats
        private int lastVertexCount;
        private int lastTriangleCount;
        private float lastUpdateTime;

        private void Awake()
        {
            // Create default block registry
            blockRegistry = BlockRegistry.CreateDefault();

            // Create parent object for organization
            chunksParent = new GameObject("Chunks").transform;
            chunksParent.parent = transform;

            // Setup camera
            if (mainCamera == null)
            {
                mainCamera = Camera.main;
            }

            // Setup frustum culler
            if (mainCamera != null)
            {
                frustumCuller = new FrustumCuller(mainCamera);
            }
        }

        private void Start()
        {
            // Ensure we have a material
            if (voxelMaterial == null)
            {
                voxelMaterial = CreateDefaultMaterial();
            }

            if (useStreaming)
            {
                // Initialize chunk manager for streaming mode
                chunkManager = new ChunkManager(seed, terrainSettings, voxelMaterial, blockRegistry, chunksParent);
                chunkManager.RenderDistanceSq = renderDistance * renderDistance;
                chunkManager.UnloadDistanceSq = unloadDistance * unloadDistance;
                chunkManager.MaxGeneratePerFrame = maxGeneratePerFrame;
                chunkManager.MaxMeshPerFrame = maxMeshPerFrame;
            }
            else if (generateOnStart)
            {
                GenerateTestWorld();
            }
        }

        private void Update()
        {
            float startTime = Time.realtimeSinceStartup;

            if (useStreaming && chunkManager != null)
            {
                // Get player position
                float3 playerPos = player != null
                    ? (float3)player.position
                    : mainCamera != null
                        ? (float3)mainCamera.transform.position
                        : float3.zero;

                // Update chunk manager
                chunkManager.Update(playerPos);
            }

            // Update frustum culling
            if (useFrustumCulling && frustumCuller != null)
            {
                frustumCuller.UpdateFrustum();
                UpdateChunkVisibility();
            }

            lastUpdateTime = (Time.realtimeSinceStartup - startTime) * 1000f;
        }

        private void OnDestroy()
        {
            // Clean up streaming mode
            chunkManager?.Dispose();

            // Clean up simple mode
            foreach (var chunk in chunks.Values)
            {
                chunk.Dispose();
            }
            chunks.Clear();
        }

        private void UpdateChunkVisibility()
        {
            if (useStreaming)
            {
                // ChunkManager handles its own visibility
                return;
            }

            foreach (var chunk in chunks.Values)
            {
                bool visible = frustumCuller.IsChunkVisible(chunk.Coord);
                chunk.SetVisible(visible);
            }
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

            lastVertexCount = 0;
            lastTriangleCount = 0;

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

            Debug.Log($"Generated {chunks.Count} chunks ({testWorldSize * Constants.CHUNK_SIZE}^3 voxels) - Vertices: {lastVertexCount}, Triangles: {lastTriangleCount / 3}");
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
            chunk.GenerateMesh(blockRegistry, useGreedyMeshing);

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
            if (useStreaming && chunkManager != null)
            {
                return chunkManager.GetChunk(coord);
            }
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
            chunk.GenerateMesh(blockRegistry, useGreedyMeshing);
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

        private void OnGUI()
        {
            if (!showStats) return;

            int chunkCount = useStreaming && chunkManager != null
                ? chunkManager.LoadedChunkCount
                : chunks.Count;

            int queuedGenerate = useStreaming && chunkManager != null
                ? chunkManager.QueuedGenerateCount
                : 0;

            int queuedMesh = useStreaming && chunkManager != null
                ? chunkManager.QueuedMeshCount
                : 0;

            GUILayout.BeginArea(new Rect(10, 10, 300, 150));
            GUILayout.Label($"Chunks: {chunkCount}");
            GUILayout.Label($"Generate Queue: {queuedGenerate}");
            GUILayout.Label($"Mesh Queue: {queuedMesh}");
            GUILayout.Label($"Update Time: {lastUpdateTime:F2}ms");
            GUILayout.Label($"Greedy Meshing: {(useGreedyMeshing ? "ON" : "OFF")}");
            GUILayout.Label($"Frustum Culling: {(useFrustumCulling ? "ON" : "OFF")}");
            GUILayout.EndArea();
        }

        private void OnDrawGizmos()
        {
            if (!showChunkBounds) return;

            Gizmos.color = Color.yellow;

            if (useStreaming && chunkManager != null)
            {
                // Draw bounds for streaming chunks
                // Note: ChunkManager doesn't expose chunk iteration, so we skip this for now
            }
            else if (chunks != null)
            {
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
}
