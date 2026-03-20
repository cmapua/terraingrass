//using ArtificeToolkit.Attributes;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static System.Runtime.InteropServices.Marshal;

// TODO remove this
// ReSharper disable Unity.PreferAddressByIdToGraphicsParams

[ExecuteInEditMode]
public class TerrainGrass : MonoBehaviour
{
    #region Members
    
    struct GrassData
    {
        public Vector3 Position;
        /// <summary>
        /// Terrain normal at position.
        /// </summary>
        public Vector3 Normal;
        /// <summary>
        /// Alpha map value at given position, in 0-1 range.
        /// </summary>
        public float Alpha;
    }

    struct GrassInstanceData
    {
        public Matrix4x4 TransformMatrix;
        public Vector3 Normal;
    }

    struct GrassTile
    {
        /// <summary>
        /// Stores grass data generated from <see cref="TerrainGrass.TileInit"/>.
        /// </summary>
        public GrassData[] TileGrassData;
        
        /// <summary>
        /// The actual number of instances after the initial culling. If initial culling
        /// is disabled, will just be equal to <see cref="TerrainGrass.MaxInstancesPerTile"/>.
        /// </summary>
        public int InstanceCount;
        
        // Buffers
        /// <summary>
        /// GPU buffer for <see cref="GrassData"/>. Fed to compute shader only.
        /// </summary>
        public GraphicsBuffer GrassBuffer;
        
        /// <summary>
        /// GPU buffer for <see cref="GrassInstanceData"/>. Output from compute, fed into cull.
        /// </summary>
        public GraphicsBuffer GrassInstanceBuffer;
        
        /// <summary>
        /// GPU buffer for <see cref="GrassInstanceData"/>. Output from cull, fed into draw.
        /// </summary>
        public GraphicsBuffer GrassInstanceBufferCulled;
        
        /// <summary>
        /// fed into cull and used when drawing.
        /// </summary>
        public GraphicsBuffer IndirectArgsBuffer;
        
        // Draw Variables
        public MaterialPropertyBlock MaterialPropertyBlock;
        public Bounds Bounds;
    }

    private Terrain _t;
    private GrassTile[] _tiles;
    private Plane[] _frustumPlanes = new Plane[6];
    #if UNITY_EDITOR
    private Camera _editorCamera;
    #endif

    ///////////////////// 
    // Main Settings
    ///////////////////// 
    [Header("Terrain Settings")]
    [SerializeField] private int _grassTerrainLayer;
    [SerializeField, Range(0, 1)] private float _grassMinimumAlpha = 0.5f;
    
    [Header("Settings")]
    [SerializeField, /*OnValueChanged(nameof(Refresh)),*/ Range(1, 32)] private int _tileDivisions = 16;
    [SerializeField, /*OnValueChanged(nameof(Refresh)),*/ Range(0, 7)] private int _density = 6;
    [SerializeField] private float _maxDistance = 20f;
    [SerializeField] private float _maxDistanceLOD0 = 10f;
    [SerializeField] private bool _computeCullingEnabled = true;
    [SerializeField] private bool _perTileDistanceCullingEnabled = true;
    [SerializeField] private bool _perTileFrustumCullingEnabled = true;
    [SerializeField] private bool _drawProcedural;

    public enum LODMode
    {
        Sphere,
        Box,
    }

    [SerializeField] private LODMode _lodMode;

    // cached (readonly) values
    [SerializeField] private int _actualDensity;
    [SerializeField] private int _maxInstancesPerTile;
    [SerializeField] private float _tileMaxDistanceSqr;
    [SerializeField] private float _tileMaxDistanceLOD0Sqr;
    
    private int ActualDensity => _actualDensity;
    private int MaxInstancesPerTile => _maxInstancesPerTile;
    
    ///////////////////// 
    // Compute Shader
    ///////////////////// 
    [Header("Compute Shader")]
    [SerializeField /*, Required*/] private ComputeShader _computeShader;
    [SerializeField /*, Required*/] private ComputeShader _cullShader;
    [SerializeField /*, Required*/] private Camera _camera;
    [SerializeField] private float _minBladeHeight = 0.5f;
    [SerializeField] private float _maxBladeHeight = 1f;
    [SerializeField] private float _minBladeOffset = -0.25f;
    [SerializeField] private float _maxBladeOffset = 0.25f;
    [SerializeField] private float _bladeScale = 1f;
    [SerializeField] private bool _applyBlenderTransformCorrection;
    [SerializeField] private bool _orientToTerrainNormals;
    [SerializeField] private bool _initialCullingEnabled;

