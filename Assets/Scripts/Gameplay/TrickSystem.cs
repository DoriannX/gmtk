using UnityEngine;

namespace Gameplay
{
    // Detection de figures facon Forza poussee cartoon, EN TEMPS REEL. Pendant un
    // saut, chaque axe montre un slap live qui ESCALADE au fil des tours
    // (FLIP -> DOUBLE FLIP -> TRIPLE -> QUAD -> MONSTER). A l'atterrissage :
    //  - sur les roues : le saut est valide -> score * multiplicateur de chaine ;
    //  - sur le toit : RATE ! -> chaine perdue.
    // La chaine se banque apres un court delai au sol sans nouvelle figure.
    [RequireComponent(typeof(ArcadeCarController))]
    public class TrickSystem : MonoBehaviour
    {
        [SerializeField] private float turnPerTrick = 320f;   // deg par tour valide (< 360 = indulgent)
        [SerializeField] private float uprightDot = 0.5f;     // up.monde mini pour "sur les roues"
        [SerializeField] private float comboWindow = 2.6f;    // temps au sol sans figure avant de banquer
        [SerializeField] private float bigAirTime = 0.9f;     // airtime mini pour bonus GROS SAUT
        [SerializeField] private float driftMinTime = 0.7f;   // duree mini de glisse pour un DRIFT
        [SerializeField] private int maxMultiplier = 10;

        [Header("Points de base")]
        [SerializeField] private int spinPts = 250;
        [SerializeField] private int flipPts = 450;
        [SerializeField] private int bigAirPtsPerSec = 260;

        [Header("Grind")]
        [SerializeField] private int grindPtsPerMeter = 8;  // points par metre glisse (score = DISTANCE, pas temps)
        [SerializeField] private float grindMinDist = 2f;   // distance mini pour scorer un grind

        private static readonly Color SpinCol = new Color(0.35f, 0.8f, 1f);
        private static readonly Color FlipCol = new Color(1f, 0.55f, 0.2f);
        private static readonly Color GrindCol = new Color(1f, 0.45f, 0.85f);

        private float grindDist;
        private bool wasRail;

        private ArcadeCarController car;
        private TrickHud hud;

        private bool airborne;
        private float airTime;
        private int shownSpins, shownFlips;

        private float driftTime, driftAccum;

        private int chainScore, comboCount;
        private float window;
        public int Total { get; private set; }

        // Etat de chaine expose pour le HUD : la chaine est le 3e compteur du jeu, il faut
        // pouvoir la dessiner en compte a rebours. window ne descend qu'AU SOL (cf Update) ->
        // en l'air la fenetre reste pleine, ce qui est le comportement voulu.
        public bool ChainAlive => chainScore > 0;
        public float ComboWindow01 => chainScore > 0 && comboWindow > 0f ? Mathf.Clamp01(window / comboWindow) : 0f;
        public int ComboCount => comboCount;
        public int ChainScore => chainScore;

        // Records de la run, pour l'ecran de fin.
        public int BestCombo { get; private set; }
        public float BestGrind { get; private set; }

        private void Awake()
        {
            car = GetComponent<ArcadeCarController>();
            hud = GetComponent<TrickHud>();
            if (hud == null) hud = gameObject.AddComponent<TrickHud>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            HandleGrind(dt); // grind = etat propre (au sol logiquement), gere avant les branches air/sol
            if (!car.Grounded)
            {
                if (!airborne) BeginAir();
                airTime += dt;
                UpdateLive();
            }
            else
            {
                if (airborne) Land();
                HandleDrift(dt);
                if (chainScore > 0)
                {
                    window -= dt;
                    if (window <= 0f) Bank();
                }
            }
        }

        private void BeginAir()
        {
            airborne = true;
            airTime = 0f;
            shownSpins = shownFlips = 0;
        }

        // Affichage temps reel : des qu'un tour de plus est boucle, le slap escalade.
        private void UpdateLive()
        {
            int spins = Mathf.FloorToInt(Mathf.Abs(car.AirSpinDeg) / turnPerTrick);
            int flips = Mathf.FloorToInt(Mathf.Abs(car.AirFlipDeg) / turnPerTrick);
            if (spins >= 1 && spins != shownSpins) { shownSpins = spins; hud.SetLive("spin", SpinLabel(spins, car.AirSpinDeg), SpinCol, true); }
            if (flips >= 1 && flips != shownFlips) { shownFlips = flips; hud.SetLive("flip", FlipLabel(flips, car.AirFlipDeg), FlipCol, true); }
        }

