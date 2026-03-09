#include "UnityCG.cginc"
#include "AutoLight.cginc"
#include "Lighting.cginc"
#include "SimpleNoise.cginc"

// TODO: use unity macros for lighting/shadows?
// TODO: fog support?
struct v2f {
    float4 pos : SV_POSITION;
    float4 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float3 normal : TEXCOORD2;
    float3 color : TEXCOORD3;
    #if defined(INSTANCED_INDIRECT)
    SHADOW_COORDS(4)
    #else
        #if defined(SHADOWS_SCREEN)
        float4 shadowCoords : TEXCOORD4;
        #endif
    #endif
};

struct GrassInstanceData {
    float4x4 TransformMatrix;
    float3 Normal;
};

// from TerrainGrass.cs
StructuredBuffer<GrassInstanceData> _GrassInstanceData;
int _UseTerrainNormals;

// from material
float _WindWaveScale;
float _WindSpeed;
float _WindForce;
float _ColorNoiseScale;
float4 _TipColor;
float4 _TipColor2;
float4 _RootColor;

inline float4 GetWindAnimationOffset(float4 positionWS, float2 uv) {
    float t = _Time.y * (_WindSpeed * 5);
    float noise = snoise((positionWS + t) * _WindWaveScale) * 0.01;
    float fixBase = noise * pow(uv.y, 2.0);
    float wind = fixBase * (_WindForce * 30);

    return float4((float3)wind, 0);
}

// needed?
float _IndirectBoost;
float _AttenuationLighten;

float4 frag (v2f i) : SV_Target {
    float3 baseColor = lerp(i.color, _RootColor, 1.0f - i.uv.y).rgb;
    #if defined(POINT) || defined(POINT_COOKIE) || defined(SPOT)
    float3 lightDir = _WorldSpaceLightPos0.xyz - i.positionWS.xyz;
    #else
    float3 lightDir = _WorldSpaceLightPos0.xyz;
    #endif
    float3 lightColor = _LightColor0.rgb;
    float3 normal = normalize(i.normal);
    float3 indirect = max(0, ShadeSH9(float4(normal, 1.0))) + _IndirectBoost;

    #if !defined(INSTANCED_INDIRECT) && defined(SHADOWS_SCREEN)
    float atten = tex2D(_ShadowMapTexture, i.shadowCoords.xy / i.shadowCoords.w);
    #else
    UNITY_LIGHT_ATTENUATION(atten, i, i.positionWS.xyz);
    #endif

    // #if defined(INSTANCED_INDIRECT)
    // float atten = UNITY_SHADOW_ATTENUATION(i, i.positionWS.xyz);
    // #else
    //     #if defined(SHADOWS_SCREEN)
    //     float atten = tex2D(_ShadowMapTexture, i.shadowCoords.xy / i.shadowCoords.w);
    //     #else
    //     UNITY_LIGHT_ATTENUATION(atten, i, i.positionWS.xyz);
    //     #endif
    // #endif
    
    atten = saturate(max(0, atten) + _AttenuationLighten);

    //return atten;
    
    float diffuse = max(dot(lightDir, normal), 0.0);
    // half lambert
    //diffuse = pow(diffuse * 0.5 + 0.5, 2.0);
    
    lightColor *= diffuse * atten;
                
    float4 finalColor = float4(baseColor * (lightColor + indirect), 1.0);
    return finalColor;
}

float4 fragShadow (v2f i) : SV_Target {
    SHADOW_CASTER_FRAGMENT(i)
}