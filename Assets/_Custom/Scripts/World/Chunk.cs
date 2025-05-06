using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using NUnit.Framework.Internal;
using UnityEditor;

public enum Status
{
    None = 0,
    Calculating = 1,
    Calculated = 2,
    Rendering = 3,
    Ready = 4,
    Updating = 5,
}

public struct MeshBuffers
{
    public NativeList<float3> Vertices;
    public NativeList<int> Triangles;
    public NativeList<float2> UVs;

    public MeshBuffers(Allocator allocator)
    {
        Vertices = new NativeList<float3>(allocator);
        Triangles = new NativeList<int>(allocator);
        UVs = new NativeList<float2>(allocator);
    }

    public void Dispose()
    {
        if (Vertices.IsCreated) Vertices.Dispose();
        if (Triangles.IsCreated) Triangles.Dispose();
        if (UVs.IsCreated) UVs.Dispose();
    }
}

[BurstCompile]
public struct ChunkCalculateJob : IJob
{
    public int chunkYBase; // World Y position of the chunk
    public int3 chunkSize; // Dimensions of the chunk
    public int2 heightmapResolution; // Resolution of heightmap (X × Z)
    [ReadOnly] public NativeArray<float> heightmap;
    public NativeArray<byte> voxelData; // Output voxel data (0 = air, 1 = stone)

    public void Execute()
    {
        int width = chunkSize.x;
        int depth = chunkSize.z;
        int height = chunkSize.y;

        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                float heightValue = heightmap[x + z * heightmapResolution.x];

                for (int y = 0; y < height; y++)
                {
                    int worldY = chunkYBase + y;
                    int index = x + y * width + z * width * height;

                    voxelData[index] = worldY <= heightValue ? (byte)1 : (byte)0;
                }
            }
        }
    }
}

[BurstCompile]
public struct ChunkRenderJob : IJob
{
    [ReadOnly] public NativeArray<byte> voxelData;
    public int3 chunkSize;

    public MeshBuffers greedySolid;
    public MeshBuffers greedyTransparent;

    public MeshBuffers detailsSolid;
    public MeshBuffers detailsTransparent;


    public void Execute()
    {

        // EXAMPLE
        //int width = chunkSize.x;
        //int height = chunkSize.y;
        //int depth = chunkSize.z;

        //for (int z = 0; z < depth; z++)
        //{
        //    for (int y = 0; y < height; y++)
        //    {
        //        for (int x = 0; x < width; x++)
        //        {
        //            int index = x + y * width + z * width * height;
        //            byte voxel = voxelData[index];

        //            int meshIndex = GetMeshType(voxel);
        //            var mesh = meshBuffers[meshIndex];

        //            // Dummy face (1 quad)
        //            float3 pos = new float3(x, y, z);
        //            int vertIndex = mesh.Vertices.Length;

        //            mesh.Vertices.Add(pos + new float3(0, 0, 0));
        //            mesh.Vertices.Add(pos + new float3(1, 0, 0));
        //            mesh.Vertices.Add(pos + new float3(1, 1, 0));
        //            mesh.Vertices.Add(pos + new float3(0, 1, 0));

        //            mesh.UVs.Add(new float2(0, 0));
        //            mesh.UVs.Add(new float2(1, 0));
        //            mesh.UVs.Add(new float2(1, 1));
        //            mesh.UVs.Add(new float2(0, 1));

        //            mesh.Triangles.Add(vertIndex + 0);
        //            mesh.Triangles.Add(vertIndex + 2);
        //            mesh.Triangles.Add(vertIndex + 1);
        //            mesh.Triangles.Add(vertIndex + 0);
        //            mesh.Triangles.Add(vertIndex + 3);
        //            mesh.Triangles.Add(vertIndex + 2);
        //        }
        //    }
        //}
    }
}

public class Chunk
{
    public Vector3 pos;
    public Vector3Int chunkSize;
    public Status status = Status.None;

    private JobHandle calcHandle;
    private JobHandle renderHandle;
    private bool jobScheduled = false;

    private NativeArray<float> heightmap;
    private NativeArray<byte> voxelData;
    private List<Mesh> meshes = new();
    private NativeArray<MeshBuffers> meshBufferArray = new NativeArray<MeshBuffers>(4, Allocator.Persistent);
    private Matrix4x4 matrix;

    public void Init(Vector3Int pos, Vector3Int chunkSize, NativeArray<float> heightmap)
    {
        this.pos = pos;
        this.chunkSize = chunkSize;
        status = Status.Calculating;
        jobScheduled = false;

        this.matrix = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);

        this.heightmap = new NativeArray<float>(heightmap.Length, Allocator.Persistent);
        heightmap.CopyTo(this.heightmap);

        voxelData = new NativeArray<byte>(chunkSize.x * chunkSize.y * chunkSize.z, Allocator.Persistent);
    }

    public void Update()
    {
        if (status == Status.Calculating && !jobScheduled)
        {
            var calcJob = new ChunkCalculateJob {
                chunkYBase = (int) pos.y,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                heightmap = heightmap,
                heightmapResolution = new int2(chunkSize.x, chunkSize.z),
                voxelData = voxelData
            };
            calcHandle = calcJob.Schedule();
            jobScheduled = true;
        }
        else if (status == Status.Calculating && calcHandle.IsCompleted)
        {
            calcHandle.Complete();
            status = Status.Calculated;
            heightmap.Dispose();
            jobScheduled = false;
        }
        else if (status == Status.Calculated && !jobScheduled)
        {
            meshBufferArray[0] = new MeshBuffers(Allocator.Persistent); // Mesh 0
            meshBufferArray[1] = new MeshBuffers(Allocator.Persistent); // Mesh 1
            meshBufferArray[2] = new MeshBuffers(Allocator.Persistent); // Mesh 0
            meshBufferArray[3] = new MeshBuffers(Allocator.Persistent); // Mesh 1

            var renderJob = new ChunkRenderJob
            {
                voxelData = voxelData,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                greedySolid = meshBufferArray[0],
                greedyTransparent = meshBufferArray[1],
                detailsSolid = meshBufferArray[2],
                detailsTransparent = meshBufferArray[3],
            };
            renderHandle = renderJob.Schedule();
            jobScheduled = true;
            status = Status.Rendering;
        }
        else if (status == Status.Rendering && renderHandle.IsCompleted)
        {
            renderHandle.Complete();
            status = Status.Ready;

            meshes.Add(Helpers.ConvertToMesh("Greedy", meshBufferArray[0], meshBufferArray[1]));
            meshes.Add(Helpers.ConvertToMesh("Details", meshBufferArray[2], meshBufferArray[3]));
        }
    }

    public void Draw()
    {
        Color color = status switch
        {
            Status.None => Color.black,
            Status.Calculating or Status.Rendering or Status.Updating => Color.red,
            Status.Ready => Color.green,
            _ => Color.white
        };
        Gizmos.color = color;
        Gizmos.DrawWireCube(pos + (chunkSize / 2), chunkSize);

        if(meshes.Count > 0)
        {
            int index = 0;
            foreach (Mesh mesh in meshes)
            {
                if (!mesh) { meshes.Remove(mesh); break; }

                Graphics.RenderMesh(World.renderParams[index % 2], mesh, index % 2, matrix);
                index++;
            }
        }
    }

    public void Reset()
    {
        status = Status.None;
        jobScheduled = false;
    }
}