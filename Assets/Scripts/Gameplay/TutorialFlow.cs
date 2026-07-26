using System;
using Core;
using UnityEngine;

namespace Gameplay
{
    // LE TUTO. Il ne coupe JAMAIS le jeu : pas de timeScale, pas de plein ecran, pas de
    // "clique pour continuer". Le joueur roule deja pour de vrai -- le tuto EST le debut de
    // la partie. La seule chose qu'on suspend, c'est la fonte de la popularite (via
    // ScoreGauge.DrainMultiplier), sinon apprendre couterait la partie.
    //
    // POURQUOI PAS une scene de tuto separee : il faudrait la construire, la maintenir, la
    // mettre au build et y dupliquer joueur / colis / HUD. Ici zero asset, zero prefab, zero
    // champ a remplir : la table d'etapes est en dur plus bas, chaque etape sait se valider
    // toute seule en relisant InputActions / TrickSystem / DeliveryQuest.
    //
    // POURQUOI PAS un Canvas : tout le HUD du projet est en OnGUI (ScoreGauge, RunHud,
    // TrickHud, MenuFlow, RunEnd) et partage les unites de design de MenuSkin (ecran = 1080
    // de haut). Rester en OnGUI, c'est pointer le vrai HUD avec les memes coordonnees.
    //
    // Sauvegarde : une seule cle PlayerPrefs. Deja vu -> le composant se desactive dans Awake
    // et ne coute plus rien. Options -> "Revoir le tuto" le relance en accelere (cf MenuFlow).
    public class TutorialFlow : MonoBehaviour
    {
        private const string SeenKey = "tuto.seen.v1";

        // Seuil du SUPER saut, aligne sur ArcadeCarController (jumpChargeMax 1s * 0.9). Valider
        // l'etape avant que le vehicule ne parte vraiment en super saut apprendrait le mauvais
        // geste : on veut que la reussite tombe pile quand le gros saut part.
        private const float SuperJumpHold = 0.9f;

        // Degres de rotation en l'air qui valent une figure, aligne sur TrickSystem (320, volontairement
        // moins que 360). On lit la rotation VECUE et pas TrickSystem.Total : Total ne monte qu'au
        // Bank(), c'est a dire ~2.6s apres, une fois la fenetre de chaine ecoulee -- une figure faite a
        // l'etape d'avant tombait donc en plein dans l'etape FIGURES et la validait toute seule.
        private const float TrickTarget = 320f;

        // Charge de burnout (0..1) demandee. burnoutMinCharge vaut 0.35 / 1.5 = 0.23 : on exige un peu
        // plus pour que le joueur sente vraiment l'arrachage.
        private const float BurnoutTarget = 0.5f;

        // Metres de rail a parcourir. TrickSystem ne compte un grind qu'au-dela de 2 m : on en
        // demande le triple pour que le joueur ait le temps de sentir l'equilibre.
        private const float GrindTarget = 6f;

        public static TutorialFlow Instance { get; private set; }

        // Statics : ils survivent au LoadScene de MenuFlow.Play(), donc "Revoir le tuto"
        // depuis les options de Menu.unity arme la partie suivante sans DontDestroyOnLoad.
        public static bool ReplayRequested;
        public static bool FastMode;

        // DisableDomainReload : les statics survivent au Stop/Play. Meme raison que MenuFlow.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            ReplayRequested = false;
            FastMode = false;
        }

        [Header("Rythme (secondes, temps non mis a l'echelle)")]
        [SerializeField] private float introTime = 0.6f;
        [SerializeField] private float celebrateTime = 0.9f;
        [SerializeField] private float nudgeAfter = 8f;
        [SerializeField] private float outroTime = 1.4f;

        [Header("Divers")]
        [Tooltip("Maintenir cette touche saute le tuto et le marque comme vu.")]
        [SerializeField] private float skipHold = 0.6f;
        [SerializeField] private bool alwaysShow;   // pour tester : ignore la sauvegarde

        // ---- etat ----

        private enum Phase { Intro, Waiting, Success, Outro, Done }

        private Phase phase;
        private int step;
        private float phaseT;      // temps dans la phase courante
        private float stepT;       // temps dans l'etape courante (intro comprise)
        private float t;           // horloge globale, pour les pulsations
        private float skipT;
        private bool fast;

