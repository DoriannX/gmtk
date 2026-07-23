Shader "GMTK/OutlineEdgeDetect"
{
    Properties
    {
        _OutlineColor      ("Outline Color", Color)          = (0.05, 0.04, 0.09, 1)
        _OutlineThickness  ("Thickness (px)", Range(0.5, 4)) = 1.2
        _DepthThreshold    ("Depth Threshold", Range(0, 4))  = 0.6
        _DepthSensitivity  ("Depth Sensitivity", Range(0, 40)) = 12
        _NormalThreshold   ("Normal Threshold", Range(0, 2)) = 0.35
        _NormalSensitivity ("Normal Sensitivity", Range(0, 8)) = 3
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "OutlineEdgeDetect"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            float4 _OutlineColor;
            float  _OutlineThickness;
            float  _DepthThreshold;
            float  _DepthSensitivity;
            float  _NormalThreshold;
            float  _NormalSensitivity;

            float SampleEyeDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float2 o = _OutlineThickness / _ScreenParams.xy;

                // Roberts cross sample offsets (diagonals)
                float2 uv0 = uv + float2( o.x,  o.y);
                float2 uv1 = uv + float2(-o.x, -o.y);
                float2 uv2 = uv + float2( o.x, -o.y);
                float2 uv3 = uv + float2(-o.x,  o.y);

                // --- depth edges ---
                float d0 = SampleEyeDepth(uv0);
                float d1 = SampleEyeDepth(uv1);
                float d2 = SampleEyeDepth(uv2);
                float d3 = SampleEyeDepth(uv3);
                float depthDiff = sqrt(pow(d1 - d0, 2) + pow(d3 - d2, 2));
                // normalize against distance so far objects don't over-outline
                float centerDepth = SampleEyeDepth(uv);
                depthDiff /= max(centerDepth, 0.001);
                float depthEdge = smoothstep(_DepthThreshold, _DepthThreshold + 0.001,
                                             depthDiff * _DepthSensitivity);

                // --- normal edges ---
                float3 n0 = SampleSceneNormals(uv0);
                float3 n1 = SampleSceneNormals(uv1);
                float3 n2 = SampleSceneNormals(uv2);
                float3 n3 = SampleSceneNormals(uv3);
                float3 dn0 = n1 - n0;
                float3 dn1 = n3 - n2;
                float normalDiff = sqrt(dot(dn0, dn0) + dot(dn1, dn1));
                float normalEdge = smoothstep(_NormalThreshold, _NormalThreshold + 0.001,
                                              normalDiff * _NormalSensitivity);

                float edge = saturate(max(depthEdge, normalEdge));

                half3 outCol = lerp(sceneColor.rgb, _OutlineColor.rgb, edge * _OutlineColor.a);
                return half4(outCol, sceneColor.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
