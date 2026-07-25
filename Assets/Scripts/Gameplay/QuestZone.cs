using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;

namespace Gameplay
{
    // Zone de quete : le joueur doit RESTER holdTime secondes dedans pour valider.
    // Presence testee par Collider.ClosestPoint chaque frame, PAS par les evenements trigger :
    // ceux-ci sautent des qu'un rigidbody dort, qu'on teleporte le joueur, ou que la zone
    // s'active autour de lui (cas de la zone de livraison) -> quete bloquee. Un test
    // geometrique n'a aucun de ces angles morts. Le collider reste isTrigger pour ne pas
    // bloquer la moto. (ClosestPoint ne gere pas les MeshCollider concaves : zones = boites.)
    // Jauge dessinee en OnGUI au-dessus de la zone -> zero Canvas, comme GrindBalanceHud.
    // Le visuel reste un simple CUBE. Le placage de la base sur le sol est ENTIEREMENT dans le
    // shader (GMTK/NeonZone, via le depth buffer) : rien a precalculer ici, la zone peut etre
    // deplacee librement, le relief est suivi dans la frame. Le script ne fait que la quete et
    // pousse progress / done / squash au shader.
    [RequireComponent(typeof(Collider), typeof(MeshFilter))]
    public class QuestZone : MonoBehaviour
    {
        [SerializeField] private float holdTime = 3f;        // duree a tenir dans la zone
        [SerializeField] private bool resetOnExit = false;   // true = repart de zero si on sort
        [SerializeField] private float drainSpeed = 0.5f;    // sinon : vitesse de perte hors zone (x temps)
        [SerializeField] private bool once = true;           // ne se valide qu'une fois
        [SerializeField] private string label = "RESTE DANS LA ZONE";
        [SerializeField] private Renderer fill;              // optionnel : visuel de la zone
        [SerializeField] private Vector3 hudWorldOffset = new Vector3(0f, 3f, 0f);
        [SerializeField] private float hudWorldWidth = 3.4f;   // largeur REELLE du panneau, en metres
        [SerializeField] private Vector2 hudScaleClamp = new Vector2(0.15f, 3f);

        [Header("Verrou (0 = zone ouverte)")]
        [SerializeField] private int requiredScore;          // score exige pour pouvoir entrer
        [SerializeField] private float denyRadius = 1.1f;    // distance au mur qui declenche le refus

        [Header("Juice (squash & stretch)")]
        [SerializeField] private float squashOnEnter = 0.7f;  // ecrasement a l'entree
        [SerializeField] private float stretchOnDone = 1.5f;  // etirement a la validation
        [SerializeField] private float idleBreath = 0.06f;    // amplitude de la respiration dedans

        public UnityEvent onCompleted;                       // branche la suite ici
        public UnityEvent onEntered;
        public UnityEvent onExited;

        public float Progress01 => holdTime <= 0f ? 1f : Mathf.Clamp01(held / holdTime);
        public bool Completed { get; private set; }

        // Le seuil se juge sur le CUMUL gagne, pas sur la charge courante de la jauge : sinon
        // une zone se refermerait pendant qu'on roule vers elle, la jauge se vidant en route.
        public int Score => ScoreGauge.Instance != null
            ? ScoreGauge.Instance.Earned
            : (tricks != null ? tricks.Total : 0);
        public int RequiredScore => requiredScore;

        // Verrou par SCORE. Une fois ouverte elle le reste : perdre des points ne doit pas
        // refermer une zone dans laquelle on est en train d'entrer.
        public bool ScoreLocked => requiredScore > 0 && !unlocked;
        // Verrou temporaire : on porte deja un colis, les autres points sont condamnes.
        public bool Busy { get; private set; }
        // Dans les deux cas : mur SOLIDE.
        public bool Locked => ScoreLocked || Busy;

        public void SetBusy(bool v)
        {
            if (Busy == v) return;
            Busy = v;
            ApplyLockCollider();
        }

        public bool PlayerInside
        {
            get
            {
                if (player == null || zone == null) return false;
                Vector3 p = player.position;
                return zone.ClosestPoint(p) == p;   // == sur Vector3 = comparaison approchee
            }
        }

        private static readonly int ProgressId = Shader.PropertyToID("_Progress");
        private static readonly int DoneId = Shader.PropertyToID("_Done");
        private static readonly int SquashId = Shader.PropertyToID("_Squash");
        private static readonly int LockedId = Shader.PropertyToID("_Locked");
        private static readonly int DenyId = Shader.PropertyToID("_Deny");
        private MaterialPropertyBlock mpb;

