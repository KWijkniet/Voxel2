namespace Voxel.Core
{
    public static class Constants
    {
        // Chunk dimensions
        public const int CHUNK_SIZE = 16;
        public const int CHUNK_SIZE_SQ = CHUNK_SIZE * CHUNK_SIZE;
        public const int CHUNK_VOLUME = CHUNK_SIZE * CHUNK_SIZE * CHUNK_SIZE;

        // Voxel scale: 16 voxels per Unity unit
        public const float VOXEL_SCALE = 1f / 16f; // 0.0625

        // Chunk size in world units
        public const float CHUNK_WORLD_SIZE = CHUNK_SIZE * VOXEL_SCALE; // 1.0 Unity unit

        // Block type constants
        public const ushort BLOCK_AIR = 0;
        public const ushort BLOCK_STONE = 1;
        public const ushort BLOCK_DIRT = 2;
        public const ushort BLOCK_GRASS = 3;

        // Direction indices for neighbor access
        public const int DIR_RIGHT = 0;  // +X
        public const int DIR_LEFT = 1;   // -X
        public const int DIR_UP = 2;     // +Y
        public const int DIR_DOWN = 3;   // -Y
        public const int DIR_FORWARD = 4; // +Z
        public const int DIR_BACK = 5;    // -Z
    }
}
