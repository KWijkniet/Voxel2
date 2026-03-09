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