        // accumulateurs d'etape, remis a zero a chaque entree d'etape
        private float driftT, chargeT, turnL, turnR, boostFlash, trickBest, burnoutBest, grindDist;
        private bool charged;
        private int deliverBase;

        private ArcadeCarController car;
        private Rigidbody carBody;

        private Step[] steps;

        private static readonly string[] Stamps = { "OK !", "BIEN !", "GO !", "NICKEL !", "CLASSE !", "OUI !" };

        // ---- table des etapes ----

        private enum Target { None, Gauge, Packages }

        private class Step
        {
            public string Title;
            public string Hint;
            public string Hint2;   // 2e ligne permanente, pour les etapes d'explication
            public string Nudge;
            public Cap[] Caps;
            public Func<bool> Done;        // null = etape a duree (callout)
            public Func<float> Progress;   // null = pas de barre
            public Target Point = Target.None;
        }

        private struct Cap
        {
            public string Label;
            public Func<bool> Down;
            public float Width;
        }

        private static Cap K(string label, Func<bool> down, float width = 74f)
            => new Cap { Label = label, Down = down, Width = width };

        // ArcadeCarController.Speed est une MAGNITUDE : reculer la fait monter autant
        // qu'avancer, et l'etape "DEMARRE" se validait en marche arriere. On projette donc la
        // vitesse sur le nez du vehicule -- negatif en marche arriere.
        private float ForwardSpeed()
        {
            if (carBody == null || car == null) return 0f;
            Vector3 v = carBody.linearVelocity;
            v.y = 0f;
            return Vector3.Dot(v, car.transform.forward);
        }

