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
    /// Represents a single chunk in the world.
    /// Manages voxel data, mesh generation, and rendering.
    /// </summary>
    public class Chunk : System.IDisposable
    {
        public int3 Coord { get; private set; }
        public ChunkState State { get; private set; }

        // Store voxels directly to avoid struct copy issues with properties
        private NativeArray<ushort> voxels;
        public NativeArray<ushort> Voxels => voxels;

        private GameObject gameObject;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh mesh;

        public enum ChunkState
        {
            Empty,
            Generated,
            Meshed,
            Ready
        }

        public Chunk(int3 coord)
        {
            Coord = coord;
            voxels = new NativeArray<ushort>(Constants.CHUNK_VOLUME, Allocator.Persistent);
            State = ChunkState.Empty;
        }

        /// <summary>
        /// Generate terrain for this chunk.
        /// </summary>
        public void Generate(int seed, TerrainSettings settings)
        {
            var job = new GenerateTerrainJob
            {
                voxels = voxels,
                chunkCoord = Coord,
                seed = seed,
                baseHeight = settings.baseHeight,
                heightAmplitude = settings.heightAmplitude,
                frequency = settings.frequency
            };
            job.Run();
            State = ChunkState.Generated;
        }

        /// <summary>
        /// Generate mesh from voxel data.
        /// </summary>
        /// <param name="useGreedyMeshing">Use greedy meshing for better performance (default true)</param>
        public void GenerateMesh(BlockRegistry blockRegistry, bool useGreedyMeshing = true)
        {
            if (State < ChunkState.Generated) return;

            using (var meshData = useGreedyMeshing
                ? GreedyMesher.GenerateMesh(voxels)
                : NaiveMesher.GenerateMesh(voxels, blockRegistry))
            {
                if (meshData.vertexCount == 0)
                {
                    // No visible faces (all air or all solid with no exposed faces)
                    State = ChunkState.Meshed;
                    return;
                }

                // Create or update mesh
                if (mesh == null)
                {
                    mesh = new Mesh();
                    mesh.name = $"Chunk_{Coord.x}_{Coord.y}_{Coord.z}";
                }
                else
                {
                    mesh.Clear();
                }

                // Copy data to mesh
                var vertices = new Vector3[meshData.vertexCount];
                var normals = new Vector3[meshData.vertexCount];
                var colors = new Color[meshData.vertexCount];
                var triangles = new int[meshData.triangleCount];

                for (int i = 0; i < meshData.vertexCount; i++)
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

                for (int i = 0; i < meshData.triangleCount; i++)
                {
                    triangles[i] = meshData.triangles[i];
                }

                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.colors = colors;
                mesh.triangles = triangles;

                mesh.RecalculateBounds();
            }

            State = ChunkState.Meshed;
        }

        /// <summary>
        /// Create GameObject and setup rendering components.
        /// </summary>
        public void CreateGameObject(Material material, Transform parent = null)
        {
            if (gameObject != null) return;

            gameObject = new GameObject($"Chunk_{Coord.x}_{Coord.y}_{Coord.z}");
            gameObject.transform.parent = parent;
            gameObject.transform.position = ChunkCoord.ChunkToWorld(Coord);

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();

            meshRenderer.material = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            meshRenderer.receiveShadows = true;

            if (mesh != null)
            {
                meshFilter.mesh = mesh;
            }

            State = ChunkState.Ready;
        }

        /// <summary>
        /// Update the mesh on the GameObject.
        /// </summary>
        public void UpdateMesh()
        {
            if (meshFilter != null && mesh != null)
            {
                meshFilter.mesh = mesh;
            }
        }

        /// <summary>
        /// Set mesh from external source (used by ChunkManager for async meshing).
        /// </summary>
        public void SetMesh(Mesh newMesh)
        {
            if (mesh != null && mesh != newMesh)
            {
                if (Application.isPlaying)
                    Object.Destroy(mesh);
                else
                    Object.DestroyImmediate(mesh);
            }

            mesh = newMesh;
            State = ChunkState.Meshed;

            if (meshFilter != null)
            {
                meshFilter.mesh = mesh;
                State = ChunkState.Ready;
            }
        }

        /// <summary>
        /// Set chunk visibility.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (gameObject != null)
            {
                gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// Get voxel at local position.
        /// </summary>
        public ushort GetVoxel(int3 localPos)
        {
            if (!ChunkData.IsInBounds(localPos)) return Constants.BLOCK_AIR;
            return voxels[ChunkData.ToIndex(localPos)];
        }

        /// <summary>
        /// Set voxel at local position.
        /// </summary>
        public void SetVoxel(int3 localPos, ushort blockType)
        {
            if (!ChunkData.IsInBounds(localPos)) return;
            voxels[ChunkData.ToIndex(localPos)] = blockType;
        }

        public void Dispose()
        {
            if (voxels.IsCreated)
            {
                voxels.Dispose();
            }

            if (mesh != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(mesh);
                else
                    Object.DestroyImmediate(mesh);
            }

            if (gameObject != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(gameObject);
                else
                    Object.DestroyImmediate(gameObject);
            }
        }
    }
}
