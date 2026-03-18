using System;
using System.Runtime.InteropServices;
using UnityEngine;

public enum TreeSpecies { None, Oak, Birch, Pine, Dead }

[Serializable]
[StructLayout(LayoutKind.Sequential)]
public struct TreeConfig
{
    public TreeSpecies Species;
    [Range(0f, 1f)] public float Density;
    public int MinAltitude;
    public int MaxAltitude;

    /// <summary>
    /// Default tree configs indexed by biome ID (matches BiomeDef.CreateDefaults() order):
    /// 0=DeepOcean 1=FrozenOcean 2=Ocean 3=Beach 4=Plains 5=Forest
    /// 6=Desert 7=Taiga 8=Tundra 9=Mountains
    /// </summary>
    public static TreeConfig[] CreateDefaults(int seaLevel, int snowAltitude) => new[]
    {
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.None },
        new TreeConfig { Species = TreeSpecies.Oak,  Density = 0.01f, MinAltitude = seaLevel + 1, MaxAltitude = int.MaxValue },      // Plains  — biome system bounds placement, no altitude cap
        new TreeConfig { Species = TreeSpecies.Oak,  Density = 0.06f, MinAltitude = seaLevel + 1, MaxAltitude = int.MaxValue },      // Forest (Oak/Birch split in decorator)
        new TreeConfig { Species = TreeSpecies.Dead, Density = 0.004f, MinAltitude = seaLevel + 1, MaxAltitude = int.MaxValue },     // Desert
        new TreeConfig { Species = TreeSpecies.Pine, Density = 0.05f,  MinAltitude = seaLevel + 1, MaxAltitude = int.MaxValue },     // Taiga
        new TreeConfig { Species = TreeSpecies.Dead, Density = 0.003f, MinAltitude = seaLevel + 1, MaxAltitude = int.MaxValue },     // Tundra
        new TreeConfig { Species = TreeSpecies.Pine, Density = 0.02f,  MinAltitude = seaLevel + 1, MaxAltitude = snowAltitude - 1 },// Mountains — capped at snow line
    };
}
