/// <summary>
/// Groups hSize×verticalChunks×hSize chunk voxel arrays into a single voxel-addressable region.
/// hSize = 1&lt;&lt;lodLevel: 2 (LOD 1), 4 (LOD 2), 8 (LOD 3).
/// Stores voxels as flat byte[] per chunk — same layout as VoxelChunk.Blocks.
/// Thread-safe for reads once all chunks are set.
/// </summary>
public class RegionData
{
    public readonly int HSize;          // chunks per region side (1 << lodLevel)
    public readonly int VerticalChunks;
    public readonly int VoxelSizeX;     // HSize * 16
    public readonly int VoxelSizeY;     // VerticalChunks * 16
    public readonly int VoxelSizeZ;     // HSize * 16
    public int  TotalChunks { get; }
    public bool IsComplete  => _readyCount >= TotalChunks;

    private const int ChunkSize = VoxelChunk.Size; // 16

    // One flat byte[] (VoxelChunk.VoxelCount bytes) per child chunk.
    // Index: cx + cy*HSize + cz*HSize*VerticalChunks
    private readonly byte[][] _voxels;
    private int _readyCount;

    public RegionData(int verticalChunks, int hSize)
    {
        HSize          = hSize;
        VerticalChunks = verticalChunks;
        VoxelSizeX     = hSize * ChunkSize;
        VoxelSizeY     = verticalChunks * ChunkSize;
        VoxelSizeZ     = hSize * ChunkSize;
        TotalChunks    = hSize * verticalChunks * hSize;
        _voxels        = new byte[TotalChunks][];
    }

    /// <summary>Store chunk voxel data at local region-space coordinates.</summary>
    public void SetChunk(int cx, int cy, int cz, byte[] voxels)
    {
        int idx = Index(cx, cy, cz);
        if (_voxels[idx] == null) _readyCount++;
        _voxels[idx] = voxels;
    }

    public bool HasChunk(int cx, int cy, int cz) =>
        cx >= 0 && cy >= 0 && cz >= 0 &&
        cx < HSize && cy < VerticalChunks && cz < HSize &&
        _voxels[Index(cx, cy, cz)] != null;

    /// <summary>Get a block at voxel coords local to this region.</summary>
    public byte GetBlock(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= VoxelSizeX || y >= VoxelSizeY || z >= VoxelSizeZ)
            return BlockType.Air;
        return GetBlockUnchecked(x, y, z);
    }

    public bool IsSolid(int x, int y, int z) => GetBlock(x, y, z) != BlockType.Air;

    /// <summary>
    /// Bounds-check-free variant for the greedy mesher inner loop.
    /// Caller must guarantee (x, y, z) is within [0, VoxelSizeX/Y/Z).
    /// </summary>
    public byte GetBlockUnchecked(int x, int y, int z)
    {
        int cx = x / ChunkSize, cy = y / ChunkSize, cz = z / ChunkSize;
        var v  = _voxels[Index(cx, cy, cz)];
        if (v == null) return BlockType.Air;
        int lx = x % ChunkSize, ly = y % ChunkSize, lz = z % ChunkSize;
        return v[lx + ly * ChunkSize + lz * ChunkSize * ChunkSize];
    }

    public bool IsSolidUnchecked(int x, int y, int z) => GetBlockUnchecked(x, y, z) != BlockType.Air;

    private int Index(int cx, int cy, int cz) => cx + cy * HSize + cz * HSize * VerticalChunks;
}
