using UnityEngine;
using System.Collections;
using UnityEditor;

[CustomEditor(typeof(ChunkCalculator))]
public class ChunkCalculatorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        ChunkCalculator myTarget = (ChunkCalculator)target;

        if (GUILayout.Button("Generate")) {
            myTarget.Generate();
        }

        if (GUILayout.Button("Clear")) {
            myTarget.Clear();
        }
    }
}
