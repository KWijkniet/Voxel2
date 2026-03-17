using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor window for previewing and tuning the V2 terrain generator.
/// Open via Window → Voxel → Terrain V2 Preview.
///
/// Shows 8 side-by-side texture maps (Height, Biome, C, E, PV, T, H, River) computed
/// from TerrainGeneratorV2 with fully editable settings and splines.
/// </summary>
public class TerrainV2PreviewWindow : EditorWindow
{
    // ── Constants ──────────────────────────────────────────────────────────────

    private const int   SidebarWidth = 280;
    private const int   MapDisplaySize = 220;   // pixels per map in the window
    private const int   MapsPerRow    = 4;

    private static readonly string[] MapLabels =
    {
        "Height", "Biome", "Continentalness (C)", "Erosion (E)",
        "Peaks & Valleys (PV)", "Temperature (T)", "Humidity (H)", "River Mask",
    };

    // Biome colors indexed by default BiomeDef order
    private static readonly Color[] BiomeColors =
    {
        new Color(0.05f, 0.15f, 0.65f),  // 0: Deep Ocean     — deep blue
        new Color(0.65f, 0.95f, 1.00f),  // 1: Frozen Ocean   — ice blue
        new Color(0.15f, 0.50f, 0.95f),  // 2: Ocean          — bright blue
        new Color(0.95f, 0.88f, 0.60f),  // 3: Beach          — sandy yellow
        new Color(0.40f, 0.85f, 0.22f),  // 4: Plains         — bright green
        new Color(0.10f, 0.55f, 0.10f),  // 5: Forest         — dark green
        new Color(0.98f, 0.85f, 0.20f),  // 6: Desert         — vivid yellow
        new Color(0.50f, 0.65f, 0.80f),  // 7: Taiga          — steel blue
        new Color(0.92f, 0.97f, 1.00f),  // 8: Tundra         — near white
        new Color(0.65f, 0.60f, 0.60f),  // 9: Mountains      — warm grey
    };

    // ── State ──────────────────────────────────────────────────────────────────

    // Preview region
    private float _centerX     = 0f;
    private float _centerZ     = 0f;
    private float _worldRadius = 512f;
    private int   _resolution  = 256;
    private bool  _autoRefresh = false;

    // Generator settings
    private TerrainSettingsV2 _settings = TerrainSettingsV2.Default;
    private Vector2[]         _cSpline;
    private Vector2[]         _eSpline;
    private BiomeDef[]        _biomes;

    // Generated textures (one per map)
    private Texture2D[] _textures;
    private float _heightMin, _heightMax;

    // Editor UI scroll / fold state
    private Vector2 _sidebarScroll;
    private Vector2 _mapsScroll;
    private bool    _settingsFold  = true;
    private bool    _cSplineFold   = true;
    private bool    _eSplineFold   = true;
    private bool[]  _biomeFolds;
    private bool    _biomesListFold = true;
    private bool    _legendFold    = true;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [MenuItem("Window/Voxel/Terrain V2 Preview")]
    public static void Open() => GetWindow<TerrainV2PreviewWindow>("Terrain V2 Preview");

    private void OnEnable()
    {
        _cSpline   = SplineUtils.DefaultContinentalnessSpline;
        _eSpline   = SplineUtils.DefaultErosionSpline;
        _biomes    = BiomeDef.CreateDefaults();
        _biomeFolds = new bool[_biomes.Length];
        _textures  = new Texture2D[MapLabels.Length];
    }

    private void OnDisable() => DestroyTextures();

    // ── GUI ───────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        // ── Top bar ──────────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Center X:", GUILayout.Width(60));
        _centerX = EditorGUILayout.FloatField(_centerX, GUILayout.Width(70));
        GUILayout.Label("Z:", GUILayout.Width(16));
        _centerZ = EditorGUILayout.FloatField(_centerZ, GUILayout.Width(70));
        GUILayout.Label("Radius:", GUILayout.Width(48));
        _worldRadius = EditorGUILayout.FloatField(_worldRadius, GUILayout.Width(60));
        GUILayout.Label("Res:", GUILayout.Width(30));
        _resolution = EditorGUILayout.IntField(_resolution, GUILayout.Width(50));
        _resolution = Mathf.Clamp(_resolution, 64, 1024);
        GUILayout.FlexibleSpace();
        _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto", EditorStyles.toolbarButton, GUILayout.Width(50));
        if (GUILayout.Button("Generate", EditorStyles.toolbarButton, GUILayout.Width(70)))
            Generate();
        EditorGUILayout.EndHorizontal();

