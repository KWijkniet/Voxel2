using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public struct HeightmapGenerationJob : IJob
{
    public int2 columnOrigin;
    public int2 resolution;
    public float frequency;
    public float amplitude;

    public NativeArray<float> heightmap;

    public void Execute()
    {
        for (int z = 0; z < resolution.y; z++)
        {
            for (int x = 0; x < resolution.x; x++)
            {
                float2 samplePos = new float2(
                    (columnOrigin.x + x) * frequency,
                    (columnOrigin.y + z) * frequency
                );

                float height = noise.cnoise(samplePos) * amplitude;
                heightmap[x + z * resolution.x] = height;
            }
        }
    }
}

public class Region
{
    public Vector3Int pos;
    public Vector3Int chunkSize;
    private int maxHeight;

    private NativeArray<float> heightmap;
    private JobHandle heightmapJobHandle;
    private bool heightmapScheduled;

    private Dictionary<int, Chunk> chunks = new();
    private ObjectPool<Chunk> pool;

    public Region(Vector2Int gridPos, Vector3Int chunkSize, int maxHeight, ObjectPool<Chunk> chunkPool)
    {
        this.pos = new Vector3Int(gridPos.x, 0, gridPos.y);
        this.chunkSize = chunkSize;
        this.maxHeight = maxHeight;
        this.pool = chunkPool;

        ScheduleHeightmapJob();
    }

    private void ScheduleHeightmapJob()
    {
        int2 resolution = new int2(chunkSize.x, chunkSize.z);
        heightmap = new NativeArray<float>(resolution.x * resolution.y, Allocator.Persistent);

        var job = new HeightmapGenerationJob
        {
            columnOrigin = new int2(pos.x, pos.z),
            resolution = resolution,
            frequency = 0.05f,
            amplitude = maxHeight,
            heightmap = heightmap
        };

        heightmapJobHandle = job.Schedule();
        heightmapScheduled = true;
    }

    private void GenerateChunksFromHeightmap()
    {
        for (int y = 0; y < maxHeight; y += chunkSize.y)
        {
            var chunk = pool.Get();
            chunk.Init(new Vector3Int(pos.x, y, pos.z), chunkSize, heightmap);
            chunks[y] = chunk;
        }
    }

    public void Update()
    {
        if (heightmapScheduled && heightmapJobHandle.IsCompleted)
        {
            heightmapJobHandle.Complete();
            heightmapScheduled = false;

            GenerateChunksFromHeightmap();
            heightmap.Dispose();
        }
    }

    public void Draw(int playerY, int distance)
    {
        foreach (var kvp in chunks)
        {
            if (Mathf.Abs(kvp.Key - playerY) <= chunkSize.y * distance)
            {
                kvp.Value.Draw();
            }
        }
    }

    public void UpdateChunks()
    {
        foreach (var chunk in chunks.Values)
        {
            chunk.Update();
        }
    }

    public void ReleaseChunks()
    {
        foreach (var chunk in chunks.Values)
        {
            chunk.Reset();
            pool.Return(chunk);
        }
        chunks.Clear();
    }
}
