using UnityEngine;

namespace Gameplay
{
    // JAUGE DE POINTS : ressource qui FOND en continu. On demarre plein-ish, ca descend tout
    // seul, et il faut enchainer figures + livraisons pour remonter. Source unique de verite
    // pour "les points" :
    //   Value  = charge courante, SANS PLAFOND, c'est elle qui se vide ;
    //   Earned = cumul de tout ce qui a ete gagne, ne redescend JAMAIS.
    // AUCUNE limite haute : la seule borne du jeu est le zero. La barre ne mesure donc pas
    // "combien sur un maximum" (il n'y en a pas) mais LE TEMPS DE SURVIE restant a la fonte
    // courante -- la seule lecture qui ait un sens quand la valeur est libre de monter.
    // Les zones verrouillees se basent sur Earned : un seuil doit rester atteint une fois
    // franchi, sinon une zone se refermerait pendant qu'on roule vers elle.
    // Les points de figures sont lus sur TrickSystem.Total (sa banque) par DELTA -> pas besoin
    // de le modifier. Les livraisons appellent Add().
    // Rendu OnGUI -> zero Canvas, comme GrindBalanceHud / TrickHud.
    public class ScoreGauge : MonoBehaviour
    {
        public static ScoreGauge Instance { get; private set; }

        [Header("Ressource")]
        [SerializeField] private float startValue = 975f;
        [SerializeField] private float drainPerSecond = 10f;
        // Echelle de la BARRE seulement : temps de survie affiche a barre pleine. Ce n'est pas
        // un plafond -- au-dela la barre reste pleine et passe a l'or, la valeur continue.
        [SerializeField] private float fullBarSeconds = 120f;
        [SerializeField] private float lowThreshold = 0.25f;   // en dessous : alarme
        // Ecraser un PNJ fait fuir les temoins... et les abonnes. C'est la contrepartie du
        // carnage : la conduite sale rapporte des figures mais coute de la popularite.
        [SerializeField] private int killPenalty = 250;

        [Header("Sources")]
        [SerializeField] private TrickSystem tricks;

        [Header("Placement (unites de design, cf referenceHeight)")]
        // Coordonnees de la maquette Figma : barre 420x38 posee en (119, 75), pastille
        // d'icone 75x75 en (57, 56). screenPos est le coin du BLOC (la barre est a +20 en y).
        [SerializeField] private Vector2 screenPos = new Vector2(119f, 55f);
        [SerializeField] private float width = 420f;
        [SerializeField] private float height = 38f;
        // OnGUI travaille en pixels bruts : sans mise a l'echelle, la jauge devient un
        // timbre-poste en 4K et un pave en 720p. Tout est dessine comme si l'ecran faisait
        // referenceHeight de haut, puis mis a l'echelle.
        [SerializeField] private float referenceHeight = 1080f;

        // Coin du bloc en unites de design : le tuto s'en sert pour encadrer la vraie jauge
        // au lieu d'en redessiner une copie a cote.
        public Vector2 ScreenPos => screenPos;

        public float Value { get; private set; }
        public int Earned { get; private set; }

        // Multiplicateur de fonte, pousse de l'exterieur (escalade du dernier colis, cf RunHud).
        // 1 = rythme normal.
        public float DrainMultiplier { get; set; } = 1f;

        // Secondes avant le zero a la fonte courante. C'est LA lecture du joueur.
        public float SecondsLeft
        {
            get
            {
                float d = drainPerSecond * Mathf.Max(0f, DrainMultiplier);
                return d <= 0.001f ? float.PositiveInfinity : Value / d;
            }
        }

        // Remplissage de la barre : 1 = au moins fullBarSeconds de marge. Au-dela, la barre
        // ne peut plus grandir, c'est la couleur qui prend le relais (cf `over` dans OnGUI).
        public float Fill01 => fullBarSeconds <= 0f ? 0f : Mathf.Clamp01(SecondsLeft / fullBarSeconds);
        public bool Empty => Value <= 0.001f;

