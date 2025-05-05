using System.Collections.Generic;
using UnityEngine;

public class Region
{
    public bool hasCalculated = false;
    public bool hasGenerated = false;

    private Vector2Int worldPos;

    private Dictionary<int, Chunk> chunks = new Dictionary<int, Chunk>(); // Key: vertical chunk index (y chunk)

    public Region(Vector2Int pos)
    {
        worldPos = pos;
        Calculate();
    }

    public void Render()
    {
        foreach (KeyValuePair<int, Chunk> entry in chunks)
        {
            entry.Value.Render();
        }
    }

    public void CalculateRange(int y)
    {
        //
    }

    public void Calculate()
    {
        int[,] heightmap = CalculateHeightmap();
        int steps = Database.worldController.worldHeight / Database.worldController.height;
        for (int i = steps; i >= 0; i--)
        {
            chunks.Add(i * Database.worldController.height, new Chunk(new Vector3Int(worldPos.x, i * Database.worldController.height, worldPos.y), heightmap));
        }
    }

    // public Chunk GetOrCreateChunk(int chunkY)
    // {
    //     if (!chunks.TryGetValue(chunkY, out var chunk))
    //     {
    //         Chunk chunk = new Chunk(chunkY);
    //         chunks[chunkY] = chunk;
    //     }
    //     return chunk;
    // }

    private int[,] CalculateHeightmap()
    {
        int[,] heightmap = new int[Database.worldController.width, Database.worldController.depth];
        for (int x = 0; x < Database.worldController.depth; x++)
        {
            for (int z = 0; z < Database.worldController.width; z++)
            {
                int maxHeight = Mathf.RoundToInt(Mathf.PerlinNoise((worldPos.x + x) * 0.05f + 1000000f, (worldPos.y + z) * 0.05f + 1000000f) * Database.worldController.worldHeight);
                heightmap[x, z] = maxHeight;
            }
        }
        return heightmap;
    }
}
