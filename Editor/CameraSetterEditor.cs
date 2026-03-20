using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(CameraSetter))]
public class CameraSetterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        if (GUILayout.Button("Update Camera"))
        {
            ((CameraSetter)target).UpdateCamera();
        }
    }
}