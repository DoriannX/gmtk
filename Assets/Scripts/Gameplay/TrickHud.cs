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
    //  - ONE-SHOT : score final du saut, drift, banque de chaine, ou RATE.
    // 100% procedural (Canvas overlay + Text runtime), aucun asset.
    public class TrickHud : MonoBehaviour
    {
        private static readonly Color[] Pop =
        {
            new Color(1f, 0.85f, 0.15f), new Color(1f, 0.5f, 0.2f),
            new Color(1f, 0.3f, 0.55f), new Color(0.35f, 0.8f, 1f), new Color(0.6f, 1f, 0.35f),
        };
        private static readonly Color Bad = new Color(1f, 0.3f, 0.3f);
        private static readonly Color Good = new Color(0.5f, 1f, 0.45f);

        private Canvas canvas;
        private Font font;
        private int popCount;
        private readonly Dictionary<string, Text> lives = new();
        private readonly List<string> liveOrder = new();

        private void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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
            var rt = NewSlap(label, col, 70, new Vector2(0f, 130f - slot * 92f));
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
                    .Append(FadeOut(t, 0.16f)).OnComplete(() => { if (rt) Destroy(rt.gameObject); });
            }
            lives.Clear();
            liveOrder.Clear();
        }

        // ---- ONE-SHOT ----

        // landed=true : atterrissage de figure -> TAMPON de validation (vert, coche,
        // centre) bien distinct du slap LIVE en haut (le nom de figure pendant le
        // saut). Sinon (drift au sol) : pop colore classique en bas.
        public void ScorePop(string label, int points, int mult, bool landed)
        {
            string mtxt = mult > 1 ? " <color=#FFE24D>x" + mult + "</color>" : "";
            if (landed)
            {
                string txt = "<color=#B6FF7A>✓</color> " + label + mtxt
                    + "  <color=#FFFFFF>+" + points.ToString("N0") + "</color>";
                OneShot(txt, Good, 78, new Vector2(0f, -40f), 1.0f);
                return;
            }
            var col = Pop[popCount % Pop.Length];
            string dtxt = label + mtxt + "  <color=#FFFFFF>+" + points.ToString("N0") + "</color>";
            popCount++;
            OneShot(dtxt, col, 66, new Vector2(Random.Range(-160f, 160f), -120f), 0.9f);
        }

        public void Bank(int amount, int combo)
        {
            OneShot("CHAINE x" + combo + " !  <color=#FFFFFF>+" + amount.ToString("N0") + "</color>",
                Good, 90, new Vector2(0, -220f), 1.2f);
        }

        public void Wasted()
        {
            OneShot("RATE !", Bad, 96, new Vector2(0, 40f), 1.0f);
        }

        private void OneShot(string text, Color col, int size, Vector2 pos, float hold)
        {
            var rt = NewSlap(text, col, size, pos);
            var t = rt.GetComponentInChildren<Text>();
            float tilt = Random.Range(-11f, 11f);
            rt.localScale = Vector3.one * 2.6f;
            rt.localRotation = Quaternion.Euler(0, 0, tilt * 1.6f);
            var seq = DOTween.Sequence();
            seq.Append(rt.DOScale(1f, 0.16f).SetEase(Ease.OutBack, 3.2f));
            seq.Join(rt.DOLocalRotate(new Vector3(0, 0, tilt), 0.16f).SetEase(Ease.OutBack));
            seq.Join(rt.DOShakeAnchorPos(0.18f, 14f, 22, 90f, false, false));
            seq.AppendInterval(hold);
            seq.Append(rt.DOScale(1.25f, 0.22f).SetEase(Ease.InQuad));
            seq.Join(FadeOut(t, 0.22f));
            seq.OnComplete(() => { if (rt) Destroy(rt.gameObject); });
        }

        // ---- helpers ----

        private RectTransform NewSlap(string text, Color col, int size, Vector2 pos)
        {
            var rt = new GameObject("Slap", typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(1400, 200);
            MakeText(rt, text, size, col);
            return rt;
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
            t.fontStyle = FontStyle.BoldAndItalic;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.color = col;
            t.text = s;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = new Color(0.1f, 0.08f, 0.14f, 1f);
            outline.effectDistance = new Vector2(4f, -4f);
            var shadow = go.GetComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.4f);
            shadow.effectDistance = new Vector2(6f, -7f);
        }

        private Tween FadeOut(Text t, float dur)
        {
            return DOVirtual.Float(1f, 0f, dur, v =>
            {
                if (t == null) return;
                var c = t.color; c.a = v; t.color = c;
                var o = t.GetComponent<Outline>(); if (o) { var oc = o.effectColor; oc.a = v; o.effectColor = oc; }
                var sh = t.GetComponent<Shadow>(); if (sh) { var sc = sh.effectColor; sc.a = v * 0.4f; sh.effectColor = sc; }
            });
        }
    }
}