        // ── Main body: sidebar | maps ─────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();

        // Sidebar
        EditorGUILayout.BeginVertical(GUILayout.Width(SidebarWidth));
        DrawSidebar();
        EditorGUILayout.EndVertical();

        // Thin divider
        GUILayout.Box(GUIContent.none,
            GUILayout.Width(1), GUILayout.ExpandHeight(true));

        // Maps area
        EditorGUILayout.BeginVertical();
        DrawMaps();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        if (_autoRefresh && GUI.changed) Generate();
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void DrawSidebar()
    {
        _sidebarScroll = EditorGUILayout.BeginScrollView(_sidebarScroll);

        // Settings
        _settingsFold = EditorGUILayout.Foldout(_settingsFold, "Settings", true);
        if (_settingsFold) DrawSettings();

        EditorGUILayout.Space(4);

        // C spline
        _cSplineFold = EditorGUILayout.Foldout(_cSplineFold, "Continentalness Spline (C → height offset)", true);
        if (_cSplineFold) DrawSplineEditor(ref _cSpline, "C value [-1..1]", "Height offset (voxels)");

        EditorGUILayout.Space(4);

        // E spline
        _eSplineFold = EditorGUILayout.Foldout(_eSplineFold, "Erosion Spline (E → PV scale)", true);
        if (_eSplineFold) DrawSplineEditor(ref _eSpline, "E value [0..1]", "PV scale [0..1]");

        EditorGUILayout.Space(4);

        // Biomes
        _biomesListFold = EditorGUILayout.Foldout(_biomesListFold, "Biomes", true);
        if (_biomesListFold) DrawBiomeList();

        EditorGUILayout.Space(4);

        // Legend
        _legendFold = EditorGUILayout.Foldout(_legendFold, "Biome Color Legend", true);
        if (_legendFold) DrawLegend();

        EditorGUILayout.EndScrollView();
    }