        private Transform player;
        private Collider zone;
        private float held;
        private bool wasInside;
        private float popT = -1f;   // anim de validation (-1 = pas jouee)
        private Camera cam;
        private float squash = 1f;  // -> _Squash du shader (deformation vertex, pas le collider)
        private Tween squashTween, breathTween;
        private TrickSystem tricks;
        private bool unlocked;
        private float lockAmount = 1f;   // -> _Locked (1 = mur, 0 = ouverte), anime a l'ouverture
        private float deny;              // -> _Deny, claque rouge quand on tape le mur
        private float denyCooldown;
        private float unlockBanner = -1f;
        private const float BannerTime = 1.6f;

        // Apparition : la zone jaillit du sol. Sert surtout aux zones activees en cours de
        // partie (la zone de livraison n'existe qu'une fois le paquet pris).
        private void OnEnable()
        {
            zone = GetComponent<Collider>();
            if (player == null)
            {
                var car = FindAnyObjectByType<ArcadeCarController>();
                if (car != null) player = car.transform;
            }
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();

            unlocked = requiredScore <= 0;
            lockAmount = unlocked ? 0f : 1f;
            ApplyLockCollider();
            if (Application.isPlaying) SquashImpact(1.7f, 1f, false);
        }

        private void OnDisable() => KillTweens();

        private void Reset()
        {
            var c = GetComponent<Collider>();
            if (c != null) c.isTrigger = true;
        }

        // Verrouillee, le collider n'est PLUS un trigger : c'est un mur, la moto rebondit.
        // MAIS jamais tant que le joueur est dedans : solidifier autour de lui l'enfermerait
        // ou l'ejecterait en le catapultant. On attend qu'il soit sorti — appele chaque frame
        // par TickLock, donc le mur se ferme des qu'il degage.
        private void ApplyLockCollider()
        {
            if (zone == null) return;
            zone.isTrigger = !(Locked && !PlayerInside);
        }

        private void TickLock()
        {
            float dt = Time.deltaTime;
            deny = Mathf.Max(0f, deny - dt * 3.2f);
            denyCooldown -= dt;
            // la banniere d'ouverture est une FENETRE, pas un drapeau : sans ca elle restait
            // vraie a vie et affichait "ACCES OUVERT" par-dessus un mur bien reel.
            if (unlockBanner >= 0f)
            {
                unlockBanner += dt;
                if (unlockBanner > BannerTime) unlockBanner = -1f;
            }

            // une seule rampe pour les deux causes de verrou (score ET colis en cours) :
            // le mur monte vite, il redescend plus doucement.
            lockAmount = Mathf.MoveTowards(lockAmount, Locked ? 1f : 0f, dt * (Locked ? 4f : 1.6f));
            ApplyLockCollider();   // le mur se ferme des que le joueur est sorti, pas avant
            if (!Locked) return;

            if (ScoreLocked && Score >= requiredScore) { Unlock(); return; }

            // refus : on tape le mur -> claque rouge + secousse + hitstop
            if (denyCooldown <= 0f && player != null && zone != null)
            {
                Vector3 p = player.position;
                if (Vector3.Distance(p, zone.ClosestPoint(p)) < denyRadius)
                {
                    denyCooldown = 0.55f;
                    deny = 1f;
                    SquashImpact(0.82f, 0.7f, false);
                    Hitstop.Punch(0.35f, 0.06f, 0.18f);
                }
            }
        }

        private void Unlock()
        {
            unlocked = true;
            unlockBanner = 0f;
            ApplyLockCollider();
            deny = 0f;
            SquashImpact(1.55f, 1.1f, false);          // la barriere saute
            Hitstop.Punch(0.3f, 0.09f, 0.35f);
        }

        private Renderer Visual => fill != null ? fill : GetComponent<Renderer>();

        // Tout passe par un MaterialPropertyBlock, jamais par le materiau : le materiau est
        // partage entre zones (elles s'ecraseraient), et .material creerait une instance par
        // zone. Le placage au sol n'est PAS ici : le shader le fait seul via le depth buffer.
        private void PushToMaterial()
        {
            var r = Visual;
            if (r == null || r.sharedMaterial == null) return;

            if (!r.sharedMaterial.HasFloat(ProgressId))   // materiau lambda : repli couleur
            {
                float pf = Progress01;
                var cf = Color.Lerp(new Color(0.25f, 0.6f, 1f), new Color(0.3f, 1f, 0.45f), pf);
                cf.a = Mathf.Lerp(0.18f, 0.42f, pf);
                if (Completed) cf.a *= 0.6f + 0.4f * Mathf.Sin(Time.time * 6f);
                r.material.color = cf;
                return;
            }

            mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(ProgressId, Progress01);
            mpb.SetFloat(DoneId, Completed ? 1f : 0f);
            mpb.SetFloat(SquashId, squash);
            mpb.SetFloat(LockedId, lockAmount);
            mpb.SetFloat(DenyId, deny);
            r.SetPropertyBlock(mpb);
        }

