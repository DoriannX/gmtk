using Core;
using Gameplay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UI
{
    // LES ECRANS DE MENU (placeholder d'apres la maquette Figma "Crazy Deliveries").
    // UN SEUL composant pour deux emplois, regle par `menuScene` :
    //   - pose dans la scene de MENU (menuScene = true) : affiche le menu titre, PLAY charge
    //     la scene de jeu ;
    //   - pose dans la scene de JEU (menuScene = false) : reste muet, et Echap / Start ouvre
    //     la PAUSE (Resume / Options / Menu).
    // Deux ecrans, un seul jeu de dessins et une seule navigation a maintenir. Pas de Canvas,
    // pas de prefab a cabler -- meme parti-pris OnGUI que le HUD.
    //
    // Boucle complete : Menu.unity -> (Play) -> scene de jeu -> RunEnd (victoire / defaite)
    //                             -> Retry (recharge la scene de jeu) ou Quit (retour Menu.unity).
    //
    // L'ecran de fin appartient a RunEnd : tant que RunEnd.Finished, ce composant se tait.
    public class MenuFlow : MonoBehaviour
    {
        private enum Panel { Menu, Options, Playing, Paused }

        // Etat courant, lu par les HUD pour se ranger pendant les menus. Defaut = Playing :
        // une scene SANS MenuFlow (proto, scene de test) doit jouer normalement, pas rester
        // bloquee sur un menu que personne ne dessine.
        private static Panel current = Panel.Playing;
        public static bool Blocking => current != Panel.Playing;

        // DisableDomainReload : `current` survit au Stop/Play. Sans remise a zero, relancer
        // depuis une scene jouable pouvait demarrer en pause, ou l'inverse. Cf RunEnd.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => current = Panel.Playing;

        [Header("Role de ce MenuFlow")]
        [Tooltip("Coche dans Menu.unity, decoche dans les scenes jouables (il n'y sert que de pause).")]
        [SerializeField] private bool menuScene;
        [SerializeField] private string gameScene = "Game";
        // Partage avec RunEnd : le nom de la scene de menu est la seule chose que la scene de
        // jeu a besoin de connaitre pour revenir en arriere.
        public const string MenuScene = "Menu";

        [SerializeField] private string title = "Crazy\nDeliveries";

        private Panel returnTo = Panel.Menu;   // d'ou on a ouvert les options
        private int index;
        private float t;
        private float navAxis;                 // etat precedent de l'axe, pour ne bouger qu'au front

        private static readonly string[] MenuItems = { "Play", "Options", "Quit" };
        private static readonly string[] PauseItems = { "Resume", "Options", "Menu" };
        private static readonly string[] OptionItems = { "Revoir le tuto", "Oublier le tuto", "Return" };

        private void Awake()
        {
            current = menuScene ? Panel.Menu : Panel.Playing;
            index = 0;
            // LoadScene ne remet PAS timeScale a 1 : sans ca, arriver du menu (gele) ou d'un
            // ecran de fin laisserait la scene suivante figee.
            Time.timeScale = current == Panel.Playing ? 1f : 0f;
            FreeCursor();
            Sync();
        }

        // UN ECRAN DE MENU POSSEDE SON CURSEUR. La camera de jeu le verrouille pour le look
        // souris et le rend quand une UI prend la main, mais elle ne peut rien pour la scene
        // de MENU, ou elle n'existe pas : en arrivant par QUIT depuis l'ecran de fin, la
        // camera re-verrouille sur la derniere frame de la partie (Finished repasse a false
        // avant le LoadScene) et le menu heritait d'une souris invisible et bloquee au centre.
        // On ne verrouille jamais ici : rendre la main au jeu est le travail de la camera.
        private void FreeCursor()
        {
            if (current == Panel.Playing) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void OnDestroy()
        {
            // Ne jamais laisser la scene suivante gelee derriere soi.
            if (current != Panel.Playing) Time.timeScale = 1f;
        }

        private string[] Items => current switch
        {
            Panel.Options => OptionItems,
            Panel.Paused => PauseItems,
            _ => MenuItems,
        };

        private void Update()
        {
            if (RunEnd.Finished) return;   // l'ecran de fin a la main sur tout

            t += Time.unscaledDeltaTime;

            if (current == Panel.Playing)
            {
                if (InputActions.GetPausePressed()) Open(Panel.Paused);
                return;
            }

            // Echap referme la pause / revient des options.
            if (InputActions.GetPausePressed())
            {
                if (current == Panel.Paused) Resume();
                else if (current == Panel.Options) Open(returnTo);
                return;
            }

            var items = Items;

            // Clavier / manette : on ne bouge qu'au FRANCHISSEMENT du seuil, sinon un stick
            // maintenu ferait defiler la liste a la frequence d'affichage.
            float axis = InputActions.GetMovementAxis().y;
            if (Mathf.Abs(axis) >= 0.5f && Mathf.Abs(navAxis) < 0.5f)
                index = (index + (axis > 0f ? items.Length - 1 : 1)) % items.Length;
            navAxis = axis;

            // Souris : le survol prend la selection, le clic valide.
            Vector2 mouse = MenuSkin.Mouse();
            for (int i = 0; i < items.Length; i++)
                if (ButtonRect(i).Contains(mouse))
                {
                    index = i;
                    if (MenuSkin.MouseClicked()) { Activate(i); return; }
                }

            if (InputActions.GetActionPressed()) Activate(index);
        }

        private void Activate(int i)
        {
            switch (current)
            {
                case Panel.Menu:
                    if (i == 0) Play();
                    else if (i == 1) Open(Panel.Options);
                    else Quit();
                    break;
                case Panel.Paused:
                    if (i == 0) Resume();
                    else if (i == 1) Open(Panel.Options);
                    else ToMenu();
                    break;
                case Panel.Options:
                    if (i == 0) ReplayTutorial();
                    else if (i == 1) TutorialFlow.ForgetSave();
                    else Open(returnTo);
                    break;
            }
        }

        // "Revoir le tuto" : en ACCELERE (memes etapes, memes conditions, sans les temps
        // morts). Depuis la scene de jeu le tuto est deja en scene -> on le relance et on rend
        // la main tout de suite, sinon on regarderait le menu par-dessus. Depuis Menu.unity il
        // n'y a rien a relancer : le drapeau statique arme la partie suivante.
        private void ReplayTutorial()
        {
            bool live = TutorialFlow.Instance != null;
            TutorialFlow.RequestReplay(true);
            if (live) Resume();
        }

        private void Open(Panel p)
        {
            if (p == Panel.Options) returnTo = current;
            current = p;
            index = 0;
            Time.timeScale = p == Panel.Playing ? 1f : 0f;
            FreeCursor();
            Sync();
        }

        // Lancer la partie = charger la scene de jeu. Le chargement remet tout a zero (colis,
        // jauge, chaine) sans un Reset() a maintenir sur chaque systeme.
        private void Play() => Load(gameScene, Panel.Playing);
        private void ToMenu() => Load(MenuScene, Panel.Menu);

        private void Resume() => Open(Panel.Playing);

        private void Load(string scene, Panel next)
        {
            current = next;
            Time.timeScale = 1f;   // AVANT le chargement, sinon la scene chargee reste gelee
            SceneManager.LoadScene(scene);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // Tient GameManager au courant s'il est en scene (il porte l'etat partage, mais rien
        // ici n'en depend : le menu marche dans une scene qui n'en a pas).
        private void Sync()
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            switch (current)
            {
                case Panel.Playing: if (gm.CurrentState == GameState.Paused) gm.ResumeGame(); else gm.StartGame(); break;
                case Panel.Paused: gm.PauseGame(); break;
                default: gm.ReturnToMenu(); break;
            }
        }

        // ---- rendu ----

        // Layout partage par le dessin ET la souris : une seule source pour les rectangles.
        private const float ButtonX = 40f, ButtonW = 660f, ButtonH = 62f, ButtonGap = 22f;
        private float FirstButtonY => current == Panel.Menu ? 600f : 470f;

        private Rect ButtonRect(int i) =>
            new Rect(ButtonX, FirstButtonY + i * (ButtonH + ButtonGap), ButtonW, ButtonH);

        private void OnGUI()
        {
            if (current == Panel.Playing || RunEnd.Finished) return;

            Matrix4x4 m0 = MenuSkin.Begin(out float sw, out float sh);

            if (current == Panel.Paused)
            {
                // La partie reste visible dessous, juste assourdie, + le biseau cyan de la maquette.
                // gris fonce en LINEAIRE : GUI.color est corrige en gamma, un 0.35 ressort blanc
                MenuSkin.Fill(new Rect(0f, 0f, sw, sh), new Color(0.09f, 0.09f, 0.11f, 0.78f));
                MenuSkin.Wedge(new Rect(sw - 420f, 0f, 420f, sh), MenuSkin.Cyan);
                MenuSkin.TextOutlined(new Rect(0f, 120f, sw - 80f, 90f), "PAUSE", 76,
                                      Color.white, MenuSkin.Ink, 4f, TextAnchor.UpperRight);
            }
            else
            {
                MenuSkin.Fill(new Rect(0f, 0f, sw, sh), Color.white);
                if (current == Panel.Menu)
                {
                    // Titre bulle : ombre rose decalee, puis le lettrage blanc cerne de noir.
                    var box = new Rect(0f, 110f, sw, 260f);
                    MenuSkin.TextOutlined(new Rect(box.x + 10f, box.y + 12f, box.width, box.height),
                                          title, 96, MenuSkin.Pink, MenuSkin.Pink, 3f, TextAnchor.UpperCenter);
                    MenuSkin.TextOutlined(box, title, 96, Color.white, MenuSkin.Ink, 5f, TextAnchor.UpperCenter);
                }
                else
                {
                    MenuSkin.TextOutlined(new Rect(0f, 150f, sw, 100f), "OPTIONS", 72,
                                          Color.white, MenuSkin.Ink, 4f, TextAnchor.UpperCenter);
                }
            }

            var items = Items;
            for (int i = 0; i < items.Length; i++)
                MenuSkin.MenuButton(ButtonRect(i), items[i], i == index, t);

            MenuSkin.Text(new Rect(ButtonX, sh - 56f, sw - ButtonX * 2f, 30f),
                          "FLECHES / STICK  -  DEPLACER      ESPACE / A  -  VALIDER", 18,
                          new Color(0f, 0f, 0f, current == Panel.Paused ? 0.65f : 0.35f),
                          TextAnchor.UpperLeft);

            MenuSkin.End(m0);
        }
    }
}
