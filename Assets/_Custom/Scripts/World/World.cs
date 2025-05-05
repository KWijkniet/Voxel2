using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using System;
using UnityEngine.UIElements;
using System.Collections;

public class World : MonoBehaviour
{
    [SerializeField] private Vector3Int chunkSize = new Vector3Int(16, 16, 16);
    [SerializeField] private int calculateDistance = 2;
    [SerializeField] private int renderDistance = 1;
    [SerializeField] private int maxHeight = 255;

    private List<Vector2Int> columnLoadQueue = new List<Vector2Int>();
    private Coroutine loadRoutine;
    private Vector3 lastPos = Vector3.positiveInfinity;
    private Dictionary<Vector2Int, WorldColumn> columns = new Dictionary<Vector2Int, WorldColumn>();
    private HashSet<Vector2Int> currentCalcRange = new HashSet<Vector2Int>();
    private List<Vector2Int> insideRenderRange = new List<Vector2Int>();
    private ObjectPool<WorldChunk> chunkPool = new ObjectPool<WorldChunk>();

    private void Update()
    {
        var gridPos = RoundDownToInterval(Vector3Int.FloorToInt(transform.position), chunkSize);
        var lastGrid = RoundDownToInterval(Vector3Int.FloorToInt(lastPos), chunkSize);

        if (gridPos != lastGrid)
        {
            lastPos = transform.position;

            // Get new calculation range
            var newCalcRange = new HashSet<Vector2Int>(GetGridPositionsAround(gridPos, calculateDistance));

            // Unload columns that are no longer in range
            foreach (var pos in currentCalcRange)
            {
                if (!newCalcRange.Contains(pos) && columns.TryGetValue(pos, out var column))
                {
                    column.ReleaseChunks();
                    columns.Remove(pos);
                }
            }

            currentCalcRange = newCalcRange;

            // Queue new column positions for loading
            foreach (var pos in currentCalcRange)
            {
                if (!columns.ContainsKey(pos) && !columnLoadQueue.Contains(pos))
                {
                    columnLoadQueue.Add(pos);
                }
            }

            // Start coroutine if not running
            if (loadRoutine == null && columnLoadQueue.Count > 0)
            {
                loadRoutine = StartCoroutine(ProcessColumnLoadQueue());
            }

            // Update render range
            insideRenderRange.Clear();
            insideRenderRange.AddRange(GetGridPositionsAround(gridPos, renderDistance));
        }
    }

    private IEnumerator ProcessColumnLoadQueue()
    {
        // Sort by distance to player before processing
        columnLoadQueue.Sort((a, b) =>
        {
            float distA = (new Vector3(a.x, 0, a.y) - transform.position).sqrMagnitude;
            float distB = (new Vector3(b.x, 0, b.y) - transform.position).sqrMagnitude;
            return distA.CompareTo(distB);
        });

        int batchSize = 4;
        int processed = 0;

        while (columnLoadQueue.Count > 0)
        {
            Vector2Int pos = columnLoadQueue[0];
            columnLoadQueue.RemoveAt(0);

            if (!columns.ContainsKey(pos))
            {
                columns[pos] = new WorldColumn(pos, chunkSize, maxHeight, chunkPool);
            }

            processed++;
            if (processed >= batchSize)
            {
                processed = 0;
                yield return null;
            }
        }

        loadRoutine = null;
    }

    private List<Vector2Int> GetGridPositionsAround(Vector3Int center, int distance)
    {
        var result = new List<Vector2Int>();
        int ceil = Mathf.CeilToInt(distance);

        for (int x = -ceil; x <= ceil; x++)
        {
            for (int z = -ceil; z <= ceil; z++)
            {
                Vector2Int offset = new Vector2Int(x, z);
                if (offset.magnitude <= distance)
                {
                    result.Add(new Vector2Int(center.x + x * chunkSize.x, center.z + z * chunkSize.z));
                }
            }
        }
        return result;
    }

    private Vector3Int RoundDownToInterval(Vector3 position, Vector3Int interval)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / interval.x) * interval.x,
            Mathf.FloorToInt(position.y / interval.y) * interval.y,
            Mathf.FloorToInt(position.z / interval.z) * interval.z
        );
    }

    private void OnDrawGizmos()
    {
        var gridPos = RoundDownToInterval(Vector3Int.FloorToInt(lastPos), chunkSize);

        foreach (var pos in insideRenderRange)
        {
            if (columns.TryGetValue(pos, out var column))
            {
                column.Draw(gridPos.y, renderDistance);
            }
        }
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

public class WorldColumn
{
    public Vector3Int pos;
    public Vector3Int chunkSize;
    private Dictionary<int, WorldChunk> chunks = new Dictionary<int, WorldChunk>();
    private ObjectPool<WorldChunk> pool;

    public WorldColumn(Vector2Int pos, Vector3Int chunkSize, int maxHeight, ObjectPool<WorldChunk> pool)
    {
        this.pos = new Vector3Int(pos.x, 0, pos.y);
        this.chunkSize = chunkSize;
        this.pool = pool;
        PrecalculateChunks(maxHeight);
    }

    private void PrecalculateChunks(int maxHeight)
    {
        for (int y = maxHeight; y >= 0; y -= chunkSize.y)
        {
            var chunk = pool.Get();
            chunk.Init(new Vector3Int(pos.x, y, pos.z), chunkSize);
            chunks[y] = chunk;
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

    private List<int> GetHeightsAround(int centerY, int distance)
    {
        var list = new List<int>();
        int ceil = Mathf.CeilToInt(distance);

        for (int i = -ceil; i <= ceil; i++)
        {
            list.Add(centerY + i * chunkSize.y);
        }
        return list;
    }

    public void Draw(int y, int distance)
    {
        foreach (int height in GetHeightsAround(y, distance))
        {
            if (chunks.TryGetValue(height, out var chunk))
            {
                chunk.Reload();
                chunk.Draw();
            }
        }
    }
}

public class WorldChunk
{
    public Vector3 pos;
    public Vector3Int chunkSize;
    public Status status = Status.None;

    public void Init(Vector3Int pos, Vector3Int chunkSize)
    {
        this.pos = pos;
        this.chunkSize = chunkSize;
        status = Status.None;
        Calculating();
    }

    public void Reset()
    {
        status = Status.None;
    }

    private async void Calculating()
    {
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

    public void Reload()
    {
        if (status == Status.Ready) return;
        if (status == Status.Calculated)
            Rendering();
        else
            Calculating();
    }

    public void Draw()
    {
        Color color = status switch
        {
            Status.None => Color.black,
            Status.Calculating or Status.Rendering or Status.Updating => Color.red,
            Status.Ready => Color.green,
            _ => Color.white,
        };
        Gizmos.color = color;
        Gizmos.DrawWireCube(pos + (chunkSize / 2), chunkSize);
    }
}