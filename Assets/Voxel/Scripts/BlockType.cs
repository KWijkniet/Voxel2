public static class BlockType
{
    public const byte Air   = 0;
    public const byte Stone = 1;
    public const byte Dirt  = 2;
    public const byte Grass = 3;
    public const byte Sand  = 4;
    public const byte Water = 5;
    public const byte Snow       = 6;
    public const byte Sandstone  = 7;
    public const byte FrozenDirt = 8;
    public const byte Ice        = 9;
    public const byte PackedIce  = 10;

    // Number of solid block types in the texture atlas (Air is not in the atlas).
    // Atlas layout: [Stone | Dirt | Grass | Sand | Water | Snow | Sandstone | FrozenDirt | Ice | PackedIce]
    public const int AtlasTileCount = 10;

    /// <summary>
    /// Returns true for block types that use alpha blending (water, glass, leaves, …).
    /// Add new transparent types here; the meshers and renderer pick up the change automatically.
    /// </summary>
    public static bool IsTransparent(byte b) => b == Water;
}
