public static class BlockType
{
    public const byte Air         = 0;
    public const byte Stone       = 1;
    public const byte Dirt        = 2;
    public const byte Grass       = 3;
    public const byte Sand        = 4;
    public const byte Water       = 5;
    public const byte Snow        = 6;
    public const byte Sandstone   = 7;
    public const byte FrozenDirt  = 8;
    public const byte Ice         = 9;
    public const byte PackedIce   = 10;
    public const byte Log         = 11;
    public const byte Leaves      = 12;
    public const byte PineNeedles = 13;

    // Number of atlas texture tiles. Log occupies two tiles (LogTop + LogSide).
    // Atlas layout: [Stone|Dirt|Grass|Sand|Water|Snow|Sandstone|FrozenDirt|Ice|PackedIce|LogTop|LogSide|Leaves|PineNeedles]
    public const int AtlasTileCount = 14;

    // Explicit tile indices for Log (Log does NOT follow the blockType-1 formula).
    // Log occupies two atlas slots (tiles 10 and 11), so Leaves and PineNeedles also
    // cannot use blockType-1. Use GetAtlasTile() instead of blockType-1 directly.
    public const int LogTopTile      = 10; // top/bottom faces of Log
    public const int LogSideTile     = 11; // side faces of Log
    public const int LeavesTile      = 12;
    public const int PineNeedlesTile = 13;

    /// <summary>
    /// Returns the default atlas tile index for a block type.
    /// For Log, returns LogTopTile; the mesher overrides to LogSideTile for side faces.
    /// Always prefer this over bare blockType-1 arithmetic.
    /// </summary>
    public static int GetAtlasTile(byte blockType) => blockType switch
    {
        Log         => LogTopTile,      // sides override to LogSideTile in mesher
        Leaves      => LeavesTile,
        PineNeedles => PineNeedlesTile,
        _           => blockType - 1,
    };

    /// <summary>Returns true for alpha-blended block types. Add new transparent types here.</summary>
    public static bool IsTransparent(byte b) => b == Water || b == Leaves || b == PineNeedles;
}
