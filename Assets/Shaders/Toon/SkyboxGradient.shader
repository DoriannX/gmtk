Shader "GMTK/SkyboxGradient"
{
    Properties
    {
        [Header(Sky Gradient)]
        _ZenithColor  ("Zenith (top)", Color)    = (0.20, 0.18, 0.42, 1)
        _MidColor     ("Mid Sky", Color)         = (0.62, 0.42, 0.58, 1)
        _HorizonColor ("Horizon Base", Color)    = (1.00, 0.66, 0.36, 1)
        _GroundColor  ("Ground (below)", Color)  = (0.10, 0.07, 0.12, 1)
        _MidHeight    ("Mid Band Height", Range(0.0, 1.0)) = 0.35
        _HorizonSharp ("Horizon Sharpness", Range(0.2, 6)) = 1.6

        [Header(Sun)]
        _SunDir       ("Sun Visual Dir (xyz, 0=use light)", Vector) = (0, 0, 0, 0)
        _SunColor     ("Sun Core Color", Color)  = (6.0, 4.4, 2.6, 1)
        _SunGlowColor ("Sun Glow Color", Color)  = (3.0, 1.2, 0.45, 1)
        _SunSize      ("Sun Disc Size", Range(0.0005, 0.1)) = 0.007
        _SunSoftness  ("Sun Disc Softness", Range(0.0, 0.03)) = 0.003
        _SunGlow      ("Sun Glow Spread", Range(1, 512)) = 110
        _SunGlowInt   ("Sun Glow Intensity", Range(0, 4)) = 1.7

        [Header(Horizon Sun Wash)]
        _WashColor    ("Wash Color", Color)      = (1.0, 0.42, 0.20, 1)
        _WashSpread   ("Wash Azimuth Spread", Range(1, 16)) = 4
        _WashHeight   ("Wash Height Falloff", Range(1, 40)) = 10

        [Header(Soft Painted Clouds)]
        _CloudColor   ("Cloud Lit Color", Color)   = (1.0, 0.90, 0.80, 1)
        _CloudShadow  ("Cloud Shadow Color", Color)= (0.80, 0.58, 0.62, 1)
        _CloudScale   ("Cloud Scale", Range(0.2, 6)) = 1.8
        _CloudDensity ("Cloud Density", Range(0, 1)) = 0.42
        _CloudSoft    ("Cloud Edge Softness", Range(0.01, 0.4)) = 0.12
        _CloudBodyCut ("Cloud Lit Crown", Range(0.02, 0.4)) = 0.16
        _CloudSpeed   ("Cloud Speed", Range(0, 0.5)) = 0.015
        _CloudOpacity ("Cloud Opacity", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor;
                float4 _MidColor;
                float4 _HorizonColor;
                float4 _GroundColor;
                float  _MidHeight;
                float  _HorizonSharp;
                float4 _SunDir;
                float4 _SunColor;
                float4 _SunGlowColor;
                float  _SunSize;
                float  _SunSoftness;
                float  _SunGlow;
                float  _SunGlowInt;
                float4 _WashColor;
                float  _WashSpread;
                float  _WashHeight;
                float4 _CloudColor;
                float4 _CloudShadow;
                float  _CloudScale;
                float  _CloudDensity;
                float  _CloudSoft;
                float  _CloudBodyCut;
                float  _CloudSpeed;
                float  _CloudOpacity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir        : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.dir = IN.positionOS.xyz;
                return OUT;
            }

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }
            float vnoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }
            float fbm(float2 p)
            {
                float v = 0.0, amp = 0.5;
                [unroll] for (int i = 0; i < 3; i++)   // few octaves -> chunky cartoon blobs, not wispy
                {
                    v += amp * vnoise(p);
                    p = p * 2.03 + 13.1;
                    amp *= 0.5;
                }
                return v;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);
                float h = dir.y;

                Light main = GetMainLight();
                // visual sun dir: optional override (lets the disc sit lower than the light)
                float3 sunDir = (dot(_SunDir.xyz, _SunDir.xyz) > 1e-4)
                                ? normalize(_SunDir.xyz)
                                : normalize(main.direction);

                // ---------- vertical gradient ----------
                float above = saturate(h);
                float tHorizonMid = saturate(pow(above / max(_MidHeight, 0.001), _HorizonSharp));
                float3 col = lerp(_HorizonColor.rgb, _MidColor.rgb, tHorizonMid);
                float tMidZenith = saturate((above - _MidHeight) / max(1.0 - _MidHeight, 0.001));
                col = lerp(col, _ZenithColor.rgb, tMidZenith);
                col = lerp(col, _GroundColor.rgb, saturate(-h * 3.0));

                // ---------- horizon sun wash ----------
                float azim = saturate(dot(normalize(float3(dir.x, 0, dir.z)),
                                          normalize(float3(sunDir.x, 0, sunDir.z))));
                float wash = pow(azim, _WashSpread) * exp(-abs(h) * _WashHeight);
                col = lerp(col, _WashColor.rgb, saturate(wash) * _WashColor.a);

                // ---------- sun glow ----------
                float sunDot = saturate(dot(dir, sunDir));
                float glow = pow(sunDot, _SunGlow) * _SunGlowInt;
                col += _SunGlowColor.rgb * glow;

                // ---------- distant sun disc, cut at horizon ----------
                float disc = smoothstep(1.0 - _SunSize - _SunSoftness, 1.0 - _SunSize, sunDot);
                float horizonCut = smoothstep(-0.01, 0.008, h);
                col = lerp(col, _SunColor.rgb, saturate(disc) * horizonCut);

                // ---------- flat cel clouds, drawn OVER the sun so they occlude it ----------
                if (h > 0.0)
                {
                    float2 cuv = dir.xz / (h + 0.35);
                    cuv *= _CloudScale;
                    cuv += _Time.y * _CloudSpeed * float2(1.0, 0.35);

                    float n = fbm(cuv);
                    float t = 1.0 - _CloudDensity;

                    // crisp-ish silhouette (cel), still soft enough to avoid a harsh black line
                    float cov = smoothstep(t - _CloudSoft, t + _CloudSoft, n);
                    // hard 2-tone body: cool base vs warm-lit crown
                    float litSel = step(t + _CloudBodyCut, n);
                    float3 litCol = lerp(_CloudColor.rgb, _WashColor.rgb, saturate(wash) * 0.5);
                    float3 cloudCol = lerp(_CloudShadow.rgb, litCol, litSel);

                    float fade = smoothstep(0.03, 0.22, h) * smoothstep(1.0, 0.5, h);
                    col = lerp(col, cloudCol, cov * fade * _CloudOpacity);
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