    private void DrawSettings()
    {
        EditorGUI.indentLevel++;

        _settings.SeaLevel     = EditorGUILayout.IntField("Sea Level", _settings.SeaLevel);
        _settings.MaxAmplitude = EditorGUILayout.FloatField("Max Amplitude", _settings.MaxAmplitude);

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Noise Frequencies", EditorStyles.boldLabel);
        _settings.CScale  = EditorGUILayout.FloatField("C Scale",  _settings.CScale);
        _settings.EScale  = EditorGUILayout.FloatField("E Scale",  _settings.EScale);
        _settings.PVScale = EditorGUILayout.FloatField("PV Scale", _settings.PVScale);
        _settings.TScale  = EditorGUILayout.FloatField("T Scale",  _settings.TScale);
        _settings.HScale  = EditorGUILayout.FloatField("H Scale",  _settings.HScale);
        _settings.PVOctaves = EditorGUILayout.IntField("PV Octaves", _settings.PVOctaves);
        _settings.PVOctaves  = Mathf.Clamp(_settings.PVOctaves, 1, 8);

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Rivers", EditorStyles.boldLabel);
        _settings.RiverThreshold   = EditorGUILayout.Slider("River Threshold",   _settings.RiverThreshold,   0f, 0.3f);
        _settings.RiverMaskScale   = EditorGUILayout.FloatField("River Mask Scale",   _settings.RiverMaskScale);
        _settings.RiverCarveDepth  = EditorGUILayout.IntField("River Carve Depth", _settings.RiverCarveDepth);
        _settings.RiverErosionMin  = EditorGUILayout.Slider("River Erosion Min", _settings.RiverErosionMin, 0f, 1f);

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Terrace Cliffs", EditorStyles.boldLabel);
        _settings.TerraceStep        = EditorGUILayout.FloatField("Terrace Step",        _settings.TerraceStep);
        _settings.TerraceErosionMin  = EditorGUILayout.Slider("Terrace E Min", _settings.TerraceErosionMin, 0f, 1f);
        _settings.TerraceErosionMax  = EditorGUILayout.Slider("Terrace E Max", _settings.TerraceErosionMax, 0f, 1f);

        EditorGUILayout.Space(2);
        if (GUILayout.Button("Reset to Defaults"))
        {
            _settings = TerrainSettingsV2.Default;
            _cSpline  = SplineUtils.DefaultContinentalnessSpline;
            _eSpline  = SplineUtils.DefaultErosionSpline;
            _biomes   = BiomeDef.CreateDefaults();
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Apply to VoxelWorld in Scene"))
            ApplyToVoxelWorld();

        EditorGUI.indentLevel--;
    }

    private void DrawSplineEditor(ref Vector2[] points, string xLabel, string yLabel)
    {
        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(xLabel, GUILayout.Width(120));
        EditorGUILayout.LabelField(yLabel, GUILayout.Width(120));
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < points.Length; i++)
        {
            EditorGUILayout.BeginHorizontal();
            float x = EditorGUILayout.FloatField(points[i].x, GUILayout.Width(80));
            float y = EditorGUILayout.FloatField(points[i].y, GUILayout.Width(80));
            points[i] = new Vector2(x, y);
            if (GUILayout.Button("−", GUILayout.Width(22)) && points.Length > 2)
            {
                ArrayRemoveAt(ref points, i);
                EditorGUILayout.EndHorizontal();
                break;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("+ Add Point", GUILayout.Width(100)))
        {
            var last = points[points.Length - 1];
            ArrayAppend(ref points, new Vector2(last.x + 0.1f, last.y));
        }

        EditorGUI.indentLevel--;
    }

    private void DrawBiomeList()
    {
        if (_biomeFolds == null || _biomeFolds.Length != _biomes.Length)
            _biomeFolds = new bool[_biomes.Length];

        for (int i = 0; i < _biomes.Length; i++)
        {
            var b = _biomes[i];
            string biomeName = i < BiomeDef.DefaultNames.Length ? BiomeDef.DefaultNames[i] : $"Biome {i}";
            _biomeFolds[i] = EditorGUILayout.Foldout(_biomeFolds[i], $"  {i}: {biomeName}", true);
            if (!_biomeFolds[i]) continue;

            EditorGUI.indentLevel += 2;

            EditorGUILayout.LabelField("Continentalness", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            b.MinC   = EditorGUILayout.FloatField("Min", b.MinC);
            b.MaxC   = EditorGUILayout.FloatField("Max", b.MaxC);
            b.BlendC = EditorGUILayout.FloatField("Blend", b.BlendC);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Temperature", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            b.MinT   = EditorGUILayout.FloatField("Min", b.MinT);
            b.MaxT   = EditorGUILayout.FloatField("Max", b.MaxT);
            b.BlendT = EditorGUILayout.FloatField("Blend", b.BlendT);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Humidity", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            b.MinH   = EditorGUILayout.FloatField("Min", b.MinH);
            b.MaxH   = EditorGUILayout.FloatField("Max", b.MaxH);
            b.BlendH = EditorGUILayout.FloatField("Blend", b.BlendH);
            EditorGUILayout.EndHorizontal();

            b.HeightBias      = EditorGUILayout.FloatField("Height Bias", b.HeightBias);
            b.SnowAltitude    = EditorGUILayout.IntField("Snow Altitude (-1=off)", b.SnowAltitude);
            b.SubSurfaceDepth = EditorGUILayout.IntField("Sub-surface Depth", b.SubSurfaceDepth);

            _biomes[i] = b;
            EditorGUI.indentLevel -= 2;
        }
    }

    private void DrawLegend()
    {
        EditorGUI.indentLevel++;
        int count = Mathf.Min(_biomes.Length, BiomeColors.Length);
        for (int i = 0; i < count; i++)
        {
            string n = i < BiomeDef.DefaultNames.Length ? BiomeDef.DefaultNames[i] : $"Biome {i}";
            EditorGUILayout.BeginHorizontal();
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = BiomeColors[i];
            GUILayout.Box(GUIContent.none, GUILayout.Width(16), GUILayout.Height(16));
            GUI.backgroundColor = prevBg;
            EditorGUILayout.LabelField($"{i}: {n}");
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    // ── Maps area ─────────────────────────────────────────────────────────────

    private void DrawMaps()
    {
        _mapsScroll = EditorGUILayout.BeginScrollView(_mapsScroll);

        bool anyGenerated = _textures != null && _textures[0] != null;

        if (!anyGenerated)
        {
            EditorGUILayout.Space(40);
            EditorGUILayout.HelpBox("Click Generate to render the terrain maps.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        // Draw height range info
        EditorGUILayout.LabelField($"Height range: {_heightMin:F0} – {_heightMax:F0} voxels",
            EditorStyles.miniLabel);
        EditorGUILayout.Space(4);

        // Maps in a MapsPerRow-column grid
        int total = MapLabels.Length;
        for (int row = 0; row < Mathf.CeilToInt((float)total / MapsPerRow); row++)
        {
            EditorGUILayout.BeginHorizontal();
            for (int col = 0; col < MapsPerRow; col++)
            {
                int idx = row * MapsPerRow + col;
                if (idx >= total) break;
                DrawMapCell(idx);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawMapCell(int idx)
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(MapDisplaySize + 4));
        EditorGUILayout.LabelField(MapLabels[idx], EditorStyles.centeredGreyMiniLabel,
            GUILayout.Width(MapDisplaySize));

        var tex = _textures[idx];
        if (tex != null)
        {
            var rect = GUILayoutUtility.GetRect(MapDisplaySize, MapDisplaySize,
                GUILayout.Width(MapDisplaySize), GUILayout.Height(MapDisplaySize));
            EditorGUI.DrawPreviewTexture(rect, tex);
        }

        EditorGUILayout.EndVertical();
        GUILayout.Space(6);
    }

    // ── Generation ────────────────────────────────────────────────────────────

    private void Generate()
    {
        int res = _resolution;
        if (res <= 0) return;

        // Pre-allocate sample buffer
        var samples = new TerrainGeneratorV2.ChannelSample[res * res];

        try
        {
            EditorUtility.DisplayProgressBar("Terrain V2 Preview", "Sampling channels...", 0f);
            float invRes = 1f / (res - 1);

            for (int pz = 0; pz < res; pz++)
            {
                if (pz % 16 == 0)
                    EditorUtility.DisplayProgressBar("Terrain V2 Preview",
                        $"Sampling channels... {pz}/{res}", (float)pz / res * 0.7f);

                for (int px = 0; px < res; px++)
                {
                    float wx = _centerX + (px * invRes - 0.5f) * _worldRadius * 2f;
                    float wz = _centerZ + (pz * invRes - 0.5f) * _worldRadius * 2f;

                    samples[pz * res + px] = TerrainGeneratorV2.SampleChannels(
                        Mathf.RoundToInt(wx), Mathf.RoundToInt(wz),
                        _cSpline, _eSpline, _biomes, _settings);
                }
            }

            // Find height range for normalization
            _heightMin = float.MaxValue;
            _heightMax = float.MinValue;
            foreach (var s in samples)
            {
                if (s.HeightFinal < _heightMin) _heightMin = s.HeightFinal;
                if (s.HeightFinal > _heightMax) _heightMax = s.HeightFinal;
            }
            float heightRange = Mathf.Max(_heightMax - _heightMin, 1f);

            EditorUtility.DisplayProgressBar("Terrain V2 Preview", "Building textures...", 0.75f);

            // Allocate or resize textures
            EnsureTextures(res);

            // Fill pixel arrays
            var pxHeight = new Color32[res * res];
            var pxBiome  = new Color32[res * res];
            var pxC      = new Color32[res * res];
            var pxE      = new Color32[res * res];
            var pxPV     = new Color32[res * res];
            var pxT      = new Color32[res * res];
            var pxH      = new Color32[res * res];
            var pxRiver  = new Color32[res * res];

            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];

                // Height — grayscale
                byte hv = ToByte((s.HeightFinal - _heightMin) / heightRange);
                pxHeight[i] = new Color32(hv, hv, hv, 255);

                // Biome — blended color
                Color biomeCol = Color.black;
                for (int b = 0; b < s.BiomeWeights.Length && b < BiomeColors.Length; b++)
                    biomeCol += BiomeColors[b] * s.BiomeWeights[b];
                pxBiome[i] = ToColor32(biomeCol);

                // C — grayscale, remapped [-1,1] → [0,1]
                byte cv = ToByte((s.C + 1f) * 0.5f);
                pxC[i] = new Color32(cv, cv, cv, 255);

                // E — grayscale
                byte ev = ToByte(s.E);
                pxE[i] = new Color32(ev, ev, ev, 255);

                // PV — grayscale
                byte pv = ToByte(s.PV);
                pxPV[i] = new Color32(pv, pv, pv, 255);

                // T — blue (cold) → red (hot)
                pxT[i] = ToColor32(new Color(s.T, 0f, 1f - s.T));

                // H — yellow (dry) → teal (wet)
                pxH[i] = ToColor32(Color.Lerp(
                    new Color(0.90f, 0.80f, 0.10f),
                    new Color(0.10f, 0.70f, 0.60f), s.H));

                // River — grayscale with red channels
                bool isRiver = s.RiverMask < _settings.RiverThreshold
                            && s.E > _settings.RiverErosionMin;
                if (isRiver)
                    pxRiver[i] = new Color32(200, 30, 30, 255);
                else
                {
                    byte rv = ToByte(s.RiverMask);
                    pxRiver[i] = new Color32(rv, rv, rv, 255);
                }
            }

            // Upload to textures
            _textures[0].SetPixels32(pxHeight); _textures[0].Apply();
            _textures[1].SetPixels32(pxBiome);  _textures[1].Apply();
            _textures[2].SetPixels32(pxC);      _textures[2].Apply();
            _textures[3].SetPixels32(pxE);      _textures[3].Apply();
            _textures[4].SetPixels32(pxPV);     _textures[4].Apply();
            _textures[5].SetPixels32(pxT);      _textures[5].Apply();
            _textures[6].SetPixels32(pxH);      _textures[6].Apply();
            _textures[7].SetPixels32(pxRiver);  _textures[7].Apply();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Repaint();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureTextures(int res)
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            if (_textures[i] != null && (_textures[i].width != res || _textures[i].height != res))
            {
                DestroyImmediate(_textures[i]);
                _textures[i] = null;
            }
            if (_textures[i] == null)
            {
                _textures[i] = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode   = TextureWrapMode.Clamp,
                };
            }
        }
    }

    private void DestroyTextures()
    {
        if (_textures == null) return;
        foreach (var t in _textures)
            if (t != null) DestroyImmediate(t);
        _textures = null;
    }

    private static byte ToByte(float v) =>
        (byte)(Mathf.Clamp01(v) * 255f + 0.5f);

    private static Color32 ToColor32(Color c) =>
        new Color32(ToByte(c.r), ToByte(c.g), ToByte(c.b), 255);

    private static void ArrayRemoveAt<T>(ref T[] arr, int idx)
    {
        var next = new T[arr.Length - 1];
        Array.Copy(arr, 0,       next, 0,   idx);
        Array.Copy(arr, idx + 1, next, idx, arr.Length - idx - 1);
        arr = next;
    }

    private static void ArrayAppend<T>(ref T[] arr, T item)
    {
        var next = new T[arr.Length + 1];
        Array.Copy(arr, next, arr.Length);
        next[arr.Length] = item;
        arr = next;
    }

    // ── Apply to VoxelWorld ───────────────────────────────────────────────────

    private void ApplyToVoxelWorld()
    {
        var world = FindObjectOfType<VoxelWorld>();
        if (world == null)
        {
            EditorUtility.DisplayDialog("Apply to VoxelWorld",
                "No VoxelWorld found in the current scene.", "OK");
            return;
        }

        Undo.RecordObject(world, "Apply V2 Preview Settings to VoxelWorld");

        world.v2SeaLevel          = _settings.SeaLevel;
        world.v2MaxAmplitude      = _settings.MaxAmplitude;
        world.v2CScale            = _settings.CScale;
        world.v2EScale            = _settings.EScale;
        world.v2PVScale           = _settings.PVScale;
        world.v2TScale            = _settings.TScale;
        world.v2HScale            = _settings.HScale;
        world.v2PVOctaves         = _settings.PVOctaves;
        world.v2RiverThreshold    = _settings.RiverThreshold;
        world.v2RiverMaskScale    = _settings.RiverMaskScale;
        world.v2RiverCarveDepth   = _settings.RiverCarveDepth;
        world.v2RiverErosionMin   = _settings.RiverErosionMin;
        world.v2TerraceStep       = _settings.TerraceStep;
        world.v2TerraceErosionMin = _settings.TerraceErosionMin;
        world.v2TerraceErosionMax = _settings.TerraceErosionMax;

        world.v2CSplinePoints = (Vector2[])_cSpline.Clone();
        world.v2ESplinePoints = (Vector2[])_eSpline.Clone();
        world.v2Biomes        = (BiomeDef[])_biomes.Clone();

        EditorUtility.SetDirty(world);
        Debug.Log("[TerrainV2Preview] Settings applied to VoxelWorld in scene.");
    }
}
