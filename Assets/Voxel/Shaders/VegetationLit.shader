Shader "Voxel/VegetationLit"
{
    Properties
    {
        _MainTex        ("Texture",         2D)          = "white" {}
        _Color          ("Tint",            Color)       = (0.45, 0.85, 0.30, 1)
        _Cutoff         ("Alpha Cutoff",    Range(0,1))  = 0.35
        _WindSpeed      ("Wind Speed",      Float)       = 1.5
        _WindStrength   ("Wind Strength",   Float)       = 0.10
        _WindFrequency  ("Wind Frequency",  Float)       = 0.9
        _AmbientMin     ("Ambient Min",     Range(0,1))  = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "TransparentCutout"
            "Queue"          = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off

        // ── Forward Lit pass ──────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ FOG_LINEAR FOG_EXP FOG_EXP2

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                float  _WindSpeed;
                float  _WindStrength;
                float  _WindFrequency;
                half   _AmbientMin;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attrs
            {
                float4 posOS  : POSITION;
                float3 normOS : NORMAL;
                float2 uv     : TEXCOORD0;
                float2 uv2    : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 posCS    : SV_POSITION;
                float2 uv       : TEXCOORD0;
                half3  normWS   : TEXCOORD1;
                float  fogCoord : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attrs IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS = TransformObjectToWorld(IN.posOS.xyz);
                float  sway  = IN.uv2.y;
                float  wave  = sin(_Time.y * _WindSpeed + posWS.x * _WindFrequency);
                posWS.x += wave       * sway * _WindStrength;
                posWS.z += wave * 0.4 * sway * _WindStrength;

                OUT.posCS    = TransformWorldToHClip(posWS);
                OUT.uv       = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.normWS   = TransformObjectToWorldNormal(IN.normOS);
                OUT.fogCoord = ComputeFogFactor(OUT.posCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Color;
                clip(col.a - _Cutoff);

                // Use abs(NdL) so both sides of a blade face the sun equally.
                half3 normWS = normalize(IN.normWS);
                Light  sun   = GetMainLight();
                half   NdL   = abs(dot(normWS, (half3)sun.direction));
                col.rgb     *= (half3)sun.color * max(NdL, _AmbientMin);
                col.rgb      = MixFog(col.rgb, IN.fogCoord);
                return col;
            }
            ENDHLSL
        }

        // ── Shadow caster ─────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex   shadowVert
            #pragma fragment shadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half   _Cutoff;
                float  _WindSpeed;
                float  _WindStrength;
                float  _WindFrequency;
                half   _AmbientMin;
            CBUFFER_END
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            struct SAttrs
            {
                float4 posOS   : POSITION;
                float3 normOS  : NORMAL;
                float2 uv      : TEXCOORD0;
                float2 uv2     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct SV2F
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            SV2F shadowVert(SAttrs IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                SV2F OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS  = TransformObjectToWorld(IN.posOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normOS);

                float wave = sin(_Time.y * _WindSpeed + posWS.x * _WindFrequency);
                posWS.x += wave       * IN.uv2.y * _WindStrength;
                posWS.z += wave * 0.4 * IN.uv2.y * _WindStrength;

                float4 posCS = TransformWorldToHClip(ApplyShadowBias(posWS, normWS, _MainLightPosition.xyz));
                // Clamp depth so back-facing polys don't disappear behind the shadow near plane.
                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, posCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.posCS = posCS;
                OUT.uv    = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 shadowFrag(SV2F IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Color;
                clip(col.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