    private int _voteKernel, _scanInstanceKernel, _scanGroupKernel, _compactKernel;
    private int _voteThreadGroupSize, _cullThreadGroupSize, _scanGroupThreadGroupSize;
    private GraphicsBuffer _voteBuffer, _scanBuffer, _groupSumBuffer, _scannedGroupSumBuffer;
    private bool _computeInitialized;
    
    /////////////////////
    // Drawing
    /////////////////////
    [Header("Drawing")] 
    [SerializeField] private bool _drawingEnabled = true;
    [SerializeField] private bool _useTerrainNormals;
    [SerializeField] private Mesh _grassMesh, _grassMeshLOD;
    [SerializeField] private Material _grassProceduralMaterial;
    [SerializeField] private Material _grassInstancedIndirectMaterial;

    private bool _drawInitialized;
    // draw indirect variables
    private uint[] _indirectArgs, _indirectArgsLOD;
    // draw procedural buffers
    private GraphicsBuffer _grassVertexBuffer;
    private GraphicsBuffer _grassNormalBuffer;
    private GraphicsBuffer _grassTriangleBuffer;
    private GraphicsBuffer _grassUVBuffer;

    /////////////////////
    // Debug
    /////////////////////
    [Header("Debug")] 
    [SerializeField] private bool _debugGizmosEnabled;
    [SerializeField] private bool _debugInstanceGizmosEnabled = true;
    [SerializeField] private bool _debugLODGizmosEnabled = true;
    [SerializeField] private float _debugNormalsLength = 1f;
    [SerializeField] private Color _activeTile = Color.green;
    [SerializeField] private Color _inactiveTile = new Color(0.5f, 0.5f, 0.5f, 0.25f);
    [SerializeField] private Color _debugLODGizmosColor = Color.yellow;
    [SerializeField] private Color _debugInstanceGizmosColor = Color.cyan;
    [SerializeField, Range(0, 1)] private float _debugTileIndex;
    
    #endregion
    
    #region MonoBehaviour Lifetime
    
    private void OnEnable()
    {
#if UNITY_EDITOR
        _editorCamera = SceneView.currentDrawingSceneView?.camera;
#endif
        Refresh();
    }

    private void OnDisable()
    {
        CleanupAll();
        _t = null;
#if UNITY_EDITOR
        _editorCamera = null;
#endif
    }

    private void OnDrawGizmos()
    {
        if (!_debugGizmosEnabled) return;
        if (!_t) return;
        

        if (_tiles is {Length: > 0})
        {
            var activeTileIndex = Mathf.FloorToInt(_debugTileIndex * (_tiles.Length - 1)); //Mathf.Clamp(_debugTileIndex, 0, _tiles.Length - 1);
            var cam = _camera;
            var camPos = cam.transform.position;
            var debugPlanes = new Plane[6];
            
            if (cam && _debugLODGizmosEnabled)
            {
                Gizmos.color = _debugLODGizmosColor;
                Gizmos.DrawWireSphere(camPos, _maxDistance);
                switch (_lodMode)
                {
                    case LODMode.Sphere:
                        Gizmos.DrawWireSphere(camPos, _maxDistanceLOD0);
                        break;
                    case LODMode.Box:
                        var boundsLOD0 = new Bounds(camPos, Vector3.one * _maxDistanceLOD0);
                        Gizmos.DrawWireCube(boundsLOD0.center, boundsLOD0.size);
                        break;
                }
            }
            
            // draw per-tile gizmos
            for (int i = 0; i < _tiles.Length; i++)
            {
                var tile = _tiles[i];
                
                var visible = true;
                if (cam && _perTileDistanceCullingEnabled)
                {
                    var distance = (camPos - tile.Bounds.center).sqrMagnitude;
                    visible = distance < _tileMaxDistanceSqr;
                }
                if (cam && _perTileFrustumCullingEnabled)
                {
                    GeometryUtility.CalculateFrustumPlanes(cam, debugPlanes);
                    visible = visible && GeometryUtility.TestPlanesAABB(debugPlanes, tile.Bounds);
                }

                visible = i == activeTileIndex;

                if (!visible) continue;
                
                Gizmos.color = i == activeTileIndex ? _activeTile : _inactiveTile;
                Gizmos.DrawWireCube(tile.Bounds.center, tile.Bounds.size);
            }

            // draw per-instance gizmos in currently active tile.
            if (_debugInstanceGizmosEnabled)
            {
                var activeTile = _tiles[activeTileIndex];
                Gizmos.color = _debugInstanceGizmosColor;
                for (var i = 0; i < activeTile.TileGrassData.Length; i++)
                {
                    var gd = activeTile.TileGrassData[i];
                    var p = gd.Position;
                    var n = gd.Normal;
                    //var c = gd.Color;
                    var a = gd.Alpha;
                
                    //Gizmos.color = c;
                    Gizmos.DrawLine(p, p + n * (a * _debugNormalsLength));
                }
            }
        }
    }

