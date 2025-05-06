using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using UnityEngine;
using UnityEditor.IMGUI.Controls;

public enum Status
{
    None = 0,
    Calculating = 1,
    Calculated = 2,
    Rendering = 3,
    Ready = 4,
    Updating = 5,
}

[BurstCompile]
public struct ChunkCalculateJob : IJob
{
    public Vector3Int position;
    public NativeArray<float> heightmap;

    public void Execute()
    {
        // Simulate chunk data generation
        // Replace with noise, voxel generation, etc.
        for (int i = 0; i < 10000; i++) { float x = Mathf.Sin(i); }
    }
}

[BurstCompile]
public struct ChunkRenderJob : IJob
{
    public Vector3Int position;

    public void Execute()
    {
        // Simulate rendering prep
        for (int i = 0; i < 10000; i++) { float x = Mathf.Cos(i); }
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

    public void Init(Vector3Int pos, Vector3Int chunkSize, NativeArray<float> heightmap)
    {
        this.pos = pos;
        this.chunkSize = chunkSize;
        this.heightmap = heightmap;
        status = Status.Calculating;
        jobScheduled = false;
    }

    public void Update()
    {
        if (status == Status.Calculating && !jobScheduled)
        {
            var calcJob = new ChunkCalculateJob { position = Vector3Int.RoundToInt(pos), heightmap = heightmap };
            calcHandle = calcJob.Schedule();
            jobScheduled = true;
        }
        else if (status == Status.Calculating && calcHandle.IsCompleted)
        {
            calcHandle.Complete();
            status = Status.Calculated;
            jobScheduled = false;
        }
        else if (status == Status.Calculated && !jobScheduled)
        {
            var renderJob = new ChunkRenderJob { position = Vector3Int.RoundToInt(pos) };
            renderHandle = renderJob.Schedule();
            jobScheduled = true;
            status = Status.Rendering;
        }
        else if (status == Status.Rendering && renderHandle.IsCompleted)
        {
            renderHandle.Complete();
            status = Status.Ready;
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
    }

    public void Reset()
    {
        status = Status.None;
        jobScheduled = false;
    }
}