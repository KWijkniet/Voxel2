using Unity.Mathematics;
using UnityEngine;

namespace Voxel.Core
{
    /// <summary>
    /// Utility methods for coordinate conversions between world space, chunk space, and local voxel space.
    /// </summary>
    public static class ChunkCoord
    {
        /// <summary>
        /// Convert world position to chunk coordinate.
        /// </summary>
        public static int3 WorldToChunk(float3 worldPos)
        {
            // Convert world position to voxel position, then to chunk
            int3 voxelPos = WorldToVoxel(worldPos);
            return VoxelToChunk(voxelPos);
        }

        /// <summary>
        /// Convert world position to voxel coordinate (global voxel index).
        /// </summary>
        public static int3 WorldToVoxel(float3 worldPos)
        {
            return new int3(
                (int)math.floor(worldPos.x / Constants.VOXEL_SCALE),
                (int)math.floor(worldPos.y / Constants.VOXEL_SCALE),
                (int)math.floor(worldPos.z / Constants.VOXEL_SCALE)
            );
        }

        /// <summary>
        /// Convert global voxel position to chunk coordinate.
        /// </summary>
        public static int3 VoxelToChunk(int3 voxelPos)
        {
            // Use floor division for negative coordinates
            return new int3(
                FloorDiv(voxelPos.x, Constants.CHUNK_SIZE),
                FloorDiv(voxelPos.y, Constants.CHUNK_SIZE),
                FloorDiv(voxelPos.z, Constants.CHUNK_SIZE)
            );
        }

        /// <summary>
        /// Convert global voxel position to local position within chunk.
        /// </summary>
        public static int3 VoxelToLocal(int3 voxelPos)
        {
            // Use modulo that handles negative numbers correctly
            return new int3(
                Mod(voxelPos.x, Constants.CHUNK_SIZE),
                Mod(voxelPos.y, Constants.CHUNK_SIZE),
                Mod(voxelPos.z, Constants.CHUNK_SIZE)
            );
        }

        /// <summary>
        /// Convert chunk coordinate to world position (chunk origin).
        /// </summary>
        public static float3 ChunkToWorld(int3 chunkCoord)
        {
            return new float3(
                chunkCoord.x * Constants.CHUNK_WORLD_SIZE,
                chunkCoord.y * Constants.CHUNK_WORLD_SIZE,
                chunkCoord.z * Constants.CHUNK_WORLD_SIZE
            );
        }

        /// <summary>
        /// Convert local voxel position within chunk to world position (voxel center).
        /// </summary>
        public static float3 LocalToWorld(int3 chunkCoord, int3 localPos)
        {
            float3 chunkOrigin = ChunkToWorld(chunkCoord);
            return chunkOrigin + new float3(
                (localPos.x + 0.5f) * Constants.VOXEL_SCALE,
                (localPos.y + 0.5f) * Constants.VOXEL_SCALE,
                (localPos.z + 0.5f) * Constants.VOXEL_SCALE
            );
        }

        /// <summary>
        /// Get global voxel position from chunk and local position.
        /// </summary>
        public static int3 LocalToGlobalVoxel(int3 chunkCoord, int3 localPos)
        {
            return chunkCoord * Constants.CHUNK_SIZE + localPos;
        }

        /// <summary>
        /// Get the 6 neighbor chunk coordinates.
        /// </summary>
        public static int3[] GetNeighborChunks(int3 chunkCoord)
        {
            return new int3[]
            {
                chunkCoord + new int3(1, 0, 0),   // Right
                chunkCoord + new int3(-1, 0, 0),  // Left
                chunkCoord + new int3(0, 1, 0),   // Up
                chunkCoord + new int3(0, -1, 0),  // Down
                chunkCoord + new int3(0, 0, 1),   // Forward
                chunkCoord + new int3(0, 0, -1)   // Back
            };
        }

        /// <summary>
        /// Direction offsets for the 6 cardinal directions.
        /// Index matches Constants.DIR_* values.
        /// </summary>
        public static readonly int3[] Directions = new int3[]
        {
            new int3(1, 0, 0),   // DIR_RIGHT
            new int3(-1, 0, 0),  // DIR_LEFT
            new int3(0, 1, 0),   // DIR_UP
            new int3(0, -1, 0),  // DIR_DOWN
            new int3(0, 0, 1),   // DIR_FORWARD
            new int3(0, 0, -1)   // DIR_BACK
        };

        /// <summary>
        /// Floor division that works correctly for negative numbers.
        /// </summary>
        private static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : (a - b + 1) / b;
        }

        /// <summary>
        /// Modulo that always returns positive result.
        /// </summary>
        private static int Mod(int a, int b)
        {
            int r = a % b;
            return r < 0 ? r + b : r;
        }

        /// <summary>
        /// Calculate squared distance between two chunk coordinates.
        /// Useful for sorting by distance without expensive sqrt.
        /// </summary>
        public static int DistanceSquared(int3 a, int3 b)
        {
            int3 d = a - b;
            return d.x * d.x + d.y * d.y + d.z * d.z;
        }

        /// <summary>
        /// Check if chunk is within render distance of center.
        /// </summary>
        public static bool IsInRange(int3 chunk, int3 center, int rangeSquared)
        {
            return DistanceSquared(chunk, center) <= rangeSquared;
        }
    }
}
