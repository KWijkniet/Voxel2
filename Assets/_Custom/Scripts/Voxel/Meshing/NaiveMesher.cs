using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxel.Core;

namespace Voxel.Meshing
{
    /// <summary>
    /// Naive mesh generator - creates one quad per visible voxel face.
    /// No optimization, establishes correctness first.
    /// </summary>
    public static class NaiveMesher
    {
        /// <summary>
        /// Generate mesh data for a chunk synchronously.
        /// </summary>
        public static MeshData GenerateMesh(NativeArray<ushort> voxels, BlockRegistry blockRegistry)
        {
            // Estimate max sizes (worst case: checkerboard pattern)
            int maxFaces = Constants.CHUNK_VOLUME * 6;
            int maxVertices = maxFaces * 4;
            int maxTriangles = maxFaces * 6;

            var meshData = new MeshData(maxVertices, maxTriangles, Allocator.TempJob);

            var job = new GenerateMeshJob
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
        /// Schedule mesh generation job for async processing.
        /// </summary>
        public static JobHandle ScheduleGenerateMesh(
            NativeArray<ushort> voxels,
            MeshData meshData,
            JobHandle dependency = default)
        {
            var job = new GenerateMeshJob
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
    /// Container for mesh generation output data.
    /// </summary>
    public struct MeshData : System.IDisposable
    {
        public NativeArray<float3> vertices;
        public NativeArray<float3> normals;
        public NativeArray<float4> colors;
        public NativeArray<int> triangles;

        // Single-element arrays for job output (Burst workaround)
        public NativeArray<int> vertexCountArray;
        public NativeArray<int> triangleCountArray;

        public int vertexCount;
        public int triangleCount;

        public MeshData(int maxVertices, int maxTriangles, Allocator allocator)
        {
            vertices = new NativeArray<float3>(maxVertices, allocator);
            normals = new NativeArray<float3>(maxVertices, allocator);
            colors = new NativeArray<float4>(maxVertices, allocator);
            triangles = new NativeArray<int>(maxTriangles, allocator);
            vertexCountArray = new NativeArray<int>(1, allocator);
            triangleCountArray = new NativeArray<int>(1, allocator);
            vertexCount = 0;
            triangleCount = 0;
        }

        public void Dispose()
        {
            if (vertices.IsCreated) vertices.Dispose();
            if (normals.IsCreated) normals.Dispose();
            if (colors.IsCreated) colors.Dispose();
            if (triangles.IsCreated) triangles.Dispose();
            if (vertexCountArray.IsCreated) vertexCountArray.Dispose();
            if (triangleCountArray.IsCreated) triangleCountArray.Dispose();
        }
    }

    /// <summary>
    /// Burst-compiled mesh generation job.
    /// </summary>
    [BurstCompile]
    public struct GenerateMeshJob : IJob
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

            // Iterate through all voxels
            for (int z = 0; z < Constants.CHUNK_SIZE; z++)
            {
                for (int y = 0; y < Constants.CHUNK_SIZE; y++)
                {
                    for (int x = 0; x < Constants.CHUNK_SIZE; x++)
                    {
                        int index = ChunkData.ToIndex(x, y, z);
                        ushort block = voxels[index];

                        // Skip air blocks
                        if (block == Constants.BLOCK_AIR) continue;

                        // Get block color
                        float4 color = GetBlockColor(block);

                        // Check each face
                        // Right face (+X)
                        if (IsFaceVisible(x + 1, y, z))
                        {
                            AddFace(x, y, z, 0, color, ref vCount, ref tCount);
                        }

                        // Left face (-X)
                        if (IsFaceVisible(x - 1, y, z))
                        {
                            AddFace(x, y, z, 1, color, ref vCount, ref tCount);
                        }

                        // Top face (+Y)
                        if (IsFaceVisible(x, y + 1, z))
                        {
                            AddFace(x, y, z, 2, color, ref vCount, ref tCount);
                        }

                        // Bottom face (-Y)
                        if (IsFaceVisible(x, y - 1, z))
                        {
                            AddFace(x, y, z, 3, color, ref vCount, ref tCount);
                        }

                        // Front face (+Z)
                        if (IsFaceVisible(x, y, z + 1))
                        {
                            AddFace(x, y, z, 4, color, ref vCount, ref tCount);
                        }

                        // Back face (-Z)
                        if (IsFaceVisible(x, y, z - 1))
                        {
                            AddFace(x, y, z, 5, color, ref vCount, ref tCount);
                        }
                    }
                }
            }

            vertexCount[0] = vCount;
            triangleCount[0] = tCount;
        }

        private bool IsFaceVisible(int x, int y, int z)
        {
            // Face is visible if neighbor is air or out of bounds
            if (x < 0 || x >= Constants.CHUNK_SIZE ||
                y < 0 || y >= Constants.CHUNK_SIZE ||
                z < 0 || z >= Constants.CHUNK_SIZE)
            {
                return true; // Out of bounds = visible (chunk boundary)
            }

            return voxels[ChunkData.ToIndex(x, y, z)] == Constants.BLOCK_AIR;
        }

        private void AddFace(int x, int y, int z, int face, float4 color, ref int vCount, ref int tCount)
        {
            // Calculate voxel position in world units
            float px = x * Constants.VOXEL_SCALE;
            float py = y * Constants.VOXEL_SCALE;
            float pz = z * Constants.VOXEL_SCALE;
            float s = Constants.VOXEL_SCALE; // Voxel size

            int startVertex = vCount;

            // Face vertices and normal based on direction
            switch (face)
            {
                case 0: // Right (+X)
                    vertices[vCount] = new float3(px + s, py, pz);
                    vertices[vCount + 1] = new float3(px + s, py + s, pz);
                    vertices[vCount + 2] = new float3(px + s, py + s, pz + s);
                    vertices[vCount + 3] = new float3(px + s, py, pz + s);
                    SetNormals(vCount, new float3(1, 0, 0));
                    break;

                case 1: // Left (-X)
                    vertices[vCount] = new float3(px, py, pz + s);
                    vertices[vCount + 1] = new float3(px, py + s, pz + s);
                    vertices[vCount + 2] = new float3(px, py + s, pz);
                    vertices[vCount + 3] = new float3(px, py, pz);
                    SetNormals(vCount, new float3(-1, 0, 0));
                    break;

                case 2: // Top (+Y)
                    vertices[vCount] = new float3(px, py + s, pz);
                    vertices[vCount + 1] = new float3(px, py + s, pz + s);
                    vertices[vCount + 2] = new float3(px + s, py + s, pz + s);
                    vertices[vCount + 3] = new float3(px + s, py + s, pz);
                    SetNormals(vCount, new float3(0, 1, 0));
                    break;

                case 3: // Bottom (-Y)
                    vertices[vCount] = new float3(px, py, pz + s);
                    vertices[vCount + 1] = new float3(px, py, pz);
                    vertices[vCount + 2] = new float3(px + s, py, pz);
                    vertices[vCount + 3] = new float3(px + s, py, pz + s);
                    SetNormals(vCount, new float3(0, -1, 0));
                    break;

                case 4: // Front (+Z)
                    vertices[vCount] = new float3(px + s, py, pz + s);
                    vertices[vCount + 1] = new float3(px + s, py + s, pz + s);
                    vertices[vCount + 2] = new float3(px, py + s, pz + s);
                    vertices[vCount + 3] = new float3(px, py, pz + s);
                    SetNormals(vCount, new float3(0, 0, 1));
                    break;

                case 5: // Back (-Z)
                    vertices[vCount] = new float3(px, py, pz);
                    vertices[vCount + 1] = new float3(px, py + s, pz);
                    vertices[vCount + 2] = new float3(px + s, py + s, pz);
                    vertices[vCount + 3] = new float3(px + s, py, pz);
                    SetNormals(vCount, new float3(0, 0, -1));
                    break;
            }

            // Set colors for all 4 vertices
            colors[vCount] = color;
            colors[vCount + 1] = color;
            colors[vCount + 2] = color;
            colors[vCount + 3] = color;

            vCount += 4;

            // Add triangles (two triangles per face)
            // Triangle 1: 0, 1, 2
            // Triangle 2: 0, 2, 3
            triangles[tCount] = startVertex;
            triangles[tCount + 1] = startVertex + 1;
            triangles[tCount + 2] = startVertex + 2;
            triangles[tCount + 3] = startVertex;
            triangles[tCount + 4] = startVertex + 2;
            triangles[tCount + 5] = startVertex + 3;

            tCount += 6;
        }

        private void SetNormals(int startIndex, float3 normal)
        {
            normals[startIndex] = normal;
            normals[startIndex + 1] = normal;
            normals[startIndex + 2] = normal;
            normals[startIndex + 3] = normal;
        }

        private float4 GetBlockColor(ushort blockType)
        {
            // Simple color lookup (matches BlockRegistry defaults)
            switch (blockType)
            {
                case Constants.BLOCK_STONE:
                    return new float4(0.5f, 0.5f, 0.5f, 1f); // Gray
                case Constants.BLOCK_DIRT:
                    return new float4(0.55f, 0.35f, 0.2f, 1f); // Brown
                case Constants.BLOCK_GRASS:
                    return new float4(0.3f, 0.7f, 0.2f, 1f); // Green
                default:
                    return new float4(1f, 0f, 1f, 1f); // Magenta for unknown
            }
        }
    }
}
