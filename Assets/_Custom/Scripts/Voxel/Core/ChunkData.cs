using Unity.Collections;
using Unity.Mathematics;

namespace Voxel.Core
{
    /// <summary>
    /// Stores voxel data for a 16x16x16 chunk using NativeArray for Burst compatibility.
    /// Index formula: x + y * CHUNK_SIZE + z * CHUNK_SIZE_SQ
    /// Memory: 16^3 * 2 bytes = 8KB per chunk
    /// </summary>
    public struct ChunkData : System.IDisposable
    {
        public NativeArray<ushort> voxels;
        public int3 chunkCoord;
        public bool isGenerated;

        public ChunkData(int3 coord, Allocator allocator)
        {
            voxels = new NativeArray<ushort>(Constants.CHUNK_VOLUME, allocator);
            chunkCoord = coord;
            isGenerated = false;
        }

        public void Dispose()
        {
            if (voxels.IsCreated)
            {
                voxels.Dispose();
            }
        }

        /// <summary>
        /// Convert local XYZ position to flat array index.
        /// </summary>
        public static int ToIndex(int x, int y, int z)
        {
            return x + y * Constants.CHUNK_SIZE + z * Constants.CHUNK_SIZE_SQ;
        }

        /// <summary>
        /// Convert local XYZ position to flat array index.
        /// </summary>
        public static int ToIndex(int3 pos)
        {
            return pos.x + pos.y * Constants.CHUNK_SIZE + pos.z * Constants.CHUNK_SIZE_SQ;
        }

        /// <summary>
        /// Convert flat array index to local XYZ position.
        /// </summary>
        public static int3 ToPosition(int index)
        {
            int x = index % Constants.CHUNK_SIZE;
            int y = (index / Constants.CHUNK_SIZE) % Constants.CHUNK_SIZE;
            int z = index / Constants.CHUNK_SIZE_SQ;
            return new int3(x, y, z);
        }

        /// <summary>
        /// Check if local position is within chunk bounds.
        /// </summary>
        public static bool IsInBounds(int x, int y, int z)
        {
            return x >= 0 && x < Constants.CHUNK_SIZE &&
                   y >= 0 && y < Constants.CHUNK_SIZE &&
                   z >= 0 && z < Constants.CHUNK_SIZE;
        }

        /// <summary>
        /// Check if local position is within chunk bounds.
        /// </summary>
        public static bool IsInBounds(int3 pos)
        {
            return IsInBounds(pos.x, pos.y, pos.z);
        }

        /// <summary>
        /// Get voxel at local position. Returns air if out of bounds.
        /// </summary>
        public ushort GetVoxel(int x, int y, int z)
        {
            if (!IsInBounds(x, y, z)) return Constants.BLOCK_AIR;
            return voxels[ToIndex(x, y, z)];
        }

        /// <summary>
        /// Get voxel at local position. Returns air if out of bounds.
        /// </summary>
        public ushort GetVoxel(int3 pos)
        {
            return GetVoxel(pos.x, pos.y, pos.z);
        }

        /// <summary>
        /// Set voxel at local position. Does nothing if out of bounds.
        /// </summary>
        public void SetVoxel(int x, int y, int z, ushort type)
        {
            if (!IsInBounds(x, y, z)) return;
            voxels[ToIndex(x, y, z)] = type;
        }

        /// <summary>
        /// Set voxel at local position. Does nothing if out of bounds.
        /// </summary>
        public void SetVoxel(int3 pos, ushort type)
        {
            SetVoxel(pos.x, pos.y, pos.z, type);
        }

        /// <summary>
        /// Fill entire chunk with a single voxel type.
        /// </summary>
        public void Fill(ushort type)
        {
            for (int i = 0; i < Constants.CHUNK_VOLUME; i++)
            {
                voxels[i] = type;
            }
        }

        /// <summary>
        /// Clear chunk to all air.
        /// </summary>
        public void Clear()
        {
            Fill(Constants.BLOCK_AIR);
        }
    }
}