    private void Update()
    {
        DrawUpdate();
    }
    
    #endregion
    
    #region API

    /// <summary>
    /// Sets the camera used by the system.
    /// </summary>
    public void SetCamera(Camera c)
    {
        _camera = c;
    }

    /// <summary>
    /// Sets the maximum draw distance for grass.
    /// </summary>
    public void SetMaxDrawDistance(float distance)
    {
        _maxDistance = distance;
    }
    
    /// <summary>
    /// Refresh the system.
    /// </summary>
    public void Refresh()
    {
        Debug.Log("Refresh called");
        
        CleanupAll();
        
        if (!_t) _t = GetComponent<Terrain>();
        if (!_t)
        {
            Debug.LogError("Unable to find Terrain");
            return;
        }

        if (!_grassMesh)
        {
            Debug.LogError("No grass mesh assigned");
            return;
        }

        if ((_drawProcedural && !_grassProceduralMaterial) ||
            (!_drawProcedural && !_grassInstancedIndirectMaterial))
        {
            Debug.LogError("No material assigned for the selected draw mode");
            return;
        }
        
        InitAll();
    }

    #endregion

    #region System
    
    private void InitAll()
    {
        TileInit(_t.terrainData);
        IndirectArgsInit();
        ComputeInit();
        CullInit();
        DrawInit();
    }
    
    private void CleanupAll()
    {
        DrawCleanup();
        CullCleanup();
        ComputeCleanup();
        TileCleanup();
    }
    
    private void IndirectArgsInit()
    {
        _indirectArgs = new uint[]
        {
            _grassMesh.GetIndexCount(0),
            0,
            _grassMesh.GetIndexStart(0),
            _grassMesh.GetBaseVertex(0),
            0
        };

        if (_grassMeshLOD)
        {
            _indirectArgsLOD = new uint[]
            {
                _grassMeshLOD.GetIndexCount(0),
                0,
                _grassMeshLOD.GetIndexStart(0),
                _grassMeshLOD.GetBaseVertex(0),
                0
            };
        }
    }

