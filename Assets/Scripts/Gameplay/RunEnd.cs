using UnityEngine;
using UnityEngine.SceneManagement;
using Core;

namespace Gameplay
{
    // FIN DE PARTIE : les deux compteurs du jeu se courent apres, et c'est celui qui touche
    // zero en premier qui decide de l'issue.
    //   POPULARITE (ScoreGauge.Value) tombe a 0 -> DEFAITE ;
    //   COLIS RESTANTS (DeliveryQuest.Remaining) tombe a 0 -> VICTOIRE.
    // Un seul composant pour les deux issues : elles partagent tout l'ecran de resultats.
    // Rendu OnGUI -> zero Canvas, comme ScoreGauge / GrindBalanceHud / TrickHud.
    public class RunEnd : MonoBehaviour
    {
        // Lu par les HUD pour se ranger pendant l'ecran de fin.
        public static bool Finished { get; private set; }

        [SerializeField] private TrickSystem tricks;
        [SerializeField] private float referenceHeight = 1080f;
        // Le joueur martelait peut-etre le saut a l'instant de mourir : sans ce delai il
        // relancerait la partie sans avoir lu une seule ligne de resultats.
        [SerializeField] private float inputDelay = 0.7f;
        // Respiration entre la derniere livraison et l'ecran de victoire : le temps que la
        // chaine se banque et que le "+N" de la jauge se joue.
        [SerializeField] private float winDelay = 1.4f;

        private bool won;
        private float t;      // temps ecoule depuis la fin, en NON scale (le jeu est gele)
        private float winT;
        private Texture2D disc;

        private void Awake()
        {
            Finished = false;                 // static : doit repartir propre a chaque rechargement
            Time.timeScale = 1f;              // au cas ou on revient d'un ecran de fin
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
            disc = MakeDisc(64);
        }

        private void OnDestroy()
        {
            // Sortir de la scene en laissant timeScale a 0 gelerait la suivante.
            if (Finished) Time.timeScale = 1f;
        }

        private void Update()
        {
            if (Finished)
            {
                t += Time.unscaledDeltaTime;
                if (t >= inputDelay && InputActions.GetActionPressed()) Retry();
                return;
            }

            // VICTOIRE. Total > 0 : sans ce garde-fou une scene sans quete "gagnerait" des la
            // 1re frame, les DeliveryQuest s'enregistrant dans All au OnEnable, pas avant.
            // On ne fige pas dans la frame : les points de la derniere livraison sont encore
            // dans la chaine (ils n'atteignent la jauge qu'au Bank), l'ecran de resultats les
            // oublierait. On banque, on laisse le juice se jouer, PUIS on fige. Le branchement
            // sort ici : une fois la victoire acquise, la fonte ne peut plus la reprendre.
            if (DeliveryQuest.Total > 0 && DeliveryQuest.Remaining == 0)
            {
                if (tricks != null && tricks.ChainAlive) tricks.ForceBank();
                winT += Time.deltaTime;
                if (winT >= winDelay) End(true);
                return;
            }

            if (ScoreGauge.Instance == null || !ScoreGauge.Instance.Empty) return;

            // Derniere chance : les gains passent par la chaine et n'atteignent la jauge qu'au
            // Bank(), jusqu'a 2.6 s apres coup. Mourir sur une chaine pleine -- typiquement
            // juste apres une livraison -- serait vole. On la banque et on retente au prochain
            // passage : la jauge aura absorbe les points d'ici la.
            if (tricks != null && tricks.ChainAlive) { tricks.ForceBank(); return; }

            End(false);
        }

        private void End(bool victory)
        {
            won = victory;
            Finished = true;
            t = 0f;
            Time.timeScale = 0f;
        }

