/// <summary>
/// Groups hSize×verticalChunks×hSize PaletteChunks into a single voxel-accessible region.
/// hSize = 1&lt;&lt;lodLevel: 1 (LOD 0), 2 (LOD 1), 4 (LOD 2), 8 (LOD 3).
/// Used as the rendering unit for LOD 1+ — one draw call per region instead of many.
/// Thread-safe for reads once all chunks are set.
/// </summary>
public class RegionData
{
    public readonly int HSize;        // chunks per region side (1 << lodLevel)
    public readonly int VerticalChunks;
    public readonly int VoxelSizeX;   // HSize * 16
    public readonly int VoxelSizeY;   // VerticalChunks * 16
    public readonly int VoxelSizeZ;   // HSize * 16
    public int TotalChunks { get; }
    public bool IsComplete => _readyCount >= TotalChunks;

    private readonly PaletteChunk[] _chunks; // [cx + cy*HSize + cz*HSize*vc]
    private int _readyCount;

    public RegionData(int verticalChunks, int hSize)
    {
        HSize          = hSize;
        VerticalChunks = verticalChunks;
        VoxelSizeX     = hSize * PaletteChunk.Size;
        VoxelSizeY     = verticalChunks * PaletteChunk.Size;
        VoxelSizeZ     = hSize * PaletteChunk.Size;
        TotalChunks    = hSize * verticalChunks * hSize;
        _chunks        = new PaletteChunk[TotalChunks];
    }

    /// <summary>Store a chunk at local region-space coordinates (0..HSize-1, 0..vc-1, 0..HSize-1).</summary>
    public void SetChunk(int cx, int cy, int cz, PaletteChunk chunk)
    {
        int idx = Index(cx, cy, cz);
        if (_chunks[idx] == null) _readyCount++;
        _chunks[idx] = chunk;
    }

    public bool HasChunk(int cx, int cy, int cz) =>
        cx >= 0 && cy >= 0 && cz >= 0 &&
        cx < HSize && cy < VerticalChunks && cz < HSize &&
        _chunks[Index(cx, cy, cz)] != null;

    /// <summary>Get a block at voxel coords local to this region (0..VoxelSizeX, etc.).</summary>
    public byte GetBlock(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= VoxelSizeX || y >= VoxelSizeY || z >= VoxelSizeZ)
            return BlockType.Air;

        int cs    = PaletteChunk.Size;
        var chunk = _chunks[Index(x / cs, y / cs, z / cs)];
        return chunk?.GetBlock(x % cs, y % cs, z % cs) ?? BlockType.Air;
    }

    public bool IsSolid(int x, int y, int z) => GetBlock(x, y, z) != BlockType.Air;

    /// <summary>
    /// Bounds-check-free GetBlock for the greedy mesher inner loop.
    /// Caller must guarantee (x, y, z) is within [0, VoxelSizeX/Y/Z).
    /// </summary>
    public byte GetBlockUnchecked(int x, int y, int z)
    {
        int cs    = PaletteChunk.Size;
        var chunk = _chunks[Index(x / cs, y / cs, z / cs)];
        return chunk != null ? chunk.GetBlock(x % cs, y % cs, z % cs) : BlockType.Air;
    }

    public bool IsSolidUnchecked(int x, int y, int z) => GetBlockUnchecked(x, y, z) != BlockType.Air;

    private int Index(int cx, int cy, int cz) => cx + cy * HSize + cz * HSize * VerticalChunks;
}
