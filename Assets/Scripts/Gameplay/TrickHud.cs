using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using DG.Tweening;

namespace Gameplay
{
    // Feedback de figures "slap" a l'ecran (screen-space, sticker qui claque).
    // Deux types :
    //  - LIVE : pendant la figure, un slap par axe (SPIN/FLIP) qui s'affiche en
    //    temps reel et ESCALADE (FLIP -> DOUBLE FLIP -> ... -> MONSTER FLIP), avec
    //    un punch a chaque palier ;
    //  - ONE-SHOT : score final du saut, drift, banque de chaine, ou RATE. Ceux-la
    //    portent le STICKER de la maquette Figma ("In Game slaps de figures") :
    //    eclat de rayon jaune cerne de noir + lettrage rose cerne et ombre portee.
    // 100% procedural (Canvas overlay + Text runtime), aucun asset.
    public class TrickHud : MonoBehaviour
    {
        // Couleurs de la maquette Figma.
        private static readonly Color StickerPink = new Color(0.856f, 0.062f, 0.710f);   // #DA10B5
        private static readonly Color BoltYellow = new Color(1f, 0.853f, 0.019f);        // #FFDA05
        private static readonly Color BoltRed = new Color(0.885f, 0.021f, 0.021f);       // rate
        private static readonly Color Ink = new Color(0f, 0f, 0f, 1f);

        // Bande des slaps LIVE : eux restent empiles au meme endroit, ils se REECRIVENT
        // en place au fil de la figure (FLIP -> DOUBLE FLIP -> ...). Les faire sauter
        // d'un coin a l'autre a chaque palier serait illisible.
        private const float TopBand = 330f;

        // POINTS DE COLLAGE des stickers one-shot : une couronne autour de l'aire de jeu,
        // pour qu'ils claquent un peu partout comme de vrais autocollants. Deux zones
        // restent vides : le couloir central (la route droit devant) et le coin
        // haut-gauche (jauge de popularite). Un eclat de rayon fait ~10.4x le corps du
        // texte en largeur -- d'ou les |x| eleves : plus pres du centre il le mordrait.
        // Coordonnees du canvas de reference 1920x1080 (0 = centre, +-960 / +-540 = bords).
        private static readonly Vector2[] SlapSpots =
        {
            new Vector2(-560f,  190f), new Vector2( 560f, -350f),
            new Vector2( 600f,  -20f), new Vector2(-580f, -330f),
            new Vector2(-520f,  -60f), new Vector2( 540f,  300f),
            new Vector2(   0f, -400f), new Vector2( 620f,  150f),
        };
        private int spot;

        private Canvas canvas;
        private Font font;
        private bool heavyFont;
        private readonly Dictionary<string, Text> lives = new();
        private readonly List<string> liveOrder = new();

