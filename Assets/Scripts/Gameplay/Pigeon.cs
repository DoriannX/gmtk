using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Pigeon au sol : picore + sautille (hops facon Overcooked), et s'envole
    // en panique quand une menace (transform assignable, sinon tag "Player")
    // approche. Puis se repose plus loin. Tout en DOTween exagere : squash a
    // l'atterrissage, punch de sursaut, battement d'ailes ample.
    // Parts attendues (auto-trouvees par nom, degrade proprement si absentes) :
    //   "Head" (picore), "WingL"/"WingR" (pivots epaule, battent autour de Z).
    [DisallowMultipleComponent]
    public class Pigeon : MonoBehaviour
    {
        [Header("Menaces")]
        [Tooltip("Menace en plus (optionnelle) a assigner a la main.")]
        [SerializeField] private Transform extraThreat;
        [Tooltip("Objet tague ainsi = menace (ex: la voiture).")]
        [SerializeField] private string threatTag = "Player";
        [Tooltip("Les pietons (component Pedestrian) font aussi fuir.")]
        [SerializeField] private bool fleeFromPedestrians = true;
        [SerializeField] private float fleeRadius = 6f;    // rayon de declenchement de la fuite

        [Header("Sol : picore + sautille")]
        [SerializeField] private Vector2 groundActionDelay = new Vector2(0.6f, 1.8f);
        [SerializeField] private float hopDistance = 0.6f;
        [SerializeField] private float hopHeight = 0.35f;
        [SerializeField] private float wanderRadius = 5f;

        [Header("Fuite / vol")]
        [SerializeField] private float flyDistance = 14f;  // saut de fuite
        [SerializeField] private float flyHeight = 6f;     // hauteur de l'arc
        [SerializeField] private float flapAngle = 55f;    // amplitude d'aile en vol
        [SerializeField] private float idleWingAngle = 8f; // ailes repliees au sol

        [Header("Fuite : anticipation")]
        [Tooltip("Temps accroupi au sol avant de decoller. Fenetre ou une voiture " +
                 "boostee (rapide) peut percuter le pigeon avant qu'il s'envole. " +
                 "Plus court = plus dur a toucher.")]
        [SerializeField] private float startleDelay = 0.22f;

        private enum State { Grounded, Flying }
        private State state = State.Grounded;

        private Transform pivot;         // porte squash/anims, comme Pedestrian
        private Transform head, wingL, wingR;
        private Quaternion headBaseRot, wingLBase, wingRBase;
        private Vector3 headBasePos;
        private Vector3 spawnPos;
        private float actionTimer;
        private Tween flapL, flapR, moveTween;
        private readonly System.Collections.Generic.List<Tween> anims = new();
        private readonly System.Collections.Generic.List<Transform> threats = new();
        private Transform fleeFrom;   // menace memorisee entre l'accroupissement et le decollage
        private bool dead;

        private void Awake()
        {
            // pivot intermediaire (n'ecrase pas la rotation d'import du futur FBX)
            pivot = new GameObject("Pivot").transform;
            pivot.SetParent(transform, false);
            var kids = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in transform) if (c != pivot) kids.Add(c);
            foreach (var c in kids) c.SetParent(pivot, true);

            head = FindDeep(pivot, "Head");
            wingL = FindDeep(pivot, "WingL");
            wingR = FindDeep(pivot, "WingR");
            if (head != null) { headBasePos = head.localPosition; headBaseRot = head.localRotation; }
            if (wingL != null) wingLBase = wingL.localRotation;
            if (wingR != null) wingRBase = wingR.localRotation;
            spawnPos = transform.position;

            GatherThreats();
        }

        // Menaces = extra assignee + objet tague + tous les pietons.
        // On garde les transforms (les pietons bougent) ; null tolere (roadkill).
        private void GatherThreats()
        {
            threats.Clear();
            if (extraThreat != null) threats.Add(extraThreat);
            if (!string.IsNullOrEmpty(threatTag))
            {
                var go = GameObject.FindGameObjectWithTag(threatTag);
                if (go != null) threats.Add(go.transform);
            }
            if (fleeFromPedestrians)
                foreach (var p in FindObjectsByType<Pedestrian>(FindObjectsSortMode.None))
                    threats.Add(p.transform);
        }

        private void Start()
        {
            FoldWings();
            ScheduleGroundAction();
        }

        private void Update()
        {
            if (dead) return;
            if (state == State.Grounded)
            {
                var th = NearestThreat();
                if (th != null) { Flee(th); return; }
                actionTimer -= Time.deltaTime;
                if (actionTimer <= 0f) GroundAction();
            }
        }

        // Menace la plus proche dans fleeRadius (plan XZ), sinon null.
        private Transform NearestThreat()
        {
            Transform best = null;
            float bestSq = fleeRadius * fleeRadius;
            for (int i = 0; i < threats.Count; i++)
            {
                var t = threats[i];
                if (t == null) continue;
                Vector3 d = t.position - transform.position; d.y = 0f;
                float sq = d.sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; best = t; }
            }
            return best;
        }

        // ---- Sol ----

        private void ScheduleGroundAction() =>
            actionTimer = Random.Range(groundActionDelay.x, groundActionDelay.y);

        private void GroundAction()
        {
            // ~40% picore, sinon sautille
            if (Random.value < 0.4f) Peck();
            else Hop();
            ScheduleGroundAction();
        }

        private void Peck()
        {
            if (head == null) { BodyBob(); return; }
            var seq = DOTween.Sequence();
            // plonge le bec, corps qui s'ecrase, remonte avec overshoot
            seq.Append(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(55f, 0, 0), 0.12f).SetEase(Ease.OutQuad));
            seq.Join(pivot.DOScale(new Vector3(1.12f, 0.82f, 1.12f), 0.12f).SetEase(Ease.OutQuad));
            seq.Append(head.DOLocalRotateQuaternion(headBaseRot, 0.18f).SetEase(Ease.OutBack));
            seq.Join(pivot.DOScale(Vector3.one, 0.2f).SetEase(Ease.OutBack));
            anims.Add(seq);
        }

        private void BodyBob() =>
            anims.Add(pivot.DOScale(new Vector3(1.08f, 0.9f, 1.08f), 0.12f).SetLoops(2, LoopType.Yoyo).SetEase(Ease.OutQuad));

        private void Hop()
        {
            // sautille vers un point proche, biaise vers le spawn si trop loin
            Vector3 fromSpawn = transform.position - spawnPos; fromSpawn.y = 0f;
            Vector3 dir = fromSpawn.magnitude > wanderRadius
                ? -fromSpawn.normalized
                : Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
            dir = (Quaternion.Euler(0, Random.Range(-35f, 35f), 0) * dir).normalized;
            Vector3 target = transform.position + dir * hopDistance;

            transform.DORotateQuaternion(Quaternion.LookRotation(dir), 0.15f);
            moveTween = transform.DOJump(target, hopHeight, 1, 0.35f).SetEase(Ease.OutQuad);
            // squash au decollage puis a la reception
            anims.Add(pivot.DOScale(new Vector3(0.85f, 1.2f, 0.85f), 0.12f).SetEase(Ease.OutQuad)
                .OnComplete(() => pivot.DOScale(Vector3.one, 0.23f).SetEase(Ease.OutBounce)));
        }

        // ---- Fuite / vol ----

        private void Flee(Transform from)
        {
            state = State.Flying;   // bloque re-declenchement, mais RESTE au sol le temps du sursaut
            KillAll();
            fleeFrom = from;

            // sursaut : s'accroupit sur place (toujours au sol -> percutable) puis
            // decolle apres startleDelay. Fenetre courte = seul le boost le rattrape.
            anims.Add(pivot.DOScale(new Vector3(1.25f, 0.68f, 1.25f), 0.1f).SetEase(Ease.OutQuad));
            StartFlap();
            anims.Add(DOVirtual.DelayedCall(startleDelay, Launch)); // dans anims -> tue par Explode
        }

        // Decollage effectif (apres le sursaut). Le pigeon n'a plus ete percute.
        private void Launch()
        {
            if (dead || state != State.Flying) return;

            Vector3 away = transform.position - (fleeFrom != null ? fleeFrom.position : transform.position - transform.forward);
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Random.insideUnitSphere;
            away = (Quaternion.Euler(0, Random.Range(-40f, 40f), 0) * away.normalized);
            Vector3 target = transform.position + away * flyDistance;
            target.y = spawnPos.y;

            anims.Add(pivot.DOScale(Vector3.one, 0.2f).SetEase(Ease.OutBack)); // detend l'accroupi
            transform.DORotateQuaternion(Quaternion.LookRotation(away), 0.25f).SetEase(Ease.OutQuad);
            // grand arc d'envol
            moveTween = transform.DOJump(target, flyHeight, 1, 1.5f).SetEase(Ease.InOutSine)
                .OnComplete(Land);
        }

        private void Land()
        {
            state = State.Grounded;
            StopFlap();
            FoldWings();
            spawnPos = transform.position; // nouveau point d'ancrage
            // ecrasement d'atterrissage + rebond
            anims.Add(pivot.DOScale(new Vector3(1.25f, 0.7f, 1.25f), 0.1f).SetEase(Ease.OutQuad)
                .OnComplete(() => pivot.DOScale(Vector3.one, 0.35f).SetEase(Ease.OutBounce)));
            // si une menace est deja dans le rayon, re-decolle apres un souffle
            if (NearestThreat() != null)
                DOVirtual.DelayedCall(0.25f, () =>
                {
                    if (state != State.Grounded) return;
                    var th = NearestThreat();
                    if (th != null) Flee(th);
                });
            else
                ScheduleGroundAction();
        }

        // ---- Ailes ----

        private void StartFlap()
        {
            StopFlap();
            // battements = deux tweens Yoyo infinis independants (pas dans une
            // Sequence : DOTween interdit les loops infinis imbriques)
            if (wingL != null)
                flapL = wingL.DOLocalRotate(wingLBase.eulerAngles + new Vector3(0, 0, flapAngle), 0.14f)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
            if (wingR != null)
                flapR = wingR.DOLocalRotate(wingRBase.eulerAngles - new Vector3(0, 0, flapAngle), 0.14f)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
        }

        private void StopFlap() { flapL?.Kill(); flapR?.Kill(); flapL = flapR = null; }

        private void FoldWings()
        {
            if (wingL != null) wingL.DOLocalRotate(wingLBase.eulerAngles + new Vector3(0, 0, idleWingAngle), 0.25f).SetEase(Ease.OutBack);
            if (wingR != null) wingR.DOLocalRotate(wingRBase.eulerAngles - new Vector3(0, 0, idleWingAngle), 0.25f).SetEase(Ease.OutBack);
        }

        private void KillAll()
        {
            foreach (var t in anims) t.Kill();
            anims.Clear();
            moveTween?.Kill();
        }

        private static Transform FindDeep(Transform root, string n)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == n) return t;
            return null;
        }

        // ---- Roadkill : voiture rapide au contact -> explosion (comme Pedestrian) ----

        private void OnTriggerEnter(Collider other)
        {
            if (dead) return;
            var car = other.GetComponentInParent<ArcadeCarController>();
            if (car == null) return;
            // uniquement en boost (pleine vitesse) : le sursaut le rend juste
            // atteignable a cette vitesse, et le boost seul l'explose.
            if (car.IsBoosting)
                Explode(car.transform.forward);
        }

        private void Explode(Vector3 hitDir)
        {
            if (dead) return;
            dead = true;
            StopFlap();
            KillAll();
            Hitstop.Punch();                                        // slow-mo d'impact
            BloodFx.Spawn(transform.position + Vector3.up * 0.3f, hitDir);
            Creatures.ReportKill(transform.position, true);          // temoins paniquent + popularite
                                                                    // (seul le joueur peut l'exploser, cf OnTriggerEnter)
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            StopFlap();
            KillAll();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, fleeRadius);
        }
    }
}
