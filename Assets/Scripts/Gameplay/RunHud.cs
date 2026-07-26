using UnityEngine;

namespace Gameplay
{
    // LES COMPTEURS, a l'ecran. Le theme peut etre parfaitement code et rester invisible :
    // c'est ce composant qui le rend lisible.
    //   1. COLIS RESTANTS : le compteur de victoire, en cases qui s'eteignent une a une ;
    //   2. CHAINE : le 3e compteur, la fenetre de combo en compte a rebours -- c'est lui qui
    //      dit au joueur qu'il a 2.6 s pour relancer, livraison comprise ;
    //   3. l'ESCALADE du dernier colis (la fonte s'accelere) est pilotee ici aussi, parce
    //      qu'elle se declenche sur le meme compteur que le HUD affiche.
    // Rendu OnGUI -> zero Canvas, comme ScoreGauge / GrindBalanceHud / TrickHud.
    public class RunHud : MonoBehaviour
    {
        [SerializeField] private TrickSystem tricks;

        [Header("Placement (unites de design, cf referenceHeight)")]
        // sous la jauge de popularite (frame a y=140, h=38, puis la ligne CUMUL)
        [SerializeField] private Vector2 screenPos = new Vector2(28f, 212f);
        [SerializeField] private float width = 420f;
        [SerializeField] private float referenceHeight = 1080f;

        [Header("Dernier colis")]
        // Quand il n'en reste qu'un, la fonte s'accelere : la fin de run doit se sentir.
        [SerializeField] private float finalRushDrain = 1.5f;

        private int remaining, total, shownRemaining;
        private float pop;        // 0..1, claque a chaque decrement
        private float t;
        private bool rush;

        private void Awake()
        {
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
        }

        private void OnDisable()
        {
            // Ne pas laisser la fonte acceleree derriere soi.
            if (ScoreGauge.Instance != null) ScoreGauge.Instance.DrainMultiplier = 1f;
        }

        private void Update()
        {
            float ddt = Time.unscaledDeltaTime;
            t += ddt;
            pop = Mathf.Max(0f, pop - ddt * 2f);

            total = DeliveryQuest.Total;
            remaining = DeliveryQuest.Remaining;
            if (remaining != shownRemaining)
            {
                if (remaining < shownRemaining) pop = 1f;   // claque au decrement, pas a l'init
                shownRemaining = remaining;
            }

            // Escalade : uniquement s'il reste VRAIMENT un dernier colis parmi plusieurs
            // (sinon une scene a une seule quete demarrerait deja en rush).
            rush = total > 1 && remaining == 1;
            if (ScoreGauge.Instance != null)
                ScoreGauge.Instance.DrainMultiplier = rush ? finalRushDrain : 1f;
        }

        private void OnGUI()
        {
            if (RunEnd.Finished || UI.MenuFlow.Blocking) return;   // ecran de fin / menus
            if (total <= 0) return;           // pas de quete en scene : pas de compteur

            Matrix4x4 m0 = GUI.matrix;
            float k = referenceHeight > 1f ? Screen.height / referenceHeight : 1f;
            GUIUtility.ScaleAroundPivot(new Vector2(k, k), Vector2.zero);

            DrawPackages();
            DrawChain();

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // COMPTEUR DE COLIS : une case par livraison, elles s'ETEIGNENT au fur et a mesure ->
        // c'est un compte a rebours, pas une barre de progression.
        private void DrawPackages()
        {
            float shake = rush ? 3f : 0f;
            float x = screenPos.x + Mathf.Sin(t * 41f) * shake;
            float y = screenPos.y + Mathf.Cos(t * 47f) * shake * 0.7f;

            Color hue = rush
                ? Color.Lerp(new Color(1f, 0.25f, 0.2f), Color.white, 0.35f + 0.35f * Mathf.Sin(t * 10f))
                : new Color(1f, 0.45f, 0.85f);

            Matrix4x4 m = GUI.matrix;
            float s = 1f + 0.16f * EaseOutBack(pop);
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(x + width * 0.5f, y + 22f));

            Label(new Rect(x + 1f, y - 2f, 260f, 20f),
                  rush ? "DERNIER COLIS !" : "COLIS RESTANTS", 13,
                  rush ? hue : new Color(0.62f, 0.8f, 1f, 0.9f), TextAnchor.UpperLeft);

            // gros chiffre a droite du libelle
            Label(new Rect(x, y - 6f, width - 2f, 26f), remaining + " / " + total, 20,
                  Color.white, TextAnchor.UpperRight);

            // rangee de cases
            const float h = 16f, gap = 4f;
            float cw = (width - gap * (total - 1)) / Mathf.Max(1, total);
            for (int i = 0; i < total; i++)
            {
                var cell = new Rect(x + i * (cw + gap), y + 22f, cw, h);
                bool lit = i < remaining;                  // les livres s'eteignent par la droite
                Fill(new Rect(cell.x - 1f, cell.y - 1f, cell.width + 2f, cell.height + 2f),
                     new Color(0.02f, 0.02f, 0.05f, 0.85f));
                Fill(cell, lit ? hue : new Color(0.14f, 0.15f, 0.24f, 0.95f));
                if (lit)
                    Fill(new Rect(cell.x, cell.y, cell.width, cell.height * 0.45f),
                         new Color(1f, 1f, 1f, 0.16f));    // brillance, look plastique
            }

            GUI.matrix = m;
        }

        // COMPTE A REBOURS DE CHAINE. window ne descend qu'au sol : en l'air la barre reste
        // pleine, ce qui traduit exactement la regle du jeu.
        private void DrawChain()
        {
            if (tricks == null || !tricks.ChainAlive) return;

            float f = tricks.ComboWindow01;
            float x = screenPos.x, y = screenPos.y + 52f;
            bool urgent = f < 0.35f;
            Color col = urgent
                ? Color.Lerp(new Color(1f, 0.3f, 0.2f), new Color(1f, 0.85f, 0.2f), f / 0.35f)
                : new Color(1f, 0.85f, 0.25f);

            Label(new Rect(x + 1f, y, 260f, 20f), "CHAINE  x" + tricks.ComboCount, 13, col,
                  TextAnchor.UpperLeft);
            Label(new Rect(x, y, width - 2f, 20f), "+" + tricks.ChainScore.ToString("N0"), 14,
                  new Color(1f, 1f, 1f, 0.85f), TextAnchor.UpperRight);

            var frame = new Rect(x, y + 20f, width, 10f);
            Fill(new Rect(frame.x - 1f, frame.y - 1f, frame.width + 2f, frame.height + 2f),
                 new Color(0.02f, 0.02f, 0.05f, 0.9f));
            Fill(frame, new Color(0.12f, 0.13f, 0.2f, 0.95f));
            Fill(new Rect(frame.x, frame.y, frame.width * f, frame.height), col);
        }

        private static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        private static void Label(Rect r, string txt, int size, Color c, TextAnchor a)
        {
            GUI.color = c;
            GUI.Label(r, txt, new GUIStyle(GUI.skin.label)
            {
                fontSize = size, fontStyle = FontStyle.Bold, alignment = a, wordWrap = false,
            });
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 2.2f, c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }
    }
}
