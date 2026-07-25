using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace Gameplay
{
    // Bulle BD cartoon au dessus d'un NPC. Le sprite (coins ronds, contour
    // epais, queue triangle pour la parole / petites bulles pour la pensee)
    // est dessine procéduralement dans une Texture2D au runtime : pas d'asset,
    // pas de prefab. Taille calculee sur le texte reel (TextGenerator),
    // billboard camera en LateUpdate, pop + wobble DOTween.
    public class SpeechBubble : MonoBehaviour
    {
        // constantes (pas serialise : evite les vieilles valeurs figees en scene)
        private const float BubbleHeight = 1.8f; // tip de la queue au dessus du pivot
        private const float MaxWidth = 460f;     // px canvas
        private const int FontSize = 30;

        private const float PxToMeters = 0.0062f;
        private const int Outline = 7;    // epaisseur contour cartoon
        private const int TailH = 34;     // zone queue sous le corps
        private const int PadX = 22, PadY = 15;
        private const int Margin = 4;     // marge texture (antialias du contour)

        private static readonly Color FillSpeech = Color.white;
        private static readonly Color FillThought = new(0.92f, 0.95f, 1f);
        private static readonly Color OutlineCol = new(0.13f, 0.1f, 0.16f);

        private RectTransform root;    // canvas : billboard + pop scale
        private RectTransform visual;  // wobble rotation
        private Image bubbleImage;
        private Text label;
        private Camera cam;
        private Tween popTween, wobbleTween;
        private Texture2D tex;
        private Sprite sprite;

        public bool IsShowing { get; private set; }

        // Start et pas Awake : Pedestrian.Awake reparente tous les enfants
        // sous son Pivot anime — le canvas doit etre cree APRES pour rester
        // accroche a la racine (sinon il danse avec le corps).
        private void Start()
        {
            var canvasGO = new GameObject("SpeechBubble", typeof(Canvas));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.transform.localPosition = Vector3.up * BubbleHeight;
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            root = (RectTransform)canvasGO.transform;
            root.pivot = new Vector2(0.5f, 0f); // pivot = tip de la queue
            root.localScale = Vector3.one * PxToMeters;

            var visualGO = new GameObject("Visual", typeof(Image));
            visualGO.transform.SetParent(root, false);
            visual = (RectTransform)visualGO.transform;
            visual.anchorMin = Vector2.zero;
            visual.anchorMax = Vector2.one;
            visual.offsetMin = Vector2.zero;
            visual.offsetMax = Vector2.zero;
            visual.pivot = new Vector2(0.5f, 0f);
            bubbleImage = visualGO.GetComponent<Image>();

            var textGO = new GameObject("Text", typeof(Text));
            textGO.transform.SetParent(visual, false);
            label = textGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = FontSize;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            var textRect = (RectTransform)textGO.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;

            root.gameObject.SetActive(false);
        }

        public void Show(string text, bool isThought, float duration)
        {
            if (root == null) return; // Start pas encore passe
            IsShowing = true;
            label.fontStyle = isThought ? FontStyle.Italic : FontStyle.Bold;
            label.color = isThought ? new Color(0.25f, 0.28f, 0.42f) : OutlineCol;
            label.text = text;

            // mesure reelle du texte : largeur libre puis hauteur a largeur contrainte
            var gen = new TextGenerator();
            float innerMaxW = MaxWidth - 2 * (PadX + Margin);
            float prefW = gen.GetPreferredWidth(text, label.GetGenerationSettings(Vector2.zero));
            float innerW = Mathf.Min(prefW + 2f, innerMaxW); // +2 : arrondi anti-wrap
            float innerH = gen.GetPreferredHeight(text, label.GetGenerationSettings(new Vector2(innerW, 0f)));

            int w = Mathf.CeilToInt(innerW) + 2 * (PadX + Margin);
            int h = Mathf.CeilToInt(innerH) + 2 * PadY + Margin + TailH;
            root.sizeDelta = new Vector2(w, h);
            var textRect = (RectTransform)label.transform;
            textRect.offsetMin = new Vector2(PadX + Margin, TailH + PadY);
            textRect.offsetMax = new Vector2(-(PadX + Margin), -(PadY + Margin));

            RegenSprite(w, h, isThought);

            root.gameObject.SetActive(true);
            popTween?.Kill();
            wobbleTween?.Kill();
            root.localScale = Vector3.zero;
            popTween = root.DOScale(Vector3.one * PxToMeters, 0.3f).SetEase(Ease.OutBack, 1.4f);
            visual.localRotation = Quaternion.Euler(0, 0, -1.5f);
            wobbleTween = visual.DOLocalRotate(new Vector3(0, 0, 1.5f), 0.9f)
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);

            CancelInvoke(nameof(Hide));
            Invoke(nameof(Hide), duration);
        }

        public void Hide()
        {
            CancelInvoke(nameof(Hide));
            if (!IsShowing) return;
            IsShowing = false;
            wobbleTween?.Kill();
            popTween?.Kill();
            popTween = root.DOScale(Vector3.zero, 0.18f).SetEase(Ease.InBack)
                .OnComplete(() => root.gameObject.SetActive(false));
        }

        private void LateUpdate()
        {
            if (!IsShowing) return;
            if (cam == null) cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            if (cam == null) return;
            root.rotation = cam.transform.rotation; // toujours face camera
        }

        // ---- dessin procédural de la bulle (SDF -> Texture2D) ----

        private void RegenSprite(int w, int h, bool thought)
        {
            if (tex != null) { Destroy(sprite); Destroy(tex); }
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            // corps : rounded rect au dessus de la zone queue
            var halfExt = new Vector2((w - 2f * Margin) * 0.5f, (h - Margin - TailH) * 0.5f);
            var center = new Vector2(w * 0.5f, TailH + halfExt.y);
            float radius = Mathf.Min(thought ? 60f : 28f, Mathf.Min(halfExt.x, halfExt.y));
            float cx = w * 0.5f;

            // queue parole : triangle qui penche vers la gauche
            var t0 = new Vector2(cx - 30f, TailH + 8f);
            var t1 = new Vector2(cx + 4f, TailH + 8f);
            var t2 = new Vector2(cx - 16f, Margin + 1f);

            var px = new Color32[w * h];
            var fill = thought ? FillThought : FillSpeech;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d = RoundRectSDF(p, center, halfExt, radius);
                if (thought)
                {
                    // pensee : deux petites bulles qui descendent
                    d = Mathf.Min(d, (p - new Vector2(cx - 16f, TailH - 1f)).magnitude - 10f);
                    d = Mathf.Min(d, (p - new Vector2(cx - 26f, Margin + 6f)).magnitude - 5.5f);
                }
                else
                {
                    d = Mathf.Min(d, TriangleSDF(p, t0, t1, t2));
                }

                float aShape = Mathf.Clamp01(0.5f - d / 1.6f);          // bord externe
                float tFill = Mathf.Clamp01(0.5f - (d + Outline) / 1.6f); // fill vs contour
                var c = Color.Lerp(OutlineCol, fill, tFill);
                c.a = aShape;
                px[y * w + x] = c;
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 100f,
                0, SpriteMeshType.FullRect);
            bubbleImage.sprite = sprite;
        }

        private static float RoundRectSDF(Vector2 p, Vector2 center, Vector2 halfExt, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x - center.x), Mathf.Abs(p.y - center.y)) - (halfExt - Vector2.one * r);
            var qc = Vector2.Max(q, Vector2.zero);
            return qc.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float TriangleSDF(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d = Mathf.Min(SegDist(p, a, b), Mathf.Min(SegDist(p, b, c), SegDist(p, c, a)));
            float s0 = Cross(b - a, p - a);
            float s1 = Cross(c - b, p - b);
            float s2 = Cross(a - c, p - c);
            bool inside = (s0 >= 0 && s1 >= 0 && s2 >= 0) || (s0 <= 0 && s1 <= 0 && s2 <= 0);
            return inside ? -d : d;
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * t).magnitude;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private void OnDestroy()
        {
            popTween?.Kill();
            wobbleTween?.Kill();
            if (tex != null) { Destroy(sprite); Destroy(tex); }
        }
    }
}
