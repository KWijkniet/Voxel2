using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Generates a Texture2DArray and a VoxelTerrain material from it.
///
/// Texture lookup order per block type (BlockType constants 1–8):
///   1. Assets/Voxel/Textures/stone.png  (or dirt.png, grass.png, …)
///   2. Procedural fallback — a coloured pattern generated in code.
///
/// Drop 16×16 (or any power-of-2) PNGs into Assets/Voxel/Textures/ named
/// stone, dirt, grass, sand, water, snow, sandstone, frozendirt to use your own art.
///
/// Atlas layout: [Stone | Dirt | Grass | Sand | Water | Snow | Sandstone | FrozenDirt | Ice | PackedIce | LogTop | LogSide | Leaves | PineNeedles]
/// </summary>
public static class VoxelMaterialGenerator
{
    private const string OutputFolder        = "Assets/Voxel/Generated";
    private const string TextureArrPath     = OutputFolder + "/VoxelTexArray.asset";
    private const string MaterialPath       = OutputFolder + "/VoxelTerrain.mat";
    private const string TransMaterialPath  = OutputFolder + "/VoxelTransparent.mat";
    private const string ShaderPath         = "Voxel/VoxelTerrain";
    private const string TransShaderPath    = "Voxel/VoxelTransparent";
    private const string TextureFolder  = "Assets/Voxel/Textures";
    private const int    TileSize       = 16;

    // Name and fallback colour per block slot (index 0 = BlockType 1 = Stone)
    private static readonly (string name, Color32 fallback)[] BlockDefs =
    {
        ("stone",     new Color32(120, 120, 120, 255)),
        ("dirt",      new Color32(139,  90,  43, 255)),
        ("grass",     new Color32( 67, 155,  40, 255)),
        ("sand",      new Color32(194, 178, 128, 255)),
        ("water",     new Color32( 30, 100, 200, 255)),
        ("snow",      new Color32(220, 235, 255, 255)),
        ("sandstone", new Color32(210, 180,  90, 255)),
        ("frozendirt",new Color32(100,  95, 110, 255)),
        ("ice",         new Color32(160, 215, 240, 255)),
        ("packedice",   new Color32(100, 170, 210, 255)),
        ("logtop",      new Color32(139, 115,  75, 255)),  // tile 10 — log top/bottom face
        ("logside",     new Color32( 95,  65,  30, 255)),  // tile 11 — log bark
        ("leaves",      new Color32( 50, 130,  25, 200)),  // tile 12 — oak/birch leaves (transparent)
        ("pineneedles", new Color32( 25,  85,  20, 200)),  // tile 13 — pine needles (transparent)
    };

    /// <summary>Generates and returns (opaqueMaterial, transparentMaterial).</summary>
    public static (Material opaque, Material transparent) Generate()
    {
        EnsureFolder();

        var arr = BuildTextureArray();
        AssetDatabase.CreateAsset(arr, TextureArrPath);

        var mat      = BuildMaterial(arr, ShaderPath,      MaterialPath);
        var transMat = BuildMaterial(arr, TransShaderPath, TransMaterialPath);
        AssetDatabase.CreateAsset(mat,      MaterialPath);
        AssetDatabase.CreateAsset(transMat, TransMaterialPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[VoxelMaterialGenerator] Texture array  → {TextureArrPath}\n" +
                  $"                         Opaque mat     → {MaterialPath}\n" +
                  $"                         Transparent mat→ {TransMaterialPath}");
        return (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                AssetDatabase.LoadAssetAtPath<Material>(TransMaterialPath));
    }

    // ── Texture array ─────────────────────────────────────────────────────────

    private static Texture2DArray BuildTextureArray()
    {
        int count = BlockDefs.Length;
        var arr   = new Texture2DArray(TileSize, TileSize, count,
                                       TextureFormat.RGBA32, mipChain: true)
        {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Repeat,
            name       = "VoxelTexArray",
        };

        for (int i = 0; i < count; i++)
        {
            Texture2D tex = TryLoadTexture(BlockDefs[i].name)
                         ?? GenerateFallback(BlockDefs[i].name, BlockDefs[i].fallback);
            var pixels = tex.GetPixels32();
            arr.SetPixels32(pixels, i);
        }

        arr.Apply();
        return arr;
    }

    /// <summary>
    /// Tries to load Assets/Voxel/Textures/{name}.png.
    /// Returns null if the file is missing or can't be read.
    /// </summary>
    private static Texture2D TryLoadTexture(string name)
    {
        string path = $"{TextureFolder}/{name}.png";
        var    tex  = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex == null) return null;

