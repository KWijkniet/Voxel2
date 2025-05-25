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
    public NativeList<float3> UVs;
    public Allocator allocator;

    public MeshBuffers(Allocator allocator)
    {
        this.allocator = allocator;
        Vertices = new NativeList<float3>(allocator);
        Triangles = new NativeList<int>(allocator);
        UVs = new NativeList<float3>(allocator);
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
    [ReadOnly] public int chunkYBase; // World Y position of the chunk
    [ReadOnly] public int3 chunkSize; // Dimensions of the chunk
    [ReadOnly] public int2 heightmapResolution; // Resolution of heightmap (X × Z)
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
    [ReadOnly] public int3 chunkSize;
    [ReadOnly] public float3 chunkPos;
    public MeshBuffers meshBuffer;

    public void Execute()
    {
        GreedyMesherThreat gm = new(voxelData, chunkSize.x, chunkSize.y, chunkSize.z, chunkPos);
        meshBuffer = gm.GenerateMesh(meshBuffer, false);
    }
}

public class Chunk
{
    public Vector3 pos;
    public Vector3Int chunkSize;
    public Status status = Status.None;

    public bool hasCalculated = false;
    public bool hasGenerated = false;
    public bool isEmpty = true;
    public bool isFull = true;

    private JobHandle calcHandle;
    private NativeArray<JobHandle> renderHandle;
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
            hasCalculated = true;
            heightmap.Dispose();
            jobScheduled = false;

            foreach (var item in voxelData)
            {
                if (item != 0)
                {
                    isEmpty = false;
                    break;
                }
            }

            foreach (var item in voxelData)
            {
                if (item == 0)
                {
                    isFull = false;
                    break;
                }
            }

            if (isEmpty)
            {
                status = Status.Ready;
            }
        }
        else if (status == Status.Calculated && !jobScheduled)
        {

            // Ensure renderHandle is not already allocated or disposed
            if (renderHandle.IsCreated)
                renderHandle.Dispose();

            // Ensure the surrounding chunks have been calculated
            if (
                World.Instance.GetChunk((int)pos.x + 1, (int)pos.y, (int)pos.z).hasCalculated != true ||
                World.Instance.GetChunk((int)pos.x - 1, (int)pos.y, (int)pos.z).hasCalculated != true ||
                World.Instance.GetChunk((int)pos.x, (int)pos.y, (int)pos.z + 1).hasCalculated != true ||
                World.Instance.GetChunk((int)pos.x, (int)pos.y, (int)pos.z - 1).hasCalculated != true ||
                World.Instance.GetChunk((int)pos.x, (int)pos.y + 1, (int)pos.z).hasCalculated != true ||
                World.Instance.GetChunk((int)pos.x, (int)pos.y - 1, (int)pos.z).hasCalculated != true
                )
            {
                return;
            }

            renderHandle = new NativeArray<JobHandle>(4, Allocator.TempJob); // Allocate per use!

            for (int i = 0; i < 4; i++)
            {
                meshBufferArray[i] = new MeshBuffers(Allocator.Persistent);
            }

            renderHandle[0] = new ChunkRenderJob
            {
                voxelData = voxelData,
                chunkPos = pos,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                meshBuffer = meshBufferArray[0]
            }.Schedule();

            renderHandle[1] = new ChunkRenderJob
            {
                voxelData = voxelData,
                chunkPos = pos,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                meshBuffer = meshBufferArray[1]
            }.Schedule();

            renderHandle[2] = new ChunkRenderJob
            {
                voxelData = voxelData,
                chunkPos = pos,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                meshBuffer = meshBufferArray[2]
            }.Schedule();

            renderHandle[3] = new ChunkRenderJob
            {
                voxelData = voxelData,
                chunkPos = pos,
                chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
                meshBuffer = meshBufferArray[3]
            }.Schedule();

            jobScheduled = true;
            status = Status.Rendering;
        }
        else if (status == Status.Rendering && renderHandle.IsCreated)
        {
            if (JobHandle.CombineDependencies(renderHandle).IsCompleted)
            {
                JobHandle.CompleteAll(renderHandle);
                status = Status.Ready;
                hasGenerated = true;

                meshes.Add(Helpers.ConvertToMesh("Greedy", meshBufferArray[0], meshBufferArray[1]));
                meshes.Add(Helpers.ConvertToMesh("Details", meshBufferArray[2], meshBufferArray[3]));

                renderHandle.Dispose();
            }
        }
    }

    public void Draw()
    {
        if (meshes.Count > 0)
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

    public void DrawGizmos()
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
    }

    public NativeArray<byte> GetVoxelData()
    {
        return voxelData;
    }

    public void Reset()
    {
        status = Status.None;
        jobScheduled = false;
    }
}