        private void Update()
        {
            TickLock();
            if (Locked) { PushToMaterial(); return; }   // mur : aucune progression possible

            bool now = PlayerInside;
            if (now != wasInside)
            {
                if (now) SquashImpact(squashOnEnter, 0.9f, true);
                else Settle();
                (now ? onEntered : onExited)?.Invoke();
                wasInside = now;
            }

            float dt = Time.deltaTime;
            if (Completed && once)
            {
                if (popT >= 0f) popT += dt;
            }
            else if (now)
            {
                held += dt;
                if (held >= holdTime)
                {
                    held = holdTime;
                    Completed = true;
                    popT = 0f;
                    SquashImpact(stretchOnDone, 1.2f, false);
                    onCompleted?.Invoke();
                }
            }
            else
            {
                held = resetOnExit ? 0f : Mathf.Max(0f, held - dt * drainSpeed);
                if (!once) Completed = false;
            }

            PushToMaterial();
        }

        // Impact cartoon : deformation seche puis retour elastique (overshoot), facon Overcooked.
        // Le squash part a la valeur voulue INSTANTANEMENT -> c'est le retour qui se joue.
        private void SquashImpact(float from, float dur, bool breatheAfter)
        {
            KillTweens();
            squash = from;
            squashTween = DOTween.To(() => squash, v => squash = v, 1f, dur)
                .SetEase(Ease.OutElastic).SetTarget(this)
                .OnComplete(() => { if (breatheAfter) Breathe(); });
        }

        // Respiration idle tant qu'on est dedans : jamais d'arret sec.
        private void Breathe()
        {
            if (!PlayerInside || Completed) return;
            breathTween = DOTween.To(() => squash, v => squash = v, 1f + idleBreath, 0.85f)
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetTarget(this);
        }

        // Sortie : retour au repos avec un petit depassement.
        private void Settle()
        {
            KillTweens();
            squashTween = DOTween.To(() => squash, v => squash = v, 1f, 0.45f)
                .SetEase(Ease.OutBack).SetTarget(this);
        }

        private void KillTweens()
        {
            squashTween?.Kill();
            breathTween?.Kill();
            squashTween = breathTween = null;
        }

        private void OnDestroy() => DOTween.Kill(this);

        private void OnGUI()
        {
            if (!Application.isPlaying) return;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            if (Locked || unlockBanner >= 0f) { DrawLockSign(); return; }

            if (Completed && (popT < 0f || popT > 1.4f)) return;  // valide : on affiche le flash puis plus rien
            if (!Completed && held <= 0.001f) return;
            Vector3 sp = cam.WorldToScreenPoint(transform.position + hudWorldOffset);
            if (sp.z < 0f) return;

            float x = sp.x, y = Screen.height - sp.y;
            float w = 220f, h = 22f;
            float p = Progress01;

            // pop elastique a la validation, sur une base a l'echelle du monde
            float s = WorldScale(transform.position + hudWorldOffset, w)
                    * (Completed ? 1f + 0.35f * Mathf.Exp(-popT * 6f) * Mathf.Cos(popT * 26f) : 1f);
            Matrix4x4 m0 = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(x, y));

            var r = new Rect(x - w * 0.5f, y - h * 0.5f, w, h);
            Box(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), new Color(0.98f, 0.98f, 1f, 0.95f));
            Box(r, new Color(0.12f, 0.13f, 0.22f, 0.95f));
            Box(new Rect(r.x, r.y, r.width * p, r.height),
                Color.Lerp(new Color(0.3f, 0.7f, 1f), new Color(0.35f, 1f, 0.5f), p));

            GUI.color = Color.white;
            var st = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            string txt = Completed ? "OK !" : $"{label}  {Mathf.CeilToInt(holdTime - held)}s";
            GUI.Label(new Rect(r.x, r.y - 24f, r.width, 22f), txt, st);

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // Panneau "ACCES REFUSE" ancre sur la zone : cadre rouge zebre facon barriere de
        // chantier, cadenas, score courant / requis, et bandeau vert a l'ouverture.
        private void DrawLockSign()
        {
            Vector3 sp = cam.WorldToScreenPoint(transform.position + hudWorldOffset);
            if (sp.z < 0f) return;

            // La banniere d'ouverture ne peut jamais s'afficher si la zone est fermee pour une
            // autre raison : le panneau dit l'etat REEL, pas le dernier evenement joue.
            bool opening = !Locked && unlockBanner >= 0f;
            float x = sp.x, y = Screen.height - sp.y;
            float w = 260f, h = 76f;

            float world = WorldScale(transform.position + hudWorldOffset, w);

            // secousse au refus (en pixels ecran, donc mise a l'echelle elle aussi)
            float shake = deny * 9f * world;
            x += Mathf.Sin(Time.unscaledTime * 62f) * shake;
            y += Mathf.Cos(Time.unscaledTime * 71f) * shake * 0.6f;
            float s = world * (opening
                ? 1f + 0.45f * Mathf.Exp(-unlockBanner * 5f) * Mathf.Cos(unlockBanner * 22f)
                : 1f + 0.12f * deny);

            Matrix4x4 m0 = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(x, y));

