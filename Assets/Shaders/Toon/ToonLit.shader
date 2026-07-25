Shader "GMTK/ToonLit"
{
    Properties
    {
        [Header(Base)]
        _BaseMap        ("Base Map", 2D)          = "white" {}
        _BaseColor      ("Base Color", Color)     = (1, 1, 1, 1)
        [Toggle(_UNLIT_ON)] _Unlit ("Unlit (enseigne neon)", Float) = 0

        [Header(Toon Ramp)]
        _RampSteps      ("Ramp Steps", Range(1, 6))        = 2
        _RampSmooth     ("Ramp Smoothness", Range(0, 0.5)) = 0.015
        // Nuit cartoon : l'ombre est une COULEUR choisie, pas une absence de lumiere.
        // Un gris desature ici rend l'image boueuse des que l'ambient remonte.
        _ShadowTint     ("Shadow Tint", Color)             = (0.24, 0.16, 0.42, 1)
        // L'ancien 0.35 etait code en dur et privait les surfaces toon des 2/3 de
        // l'ambient que recoit une surface URP/Lit dans la meme scene.
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1

        [Header(Rim Light)]
        _RimColor       ("Rim Color", Color)      = (1, 1, 1, 1)
        _RimPower       ("Rim Power", Range(0.5, 16)) = 4
        _RimAmount      ("Rim Amount", Range(0, 1))   = 0.5
        // Teinte la rim par les neons voisins : c'est ce qui detache les silhouettes
        // du fond la nuit, y compris dos a la lune.
        _RimNeonBoost   ("Rim Neon Boost", Range(0, 4)) = 1.2

        [Header(Toon Specular)]
        _ToonSpecColor  ("Specular Color", Color)      = (1, 1, 1, 1)
        _Glossiness     ("Glossiness", Range(1, 256))  = 32
        _SpecStep       ("Specular Step", Range(0, 1)) = 0.6

        // Plafond de la flaque neon. En cel-shading la flaque est une bande de couleur
        // bornee, pas une decroissance physique : l'intensite de la lampe regle sa
        // PORTEE, ce parametre regle sa force. Sans plafond, une lampe a 31 juste
        // au-dessus d'une route la crame en blanc.
        _NeonPoolStrength ("Neon Pool Strength", Range(0, 3)) = 1.3

        [Header(Halftone   trame dans la retombee neon)]
        _HalftoneColor      ("Dot Ink Color", Color)          = (0.08, 0.06, 0.12, 1)
        // Taille de cellule en METRES, pas en pixels : une trame en espace ecran reste
        // collee a l'ecran pendant que le decor defile dessous (effet saletes sur
        // l'objectif). Ancree au monde, elle voyage avec les surfaces.
        _HalftoneScale      ("Dot Cell Size (m)", Float)      = 0.3
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
            #pragma shader_feature_local_fragment _UNLIT_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _RampSteps;
                float  _RampSmooth;
                float4 _ShadowTint;
                float  _AmbientStrength;
                float4 _RimColor;
                float  _RimPower;
                float  _RimAmount;
                float  _RimNeonBoost;
                float4 _ToonSpecColor;
                float  _Glossiness;
                float  _SpecStep;
                float4 _HalftoneColor;
                float  _HalftoneScale;
                float  _HalftoneStrength;
                float  _HalftoneMaxRadius;
                float  _NeonPoolStrength;
                float  _Unlit;
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

            // Grille de trame ancree au MONDE, projetee sur l'axe dominant de la normale
            // (triplanaire simplifiee). Les points collent alors aux surfaces au lieu de
            // rester fixes a l'ecran quand la camera bouge.
            float2 HalftoneGrid(float3 positionWS, float3 N)
            {
                float3 a = abs(N);
                float2 p = (a.y >= a.x && a.y >= a.z) ? positionWS.xz
                         : (a.x >= a.z              ? positionWS.zy
                                                    : positionWS.xy);
                return p / max(0.01, _HalftoneScale);
            }

            // Halftone dot coverage. darkness 0 -> no ink, 1 -> full ink.
            // 45-degree rotated grid for a classic comic look.
            float Halftone(float2 uv, float darkness)
            {
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

                // Enseigne neon : aplat de couleur pur, aucun eclairage, alimente le bloom.
                // Tres cartoon ET tres cyberpunk en meme temps.
                #if defined(_UNLIT_ON)
                    return half4(MixFog(albedo, IN.fogFactor), baseTex.a * _BaseColor.a);
                #endif

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

                // ---------------------------------------------------------------
                //  Lumieres additionnelles = LES NEONS. Ce sont elles la key light
                //  de la ville, pas le clair de lune. On accumule au lieu d'ajouter
                //  tout de suite : la couleur sert aussi a la rim et a la trame.
                // ---------------------------------------------------------------
                // En Forward+ (PC_Renderer m_RenderingMode = 2) URP n'active PAS
                // _ADDITIONAL_LIGHTS : les lumieres passent par la boucle clusterisee, gardee
                // par _CLUSTER_LIGHT_LOOP. Tester le seul _ADDITIONAL_LIGHTS rendait ce bloc
                // mort -> aucune point light n'eclairait quoi que ce soit en toon (verifie a
                // la sphere temoin : URP/Lit allumee, ToonLit grise).
                // On separe TEINTE et COUVERTURE. add.color contient deja l'intensite de
                // la lampe (31 pour un lampadaire) : l'utiliser telle quelle comme energie
                // crame la surface. La couverture, elle, est purement geometrique (0..1).
                float3 neonHueSum = 0;  // teintes cumulees, ponderees
                float  neonWeight = 0;  // somme des poids, sert UNIQUEMENT a moyenner la teinte
                float  neonCover  = 0;  // couverture = MAX sur les lampes, pas somme
                float3 neonRimSum = 0;  // idem, filtre "lumiere derriere l'objet"
                float  rimWeight  = 0;
                float  rimCover   = 0;

                #if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = IN.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light add = GetAdditionalLight(lightIndex, IN.positionWS);
                        float aNdotL = saturate(dot(N, normalize(add.direction)));
                        // La bande toon se calcule sur le SEUL angle. Mettre l'attenuation de
                        // distance DANS le quantizer (ce qui etait fait) tuait toute lumiere
                        // ponctuelle : URP attenue en 1/d2, donc a 4.5 m avec range 24 on a
                        // distanceAttenuation ~ 0.05, et ToonRamp(0.05) sur 2 bandes vaut
                        // exactement 0. L'attenuation multiplie la couleur, comme dans URP.
                        float aRamp  = ToonRamp(aNdotL);
                        float aAtten = add.distanceAttenuation * add.shadowAttenuation;
                        // Teinte pure de la lampe, debarrassee de son intensite.
                        float  aInt = Max3(add.color.r, add.color.g, add.color.b);  // ~ intensite
                        float3 aHue = add.color / max(aInt, 1e-4);

                        // L'intensite pilote la PORTEE de la flaque (jusqu'ou la couverture
                        // reste a 1), pas sa brillance : bornee par lampe, elle ne peut plus
                        // partir a 30 et cramer la surface.
                        float aCover = saturate(aAtten * aInt);
                        float w = aRamp * aCover;
                        neonHueSum += aHue * w;
                        neonWeight += w;
                        // MAX et non somme : road.unity a 112 lampes, chacune saturee dans
                        // son rayon. Les additionner donne une couverture > 1 partout, donc
                        // un aplat uniforme sur toute la chaussee au lieu de flaques -- et
                        // la teinte moyenne d'une vingtaine de lampes vire au creme.
                        neonCover = max(neonCover, w);

                        // Une rim light simule une lumiere qui CONTOURNE une silhouette :
                        // elle n'a de sens que si la lumiere est derriere l'objet vu de la
                        // camera. Sans ce filtre, un lampadaire au-dessus d'une route
                        // allume la rim sur toute la chaussee (cf. le piege _RimAmount).
                        float back = saturate(-dot(normalize(add.direction), V));
                        neonRimSum += aHue * aCover * back;
                        rimWeight  += aCover * back;
                        rimCover    = max(rimCover, aCover * back);
                    LIGHT_LOOP_END
                #endif

                // Trame BD dans la RETOMBEE du neon (et non dans l'ombre : une trame
                // posee sur des zones deja noires ne donne pas de la BD, elle donne de
                // la boue uniforme). Les points remplacent le degrade en bord de flaque.
                // ponytail: une seule trame sur l'accumulation, pas une par lumiere.
                // road.unity a 112 point lights -> une trame par lumiere serait
                // inabordable et moirerait. Upgrade si ca manque de nettete : ne
                // tramer que la lumiere la plus proche.
                float pool  = saturate(neonCover);
                float3 neonTint = neonHueSum / max(neonWeight, 1e-4);   // teinte moyenne, bornee a 1
                float2 hUV  = HalftoneGrid(IN.positionWS, N);
                float dots  = Halftone(hUV, pool);
                // Au loin une cellule descend sous le pixel -> moire. On eteint la trame
                // des que c'est le cas, sans parametre a regler.
                float dotFade = saturate(1.0 - (fwidth(hUV.x) + fwidth(hUV.y)));
                // x3 : la cloche ne culmine qu'au milieu de la retombee. A x7 elle saturait
                // sur ~[0.15, 0.85], donc la trame couvrait toute la flaque et lisait comme
                // une grille de pastilles posee sur la route plutot que comme un degrade
                // discretise.
                float band  = saturate(pool * (1.0 - pool) * 3.0);
                // MULTIPLICATIF, pas lerp : un lerp(pool, dots, ...) remplace une couverture
                // faible par un point binaire, donc les points brillent plus fort que la
                // lumiere qu'ils representent -> trame sur toute l'image, y compris la ou il
                // n'y a presque pas de neon. En modulant, la trame ne peut jamais depasser
                // la flaque reelle.
                float shaped = pool * lerp(1.0, dots, band * _HalftoneStrength * dotFade);
                float3 ink = lerp(_HalftoneColor.rgb, neonTint, 0.85);
                color += albedo * ink * shaped * _NeonPoolStrength;

                // Rim teintee par les neons voisins. L'ancienne version multipliait par
                // NdotL, donc la rim ne s'allumait QUE du cote deja eclaire par la lune
                // -- inutile la ou on en a besoin. La part neon, elle, n'en depend pas.
                float rim = 1.0 - saturate(dot(N, V));
                rim = pow(rim, _RimPower);
                float rimMask = smoothstep(_RimAmount - 0.03, _RimAmount + 0.03, rim);
                float3 rimHue = neonRimSum / max(rimWeight, 1e-4);
                // Meme filtre que pour les neons, applique a la lune. Sans lui, un grand
                // plan horizontal vu en rasant a rim ~ 1 sur tout l'ecran et recoit
                // _RimColor * NdotL en plein : la chaussee vire au blanc.
                float backMain = saturate(-dot(L, V));
                color += rimMask * (_RimColor.rgb * NdotL * backMain
                                  + rimHue * saturate(rimCover) * _RimNeonBoost);

                // ambient
                color += albedo * SampleSH(N) * _AmbientStrength;

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
                float _RampSteps; float _RampSmooth; float4 _ShadowTint; float _AmbientStrength;
                float4 _RimColor; float _RimPower; float _RimAmount; float _RimNeonBoost;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
                float _NeonPoolStrength; float _Unlit;
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
                float _RampSteps; float _RampSmooth; float4 _ShadowTint; float _AmbientStrength;
                float4 _RimColor; float _RimPower; float _RimAmount; float _RimNeonBoost;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
                float _NeonPoolStrength; float _Unlit;
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
                float _RampSteps; float _RampSmooth; float4 _ShadowTint; float _AmbientStrength;
                float4 _RimColor; float _RimPower; float _RimAmount; float _RimNeonBoost;
                float4 _ToonSpecColor; float _Glossiness; float _SpecStep;
                float4 _HalftoneColor; float _HalftoneScale; float _HalftoneStrength; float _HalftoneMaxRadius;
                float _NeonPoolStrength; float _Unlit;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