    private void TileInit(TerrainData terrainData)
    {
        var terrainWidth = terrainData.size.x;
        var terrainLength = terrainData.size.z;
        var tileWidth = terrainWidth / _tileDivisions;
        var tileLength = terrainLength / _tileDivisions;
        var halfWidth = tileWidth * 0.5f;
        var halfLength = tileLength * 0.5f;
        var instanceOffsetX = tileWidth / ActualDensity;
        var instanceOffsetZ = tileLength / ActualDensity;
        var halfInstanceOffsetX = instanceOffsetX * 0.5f;
        var halfInstanceOffsetZ = instanceOffsetZ * 0.5f;
        var alphaW = terrainData.alphamapWidth / _tileDivisions;
        var alphaH = terrainData.alphamapHeight / _tileDivisions;

        _actualDensity = 1 << _density;
        _maxInstancesPerTile = _actualDensity * _actualDensity;
        
        _tiles = new GrassTile[_tileDivisions * _tileDivisions];
        
        for (int x = 0; x < _tileDivisions; x++)
        {
            for (int z = 0; z < _tileDivisions; z++)
            {
                var worldOffsetX = x * tileWidth;
                var worldOffsetZ = z * tileLength;
                var worldCenterX = worldOffsetX + halfWidth;
                var worldCenterZ = worldOffsetZ + halfLength;
                var normalizedX = worldCenterX / terrainWidth;
                var normalizedZ = worldCenterZ / terrainLength;
                
                var sampledHeight = terrainData.GetInterpolatedHeight(normalizedX, normalizedZ);
                var tileAlpha = terrainData.GetAlphamaps(x * alphaW, z * alphaH, alphaW, alphaH);
                
                var center = new Vector3(
                    worldCenterX, 
                    sampledHeight,
                    worldCenterZ);
                var size = new Vector3(tileWidth, tileWidth, tileLength);
                var bounds = new Bounds(center, size);
                
                // density
                var instancesPerTile = MaxInstancesPerTile;
                var grassData = new GrassData[instancesPerTile];
                for (int xInstance = 0; xInstance < ActualDensity; xInstance++)
                {
                    for (int zInstance = 0; zInstance < ActualDensity; zInstance++)
                    {
                        var instanceWorldOffsetX = xInstance * instanceOffsetX + worldOffsetX;
                        var instanceWorldOffsetZ = zInstance * instanceOffsetZ + worldOffsetZ;
                        var instanceWorldPosX = instanceWorldOffsetX + halfInstanceOffsetX;
                        var instanceWorldPosZ = instanceWorldOffsetZ + halfInstanceOffsetZ;

                        var instanceNormalizedX = instanceWorldPosX / terrainWidth;
                        var instanceNormalizedZ = instanceWorldPosZ / terrainLength;
                        
                        var instanceWorldPosY = terrainData.GetInterpolatedHeight(
                            instanceNormalizedX,
                            instanceNormalizedZ);
                        
                        var normal = terrainData.GetInterpolatedNormal(
                            instanceNormalizedX,
                            instanceNormalizedZ);
                        
                        // alpha map value
                        // NOTE: coordinates are in alpha map space (0, 0, width, height)
                        var alphaX = Mathf.FloorToInt((xInstance / (float)ActualDensity) * alphaW);
                        var alphaZ = Mathf.FloorToInt((zInstance / (float)ActualDensity) * alphaH);
                        
                        grassData[xInstance * ActualDensity + zInstance] = new GrassData
                        {
                            Position = new Vector3(instanceWorldPosX, instanceWorldPosY, instanceWorldPosZ),
                            Normal = normal,
                            Alpha = tileAlpha[alphaZ, alphaX, _grassTerrainLayer]
                        };
                    }
                }
                
                // initial culling
                if (_initialCullingEnabled)
                {
                    var indices = new List<int>();
                    for (int i = 0; i < instancesPerTile; i++)
                    {
                        if (grassData[i].Alpha < _grassMinimumAlpha) continue;
                        
                        indices.Add(i);
                    }

                    instancesPerTile = indices.Count;
                    var culledData = new GrassData[instancesPerTile];
                    for (int i = 0; i < instancesPerTile; i++)
                    {
                        culledData[i] = grassData[indices[i]];
                    }

                    grassData = culledData;
                }

                _tiles[x * _tileDivisions + z] = new GrassTile
                {
                    TileGrassData = grassData,
                    InstanceCount = instancesPerTile,
                    Bounds = bounds,
                    
                    GrassBuffer = instancesPerTile == 0 ? null : new GraphicsBuffer(GraphicsBuffer.Target.Structured, instancesPerTile, SizeOf<GrassData>()),
                    GrassInstanceBuffer = instancesPerTile == 0 ? null : new GraphicsBuffer(GraphicsBuffer.Target.Structured, instancesPerTile, SizeOf<GrassInstanceData>()),
                    GrassInstanceBufferCulled = instancesPerTile == 0 ? null : new GraphicsBuffer(GraphicsBuffer.Target.Structured, instancesPerTile, SizeOf<GrassInstanceData>()),
                    IndirectArgsBuffer = instancesPerTile == 0 ? null : new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint) * 5)
                };
            }
        }
    }

    private void TileCleanup()
    {
        if (_tiles is { Length: > 0 })
        {
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i].TileGrassData = null;
                _tiles[i].MaterialPropertyBlock = null;
                
                _tiles[i].GrassBuffer?.Dispose();
                _tiles[i].GrassBuffer = null;
                
                _tiles[i].GrassInstanceBuffer?.Dispose();
                _tiles[i].GrassInstanceBuffer = null;
                
                _tiles[i].GrassInstanceBufferCulled?.Dispose();
                _tiles[i].GrassInstanceBufferCulled = null;
                
                _tiles[i].IndirectArgsBuffer?.Dispose();
                _tiles[i].IndirectArgsBuffer = null;
            }
        }

        _tiles = null;
    }
    
    #endregion
    
    #region Compute
    
    private void ComputeInit()
    {
        if (_computeInitialized)
        {
            Debug.LogWarning("Compute Shader already initialized, but ComputeInit() called again; please investigate!");
            return;
        }

        _computeInitialized = false;
        if (!_computeShader) return;

        // get kernel and thread group size.
        var kernel = _computeShader.FindKernel("CSMain");
        _computeShader.GetKernelThreadGroupSizes(kernel, out var threadGroupSize, out _, out _);
        
        // Bind global variables to the compute shader.
        _computeShader.SetMatrix("_TerrainObjectToWorld", transform.localToWorldMatrix);
        _computeShader.SetFloat("_MinBladeHeight", _minBladeHeight);
        _computeShader.SetFloat("_MaxBladeHeight", _maxBladeHeight);
        _computeShader.SetFloat("_MinOffset", _minBladeOffset);
        _computeShader.SetFloat("_MaxOffset", _maxBladeOffset);
        _computeShader.SetFloat("_Scale", _bladeScale);
        _computeShader.SetBool("_ApplyBlenderTransformCorrection", _applyBlenderTransformCorrection);
        _computeShader.SetBool("_OrientToTerrainNormal", _orientToTerrainNormals);
        
        for (int i = 0; i < _tiles.Length; i++)
        {
            var instancesPerTile = _tiles[i].InstanceCount;
            
            // NOTE: if _initialCullingEnabled is true and a tile is small enough, it could have 0 instances.
            if (instancesPerTile == 0) continue;
            
            // grass data (Input only)
            _tiles[i].GrassBuffer.SetData(_tiles[i].TileGrassData);

            // Bind per-tile buffers and variables
            _computeShader.SetBuffer(kernel, "_GrassBufferIN", _tiles[i].GrassBuffer);
            _computeShader.SetBuffer(kernel, "_GrassInstanceDataOUT", _tiles[i].GrassInstanceBuffer);
            _computeShader.SetInt("_InstanceCount", instancesPerTile);
            
            // Run the compute shader's kernel function.
            var threadGroups = Mathf.CeilToInt(instancesPerTile / (float)threadGroupSize);
            _computeShader.Dispatch(kernel, threadGroups, 1, 1);
        }

        _computeInitialized = true;
        //Debug.Log("Compute initialized.");
    }

    private void ComputeCleanup()
    {
        _computeInitialized = false;
    }

    private void CullInit()
    {
        // get kernels
        _voteKernel = _cullShader.FindKernel("Vote");
        _scanInstanceKernel = _cullShader.FindKernel("Scan");
        _scanGroupKernel = _cullShader.FindKernel("ScanGroupSums");
        _compactKernel = _cullShader.FindKernel("Compact");

        // get thread group sizes
        var maxInstancesPerTile = MaxInstancesPerTile;
        _cullThreadGroupSize = Mathf.CeilToInt(maxInstancesPerTile / 128f);
        
        _cullShader.GetKernelThreadGroupSizes(_voteKernel, out var voteThreadGroups, out _, out _);
        _cullShader.GetKernelThreadGroupSizes(_scanGroupKernel, out var scanThreadGroups, out _, out _);
        _voteThreadGroupSize = Mathf.CeilToInt(maxInstancesPerTile / (float)voteThreadGroups);
        _scanGroupThreadGroupSize = Mathf.CeilToInt(maxInstancesPerTile / (float)scanThreadGroups);
        
        // initialize buffers
        _voteBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxInstancesPerTile, sizeof(uint));
        _scanBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxInstancesPerTile, sizeof(uint));
        _groupSumBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxInstancesPerTile, sizeof(uint));
        _scannedGroupSumBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxInstancesPerTile, sizeof(uint));
    }

    private void CullCleanup()
    {
        _voteBuffer?.Dispose();
        _voteBuffer = null;
        
        _scanBuffer?.Dispose();
        _scanBuffer = null;
        
        _groupSumBuffer?.Dispose();
        _groupSumBuffer = null;
        
        _scannedGroupSumBuffer?.Dispose();
        _scannedGroupSumBuffer = null;
    }
    
    private void CullTile(ref GrassTile tile, ref Matrix4x4 vp, uint[] indirectArgs)
    {
        if (!_computeCullingEnabled)
        {
            indirectArgs[1] = (uint)MaxInstancesPerTile;
            tile.IndirectArgsBuffer.SetData(indirectArgs);
            return;
        }
        
        // reset args
        tile.IndirectArgsBuffer.SetData(indirectArgs);
        
        // global variables
        _cullShader.SetVector("_CameraPosition", _camera.transform.position);
        _cullShader.SetMatrix("_MatrixVP", vp);
        _cullShader.SetFloat("_Distance", _maxDistance);
        _cullShader.SetInt("_GroupNum", _cullThreadGroupSize);
        
        // run vote
        _cullShader.SetBuffer(_voteKernel, "_TransformMatricesIN", tile.GrassInstanceBuffer);
        _cullShader.SetBuffer(_voteKernel, "_VoteBuffer", _voteBuffer);
        _cullShader.Dispatch(_voteKernel, _voteThreadGroupSize, 1, 1);
        
        // run scan instances
        _cullShader.SetBuffer(_scanInstanceKernel, "_VoteBuffer", _voteBuffer);
        _cullShader.SetBuffer(_scanInstanceKernel, "_ScanBuffer", _scanBuffer);
        _cullShader.SetBuffer(_scanInstanceKernel, "_GroupSumArray", _groupSumBuffer);
        _cullShader.Dispatch(_scanInstanceKernel, _cullThreadGroupSize, 1, 1);
        
        // run scan groups
        _cullShader.SetBuffer(_scanGroupKernel, "_GroupSumArrayIn", _groupSumBuffer);
        _cullShader.SetBuffer(_scanGroupKernel, "_GroupSumArrayOut", _scannedGroupSumBuffer);
        _cullShader.Dispatch(_scanGroupKernel, _scanGroupThreadGroupSize, 1, 1);
        
        // run compact
        _cullShader.SetBuffer(_compactKernel, "_TransformMatricesIN", tile.GrassInstanceBuffer);
        _cullShader.SetBuffer(_compactKernel, "_TransformMatricesOUT", tile.GrassInstanceBufferCulled);
        _cullShader.SetBuffer(_compactKernel, "_IndirectArgsBuffer", tile.IndirectArgsBuffer);
        _cullShader.SetBuffer(_compactKernel, "_VoteBuffer", _voteBuffer);
        _cullShader.SetBuffer(_compactKernel, "_ScanBuffer", _scanBuffer);
        _cullShader.SetBuffer(_compactKernel, "_GroupSumArray", _scannedGroupSumBuffer);
        _cullShader.Dispatch(_compactKernel, _cullThreadGroupSize, 1, 1);
    }

    #endregion

    #region Draw
    
    private void DrawInit()
    {
        if (_drawInitialized)
        {
            Debug.LogWarning("Draw Shader already initialized, but DrawInit() called again; please investigate!");
            return;
        }

        _drawInitialized = false;
        
        // Initialize draw procedural buffers.
        // Note: _grassTriangleBuffer is used in the Graphics.DrawProcedural call, not bound to
        // the material property block.
        if (_drawProcedural)
        {
            var grassVertices = _grassMesh.vertices;
            var grassTris = _grassMesh.triangles;
            var grassUVs = _grassMesh.uv;
        
            _grassVertexBuffer =
                new GraphicsBuffer(GraphicsBuffer.Target.Structured, grassVertices.Length, sizeof(float) * 3);
            _grassTriangleBuffer =
                new GraphicsBuffer(GraphicsBuffer.Target.Structured, grassTris.Length, sizeof(int));
            _grassUVBuffer =
                new GraphicsBuffer(GraphicsBuffer.Target.Structured, grassUVs.Length, sizeof(float) * 2);
    
            _grassVertexBuffer.SetData(grassVertices);
            _grassTriangleBuffer.SetData(grassTris);
            _grassUVBuffer.SetData(grassUVs);

            if (!_useTerrainNormals)
            {
                var grassNormals = _grassMesh.normals;
                _grassNormalBuffer =
                    new GraphicsBuffer(GraphicsBuffer.Target.Structured, grassNormals.Length, sizeof(float) * 3);
                _grassNormalBuffer.SetData(grassNormals);
            }
        }
        
        // Bind buffers and variables to material property blocks per tile.
        for (int i = 0; i < _tiles.Length; i++)
        {
            _tiles[i].MaterialPropertyBlock = new MaterialPropertyBlock();
            
            if (_drawProcedural)
            {
                _tiles[i].MaterialPropertyBlock.SetBuffer("_Positions", _grassVertexBuffer);
                _tiles[i].MaterialPropertyBlock.SetBuffer("_UVs", _grassUVBuffer);
                _tiles[i].MaterialPropertyBlock.SetBuffer("_Normals", _grassNormalBuffer);
            }
            
            _tiles[i].MaterialPropertyBlock.SetInteger("_UseTerrainNormals", _useTerrainNormals ? 1 : 0);
            _tiles[i].MaterialPropertyBlock.SetBuffer("_GrassInstanceData",
                (_computeCullingEnabled && !_drawProcedural)
                    ? _tiles[i].GrassInstanceBufferCulled
                    : _tiles[i].GrassInstanceBuffer);
        }

        _drawInitialized = true;
        //Debug.Log("Draw initialized.");
    }
    
    private void DrawCleanup()
    {
        _grassVertexBuffer?.Dispose();
        _grassVertexBuffer = null;
        
        _grassTriangleBuffer?.Dispose();
        _grassTriangleBuffer = null;
        
        _grassUVBuffer?.Dispose();
        _grassUVBuffer = null;
        
        _grassNormalBuffer?.Dispose();
        _grassNormalBuffer = null;
        
        _drawInitialized = false;
    }

    private void DrawUpdate()
    {
        if (!_drawingEnabled || !_drawInitialized || !_camera) return;

        var cameraPosition = _camera.transform.position;
        var p = _camera.projectionMatrix;
        var v = _camera.transform.worldToLocalMatrix;
        var vp = p * v;
        
        var halfWidth = _tiles[0].Bounds.size.x * 0.5f;
        var halfLength = _tiles[0].Bounds.size.z * 0.5f;
        var tileMaxDist = Mathf.Max(halfWidth, halfLength) + _maxDistance;
        var tileMaxDistLOD = Mathf.Max(halfWidth, halfLength) + _maxDistanceLOD0;
        _tileMaxDistanceSqr = tileMaxDist * tileMaxDist;
        _tileMaxDistanceLOD0Sqr = tileMaxDistLOD * tileMaxDistLOD;

        var boundsLOD0 = new Bounds(cameraPosition, Vector3.one * _maxDistanceLOD0);
        
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i].InstanceCount == 0) continue;

            var tileDistFromCamera = (cameraPosition - _tiles[i].Bounds.center).sqrMagnitude;
            
            // skip drawing far away tiles.
            if (_perTileDistanceCullingEnabled)
            {
                if (tileDistFromCamera > _tileMaxDistanceSqr) continue;
            }

            // skip drawing tiles not in frustum.
            if (_perTileFrustumCullingEnabled)
            {
                GeometryUtility.CalculateFrustumPlanes(_camera, _frustumPlanes);
                var isVisible = GeometryUtility.TestPlanesAABB(_frustumPlanes, _tiles[i].Bounds);
                if (!isVisible) continue;
            }
            
            if (_drawProcedural)
            {
                Graphics.DrawProcedural(
                    _grassProceduralMaterial, 
                    _tiles[i].Bounds, 
                    MeshTopology.Triangles, 
                    _grassTriangleBuffer, 
                    _grassTriangleBuffer.count,
                    _tiles[i].InstanceCount,
                    null,
                    _tiles[i].MaterialPropertyBlock,
                    ShadowCastingMode.Off,
                    true,
                    gameObject.layer
                );
            }
            else
            {
                var isLOD1 = false;
                switch (_lodMode)
                {
                    case LODMode.Sphere:
                        isLOD1 = tileDistFromCamera > _tileMaxDistanceLOD0Sqr && _grassMeshLOD;
                        break;
                    case LODMode.Box:
                        isLOD1 = !boundsLOD0.Intersects(_tiles[i].Bounds) && _grassMeshLOD;
                        break;
                }
                
                CullTile(ref _tiles[i], ref vp, isLOD1 ? _indirectArgsLOD : _indirectArgs);
            
                Graphics.DrawMeshInstancedIndirect(
                    isLOD1 ? _grassMeshLOD : _grassMesh, 0, 
                    _grassInstancedIndirectMaterial, 
                    _tiles[i].Bounds, 
                    _tiles[i].IndirectArgsBuffer,
                    0,
                    _tiles[i].MaterialPropertyBlock,
                    ShadowCastingMode.Off,
                    true,
                    gameObject.layer
                );
            }
        }
    }
    
    #endregion
}
