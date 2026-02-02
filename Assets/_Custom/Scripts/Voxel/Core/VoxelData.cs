using Unity.Mathematics;

namespace Voxel.Core
{
    /// <summary>
    /// Represents a single voxel's data.
    /// Uses ushort for block type to support 256+ block types.
    /// </summary>
    public struct Voxel
    {
        public ushort typeId;

        public bool IsAir => typeId == Constants.BLOCK_AIR;
        public bool IsSolid => typeId != Constants.BLOCK_AIR;

        public Voxel(ushort type)
        {
            typeId = type;
        }

        public static Voxel Air => new Voxel(Constants.BLOCK_AIR);
        public static Voxel Stone => new Voxel(Constants.BLOCK_STONE);
        public static Voxel Dirt => new Voxel(Constants.BLOCK_DIRT);
        public static Voxel Grass => new Voxel(Constants.BLOCK_GRASS);
    }

    /// <summary>
    /// Block type definition with rendering and physics properties.
    /// </summary>
    [System.Serializable]
    public struct BlockType
    {
        public ushort id;
        public string name;
        public bool isSolid;
        public bool isTransparent;
        public float4 color; // RGBA color for solid color rendering

        public static BlockType CreateSolid(ushort id, string name, float4 color)
        {
            return new BlockType
            {
                id = id,
                name = name,
                isSolid = true,
                isTransparent = false,
                color = color
            };
        }

        public static BlockType CreateAir()
        {
            return new BlockType
            {
                id = Constants.BLOCK_AIR,
                name = "Air",
                isSolid = false,
                isTransparent = true,
                color = new float4(0, 0, 0, 0)
            };
        }
    }
}
