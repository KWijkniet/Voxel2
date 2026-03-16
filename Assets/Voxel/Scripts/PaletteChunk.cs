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

    // O(1) reverse lookup: block type → palette index.
    // 255 = "not in palette yet". Air is always at index 0.
    private readonly byte[] _blockToIndex = new byte[256];

    public PaletteChunk()
    {
        _bitsPerIndex = 1;
        _data = AllocData(_bitsPerIndex);

        // Initialise all as "not in palette", then register Air at index 0
        for (int i = 0; i < 256; i++) _blockToIndex[i] = 255;
        _blockToIndex[BlockType.Air] = 0;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public byte GetBlock(int x, int y, int z)
    {
        int paletteIndex = ReadBits(Flatten(x, y, z));
        return _palette[paletteIndex];
    }

    public void SetBlock(int x, int y, int z, byte blockType)
    {
        int paletteIndex = _blockToIndex[blockType];
        if (paletteIndex == 255) // not yet in palette
        {
            paletteIndex = _palette.Count;
            _palette.Add(blockType);
            _blockToIndex[blockType] = (byte)paletteIndex;
            GrowIfNeeded();
        }
        WriteBits(Flatten(x, y, z), paletteIndex);
    }

    /// <summary>
    /// Fill every voxel with a single block type in O(n/32) time —
    /// far faster than calling SetBlock 4096 times for uniform chunks.
    /// </summary>
    public void FillAll(byte blockType)
    {
        // Reset palette and lookup table
        _palette.Clear();
        _palette.Add(BlockType.Air);
        for (int i = 0; i < 256; i++) _blockToIndex[i] = 255;
        _blockToIndex[BlockType.Air] = 0;

        if (blockType == BlockType.Air)
        {
            // All-air: 1 bit per voxel, all zero (default)
            _bitsPerIndex = 1;
            _data = AllocData(1);
            return;
        }

        _palette.Add(blockType);
        _blockToIndex[blockType] = 1;
        _bitsPerIndex = 1;
        _data = AllocData(1);

        // Index 1 = blockType. Set every bit to 1 → every voxel = index 1.
        for (int i = 0; i < _data.Length; i++)
            _data[i] = uint.MaxValue;
    }

    public bool IsSolid(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Size || y >= Size || z >= Size)
            return false;
        return GetBlock(x, y, z) != BlockType.Air;
    }

    /// <summary>
    /// Bulk-loads 4096 block types from the flat byte array produced by GenerateChunkJob
    /// (index layout: x + y*16 + z*256). SeedPalette must be called first with every
    /// block type that appears in the array. Significantly faster than 4096 SetBlock calls —
    /// skips the palette-registration branch and GrowIfNeeded entirely.
    /// </summary>
    public void BulkLoad(byte[] blocks)
    {
        for (int i = 0; i < VoxelCount; i++)
        {
            byte b = blocks[i];
            if (b == BlockType.Air) continue; // index 0 = all-zero bits, already initialised
            WriteBits(i, _blockToIndex[b]);
        }
    }

    /// <summary>
    /// Pre-populates the palette with all provided block types and widens _data to the
    /// required final bit-width in one step. Call this BEFORE any SetBlock to avoid
    /// incremental GrowIfNeeded repacks as the palette crosses each bit-width boundary.
    ///
    /// Safe only when all voxels are still Air (index 0): index 0 is all-zero bits at
    /// every supported bit-width, so reallocating _data without a voxel repack is correct.
    /// </summary>
    public void SeedPalette(params byte[] blockTypes)
    {
        foreach (var bt in blockTypes)
        {
            if (_blockToIndex[bt] != 255) continue; // already registered
            _blockToIndex[bt] = (byte)_palette.Count;
            _palette.Add(bt);
        }

        // Grow _data once to the final required width.
        // No voxel repack is needed — all voxels are Air (index 0 = all-zero bits).
        int needed = BitsRequired(_palette.Count);
        if (needed > _bitsPerIndex)
        {
            _bitsPerIndex = needed;
            _data = AllocData(_bitsPerIndex);
        }
    }

    // ── Bit packing ───────────────────────────────────────────────────────────

    private int ReadBits(int voxelIndex)
    {
        int bitIndex  = voxelIndex * _bitsPerIndex;
        int wordIndex = bitIndex >> 5;
        int bitOffset = bitIndex & 31;
        uint mask = (1u << _bitsPerIndex) - 1u;

        if (bitOffset + _bitsPerIndex <= 32)
            return (int)((_data[wordIndex] >> bitOffset) & mask);

        int bitsInFirst = 32 - bitOffset;
        uint lo = (_data[wordIndex] >> bitOffset);
        uint hi = _data[wordIndex + 1] & (mask >> bitsInFirst);
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
