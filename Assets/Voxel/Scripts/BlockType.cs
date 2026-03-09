public static class BlockType
{
    public const byte Air   = 0;
    public const byte Stone = 1;
    public const byte Dirt  = 2;
    public const byte Grass = 3;

    // Number of solid block types in the texture atlas (Air is not in the atlas)
    public const int AtlasTileCount = 3;
}
