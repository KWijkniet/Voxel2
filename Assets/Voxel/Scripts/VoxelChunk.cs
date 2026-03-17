using Unity.Collections;

/// <summary>
/// Flat voxel storage for a 16³ chunk.
/// Stores 4096 block types as a plain NativeArray&lt;byte&gt; for direct Burst job access.
/// Index layout: x + y*16 + z*256  (matches GenerateChunkJob output).
/// Caller owns lifetime — call Dispose() when evicting.
/// </summary>
public struct VoxelChunk
{
    public const int Size       = 16;
    public const int VoxelCount = Size * Size * Size; // 4096

    /// <summary>4096-byte flat voxel array. Allocator.Persistent — caller must Dispose.</summary>
    public NativeArray<byte> Blocks;

    public byte GetBlock(int x, int y, int z) => Blocks[x + y * Size + z * Size * Size];

    public bool IsSolid(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Size || y >= Size || z >= Size) return false;
        return Blocks[x + y * Size + z * Size * Size] != BlockType.Air;
    }

    /// <summary>Copies all voxel data into a managed byte array (for region storage).</summary>
    public void CopyTo(byte[] output) => Blocks.CopyTo(output);

    public void Dispose() { if (Blocks.IsCreated) Blocks.Dispose(); }
}
