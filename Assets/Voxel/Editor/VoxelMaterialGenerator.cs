using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Generates a pixel-art texture atlas and a URP Lit material from it.
/// Atlas layout: [Stone | Dirt | Grass | Sand | Water | Snow | Sandstone | FrozenDirt] — one texel per block type.
/// Point filtering ensures clean colour boundaries with no bleed.
/// </summary>
public static class VoxelMaterialGenerator
{
    private const string OutputFolder  = "Assets/Voxel/Generated";
    private const string TexturePath   = OutputFolder + "/VoxelAtlas.png";
    private const string MaterialPath  = OutputFolder + "/VoxelTerrain.mat";

    // One colour per solid block type (order matches BlockType: Stone=1 … FrozenDirt=8)
    private static readonly Color32[] BlockColours =
    {
        new Color32(120, 120, 120, 255), // Stone      — grey
        new Color32(139,  90,  43, 255), // Dirt       — brown
        new Color32( 67, 155,  40, 255), // Grass      — green
        new Color32(194, 178, 128, 255), // Sand       — tan
        new Color32( 30, 100, 200, 255), // Water      — blue
        new Color32(220, 235, 255, 255), // Snow       — ice white
        new Color32(210, 180,  90, 255), // Sandstone  — warm tan-orange
        new Color32(100,  95, 110, 255), // FrozenDirt — cold grey-blue
    };

    /// <summary>Generates the atlas texture and material, saves them as assets, and returns the material.</summary>
    public static Material Generate()
    {
        EnsureFolder();

        var tex = BuildAtlasTexture();
        SaveTexture(tex, TexturePath);

        var mat = BuildMaterial(AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath));
        AssetDatabase.CreateAsset(mat, MaterialPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[VoxelMaterialGenerator] Saved atlas → {TexturePath}\n" +
                  $"                         Material  → {MaterialPath}");

        return AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Texture2D BuildAtlasTexture()
    {
        int tileCount = BlockColours.Length; // == BlockType.AtlasTileCount
        var tex = new Texture2D(tileCount, 1, TextureFormat.RGBA32, mipChain: false)
        {
            filterMode = FilterMode.Point,   // hard pixel edges, no bleed between tiles
            wrapMode   = TextureWrapMode.Clamp,
            name       = "VoxelAtlas",
        };

        for (int i = 0; i < tileCount; i++)
            tex.SetPixel(i, 0, BlockColours[i]);

        tex.Apply();
        return tex;
    }

    private static void SaveTexture(Texture2D tex, string path)
    {
        byte[] png = tex.EncodeToPNG();
        File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path);

        // Disable compression / mips so the single pixels are preserved exactly
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer != null)
        {
            importer.textureType        = TextureImporterType.Default;
            importer.filterMode         = FilterMode.Point;
            importer.mipmapEnabled      = false;
            importer.wrapMode           = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
    }

    private static Material BuildMaterial(Texture2D atlas)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");

        var mat = new Material(shader) { name = "VoxelTerrain" };
        mat.mainTexture = atlas;

        // ── Force fully opaque rendering ──────────────────────────────────────
        // URP Lit created programmatically does not set these correctly by default,
        // causing ZWrite=Off / transparent blending → large triangular Z-fighting.
        if (mat.HasProperty("_Surface"))   mat.SetFloat("_Surface",  0f); // 0 = Opaque
        if (mat.HasProperty("_Blend"))     mat.SetFloat("_Blend",    0f); // Alpha mode
        if (mat.HasProperty("_SrcBlend"))  mat.SetInt("_SrcBlend",   1);  // BlendMode.One
        if (mat.HasProperty("_DstBlend"))  mat.SetInt("_DstBlend",   0);  // BlendMode.Zero
        if (mat.HasProperty("_ZWrite"))    mat.SetInt("_ZWrite",     1);  // ZWrite On
        if (mat.HasProperty("_Cull"))      mat.SetInt("_Cull",       2);  // CullMode.Back
        mat.renderQueue = 2000; // RenderQueue.Geometry

        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.EnableKeyword("_EMISSION"); // suppress shader variant warnings

        // Smooth = 0, Metallic = 0 for a matte voxel look
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic",   0f);
        return mat;
    }

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(OutputFolder))
        {
            AssetDatabase.CreateFolder("Assets/Voxel", "Generated");
            AssetDatabase.Refresh();
        }
    }
}
