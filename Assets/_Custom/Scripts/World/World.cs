using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using System;
using UnityEngine.UIElements;

public class World : MonoBehaviour
{
    public Vector3Int chunkSize = new Vector3Int(16, 16, 16);
    public int calculateDistance = 1;
    public int renderDistance = 1;
    public int maxHeight = 255;
    private Dictionary<Vector2Int, WorldColumn> columns = new Dictionary<Vector2Int, WorldColumn>();
    private List<Vector2Int> insideRange = new List<Vector2Int>();
    private Vector3 lastPos;

    private void Start()
    {
        lastPos = new Vector3(10000, 10000, 10000);
        if (renderDistance >= calculateDistance)
        {
            renderDistance = calculateDistance - 1;
        }
    }

    private void Update()
    {
        if (Vector3.Distance(lastPos, transform.position) > chunkSize.x)
        {
            lastPos = transform.position;
            insideRange.Clear();

            //Recalculate inside range
            Vector3Int gridPos = RoundDownToInterval(Vector3Int.FloorToInt(lastPos), chunkSize);
            List<Vector2Int> range = GetGridPositionsAround(gridPos, renderDistance);
            insideRange.AddRange(range);
        }
    }

    private List<Vector2Int> GetGridPositionsAround(Vector3Int center, int distance)
    {
        List<Vector2Int> positions = new List<Vector2Int>();
        int ceilDist = Mathf.CeilToInt(distance);

        for (int x = -ceilDist; x <= ceilDist; x++)
        {
            for (int z = -ceilDist; z <= ceilDist; z++)
            {
                Vector3 offset = new Vector3(x, 0, z);
                if (offset.magnitude <= distance)
                {
                    positions.Add(new Vector2Int(center.x + (x * chunkSize.x), center.z + (z * chunkSize.z)));
                }
            }
        }

        return positions;
    }

    private WorldColumn GetOrCreate(Vector2Int pos)
    {
        if (columns.TryGetValue(pos, out WorldColumn column))
        {
            return column;
        }

        WorldColumn wc = new WorldColumn(pos, chunkSize, maxHeight);
        columns.Add(pos, wc);
        return wc;
    }

    void OnDrawGizmos()
    {
        Vector3Int gridPos = RoundDownToInterval(Vector3Int.FloorToInt(lastPos), chunkSize);
        foreach (Vector2Int item in insideRange)
        {
            WorldColumn wc = GetOrCreate(item);
            wc.Draw(gridPos.y, renderDistance);
        }
    }

    private Vector3Int RoundDownToInterval(Vector3 position, Vector3Int interval)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / interval.x) * interval.x,
            Mathf.FloorToInt(position.y / interval.y) * interval.y,
            Mathf.FloorToInt(position.z / interval.z) * interval.z
        );
    }
}

public class WorldColumn
{
    public Vector3Int pos;
    public bool isReady;
    public Vector3Int chunkSize;

    private Dictionary<int, WorldChunk> chunks = new Dictionary<int, WorldChunk>();

    public WorldColumn(Vector2Int pos, Vector3Int chunkSize, int maxHeight)
    {
        this.pos = new Vector3Int(pos.x, 0, pos.y);
        this.chunkSize = chunkSize;
        Calculating(maxHeight);
    }

    private async void Calculating(int maxHeight)
    {
        isReady = false;

        for (int y = maxHeight; y >= 0; y -= chunkSize.y)
        {
            chunks.Add(y, new WorldChunk(new Vector3Int(pos.x, y, pos.z), chunkSize));
        }

        await Helpers.Sleep(0.1f);
        isReady = true;
    }

    private List<int> GetGridPositionsAround(int y, int distance)
    {
        List<int> positions = new List<int>();
        int ceilDist = Mathf.CeilToInt(distance);

        for (int i = -ceilDist; i <= ceilDist; i++)
        {
            positions.Add(y + (i * chunkSize.y));
        }

        return positions;
    }

    public void Draw(int y, int distance)
    {
        List<int> insideRange = GetGridPositionsAround(y, distance);
        foreach (int item in insideRange)
        {
            if (chunks.TryGetValue(item, out WorldChunk chunk))
            {
                chunk.Draw();
            }
        }
    }
}

public class WorldChunk
{
    public Vector3 pos;
    public Status status = Status.None;
    public Vector3Int chunkSize;

    public WorldChunk(Vector3Int pos, Vector3Int chunkSize)
    {
        this.pos = pos;
        this.chunkSize = chunkSize;
        Calculating();
    }

    private async void Calculating()
    {
        if (status == Status.Calculated || status == Status.Ready) return;
        if (status != Status.None) return;

        status = Status.Calculating;
        await Helpers.Sleep(3f);
        status = Status.Calculated;
        Rendering();
    }

    private async void Rendering()
    {
        if (status != Status.Calculated) return;

        status = Status.Rendering;
        await Helpers.Sleep(3f);
        status = Status.Ready;
    }

    public void Draw()
    {
        Color color = Color.white;
        switch (status)
        {
            case Status.None:
                color = Color.black;
                break;
            case Status.Calculating:
            case Status.Rendering:
            case Status.Updating:
                color = Color.red;
                break;
            case Status.Ready:
                color = Color.green;
                break;
        }
        Gizmos.color = color;
        Gizmos.DrawWireCube(pos + (chunkSize / 2), chunkSize);
    }
}

public enum Status
{
    None = 0,
    Calculating = 1,
    Calculated = 2,
    Rendering = 3,
    Ready = 4,
    Updating = 5,
}
