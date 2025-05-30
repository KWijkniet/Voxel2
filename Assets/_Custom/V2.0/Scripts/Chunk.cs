using Unity.Collections;
using UnityEngine;
using Custom.Voxels.Jobs;
using Unity.Jobs;
using Unity.Mathematics;
using Custom.Voxels.Helpers;
using System.Collections.Generic;
using static UnityEditor.Progress;

namespace Custom.Voxels
{
    public class Chunk
    {
        public int3 pos;
        public bool hasCalculated = false;
        public bool hasGenerated = false;

        private bool jobRunning = false;
        private JobHandle jobHandle;

        private NativeArray<int> heightmap;
        private NativeArray<byte> voxels;
        private NativeList<float3> vertices;
        private NativeList<int> triangles;
        private NativeList<float2> uvs;

        private Matrix4x4 matrix;
        private Mesh mesh;
        private byte generationMode;
        private Bounds bounds;
        private Dictionary<int3, Chunk> neighbours;

        public Chunk(int3 pos, byte generationMode)
        {
            this.pos = pos;
            matrix = Matrix4x4.TRS(MathematicsHelper.Int3ToVector3(pos), Quaternion.identity, Vector3.one);
            this.generationMode = generationMode;
            this.bounds = new Bounds(new Vector3(pos.x + WorldSettings.SIZE.x / 2, pos.y + WorldSettings.SIZE.y / 2, pos.z + WorldSettings.SIZE.z / 2), MathematicsHelper.Float3ToVector3(WorldSettings.SIZE));

            neighbours = new Dictionary<int3, Chunk>(6);
        }

        public void LoadNeighbours()
        {
            foreach (var offset in WorldSettings.neighbourPositions)
            {
                Chunk neighbor = WorldSettings.chunks.GetChunk(pos + offset);
                if (neighbor != null)
                    neighbours.Add(pos + offset, neighbor);
            }
        }

        public byte GetVoxel(int x, int y, int z)
        {
            int index = MathematicsHelper.XYZToIndex(x, y, z, WorldSettings.SIZE);
            
            if (index >= 0 && index < voxels.Length) return voxels[index];
            return 0;
        }

        public NativeArray<byte> GetVoxels()
        {
            return voxels;
        }

        public void Dispose()
        {
            if (heightmap.IsCreated) heightmap.Dispose();
            if (voxels.IsCreated) voxels.Dispose();
            if (vertices.IsCreated) vertices.Dispose();
            if (triangles.IsCreated) triangles.Dispose();
            if (uvs.IsCreated) uvs.Dispose();

            if (mesh) mesh.Clear();
        }

        public void Update()
        {
            Calculate();
            Generate();
            Render();
        }

        private void Calculate()
        {
            if (hasCalculated && !heightmap.IsCreated) return;

            if (!jobRunning)
            {
                heightmap = new NativeArray<int>(WorldSettings.SIZE.x * WorldSettings.SIZE.z, Allocator.TempJob);
                int index = 0;
                for (int x = 0; x < WorldSettings.SIZE.x; x++)
                {
                    for (int z = 0; z < WorldSettings.SIZE.z; z++)
                    {
                        heightmap[index] = (int)math.round(noise.cnoise(new float2(x * 10000, z * 10000)) * WorldSettings.SIZE.y);
                        index++;
                    }
                }
                voxels = new(WorldSettings.SIZE.x * WorldSettings.SIZE.y * WorldSettings.SIZE.z, Allocator.Persistent);

                CalculateChunkJob job = new CalculateChunkJob
                {
                    size = WorldSettings.SIZE,
                    heightmap = heightmap,
                    voxels = voxels
                };

                jobHandle = job.Schedule();
                jobRunning = true;
            }
            else if (jobHandle.IsCompleted)
            {
                jobHandle.Complete();
                hasCalculated = true;
                jobRunning = false;

                heightmap.Dispose();
            }
        }

        private void Generate()
        {
            if (hasGenerated || !hasCalculated || !NeighboursReady()) return;

            if (!jobRunning)
            {
                vertices = new NativeList<float3>(Allocator.TempJob);
                triangles = new NativeList<int>(Allocator.TempJob);
                uvs = new NativeList<float2>(Allocator.TempJob);
                int3[] neighborOffsets = WorldSettings.neighbourPositions;

                GenerateChunkJob job = new GenerateChunkJob
                {
                    generationMode = generationMode,
                    size = WorldSettings.SIZE,
                    voxels = voxels,
                    vertices = vertices,
                    triangles = triangles,
                    uvs = uvs,
                    neighbours = new Neighbours
                    {
                        xPos = neighbours.TryGetValue(pos + neighborOffsets[0], out var nxp)
                            ? nxp.GetVoxels()
                            : WorldSettings.emptyVoxels,

                        xNeg = neighbours.TryGetValue(pos + neighborOffsets[1], out var nxn)
                            ? nxn.GetVoxels()
                            : WorldSettings.emptyVoxels,

                        yPos = neighbours.TryGetValue(pos + neighborOffsets[2], out var nyp)
                            ? nyp.GetVoxels()
                            : WorldSettings.emptyVoxels,

                        yNeg = neighbours.TryGetValue(pos + neighborOffsets[3], out var nyn)
                            ? nyn.GetVoxels()
                            : WorldSettings.emptyVoxels,

                        zPos = neighbours.TryGetValue(pos + neighborOffsets[4], out var nzp)
                            ? nzp.GetVoxels()
                            : WorldSettings.emptyVoxels,

                        zNeg = neighbours.TryGetValue(pos + neighborOffsets[5], out var nzn)
                            ? nzn.GetVoxels()
                            : WorldSettings.emptyVoxels,
                    }
                };

                jobHandle = job.Schedule();
                jobRunning = true;
            }
            else if (jobHandle.IsCompleted)
            {
                jobHandle.Complete();
                hasGenerated = true;
                jobRunning = false;

                if (mesh) mesh.Clear();
                else
                {
                    mesh = new Mesh
                    {
                        name = "Chunk Mesh",
                        indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
                    };
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
                }
                mesh.MarkDynamic();

                // Assign to mesh
                mesh.Clear();
                mesh.vertices = MeshHelper.NativeToVector3(vertices);
                mesh.SetTriangles(MeshHelper.NativeToInt(triangles), 0);
                mesh.SetUVs(0, MeshHelper.NativeToFloat2(uvs));

                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                vertices.Dispose();
                triangles.Dispose();
                uvs.Dispose();
                mesh.tangents = null;
                mesh.colors = null;
                mesh.UploadMeshData(true);
            }
        }

        private void Render()
        {
            if (!hasGenerated || !mesh) return;

            if (GeometryUtility.TestPlanesAABB(WorldSettings.cameraPlanes, bounds))
            {
                Graphics.RenderMesh(WorldSettings.RENDERPARAMS, mesh, 0, matrix);
            }
        }

        private bool NeighboursReady()
        {
            if (neighbours.Count == 0) return true;


            foreach (var offset in WorldSettings.neighbourPositions)
            {
                if (neighbours.ContainsKey(pos + offset))
                {
                    Chunk item = neighbours[pos + offset];
                    if (!item.hasCalculated)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
