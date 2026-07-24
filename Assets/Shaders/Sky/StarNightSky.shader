Shader "GMTK/StarNightSky"
{
    Properties
    {
        [Header(Sky Gradient)]
        _ZenithColor  ("Zenith (top)", Color)   = (0.010, 0.020, 0.055, 1)
        _HorizonColor ("Horizon", Color)         = (0.040, 0.055, 0.120, 1)
        _GroundColor  ("Ground (below)", Color)  = (0.006, 0.008, 0.020, 1)
        _HorizonSharp ("Horizon Sharpness", Range(0.4, 6)) = 1.4

        [Header(Stars)]
        _StarColorA   ("Star Color Warm", Color) = (1.0, 0.85, 0.65, 1)
        _StarColorB   ("Star Color Cool", Color) = (0.70, 0.85, 1.0, 1)
        _StarDensity  ("Star Density", Range(40, 400)) = 190
        _StarSparse   ("Star Sparseness", Range(0.80, 0.995)) = 0.93
        _StarSize     ("Star Size", Range(0.02, 0.4)) = 0.11
        _StarBright   ("Star Brightness", Range(0, 6)) = 2.2
        _Twinkle      ("Twinkle Speed", Range(0, 6)) = 2.5
        _StarDensity2 ("Star Density Fine", Range(80, 800)) = 420
        _StarSparse2  ("Star Sparseness Fine", Range(0.80, 0.999)) = 0.975

        [Header(Milky Way Haze)]
        _MilkyColor   ("Milky Way Tint", Color) = (0.35, 0.30, 0.65, 1)
        _MilkyInt     ("Milky Way Intensity", Range(0, 2)) = 0.55
        _MilkyScale   ("Milky Way Scale", Range(0.5, 5)) = 2.0
        _MilkyTilt    ("Milky Way Tilt", Range(-1, 1)) = 0.35

        [Header(Moon)]
        _MoonDir      ("Moon Dir (xyz)", Vector) = (0.30, 0.40, 0.72, 0)
        _MoonPhaseDir ("Moon Phase Light Dir", Vector) = (-0.7, 0.15, 0.4, 0)
        _MoonColor    ("Moon Surface", Color)   = (1.15, 1.20, 1.30, 1)
        _MoonGlowCol  ("Moon Glow", Color)   = (0.35, 0.50, 0.85, 1)
        _MoonSize     ("Moon Angular Size", Range(0.005, 0.09)) = 0.030
        _MoonHalo     ("Moon Glow Tightness", Range(0.5, 12)) = 4.0
        _MoonHaloInt  ("Moon Glow Intensity", Range(0, 2)) = 0.5

        [Header(Cyberpunk Horizon Glow)]
        _CityGlowA    ("City Glow Color A", Color) = (0.10, 0.85, 1.10, 1)
        _CityGlowB    ("City Glow Color B", Color) = (1.00, 0.20, 0.75, 1)
        _CityGlowInt  ("City Glow Intensity", Range(0, 3)) = 0.85
        _CityGlowFall ("City Glow Height Falloff", Range(2, 40)) = 16
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

            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor;
                float4 _HorizonColor;
                float4 _GroundColor;
                float  _HorizonSharp;
                float4 _StarColorA;
                float4 _StarColorB;
                float  _StarDensity;
                float  _StarSparse;
                float  _StarSize;
                float  _StarBright;
                float  _Twinkle;
                float  _StarDensity2;
                float  _StarSparse2;
                float4 _MilkyColor;
                float  _MilkyInt;
                float  _MilkyScale;
                float  _MilkyTilt;
                float4 _MoonDir;
                float4 _MoonPhaseDir;
                float4 _MoonColor;
                float4 _MoonGlowCol;
                float  _MoonSize;
                float  _MoonHalo;
                float  _MoonHaloInt;
                float4 _CityGlowA;
                float4 _CityGlowB;
                float  _CityGlowInt;
                float  _CityGlowFall;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

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
            float2 hash22(float2 p)
            {
                float3 a = frac(float3(p.xyx) * float3(123.34, 234.34, 345.65));
                a += dot(a, a + 34.45);
                return frac(float2(a.x * a.y, a.y * a.z));
            }
            float vnoise(float2 p)
            {
                float2 i = floor(p); float2 f = frac(p);
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
                [unroll] for (int i = 0; i < 5; i++) { v += amp * vnoise(p); p = p * 2.02 + 11.3; amp *= 0.5; }
                return v;
            }

            // spherical UV from direction (used for star cells)
            float2 dirUV(float3 d)
            {
                float u = atan2(d.z, d.x) * 0.1591549 + 0.5; // /(2pi)
                float v = asin(clamp(d.y, -1.0, 1.0)) * 0.3183099 + 0.5; // /pi
                return float2(u, v);
            }

            // one star layer
            float starLayer(float3 dir, float density, float sparse, float size, float twPhase)
            {
                float2 uv = dirUV(dir) * density;
                float2 cell = floor(uv);
                float2 f = frac(uv);
                float r = hash21(cell);
                float exists = step(sparse, r);
                float2 jit = hash22(cell) * 0.7 + 0.15;   // star position inside cell
                float d = length(f - jit);
                float pt = smoothstep(size, 0.0, d);       // round point
                float tw = 0.55 + 0.45 * sin(_Time.y * _Twinkle + twPhase + r * 30.0);
                return pt * exists * tw;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);
                float h = dir.y;

                // ---- vertical gradient ----
                float above = saturate(h);
                float t = saturate(pow(above, 1.0 / _HorizonSharp));
                float3 col = lerp(_HorizonColor.rgb, _ZenithColor.rgb, t);
                col = lerp(col, _GroundColor.rgb, saturate(-h * 4.0));

                float skyMask = saturate(h * 6.0 + 0.05); // fade stars/moon below horizon

                // ---- milky way haze band ----
                float band = dir.y - _MilkyTilt * dir.x;          // tilted plane
                float milk = fbm(float2(dir.x, dir.z) * _MilkyScale + dir.y * 1.7);
                milk *= exp(-band * band * 6.0);                  // concentrate into a band
                milk = saturate(milk * 1.6 - 0.25);
                col += _MilkyColor.rgb * milk * _MilkyInt * skyMask;

                // ---- stars (two layers: bold + fine) ----
                float s1 = starLayer(dir, _StarDensity,  _StarSparse,  _StarSize,        0.0);
                float s2 = starLayer(dir, _StarDensity2, _StarSparse2, _StarSize * 0.55, 1.7);
                float milkBoost = 1.0 + milk * 2.0;               // denser stars in the band
                float2 tint = hash22(floor(dirUV(dir) * _StarDensity));
                float3 starCol = lerp(_StarColorB.rgb, _StarColorA.rgb, tint.x);
                float starV = (s1 + s2 * 0.8) * milkBoost * _StarBright * skyMask;
                col += starCol * starV;

                // ---- cyberpunk horizon glow (cyan <-> magenta by azimuth) ----
                float az = dir.x * 0.5 + 0.5;
                float3 cityCol = lerp(_CityGlowA.rgb, _CityGlowB.rgb, az);
                float cityFall = exp(-abs(h) * _CityGlowFall);
                col += cityCol * cityFall * _CityGlowInt * saturate(h * 20.0 + 1.0);

                // ---- moon (real disc: phase + maria + soft limb) ----
                float3 mdir = normalize(_MoonDir.xyz);
                float3 mup  = abs(mdir.y) < 0.99 ? float3(0,1,0) : float3(1,0,0);
                float3 mtx  = normalize(cross(mup, mdir));
                float3 mty  = cross(mdir, mtx);
                // disc-local coords, radius 1 at the limb
                float2 duv = float2(dot(dir, mtx), dot(dir, mty)) / _MoonSize;
                float  r2  = dot(duv, duv);
                float  front = step(0.0, dot(dir, mdir));
                // tight glow halo (exp falloff, not a wide pow blob)
                float halo = exp(-r2 * _MoonHalo) * _MoonHaloInt * front;
                col += _MoonGlowCol.rgb * halo * skyMask;
                if (r2 < 1.0 && front > 0.5)
                {
                    float3 n = mtx * duv.x + mty * duv.y + mdir * sqrt(saturate(1.0 - r2));
                    float3 pl = normalize(_MoonPhaseDir.xyz);
                    float lam = saturate(dot(n, pl));
                    lam = lam * lam * 0.95 + 0.05;            // crescent/gibbous terminator
                    float mare = fbm(duv * 2.2 + 5.0);        // dark seas / bright highlands
                    float surf = lerp(0.62, 1.05, smoothstep(0.35, 0.62, mare));
                    float limb = smoothstep(1.0, 0.90, r2);   // soft anti-aliased edge
                    float3 moon = _MoonColor.rgb * surf * lam;
                    col = lerp(col, moon, limb);
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
