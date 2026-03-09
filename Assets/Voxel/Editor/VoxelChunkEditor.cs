using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(VoxelChunk))]
public class VoxelChunkEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var chunk = (VoxelChunk)target;

        EditorGUILayout.Space(8);

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Generate Chunk", GUILayout.Height(30)))
        {
            Undo.RecordObject(chunk.gameObject, "Generate Voxel Chunk");
            chunk.Generate();
            EditorUtility.SetDirty(chunk);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear Chunk", GUILayout.Height(30)))
        {
            Undo.RecordObject(chunk.gameObject, "Clear Voxel Chunk");
            chunk.Clear();
            EditorUtility.SetDirty(chunk);
        }

        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            $"Chunk size: {VoxelChunk.Size}³ voxels ({VoxelChunk.Size}m³)",
            MessageType.Info);
    }
}
