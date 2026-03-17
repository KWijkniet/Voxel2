using System;

[Serializable]
public struct TerrainSettingsV2
{
    public int   SeaLevel;
    public float MaxAmplitude;

    // Noise channel frequencies
    public float CScale;          // continentalness
    public float EScale;          // erosion
    public float PVScale;         // peaks & valleys
    public float TScale;          // temperature
    public float HScale;          // humidity
    public int   PVOctaves;       // ridged PV octave count

    // Rivers
    public float RiverThreshold;  // river_mask < this → river (narrower = fewer rivers)
    public float RiverMaskScale;  // river mask noise frequency
    public int   RiverCarveDepth; // voxels carved below surface for river bed
    public float RiverErosionMin; // E must be above this for rivers to appear

    // Terrace cliffs
    public float TerraceStep;        // quantization step in voxels (0 = disabled)
    public float TerraceErosionMin;  // E range where terracing applies
    public float TerraceErosionMax;

    public static TerrainSettingsV2 Default => new TerrainSettingsV2
    {
        SeaLevel         = 20,
        MaxAmplitude     = 180f,
        CScale           = 0.0003f,
        EScale           = 0.001f,
        PVScale          = 0.003f,
        TScale           = 0.0008f,
        HScale           = 0.0008f,
        PVOctaves        = 5,
        RiverThreshold   = 0.04f,
        RiverMaskScale   = 0.0006f,
        RiverCarveDepth  = 6,
        RiverErosionMin  = 0.35f,
        TerraceStep      = 0f,    // 0 = disabled; set to e.g. 8 to enable terrace cliffs
        TerraceErosionMin = 0.3f,
        TerraceErosionMax = 0.6f,
    };
}
