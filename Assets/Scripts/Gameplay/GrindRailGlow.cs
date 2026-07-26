using UnityEngine;
using UnityEngine.Rendering;

namespace Gameplay
{
    // "CE REBORD SE GRINDE." Trace un ruban neon anime sur CHAQUE rail declare au
    // GrindRailNetwork. Sans ca, rien a l'ecran ne distingue une rambarde grindable d'un decor :
    // le rail n'est qu'une polyligne en memoire, et un rebord de pont n'a meme pas de mesh a lui.
    //
    // POURQUOI PAS peindre les meshes concernes : les rails ne viennent pas tous d'un mesh (les
    // rambardes d'ouvrage sortent des splines de RoadNetwork, les props declarent une barre en
    // deux points). Le seul endroit qui les connait TOUS, c'est le reseau -- donc on dessine
    // depuis lui, et tout ce qui devient grindable s'allume tout seul.
    //
    // POURQUOI PAS un LineRenderer par rail : une passe de CityPropsSeeder pose ~200 rambardes,
    // soit 200 GameObjects et 200 draw calls. Ici, un seul mesh pour toute la ville.
    //
    // Zero asset, zero cablage : le composant s'ajoute tout seul au reseau (meme procede que
    // DebugOverlay), fabrique sa texture et son materiau en code.
    [DisallowMultipleComponent]
    public class GrindRailGlow : MonoBehaviour
    {
        [Header("Ruban")]
        [SerializeField] private float width = 0.16f;
        [Tooltip("Souleve le ruban pour qu'il ne z-fight pas avec la rambarde qu'il souligne.")]
        [SerializeField] private float lift = 0.04f;
        [SerializeField] private Color color = new Color(0.35f, 1f, 1f);
        [Tooltip("Intensite HDR : au-dela de 1 le bloom s'en empare, c'est ce qui fait le neon.")]
        [SerializeField] private float intensity = 3.2f;

        [Header("Animation")]
        [Tooltip("Metres par seconde de defilement des chevrons. Le sens indique le rail, pas un sens de parcours.")]
        [SerializeField] private float scrollSpeed = 2.2f;
        [SerializeField] private float pulseAmplitude = 0.25f;
        [SerializeField] private float pulseSpeed = 2.6f;

        // Longueur, en metres, d'un motif de chevron. Fixe : c'est l'echelle a laquelle le motif
        // se lit en roulant, pas un reglage de gout.
        private const float PatternMeters = 0.6f;

        private Material mat;
        private float baseAlpha;
        private GrindRailNetwork net;
        private MeshFilter mf;
        // Nombre de rails pris en compte au dernier build. Les emetteurs ne se declarent pas
        // tous au meme moment : RoadNetwork pose ses rambardes d'ouvrage depuis son Update, donc
        // APRES le premier build. On surveille le compte plutot que de deviner un ordre.
        private int builtCount = -1;

        // AfterSceneLoad et pas Awake : tous les emetteurs (GrindRail, GrindRailSource,
        // RoadNetwork) se declarent pendant les Awake de la scene. Plus tot, `paths` serait vide.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            var net = FindAnyObjectByType<GrindRailNetwork>();
            if (net == null) return;
            if (net.GetComponent<GrindRailGlow>() == null) net.gameObject.AddComponent<GrindRailGlow>();
        }

        private void Start() => EnsureRig();

        // Reentrant : une recompilation PENDANT le play mode vide les champs non serialises
        // (net, mf, mat) sans repasser par Start, et Update partait en NullReference a chaque
        // frame. On retrouve l'enfant deja pose plutot que d'en empiler un second.
        private bool EnsureRig()
        {
            if (net == null) net = GetComponent<GrindRailNetwork>();
            if (net == null) { enabled = false; return false; }
            if (mf != null && mat != null) return true;

            // Enfant a l'identite : le mesh est bati en coordonnees MONDE (les rails le sont
            // deja), donc son porteur ne doit appliquer aucune transformation.
            var child = transform.Find("Glow");
            if (child == null)
            {
                var go = new GameObject("Glow");
                go.transform.SetParent(transform, false);
                child = go.transform;
            }
            child.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            child.localScale = Vector3.one;

            mf = child.GetComponent<MeshFilter>();
            if (mf == null) mf = child.gameObject.AddComponent<MeshFilter>();
            var mr = child.GetComponent<MeshRenderer>();
            if (mr == null) mr = child.gameObject.AddComponent<MeshRenderer>();

            mr.sharedMaterial = mat = MakeMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            builtCount = -1;   // force la reconstruction du mesh
            return true;
        }

