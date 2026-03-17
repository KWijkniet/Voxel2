using System;

/// <summary>
/// Fully unmanaged biome descriptor — safe for NativeArray and Burst jobs.
/// Display names are stored separately in <see cref="DefaultNames"/>.
/// </summary>
[Serializable]
public struct BiomeDef
{
    // Noise-space placement ranges — C ∈ [-1,1], T and H ∈ [0,1].
    // BlendX = transition half-width at each range edge (0 = hard cut).
    public float MinC, MaxC, BlendC;
    public float MinT, MaxT, BlendT;
    public float MinH, MaxH, BlendH;

    // Additive height offset applied on top of the spline result (voxels).
    public float HeightBias;

    // Block layering
    public byte SurfaceBlock;
    public byte SubSurfaceBlock;
    public int  SubSurfaceDepth;   // voxels below surface that use SubSurfaceBlock
    public byte DeepBlock;         // everything below SubSurfaceDepth

    // Altitude-based snow: surface voxel → Snow when worldY >= SnowAltitude. -1 = disabled.
    public int SnowAltitude;

    // ── Default biome list ────────────────────────────────────────────────────

    /// <summary>Display names parallel to <see cref="CreateDefaults()"/>.</summary>
    public static readonly string[] DefaultNames =
    {
        "Deep Ocean", "Frozen Ocean", "Ocean", "Beach",
        "Plains", "Forest", "Desert", "Taiga", "Tundra", "Mountains",
    };

    public static BiomeDef[] CreateDefaults() => new BiomeDef[]
    {
        // 0: Deep Ocean
        new BiomeDef
        {
            MinC = -1f, MaxC = -0.3f, BlendC = 0.05f,
            MinT = 0f,  MaxT = 1f,    BlendT = 0f,
            MinH = 0f,  MaxH = 1f,    BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Sand, SubSurfaceBlock = BlockType.Sand,
            SubSurfaceDepth = 2, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 1: Frozen Ocean
        new BiomeDef
        {
            MinC = -0.3f, MaxC = -0.1f, BlendC = 0.05f,
            MinT = 0f,    MaxT = 0.25f, BlendT = 0.05f,
            MinH = 0f,    MaxH = 1f,    BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Ice, SubSurfaceBlock = BlockType.PackedIce,
            SubSurfaceDepth = 4, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 2: Ocean
        new BiomeDef
        {
            MinC = -0.3f, MaxC = -0.1f, BlendC = 0.05f,
            MinT = 0f,    MaxT = 1f,    BlendT = 0f,
            MinH = 0f,    MaxH = 1f,    BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Sand, SubSurfaceBlock = BlockType.Stone,
            SubSurfaceDepth = 3, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 3: Beach
        new BiomeDef
        {
            MinC = -0.1f, MaxC = 0.1f, BlendC = 0.05f,
            MinT = 0f,    MaxT = 1f,   BlendT = 0f,
            MinH = 0f,    MaxH = 1f,   BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Sand, SubSurfaceBlock = BlockType.Sandstone,
            SubSurfaceDepth = 4, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 4: Plains
        new BiomeDef
        {
            MinC = 0.1f, MaxC = 1f,   BlendC = 0.05f,
            MinT = 0.3f, MaxT = 0.7f, BlendT = 0.05f,
            MinH = 0.2f, MaxH = 0.6f, BlendH = 0.05f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Grass, SubSurfaceBlock = BlockType.Dirt,
            SubSurfaceDepth = 3, DeepBlock = BlockType.Stone, SnowAltitude = 75,
        },
        // 5: Forest
        new BiomeDef
        {
            MinC = 0.1f, MaxC = 1f,   BlendC = 0.05f,
            MinT = 0.3f, MaxT = 0.7f, BlendT = 0.05f,
            MinH = 0.6f, MaxH = 1f,   BlendH = 0.05f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Grass, SubSurfaceBlock = BlockType.Dirt,
            SubSurfaceDepth = 4, DeepBlock = BlockType.Stone, SnowAltitude = 75,
        },
        // 6: Desert
        new BiomeDef
        {
            MinC = 0.1f, MaxC = 1f,   BlendC = 0.05f,
            MinT = 0.7f, MaxT = 1f,   BlendT = 0.05f,
            MinH = 0f,   MaxH = 0.5f, BlendH = 0.05f,
            HeightBias = -5f,
            SurfaceBlock = BlockType.Sand, SubSurfaceBlock = BlockType.Sandstone,
            SubSurfaceDepth = 6, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 7: Taiga
        new BiomeDef
        {
            MinC = 0.1f,  MaxC = 1f,    BlendC = 0.05f,
            MinT = 0.2f,  MaxT = 0.45f, BlendT = 0.05f,
            MinH = 0.4f,  MaxH = 1f,    BlendH = 0.05f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Snow, SubSurfaceBlock = BlockType.FrozenDirt,
            SubSurfaceDepth = 3, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 8: Tundra
        new BiomeDef
        {
            MinC = 0.1f, MaxC = 1f,    BlendC = 0.05f,
            MinT = 0f,   MaxT = 0.25f, BlendT = 0.05f,
            MinH = 0f,   MaxH = 1f,    BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Snow, SubSurfaceBlock = BlockType.FrozenDirt,
            SubSurfaceDepth = 3, DeepBlock = BlockType.Stone, SnowAltitude = -1,
        },
        // 9: Mountains
        new BiomeDef
        {
            MinC = 0.4f, MaxC = 1f, BlendC = 0.05f,
            MinT = 0f,   MaxT = 1f, BlendT = 0f,
            MinH = 0f,   MaxH = 1f, BlendH = 0f,
            HeightBias = 0f,
            SurfaceBlock = BlockType.Stone, SubSurfaceBlock = BlockType.Stone,
            SubSurfaceDepth = 10, DeepBlock = BlockType.Stone, SnowAltitude = 75,
        },
    };
}
