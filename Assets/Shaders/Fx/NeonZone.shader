// Zone "hologramme neon" cyberpunk. Le mesh reste un simple CUBE : c'est le shader qui plaque
// la base sur le sol, ENTIEREMENT AU RUNTIME, sans rien precalculer cote CPU.
// Le sol est lu dans le depth buffer de la camera (_CameraDepthTexture) : on reconstruit la
// position monde de la surface opaque visible derriere chaque pixel de paroi, son Y sert de
// niveau du sol. Le shader ne garde que la tranche [sol .. sol + _WallHeight].
// -> marche sur le terrain, les rampes, les props, la geometrie qui bouge ; deplacer la zone
//    met la base a jour dans la frame, sans le moindre raycast.
// Repli quand il n'y a que le ciel derriere (pas de depth) : le bas du cube.
// Bordures neon a largeur constante a l'ecran (fwidth), grille, scan, remplissage _Progress.
// Additif -> mange le Bloom du CyberpunkPostProfile.
// PREREQUIS : Depth Texture activee sur l'asset URP.
Shader "GMTK/NeonZone"
{
    Properties
    {
        [MainColor] _BaseColor("Couleur (progress 0)", Color) = (0.15, 0.8, 1.0, 1)
        _FullColor("Couleur (progress 1)", Color) = (0.35, 1.0, 0.55, 1)
        _DoneColor("Couleur validee", Color) = (1.0, 0.3, 0.9, 1)
        _Progress("Progress", Range(0,1)) = 0
        _Done("Valide", Range(0,1)) = 0

        _EdgeWidth("Bord : coeur (px)", Range(0.5, 20)) = 2.5
        _EdgeGlow("Bord : halo (px)", Range(1, 120)) = 26
        _Intensity("Intensite", Range(0, 10)) = 2.4
        _WallAlpha("Opacite paroi", Range(0, 1)) = 0.06
        _Fresnel("Fresnel paroi", Range(0.5, 8)) = 2.2

        _GridSize("Grille : densite", Range(0, 40)) = 6
        _GridAlpha("Grille : opacite", Range(0, 1)) = 0.18

        _ScanSpeed("Scan : vitesse", Float) = 0.35
        _ScanWidth("Scan : largeur", Range(0.005, 0.5)) = 0.05
        _ScanAlpha("Scan : opacite", Range(0, 1)) = 0.35

        _BaseGlow("Liseré au sol", Range(0, 3)) = 1.2
        _FadeStart("Fondu : hauteur de depart", Range(0, 1)) = 0.1
        _Squash("Squash (1 = repos)", Range(0.2, 2)) = 1
        _WallHeight("Hauteur des parois (m)", Float) = 1.8
        _PulseSpeed("Pulse (valide) : vitesse", Float) = 7.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "NeonZone"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One      // additif : les zones a alpha 0 disparaissent vraiment
            ZWrite Off
            Cull Off                // on voit les parois du fond -> volume lisible
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewWS     : TEXCOORD2;
                float3 posWS      : TEXCOORD3;
                float  bottomWS   : TEXCOORD4;   // bas du cube, repli quand il n'y a que le ciel
                float3 normalOS   : TEXCOORD5;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor, _FullColor, _DoneColor;
                float _Progress, _Done;
                float _EdgeWidth, _EdgeGlow, _Intensity, _WallAlpha, _Fresnel;
                float _GridSize, _GridAlpha;
                float _ScanSpeed, _ScanWidth, _ScanAlpha;
                float _BaseGlow, _FadeStart, _Squash, _WallHeight, _PulseSpeed;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings o;
                float3 p = IN.positionOS.xyz;

                // SQUASH & STRETCH en XZ seulement ici ; le vertical est fait au fragment
                // (la base varie par pixel avec le relief, un sommet de cube ne peut pas la suivre).
                float sq = max(_Squash, 0.05);
                p.xz *= rsqrt(sq);

                float3 posWS = TransformObjectToWorld(p);
                o.positionCS = TransformWorldToHClip(posWS);
                o.posWS = posWS;
                o.bottomWS = TransformObjectToWorld(float3(p.x, -0.5, p.z)).y;
                o.uv = IN.uv;
                o.normalOS = IN.normalOS;
                o.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                o.viewWS = GetWorldSpaceViewDir(posWS);
                return o;
            }

            // ligne d'epaisseur constante a l'ecran : d = distance au trait, px = largeur voulue
            float Line(float d, float px)
            {
                float aa = fwidth(d) + 1e-6;
                return 1.0 - smoothstep(0.0, aa * px, d);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // parois seulement : le dessus et le dessous du cube ne doivent rien dessiner
                clip(0.5 - abs(IN.normalOS.y));

                float2 uv = IN.uv;

                // BASE PLAQUEE AU SOL, 100% RUNTIME : on lit la profondeur de la scene derriere
                // ce pixel et on reconstruit la position monde de la surface opaque. Son Y est
                // le niveau du sol. Aucune donnee precalculee : ce qui bouge est suivi direct.
                float2 suv = GetNormalizedScreenSpaceUV(IN.positionCS);
                float rawD = SampleSceneDepth(suv);
            #if UNITY_REVERSED_Z
                bool sky = rawD <= 1e-6;
            #else
                bool sky = rawD >= 1.0 - 1e-6;
            #endif
                // rien derriere (ciel) -> on retombe sur le bas du cube, sinon la paroi
                // disparaitrait des qu'elle se detache sur l'horizon.
                float groundY = sky
                    ? IN.bottomWS
                    : ComputeWorldSpacePosition(suv, rawD, UNITY_MATRIX_I_VP).y;

                // h = 0 au sol (qui ondule avec le relief), 1 en haut de paroi. Le squash
                // vertical joue ici : il comprime/etire la tranche visible depuis le sol.
                float sq = max(_Squash, 0.05);
                float h = (IN.posWS.y - groundY) / max(_WallHeight * sq, 1e-4);

                // hors de la tranche [sol .. sol + hauteur] : rien. C'est ce clip qui donne
                // la forme, pas la geometrie -> le mesh reste un cube quelconque.
                clip(h);
                clip(1.0 - h);

                uv.y = h;   // la grille, le scan et les bordures suivent la base ondulee

                // --- bordures : coins verticaux + liseré bas. PAS de bordure en haut :
                // la paroi doit se dissoudre, pas se terminer par un trait net. ---
                float edgeDist = min(min(uv.x, 1.0 - uv.x), h);
                float core = Line(edgeDist, _EdgeWidth);
                float glow = Line(edgeDist, _EdgeGlow);
                glow *= glow;                       // halo qui retombe vite

                // --- grille fine ---
                float2 g = uv * _GridSize;
                float2 gd = abs(frac(g + 0.5) - 0.5) / (fwidth(g) + 1e-6);
                float grid = 1.0 - smoothstep(0.0, 1.2, min(gd.x, gd.y));

                // --- scan vertical qui monte en boucle ---
                float scanY = frac(_Time.y * _ScanSpeed);
                float scan = 1.0 - smoothstep(0.0, _ScanWidth, abs(h - scanY));

                // --- remplissage jusqu'a _Progress + trait vif sur le niveau ---
                float filled = 1.0 - smoothstep(_Progress, _Progress + 0.02, h);
                float fillLine = 1.0 - smoothstep(0.0, 0.025, abs(h - _Progress));
                fillLine *= step(0.001, _Progress) * (1.0 - _Done);

                // --- paroi : visible surtout en incidence rasante ---
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(IN.viewWS);
                float fres = pow(1.0 - saturate(abs(dot(N, V))), _Fresnel);

                // La couleur monte du sol : sous le niveau _Progress c'est plein _FullColor,
                // au-dessus ca vire progressivement -> on LIT le temps passe dans la zone.
                float3 col = lerp(_BaseColor.rgb, _FullColor.rgb, max(_Progress * 0.6, filled));
                col = lerp(col, _DoneColor.rgb, _Done);

                float pulse = 1.0 + _Done * 0.45 * sin(_Time.y * _PulseSpeed);

                // liseré vif au contact du sol -> c'est lui qui dessine le carré au sol
                float baseLine = 1.0 - smoothstep(0.0, 0.15, h);
                baseLine *= baseLine;

                // tout se dissout vers le haut, BORDURES COMPRISES : la zone n'a pas de fin
                // franche, elle s'evapore. Sinon le trait du haut la referme visuellement.
                float fade = 1.0 - smoothstep(_FadeStart, 1.0, h);
                fade *= fade;

                float a =
                      core * 1.0
                    + glow * 0.45
                    + grid * _GridAlpha
                    + scan * _ScanAlpha
                    + _WallAlpha * (0.3 + fres * 1.6)
                    + filled * _WallAlpha * 1.1
                    + fillLine * 0.9;

                a = saturate(a * fade + baseLine * _BaseGlow) * pulse;
                return half4(col * _Intensity, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
