// Vertex-color paint shader for Goop (URP). Albedo comes from the mesh's per-vertex COLOR, and
// metallic/smoothness from TEXCOORD1 (x=metallic, y=smoothness). This lets us paint a character whose
// UVs are unusable for texturing — painting writes vertex colors, not texels, so UV layout is irrelevant.
Shader "Goop/VertexPaintLit"
{
    Properties
    {
        _Metallic ("Metallic (fallback)", Range(0,1)) = 0
        _Smoothness ("Smoothness (fallback)", Range(0,1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _MIXED_LIGHTING_SUBTRACTIVE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
                float2 uv1        : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float4 color       : TEXCOORD2;
                float2 mr          : TEXCOORD3;
                float  fogCoord    : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings o = (Varyings)0;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                o.positionHCS = p.positionCS;
                o.positionWS  = p.positionWS;
                o.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                o.color       = IN.color;
                o.mr          = IN.uv1;
                o.fogCoord    = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 albedo     = IN.color.rgb;
                half  metallic   = saturate(IN.mr.x);
                half  smoothness = saturate(IN.mr.y);
                half3 normalWS   = normalize(IN.normalWS);
                half3 viewDir    = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 ambient = SampleSH(normalWS);
                half  ndotl   = saturate(dot(normalWS, mainLight.direction));
                half3 diffuse = mainLight.color * (ndotl * mainLight.shadowAttenuation);

                // Cheap Blinn-Phong sheen, scaled by smoothness; metallic tints it toward the albedo.
                half3 hvec    = normalize(mainLight.direction + viewDir);
                half  specPow = lerp(8.0, 128.0, smoothness);
                half  spec    = pow(saturate(dot(normalWS, hvec)), specPow) * smoothness;
                half3 specCol = lerp(half3(1,1,1), albedo, metallic);
                half3 specular = mainLight.color * (spec * mainLight.shadowAttenuation) * specCol;

                half3 addDiffuse = 0;
            #ifdef _ADDITIONAL_LIGHTS
                uint lc = GetAdditionalLightsCount();
                for (uint li = 0u; li < lc; li++)
                {
                    Light l = GetAdditionalLight(li, IN.positionWS);
                    addDiffuse += l.color * (saturate(dot(normalWS, l.direction)) * l.distanceAttenuation * l.shadowAttenuation);
                }
            #endif

                // Ambient floor guarantees the surface is never pure black even with no scene light.
                half3 lighting = max(ambient + diffuse + addDiffuse, half3(0.18, 0.18, 0.18));
                half3 col = albedo * lighting + specular;
                col = MixFog(col, IN.fogCoord);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertSh
            #pragma fragment fragSh
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct AttrSh { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct VarSh  { float4 positionHCS : SV_POSITION; };

            VarSh vertSh(AttrSh IN)
            {
                VarSh o;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nWS   = TransformObjectToWorldNormal(IN.normalOS);
                float4 pos   = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    pos.z = min(pos.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    pos.z = max(pos.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionHCS = pos;
                return o;
            }

            half4 fragSh(VarSh IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertD
            #pragma fragment fragD
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct AttrD { float4 positionOS : POSITION; };
            struct VarD  { float4 positionHCS : SV_POSITION; };

            VarD vertD(AttrD IN) { VarD o; o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 fragD(VarD IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