        private void Retry()
        {
            Finished = false;
            Time.timeScale = 1f;   // AVANT le chargement, sinon la scene rechargee reste gelee
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void OnGUI()
        {
            if (!Finished) return;

            Matrix4x4 m0 = GUI.matrix;
            float k = referenceHeight > 1f ? Screen.height / referenceHeight : 1f;
            GUIUtility.ScaleAroundPivot(new Vector2(k, k), Vector2.zero);
            float sw = Screen.width / k, sh = Screen.height / k;

            // fondu qui s'installe (non scale : le jeu est gele)
            float fade = Mathf.Clamp01(t * 2.2f);
            Fill(new Rect(0f, 0f, sw, sh), new Color(0.02f, 0.02f, 0.06f, 0.86f * fade));

            Color hue = won ? new Color(0.35f, 1f, 0.6f) : new Color(1f, 0.3f, 0.35f);
            const float pw = 720f, ph = 420f;
            // pop d'arrivee du panneau
            float pop = EaseOutBack(Mathf.Clamp01(t * 1.8f));
            var panel = new Rect((sw - pw) * 0.5f, (sh - ph) * 0.5f, pw, ph);
            GUIUtility.ScaleAroundPivot(new Vector2(pop, pop), panel.center);

            Blit(panel.center.x, panel.center.y, pw * 0.62f, new Color(hue.r, hue.g, hue.b, 0.16f));
            Fill(panel, new Color(0.05f, 0.06f, 0.12f, 0.97f));
            Frame(panel, 4f, hue);
            // liseres d'angle facon enseigne, comme la jauge
            Fill(new Rect(panel.x, panel.y - 5f, 90f, 4f), new Color(1f, 0.25f, 0.85f, 0.95f));
            Fill(new Rect(panel.xMax - 90f, panel.yMax + 1f, 90f, 4f), new Color(1f, 0.25f, 0.85f, 0.95f));

            Label(new Rect(panel.x, panel.y + 34f, panel.width, 70f),
                  won ? "TOUS LES COLIS LIVRES !" : "PLUS PERSONNE NE TE REGARDE",
                  won ? 52 : 40, hue, TextAnchor.UpperCenter);
            Label(new Rect(panel.x, panel.y + 96f, panel.width, 30f),
                  won ? "VICTOIRE" : "GAME OVER", 22,
                  new Color(1f, 1f, 1f, 0.55f), TextAnchor.UpperCenter);

            float y = panel.y + 158f;
            Row(panel, ref y, "COLIS LIVRES", DeliveryQuest.DeliveredCount + " / " + DeliveryQuest.Total);
            Row(panel, ref y, "POPULARITE GAGNEE",
                (ScoreGauge.Instance != null ? ScoreGauge.Instance.Earned : 0).ToString("N0"));
            Row(panel, ref y, "MEILLEURE CHAINE",
                tricks != null ? "x" + tricks.BestCombo : "--");
            Row(panel, ref y, "PLUS LONG GRIND",
                tricks != null ? Mathf.RoundToInt(tricks.BestGrind) + " m" : "--");

            // invite au retry, une fois le delai passe
            if (t >= inputDelay)
            {
                float blink = 0.65f + 0.35f * Mathf.Sin(t * 5f);
                Label(new Rect(panel.x, panel.yMax - 58f, panel.width, 30f),
                      "A  /  ESPACE   ->   REJOUER", 20,
                      new Color(1f, 1f, 1f, blink), TextAnchor.UpperCenter);
            }

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // Une ligne de resultat : libelle a gauche, valeur a droite, filet entre les deux.
        private static void Row(Rect panel, ref float y, string label, string value)
        {
            float x = panel.x + 60f, w = panel.width - 120f;
            Label(new Rect(x, y, w, 26f), label, 17, new Color(0.62f, 0.8f, 1f, 0.8f), TextAnchor.UpperLeft);
            Label(new Rect(x, y - 3f, w, 30f), value, 24, Color.white, TextAnchor.UpperRight);
            Fill(new Rect(x, y + 30f, w, 1f), new Color(1f, 1f, 1f, 0.10f));
            y += 48f;
        }

        private static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        private void Blit(float cx, float cy, float rad, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - rad, cy - rad, rad * 2f, rad * 2f), disc);
        }

        private static void Frame(Rect r, float th, Color c)
        {
            Fill(new Rect(r.x, r.y, r.width, th), c);
            Fill(new Rect(r.x, r.yMax - th, r.width, th), c);
            Fill(new Rect(r.x, r.y, th, r.height), c);
            Fill(new Rect(r.xMax - th, r.y, th, r.height), c);
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

        private static Texture2D MakeDisc(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float rad = size * 0.5f;
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    float d = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), new Vector2(rad, rad));
                    tex.SetPixel(i, j, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - d / rad)));
                }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
