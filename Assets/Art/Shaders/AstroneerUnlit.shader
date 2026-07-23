Shader "Custom/AstroneerUnlit"
{
    Properties
    {
        _BaseColor ("Base Color Tint", Color) = (1, 1, 1, 1)
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
            Name "Unlit"
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR; // C'est ici qu'on récupère la couleur des sommets (Vertex Color) !
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // Transforme la position locale 3D en position sur l'écran 2D
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // On fait passer la couleur du sommet vers le fragment
                OUT.color = IN.color; 
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Rendu final : on multiplie la couleur des sommets par la teinte de base
                return IN.color * _BaseColor;
            }
            ENDHLSL
        }
    }
}
