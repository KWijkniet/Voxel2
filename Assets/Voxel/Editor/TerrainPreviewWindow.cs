using UnityEngine;
using UnityEditor;
using System.Diagnostics;

/// <summary>
/// Editor window — visualises VoxelWorld terrain settings as a 2D top-down map.
/// Open via  Voxel ▸ Terrain Preview  (or Ctrl+Shift+T).
///
/// Biome mode  : each column coloured by dominant biome, shaded by relative height.
/// Height mode : classic terrain-gradient colour ramp (deep blue → sand → green → brown → white).
/// </summary>
public class TerrainPreviewWindow : EditorWindow
{
    // ── Settings ──────────────────────────────────────────────────────────────

    private VoxelWorld _world;

    private enum ViewMode { Biome, Height }
    private ViewMode _viewMode = ViewMode.Biome;

    private int _resolution = 256;       // texture size in pixels
    private int _stride     = 4;         // world voxels per pixel
    private int _centerX    = 0;
    private int _centerZ    = 0;

    // ── State ─────────────────────────────────────────────────────────────────

    private Texture2D _tex;
    private string    _statusLine = "Click Generate to build the preview.";
    private Vector2   _previewScroll;

    // ── Biome colours (match BlockType palette roughly) ───────────────────────

    private static readonly Color32 ColPlains   = new Color32( 80, 160,  50, 255);
    private static readonly Color32 ColForest   = new Color32( 34,  90,  25, 255);
    private static readonly Color32 ColDesert   = new Color32(194, 178, 128, 255);
    private static readonly Color32 ColTundra   = new Color32(180, 210, 230, 255);
    private static readonly Color32 ColMountain = new Color32(110,  95,  80, 255);
    private static readonly Color32 ColOcean    = new Color32( 30, 100, 200, 255);

    // ── Menu entry ────────────────────────────────────────────────────────────

    [MenuItem("Voxel/Terrain Preview %#t")]
    public static void Open() => GetWindow<TerrainPreviewWindow>("Terrain Preview");

