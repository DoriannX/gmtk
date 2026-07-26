using UnityEngine;
using UnityEngine.InputSystem;

namespace UI
{
    // HABILLAGE COMMUN DES ECRANS, d'apres la maquette Figma "Crazy Deliveries" :
    // bandes cyan en biais a texte rose cerne, titres "bulle" cernes de noir, panneau
    // magenta en diagonale portant la lettre de rang.
    // Dessine en OnGUI comme tout le reste du HUD (ScoreGauge / RunHud / TrickHud) :
    // zero Canvas, zero prefab a cabler, un composant pose en scene suffit.
    //
    // Les formes penchees sont CUITES une fois dans des textures (bande, biseau, etoile)
    // plutot que tracees en tranches : OnGUI ne sait dessiner que des rectangles droits,
    // et un parallelogramme en tranches de 1 px, c'est ~50 DrawTexture par bouton et par
    // frame. Une texture = un seul appel, et le filtrage bilineaire adoucit les bords.
    public static class MenuSkin
    {
        // Tout se dessine comme si l'ecran faisait 1080 de haut, puis est mis a l'echelle.
        public const float Reference = 1080f;

        public static readonly Color Cyan = new Color(0.44f, 0.94f, 1f);
        public static readonly Color Pink = new Color(1f, 0.18f, 0.83f);
        public static readonly Color Cream = new Color(1f, 0.95f, 0.78f);
        public static readonly Color Gold = new Color(1f, 0.78f, 0.24f);
        public static readonly Color Ink = new Color(0.03f, 0.03f, 0.06f);
        // Volontairement tres sombre : en espace LINEAIRE, GUI.color est corrige en gamma,
        // et un violet "correct" a l'oeil dans l'inspecteur ressort delave a l'ecran.
        public static readonly Color Night = new Color(0.023f, 0.010f, 0.090f);

        // Penche des bandes / biseaux, en fraction de leur largeur.
        private const float Skew = 0.10f;

        // ---- mise a l'echelle ----

        // Installe le repere "unites de design". Retourne l'ancienne matrice a rendre a End().
        public static Matrix4x4 Begin(out float width, out float height)
        {
            Matrix4x4 m = GUI.matrix;
            float k = K;
            GUIUtility.ScaleAroundPivot(new Vector2(k, k), Vector2.zero);
            width = UnityEngine.Screen.width / k;
            height = Reference;
            return m;
        }

        public static void End(Matrix4x4 m)
        {
            GUI.matrix = m;
            GUI.color = Color.white;
        }

        private static float K => UnityEngine.Screen.height / Reference;

        // Souris DANS les unites de design. On ne passe pas par Event.current.mousePosition :
        // la selection est resolue dans Update (OnGUI tourne plusieurs fois par frame, un
        // clic y serait consomme deux fois).
        public static Vector2 Mouse()
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return new Vector2(-1000f, -1000f);
            Vector2 p = m.position.ReadValue();
            return new Vector2(p.x, UnityEngine.Screen.height - p.y) / K;
        }