        // Ajoute des points : remonte la jauge ET le cumul. Aucun ecretage : une chaine a x10
        // sur une jauge deja bien remplie doit valoir ses points, sinon le beau jeu est puni.
        public void Add(int pts)
        {
            if (pts <= 0) return;
            Value += pts;
            Earned += pts;
            gainPop = 1f;
            gainFloat = pts;
            gainFloatT = 0f;
        }

        // Retire des points SANS toucher au cumul : Earned est un historique de ce qui a ete
        // gagne (il ouvre les zones verrouillees), une bavure ne doit pas le faire reculer.
        public void Penalize(int pts)
        {
            if (pts <= 0) return;
            Value = Mathf.Max(0f, Value - pts);
            penaltyPop = 1f;
            gainFloat = -pts;
            gainFloatT = 0f;
        }

        private int lastTrickTotal;
        private float gainPop;        // 0..1, flash a chaque gain
        private float penaltyPop;     // 0..1, flash rouge a chaque bavure
        private float gainFloatT = -1f;
        private int gainFloat;        // signe : positif = gain, negatif = penalite
        private float t, shownValue;

        // Couleurs de la maquette Figma (jauge de score).
        private static readonly Color GradA = new Color(0.861f, 0.270f, 0.165f);   // #DC4529, cote vide
        private static readonly Color GradB = new Color(1f, 0.917f, 0f);           // #FFEA00, cote plein
        private static readonly Color IconGrey = new Color(0.851f, 0.851f, 0.851f);

        private void Awake()
        {
            Instance = this;
            if (tricks == null) tricks = GetComponent<TrickSystem>();
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
            lastTrickTotal = tricks != null ? tricks.Total : 0;
            Value = Mathf.Max(0f, startValue);
            shownValue = Value;
        }

        // Tous les kills passent par ce hub (pieton comme pigeon) : un seul branchement
        // suffit, inutile de penaliser depuis chaque creature.
        private void OnEnable() => Creatures.Killed += OnCreatureKilled;
        private void OnDisable() => Creatures.Killed -= OnCreatureKilled;

