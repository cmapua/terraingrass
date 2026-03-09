Shader "karomapu/Grass/Procedural"
{
    Properties
    {
        //_MainTex ("Texture", 2D) = "white" {}
        _TipColor ("Grass Tip Color", Color) = (0.5, 1, 0.5, 1)
        _TipColor2 ("Grass Tip Color 2", Color) = (0.5, 1, 0.5, 1)
        _RootColor ("Grass Root Color", Color) = (0.25, 0.5, 0.25, 1)
        _WindForce ("Wind Force", Range(0, 1)) = 0.3
        _WindWaveScale ("Wind Wave Scale", Range(0, 1)) = 0.25
        _WindSpeed ("Wind Speed", Range(0, 1)) = 0.5
        _ColorNoiseScale ("Color Noise Scale", Float) = 1.0
        _IndirectBoost ("Indirect Color Boost", Float) = 0.0
        _AttenuationLighten ("Attenuation Lighten", Float) = 0.0
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull Mode", Float) = 0
    }

    CGINCLUDE
    #include "GrassCommon.cginc"

    struct appdata {
        uint vertexID : SV_VertexID;
        uint instanceID : SV_InstanceID;
    };
    
    // TODO: organize under one struct?
    StructuredBuffer<float3> _Positions;
    StructuredBuffer<float3> _Normals;
    StructuredBuffer<float2> _UVs;

    v2f vert (appdata v) {
        v2f o;

        float4x4 objectToWorld = _GrassInstanceData[v.instanceID].TransformMatrix;
        float4 positionOS = float4(_Positions[v.vertexID], 1.0f);
        float4 positionWS = mul(objectToWorld, positionOS);
        float3 normal = _UseTerrainNormals == 1 ? _GrassInstanceData[v.instanceID].Normal : _Normals[v.vertexID];
        float2 uv = _UVs[v.vertexID];

        o.positionWS = mul(objectToWorld, positionOS) + GetWindAnimationOffset(positionWS, uv);
        o.pos = mul(UNITY_MATRIX_VP, o.positionWS);
        o.uv = uv;
        o.normal = _UseTerrainNormals == 1 ? normal : mul(objectToWorld, normal);
        float staticNoise = snoise(positionWS * _ColorNoiseScale);
        o.color = lerp(_TipColor2, _TipColor, max(0,staticNoise)).rgb;
        
        //TRANSFER_SHADOW(o);

        #if defined(SHADOWS_SCREEN)

        #if UNITY_UV_STARTS_AT_TOP
        float2 positionCS = float2(o.pos.x, -o.pos.y);
        #else
        float2 positionCS = o.pos;
        #endif
        
        o.shadowCoords.xy = (positionCS + o.pos.w) * 0.5;
        o.shadowCoords.zw = o.pos.zw;
        
        #endif

        return o;
    }
    ENDCG

    SubShader
    {
        Pass
        {
            Name "ForwardBase"
            Tags
            {
                "LightMode" = "ForwardBase"
                "RenderType" = "Opaque"
                "Queue" = "Geometry"
            }
            Cull [_CullMode]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fwdbase_fullshadows
            //#pragma multi_compile_fog
            ENDCG
        }

        Pass
        {
            Name "Shadow Pass"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }
            Cull [_CullMode]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShadow
            #pragma multi_compile_shadowcaster
            ENDCG
        }
    }
}