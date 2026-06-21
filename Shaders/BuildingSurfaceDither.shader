Shader "ZGConnect/BuildingSurfaceDither"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        [HideInInspector] _DitherFade ("Dither Fade", Range(0, 1)) = 1
        [HideInInspector] [Toggle] _DitherFadeOut ("Dither Fade Out", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Smoothness;
                half _Metallic;
                half _DitherFade;
                half _DitherFadeOut;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Dither8x8(float2 screenPos)
            {
                screenPos = floor(screenPos);
                int x = int(fmod(screenPos.x, 8.0));
                int y = int(fmod(screenPos.y, 8.0));
                int index = x + y * 8;

                // 8x8 Bayer matrix, normalized to 0..1.
                const float bayer[64] = {
                    0.0 / 64.0, 32.0 / 64.0,  8.0 / 64.0, 40.0 / 64.0,  2.0 / 64.0, 34.0 / 64.0, 10.0 / 64.0, 42.0 / 64.0,
                    48.0 / 64.0, 16.0 / 64.0, 56.0 / 64.0, 24.0 / 64.0, 50.0 / 64.0, 18.0 / 64.0, 58.0 / 64.0, 26.0 / 64.0,
                    12.0 / 64.0, 44.0 / 64.0,  4.0 / 64.0, 36.0 / 64.0, 14.0 / 64.0, 46.0 / 64.0,  6.0 / 64.0, 38.0 / 64.0,
                    60.0 / 64.0, 28.0 / 64.0, 52.0 / 64.0, 20.0 / 64.0, 62.0 / 64.0, 30.0 / 64.0, 54.0 / 64.0, 22.0 / 64.0,
                     3.0 / 64.0, 35.0 / 64.0, 11.0 / 64.0, 43.0 / 64.0,  1.0 / 64.0, 33.0 / 64.0,  9.0 / 64.0, 41.0 / 64.0,
                    51.0 / 64.0, 19.0 / 64.0, 59.0 / 64.0, 27.0 / 64.0, 49.0 / 64.0, 17.0 / 64.0, 57.0 / 64.0, 25.0 / 64.0,
                    15.0 / 64.0, 47.0 / 64.0,  7.0 / 64.0, 39.0 / 64.0, 13.0 / 64.0, 45.0 / 64.0,  5.0 / 64.0, 37.0 / 64.0,
                    63.0 / 64.0, 31.0 / 64.0, 55.0 / 64.0, 23.0 / 64.0, 61.0 / 64.0, 29.0 / 64.0, 53.0 / 64.0, 21.0 / 64.0
                };

                return bayer[index];
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float dither = Dither8x8(input.positionCS.xy);
                if (_DitherFadeOut > 0.5h)
                    clip(dither - (1.0h - _DitherFade) - 0.001h);
                else
                    clip(_DitherFade - dither - 0.001h);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                float3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));

                half3 diffuse = albedo.rgb * mainLight.color * saturate(dot(normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * albedo.rgb;
                half3 color = diffuse + ambient;

                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Smoothness;
                half _Metallic;
                half _DitherFade;
                half _DitherFadeOut;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Dither8x8(float2 screenPos)
            {
                screenPos = floor(screenPos);
                int x = int(fmod(screenPos.x, 8.0));
                int y = int(fmod(screenPos.y, 8.0));
                int index = x + y * 8;
                const float bayer[64] = {
                    0.0 / 64.0, 32.0 / 64.0,  8.0 / 64.0, 40.0 / 64.0,  2.0 / 64.0, 34.0 / 64.0, 10.0 / 64.0, 42.0 / 64.0,
                    48.0 / 64.0, 16.0 / 64.0, 56.0 / 64.0, 24.0 / 64.0, 50.0 / 64.0, 18.0 / 64.0, 58.0 / 64.0, 26.0 / 64.0,
                    12.0 / 64.0, 44.0 / 64.0,  4.0 / 64.0, 36.0 / 64.0, 14.0 / 64.0, 46.0 / 64.0,  6.0 / 64.0, 38.0 / 64.0,
                    60.0 / 64.0, 28.0 / 64.0, 52.0 / 64.0, 20.0 / 64.0, 62.0 / 64.0, 30.0 / 64.0, 54.0 / 64.0, 22.0 / 64.0,
                     3.0 / 64.0, 35.0 / 64.0, 11.0 / 64.0, 43.0 / 64.0,  1.0 / 64.0, 33.0 / 64.0,  9.0 / 64.0, 41.0 / 64.0,
                    51.0 / 64.0, 19.0 / 64.0, 59.0 / 64.0, 27.0 / 64.0, 49.0 / 64.0, 17.0 / 64.0, 57.0 / 64.0, 25.0 / 64.0,
                    15.0 / 64.0, 47.0 / 64.0,  7.0 / 64.0, 39.0 / 64.0, 13.0 / 64.0, 45.0 / 64.0,  5.0 / 64.0, 37.0 / 64.0,
                    63.0 / 64.0, 31.0 / 64.0, 55.0 / 64.0, 23.0 / 64.0, 61.0 / 64.0, 29.0 / 64.0, 53.0 / 64.0, 21.0 / 64.0
                };
                return bayer[index];
            }

            Varyings ShadowVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float dither = Dither8x8(input.positionCS.xy);
                if (_DitherFadeOut > 0.5h)
                    clip(dither - (1.0h - _DitherFade) - 0.001h);
                else
                    clip(_DitherFade - dither - 0.001h);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