        private void Awake()
        {
            // Police de la maquette (Sigmar One, SIL OFL, cf Assets/Resources/Fonts).
            // Elle n'a QU'UNE graisse, et elle est deja tres grasse : lui appliquer le
            // gras de synthese d'uGUI empate le lettrage. D'ou le fontStyle conditionnel.
            font = Resources.Load<Font>("Fonts/SigmarOne");
            heavyFont = font != null;
            spot = Random.Range(0, SlapSpots.Length);   // deux runs ne demarrent pas au meme coin
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("TrickHud", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        // ---- LIVE (pendant la figure) ----

        // Cree ou met a jour le slap live de cet axe. escalate=true -> punch (palier).
        public void SetLive(string key, string label, Color col, bool escalate)
        {
            if (lives.TryGetValue(key, out var t) && t != null)
            {
                t.text = label;
                t.color = col;
                if (escalate)
                {
                    var prt = (RectTransform)t.transform.parent;
                    prt.DOKill();
                    prt.localScale = Vector3.one;
                    prt.DOPunchScale(Vector3.one * 0.35f, 0.25f, 9, 0.8f);
                    prt.DOPunchRotation(new Vector3(0, 0, 6f), 0.25f, 8, 0.8f);
                }
                return;
            }
            int slot = liveOrder.Count;
            var rt = NewSlap(label, col, 70, new Vector2(0f, TopBand - slot * 92f), null);
            rt.localScale = Vector3.one * 2.4f;
            rt.localRotation = Quaternion.Euler(0, 0, Random.Range(-9f, 9f));
            rt.DOScale(1f, 0.16f).SetEase(Ease.OutBack, 3.2f);
            rt.DOShakeAnchorPos(0.18f, 12f, 20, 90f, false, false);
            lives[key] = rt.GetComponentInChildren<Text>();
            liveOrder.Add(key);
        }

        // Fin de figure : les slaps live disparaissent (pop out + fade).
        public void ClearLive()
        {
            foreach (var kv in lives)
            {
                var t = kv.Value; if (t == null) continue;
                var rt = (RectTransform)t.transform.parent;
                rt.DOKill();
                rt.DOScale(1.2f, 0.18f);
                DOTween.Sequence().AppendInterval(0.02f)
                    .Append(FadeOut(rt, 0.16f)).OnComplete(() => { if (rt) Destroy(rt.gameObject); });
            }
            lives.Clear();
            liveOrder.Clear();
        }

        // ---- ONE-SHOT ----

        // landed=true : atterrissage de figure -> gros sticker. Sinon (drift au sol) :
        // sticker plus petit. Dans les deux cas il se colle sur un point libre de la
        // couronne, pas toujours au meme endroit.
        // Le multiplicateur reste en tete de ligne comme le "x10" de la maquette.
        public void ScorePop(string label, int points, int mult, bool landed)
        {
            string mtxt = mult > 1 ? "x" + mult + " " : "";
            string txt = mtxt + label + "  +" + points.ToString("N0");
            OneShot(txt, landed ? 78 : 66, 1.0f, BoltYellow);
        }

        public void Bank(int amount, int combo)
        {
            // 74 et non 90 : au-dela, l'eclat devient plus large que l'ecartement des
            // points de collage et finit toujours recadre au meme endroit -- le sticker
            // perdrait justement ce qu'on cherche, l'imprevu de la position.
            OneShot("CHAINE x" + combo + " !  +" + amount.ToString("N0"), 74, 1.2f, BoltYellow);
        }

        // Rate : meme lettrage rose que la maquette, mais l'eclat passe au ROUGE --
        // sans ca l'echec serait typographiquement identique a une reussite.
        public void Wasted()
        {
            OneShot("RATE !", 76, 1.0f, BoltRed);
        }

        // Choisit le prochain point de collage. Le pas de 3 sur 8 points est PREMIER
        // avec 8 : on passe par tous les points sans jamais en reprendre un de suite,
        // et deux stickers consecutifs atterrissent loin l'un de l'autre -- ce qu'un
        // tirage purement aleatoire ne garantit pas (il recolle volontiers au meme
        // endroit deux fois d'affilee).
        private Vector2 NextSpot(int size)
        {
            Vector2 p = SlapSpots[spot];
            spot = (spot + 3) % SlapSpots.Length;
            p += new Vector2(Random.Range(-35f, 35f), Random.Range(-25f, 25f));

            // Recadrage : un gros sticker (CHAINE, RATE) est plus large que l'ecartement
            // des points, il deborderait. On le ramene dans l'ecran plutot que de le
            // laisser sortir par le bord.
            float halfW = size * 10.4f * 0.5f;
            float halfH = halfW * BoltAspect;
            p.x = Mathf.Clamp(p.x, -(960f - halfW - 10f), 960f - halfW - 10f);
            p.y = Mathf.Clamp(p.y, -(540f - halfH - 10f), 540f - halfH - 10f);

            // Une fois recadre, un sticker trop large barre forcement le couloir central.
            // Dans ce cas on le pose TOUT EN BAS : la il couvre le decor et pas la route.
            // Jamais en haut -- c'est la que vit la jauge de popularite.
            const float Corridor = 60f;
            if (p.x - halfW < Corridor && p.x + halfW > -Corridor)
                p.y = -(540f - halfH - 10f);
            return p;
        }

        private void OneShot(string text, int size, float hold, Color bolt)
        {
            var rt = NewSlap(text, StickerPink, size, NextSpot(size), bolt);
            float tilt = Random.Range(-11f, 11f);
            rt.localScale = Vector3.one * 2.6f;
            rt.localRotation = Quaternion.Euler(0, 0, tilt * 1.6f);
            var seq = DOTween.Sequence();
            seq.Append(rt.DOScale(1f, 0.16f).SetEase(Ease.OutBack, 3.2f));
            seq.Join(rt.DOLocalRotate(new Vector3(0, 0, tilt), 0.16f).SetEase(Ease.OutBack));
            seq.Join(rt.DOShakeAnchorPos(0.18f, 14f, 22, 90f, false, false));
            seq.AppendInterval(hold);
            seq.Append(rt.DOScale(1.25f, 0.22f).SetEase(Ease.InQuad));
            seq.Join(FadeOut(rt, 0.22f));
            seq.OnComplete(() => { if (rt) Destroy(rt.gameObject); });
        }

        // ---- helpers ----

        // `bolt` non nul -> l'eclat de rayon de la maquette est glisse DERRIERE le texte.
        private RectTransform NewSlap(string text, Color col, int size, Vector2 pos, Color? bolt)
        {
            var rt = new GameObject("Slap", typeof(RectTransform), typeof(CanvasGroup))
                     .GetComponent<RectTransform>();
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(1400, 200);
            if (bolt.HasValue) MakeBolt(rt, size, bolt.Value);
            MakeText(rt, text, size, col);
            return rt;
        }

        // Eclat de rayon : l'echelle suit le corps du texte (la maquette pose un eclair
        // de ~670 de large pour un lettrage de 64).
        private void MakeBolt(RectTransform parent, int size, Color col)
        {
            var go = new GameObject("Bolt", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            float w = size * 10.4f;
            rt.sizeDelta = new Vector2(w, w * BoltAspect);
            rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-6f, 6f));
            var img = go.GetComponent<Image>();
            img.sprite = BoltSprite();
            img.color = col;
            img.raycastTarget = false;
        }

        private void MakeText(RectTransform parent, string s, int size, Color col)
        {
            var go = new GameObject("T", typeof(Text), typeof(Outline), typeof(Shadow));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = heavyFont ? FontStyle.Normal : FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.color = col;
            t.text = s;
            // Cerne noir + ombre portee decalee vers le bas-gauche : les deux effets de la
            // maquette (contour 5, ombre -7/+8 en repere Figma, donc -7/-8 en repere uGUI).
            var outline = go.GetComponent<Outline>();
            outline.effectColor = Ink;
            outline.effectDistance = new Vector2(5f, -5f);
            var shadow = go.GetComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.5f);
            shadow.effectDistance = new Vector2(-7f, -8f);
        }

