using UnityEngine;

namespace Gameplay
{
    // Affichage DEBUG du score courant (= TrickSystem.Total, la banque des figures).
    // Coin haut-gauche, look enseigne neon : cadre cyan, chiffre gras a ombre portee,
    // pop elastique + flash a chaque gain. OnGUI -> zero Canvas, comme GrindBalanceHud.
    public class ScoreHud : MonoBehaviour
    {
        [SerializeField] private TrickSystem tricks;
        [SerializeField] private Vector2 screenPos = new Vector2(28f, 24f);
        [SerializeField] private float scale = 1f;

        private int shown;      // valeur affichee (rattrape la vraie en roulant)
        private int last;
        private float pop;      // 0..1, retombe apres un gain
        private float t;

        private void Awake()
        {
            if (tricks == null) tricks = GetComponent<TrickSystem>();
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
            if (tricks != null) shown = last = tricks.Total;
        }

        private void Update()
        {
            if (tricks == null) return;
            float dt = Time.unscaledDeltaTime;
            t += dt;

            int real = tricks.Total;
            if (real != last) { pop = 1f; last = real; }
            pop = Mathf.Max(0f, pop - dt * 2.2f);

            // le compteur roule vers la vraie valeur (jamais d'arrivee seche)
            if (shown != real)
            {
                int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(real - shown) * dt * 6f));
                shown += real > shown ? Mathf.Min(step, real - shown) : -Mathf.Min(step, shown - real);
            }
        }

        private void OnGUI()
        {
            if (tricks == null) return;

            float s = scale * (1f + 0.22f * EaseOutBack(pop));
            float w = 250f, h = 74f;
            float x = screenPos.x, y = screenPos.y;

            Matrix4x4 m0 = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(x, y));

            var box = new Rect(x, y, w, h);
            float glow = 0.25f + 0.55f * pop + 0.06f * Mathf.Sin(t * 2.4f);

            Fill(new Rect(box.x - 4f, box.y - 4f, box.width + 8f, box.height + 8f),
                 new Color(0.15f, 0.95f, 1f, glow));                       // halo neon
            Fill(box, new Color(0.05f, 0.06f, 0.12f, 0.92f));              // fond
            Frame(box, 2f, Color.Lerp(new Color(0.2f, 0.9f, 1f), Color.white, pop));

            // liseres d'angle facon enseigne
            Fill(new Rect(box.x, box.y, 26f, 3f), new Color(1f, 0.25f, 0.85f, 0.95f));
            Fill(new Rect(box.xMax - 26f, box.yMax - 3f, 26f, 3f), new Color(1f, 0.25f, 0.85f, 0.95f));

            Label(new Rect(box.x + 12f, box.y + 5f, w, 20f), "SCORE", 13, FontStyle.Bold,
                  new Color(0.55f, 0.85f, 1f, 0.9f), TextAnchor.UpperLeft);

            var num = new Rect(box.x + 12f, box.y + 22f, w - 24f, 46f);
            Label(new Rect(num.x + 3f, num.y + 4f, num.width, num.height), shown.ToString("N0"), 38,
                  FontStyle.Bold, new Color(0f, 0f, 0f, 0.55f), TextAnchor.UpperLeft);   // ombre cartoon
            Label(num, shown.ToString("N0"), 38, FontStyle.Bold,
                  Color.Lerp(new Color(0.85f, 0.98f, 1f), new Color(1f, 0.95f, 0.4f), pop),
                  TextAnchor.UpperLeft);

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        private static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        private static void Frame(Rect r, float th, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, th), c);
            Fill(new Rect(r.x, r.yMax - th, r.width, th), c);
            Fill(new Rect(r.x, r.y, th, r.height), c);
            Fill(new Rect(r.xMax - th, r.y, th, r.height), c);
        }

        private static void Label(Rect r, string txt, int size, FontStyle st, Color c, TextAnchor a)
        {
            GUI.color = c;
            GUI.Label(r, txt, new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = st, alignment = a });
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 2.2f, c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }
    }
}