        public static bool MouseClicked()
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            return m != null && m.leftButton.wasPressedThisFrame;
        }

        // ---- primitives ----

        public static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        public static void Frame(Rect r, float th, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, th), c);
            Fill(new Rect(r.x, r.yMax - th, r.width, th), c);
            Fill(new Rect(r.x, r.y, th, r.height), c);
            Fill(new Rect(r.xMax - th, r.y, th, r.height), c);
        }

        // Bande penchee qui se fond vers la droite : le fond des boutons de menu.
        public static void Band(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, BandTex);
        }

        // Grand biseau plein (panneau magenta de l'ecran de fin). `flip` renvoie la
        // pente dans l'autre sens.
        public static void Wedge(Rect r, Color c, bool flip = false)
        {
            GUI.color = c;
            if (!flip) GUI.DrawTexture(r, WedgeTex);
            else GUI.DrawTextureWithTexCoords(r, WedgeTex, new Rect(1f, 0f, -1f, 1f));
        }

        // Halo rond (lueur derriere un titre / une lettre de rang).
        public static void Glow(Vector2 center, float radius, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), DiscTex);
        }

        // Etoile a pointes derriere le titre de defaite.
        public static void Burst(Vector2 center, float radius, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), BurstTex);
        }

        // Silhouette de ville en fond de l'ecran de fin (barres d'immeubles).
        public static void Skyline(Rect area, Color c)
        {
            const float step = 96f;
            int i = 0;
            for (float x = area.x; x < area.xMax; x += step, i++)
            {
                // hauteurs pseudo-aleatoires mais STABLES : un Random ici ferait clignoter
                // la ville a chaque frame.
                float h = 90f + Mathf.Abs(Mathf.Sin(i * 12.9898f) * 43758.5453f % 1f) * 260f;
                Fill(new Rect(x + 6f, area.yMax - h, step - 12f, h), c);
            }
        }

        // ---- texte ----

        private static GUIStyle style;

        public static void Text(Rect r, string s, int size, Color c, TextAnchor anchor)
        {
            // Construit au 1er appel, donc TOUJOURS depuis OnGUI : GUI.skin n'existe pas ailleurs.
            style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
            style.fontSize = size;
            style.alignment = anchor;
            style.normal.textColor = Color.white;   // la teinte passe par GUI.color
            GUI.color = c;
            GUI.Label(r, s, style);
        }

        // Texte cerne facon lettrage bulle : le contour est tire en 8 directions.
        public static void TextOutlined(Rect r, string s, int size, Color c, Color outline,
                                        float thickness, TextAnchor anchor)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    Text(new Rect(r.x + dx * thickness, r.y + dy * thickness, r.width, r.height),
                         s, size, outline, anchor);
                }
            Text(r, s, size, c, anchor);
        }

        // Bouton de menu complet : bande + libelle rose cerne, gonfle quand il est vise.
        public static void MenuButton(Rect r, string label, bool selected, float pulse)
        {
            Matrix4x4 m = GUI.matrix;
            if (selected)
            {
                float grow = 1f + 0.03f * Mathf.Sin(pulse * 6f);
                GUIUtility.ScaleAroundPivot(new Vector2(grow, grow), new Vector2(r.x, r.center.y));
            }

            // La bande selectionnee monte en intensite, mais le texte RESTE rose : le passer
            // en blanc sur une bande claircie le rendait illisible.
            Band(r, Cyan);
            if (selected)
            {
                Band(r, new Color(1f, 1f, 1f, 0.55f));
                Text(new Rect(r.x + 12f, r.y, 40f, r.height), ">", 34, Pink, TextAnchor.MiddleCenter);
            }

            var txt = new Rect(r.x + 56f, r.y + r.height * 0.5f - 26f, r.width - 70f, 52f);
            TextOutlined(txt, label.ToUpperInvariant(), 34, Pink, Ink, selected ? 3f : 2.5f,
                         TextAnchor.MiddleLeft);

            GUI.matrix = m;
        }

        // Ligne de resultat de l'ecran de fin : bande + libelle a gauche, valeur a droite.
        public static void StatRow(Rect r, string label, string value, Color bandCol)
        {
            Band(r, bandCol);
            TextOutlined(new Rect(r.x + 34f, r.y, r.width * 0.7f, r.height), label.ToUpperInvariant(), 26,
                         Pink, Ink, 2f, TextAnchor.MiddleLeft);
            TextOutlined(new Rect(r.x, r.y, r.width - 24f, r.height), value, 26,
                         Color.white, Ink, 2f, TextAnchor.MiddleRight);
        }

        // ---- textures cuites ----

        private static Texture2D bandTex, wedgeTex, discTex, burstTex;

        private static Texture2D BandTex => bandTex != null ? bandTex : bandTex = MakeBand();
        private static Texture2D WedgeTex => wedgeTex != null ? wedgeTex : wedgeTex = MakeWedge();
        private static Texture2D DiscTex => discTex != null ? discTex : discTex = MakeDisc();
        private static Texture2D BurstTex => burstTex != null ? burstTex : burstTex = MakeBurst();

        private static Texture2D New(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            return t;
        }

        private static Texture2D MakeBand()
        {
            const int W = 256, H = 32;
            var t = New(W, H);
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                // v=0 en HAUT (les textures GUI sont dessinees a l'envers de l'espace pixel)
                float v = 1f - y / (float)(H - 1);
                float shift = v * Skew * W;   // le haut est decale a droite -> parallelogramme
                for (int x = 0; x < W; x++)
                {
                    float u = (x - shift) / (W - Skew * W);
                    float a = (u < 0f || u > 1f) ? 0f : Mathf.Clamp01(1f - u * u);
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D MakeWedge()
        {
            const int W = 128, H = 128;
            var t = New(W, H);
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float v = 1f - y / (float)(H - 1);
                float shift = v * 0.34f * W;
                for (int x = 0; x < W; x++)
                {
                    float u = (x - shift) / (W - 0.34f * W);
                    px[y * W + x] = new Color(1f, 1f, 1f, u >= 0f && u <= 1f ? 1f : 0f);
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D MakeDisc()
        {
            const int S = 64;
            var t = New(S, S);
            float rad = S * 0.5f;
            for (int j = 0; j < S; j++)
                for (int i = 0; i < S; i++)
                {
                    float d = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), new Vector2(rad, rad));
                    t.SetPixel(i, j, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - d / rad)));
                }
            t.Apply();
            return t;
        }

        private static Texture2D MakeBurst()
        {
            const int S = 128, Spikes = 8;
            var t = New(S, S);
            float rad = S * 0.5f;
            for (int j = 0; j < S; j++)
                for (int i = 0; i < S; i++)
                {
                    Vector2 d = new Vector2(i + 0.5f - rad, j + 0.5f - rad);
                    float len = d.magnitude / rad;
                    float ang = Mathf.Atan2(d.y, d.x);
                    // rayon de l'etoile a cet angle : pointes fines, creux marques
                    float lobe = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * Spikes * 0.5f)), 6f);
                    float edge = Mathf.Lerp(0.18f, 1f, lobe);
                    t.SetPixel(i, j, new Color(1f, 1f, 1f, len > edge ? 0f : Mathf.Clamp01(1f - len / edge)));
                }
            t.Apply();
            return t;
        }
    }
}
