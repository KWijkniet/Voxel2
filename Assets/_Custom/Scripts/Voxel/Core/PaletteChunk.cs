using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Voxel.Core
{
    /// <summary>
    /// Palette-compressed chunk storage.
    /// Instead of storing full block IDs (16-bit), stores indices into a local palette.
    /// Typical chunks use 5-15 unique block types, so indices can be 4-8 bits.
    ///
    /// Memory savings:
    /// - Standard: 16^3 * 2 bytes = 8KB
    /// - Palette (8 types): 16^3 * 0.5 bytes + 8*2 = 2KB + 16 = ~2KB (75% savings)
    /// - Palette (16 types): 16^3 * 0.5 bytes + 16*2 = 2KB + 32 = ~2KB (75% savings)
    /// </summary>
    public class PaletteChunk : System.IDisposable
    {
        public int3 ChunkCoord { get; private set; }

        // Palette maps local index -> global block ID
        private List<ushort> palette;

        // Reverse lookup: global block ID -> local index
        private Dictionary<ushort, byte> reversePalette;

        // Voxel data stores palette indices
        // For <= 16 types: 4-bit indices packed into bytes (2 voxels per byte)
        // For <= 256 types: 8-bit indices (1 voxel per byte)
        private byte[] packedData;

        // Bits per index (4 or 8)
        private int bitsPerIndex;

        // Whether the chunk uses packed 4-bit storage
        private bool usePacked4Bit;

        public int PaletteSize => palette.Count;
        public int DataSizeBytes => packedData?.Length ?? 0;

        public PaletteChunk(int3 coord)
        {
            ChunkCoord = coord;
            palette = new List<ushort> { Constants.BLOCK_AIR }; // Air is always index 0
            reversePalette = new Dictionary<ushort, byte> { { Constants.BLOCK_AIR, 0 } };
            bitsPerIndex = 4;
            usePacked4Bit = true;

            // 4-bit packed: 2 voxels per byte
            packedData = new byte[Constants.CHUNK_VOLUME / 2];
        }

        /// <summary>
        /// Create from existing NativeArray voxel data.
        /// </summary>
        public static PaletteChunk FromVoxels(int3 coord, NativeArray<ushort> voxels)
        {
            var chunk = new PaletteChunk(coord);

            // First pass: build palette
            for (int i = 0; i < voxels.Length; i++)
            {
                ushort block = voxels[i];
                if (!chunk.reversePalette.ContainsKey(block))
                {
                    if (chunk.palette.Count >= 256)
                    {
                        // Too many unique blocks - fall back to uncompressed
                        // This shouldn't happen in normal terrain
                        break;
                    }
                    chunk.reversePalette[block] = (byte)chunk.palette.Count;
                    chunk.palette.Add(block);
                }
            }

            // Determine storage mode
            if (chunk.palette.Count > 16)
            {
                // Need 8-bit indices
                chunk.usePacked4Bit = false;
                chunk.bitsPerIndex = 8;
                chunk.packedData = new byte[Constants.CHUNK_VOLUME];
            }

            // Second pass: pack data
            for (int i = 0; i < voxels.Length; i++)
            {
                ushort block = voxels[i];
                byte index = chunk.reversePalette[block];

                if (chunk.usePacked4Bit)
                {
                    int byteIndex = i / 2;
                    if (i % 2 == 0)
                    {
                        chunk.packedData[byteIndex] = (byte)(index & 0x0F);
                    }
                    else
                    {
                        chunk.packedData[byteIndex] |= (byte)((index & 0x0F) << 4);
                    }
                }
                else
                {
                    chunk.packedData[i] = index;
                }
            }

            return chunk;
        }

        /// <summary>
        /// Convert back to NativeArray for mesh generation.
        /// </summary>
        public NativeArray<ushort> ToVoxels(Allocator allocator)
        {
            var voxels = new NativeArray<ushort>(Constants.CHUNK_VOLUME, allocator);

            for (int i = 0; i < Constants.CHUNK_VOLUME; i++)
            {
                byte index;
                if (usePacked4Bit)
                {
                    int byteIndex = i / 2;
                    if (i % 2 == 0)
                    {
                        index = (byte)(packedData[byteIndex] & 0x0F);
                    }
                    else
                    {
                        index = (byte)((packedData[byteIndex] >> 4) & 0x0F);
                    }
                }
                else
                {
                    index = packedData[i];
                }

                voxels[i] = palette[index];
            }

            return voxels;
        }

        /// <summary>
        /// Get voxel at local position.
        /// </summary>
        public ushort GetVoxel(int x, int y, int z)
        {
            if (!ChunkData.IsInBounds(x, y, z)) return Constants.BLOCK_AIR;

            int i = ChunkData.ToIndex(x, y, z);
            byte index;

            if (usePacked4Bit)
            {
                int byteIndex = i / 2;
                if (i % 2 == 0)
                {
                    index = (byte)(packedData[byteIndex] & 0x0F);
                }
                else
                {
                    index = (byte)((packedData[byteIndex] >> 4) & 0x0F);
                }
            }
            else
            {
                index = packedData[i];
            }

            return palette[index];
        }

        /// <summary>
        /// Set voxel at local position.
        /// May need to expand palette or upgrade storage mode.
        /// </summary>
        public void SetVoxel(int x, int y, int z, ushort blockType)
        {
            if (!ChunkData.IsInBounds(x, y, z)) return;

            // Ensure block is in palette
            if (!reversePalette.TryGetValue(blockType, out byte index))
            {
                // Add to palette
                if (palette.Count >= 256)
                {
                    // Palette full - would need to handle this case
                    return;
                }

                index = (byte)palette.Count;
                palette.Add(blockType);
                reversePalette[blockType] = index;

                // Check if we need to upgrade from 4-bit to 8-bit
                if (usePacked4Bit && palette.Count > 16)
                {
                    UpgradeTo8Bit();
                }
            }

            int i = ChunkData.ToIndex(x, y, z);

            if (usePacked4Bit)
            {
                int byteIndex = i / 2;
                if (i % 2 == 0)
                {
                    packedData[byteIndex] = (byte)((packedData[byteIndex] & 0xF0) | (index & 0x0F));
                }
                else
                {
                    packedData[byteIndex] = (byte)((packedData[byteIndex] & 0x0F) | ((index & 0x0F) << 4));
                }
            }
            else
            {
                packedData[i] = index;
            }
        }

        /// <summary>
        /// Upgrade from 4-bit to 8-bit storage.
        /// </summary>
        private void UpgradeTo8Bit()
        {
            var newData = new byte[Constants.CHUNK_VOLUME];

            for (int i = 0; i < Constants.CHUNK_VOLUME; i++)
            {
                int byteIndex = i / 2;
                byte index;
                if (i % 2 == 0)
                {
                    index = (byte)(packedData[byteIndex] & 0x0F);
                }
                else
                {
                    index = (byte)((packedData[byteIndex] >> 4) & 0x0F);
                }
                newData[i] = index;
            }

            packedData = newData;
            usePacked4Bit = false;
            bitsPerIndex = 8;
        }

        /// <summary>
        /// Get memory usage in bytes.
        /// </summary>
        public int GetMemoryUsage()
        {
            // Packed data + palette + overhead
            return packedData.Length + (palette.Count * 2) + 64; // 64 bytes overhead estimate
        }

        /// <summary>
        /// Get compression ratio compared to uncompressed storage.
        /// </summary>
        public float GetCompressionRatio()
        {
            int uncompressedSize = Constants.CHUNK_VOLUME * 2; // 16-bit per voxel
            return (float)GetMemoryUsage() / uncompressedSize;
        }

        public void Dispose()
        {
            palette?.Clear();
            reversePalette?.Clear();
            packedData = null;
        }
    }
}