        private void Update()
        {
            if (!EnsureRig()) return;

            int n = net.paths != null ? net.paths.Count : 0;
            if (n != builtCount)
            {
                builtCount = n;
                if (mf.sharedMesh != null) Destroy(mf.sharedMesh);
                mf.sharedMesh = BuildMesh(net);
            }

            // Defilement le long du rail (u suit l'arc-length) + respiration : un neon fixe se
            // fond dans la ville, un neon qui bat attire l'oeil.
            mat.mainTextureOffset = new Vector2(-Time.time * (scrollSpeed / PatternMeters), 0f);
            float k = 1f + pulseAmplitude * Mathf.Sin(Time.time * pulseSpeed);
            var c = color * (intensity * k);
            c.a = baseAlpha;
            mat.SetColor(BaseColorId, c);
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // Deux quads en CROIX par segment (un a plat, un debout). Un seul quad disparaitrait des
        // qu'on le regarde par la tranche -- exactement l'angle qu'on a en arrivant sur un rail.
        private Mesh BuildMesh(GrindRailNetwork net)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var tris = new System.Collections.Generic.List<int>();
            float h = width * 0.5f;

            foreach (var p in net.paths)
            {
                if (p?.points == null || p.points.Length < 2) continue;
                float s = 0f;
                for (int i = 0; i < p.points.Length - 1; i++)
                {
                    Vector3 a = p.points[i] + Vector3.up * lift;
                    Vector3 b = p.points[i + 1] + Vector3.up * lift;
                    float len = Vector3.Distance(a, b);
                    if (len < 1e-3f) continue;

                    Vector3 t = (b - a) / len;
                    // Rail vertical : le produit avec up degenere, on bascule sur un autre axe.
                    Vector3 side = Vector3.Cross(Vector3.up, t);
                    side = side.sqrMagnitude < 1e-4f ? Vector3.Cross(Vector3.forward, t).normalized
                                                     : side.normalized;
                    Vector3 up = Vector3.Cross(t, side).normalized;

                    float u0 = s / PatternMeters, u1 = (s + len) / PatternMeters;
                    Quad(verts, uvs, tris, a - side * h, b - side * h, b + side * h, a + side * h, u0, u1);
                    Quad(verts, uvs, tris, a - up * h, b - up * h, b + up * h, a + up * h, u0, u1);
                    s += len;
                }
            }

            if (tris.Count == 0) return null;

            var mesh = new Mesh { name = "GrindRailGlow" };
            // Une ville entiere de rambardes depasse largement 65k sommets.
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Quad(System.Collections.Generic.List<Vector3> verts,
                                 System.Collections.Generic.List<Vector2> uvs,
                                 System.Collections.Generic.List<int> tris,
                                 Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, float u0, float u1)
        {
            int i0 = verts.Count;
            verts.Add(v0); verts.Add(v1); verts.Add(v2); verts.Add(v3);
            uvs.Add(new Vector2(u0, 0f)); uvs.Add(new Vector2(u1, 0f));
            uvs.Add(new Vector2(u1, 1f)); uvs.Add(new Vector2(u0, 1f));
            // Une seule orientation : le materiau est en Cull Off, doubler les triangles ne
            // ferait que doubler le cout.
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
        }

        // Unlit additif : le ruban ne doit pas etre eteint par la nuit ni projeter d'ombre, il
        // doit BRILLER. L'additif evite aussi de trier des transparents sur toute la ville.
        private Material MakeMaterial()
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            m.SetFloat("_Surface", 1f);            // Transparent
            m.SetFloat("_Blend", 1f);              // Additive
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);               // double face
            m.SetFloat("_AlphaClip", 0f);
            m.renderQueue = (int)RenderQueue.Transparent;
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.One);
            m.mainTexture = ChevronTexture();
            m.mainTextureScale = Vector2.one;
            baseAlpha = 1f;
            m.SetColor(BaseColorId, color * intensity);
            return m;
        }

        // Chevrons ">>>" en niveaux d'alpha, generes une fois. Une fleche dit "ca file dans ce
        // sens" mieux qu'une bande unie, et coute 64x16 pixels.
        private static Texture2D chevron;

        private static Texture2D ChevronTexture()
        {
            if (chevron != null) return chevron;
            const int W = 64, H = 16;
            chevron = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                // Distance au centre du ruban : le bord s'eteint -> le trait a l'air rond.
                float v = Mathf.Abs(y / (H - 1f) - 0.5f) * 2f;
                float edge = 1f - v * v;
                for (int x = 0; x < W; x++)
                {
                    // Le chevron : le front avance au milieu, recule sur les bords.
                    float u = x / (float)W;
                    float front = Mathf.Repeat(u - v * 0.18f, 1f);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - Mathf.Abs(front - 0.5f) * 3.4f));
                    px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a * edge * 0.55f + edge * 0.35f));
                }
            }
            chevron.SetPixels(px);
            chevron.Apply();
            return chevron;
        }
    }
}
