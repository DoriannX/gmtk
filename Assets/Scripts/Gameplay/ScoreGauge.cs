using UnityEngine;

namespace Gameplay
{
    // JAUGE DE POINTS : ressource qui FOND en continu. On demarre plein-ish, ca descend tout
    // seul, et il faut enchainer figures + livraisons pour remonter. Source unique de verite
    // pour "les points" :
    //   Value  = charge courante (0..Max), c'est elle qui se vide ;
    //   Earned = cumul de tout ce qui a ete gagne, ne redescend JAMAIS.
    // Les zones verrouillees se basent sur Earned : un seuil doit rester atteint une fois
    // franchi, sinon une zone se refermerait pendant qu'on roule vers elle.
    // Les points de figures sont lus sur TrickSystem.Total (sa banque) par DELTA -> pas besoin
    // de le modifier. Les livraisons appellent Add().
    // Rendu OnGUI -> zero Canvas, comme GrindBalanceHud / TrickHud.
    public class ScoreGauge : MonoBehaviour
    {
        public static ScoreGauge Instance { get; private set; }

        [Header("Ressource")]
        [SerializeField] private float max = 1500f;
        [SerializeField] private float startValue = 975f;   // 65 % du max, comme avant
        [SerializeField] private float drainPerSecond = 10f;
        [SerializeField] private float lowThreshold = 0.25f;   // en dessous : alarme

        [Header("Sources")]
        [SerializeField] private TrickSystem tricks;

        [Header("Placement (unites de design, cf referenceHeight)")]
        // sous le DebugOverlay (~DebugOverlay), qui occupe le coin haut-gauche
        [SerializeField] private Vector2 screenPos = new Vector2(28f, 120f);
        [SerializeField] private float width = 420f;
        [SerializeField] private float height = 38f;
        // OnGUI travaille en pixels bruts : sans mise a l'echelle, la jauge devient un
        // timbre-poste en 4K et un pave en 720p. Tout est dessine comme si l'ecran faisait
        // referenceHeight de haut, puis mis a l'echelle.
        [SerializeField] private float referenceHeight = 1080f;

        public float Value { get; private set; }
        public float Max => max;
        public int Earned { get; private set; }

        // Multiplicateur de fonte, pousse de l'exterieur (escalade du dernier colis, cf RunHud).
        // 1 = rythme normal.
        public float DrainMultiplier { get; set; } = 1f;
        public float Fill01 => max <= 0f ? 0f : Mathf.Clamp01(Value / max);
        public bool Empty => Value <= 0.001f;

        // Ajoute des points : remonte la jauge ET le cumul.
        public void Add(int pts)
        {
            if (pts <= 0) return;
            Value = Mathf.Min(max, Value + pts);
            Earned += pts;
            gainPop = 1f;
            gainFloat = pts;
            gainFloatT = 0f;
        }

        private int lastTrickTotal;
        private float gainPop;        // 0..1, flash a chaque gain
        private float gainFloatT = -1f;
        private int gainFloat;
        private float t, shownValue;
        private Texture2D disc;

        private void Awake()
        {
            Instance = this;
            if (tricks == null) tricks = GetComponent<TrickSystem>();
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
            lastTrickTotal = tricks != null ? tricks.Total : 0;
            Value = Mathf.Clamp(startValue, 0f, max);
            shownValue = Value;
            disc = MakeDisc(64);
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
            float f = max <= 0f ? 0f : Mathf.Clamp01(shownValue / max);
            bool low = f <= lowThreshold;
            float alarm = low ? 1f - f / Mathf.Max(0.001f, lowThreshold) : 0f;

            // secousse + pulsation quand la jauge se vide
            float shake = alarm * 4f;
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
            Color hot = Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(1f, 0.85f, 0.2f), Mathf.InverseLerp(0f, 0.5f, f));
            Color fillCol = Color.Lerp(hot, new Color(0.3f, 1f, 0.55f), Mathf.InverseLerp(0.5f, 1f, f));
            Color accent = Color.Lerp(fillCol, Color.white, 0.35f + 0.5f * gainPop);

            // halo, respire quand ca va mal
            Fill(new Rect(frame.x - 6f, frame.y - 6f, frame.width + 12f, frame.height + 12f),
                 new Color(fillCol.r, fillCol.g, fillCol.b, 0.16f + 0.35f * gainPop + 0.25f * alarm));

            Fill(frame, new Color(0.05f, 0.06f, 0.12f, 0.94f));          // fond
            Fill(new Rect(frame.x, frame.y, frame.width, 2f), new Color(1f, 1f, 1f, 0.10f));  // reflet haut

            // REMPLISSAGE + hachures diagonales qui defilent (lecture cartoon)
            var bar = new Rect(frame.x + 3f, frame.y + 3f, (frame.width - 6f) * f, frame.height - 6f);
            Fill(bar, fillCol);
            Fill(new Rect(bar.x, bar.y, bar.width, bar.height * 0.45f),
                 new Color(1f, 1f, 1f, 0.14f));                 // brillance haute, look plastique
            DrawCells(bar, low ? 46f : 20f);

