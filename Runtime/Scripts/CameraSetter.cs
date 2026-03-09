using System;
using ArtificeToolkit.Attributes;
using UnityEngine;
using UnityEngine.Assertions;

[ExecuteInEditMode]
public class CameraSetter : MonoBehaviour
{
    [SerializeField] private Camera[] _cameras;
    [SerializeField, Required] private TerrainGrass _terrainGrass;
    [SerializeField] private bool _runInUpdate;

    private void OnEnable()
    {
        UpdateCamera();
    }

    private void Update()
    {
        if (!_runInUpdate) return;
        UpdateCamera();
    }

    [Button]
    private void UpdateCamera()
    {
        Assert.IsNotNull(_cameras);
        Assert.IsNotNull(_terrainGrass);
        Assert.IsTrue(_cameras.Length > 1);

        Camera cam = null;
        for (var i = 0; i < _cameras.Length; i++)
        {
            //var prev = _cameras[i - 1];
            var curr = _cameras[i];
            if (curr.enabled && curr.gameObject.activeInHierarchy) cam = curr;
        }
        
        _terrainGrass.SetCamera(cam);
    }
}