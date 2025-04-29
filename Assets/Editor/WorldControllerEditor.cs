using UnityEngine;
using System.Collections;
using UnityEditor;

[CustomEditor(typeof(WorldController))]
public class WorldControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        WorldController myTarget = (WorldController)target;

        // if (GUILayout.Button("Generate")) {
        //     myTarget.Generate();
        // }

        // if (GUILayout.Button("Clear")) {
        //     myTarget.Clear();
        // }
    }
}
