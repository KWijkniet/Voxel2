using System.Collections.Generic;

/// <summary>
/// Pure data class. Stores 16³ voxels using palette compression.
/// Bit width per index grows automatically as new block types are added:
///   1 unique type  → 1 bit/voxel  (512 B)
///   ≤2 types       → 1 bit/voxel  (512 B)
///   ≤4 types       → 2 bits/voxel (1 KB)
///   ≤16 types      → 4 bits/voxel (2 KB)
///   ≤256 types     → 8 bits/voxel (4 KB)
/// </summary>
public class PaletteChunk
{
    public const int Size = 16;
    private const int VoxelCount = Size * Size * Size; // 4096

    // palette[0] is always Air (0)
    private readonly List<byte> _palette = new List<byte> { BlockType.Air };
    private uint[] _data;
    private int _bitsPerIndex;

    public PaletteChunk()
    {
        _bitsPerIndex = 1;
        _data = AllocData(_bitsPerIndex);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public byte GetBlock(int x, int y, int z)
    {
        int paletteIndex = ReadBits(Flatten(x, y, z));
        return _palette[paletteIndex];
    }

    public void SetBlock(int x, int y, int z, byte blockType)
    {
        int paletteIndex = _palette.IndexOf(blockType);
        if (paletteIndex < 0)
        {
            paletteIndex = _palette.Count;
            _palette.Add(blockType);
            GrowIfNeeded(); // may repack _data with more bits
        }
        WriteBits(Flatten(x, y, z), paletteIndex);
    }

    public bool IsSolid(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Size || y >= Size || z >= Size)
            return false;
        return GetBlock(x, y, z) != BlockType.Air;
    }

    // ── Bit packing ───────────────────────────────────────────────────────────

    private int ReadBits(int voxelIndex)
    {
        int bitIndex  = voxelIndex * _bitsPerIndex;
        int wordIndex = bitIndex >> 5;        // / 32
        int bitOffset = bitIndex & 31;        // % 32
        uint mask = (1u << _bitsPerIndex) - 1u;

        if (bitOffset + _bitsPerIndex <= 32)
            return (int)((_data[wordIndex] >> bitOffset) & mask);

        // Spans two words
        int bitsInFirst = 32 - bitOffset;
        uint lo = (_data[wordIndex]     >> bitOffset);
        uint hi = (_data[wordIndex + 1] << bitsInFirst);  // shift left to align, then mask
        // Reconstruct: lo has bitsInFirst bits, hi contributes the rest
        hi = _data[wordIndex + 1] & (mask >> bitsInFirst);
        return (int)((lo & ((1u << bitsInFirst) - 1u)) | (hi << bitsInFirst));
    }

    private void WriteBits(int voxelIndex, int value)
    {
        int bitIndex  = voxelIndex * _bitsPerIndex;
        int wordIndex = bitIndex >> 5;
        int bitOffset = bitIndex & 31;
        uint mask = (1u << _bitsPerIndex) - 1u;

        if (bitOffset + _bitsPerIndex <= 32)
        {
            _data[wordIndex] = (_data[wordIndex] & ~(mask << bitOffset))
                             | ((uint)value << bitOffset);
            return;
        }

        // Spans two words
        int bitsInFirst = 32 - bitOffset;
        _data[wordIndex] = (_data[wordIndex] & ~(mask << bitOffset))
                         | ((uint)value << bitOffset);
        uint hiMask = mask >> bitsInFirst;
        _data[wordIndex + 1] = (_data[wordIndex + 1] & ~hiMask)
                              | ((uint)(value >> bitsInFirst) & hiMask);
    }

    // ── Growth ────────────────────────────────────────────────────────────────

    private void GrowIfNeeded()
    {
        int needed = BitsRequired(_palette.Count);
        if (needed <= _bitsPerIndex) return;

        // Read all current indices at old bit width, then repack at new width
        var indices = new int[VoxelCount];
        for (int i = 0; i < VoxelCount; i++)
            indices[i] = ReadBits(i);

        _bitsPerIndex = needed;
        _data = AllocData(_bitsPerIndex);

        for (int i = 0; i < VoxelCount; i++)
            WriteBits(i, indices[i]);
    }

    private static int BitsRequired(int paletteSize)
    {
        if (paletteSize <= 2)   return 1;
        if (paletteSize <= 4)   return 2;
        if (paletteSize <= 16)  return 4;
        return 8;
    }

    private static uint[] AllocData(int bitsPerIndex)
    {
        int totalBits = VoxelCount * bitsPerIndex;
        return new uint[(totalBits + 31) / 32];
    }

    private static int Flatten(int x, int y, int z) => x + y * Size + z * Size * Size;
}