        private void BuildSteps()
        {
            Cap capW = K("W", () => InputActions.GetMovementAxis().y > 0.5f);
            Cap capA = K("A", () => InputActions.GetMovementAxis().x < -0.5f);
            Cap capD = K("D", () => InputActions.GetMovementAxis().x > 0.5f);
            Cap capE = K("E", () => boostFlash > 0f);
            Cap capShift = K("SHIFT", InputActions.GetDriftHeld, 132f);
            Cap capSpace = K("ESPACE", InputActions.GetJumpHeld, 148f);
            Cap capRmb = K("CLIC DROIT", InputActions.GetTrickHeld, 190f);
            Cap capEnter = K("ENTREE", ContinueHeld, 158f);

            steps = new[]
            {
                new Step {
                    Title = "DEMARRE",
                    Hint = "MAINTIENS POUR ACCELERER",
                    Nudge = "S OU BAS POUR RECULER",
                    Caps = new[] { capW },
                    Done = () => ForwardSpeed() >= 6f,
                    Progress = () => ForwardSpeed() / 6f,
                },
                new Step {
                    Title = "TOURNE",
                    Hint = "EN ROULANT : A GAUCHE... PUIS A DROITE",
                    Nudge = "A L'ARRET LES ROUES PENCHENT, MAIS TU NE TOURNES PAS",
                    Caps = new[] { capA, capD },
                    Done = () => turnL >= 0.25f && turnR >= 0.25f,
                    Progress = () => (Mathf.Min(turnL, 0.25f) + Mathf.Min(turnR, 0.25f)) / 0.5f,
                },
                new Step {
                    Title = "BOOST",
                    Hint = "UN COUP DE JUS. 2.5s DE RECHARGE.",
                    Nudge = "E OU CTRL GAUCHE  /  B A LA MANETTE",
                    Caps = new[] { capE },
                    Done = () => boostFlash > 0f,
                },
                new Step {
                    Title = "DRIFTE",
                    Hint = "MAINTIENS ET TOURNE, LANCE",
                    Nudge = "RELACHE EN PLEIN GAZ : MINI-TURBO",
                    Caps = new[] { capShift, capA, capD },
                    Done = () => driftT >= 0.8f,
                    Progress = () => driftT / 0.8f,
                },
                new Step {
                    Title = "BURNOUT",
                    Hint = "A L'ARRET : PLEIN GAZ + SHIFT, PUIS LACHE",
                    Nudge = "PLUS LA CHARGE EST LONGUE, PLUS LE DEPART ARRACHE",
                    Caps = new[] { capW, capShift },
                    Done = () => burnoutBest >= BurnoutTarget,
                    Progress = () => burnoutBest / BurnoutTarget,
                },
                new Step {
                    Title = "SAUT CHARGE",
                    Hint = "MAINTIENS POUR SAUTER PLUS HAUT",
                    Nudge = "TAPE COURT = PETIT SAUT",
                    Caps = new[] { capSpace },
                    Done = () => charged,
                    Progress = () => chargeT / SuperJumpHold,
                },
                new Step {
                    Title = "FIGURES",
                    Hint = "EN L'AIR : MAINTIENS + TOURNE",
                    Nudge = "RETOMBE SUR LES ROUES, SINON C'EST PERDU",
                    Caps = new[] { capRmb, capA, capD },
                    Done = () => trickBest >= TrickTarget,
                    Progress = () => trickBest / TrickTarget,
                },
                new Step {
                    Title = "GRIND",
                    Hint = "SAUTE SUR UNE BARRE OU UN REBORD DE PONT",
                    Hint2 = "TIENS L'EQUILIBRE : BRAQUE POUR RAMENER L'AIGUILLE AU CENTRE",
                    Nudge = "IL FAUT DE LA VITESSE ET ARRIVER DANS L'AXE POUR ACCROCHER",
                    Caps = new[] { capA, capD, capSpace },
                    // Distance et pas duree : c'est la distance qui rapporte (8 pts/m dans
                    // TrickSystem), et rester a l'arret en equilibre ne prouve rien.
                    Done = () => grindDist >= GrindTarget,
                    Progress = () => grindDist / GrindTarget,
                },
                // Les deux etapes d'explication n'ont RIEN a reussir : elles attendent que le
                // joueur ait lu et appuie. Aucun minuteur -- un texte qui s'en va tout seul,
                // c'est un texte que la moitie des joueurs n'aura pas fini de lire.
                new Step {
                    Title = "TA POPULARITE",
                    Hint = "ELLE FOND TOUT SEULE. A ZERO, C'EST FINI.",
                    // killPenalty vaut 250 dans Game.unity : autant l'annoncer, c'est la punition
                    // la plus brutale du jeu et rien a l'ecran ne la fait deviner.
                    Hint2 = "+ FIGURES ET LIVRAISONS      - PIETON ECRASE : -250",
                    Nudge = "LA JAUGE, C'EST TES SECONDES DE SURVIE",
                    Caps = new[] { capEnter },
                    Done = ContinuePressed,
                    Point = Target.Gauge,
                },
                new Step {
                    Title = "TES COLIS",
                    Hint = "LIVRE-LES TOUS POUR GAGNER LA PARTIE.",
                    Nudge = "SUR LE DERNIER, LA POPULARITE FOND PLUS VITE",
                    Caps = new[] { capEnter },
                    Done = ContinuePressed,
                    Point = Target.Packages,
                },
                new Step {
                    Title = "LIVRE UN COLIS",
                    // La zone est BLEUE et verdit en se remplissant (QuestZone.cs:217). Au moment
                    // ou l'etape s'affiche le joueur n'est PAS dedans : la consigne est de la
                    // trouver, pas d'y rester.
                    Hint = "TROUVE UNE ZONE BLEUE ET RESTE DEDANS 3s",
                    Nudge = "PAQUET EN MAIN : SUIS LA FLECHE",
                    Done = () => DeliveryQuest.DeliveredCount > deliverBase,
                },
            };
        }

        // ---- cycle de vie ----

        private void Awake()
        {
            Instance = this;
            Bind();

            fast = ReplayRequested && FastMode;
            bool seen = !alwaysShow && PlayerPrefs.GetInt(SeenKey, 0) == 1;
            if (seen && !ReplayRequested) { enabled = false; return; }

            ReplayRequested = false;
            EnterStep(0);
        }

