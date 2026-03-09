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

        // Use the Scene view camera so the frustum reflects what you see in the Scene window
        var sceneCam = SceneView.lastActiveSceneView?.camera;
        if (sceneCam == null) return;

        var planes    = GeometryUtility.CalculateFrustumPlanes(sceneCam);
        var origin    = world.player != null ? world.player.position : world.transform.position;
        var playerChunk = new Vector3Int(
            Mathf.FloorToInt(origin.x / PaletteChunk.Size),
            0,
            Mathf.FloorToInt(origin.z / PaletteChunk.Size));

        float s              = PaletteChunk.Size;
        float bypassWorldSq  = (world.frustumBypassRadius * s) * (world.frustumBypassRadius * s);

        for (int x = -world.viewDistance; x <= world.viewDistance; x++)
        for (int z = -world.viewDistance; z <= world.viewDistance; z++)
        for (int y = 0; y < world.verticalChunks; y++)
        {
            var coord  = new Vector3Int(playerChunk.x + x, y, playerChunk.z + z);
            var center = new Vector3(coord.x * s + s * 0.5f, coord.y * s + s * 0.5f, coord.z * s + s * 0.5f);
            var bounds = new Bounds(center, Vector3.one * s);

            float  distSq      = (center - origin).sqrMagnitude;
            bool   inBypass    = distSq <= bypassWorldSq;
            bool   inFrustum   = GeometryUtility.TestPlanesAABB(planes, bounds);
            bool   wouldLoad   = inBypass || inFrustum;

            if (wouldLoad)
            {
                // Green = would be generated
                Handles.color = inBypass
                    ? new Color(0.2f, 1f, 0.2f, 0.6f)   // bright green = bypass radius
                    : new Color(0.2f, 0.8f, 1f, 0.4f);   // cyan = frustum-visible
            }
            else
            {
                // Red = culled, would not be generated
                Handles.color = new Color(1f, 0.2f, 0.2f, 0.15f);
            }

            Handles.DrawWireCube(center, Vector3.one * s);
        }

        // Legend in the top-left of the Scene view
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 200, 76);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(new Rect(14, 12, 190, 18), "Frustum Culling Preview");
        DrawLegendEntry(new Rect(14, 30, 190, 16), new Color(0.2f, 1f, 0.2f), "Bypass radius (always load)");
        DrawLegendEntry(new Rect(14, 48, 190, 16), new Color(0.2f, 0.8f, 1f), "In frustum (would load)");
        DrawLegendEntry(new Rect(14, 66, 190, 16), new Color(1f, 0.2f, 0.2f), "Culled (would skip)");
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
        var chunk = new PaletteChunk();
        int offsetX = coord.x * PaletteChunk.Size;
        int offsetY = coord.y * PaletteChunk.Size;
        int offsetZ = coord.z * PaletteChunk.Size;

        for (int z = 0; z < PaletteChunk.Size; z++)
        for (int x = 0; x < PaletteChunk.Size; x++)
        {
            int worldX = offsetX + x;
            int worldZ = offsetZ + z;

            float noise   = Mathf.PerlinNoise(worldX * world.noiseScale, worldZ * world.noiseScale);
            int   surface = world.baseHeight + Mathf.RoundToInt(noise * world.terrainHeight);

            for (int y = 0; y < PaletteChunk.Size; y++)
            {
                int worldY = offsetY + y;

                byte block;
                if      (worldY > surface)      block = BlockType.Air;
                else if (worldY == surface)     block = BlockType.Grass;
                else if (worldY >= surface - 3) block = BlockType.Dirt;
                else                            block = BlockType.Stone;

                chunk.SetBlock(x, y, z, block);
            }
        }

        return chunk;
    }
}
