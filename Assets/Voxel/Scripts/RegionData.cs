/// <summary>
/// Groups 4×verticalChunks×4 PaletteChunks into a single voxel-accessible region.
/// The region covers 64×(vc*16)×64 voxels.
/// Used as the rendering unit for LOD 1+ chunks — one draw call per region instead of 16+.
/// Thread-safe for reads once all chunks are set.
/// </summary>
public class RegionData
{
    public const int HSize = 4; // horizontal chunks per region side

    public readonly int VerticalChunks;
    public readonly int VoxelSizeX;  // HSize * 16 = 64
    public readonly int VoxelSizeY;  // VerticalChunks * 16
    public readonly int VoxelSizeZ;  // HSize * 16 = 64
    public int TotalChunks { get; }
    public bool IsComplete => _readyCount >= TotalChunks;

    private readonly PaletteChunk[] _chunks; // [cx + cy*HSize + cz*HSize*vc]
    private int _readyCount;

    public RegionData(int verticalChunks)
    {
        VerticalChunks = verticalChunks;
        VoxelSizeX     = HSize * PaletteChunk.Size;
        VoxelSizeY     = verticalChunks * PaletteChunk.Size;
        VoxelSizeZ     = HSize * PaletteChunk.Size;
        TotalChunks    = HSize * verticalChunks * HSize;
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

    private int Index(int cx, int cy, int cz) => cx + cy * HSize + cz * HSize * VerticalChunks;
}
