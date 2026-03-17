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
            var (mat, transMat) = VoxelMaterialGenerator.Generate();
            if (mat != null)
            {
                Undo.RecordObject(world, "Generate Voxel Material");
                world.chunkMaterial       = mat;
                world.transparentMaterial = transMat;
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
            ClearWorld(world);

        GUI.backgroundColor = Color.white;
    }

    private void GenerateWorld(VoxelWorld world)
    {
        var mat = world.chunkMaterial != null
            ? world.chunkMaterial
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));

        int view = world.viewDistance;

        // Pass 1: generate all chunk voxel data so neighbours are available during meshing
        var chunkData = new Dictionary<Vector3Int, byte[]>();
        for (int x = -view; x <= view; x++)
        for (int z = -view; z <= view; z++)
        for (int y = 0; y < world.verticalChunks; y++)
        {
            var coord = new Vector3Int(x, y, z);
            chunkData[coord] = GenerateChunkBytes(world, coord);
        }

        // Pass 2: build meshes with neighbour awareness
        int s = VoxelChunk.Size;
        foreach (var kvp in chunkData)
        {
            var coord = kvp.Key;

            var neighbours = new byte[6][];
            for (int i = 0; i < 6; i++)
            {
                var nc = coord + MeshBuilder.NeighbourDirs[i];
                chunkData.TryGetValue(nc, out neighbours[i]);
            }

            var go = new GameObject($"Chunk {coord.x},{coord.y},{coord.z}");
            go.transform.SetParent(world.transform, false);
            go.transform.localPosition = new Vector3(coord.x * s, coord.y * s, coord.z * s);

            var (meshData, _) = MeshBuilder.BuildMeshData(kvp.Value, neighbours);
            if (meshData != null)
            {
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh       = MeshBuilder.CreateMesh(meshData);
                mr.sharedMaterial   = mat;
            }

            Undo.RegisterCreatedObjectUndo(go, "Generate World");
        }

        EditorUtility.SetDirty(world);
    }

    private void ClearWorld(VoxelWorld world)
    {
        Undo.RegisterFullObjectHierarchyUndo(world.gameObject, "Clear World");
        for (int i = world.transform.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(world.transform.GetChild(i).gameObject);
        EditorUtility.SetDirty(world);
    }

    // ── Scene view gizmos ─────────────────────────────────────────────────────

    private void OnSceneGUI()
    {
        var world    = (VoxelWorld)target;
        var sceneCam = SceneView.lastActiveSceneView?.camera;
        if (sceneCam == null) return;

        var planes  = GeometryUtility.CalculateFrustumPlanes(sceneCam);
        var origin  = world.player != null ? world.player.position : world.transform.position;
        var pChunk  = new Vector3Int(
            Mathf.FloorToInt(origin.x / VoxelChunk.Size), 0,
            Mathf.FloorToInt(origin.z / VoxelChunk.Size));

        float cs      = VoxelChunk.Size;
        int   aligned = world.AlignedViewDistance;

        // LOD 0 zone
        for (int x = -aligned; x <= aligned; x++)
        for (int z = -aligned; z <= aligned; z++)
        for (int y = 0; y < world.verticalChunks; y++)
        {
            var coord  = new Vector3Int(pChunk.x + x, y, pChunk.z + z);
            var center = new Vector3(coord.x * cs + cs * .5f, coord.y * cs + cs * .5f, coord.z * cs + cs * .5f);
            bool inFrustum = GeometryUtility.TestPlanesAABB(planes, new Bounds(center, Vector3.one * cs));

            Handles.color = inFrustum ? new Color(0.2f, 0.8f, 1f, 0.4f) : new Color(1f, 0.2f, 0.2f, 0.12f);
            Handles.DrawWireCube(center, Vector3.one * cs);
        }

        // LOD 1+ region outlines
        if (world.lodLevels > 0)
        {
            var drawn = new HashSet<Vector3Int>();
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
                    var rc = new Vector3Int(playerRX + rx, lod, playerRZ + rz);
                    if (!drawn.Add(rc)) continue;

                    int baseX    = rc.x * hSize, baseZ = rc.z * hSize;
                    int nearestX = Mathf.Clamp(pChunk.x, baseX, baseX + hSize - 1);
                    int nearestZ = Mathf.Clamp(pChunk.z, baseZ, baseZ + hSize - 1);
                    int chebDist = Mathf.Max(Mathf.Abs(nearestX - pChunk.x),
                                             Mathf.Abs(nearestZ - pChunk.z));
                    if (chebDist >= outerRadius || chebDist < innerRadius) continue;

                    var center     = new Vector3(rc.x * rs + rs * .5f, 0, rc.z * rs + rs * .5f);
                    bool inFrustum = GeometryUtility.TestPlanesAABB(planes,
                                        new Bounds(center, new Vector3(rs, rs, rs)));
                    Handles.color  = inFrustum
                        ? new Color(lodColor.r, lodColor.g, lodColor.b, 0.35f)
                        : new Color(0.5f, 0.5f, 0.5f, 0.08f);
                    Handles.DrawWireCube(center, new Vector3(rs, rs * .5f, rs));
                }
            }
        }

        // Legend
        Handles.BeginGUI();
        int legendH = 22 + 18 * (3 + world.lodLevels);
        GUI.Box(new Rect(10, 10, 220, legendH), GUIContent.none);
        GUI.Label(new Rect(14, 12, 210, 18), "LOD + Frustum Preview");
        DrawLegendEntry(new Rect(14, 30, 210, 16), new Color(0.2f, 0.8f, 1f), "LOD 0 in frustum");
        DrawLegendEntry(new Rect(14, 48, 210, 16), new Color(1f,  0.2f, 0.2f), "LOD 0 culled");
        for (int lod = 1; lod <= world.lodLevels; lod++)
        {
            var col   = Color.HSVToRGB(0.08f + lod * 0.1f, 0.8f, 0.9f);
            int hSize = 1 << lod;
            DrawLegendEntry(new Rect(14, 48 + lod * 18, 210, 16), col,
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

    private static byte[] GenerateChunkBytes(VoxelWorld world, Vector3Int coord)
    {
        var settings = world.GetTerrainSettings();
        int offsetX  = coord.x * VoxelChunk.Size;
        int offsetY  = coord.y * VoxelChunk.Size;
        int offsetZ  = coord.z * VoxelChunk.Size;
        int s        = VoxelChunk.Size;
        var blocks   = new byte[VoxelChunk.VoxelCount];

        for (int z = 0; z < s; z++)
        for (int x = 0; x < s; x++)
        {
            int surface = TerrainGenerator.GetSurface(offsetX + x, offsetZ + z, settings);
            for (int y = 0; y < s; y++)
                blocks[x + y * s + z * s * s] =
                    TerrainGenerator.GetBlock(offsetX + x, offsetY + y, offsetZ + z, surface, settings);
        }
        return blocks;
    }
}
