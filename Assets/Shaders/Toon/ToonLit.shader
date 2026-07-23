Shader "GMTK/ToonLit"
{
    Properties
    {
        [Header(Base)]
        _BaseMap        ("Base Map", 2D)          = "white" {}
        _BaseColor      ("Base Color", Color)     = (1, 1, 1, 1)

        [Header(Toon Ramp)]
        _RampSteps      ("Ramp Steps", Range(1, 6))        = 2
        _RampSmooth     ("Ramp Smoothness", Range(0, 0.5)) = 0.015
        _ShadowTint     ("Shadow Tint", Color)             = (0.55, 0.58, 0.72, 1)

        [Header(Rim Light)]
        _RimColor       ("Rim Color", Color)      = (1, 1, 1, 1)
        _RimPower       ("Rim Power", Range(0.5, 16)) = 4
        _RimAmount      ("Rim Amount", Range(0, 1))   = 0.5

        [Header(Toon Specular)]
        _ToonSpecColor  ("Specular Color", Color)      = (1, 1, 1, 1)
        _Glossiness     ("Glossiness", Range(1, 256))  = 32
        _SpecStep       ("Specular Step", Range(0, 1)) = 0.6

        [Header(Halftone Shadow Dots)]
        _HalftoneColor      ("Dot Ink Color", Color)          = (0.08, 0.06, 0.12, 1)
        _HalftoneScale      ("Dot Cell Size (px)", Float)     = 6
        _HalftoneStrength   ("Dot Strength", Range(0, 1))     = 1
        _HalftoneMaxRadius  ("Dot Max Radius", Range(0.1, 1)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 300

        // ------------------------------------------------------------------
        //  Forward lit: toon ramp + rim + toon specular + halftone shadows
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // URP keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _RampSteps;
                float  _RampSmooth;
                float4 _ShadowTint;
                float4 _RimColor;
                float  _RimPower;
                float  _RimAmount;
                float4 _ToonSpecColor;
                float  _Glossiness;
                float  _SpecStep;
                float4 _HalftoneColor;
                float  _HalftoneScale;
                float  _HalftoneStrength;
                float  _HalftoneMaxRadius;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS   = nrm.normalWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            // Quantize a 0..1 value into hard toon bands with soft edges.
            float ToonRamp(float x)
            {
                float steps = max(1.0, _RampSteps);
                float scaled = saturate(x) * steps;
                float lower  = floor(scaled);
                float f      = frac(scaled);
                float e      = _RampSmooth * steps;
                float band   = smoothstep(0.5 - e, 0.5 + e, f);
                return (lower + band) / steps;
            }

            // Screen-space halftone dot coverage. darkness 0 -> no ink, 1 -> full ink.
            // 45-degree rotated grid for a classic comic look.
            float Halftone(float2 pixelPos, float darkness)
            {
                float2 uv = pixelPos / max(1.0, _HalftoneScale);
                // rotate 45 deg
                const float2x2 R = float2x2(0.70710678, -0.70710678, 0.70710678, 0.70710678);
                uv = mul(R, uv);
                float2 cell = frac(uv) - 0.5;
                float d = length(cell) * 2.0;                 // 0 center .. ~1 edge
                float radius = saturate(darkness) * _HalftoneMaxRadius;
                float aa = fwidth(d) + 1e-4;
                return smoothstep(radius + aa, radius - aa, d); // 1 inside dot
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 albedo  = baseTex.rgb * _BaseColor.rgb;

                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // main light + shadow
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 L = normalize(mainLight.direction);

                float shadowAtten = mainLight.shadowAttenuation * mainLight.distanceAttenuation;
                float NdotL = saturate(dot(N, L));
                float lightness = NdotL * shadowAtten;

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    float2 normalizedSC = GetNormalizedScreenSpaceUV(IN.positionCS);
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedSC);
                    lightness *= aoFactor.directAmbientOcclusion;
                #endif

                // toon banded lighting
                float ramp = ToonRamp(lightness);

                half3 litColor    = albedo * mainLight.color;
                half3 shadowColor = albedo * _ShadowTint.rgb;
                half3 color = lerp(shadowColor, litColor, ramp);

                // toon specular (hard cut)
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _Glossiness) * NdotL * shadowAtten;
                float specMask = smoothstep(_SpecStep, _SpecStep + 0.03, spec);
                color += specMask * _ToonSpecColor.rgb * mainLight.color;

                // additional lights (banded, no halftone for perf/clarity)
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = IN.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light add = GetAdditionalLight(lightIndex, IN.positionWS);
                        float aNdotL = saturate(dot(N, normalize(add.direction)));
                        float aRamp = ToonRamp(aNdotL * add.shadowAttenuation * add.distanceAttenuation);
                        color += albedo * add.color * aRamp;
                    LIGHT_LOOP_END
                #endif

                // rim light
                float rim = 1.0 - saturate(dot(N, V));
                rim = pow(rim, _RimPower);
                float rimMask = smoothstep(_RimAmount - 0.03, _RimAmount + 0.03, rim) * NdotL;
                color += rimMask * _RimColor.rgb;

                // ambient
                color += albedo * SampleSH(N) * 0.35;

                // halftone dots in shadowed regions (BD ink)
                float shadowMask = 1.0 - ramp;
                float darkness   = saturate(1.0 - lightness);
                float ink = Halftone(IN.positionCS.xy, darkness) * shadowMask * _HalftoneStrength;
                color = lerp(color, _HalftoneColor.rgb, ink);

                color = MixFog(color, IN.fogFactor);
                return half4(color, baseTex.a * _BaseColor.a);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        //  Shadow caster
        // ------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST; float4 _BaseColor;
                float _RampSteps; float _RampSmooth; float4 _ShadowTint;
                float4 _RimColor; float _RimPower; float _RimAmount;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        //  Depth only
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST; float4 _BaseColor;
                float _RampSteps; float _RampSmooth; float4 _ShadowTint;
                float4 _RimColor; float _RimPower; float _RimAmount;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        //  Depth + normals (needed by SSAO and edge-detect outline)
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST; float4 _BaseColor;
                float _RampSteps; float _RampSmooth; float4 _ShadowTint;
                float4 _RimColor; float _RimPower; float _RimAmount;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