        private void Land()
        {
            airborne = false;
            hud.ClearLive();

            int spins = Mathf.FloorToInt(Mathf.Abs(car.AirSpinDeg) / turnPerTrick);
            int flips = Mathf.FloorToInt(Mathf.Abs(car.AirFlipDeg) / turnPerTrick);
            bool bigAir = airTime > bigAirTime;

            // Pas sur les roues apres un VRAI saut -> RATE, chaine perdue. Independant
            // d'une figure complete : un demi-flip qui finit sur la tete est un crash,
            // pas un "gros saut". airTime mini -> ignore les micro-bosses de suspension.
            if (car.LandUprightDot < uprightDot && airTime > 0.25f)
            {
                hud.Wasted();
                ResetChain();
                return;
            }

            int jump = spins * spinPts + flips * flipPts;
            if (bigAir) jump += Mathf.RoundToInt(airTime * bigAirPtsPerSec);
            if (jump <= 0) return; // simple saut sans rien : pas de score

            AwardChain(Summary(spins, flips, bigAir, car.AirSpinDeg, car.AirFlipDeg), jump, true);
        }

        // GRIND : les points s'accumulent avec la DISTANCE glissee (vitesse*dt), pas le temps ->
        // rester immobile sur le rail ne rapporte rien (fair). Affichage live qui monte, banque a
        // la sortie (chute/saut/fin de rail) via la meme chaine/combo que les figures.
        private void HandleGrind(float dt)
        {
            if (car.OnRail)
            {
                grindDist += car.GrindSpeedAbs * dt;
                wasRail = true;
                hud.SetLive("grind", $"GRIND {Mathf.RoundToInt(grindDist * grindPtsPerMeter)}", GrindCol, true);
            }
            else if (wasRail)
            {
                wasRail = false;
                hud.ClearLive();
                if (grindDist >= grindMinDist)
                {
                    if (grindDist > BestGrind) BestGrind = grindDist;
                    AwardChain($"GRIND {Mathf.RoundToInt(grindDist)}m", Mathf.RoundToInt(grindDist * grindPtsPerMeter));
                }
                grindDist = 0f;
            }
        }

        private void HandleDrift(float dt)
        {
            if (car.Drifting && car.Speed > 6f)
            {
                driftTime += dt;
                driftAccum += car.Speed * dt;
            }
            else if (driftTime > 0f)
            {
                if (driftTime >= driftMinTime)
                    AwardChain("DRIFT", Mathf.RoundToInt(driftAccum * 3f));
                driftTime = 0f; driftAccum = 0f;
            }
        }

        // Public : les livraisons alimentent la MEME chaine que les figures (cf DeliveryQuest).
        // Livrer en plein combo vaut donc jusqu'a x10, et relance la fenetre -> la ligne parfaite
        // est un seul combo qui traverse la ville EN livrant.
        public void AwardChain(string label, int basePts, bool landed = false)
        {
            if (basePts <= 0) return;
            comboCount++;
            int mult = Mathf.Clamp(comboCount, 1, maxMultiplier);
            int pts = basePts * mult;
            chainScore += pts;
            window = comboWindow;
            if (comboCount > BestCombo) BestCombo = comboCount;
            hud.ScorePop(label, pts, mult, landed);
        }

        // Banque immediatement ce qui est en cours. Sert de derniere chance : les points d'une
        // livraison n'arrivent dans la jauge qu'au Bank(), mourir pendant cette fenetre alors
        // qu'on vient de livrer serait vole (cf RunEnd).
        public void ForceBank()
        {
            if (chainScore > 0) Bank();
        }

        private void Bank()
        {
            Total += chainScore;
            // "CHAINE" seulement pour un VRAI enchainement (2+ figures/drifts). Une
            // seule figure : le tampon ✓ a deja montre le score -> banque en silence,
            // pas de second texte redondant.
            if (comboCount >= 2) hud.Bank(chainScore, comboCount);
            ResetChain();
        }

        private void ResetChain()
        {
            chainScore = 0; comboCount = 0; window = 0f;
        }

        private static string Tier(string basic, int n)
        {
            switch (n)
            {
                case 1: return basic;
                case 2: return "DOUBLE " + basic;
                case 3: return "TRIPLE " + basic;
                case 4: return "QUAD " + basic;
                default: return "MONSTER " + basic;
            }
        }

        // Flip : signe du pitch cumule = sens. >0 (touche avant/W) = FRONT FLIP,
        // <0 (marche arriere/S) = BACKFLIP. Escalade DOUBLE/TRIPLE/...
        private static string FlipLabel(int n, float signedDeg)
            => Tier(signedDeg >= 0f ? "FRONT FLIP" : "BACKFLIP", n);

        // Spin : yaw a plat. Sens indique par une fleche (gauche/droite).
        private static string SpinLabel(int n, float signedDeg)
            => Tier((signedDeg >= 0f ? "SPIN >" : "< SPIN"), n);

        // Titre du saut : la figure dominante (avec son sens).
        private static string Summary(int spins, int flips, bool bigAir, float spinDeg, float flipDeg)
        {
            if (flips >= spins && flips >= 1) return FlipLabel(flips, flipDeg);
            if (spins >= 1) return SpinLabel(spins, spinDeg);
            return "GROS SAUT";
        }
    }
}
