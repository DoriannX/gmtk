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

        [Header("Juice (squash & stretch)")]
        [SerializeField] private float squashOnEnter = 0.7f;  // ecrasement a l'entree
        [SerializeField] private float stretchOnDone = 1.5f;  // etirement a la validation
        [SerializeField] private float idleBreath = 0.06f;    // amplitude de la respiration dedans

        public UnityEvent onCompleted;                       // branche la suite ici
        public UnityEvent onEntered;
        public UnityEvent onExited;

        public float Progress01 => holdTime <= 0f ? 1f : Mathf.Clamp01(held / holdTime);
        public bool Completed { get; private set; }

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
        private MaterialPropertyBlock mpb;

        private Transform player;
        private Collider zone;
        private float held;
        private bool wasInside;
        private float popT = -1f;   // anim de validation (-1 = pas jouee)
        private Camera cam;
        private float squash = 1f;  // -> _Squash du shader (deformation vertex, pas le collider)
        private Tween squashTween, breathTween;

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
            if (Application.isPlaying) SquashImpact(1.7f, 1f, false);
        }

        private void OnDisable() => KillTweens();

        private void Reset()
        {
            var c = GetComponent<Collider>();
            if (c != null) c.isTrigger = true;
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
            r.SetPropertyBlock(mpb);
        }

        private void Update()
        {
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
            if (Completed && (popT < 0f || popT > 1.4f)) return;  // valide : on affiche le flash puis plus rien
            if (!Completed && held <= 0.001f) return;

            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(transform.position + hudWorldOffset);
            if (sp.z < 0f) return;

            float x = sp.x, y = Screen.height - sp.y;
            float w = 220f, h = 22f;
            float p = Progress01;

            // pop elastique a la validation
            float s = Completed ? 1f + 0.35f * Mathf.Exp(-popT * 6f) * Mathf.Cos(popT * 26f) : 1f;
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
