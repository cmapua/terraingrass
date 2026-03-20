using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainGrass))]
public class TerrainGrassEditor : Editor
{
    private static readonly HashSet<string> _customProperties = new HashSet<string>
    {
        "_computeCullingEnabled",
        "_drawProcedural",
        "_applyBlenderTransformCorrection",
        "_orientToTerrainNormals",
        "_initialCullingEnabled"
    };

    private static readonly HashSet<string> _readonlyProperties = new HashSet<string>
    {
        "_actualDensity",
        "_maxInstancesPerTile",
        "_tileMaxDistanceSqr",
        "_tileMaxDistanceLOD0Sqr",
    };
    
    private TerrainGrass TerrainObject => target as TerrainGrass;
    
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        
        if (GUILayout.Button("Refresh"))
        {
            Debug.Log("Refresh button pressed");
            TerrainObject.Refresh();
        }

        var iter = serializedObject.GetIterator();
        var enterChildren = true;
        while (iter.NextVisible(enterChildren))
        {
            enterChildren = false;
            
            if (_customProperties.Contains(iter.name))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(iter);
                if (EditorGUI.EndChangeCheck())
                {
                    Debug.Log($"Property '{iter.name}' was changed");
                    
                    // force a sync of the target and the serialized object's states
                    // before doing a refresh
                    serializedObject.ApplyModifiedProperties();
                    
                    TerrainObject.Refresh();
                }
            }
            else if (_readonlyProperties.Contains(iter.name))
            {
                GUI.enabled = false;
                EditorGUILayout.PropertyField(iter);
                GUI.enabled = true;
            }
            else
            {
                EditorGUILayout.PropertyField(iter);
            }
        }
        
        serializedObject.ApplyModifiedProperties();
    }
}