            var r = new Rect(x - w * 0.5f, y - h * 0.5f, w, h);
            // verrou de score -> rouge (durable) ; simple colis en main -> ambre (temporaire).
            Color baseAccent = !ScoreLocked && Busy
                ? new Color(1f, 0.7f, 0.15f)
                : new Color(1f, 0.25f, 0.2f);
            Color accent = opening ? new Color(0.35f, 1f, 0.5f) : Color.Lerp(baseAccent, Color.white, deny);

            Box(new Rect(r.x - 5f, r.y - 5f, r.width + 10f, r.height + 10f), new Color(accent.r, accent.g, accent.b, 0.35f + 0.5f * deny));
            Box(r, new Color(0.06f, 0.05f, 0.10f, 0.94f));

            // zebrures de chantier en bandeau haut et bas
            for (int i = 0; i < 22; i++)
            {
                float bw = w / 22f;
                if ((i + Mathf.FloorToInt(Time.unscaledTime * 6f)) % 2 != 0) continue;
                Box(new Rect(r.x + i * bw, r.y, bw, 5f), accent);
                Box(new Rect(r.x + i * bw, r.yMax - 5f, bw, 5f), accent);
            }

            // wordWrap off : un titre trop long doit deborder, pas passer a la ligne et se
            // superposer au sous-titre.
            var st = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };

            // PRIORITE AU VERROU DE SCORE, meme colis en main : c'est le blocage DURABLE.
            // Le joueur reviendra forcement sans colis ; autant qu'il sache tout de suite
            // qu'il lui manque des points, plutot que de faire l'aller-retour pour rien.
            if (!ScoreLocked && Busy)
            {
                GUI.color = accent;
                st.fontSize = 17;
                GUI.Label(new Rect(r.x, r.y + 12f, r.width, 24f), "/// LIVRAISON EN COURS", st);
                st.fontSize = 12;
                GUI.color = new Color(1f, 0.92f, 0.75f);
                GUI.Label(new Rect(r.x, r.y + 40f, r.width, 22f), "DEPOSE TON COLIS D'ABORD", st);
            }
            else if (opening)
            {
                GUI.color = accent;
                st.fontSize = 24;
                GUI.Label(new Rect(r.x, r.y + 10f, r.width, 30f), "ACCES OUVERT", st);
                st.fontSize = 13;
                GUI.color = new Color(0.8f, 1f, 0.85f);
                GUI.Label(new Rect(r.x, r.y + 42f, r.width, 20f), requiredScore.ToString("N0") + " PTS ATTEINTS", st);
            }
            else
            {
                GUI.color = accent;
                st.fontSize = 22;
                GUI.Label(new Rect(r.x, r.y + 8f, r.width, 28f), "/// ACCES REFUSE", st);

                // jauge score courant / requis
                var bar = new Rect(r.x + 16f, r.y + 42f, r.width - 32f, 14f);
                Box(bar, new Color(0.15f, 0.13f, 0.2f, 1f));
                float p01 = requiredScore > 0 ? Mathf.Clamp01(Score / (float)requiredScore) : 1f;
                Box(new Rect(bar.x, bar.y, bar.width * p01, bar.height),
                    Color.Lerp(new Color(1f, 0.35f, 0.2f), new Color(1f, 0.9f, 0.3f), p01));

                GUI.color = Color.white;
                st.fontSize = 12;
                GUI.Label(bar, Score.ToString("N0") + " / " + requiredScore.ToString("N0") + " PTS", st);
            }

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // Facteur d'echelle pour qu'un panneau dessine 'designWidth' pixels occupe reellement
        // hudWorldWidth METRES dans le monde : petit de loin, gros de pres, exactement comme un
        // panneau pose dans la scene. C'est de la projection perspective, pas un lerp de
        // distance -> ca reste juste quel que soit le FOV ou la resolution.
        private float WorldScale(Vector3 anchor, float designWidth)
        {
            float dist = Vector3.Distance(cam.transform.position, anchor);
            if (dist < 0.01f) return hudScaleClamp.y;
            float pxPerMeter = Screen.height / (2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            return Mathf.Clamp(hudWorldWidth * pxPerMeter / designWidth, hudScaleClamp.x, hudScaleClamp.y);
        }

        private static void Box(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        private void OnDrawGizmos()
        {
            var c = GetComponent<Collider>();
            if (c == null) return;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireCube(c.bounds.center, c.bounds.size);
        }
    }
}
