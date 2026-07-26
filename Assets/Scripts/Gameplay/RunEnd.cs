using UnityEngine;
using UnityEngine.SceneManagement;
using Core;
using UI;

namespace Gameplay
{
    // FIN DE PARTIE : les deux compteurs du jeu se courent apres, et c'est celui qui touche
    // zero en premier qui decide de l'issue.
    //   POPULARITE (ScoreGauge.Value) tombe a 0 -> DEFAITE ;
    //   COLIS RESTANTS (DeliveryQuest.Remaining) tombe a 0 -> VICTOIRE.
    // Un seul composant pour les deux issues : elles partagent tout l'ecran de resultats
    // (meme mise en page dans la maquette, seules la couleur et la lettre de rang changent).
    // Rendu OnGUI via MenuSkin -> zero Canvas, comme ScoreGauge / RunHud / MenuFlow.
    public class RunEnd : MonoBehaviour
    {
        // Lu par les HUD et par MenuFlow pour se ranger pendant l'ecran de fin.
        public static bool Finished { get; private set; }

        // Le projet tourne avec Enter Play Mode Options / DisableDomainReload : les statiques
        // SURVIVENT au Stop/Play. Quitter l'editeur sur un ecran de victoire laissait donc
        // Finished a true, et la scene de menu -- qui ne contient aucun RunEnd pour le
        // remettre a zero -- s'affichait vide. SubsystemRegistration tourne au tout debut de
        // chaque session de play : c'est le point d'entree prevu pour ca.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Finished = false;
            Time.timeScale = 1f;
        }

        [SerializeField] private TrickSystem tricks;
        // Le joueur martelait peut-etre le saut a l'instant de mourir : sans ce delai il
        // relancerait la partie sans avoir lu une seule ligne de resultats.
        [SerializeField] private float inputDelay = 0.7f;
        // Respiration entre la derniere livraison et l'ecran de victoire : le temps que la
        // chaine se banque et que le "+N" de la jauge se joue.
        [SerializeField] private float winDelay = 1.4f;
        // Seuils de rang, du meilleur au moins bon, sur le cumul de popularite gagnee.
        [SerializeField] private int[] rankThresholds = { 6000, 4000, 2000, 0 };

        private static readonly string[] RankLetters = { "S", "A", "B", "C" };

        private bool won;
        private float t;      // temps ecoule depuis la fin, en NON scale (le jeu est gele)
        private float winT;
        private int index;    // 0 = Retry, 1 = Quit
        private float navAxis;

        private void Awake()
        {
            Finished = false;                 // static : doit repartir propre a chaque rechargement
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
        }

        private void OnDestroy()
        {
            // Sortir de la scene en laissant timeScale a 0 gelerait la suivante.
            if (Finished) Time.timeScale = 1f;
        }

        private void Update()
        {
            if (Finished) { UpdateEndScreen(); return; }

            if (MenuFlow.Blocking) return;    // menu ou pause : la partie n'a pas commence / est suspendue

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
            index = 0;
            Time.timeScale = 0f;
        }

        // Navigation RETRY / QUIT : clavier, manette et souris, comme dans MenuFlow.
        private void UpdateEndScreen()
        {
            t += Time.unscaledDeltaTime;
            if (t < inputDelay) return;

            float axis = InputActions.GetMovementAxis().x;
            if (Mathf.Abs(axis) >= 0.5f && Mathf.Abs(navAxis) < 0.5f)
                index = 1 - index;
            navAxis = axis;

            Vector2 mouse = MenuSkin.Mouse();
            for (int i = 0; i < 2; i++)
                if (ChoiceRect(i).Contains(mouse))
                {
                    index = i;
                    if (MenuSkin.MouseClicked()) { Activate(); return; }
                }

            if (InputActions.GetActionPressed()) Activate();
        }

        private void Activate()
        {
            // Les deux issues passent par un chargement de scene : c'est le seul moyen de
            // remettre colis, jauge et chaine a zero sans un Reset() a maintenir sur chaque
            // systeme. Retry recharge la partie, Quit revient a la scene de menu.
            Finished = false;
            Time.timeScale = 1f;   // AVANT le chargement, sinon la scene chargee reste gelee
            if (index == 0) SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            else SceneManager.LoadScene(MenuFlow.MenuScene);
        }

        // ---- rendu ----

        private const float RowX = 60f, RowW = 760f, RowH = 58f, RowGap = 18f, RowY = 320f;
        private static readonly string[] Choices = { "Retry", "Quit" };

        private static Rect ChoiceRect(int i) => new Rect(RowX + 190f + i * 260f, 760f, 220f, 56f);