        private void OnCreatureKilled(Vector3 pos, bool byPlayer)
        {
            if (byPlayer) Penalize(killPenalty);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            t += Time.unscaledDeltaTime;

            // figures : on suit la banque de TrickSystem par DELTA, sans y toucher
            if (tricks != null && tricks.Total != lastTrickTotal)
            {
                int delta = tricks.Total - lastTrickTotal;
                lastTrickTotal = tricks.Total;
                if (delta > 0) Add(delta);
            }

            Value = Mathf.Max(0f, Value - drainPerSecond * Mathf.Max(0f, DrainMultiplier) * dt);

            gainPop = Mathf.Max(0f, gainPop - Time.unscaledDeltaTime * 2f);
            penaltyPop = Mathf.Max(0f, penaltyPop - Time.unscaledDeltaTime * 1.6f);
            if (gainFloatT >= 0f)
            {
                gainFloatT += Time.unscaledDeltaTime;
                if (gainFloatT > 1.1f) gainFloatT = -1f;
            }
            // la barre affichee rattrape la vraie en roulant (jamais de saut sec)
            shownValue = Mathf.Lerp(shownValue, Value, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
        }

        private void OnGUI()
        {
            if (UI.MenuFlow.Blocking || RunEnd.Finished) return;   // menus / ecran de fin

            // La barre lit un TEMPS, pas un ratio sur un plafond : `barSeconds` secondes de
            // marge = barre pleine. La valeur affichee suit `shownValue` (elle roule).
            float drain = drainPerSecond * Mathf.Max(0f, DrainMultiplier);
            float secondsShown = drain <= 0.001f ? fullBarSeconds : shownValue / drain;
            float f = fullBarSeconds <= 0f ? 0f : Mathf.Clamp01(secondsShown / fullBarSeconds);
            // Marge au-dela de la barre pleine : c'est elle qui pousse la barre vers l'or.
            float over = fullBarSeconds <= 0f
                ? 0f
                : Mathf.Clamp01((secondsShown - fullBarSeconds) / fullBarSeconds);
            bool low = f <= lowThreshold;
            float alarm = low ? 1f - f / Mathf.Max(0.001f, lowThreshold) : 0f;

            // secousse + pulsation quand la jauge se vide (la bavure secoue aussi)
            float shake = alarm * 4f + penaltyPop * 6f;
            float x = screenPos.x + Mathf.Sin(t * 47f) * shake;
            float y = screenPos.y + Mathf.Cos(t * 53f) * shake * 0.7f;
            float s = 1f + 0.10f * EaseOutBack(gainPop) + 0.03f * alarm * Mathf.Sin(t * 9f);

            Matrix4x4 m0 = GUI.matrix;
            // 1) resolution -> tout le reste est en unites de design
            float k = referenceHeight > 1f ? Screen.height / referenceHeight : 1f;
            GUIUtility.ScaleAroundPivot(new Vector2(k, k), Vector2.zero);
            // 2) pop de gain / pulsation, autour de la jauge elle-meme (donc sans la deplacer)
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(x + width * 0.5f, y + 20f));

            var frame = new Rect(x, y + 20f, width, height);
            EnsureTextures();

            // Couleur du bout de barre : c'est le degrade de la maquette lu a l'endroit du
            // remplissage. Sert au halo et au "+N", pour que le juice reste raccord.
            Color tip = Color.Lerp(GradA, GradB, f);
            // bavure : tout le contour vire au rouge le temps du flash
            Color edge = penaltyPop > 0f
                ? Color.Lerp(Color.black, new Color(1f, 0.15f, 0.2f), penaltyPop)
                : Color.black;

            // Pastille d'icone de la maquette (75x75, calee a gauche et chevauchant la barre).
            // Reste un aplat gris tant que l'illustration n'est pas fournie -- c'est aussi
            // l'etat du Figma.
            Round(new Rect(frame.x - 62f, frame.center.y - 37.5f, 75f, 75f), IconGrey);

            // halo, respire quand ca va mal
            Round(new Rect(frame.x - 6f, frame.y - 6f, frame.width + 12f, frame.height + 12f),
                  new Color(tip.r, tip.g, tip.b, 0.16f + 0.35f * gainPop + 0.25f * alarm));

            Round(frame, edge);                                            // cadre noir 3 px
            var inner = new Rect(frame.x + 3f, frame.y + 3f, frame.width - 6f, frame.height - 6f);
            Round(inner, new Color(0.05f, 0.06f, 0.12f, 0.94f));           // fond de la partie vide

            // REMPLISSAGE : le degrade est peint sur la LARGEUR TOTALE puis coupe a `f`.
            // Peindre un degrade re-etire sur la portion remplie ferait mentir la couleur --
            // la barre serait jaune vif a 5 %.
            if (f > 0.001f)
            {
                var bar = new Rect(inner.x, inner.y, inner.width * f, inner.height);
                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(bar, gradTex, new Rect(0f, 0f, f, 1f));
                // alarme : le remplissage bat en rouge quand il ne reste presque rien
                if (alarm > 0f)
                    Fill(bar, new Color(1f, 0.1f, 0.1f, 0.35f * alarm * (0.5f + 0.5f * Mathf.Sin(t * 12f))));
                // au-dela de la barre pleine : voile dore pulsant, sinon le joueur croirait
                // que ses points ne comptent plus une fois la barre au maximum.
                if (over > 0f)
                    Fill(bar, new Color(1f, 0.95f, 0.6f,
                                        0.30f * over * (0.6f + 0.4f * Mathf.Sin(t * 5f))));
            }

            // libelle + valeur
            Label(new Rect(frame.x + 1f, y - 1f, 240f, 20f), low ? "POPULARITE -- CRITIQUE" : "POPULARITE", 13,
                  low ? Color.Lerp(new Color(1f, 0.4f, 0.3f), Color.white, 0.5f + 0.5f * Mathf.Sin(t * 12f))
                      : new Color(0.6f, 0.85f, 1f, 0.9f),
                  TextAnchor.UpperLeft);

            // Jamais de "/ quelque chose" : il n'y a plus de borne haute. La valeur brute, et
            // a cote le temps qu'elle represente -- c'est ce dont le joueur decide.
            string val = Mathf.CeilToInt(shownValue).ToString("N0")
                       + "   " + Mathf.CeilToInt(secondsShown) + " s";
            Label(new Rect(frame.x + 3f, frame.y + 4f, frame.width - 8f, frame.height - 6f), val, 17,
                  new Color(0f, 0f, 0f, 0.5f), TextAnchor.MiddleRight);
            Label(new Rect(frame.x + 1f, frame.y + 2f, frame.width - 10f, frame.height - 6f), val, 17,
                  Color.white, TextAnchor.MiddleRight);

            // cumul gagne (c'est lui qui ouvre les zones verrouillees)
            Label(new Rect(frame.x + 1f, frame.yMax + 4f, 260f, 18f),
                  "CUMUL  " + Earned.ToString("N0"), 11, new Color(0.65f, 0.8f, 1f, 0.75f), TextAnchor.UpperLeft);

            // "+N" qui monte a chaque gain, "-N" rouge a chaque bavure
            if (gainFloatT >= 0f)
            {
                float rise01 = gainFloatT / 1.1f;
                bool loss = gainFloat < 0;
                var fr = new Rect(frame.x + frame.width * 0.5f, frame.y - 6f - 34f * rise01, 220f, 26f);
                Label(fr, (loss ? "-" : "+") + Mathf.Abs(gainFloat).ToString("N0"), 22,
                      loss ? new Color(1f, 0.35f, 0.35f, 1f - rise01)
                           : new Color(0.5f, 1f, 0.6f, 1f - rise01), TextAnchor.UpperLeft);
                if (loss)
                    Label(new Rect(fr.x + 74f, fr.y + 4f, 260f, 22f), "TU AS TUE QUELQU'UN", 14,
                          new Color(1f, 0.5f, 0.5f, 0.9f * (1f - rise01)), TextAnchor.UpperLeft);
            }

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // ---- textures cuites (coins arrondis + degrade de la maquette) ----
        //
        // OnGUI ne sait dessiner que des quads : les coins arrondis (r=5) et le degrade
        // sont CUITS une fois dans deux textures plutot que recalcules par frame.
        //   maskTex : rectangle blanc a coins arrondis -> sert de pochoir teinte (GUI.color)
        //             pour le cadre noir, le fond, la pastille d'icone et le halo ;
        //   gradTex : le meme pochoir, mais peint du degrade #DC4529 -> #FFEA00.
        private Texture2D maskTex, gradTex;

        private void EnsureTextures()
        {
            if (maskTex != null && gradTex != null) return;
            int w = Mathf.Max(8, Mathf.RoundToInt(width));
            int h = Mathf.Max(8, Mathf.RoundToInt(height));
            maskTex = MakeRounded(w, h, 5f, null);
            gradTex = MakeRounded(w, h, 5f, u => Color.Lerp(GradA, GradB, u));
        }

        // Rectangle a coins arrondis. `grad` nul -> blanc pur (pochoir a teinter) ;
        // sinon la couleur est prise sur l'axe horizontal (0 a gauche, 1 a droite).
        private static Texture2D MakeRounded(int w, int h, float radius, System.Func<float, Color> grad)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            float r = Mathf.Min(radius, Mathf.Min(w, h) * 0.5f);
            var px = new Color[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    float cx = Mathf.Clamp(i + 0.5f, r, w - r);
                    float cy = Mathf.Clamp(j + 0.5f, r, h - r);
                    float d = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(r - d + 0.5f);      // 1 px d'antialiasing sur le rayon
                    float u = w > 1 ? i / (float)(w - 1) : 0f;
                    Color c = grad != null ? grad(u) : Color.white;
                    c.a = a;
                    px[j * w + i] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // Aplat a coins arrondis (pochoir teinte).
        private void Round(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, maskTex);
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