        // Make sure it's readable in the editor
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Scale to TileSize if needed
        if (tex.width != TileSize || tex.height != TileSize)
        {
            var scaled = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, false);
            var src    = tex.GetPixels32();
            var dst    = new Color32[TileSize * TileSize];
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                int sx = x * tex.width  / TileSize;
                int sy = y * tex.height / TileSize;
                dst[x + y * TileSize] = src[sx + sy * tex.width];
            }
            scaled.SetPixels32(dst);
            scaled.Apply();
            return scaled;
        }

        return tex;
    }

    /// <summary>Generates a simple procedural texture with pixel-art noise.</summary>
    private static Texture2D GenerateFallback(string name, Color32 baseCol)
    {
        var tex    = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, false);
        var pixels = new Color32[TileSize * TileSize];
        var rng    = new System.Random(name.GetHashCode());
        // Transparent block types get partial alpha so the shader can blend them
        byte baseAlpha = name == "water"                          ? (byte)160
                       : name == "leaves" || name == "pineneedles" ? (byte)200
                       : (byte)255;

        for (int y = 0; y < TileSize; y++)
        for (int x = 0; x < TileSize; x++)
        {
            float v = (float)(rng.NextDouble() * 2.0 - 1.0) * Variation(name);
            var c = Tint(baseCol, 1f + v);
            pixels[x + y * TileSize] = new Color32(c.r, c.g, c.b, baseAlpha);
        }

        // Extra detail pass for specific block types
        if (name == "grass")
        {
            // Darker stripes to suggest blades
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if ((x + y * 3) % 5 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.75f);
        }
        else if (name == "stone")
        {
            // Crack lines
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if ((x * 2 + y) % 7 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.70f);
        }
        else if (name == "sandstone")
        {
            // Horizontal strata
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if (y % 4 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.78f);
        }
        else if (name == "dirt")
        {
            // Pebble dots
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if ((x * 3 + y * 7) % 11 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.65f);
        }
        else if (name == "frozendirt")
        {
            // Ice vein lines
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if ((x + y * 2) % 6 == 0)
                    pixels[x + y * TileSize] = new Color32(160, 200, 220, 255);
        }
        else if (name == "logtop")
        {
            // Concentric rings suggesting end-grain
            int half = TileSize / 2;
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                int dx = x - half, dy = y - half;
                int ring = (int)Mathf.Sqrt(dx * dx + dy * dy);
                if (ring % 3 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.72f);
            }
        }
        else if (name == "logside")
        {
            // Vertical bark lines
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if (x % 4 == 0 || x % 4 == 1)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.68f);
        }
        else if (name == "leaves")
        {
            // Scattered leaf gaps (holes of full transparency)
            var rng2 = new System.Random(name.GetHashCode() + 1);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if (rng2.NextDouble() < 0.12)
                    pixels[x + y * TileSize] = new Color32(0, 0, 0, 0);
        }
        else if (name == "pineneedles")
        {
            // Diagonal needle streaks
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                if ((x + y) % 5 == 0)
                    pixels[x + y * TileSize] = Tint(baseCol, 0.65f);
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    private static float Variation(string name) => name switch
    {
        "water" or "snow" => 0.04f,
        "sand"            => 0.07f,
        "stone"           => 0.12f,
        "sandstone"       => 0.10f,
        _                 => 0.10f,
    };

    private static Color32 Tint(Color32 c, float t) => new Color32(
        (byte)Mathf.Clamp(c.r * t, 0, 255),
        (byte)Mathf.Clamp(c.g * t, 0, 255),
        (byte)Mathf.Clamp(c.b * t, 0, 255),
        255);

    // ── Material ──────────────────────────────────────────────────────────────

    private static Material BuildMaterial(Texture2DArray arr, string shaderPath, string assetPath)
    {
        var shader = Shader.Find(shaderPath);
        if (shader == null)
        {
            Debug.LogError($"[VoxelMaterialGenerator] Shader '{shaderPath}' not found.");
            shader = Shader.Find("Universal Render Pipeline/Lit");
        }

        bool isTransparent = shaderPath == TransShaderPath;
        var mat = new Material(shader) { name = isTransparent ? "VoxelTransparent" : "VoxelTerrain" };
        mat.SetTexture("_TexArray", arr);
        mat.SetFloat("_Smoothness", 0f);
        mat.renderQueue = isTransparent ? 3000 : 2000;
        return mat;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(OutputFolder))
        {
            AssetDatabase.CreateFolder("Assets/Voxel", "Generated");
            AssetDatabase.Refresh();
        }
    }
}
