Shader "Voxel/VoxelTransparent"
{
    Properties
    {
        _TexArray   ("Block Textures (2D Array)", 2DArray) = "" {}
        _Smoothness ("Smoothness", Range(0,1)) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
        }

        // ── Forward Lit (transparent) ──────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_ARRAY(_TexArray);
            SAMPLER(sampler_TexArray);

            CBUFFER_START(UnityPerMaterial)
                float _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float  slice      : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = normInputs.normalWS;
                OUT.uv         = IN.uv0;
                OUT.slice      = IN.uv1.x;
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D_ARRAY(_TexArray, sampler_TexArray,
                                                   frac(IN.uv), round(IN.slice));

                float3 normalWS = normalize(IN.normalWS);

                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif
                Light mainLight = GetMainLight(shadowCoord);

                float  shadow  = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
                float  NdotL   = saturate(dot(normalWS, mainLight.direction));
                float3 diffuse = mainLight.color * shadow * NdotL;

                half3 additionalLight = 0;
                #ifdef _ADDITIONAL_LIGHTS
                    uint lightCount = GetAdditionalLightsCount();
                    for (uint i = 0u; i < lightCount; ++i)
                    {
                        Light light = GetAdditionalLight(i, IN.positionWS);
                        additionalLight += light.color
                            * (light.distanceAttenuation * light.shadowAttenuation)
                            * saturate(dot(normalWS, light.direction));
                    }
                #endif

                half3 ambient = SampleSH(normalWS);

                col.rgb *= diffuse + additionalLight + ambient;
                col.rgb  = MixFog(col.rgb, IN.fogFactor);
                // alpha comes from the texture — set it on the water fallback or PNG
                return col;
            }
            ENDHLSL
        }

        // No ShadowCaster or DepthOnly — transparent objects don't write to depth/shadow maps
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
