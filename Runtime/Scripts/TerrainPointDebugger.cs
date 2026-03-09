using System;
using UnityEngine;

[ExecuteInEditMode]
public class TerrainPointDebugger : MonoBehaviour
{
    [SerializeField] private Terrain _terrain;

    private void OnDrawGizmosSelected()
    {
        if (!_terrain) return;

        var td = _terrain.terrainData;

        var x = transform.position.x / td.size.x;
        var z = transform.position.z / td.size.z;

        var normal = td.GetInterpolatedNormal(x, z);

        var height = td.GetInterpolatedHeight(x, z);

        var pos = new Vector3(transform.position.x, height, transform.position.z);
        
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(pos, pos + normal);
    }
}