            // bout de barre incandescent
            if (f > 0.001f)
            {
                Fill(new Rect(bar.xMax - 3f, bar.y, 3f, bar.height), Color.Lerp(fillCol, Color.white, 0.8f));
                Blit(bar.xMax, bar.center.y, frame.height * 0.85f,
                     new Color(fillCol.r, fillCol.g, fillCol.b, 0.5f + 0.3f * gainPop));
            }

            // graduations tous les 10 % : segmente = plus lisible qu'une barre lisse
            for (int i = 1; i < 10; i++)
            {
                float gx = frame.x + 3f + (frame.width - 6f) * (i / 10f);
                Fill(new Rect(gx, frame.y + 3f, 1f, frame.height - 6f), new Color(0f, 0f, 0f, 0.35f));
            }

            Frame(frame, 3f, accent);                                     // cadre epais, cartoon
            Frame(new Rect(frame.x - 3f, frame.y - 3f, frame.width + 6f, frame.height + 6f), 1f,
                  new Color(0.02f, 0.02f, 0.05f, 0.85f));                 // trait sombre exterieur
            // liseres d'angle facon enseigne
            Fill(new Rect(frame.x, frame.y - 4f, 34f, 3f), new Color(1f, 0.25f, 0.85f, 0.95f));
            Fill(new Rect(frame.xMax - 34f, frame.yMax + 1f, 34f, 3f), new Color(1f, 0.25f, 0.85f, 0.95f));

            // libelle + valeur
            Label(new Rect(frame.x + 1f, y - 1f, 240f, 20f), low ? "POPULARITE -- CRITIQUE" : "POPULARITE", 13,
                  low ? Color.Lerp(new Color(1f, 0.4f, 0.3f), Color.white, 0.5f + 0.5f * Mathf.Sin(t * 12f))
                      : new Color(0.6f, 0.85f, 1f, 0.9f),
                  TextAnchor.UpperLeft);

            string val = Mathf.CeilToInt(shownValue).ToString("N0") + " / " + Mathf.RoundToInt(max).ToString("N0");
            Label(new Rect(frame.x + 3f, frame.y + 4f, frame.width - 8f, frame.height - 6f), val, 17,
                  new Color(0f, 0f, 0f, 0.5f), TextAnchor.MiddleRight);
            Label(new Rect(frame.x + 1f, frame.y + 2f, frame.width - 10f, frame.height - 6f), val, 17,
                  Color.white, TextAnchor.MiddleRight);

            // cumul gagne (c'est lui qui ouvre les zones verrouillees)
            Label(new Rect(frame.x + 1f, frame.yMax + 4f, 260f, 18f),
                  "CUMUL  " + Earned.ToString("N0"), 11, new Color(0.65f, 0.8f, 1f, 0.75f), TextAnchor.UpperLeft);

            // "+N" qui monte a chaque gain
            if (gainFloatT >= 0f)
            {
                float rise01 = gainFloatT / 1.1f;
                var fr = new Rect(frame.x + frame.width * 0.5f, frame.y - 6f - 34f * rise01, 160f, 26f);
                Label(fr, "+" + gainFloat.ToString("N0"), 22,
                      new Color(0.5f, 1f, 0.6f, 1f - rise01), TextAnchor.UpperLeft);
            }

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // Motif interieur : cellules d'energie qui defilent + un reflet qui balaie.
        // Tout est clippe A LA MAIN (Intersect) : GUI.BeginGroup ne retient PAS ce qui est
        // dessine sous une rotation de matrice, les hachures debordaient de la barre.
        private void DrawCells(Rect bar, float speed)
        {
            if (bar.width <= 1f) return;
            const float cell = 26f, gap = 7f;
            float o = Mathf.Repeat(t * speed, cell + gap);
            for (float px = bar.x - cell - gap; px < bar.xMax; px += cell + gap)
                FillClipped(new Rect(px + o, bar.y, gap, bar.height), bar, new Color(0f, 0f, 0f, 0.16f));

            // reflet large qui balaie la barre
            float sweep = Mathf.Repeat(t * 0.35f, 1.6f) / 1.6f;
            float sx = Mathf.Lerp(bar.x - 60f, bar.xMax + 60f, sweep);
            FillClipped(new Rect(sx, bar.y, 34f, bar.height), bar, new Color(1f, 1f, 1f, 0.16f));
            FillClipped(new Rect(sx + 14f, bar.y, 6f, bar.height), bar, new Color(1f, 1f, 1f, 0.22f));
        }

        private static void FillClipped(Rect r, Rect clip, Color c)
        {
            float x0 = Mathf.Max(r.x, clip.x), x1 = Mathf.Min(r.xMax, clip.xMax);
            float y0 = Mathf.Max(r.y, clip.y), y1 = Mathf.Min(r.yMax, clip.yMax);
            if (x1 <= x0 || y1 <= y0) return;
            Fill(new Rect(x0, y0, x1 - x0, y1 - y0), c);
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
