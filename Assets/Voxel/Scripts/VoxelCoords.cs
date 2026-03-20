using UnityEngine;

public static class VoxelCoords
{
    public static Vector3Int WorldToChunkCoord(Vector3 p)
    {
        int s = VoxelChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / s), 0, Mathf.FloorToInt(p.z / s));
    }

    public static Vector3Int WorldToChunkCoord(Vector3Int p)
    {
        int s = VoxelChunk.Size;
        return new Vector3Int(Mathf.FloorToInt(p.x / (float)s), 0, Mathf.FloorToInt(p.z / (float)s));
    }

    public static Vector3Int ChunkToRegionCoord(Vector3Int chunk, int lodLevel)
    {
        int size = 1 << lodLevel;
        return new Vector3Int(
            Mathf.FloorToInt(chunk.x / (float)size),
            lodLevel,
            Mathf.FloorToInt(chunk.z / (float)size));
    }

    public static Vector3Int RegionBaseChunkCoord(Vector3Int regionCoord)
    {
        int size = 1 << regionCoord.y;
        return new Vector3Int(regionCoord.x * size, 0, regionCoord.z * size);
    }

    public static Vector3 ChunkToWorldPos(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s, c.y * s, c.z * s);
    }

    public static Vector3 RegionWorldPos(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s, 0, regionCoord.z * s);
    }

    public static Vector3 ChunkCenterWorld(Vector3Int c)
    {
        float s = VoxelChunk.Size;
        return new Vector3(c.x * s + s * .5f, c.y * s + s * .5f, c.z * s + s * .5f);
    }

    public static Vector3 RegionCenterWorld(Vector3Int regionCoord)
    {
        float s = VoxelChunk.Size * (1 << regionCoord.y);
        return new Vector3(regionCoord.x * s + s * .5f, 0, regionCoord.z * s + s * .5f);
    }
}

internal static class Vector3Ext
{
    internal static float sqrMagnitude_To(this Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return dx*dx + dy*dy + dz*dz;
    }
}