        // Table d'etapes + references au joueur. Rappele au besoin depuis Update/OnGUI : une
        // recompilation PENDANT le play mode recree le composant sans repasser par Awake, et
        // steps (des delegues, non serialisables) revient a null -> NullReference a chaque frame.
        private void Bind()
        {
            BuildSteps();
            foreach (var c in FindObjectsByType<ArcadeCarController>(FindObjectsSortMode.None))
            {
                if (car == null) car = c;
                if (c.IsPlayer) { car = c; break; }
            }
            carBody = car != null ? car.GetComponent<Rigidbody>() : null;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            // Ne jamais laisser la fonte coupee derriere soi.
            if (phase != Phase.Done && ScoreGauge.Instance != null) ScoreGauge.Instance.DrainMultiplier = 1f;
        }

        // Relance depuis les options (ou le menu contextuel). En accelere : memes conditions
        // de reussite, juste sans les temps morts.
        public static void RequestReplay(bool fastMode)
        {
            ReplayRequested = true;
            FastMode = fastMode;
            if (Instance != null) Instance.Replay();
        }

        public static void ForgetSave()
        {
            PlayerPrefs.DeleteKey(SeenKey);
            PlayerPrefs.Save();
            Debug.Log("[Tuto] Sauvegarde effacee : le tuto se rejouera au prochain lancement.");
        }

        [ContextMenu("Rejouer le tuto")]
        public void Replay()
        {
            fast = FastMode;
            ReplayRequested = false;
            enabled = true;
            skipT = 0f;
            EnterStep(0);
        }

        private void EnterStep(int i)
        {
            step = i;
            phase = Phase.Intro;
            phaseT = 0f;
            stepT = 0f;
            driftT = chargeT = turnL = turnR = trickBest = burnoutBest = grindDist = 0f;
            charged = false;
            deliverBase = DeliveryQuest.DeliveredCount;
        }

        private void Done()
        {
            phase = Phase.Done;
            PlayerPrefs.SetInt(SeenKey, 1);
            PlayerPrefs.Save();
            if (ScoreGauge.Instance != null) ScoreGauge.Instance.DrainMultiplier = 1f;
            enabled = false;
            Debug.Log("[Tuto] Termine. La fonte de popularite reprend.");
        }

        private float IntroTime => fast ? 0.15f : introTime;
        private float CelebrateTime => fast ? 0.35f : celebrateTime;
        private float OutroTime => fast ? 0.7f : outroTime;