        private void OnGUI()
        {
            if (!Finished) return;

            Matrix4x4 m0 = MenuSkin.Begin(out float sw, out float sh);

            // fondu qui s'installe (non scale : le jeu est gele)
            float fade = Mathf.Clamp01(t * 2.2f);
            float pop = EaseOutBack(Mathf.Clamp01(t * 1.6f));

            Color wedgeCol = won ? new Color(0.88f, 0.07f, 0.62f) : new Color(0.58f, 0.10f, 0.78f);
            Color rankCol = won ? MenuSkin.Gold : MenuSkin.Cream;

            // Nuit cyberpunk + silhouette de ville, comme la maquette de defaite.
            MenuSkin.Fill(new Rect(0f, 0f, sw, sh), new Color(MenuSkin.Night.r, MenuSkin.Night.g, MenuSkin.Night.b, fade));
            MenuSkin.Skyline(new Rect(0f, sh - 420f, sw, 420f), new Color(0f, 0f, 0f, 0.28f * fade));

            // Panneau en biseau + lettre de rang.
            var wedge = new Rect(sw - 640f, 0f, 640f, sh);
            MenuSkin.Wedge(wedge, new Color(wedgeCol.r, wedgeCol.g, wedgeCol.b, fade));
            var rankCenter = new Vector2(wedge.x + 330f, sh * 0.5f);
            MenuSkin.Burst(rankCenter, 300f * pop, new Color(1f, 1f, 1f, 0.28f * fade));
            MenuSkin.Glow(rankCenter, 200f * pop, new Color(rankCol.r, rankCol.g, rankCol.b, 0.22f * fade));
            MenuSkin.TextOutlined(new Rect(rankCenter.x - 200f, rankCenter.y - 210f, 400f, 420f),
                                  Rank(), Mathf.RoundToInt(300f * pop), rankCol, MenuSkin.Ink, 6f,
                                  TextAnchor.MiddleCenter);

            // Titre : eclat d'etoile derriere, lettrage cerne devant.
            var titleBox = new Rect(RowX, 120f, RowW, 120f);
            MenuSkin.Burst(new Vector2(titleBox.x + 230f, titleBox.y + 58f), 260f * pop,
                           new Color(wedgeCol.r, wedgeCol.g, wedgeCol.b, 0.75f * fade));
            MenuSkin.TextOutlined(titleBox, won ? "VICTORY" : "DEFEAT", 82,
                                  won ? MenuSkin.Cream : new Color(1f, 0.86f, 0.6f),
                                  MenuSkin.Ink, 5f, TextAnchor.UpperLeft);
            MenuSkin.Text(new Rect(RowX + 4f, titleBox.y + 96f, RowW, 34f),
                          won ? "TOUS LES COLIS SONT LIVRES" : "PLUS PERSONNE NE TE REGARDE", 22,
                          new Color(1f, 1f, 1f, 0.6f * fade), TextAnchor.UpperLeft);

            // Les 4 compteurs de la maquette, qui tombent un a un.
            Row(0, "Score", (ScoreGauge.Instance != null ? ScoreGauge.Instance.Earned : 0).ToString("N0"), fade);
            Row(1, "Deliveries", DeliveryQuest.DeliveredCount + " / " + DeliveryQuest.Total, fade);
            Row(2, "Best Combo", tricks != null ? "x" + tricks.BestCombo : "--", fade);
            Row(3, "Best Grind", tricks != null ? Mathf.RoundToInt(tricks.BestGrind) + " m" : "--", fade);

            // RETRY / QUIT
            if (t >= inputDelay)
                for (int i = 0; i < Choices.Length; i++)
                {
                    var r = ChoiceRect(i);
                    bool sel = i == index;
                    if (sel)
                    {
                        MenuSkin.Band(new Rect(r.x - 20f, r.y, r.width + 40f, r.height),
                                      new Color(MenuSkin.Cyan.r, MenuSkin.Cyan.g, MenuSkin.Cyan.b, 0.85f));
                        MenuSkin.Text(new Rect(r.x - 46f, r.y, 40f, r.height), ">", 30,
                                      Color.white, TextAnchor.MiddleCenter);
                    }
                    MenuSkin.TextOutlined(r, Choices[i].ToUpperInvariant(), 30,
                                          sel ? Color.white : MenuSkin.Cream, MenuSkin.Ink, 3f,
                                          TextAnchor.MiddleCenter);
                }

            MenuSkin.End(m0);
        }

        // Une ligne de resultat : elle arrive en glissant, decalee de la precedente.
        private void Row(int i, string label, string value, float fade)
        {
            float in01 = Mathf.Clamp01((t - 0.15f - i * 0.09f) * 4f);
            if (in01 <= 0f) return;
            var r = new Rect(RowX - 60f * (1f - in01), RowY + i * (RowH + RowGap), RowW, RowH);
            MenuSkin.StatRow(r, label, value,
                             new Color(0.32f, 0.45f, 0.95f, 0.95f * in01 * fade));
        }

        // Rang lu sur le cumul de popularite gagnee (Earned ne redescend jamais : un rang
        // ne peut pas etre vole par la fonte de fin de partie).
        private string Rank()
        {
            int earned = ScoreGauge.Instance != null ? ScoreGauge.Instance.Earned : 0;
            for (int i = 0; i < rankThresholds.Length && i < RankLetters.Length; i++)
                if (earned >= rankThresholds[i]) return RankLetters[i];
            return RankLetters[RankLetters.Length - 1];
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 2.2f, c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }
    }
}
