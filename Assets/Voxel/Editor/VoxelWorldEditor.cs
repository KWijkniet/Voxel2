using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(VoxelWorld))]
public class VoxelWorldEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var world = (VoxelWorld)target;

        EditorGUILayout.Space(8);

        GUI.backgroundColor = new Color(0.4f, 0.6f, 1.0f);
        if (GUILayout.Button("Generate Material", GUILayout.Height(26)))
        {
            var mat = VoxelMaterialGenerator.Generate();
            if (mat != null)
            {
                Undo.RecordObject(world, "Generate Voxel Material");
                world.chunkMaterial = mat;
                EditorUtility.SetDirty(world);
            }
        }

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Generate World", GUILayout.Height(30)))
        {
            ClearWorld(world);
            GenerateWorld(world);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear World", GUILayout.Height(30)))
        {
            ClearWorld(world);
        }

        GUI.backgroundColor = Color.white;
    }

    private void GenerateWorld(VoxelWorld world)
    {
        var mat = world.chunkMaterial != null
            ? world.chunkMaterial
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));

        int view = world.viewDistance;

        // Pass 1: generate all chunk data so neighbours are available during meshing
        var chunkData = new Dictionary<Vector3Int, PaletteChunk>();
        for (int x = -view; x <= view; x++)
        for (int z = -view; z <= view; z++)
        for (int y = 0; y < world.verticalChunks; y++)
        {
            var coord = new Vector3Int(x, y, z);
            chunkData[coord] = GenerateChunk(world, coord);
        }

        // Pass 2: build meshes with neighbour awareness
        foreach (var kvp in chunkData)
        {
            var coord = kvp.Key;
            var go = new GameObject($"Chunk {coord.x},{coord.y},{coord.z}");
            go.transform.SetParent(world.transform, false);
            go.transform.localPosition = new Vector3(
                coord.x * PaletteChunk.Size,
                coord.y * PaletteChunk.Size,
                coord.z * PaletteChunk.Size);

            var renderer = go.AddComponent<ChunkRenderer>();
            renderer.Render(kvp.Value, coord,
                c => chunkData.TryGetValue(c, out var n) ? n : null, mat);

            Undo.RegisterCreatedObjectUndo(go, "Generate World");
        }

        EditorUtility.SetDirty(world);
    }

    private void ClearWorld(VoxelWorld world)
    {
        Undo.RegisterFullObjectHierarchyUndo(world.gameObject, "Clear World");

        // Destroy all child GameObjects
        for (int i = world.transform.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(world.transform.GetChild(i).gameObject);

        EditorUtility.SetDirty(world);
    }

    // ── Scene view gizmos ─────────────────────────────────────────────────────

    private void OnSceneGUI()
    {
        var world = (VoxelWorld)target;

        var sceneCam = SceneView.lastActiveSceneView?.camera;
        if (sceneCam == null) return;

        var planes   = GeometryUtility.CalculateFrustumPlanes(sceneCam);
        var origin   = world.player != null ? world.player.position : world.transform.position;
        var pChunk   = new Vector3Int(
            Mathf.FloorToInt(origin.x / PaletteChunk.Size), 0,
            Mathf.FloorToInt(origin.z / PaletteChunk.Size));

        float cs      = PaletteChunk.Size;
        int   aligned = world.AlignedViewDistance;
        int   maxR    = aligned * (1 << world.lodLevels);

        // ── LOD 0 zone: individual chunks ─────────────────────────────────────
        for (int x = -aligned; x <= aligned; x++)
        for (int z = -aligned; z <= aligned; z++)
        for (int y = 0; y < world.verticalChunks; y++)
        {
            var coord  = new Vector3Int(pChunk.x + x, y, pChunk.z + z);
            var center = new Vector3(coord.x * cs + cs * 0.5f, coord.y * cs + cs * 0.5f, coord.z * cs + cs * 0.5f);
            bool inFrustum = GeometryUtility.TestPlanesAABB(planes, new Bounds(center, Vector3.one * cs));

            Handles.color = inFrustum
                ? new Color(0.2f, 0.8f, 1f, 0.4f)
                : new Color(1f, 0.2f, 0.2f, 0.12f);
            Handles.DrawWireCube(center, Vector3.one * cs);
        }

        // ── LOD 1+ zone: region outlines (one ring per LOD level) ────────────
        if (world.lodLevels > 0)
        {
            var drawnRegions = new System.Collections.Generic.HashSet<Vector3Int>();

            for (int lod = 1; lod <= world.lodLevels; lod++)
            {
                int   hSize       = 1 << lod;
                float rs          = cs * hSize;
                int   innerRadius = aligned * (1 << (lod - 1));
                int   outerRadius = aligned * (1 << lod);
                int   playerRX    = Mathf.FloorToInt(pChunk.x / (float)hSize);
                int   playerRZ    = Mathf.FloorToInt(pChunk.z / (float)hSize);
                int   maxRegionR  = outerRadius / hSize + 1;
                var   lodColor    = Color.HSVToRGB(0.08f + lod * 0.1f, 0.8f, 0.9f);

                for (int rx = -maxRegionR; rx <= maxRegionR; rx++)
                for (int rz = -maxRegionR; rz <= maxRegionR; rz++)
                {
                    var regionCoord = new Vector3Int(playerRX + rx, lod, playerRZ + rz);
                    if (!drawnRegions.Add(regionCoord)) continue;

                    int baseX    = regionCoord.x * hSize;
                    int baseZ    = regionCoord.z * hSize;
                    int nearestX = Mathf.Clamp(pChunk.x, baseX, baseX + hSize - 1);
                    int nearestZ = Mathf.Clamp(pChunk.z, baseZ, baseZ + hSize - 1);
                    int chebDist = Mathf.Max(Mathf.Abs(nearestX - pChunk.x),
                                             Mathf.Abs(nearestZ - pChunk.z));

                    if (chebDist >= outerRadius || chebDist < innerRadius) continue;

                    var  center    = new Vector3(regionCoord.x * rs + rs * 0.5f, 0, regionCoord.z * rs + rs * 0.5f);
                    bool inFrustum = GeometryUtility.TestPlanesAABB(planes,
                        new Bounds(center, new Vector3(rs, rs, rs)));

                    Handles.color = inFrustum
                        ? new Color(lodColor.r, lodColor.g, lodColor.b, 0.35f)
                        : new Color(0.5f, 0.5f, 0.5f, 0.08f);
                    Handles.DrawWireCube(center, new Vector3(rs, rs * 0.5f, rs));
                }
            }
        }

        // ── Legend ────────────────────────────────────────────────────────────
        Handles.BeginGUI();
        int legendH = 22 + 18 * (3 + world.lodLevels);
        var rect = new Rect(10, 10, 220, legendH);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(new Rect(14, 12, 210, 18), "LOD + Frustum Preview");
        DrawLegendEntry(new Rect(14, 30, 210, 16), new Color(0.2f, 1f, 0.2f),  "Bypass radius (LOD 0)");
        DrawLegendEntry(new Rect(14, 48, 210, 16), new Color(0.2f, 0.8f, 1f),  "LOD 0 in frustum");
        DrawLegendEntry(new Rect(14, 66, 210, 16), new Color(1f, 0.2f, 0.2f),  "LOD 0 culled");
        for (int lod = 1; lod <= world.lodLevels; lod++)
        {
            var col   = Color.HSVToRGB(0.08f + lod * 0.1f, 0.8f, 0.9f);
            int hSize = 1 << lod;
            DrawLegendEntry(new Rect(14, 66 + lod * 18, 210, 16), col,
                $"Region LOD {lod} ({hSize}×{hSize} chunks, step {hSize})");
        }
        Handles.EndGUI();
    }

    private static void DrawLegendEntry(Rect rect, Color colour, string label)
    {
        EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, 12, 12), colour);
        GUI.Label(new Rect(rect.x + 16, rect.y, rect.width - 16, rect.height), label);
    }

    // ── Chunk data generation ─────────────────────────────────────────────────

    private PaletteChunk GenerateChunk(VoxelWorld world, Vector3Int coord)
    {
        var chunk    = new PaletteChunk();
        var settings = world.GetTerrainSettings();
        int offsetX  = coord.x * PaletteChunk.Size;
        int offsetY  = coord.y * PaletteChunk.Size;
        int offsetZ  = coord.z * PaletteChunk.Size;

        for (int z = 0; z < PaletteChunk.Size; z++)
        for (int x = 0; x < PaletteChunk.Size; x++)
        {
            int surface = TerrainGenerator.GetSurface(offsetX + x, offsetZ + z, settings);
            for (int y = 0; y < PaletteChunk.Size; y++)
            {
                byte block = TerrainGenerator.GetBlock(offsetX + x, offsetY + y, offsetZ + z, surface, settings);
                if (block != BlockType.Air)
                    chunk.SetBlock(x, y, z, block);
            }
        }
        return chunk;
    }
}
