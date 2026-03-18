using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pre-baked voxel tree shapes. Each species has multiple height variants.
/// Height variant is selected via Hash % HeightVariantCount(species).
/// </summary>
public static class TreeShapes
{
    public struct BlockOffset
    {
        public Vector3Int pos;
        public byte block;
    }

    public static readonly BlockOffset[][] Oak;
    public static readonly BlockOffset[][] Birch;
    public static readonly BlockOffset[][] Pine;
    public static readonly BlockOffset[][] Dead;

    static TreeShapes()
    {
        Oak   = new[] { BuildOak(5),   BuildOak(6),   BuildOak(7) };
        Birch = new[] { BuildBirch(7), BuildBirch(8), BuildBirch(9) };
        Pine  = new[] { BuildPine(8),  BuildPine(9),  BuildPine(10), BuildPine(11), BuildPine(12) };
        Dead  = new[] { BuildDead(3),  BuildDead(4),  BuildDead(5) };
    }

    public static BlockOffset[] Get(TreeSpecies species, int variant) => species switch
    {
        TreeSpecies.Oak   => Oak  [variant % Oak.Length],
        TreeSpecies.Birch => Birch[variant % Birch.Length],
        TreeSpecies.Pine  => Pine [variant % Pine.Length],
        TreeSpecies.Dead  => Dead [variant % Dead.Length],
        _                 => System.Array.Empty<BlockOffset>(),
    };

    public static int HeightVariantCount(TreeSpecies species) => species switch
    {
        TreeSpecies.Oak   => Oak.Length,
        TreeSpecies.Birch => Birch.Length,
        TreeSpecies.Pine  => Pine.Length,
        TreeSpecies.Dead  => Dead.Length,
        _                 => 0,
    };

    static BlockOffset[] BuildOak(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight;
        for (int dy = -1; dy <= 2; dy++)
        {
            int radius = (dy == -1 || dy == 2) ? 2 : 3;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && dy < 0) continue;
                if (Mathf.Abs(x) + Mathf.Abs(z) > radius + 1) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, top + dy, z), block = BlockType.Leaves });
            }
        }
        return list.ToArray();
    }

    static BlockOffset[] BuildBirch(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight;
        for (int dy = -1; dy <= 2; dy++)
        {
            int radius = dy == 0 ? 2 : 1;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && dy < 0) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, top + dy, z), block = BlockType.Leaves });
            }
        }
        return list.ToArray();
    }

    static BlockOffset[] BuildPine(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int layers = trunkHeight - 2;
        for (int layer = 0; layer < layers; layer++)
        {
            int y      = trunkHeight - 1 - layer;
            int radius = Mathf.Max(1, layers - layer - 1);
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (x == 0 && z == 0 && layer == 0) continue;
                list.Add(new BlockOffset { pos = new Vector3Int(x, y, z), block = BlockType.PineNeedles });
            }
        }
        list.Add(new BlockOffset { pos = new Vector3Int(0, trunkHeight, 0), block = BlockType.PineNeedles });
        return list.ToArray();
    }

    static BlockOffset[] BuildDead(int trunkHeight)
    {
        var list = new List<BlockOffset>();
        for (int y = 0; y < trunkHeight; y++)
            list.Add(new BlockOffset { pos = new Vector3Int(0, y, 0), block = BlockType.Log });
        int top = trunkHeight - 1;
        list.Add(new BlockOffset { pos = new Vector3Int( 1, top,     0), block = BlockType.Log });
        list.Add(new BlockOffset { pos = new Vector3Int(-1, top - 1, 0), block = BlockType.Log });
        list.Add(new BlockOffset { pos = new Vector3Int( 0, top - 1, 1), block = BlockType.Log });
        return list.ToArray();
    }
}