        // "Continuer" sur les etapes d'explication. ENTREE parce que c'est la seule touche que
        // le jeu n'utilise pas : ESPACE ferait sauter le camion en meme temps qu'il valide.
        // A la manette, Y n'allume que les phares -- sans consequence.
        private static bool ContinuePressed()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) return true;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            return gp != null && gp.buttonNorth.wasPressedThisFrame;
        }

        private static bool ContinueHeld()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && (kb.enterKey.isPressed || kb.numpadEnterKey.isPressed)) return true;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            return gp != null && gp.buttonNorth.isPressed;
        }

        private void Update()
        {
            if (phase == Phase.Done) return;
            if (steps == null) Bind();

            float dt = Time.unscaledDeltaTime;
            t += dt;

            // Pendant les menus / l'ecran de fin, le tuto se met en veille comme les autres HUD.
            if (UI.MenuFlow.Blocking || RunEnd.Finished) return;

            phaseT += dt;
            stepT += dt;

            boostFlash = Mathf.Max(0f, boostFlash - dt);
            if (InputActions.GetBoostPressed()) boostFlash = 0.35f;

            // Passer le tuto : maintien, pas une tape (on ne saute pas un tuto par accident).
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool skipHeld = kb != null && kb.tKey.isPressed;
            skipT = skipHeld ? skipT + dt : 0f;
            if (skipT >= skipHold) { Debug.Log("[Tuto] Passe par le joueur."); Done(); return; }

            switch (phase)
            {
                case Phase.Intro:
                    if (phaseT >= IntroTime) { phase = Phase.Waiting; phaseT = 0f; }
                    break;

                case Phase.Waiting:
                    Accumulate(dt);
                    if (steps[step].Done()) Succeed();
                    break;

                case Phase.Success:
                    if (phaseT >= CelebrateTime)
                    {
                        if (step + 1 < steps.Length) EnterStep(step + 1);
                        else { phase = Phase.Outro; phaseT = 0f; }
                    }
                    break;

                case Phase.Outro:
                    if (phaseT >= OutroTime) Done();
                    break;
            }
        }

        // La fonte est coupee ICI et pas dans Update : RunHud reecrit DrainMultiplier dans son
        // propre Update (escalade du dernier colis), LateUpdate passe apres, donc pas de course.
        private void LateUpdate()
        {
            if (phase == Phase.Done) return;
            if (ScoreGauge.Instance != null) ScoreGauge.Instance.DrainMultiplier = 0f;
        }

        private void Accumulate(float dt)
        {
            var axis = InputActions.GetMovementAxis();

            // Braquer a l'arret ne fait que pencher les roues : on ne compte le virage que si le
            // camion AVANCE et que son cap change vraiment (angularVelocity.y, + = vers la
            // droite). Sinon l'etape se validait sans que le joueur ait vu quoi que ce soit.
            float yaw = carBody != null ? carBody.angularVelocity.y : 0f;
            if (ForwardSpeed() > 3f && Mathf.Abs(yaw) > 0.35f)
            {
                if (axis.x < -0.5f && yaw < 0f) turnL += dt;
                if (axis.x > 0.5f && yaw > 0f) turnR += dt;
            }

            if (car != null && car.Grounded && car.Speed > 8f && InputActions.GetDriftHeld()) driftT += dt;

            if (InputActions.GetJumpHeld()) chargeT += dt;
            else
            {
                if (chargeT >= SuperJumpHold) charged = true;
                chargeT = 0f;
            }

            if (car == null) return;

            // Figure : on suit la rotation accumulee DEPUIS LE DECOLLAGE (le controleur la remet a
            // zero a chaque saut), et on garde le meilleur essai de l'etape.
            if (!car.Grounded)
            {
                float best = Mathf.Max(Mathf.Abs(car.AirSpinDeg),
                             Mathf.Max(Mathf.Abs(car.AirFlipDeg), Mathf.Abs(car.AirRollDeg)));
                if (best > trickBest) trickBest = best;
            }

            if (car.BurnoutCharge01 > burnoutBest) burnoutBest = car.BurnoutCharge01;

            if (car.OnRail) grindDist += car.GrindSpeedAbs * dt;
        }

        private void Succeed()
        {
            phase = Phase.Success;
            phaseT = 0f;
            // Court expres : au-dela ca casse le rythme de conduite (cf le garde-fou de Hitstop).
            Hitstop.Punch(0.35f, 0.06f, 0.18f);
        }

        // ---- rendu ----

        // Meme habillage que les ecrans de menu : bande cyan penchee, libelle rose cerne de
        // noir. Pas de panneau sombre -- la bande de MenuSkin s'estompe vers la droite, donc
        // elle pose le texte sans masquer la route derriere.
        private const float CardW = 900f;
        private const float BarH = 84f;
        // Depuis le BAS de l'ecran. Assez haut pour que bande + consigne + 2e ligne + coup de
        // pouce + touches tiennent sans mordre le bord.
        private const float CardTop = 330f;

        private void OnGUI()
        {
            if (phase == Phase.Done) return;
            if (UI.MenuFlow.Blocking || RunEnd.Finished) return;   // menus / ecran de fin
            if (steps == null) Bind();

            Matrix4x4 m0 = UI.MenuSkin.Begin(out float sw, out float sh);

            if (phase == Phase.Outro) DrawOutro(sw, sh);
            else
            {
                DrawCallout(sw, sh);
                DrawCard(sw, sh);
            }

            UI.MenuSkin.End(m0);
        }

        private void DrawCard(float sw, float sh)
        {
            var s = steps[step];

            // Entree : glisse du bas + leger depassement. Anime en temps non mis a l'echelle,
            // le tuto doit rester vivant meme si le jeu ralentit (hitstop).
            float appear = phase == Phase.Intro ? Mathf.Clamp01(phaseT / Mathf.Max(0.001f, IntroTime)) : 1f;
            float e = EaseOutBack(appear);
            float slide = (1f - e) * 280f;

            bool win = phase == Phase.Success;
            float flash = win ? Mathf.Clamp01(1f - phaseT / 0.35f) : 0f;

            float x0 = (sw - CardW) * 0.5f;
            var bar = new Rect(x0, sh - CardTop + slide, CardW, BarH);

            // La bande de menu, avec un coup de blanc au moment de la reussite (meme geste que
            // MenuButton sur l'entree selectionnee).
            UI.MenuSkin.Band(bar, UI.MenuSkin.Cyan);
            if (flash > 0f) UI.MenuSkin.Band(bar, new Color(1f, 1f, 1f, 0.75f * flash));

            // Numero d'etape sur un biseau rose, comme le panneau de rang de l'ecran de fin.
            var badge = new Rect(bar.x - 22f, bar.y + 6f, 78f, BarH - 12f);
            UI.MenuSkin.Wedge(badge, UI.MenuSkin.Pink);
            UI.MenuSkin.TextOutlined(badge, (step + 1).ToString(), 36, Color.white, UI.MenuSkin.Ink, 3f,
                                     TextAnchor.MiddleCenter);

            UI.MenuSkin.Text(new Rect(bar.x + 66f, bar.y, 40f, bar.height), ">", 34, UI.MenuSkin.Pink,
                             TextAnchor.MiddleCenter);
            UI.MenuSkin.TextOutlined(new Rect(bar.x + 110f, bar.y, bar.width - 130f, bar.height),
                                     s.Title, 44, win ? UI.MenuSkin.Gold : UI.MenuSkin.Pink,
                                     UI.MenuSkin.Ink, 3.5f, TextAnchor.MiddleLeft);

            // Barre de progression : collee sous la bande, dans son biais.
            if (s.Progress != null)
            {
                float p = win ? 1f : Mathf.Clamp01(s.Progress());
                var pb = new Rect(bar.x + 4f, bar.yMax + 2f, bar.width - 120f, 7f);
                UI.MenuSkin.Fill(pb, new Color(0f, 0f, 0f, 0.35f));
                UI.MenuSkin.Fill(new Rect(pb.x, pb.y, pb.width * p, pb.height),
                                 Color.Lerp(UI.MenuSkin.Cyan, UI.MenuSkin.Gold, p));
            }

            // Consigne : cernee de noir, sans panneau -- lisible sur la route comme les titres
            // bulle du menu.
            UI.MenuSkin.TextOutlined(new Rect(bar.x + 110f, bar.yMax + 12f, bar.width - 130f, 34f),
                                     s.Hint, 26, UI.MenuSkin.Cream, UI.MenuSkin.Ink, 2.5f, TextAnchor.MiddleLeft);

            float capsY = bar.yMax + 54f;
            if (!string.IsNullOrEmpty(s.Hint2))
            {
                UI.MenuSkin.TextOutlined(new Rect(bar.x + 110f, bar.yMax + 46f, bar.width - 130f, 30f),
                                         s.Hint2, 22, UI.MenuSkin.Gold, UI.MenuSkin.Ink, 2.5f, TextAnchor.MiddleLeft);
                capsY += 36f;
            }

            // Coup de pouce : seulement si l'etape traine. Une aide qui arrive tout de suite
            // n'est plus une aide, c'est du bruit.
            if (!win && !string.IsNullOrEmpty(s.Nudge) && stepT > nudgeAfter)
            {
                float a = Mathf.Clamp01((stepT - nudgeAfter) / 0.6f) * (0.65f + 0.25f * Mathf.Sin(t * 4f));
                UI.MenuSkin.TextOutlined(new Rect(bar.x + 110f, capsY - 6f, bar.width - 130f, 28f),
                                         "> " + s.Nudge, 20,
                                         new Color(UI.MenuSkin.Gold.r, UI.MenuSkin.Gold.g, UI.MenuSkin.Gold.b, a),
                                         new Color(UI.MenuSkin.Ink.r, UI.MenuSkin.Ink.g, UI.MenuSkin.Ink.b, a),
                                         2f, TextAnchor.MiddleLeft);
                capsY += 34f;
            }

            if (s.Caps != null) DrawCaps(new Rect(bar.x + 110f, capsY, bar.width - 130f, 52f), s.Caps, win);

            DrawPips(new Rect(bar.x + 110f, bar.y - 26f, 200f, 14f));

            // Rappel du skip, discret : c'est une issue de secours, pas une invitation.
            string skipLabel = skipT > 0.02f
                ? "T MAINTENU  " + Mathf.RoundToInt(Mathf.Clamp01(skipT / skipHold) * 100f) + "%"
                : "T MAINTENU  -  PASSER";
            UI.MenuSkin.TextOutlined(new Rect(bar.xMax - 320f, bar.y - 28f, 300f, 24f), skipLabel, 17,
                                     new Color(1f, 1f, 1f, skipT > 0.02f ? 0.95f : 0.45f),
                                     new Color(UI.MenuSkin.Ink.r, UI.MenuSkin.Ink.g, UI.MenuSkin.Ink.b, 0.8f),
                                     2f, TextAnchor.MiddleRight);

            if (win) DrawStamp(new Vector2(bar.xMax - 140f, bar.center.y));
        }

        // LES TOUCHES SONT LE VRAI TUTO : elles s'allument quand le joueur appuie pour de vrai.
        // Une consigne ecrite se lit une fois ; une touche qui reagit se comprend sans lire.
        private void DrawCaps(Rect area, Cap[] caps, bool win)
        {
            const float gap = 14f, h = 52f;
            float x = area.x;
            for (int i = 0; i < caps.Length; i++)
            {
                bool down = win || (caps[i].Down != null && caps[i].Down());
                var r = new Rect(x, area.y, caps[i].Width, h);

                Matrix4x4 m = GUI.matrix;
                if (down)
                {
                    GUIUtility.ScaleAroundPivot(new Vector2(1.12f, 1.12f), r.center);
                    UI.MenuSkin.Glow(r.center, r.width * 0.75f,
                                     new Color(UI.MenuSkin.Gold.r, UI.MenuSkin.Gold.g, UI.MenuSkin.Gold.b, 0.45f));
                }

                // Meme grammaire que les entrees de menu : fond plein + libelle cerne. Un cadre
                // vide ne se voit pas sur une route eclairee au neon.
                UI.MenuSkin.Fill(r, down ? UI.MenuSkin.Gold
                                         : new Color(UI.MenuSkin.Night.r, UI.MenuSkin.Night.g, UI.MenuSkin.Night.b, 0.8f));
                UI.MenuSkin.Frame(r, 3f, down ? Color.white : UI.MenuSkin.Cyan);
                UI.MenuSkin.TextOutlined(r, caps[i].Label, 22, down ? UI.MenuSkin.Ink : UI.MenuSkin.Cream,
                                         down ? new Color(1f, 1f, 1f, 0.6f) : UI.MenuSkin.Ink, 2f, TextAnchor.MiddleCenter);

                GUI.matrix = m;
                x += caps[i].Width + gap;
            }
        }

        // Petits biseaux penches plutot que des points : meme pente que les bandes du menu.
        private void DrawPips(Rect area)
        {
            float d = 14f, gap = (area.width - steps.Length * d) / Mathf.Max(1, steps.Length - 1);
            for (int i = 0; i < steps.Length; i++)
            {
                var r = new Rect(area.x + i * (d + gap), area.y, d, d);
                bool past = i < step, now = i == step;
                if (now)
                {
                    float s = 1f + 0.35f * Mathf.Sin(t * 6f);
                    r = new Rect(r.center.x - d * 0.5f * s, r.center.y - d * 0.5f * s, d * s, d * s);
                }
                UI.MenuSkin.Wedge(r, past ? UI.MenuSkin.Gold : now ? UI.MenuSkin.Pink : new Color(1f, 1f, 1f, 0.28f));
            }
        }

        private void DrawStamp(Vector2 center)
        {
            float p = Mathf.Clamp01(phaseT / Mathf.Max(0.001f, CelebrateTime));
            // EaseOutBack(0) vaut EXACTEMENT 0 : sans plancher, la 1re frame envoie une matrice
            // d'echelle nulle a GUI.matrix, que Unity refuse ("matrix needs to be invertible").
            float s = Mathf.Max(0.02f, EaseOutBack(Mathf.Clamp01(p * 3f)));
            float a = 1f - Mathf.Clamp01((p - 0.6f) / 0.4f);

            Matrix4x4 m = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), center);
            UI.MenuSkin.Burst(center, 90f, new Color(UI.MenuSkin.Gold.r, UI.MenuSkin.Gold.g, UI.MenuSkin.Gold.b, 0.75f * a));
            UI.MenuSkin.TextOutlined(new Rect(center.x - 130f, center.y - 30f, 260f, 60f),
                                     Stamps[step % Stamps.Length], 42,
                                     new Color(1f, 1f, 1f, a), UI.MenuSkin.Ink, 4f, TextAnchor.MiddleCenter);
            GUI.matrix = m;
        }

        // Explication des deux compteurs : on ENCADRE le vrai HUD et on tire une trainee de
        // chevrons depuis la carte. Meme repere de design (1080) que ScoreGauge / RunHud, donc
        // il suffit de relire leur screenPos pour viser juste, meme si on les deplace.
        private void DrawCallout(float sw, float sh)
        {
            var s = steps[step];
            if (s.Point == Target.None) return;

            Rect box;
            if (s.Point == Target.Gauge)
            {
                if (ScoreGauge.Instance == null) return;
                Vector2 p = ScoreGauge.Instance.ScreenPos;
                box = new Rect(p.x - 76f, p.y - 12f, 520f, 100f);
            }
            else
            {
                if (RunHud.Instance == null) return;
                Vector2 p = RunHud.Instance.ScreenPos;
                box = new Rect(p.x - 10f, p.y - 12f, 452f, 74f);
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 6f);
            Color c = Color.Lerp(UI.MenuSkin.Cyan, UI.MenuSkin.Gold, pulse);
            UI.MenuSkin.Glow(box.center, box.width * 0.55f, new Color(c.r, c.g, c.b, 0.16f + 0.10f * pulse));
            UI.MenuSkin.Frame(box, 4f, c);

            // Trainee : des biseaux (meme pente que les bandes) qui remontent de la bande vers
            // le compteur vise.
            Vector2 from = new Vector2(sw * 0.5f, sh - CardTop - 40f);
            Vector2 to = new Vector2(box.center.x, box.yMax + 14f);
            const int n = 7;
            for (int i = 0; i < n; i++)
            {
                float k = (i / (float)n + t * 0.55f) % 1f;
                Vector2 pos = Vector2.Lerp(from, to, k);
                float a = Mathf.Sin(k * Mathf.PI) * 0.85f;
                float d = 14f + 8f * a;
                UI.MenuSkin.Wedge(new Rect(pos.x - d * 0.5f, pos.y - d * 0.5f, d, d), new Color(c.r, c.g, c.b, a));
            }
        }

        private void DrawOutro(float sw, float sh)
        {
            float p = Mathf.Clamp01(phaseT / Mathf.Max(0.001f, OutroTime));
            float s = Mathf.Max(0.02f, EaseOutBack(Mathf.Clamp01(p * 2.6f)));   // jamais 0 : cf DrawStamp
            float a = 1f - Mathf.Clamp01((p - 0.65f) / 0.35f);
            var center = new Vector2(sw * 0.5f, sh * 0.5f);

            Matrix4x4 m = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), center);
            UI.MenuSkin.Burst(center, 320f, new Color(UI.MenuSkin.Pink.r, UI.MenuSkin.Pink.g, UI.MenuSkin.Pink.b, 0.55f * a));
            UI.MenuSkin.TextOutlined(new Rect(center.x - 600f, center.y - 70f, 1200f, 140f),
                                     "A TOI DE JOUER !", 92, new Color(1f, 1f, 1f, a),
                                     UI.MenuSkin.Ink, 6f, TextAnchor.MiddleCenter);
            UI.MenuSkin.Text(new Rect(center.x - 600f, center.y + 66f, 1200f, 40f),
                             "LA POPULARITE COMMENCE A FONDRE", 26,
                             new Color(UI.MenuSkin.Gold.r, UI.MenuSkin.Gold.g, UI.MenuSkin.Gold.b, a), TextAnchor.MiddleCenter);
            GUI.matrix = m;
        }

        // Meme courbe que le reste du HUD (RunHud / ScoreGauge / RunEnd) : depassement puis retour.
        private static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float u = x - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }
}