    // ── GUI ───────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        DrawControls();
        GUILayout.Space(4);
        DrawPreviewArea();
    }

    private void DrawControls()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // Source world
        EditorGUILayout.BeginHorizontal();
        _world = (VoxelWorld)EditorGUILayout.ObjectField("VoxelWorld", _world, typeof(VoxelWorld), true);
        if (GUILayout.Button("Find", GUILayout.Width(46)))
            _world = FindObjectOfType<VoxelWorld>();
        EditorGUILayout.EndHorizontal();

        // View mode
        _viewMode = (ViewMode)EditorGUILayout.EnumPopup("View Mode", _viewMode);

        // Sampling
        _resolution = EditorGUILayout.IntSlider("Resolution (px)", _resolution, 64, 512);
        _stride     = EditorGUILayout.IntSlider("Voxels / Pixel",  _stride,      1,  16);

        // World size info
        int worldSpan = _resolution * _stride;
        EditorGUILayout.LabelField("World span",
            $"{worldSpan} voxels  ({worldSpan / VoxelChunk.Size} chunks)",
            EditorStyles.miniLabel);

        // Centre
        EditorGUILayout.BeginHorizontal();
        _centerX = EditorGUILayout.IntField("Centre X", _centerX);
        _centerZ = EditorGUILayout.IntField("Centre Z", _centerZ);
        if (GUILayout.Button("0,0", GUILayout.Width(36))) { _centerX = 0; _centerZ = 0; }
        EditorGUILayout.EndHorizontal();

        // Generate
        GUILayout.Space(4);
        GUI.enabled = _world != null;
        if (GUILayout.Button("Generate", GUILayout.Height(28)))
            Generate();
        GUI.enabled = true;

        // Status
        EditorGUILayout.LabelField(_statusLine, EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();
    }

    private void DrawPreviewArea()
    {
        if (_tex == null) return;

        // Scale texture to fit available width, keeping it square
        float available = position.width - 8f;
        float size      = Mathf.Min(available, position.height - 175f);
        if (size < 32f) return;

        _previewScroll = EditorGUILayout.BeginScrollView(_previewScroll);
        Rect texRect = GUILayoutUtility.GetRect(size, size);
        EditorGUI.DrawPreviewTexture(texRect, _tex, null, ScaleMode.ScaleToFit);

        // Pixel info on mouse hover
        if (texRect.Contains(Event.current.mousePosition))
        {
            Vector2 uv = (Event.current.mousePosition - texRect.position) / texRect.size;
            int px = Mathf.Clamp((int)(uv.x * _resolution), 0, _resolution - 1);
            int pz = Mathf.Clamp((int)((1f - uv.y) * _resolution), 0, _resolution - 1);
            int half = (_resolution * _stride) / 2;
            int wx = _centerX - half + px * _stride;
            int wz = _centerZ - half + pz * _stride;
            var s   = _world.GetTerrainSettings();
            int h   = TerrainGenerator.GetSurface(wx, wz, s);
            byte b  = TerrainGenerator.GetDominantBiome(wx, wz, s);
            EditorGUI.LabelField(
                new Rect(texRect.x + 4, texRect.y + 4, 300, 18),
                $"({wx}, {wz})  h={h}  {BiomeName(b)}",
                EditorStyles.whiteLabel);
            Repaint();
        }

        EditorGUILayout.EndScrollView();

        if (_viewMode == ViewMode.Biome)
            DrawLegend();
    }

    private void DrawLegend()
    {
        EditorGUILayout.BeginHorizontal();
        LegendSwatch(ColOcean,    "Ocean");
        LegendSwatch(ColPlains,   "Plains");
        LegendSwatch(ColForest,   "Forest");
        LegendSwatch(ColDesert,   "Desert");
        LegendSwatch(ColTundra,   "Tundra");
        LegendSwatch(ColMountain, "Mountain");
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private static void LegendSwatch(Color32 col, string label)
    {
        Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12));
        EditorGUI.DrawRect(r, col);
        GUILayout.Label(label, EditorStyles.miniLabel);
        GUILayout.Space(6);
    }

    // ── Generation ────────────────────────────────────────────────────────────

    private void Generate()
    {
        if (_world == null) return;

        var sw = Stopwatch.StartNew();
        var s  = _world.GetTerrainSettings();

        int res  = _resolution;
        int half = (res * _stride) / 2;

        // Allocate scratch arrays
        var heights = new int [res * res];
        var biomes  = new byte[res * res];

        int minH = int.MaxValue, maxH = int.MinValue;

        for (int z = 0; z < res; z++)
        for (int x = 0; x < res; x++)
        {
            int wx = _centerX - half + x * _stride;
            int wz = _centerZ - half + z * _stride;
            int h  = TerrainGenerator.GetSurface(wx, wz, s);
            heights[x + z * res] = h;
            biomes [x + z * res] = TerrainGenerator.GetDominantBiome(wx, wz, s);
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
        }

        // Build pixel colours
        var pixels = new Color32[res * res];
        float heightRange = Mathf.Max(1f, maxH - minH);

        for (int z = 0; z < res; z++)
        for (int x = 0; x < res; x++)
        {
            int  idx    = x + z * res;
            int  h      = heights[idx];
            byte biome  = biomes [idx];
            bool ocean  = h < s.seaLevel;

            Color32 col;
            if (_viewMode == ViewMode.Biome)
            {
                col = ocean ? ColOcean : BiomeColour(biome);
                // Shade by relative height so terrain shape is still readable
                float shade = ocean
                    ? Mathf.InverseLerp(minH, s.seaLevel, h) * 0.5f + 0.3f
                    : Mathf.InverseLerp(minH, maxH, h) * 0.55f + 0.45f;
                col = Tint(col, shade);
            }
            else // Height
            {
                col = HeightColour(h, s.seaLevel, minH, maxH);
            }

            // Texture row 0 = bottom of image, so flip Z
            pixels[x + (res - 1 - z) * res] = col;
        }

        // Apply to texture
        if (_tex == null || _tex.width != res)
        {
            if (_tex != null) DestroyImmediate(_tex);
            _tex = new Texture2D(res, res, TextureFormat.RGB24, mipChain: false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
            };
        }
        _tex.SetPixels32(pixels);
        _tex.Apply();

        sw.Stop();
        int worldSpan = res * _stride;
        _statusLine = $"{res}×{res} px  |  {worldSpan}×{worldSpan} voxels  " +
                      $"|  h [{minH}, {maxH}]  |  {sw.ElapsedMilliseconds} ms";
        Repaint();

        TerrainGenerator.ClearSurfaceCache(); // don't let preview entries leak into runtime cache
    }

    // ── Colour helpers ────────────────────────────────────────────────────────

    private static Color32 BiomeColour(byte biome) => biome switch
    {
        TerrainGenerator.BiomePlains   => ColPlains,
        TerrainGenerator.BiomeForest   => ColForest,
        TerrainGenerator.BiomeDesert   => ColDesert,
        TerrainGenerator.BiomeTundra   => ColTundra,
        TerrainGenerator.BiomeMountain => ColMountain,
        _                              => Color.magenta,
    };

    private static Color32 HeightColour(int h, int seaLevel, int minH, int maxH)
    {
        float t = Mathf.InverseLerp(minH, maxH, h);
        if (h < seaLevel)
        {
            float d = Mathf.InverseLerp(minH, seaLevel, h);
            return Color32.Lerp(new Color32(10, 40, 120, 255), new Color32(60, 140, 220, 255), d);
        }
        // Land gradient: sand → green → brown → snow
        if (t < 0.35f) return Color32.Lerp(new Color32(194, 178, 128, 255), new Color32(80, 160, 50, 255), t / 0.35f);
        if (t < 0.65f) return Color32.Lerp(new Color32(80, 160, 50, 255),   new Color32(110, 95, 80, 255), (t - 0.35f) / 0.30f);
        return             Color32.Lerp(new Color32(110, 95, 80, 255),       new Color32(240, 245, 255, 255), (t - 0.65f) / 0.35f);
    }

    private static Color32 Tint(Color32 c, float brightness)
    {
        return new Color32(
            (byte)(c.r * brightness),
            (byte)(c.g * brightness),
            (byte)(c.b * brightness),
            255);
    }

    private static string BiomeName(byte b) => b switch
    {
        TerrainGenerator.BiomePlains   => "Plains",
        TerrainGenerator.BiomeForest   => "Forest",
        TerrainGenerator.BiomeDesert   => "Desert",
        TerrainGenerator.BiomeTundra   => "Tundra",
        TerrainGenerator.BiomeMountain => "Mountain",
        _                              => "?",
    };
}