        // Fondu du slap ENTIER (eclat compris) : un CanvasGroup evite d'aller teinter
        // texte, contour et ombre un par un.
        private static Tween FadeOut(RectTransform rt, float dur)
        {
            var cg = rt.GetComponent<CanvasGroup>();
            return DOVirtual.Float(1f, 0f, dur, v => { if (cg != null) cg.alpha = v; });
        }

        // ---- eclat de rayon (cuit une fois) ----

        // Contour de l'eclair de la maquette, DEJA pivote (Figma le pose a 56.9 deg) et
        // exprime dans son propre repere. Repere Figma : y vers le BAS.
        private static readonly Vector2[] BoltPath =
        {
            new Vector2(0f, 0f), new Vector2(285.95f, -34.64f), new Vector2(158.96f, -68.79f),
            new Vector2(564.84f, -61.18f), new Vector2(374.99f, -50.29f), new Vector2(668.55f, -18.88f),
            new Vector2(440.67f, 32.02f), new Vector2(659.60f, 68.95f),
        };
        private const float BoltAspect = 147.74f / 678.55f;   // hauteur / largeur, marge comprise

        private static Sprite boltSprite;

        private static Sprite BoltSprite()
        {
            if (boltSprite != null) return boltSprite;
            const float stroke = 2.5f, pad = 5f;   // stroke 5 centre sur le trace
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in BoltPath)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            int w = Mathf.CeilToInt(maxX - minX + pad * 2f);
            int h = Mathf.CeilToInt(maxY - minY + pad * 2f);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    // ligne 0 = BAS de la texture, donc y le plus grand du repere Figma
                    var p = new Vector2(minX - pad + i + 0.5f, maxY + pad - j - 0.5f);
                    float d = DistToPath(p);
                    float sd = Inside(p) ? d : -d;                    // > 0 = dans la forme
                    Color c = Color.white;                            // l'Image teinte l'aplat
                    c.a = Mathf.Clamp01(sd + stroke + 0.5f);          // silhouette + cerne
                    // Le cerne est peint en noir OPAQUE par dessus la teinte : c'est la
                    // seule zone du sprite qui ne doit pas suivre img.color.
                    float fill = Mathf.Clamp01(sd - stroke + 0.5f);
                    px[j * w + i] = fill >= 1f ? c : Color.Lerp(new Color(0f, 0f, 0f, c.a), c, fill);
                }
            tex.SetPixels(px);
            tex.Apply();
            boltSprite = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f));
            boltSprite.hideFlags = HideFlags.HideAndDontSave;
            return boltSprite;
        }

        // Remplissage EVEN-ODD (parite des croisements). Le zigzag revient sur lui-meme :
        // en NONZERO les allers et les retours s'annulent et la forme ressort VIDE.
        private static bool Inside(Vector2 p)
        {
            bool inside = false;
            for (int a = 0, b = BoltPath.Length - 1; a < BoltPath.Length; b = a++)
            {
                Vector2 pa = BoltPath[a], pb = BoltPath[b];
                if ((pa.y > p.y) != (pb.y > p.y) &&
                    p.x < (pb.x - pa.x) * (p.y - pa.y) / (pb.y - pa.y) + pa.x)
                    inside = !inside;
            }
            return inside;
        }

        private static float DistToPath(Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < BoltPath.Length; i++)
            {
                Vector2 a = BoltPath[i], b = BoltPath[(i + 1) % BoltPath.Length];
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }
    }
}
