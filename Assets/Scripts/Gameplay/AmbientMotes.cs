using UnityEngine;

namespace Gameplay
{
    // Poussieres / pollen qui flottent doucement autour de la camera : captent
    // la lumiere du couchant, donnent de la vie a l'air. Auto-suffisant :
    // construit tout au runtime (ParticleSystem + material + texture, zero asset).
    // A mettre en ENFANT de la camera.
    //
    // DEUX couches pour la profondeur (parallaxe) :
    //  - "far"  : simulationSpace World -> defile quand la camera bouge (fond).
    //  - "near" : simulationSpace Local -> suit la camera (voile proche, subtil).
    // Les deux tres discretes : alpha bas, grains fins.
    [DisallowMultipleComponent]
    public class AmbientMotes : MonoBehaviour
    {
        [System.Serializable]
        public class Layer
        {
            public string name = "far";
            public ParticleSystemSimulationSpace space = ParticleSystemSimulationSpace.World;
            public Vector3 localOffset = Vector3.zero;
            public float rate = 16f;
            public Vector3 boxSize = new Vector3(40f, 18f, 40f);
            public Vector2 sizeRange = new Vector2(0.03f, 0.10f);
            public Vector2 lifeRange = new Vector2(7f, 12f);
            [Range(0f, 1f)] public float maxAlpha = 0.12f; // pic d'opacite (subtil)
            public int maxParticles = 300;
        }

        [SerializeField]
        private Layer[] layers =
        {
            // une seule couche, dans le monde : parallaxe lente quand on roule
            new Layer {
                name = "far", space = ParticleSystemSimulationSpace.World,
                rate = 48f, boxSize = new Vector3(30f, 14f, 30f),
                sizeRange = new Vector2(0.10f, 0.28f), lifeRange = new Vector2(5f, 9f),
                // coeur fort : sinon le post-process toon (halftone) posterise
                // les grains peu contrastes et les fait disparaitre.
                maxAlpha = 0.95f, maxParticles = 240
            },
        };

        // dore chaud : capte le couchant + contraste sur le sol bleu pale
        // (blanc pur = invalisible apres le halftone).
        [SerializeField] private Color tint = new Color(1f, 0.85f, 0.5f, 1f);

        private void Awake()
        {
            var mat = BuildMoteMaterial();
            foreach (var l in layers) BuildLayer(l, mat);
        }

        private void BuildLayer(Layer l, Material mat)
        {
            var go = new GameObject("Motes_" + l.name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = l.localOffset;
            var ps = go.AddComponent<ParticleSystem>(); // ajoute aussi le renderer

            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(l.lifeRange.x, l.lifeRange.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
            main.startSize = new ParticleSystem.MinMaxCurve(l.sizeRange.x, l.sizeRange.y);
            var c = tint; c.a = 1f; // l'alpha final vient du colorOverLifetime
            main.startColor = c;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.gravityModifier = -0.003f;    // flotte tres legerement vers le haut
            main.simulationSpace = l.space;
            main.maxParticles = l.maxParticles;
            main.prewarm = true;               // air deja rempli au demarrage
            main.playOnAwake = true;

            var emission = ps.emission;
            emission.rateOverTime = l.rate;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = l.boxSize;

            // derive douce et irreguliere (jamais de ligne droite)
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.12f;
            noise.frequency = 0.15f;
            noise.scrollSpeed = 0.05f;

            // fondu entree rapide / sortie douce, pic = maxAlpha. Fade-in court
            // (0.08) sinon les grains mettent trop longtemps a apparaitre.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(l.maxAlpha, 0.08f),
                        new GradientAlphaKey(l.maxAlpha, 0.85f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.material = mat;
            r.sortMode = ParticleSystemSortMode.Distance;

            // playOnAwake ne declenche pas sur un PS ajoute au runtime : Play explicite
            ps.Play();
        }

        // "Sprites/Default" : unlit, alpha-blend fiable, honore l'alpha de la
        // texture ET la couleur de particule (contrairement au shader URP
        // Particles dont le mode blend ne bascule pas via SetFloat au runtime ->
        // rendait des carres opaques). Compatible URP.
        private static Material BuildMoteMaterial()
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.mainTexture = BuildSparkle(); // _MainTex
            return mat;
        }

        // Etincelle / twinkle : coeur doux + 4 rayons fins. Blanc, alpha en
        // falloff. Lit mieux qu'un point a l'ecran (le halftone carre un point)
        // et colle a la DA toon (petits eclats de lumiere qui flottent).
        private static Texture2D BuildSparkle()
        {
            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (s - 1) * 0.5f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    // coords normalisees -1..1
                    float nx = (x - c) / (s * 0.5f);
                    float ny = (y - c) / (s * 0.5f);
                    float d = Mathf.Sqrt(nx * nx + ny * ny);

                    float core = Mathf.Clamp01(1f - d / 0.34f); core *= core;      // centre brillant
                    const float thick = 0.05f, len = 0.95f;                         // rayons fins et longs
                    float rx = Mathf.Clamp01(1f - Mathf.Abs(ny) / thick) * Mathf.Clamp01(1f - Mathf.Abs(nx) / len);
                    float ry = Mathf.Clamp01(1f - Mathf.Abs(nx) / thick) * Mathf.Clamp01(1f - Mathf.Abs(ny) / len);
                    float ray = Mathf.Max(rx, ry); ray *= ray;                       // rayons doux

                    float a = Mathf.Clamp01(core + 0.6f * ray);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            return tex;
        }
    }
}
