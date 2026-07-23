using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Pieton lambda : erre dans la rue (marche -> pause -> nouvelle direction).
    // Anims 100% DOTween sur les pieces du modele Rayman-style :
    // idle = respiration + tete qui flotte, marche = dandine + bras qui pompent.
    // Un pivot intermediaire est cree au runtime pour ne pas ecraser la
    // rotation d'import du FBX avec les tweens.
    public class Pedestrian : MonoBehaviour
    {
        [Header("Errance")]
        [SerializeField] private float walkSpeed = 1.4f;
        [SerializeField] private Vector2 walkDuration = new Vector2(2f, 5f);
        [SerializeField] private Vector2 idleDuration = new Vector2(1f, 3f);
        [SerializeField] private float wanderRadius = 12f; // rayon max autour du spawn

        [Header("Anim")]
        [SerializeField] private float waddleAngle = 7f;   // roulis du corps en marche
        [SerializeField] private float armSwing = 28f;     // balancier des bras
        [SerializeField] private float headBob = 0.025f;   // flottement de la tete (Rayman)

        [Header("Roadkill (reaction au camion)")]
        [SerializeField] private float pissedSpeed = 12f;    // au-dela : projete + insulte
        [SerializeField] private float goreSpeed = 20f;      // au-dela (ou boost) : explose
        [SerializeField] private float disturbDistance = 1.3f; // recul choc leger
        [SerializeField] private float pissedDistance = 4.5f;  // vol plane furieux

        private Transform pivot;    // porte les tweens corps entier
        private Transform head, handL, handR, stacheL, stacheR;
        private Vector3 headBasePos, handLBasePos, handRBasePos;
        private Quaternion headBaseRot, handLBaseRot, handRBaseRot;
        private Quaternion stacheLBaseRot, stacheRBaseRot;
        private Vector3 stacheLBaseScale, stacheRBaseScale;
        private Vector3 spawnPos;
        // Le FBX est Z-up : la rotation d'import (ex. -90 X) le met debout. On la
        // capture pour la RECONSERVER a chaque cap, sinon LookRotation (Y-up) le
        // couche dans le sol. Cap final = LookRotation(dir) * baseTilt.
        private Quaternion baseTilt;

        private bool walking;
        private float stateTimer;
        private Vector3 walkDir = Vector3.forward;
        private readonly System.Collections.Generic.List<Tween> anims = new();

        // controle externe (PedestrianChatter) : approche + attente scriptees
        private enum Mode { Wander, WalkTo, Wait, Stunned, Flee, Driving }
        private Mode mode = Mode.Wander;
        private Vector3 walkToTarget;
        private float walkToStopDist;
        private System.Action onArrived;
        private Sequence stacheSeq;

        // Roadkill : bulle optionnelle pour rouspeter, flag de mort, tween de projection
        private SpeechBubble bubble;
        private bool dead;
        private Tween stunTween;
        private static readonly string[] annoyedLines = { "He oh !", "Ca va pas ?!", "Non mais !", "Oh !", "Doucement !" };
        private static readonly string[] pissedLines = { "CONNARD !", "T'ES MALADE ?!", "ENFOIRE !", "JE VAIS TE—", "SALE FOU !" };

        // Temoin d'horreur : voit une creature se faire tuer devant lui -> panique
        [Header("Temoin d'horreur")]
        [SerializeField] private float horrorRadius = 12f;       // distance max pour reagir
        [SerializeField, Range(-1f, 1f)] private float horrorFrontDot = 0.1f; // doit etre devant (l'a vu)
        [SerializeField] private float fleeRunSpeed = 2.8f;      // court doucement = goofy
        [SerializeField] private Vector2 fleeDuration = new Vector2(3f, 5f);
        private Vector3 fleeDir;
        private float fleeTimer;
        private static readonly string[] horrifiedLines =
            { "OH MON DIEU !", "AU SECOURS !", "UN MONSTRE !", "C'EST HORRIBLE !",
              "IL EST FOU !", "AAAAH !", "PITIE NON !", "APPELEZ LA POLICE !" };
        public bool IsPanicking => mode == Mode.Flee;
        public bool IsDriving => mode == Mode.Driving;

        // Conduite : le pieton va parfois vers une voiture vide, monte, roule un
        // moment puis ressort. Voiture reste garee tant que personne dedans.
        [Header("Conduite (monte dans une voiture)")]
        [SerializeField] private float driveChance = 0.25f;    // proba de chercher une caisse a chaque idle
        [SerializeField] private float carSearchRadius = 14f;  // portee de reperage d'une voiture vide
        [SerializeField] private Vector2 driveDuration = new Vector2(6f, 14f);
        private AICarController drivingCar;
        private Collider selfCol;

        private void Awake()
        {
            baseTilt = transform.rotation; // pose debout d'import (tilt seul, sans cap)

            // insere un pivot entre le wrapper et le FBX
            pivot = new GameObject("Pivot").transform;
            pivot.SetParent(transform, false);
            var children = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in transform)
                if (c != pivot) children.Add(c);
            foreach (var c in children)
                c.SetParent(pivot, true);

            head = FindDeep(pivot, "PedHead");
            handL = FindDeep(pivot, "PedHandL");
            handR = FindDeep(pivot, "PedHandR");
            if (head != null) { headBasePos = head.localPosition; headBaseRot = head.localRotation; }
            if (handL != null) { handLBaseRot = handL.localRotation; handLBasePos = handL.localPosition; }
            if (handR != null) { handRBaseRot = handR.localRotation; handRBasePos = handR.localPosition; }
            stacheL = FindDeep(pivot, "PedStacheL");
            stacheR = FindDeep(pivot, "PedStacheR");
            if (stacheL != null) { stacheLBaseRot = stacheL.localRotation; stacheLBaseScale = stacheL.localScale; }
            if (stacheR != null) { stacheRBaseRot = stacheR.localRotation; stacheRBaseScale = stacheR.localScale; }
            spawnPos = transform.position;
            bubble = GetComponent<SpeechBubble>();
            selfCol = GetComponent<Collider>();
        }

        private void Start() => EnterIdle();

        private void OnEnable() => Creatures.Killed += OnCreatureKilled;
        private void OnDisable() => Creatures.Killed -= OnCreatureKilled;

        private void Update()
        {
            if (mode == Mode.Stunned) return; // projete par le camion, le tween pilote
            if (mode == Mode.Driving) return; // au volant : cache, c'est la voiture qui roule
            if (mode == Mode.Flee)
            {
                fleeTimer -= Time.deltaTime;
                Vector3 fstep = fleeDir * (fleeRunSpeed * Time.deltaTime);
                fstep.y = 0f;
                transform.position += fstep;
                if (fleeTimer <= 0f) { mode = Mode.Wander; EnterIdle(); }
                return;
            }
            if (mode == Mode.WalkTo)
            {
                Vector3 to = walkToTarget - transform.position;
                to.y = 0f;
                if (to.magnitude <= walkToStopDist)
                {
                    EnterIdle();
                    mode = Mode.Wait;
                    var cb = onArrived; onArrived = null;
                    cb?.Invoke();
                    return;
                }
                walkDir = to.normalized;
                transform.position += walkDir * (walkSpeed * Time.deltaTime);
                return;
            }
            if (mode == Mode.Wait) return;

            stateTimer -= Time.deltaTime;
            if (walking)
            {
                Vector3 step = walkDir * (walkSpeed * Time.deltaTime);
                step.y = 0f;
                transform.position += step;
                if (stateTimer <= 0f) EnterIdle();
            }
            else if (stateTimer <= 0f)
            {
                if (TrySeekCar()) return;
                EnterWalk();
            }
        }

        // ---- Conduite : va vers une voiture vide, monte, roule, ressort ----

        private bool TrySeekCar()
        {
            if (Random.value > driveChance) return false;
            var car = AICarController.FindEmptyNear(transform.position, carSearchRadius);
            if (car == null) return false;
            WalkTo(car.transform.position, 1.6f, () => EnterCar(car));
            return true;
        }

        private void EnterCar(AICarController car)
        {
            if (car == null || !car.TryEnter(this)) { ResumeWander(); return; } // prise entre-temps
            drivingCar = car;
            mode = Mode.Driving;
            KillAnims();
            SetVisible(false);                 // le pieton disparait dans la caisse
            transform.position = car.SeatPoint;
            CancelInvoke(nameof(ExitCar));
            Invoke(nameof(ExitCar), Random.Range(driveDuration.x, driveDuration.y));
        }

        private void ExitCar()
        {
            CancelInvoke(nameof(ExitCar));
            if (drivingCar != null)
            {
                transform.position = drivingCar.ExitPoint;
                drivingCar.ClearDriver();
                drivingCar = null;
            }
            SetVisible(true);
            ResumeWander();
        }

        // Le camion explose sous lui : ejection violente (mais il survit, culbute).
        public void EjectFromCar(Vector3 pos, Vector3 dir)
        {
            CancelInvoke(nameof(ExitCar));
            drivingCar = null;                 // deja detruite par l'explosion
            SetVisible(true);
            transform.position = new Vector3(pos.x, spawnPos.y, pos.z); // au sol : DOJump garde le y de depart
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = -transform.forward;
            dir.Normalize();
            Knockback(dir, pissedDistance * 1.3f, 3.2f, pissedLines, tumble: true);
        }

        private void SetVisible(bool on)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = on;
            if (selfCol != null) selfCol.enabled = on;
        }

        // ---- API pour PedestrianChatter ----

        // Marche vers un point puis passe en attente (callback a l'arrivee)
        public void WalkTo(Vector3 target, float stopDist, System.Action arrived)
        {
            mode = Mode.WalkTo;
            walkToTarget = target;
            walkToStopDist = stopDist;
            onArrived = arrived;
            walking = true;
            Vector3 dir = target - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
            {
                walkDir = dir.normalized;
                transform.DORotateQuaternion(Facing(walkDir), 0.35f).SetEase(Ease.OutQuad);
            }
            ResetPoses();
            StartWalkAnims();
        }

        // S'arrete sur place et attend (idle anime, plus d'errance)
        public void StandAndWait()
        {
            EnterIdle();
            mode = Mode.Wait;
        }

        public void FaceTowards(Vector3 pos)
        {
            Vector3 dir = pos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.DORotateQuaternion(Facing(dir.normalized), 0.3f).SetEase(Ease.OutQuad);
        }

        public void ResumeWander()
        {
            mode = Mode.Wander;
            EnterIdle();
        }

        // ---- Meme "six seven" : se fige et fait le geste de la balance ----
        // (avant-bras leves paumes au ciel, les deux mains qui montent/descendent
        // en opposition — comme peser deux trucs). Moustache en furie pendant.
        public void SixSeven(float duration)
        {
            if (dead || mode == Mode.Stunned || mode == Mode.Flee || mode == Mode.Driving) return;
            mode = Mode.Wait;
            KillAnims();
            ResetPoses();
            StartSixSevenAnims();
            MustacheFrenzy(duration);
            CancelInvoke(nameof(EndSixSeven));
            Invoke(nameof(EndSixSeven), duration);
        }

        // Geste "6-7" : la balance. Axes du modele (mesure runtime) :
        //   local +X = cote, local -Y = AVANT, local +Z = UP monde.
        // La rotation ne pilote PLUS la hauteur (pivot main = aux pieds -> gros
        // arc qui plonge dans le sol). Hauteur/seesaw = POSITION locale +Z (up
        // monde, borne -> jamais sous le sol si sixLift > sixSeesaw). La rotation
        // X ne fait que basculer l'avant-bras vers l'avant, paume au ciel.
        [Header("Six seven (geste)")]
        [SerializeField] private float sixTilt = 40f;     // bascule avant-bras vers l'avant (deg, X local)
        [SerializeField] private float sixLift = 0.30f;   // hauteur de base des mains (m, +Z = up monde)
        [SerializeField] private float sixSeesaw = 0.12f; // amplitude balance verticale (m, < sixLift !)
        [SerializeField] private float sixFwd = 0.28f;    // avance des mains devant le perso (m, -Y = avant)
        [SerializeField] private float sixInward = 0.25f; // rapproche les mains du centre (m, X ; sinon trop sur les cotes)
        [SerializeField] private float sixStep = 0.13f;   // cadence : court = agressif

        private void StartSixSevenAnims()
        {
            float step = sixStep;
            // corps + tete qui claquent au rythme (rapide = agressif)
            anims.Add(pivot.DOLocalRotate(new Vector3(0, 0, 6f), step)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (head != null)
                anims.Add(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 12f), step)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));

            // base = avancee (-Y) + rapprochement du centre (X, oppose L/R) + montee (+Z)
            // seesaw sur Z en opposition. L est a +X -> rentre en -X, R inverse.
            Vector3 fwd = new Vector3(0, -sixFwd, 0);
            Vector3 inL = new Vector3(-sixInward, 0, 0);
            Vector3 inR = new Vector3(sixInward, 0, 0);
            Vector3 low = new Vector3(0, 0, sixLift - sixSeesaw);
            Vector3 high = new Vector3(0, 0, sixLift + sixSeesaw);
            if (handL != null)
            {
                handL.localEulerAngles = handLBaseRot.eulerAngles + new Vector3(sixTilt, 0, 0);
                handL.localPosition = handLBasePos + fwd + inL + low; // demarre bas
                anims.Add(handL.DOLocalMove(handLBasePos + fwd + inL + high, step)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            }
            if (handR != null)
            {
                handR.localEulerAngles = handRBaseRot.eulerAngles + new Vector3(sixTilt, 0, 0);
                handR.localPosition = handRBasePos + fwd + inR + high; // demarre haut -> opposition
                anims.Add(handR.DOLocalMove(handRBasePos + fwd + inR + low, step)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            }
        }

        private void EndSixSeven()
        {
            CancelInvoke(nameof(EndSixSeven));
            if (mode == Mode.Wait) ResumeWander(); // sinon knock/panic a repris la main
        }

        // ---- Temoin d'horreur : une creature meurt devant lui -> il panique ----

        private void OnCreatureKilled(Vector3 deathPos)
        {
            if (dead || mode == Mode.Stunned || mode == Mode.Flee || mode == Mode.Driving) return;
            Vector3 to = deathPos - transform.position; to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.1f || dist > horrorRadius) return;                 // soi-meme, ou trop loin
            if (Vector3.Dot(transform.forward, to / dist) < horrorFrontDot) return; // pas devant -> pas vu
            Panic(deathPos);
        }

        private void Panic(Vector3 deathPos)
        {
            KillAnims();
            stunTween?.Kill();
            mode = Mode.Flee;
            onArrived = null;

            // fuit a l'oppose du carnage
            fleeDir = transform.position - deathPos; fleeDir.y = 0f;
            if (fleeDir.sqrMagnitude < 0.01f) fleeDir = -transform.forward;
            fleeDir.Normalize();
            fleeTimer = Random.Range(fleeDuration.x, fleeDuration.y);

            transform.DORotateQuaternion(Facing(fleeDir), 0.25f).SetEase(Ease.OutBack);
            pivot.DOPunchScale(new Vector3(0.25f, 0.4f, 0.25f), 0.35f, 8, 0.7f); // sursaut d'horreur

            ResetPoses();
            StartPanicAnims();

            if (bubble != null)
                bubble.Show(horrifiedLines[Random.Range(0, horrifiedLines.Length)], isThought: false, 2f);
            MustacheFrenzy(2.5f);
        }

        // Course goofy : dandine enorme, rebond, bras leves au ciel qui battent.
        // Volontairement lent (fleeRunSpeed) -> decalage comique avec l'agitation.
        private void StartPanicAnims()
        {
            const float step = 0.13f; // cadence frenetique
            anims.Add(pivot.DOLocalRotate(new Vector3(0, 0, waddleAngle * 2.4f), step)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));      // dandine exagere
            anims.Add(pivot.DOLocalMoveY(0.09f, step * 0.5f)
                .SetDelay(BlendTime).SetEase(Ease.OutQuad).SetLoops(-1, LoopType.Yoyo));         // rebond panique
            if (head != null)
                anims.Add(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 16f), step * 1.1f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));   // tete secouee
            // bras leves qui battent (bas <-> haut)
            if (handL != null)
                anims.Add(handL.DOLocalRotate(handLBaseRot.eulerAngles + new Vector3(-145f, 0, 30f), step)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (handR != null)
                anims.Add(handR.DOLocalRotate(handRBaseRot.eulerAngles + new Vector3(-145f, 0, -30f), step * 1.15f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));   // dephase
        }

        // Moustache en furie pendant la parole : bounce absurde, brins en
        // opposition, retour propre a la fin.
        public void MustacheFrenzy(float duration)
        {
            if (stacheL == null && stacheR == null) return;
            StopMustache();
            // jiggle leger et rapide : petit rebond scale + micro-rotation,
            // brins dephases — ca fretille, ca ne part pas en vrille
            stacheSeq = DOTween.Sequence();
            if (stacheL != null)
            {
                stacheSeq.Join(stacheL.DOLocalRotate(stacheLBaseRot.eulerAngles + new Vector3(0, 0, 8f), 0.09f)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
                stacheSeq.Join(stacheL.DOScale(stacheLBaseScale * 1.12f, 0.11f)
                    .SetEase(Ease.OutQuad).SetLoops(-1, LoopType.Yoyo));
            }
            if (stacheR != null)
            {
                stacheSeq.Join(stacheR.DOLocalRotate(stacheRBaseRot.eulerAngles - new Vector3(0, 0, 8f), 0.09f)
                    .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(0.045f)); // dephase
                stacheSeq.Join(stacheR.DOScale(stacheRBaseScale * 1.12f, 0.11f)
                    .SetEase(Ease.OutQuad).SetLoops(-1, LoopType.Yoyo));
            }
            Invoke(nameof(StopMustache), duration);
        }

        private void StopMustache()
        {
            CancelInvoke(nameof(StopMustache));
            stacheSeq?.Kill();
            stacheSeq = null;
            if (stacheL != null)
            {
                stacheL.DOLocalRotateQuaternion(stacheLBaseRot, 0.25f).SetEase(Ease.OutBack);
                stacheL.DOScale(stacheLBaseScale, 0.25f).SetEase(Ease.OutBack);
            }
            if (stacheR != null)
            {
                stacheR.DOLocalRotateQuaternion(stacheRBaseRot, 0.25f).SetEase(Ease.OutBack);
                stacheR.DOScale(stacheRBaseScale, 0.25f).SetEase(Ease.OutBack);
            }
        }

        private void EnterIdle()
        {
            walking = false;
            stateTimer = Random.Range(idleDuration.x, idleDuration.y);
            ResetPoses();

            // Idle continu : respiration ample et fluide, tout en sine,
            // jamais d'arret ni de rebond sec
            anims.Add(pivot.DOScale(new Vector3(0.955f, 1.10f, 0.955f), 0.55f)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo)); // souffle

            if (head != null)
            {
                // la tete flotte en continu, legerement dephasee du souffle
                anims.Add(head.DOLocalMoveY(headBasePos.y + headBob * 4f, 0.55f)
                    .SetDelay(BlendTime + 0.1f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
                anims.Add(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 6f), 1.1f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            }

            // mains : balancement marque en opposition de phase
            if (handL != null)
                anims.Add(handL.DOLocalMoveY(handLBasePos.y + 0.06f, 0.6f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (handR != null)
                anims.Add(handR.DOLocalMoveY(handRBasePos.y + 0.06f, 0.6f)
                    .SetDelay(BlendTime + 0.3f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
        }

        private void EnterWalk()
        {
            walking = true;
            stateTimer = Random.Range(walkDuration.x, walkDuration.y);

            // direction aleatoire, biaisee vers le spawn si on s'eloigne trop
            Vector3 fromSpawn = transform.position - spawnPos;
            fromSpawn.y = 0f;
            if (fromSpawn.magnitude > wanderRadius)
                walkDir = Quaternion.Euler(0, Random.Range(-40f, 40f), 0) * -fromSpawn.normalized;
            else
                walkDir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;

            transform.DORotateQuaternion(Facing(walkDir), 0.35f).SetEase(Ease.OutQuad);

            ResetPoses();
            StartWalkAnims();
        }

        private void StartWalkAnims()
        {
            const float step = 0.24f; // cadence du pas
            anims.Add(pivot.DOLocalRotate(new Vector3(0, 0, waddleAngle), step)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo)); // dandine
            anims.Add(pivot.DOLocalMoveY(0.045f, step * 0.5f)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo)); // petit rebond
            if (handL != null)
                anims.Add(handL.DOLocalRotate(handLBaseRot.eulerAngles + new Vector3(armSwing, 0, 0), step)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (handR != null)
                anims.Add(handR.DOLocalRotate(handRBaseRot.eulerAngles - new Vector3(armSwing, 0, 0), step)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (head != null)
                anims.Add(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 3f), step)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
        }

        private const float BlendTime = 0.25f;

        // Kill les tweens et RAMENE les poses de base en douceur (pas de snap)
        private void ResetPoses()
        {
            foreach (var t in anims) t.Kill();
            anims.Clear();
            anims.Add(pivot.DOLocalRotate(Vector3.zero, BlendTime).SetEase(Ease.InOutSine));
            anims.Add(pivot.DOLocalMove(Vector3.zero, BlendTime).SetEase(Ease.InOutSine));
            anims.Add(pivot.DOScale(Vector3.one, BlendTime).SetEase(Ease.InOutSine));
            if (head != null)
            {
                anims.Add(head.DOLocalMove(headBasePos, BlendTime).SetEase(Ease.InOutSine));
                anims.Add(head.DOLocalRotateQuaternion(headBaseRot, BlendTime).SetEase(Ease.InOutSine));
            }
            if (handL != null)
            {
                anims.Add(handL.DOLocalMove(handLBasePos, BlendTime).SetEase(Ease.InOutSine));
                anims.Add(handL.DOLocalRotateQuaternion(handLBaseRot, BlendTime).SetEase(Ease.InOutSine));
            }
            if (handR != null)
            {
                anims.Add(handR.DOLocalMove(handRBasePos, BlendTime).SetEase(Ease.InOutSine));
                anims.Add(handR.DOLocalRotateQuaternion(handRBaseRot, BlendTime).SetEase(Ease.InOutSine));
            }
        }

        // ---- Roadkill : reaction quand le camion rentre dedans ----

        private void OnTriggerEnter(Collider other)
        {
            if (dead || mode == Mode.Stunned) return;
            var car = other.GetComponentInParent<ArcadeCarController>();
            if (car == null) return;

            Vector3 dir = transform.position - car.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = -car.transform.forward;
            dir.Normalize();

            float speed = car.Speed;
            if (car.IsBoosting || speed >= goreSpeed)
                Explode(car.transform.forward, car.IsPlayer);
            else if (speed >= pissedSpeed)
                Knockback(dir, pissedDistance, 2.6f, pissedLines, tumble: true);
            else
                Knockback(dir, disturbDistance, 0.5f, annoyedLines, tumble: false);
        }

        // Projete le pieton (DOJump = arc propre) et le sonne le temps du vol,
        // puis il se releve et reprend l'errance. Le pivot encaisse la culbute.
        private void Knockback(Vector3 dir, float distance, float jumpPower, string[] lines, bool tumble)
        {
            KillAnims();
            stunTween?.Kill();
            DOTween.Kill(pivot);
            mode = Mode.Stunned;

            Vector3 target = transform.position + dir * distance;
            stunTween = transform.DOJump(target, jumpPower, 1, 0.45f + distance * 0.06f)
                .SetEase(Ease.OutQuad).OnComplete(Recover);

            if (tumble)
                pivot.DOLocalRotate(new Vector3(360f, 0f, Random.Range(-50f, 50f)), 0.55f, RotateMode.FastBeyond360)
                    .SetEase(Ease.OutQuad);
            else
                pivot.DOPunchRotation(new Vector3(0f, 0f, 28f), 0.4f);

            if (bubble != null) bubble.Show(lines[Random.Range(0, lines.Length)], isThought: false, 1.6f);
        }

        private void Recover()
        {
            pivot.localRotation = Quaternion.identity;
            pivot.localPosition = Vector3.zero;
            pivot.localScale = Vector3.one;
            mode = Mode.Wander;
            EnterIdle();
        }

        // Boost / impact ultra rapide : le pieton explose en confettis de barbaque.
        // Hitstop (slow-mo global) seulement si c'est LE JOUEUR qui tue.
        private void Explode(Vector3 hitDir, bool playerKill = true)
        {
            if (dead) return;
            dead = true;
            if (playerKill) Hitstop.Punch(); // slow-mo d'impact lisse
            BloodFx.Spawn(transform.position, hitDir);
            Creatures.ReportKill(transform.position); // les temoins paniquent
            Destroy(gameObject);
        }

        private void KillAnims()
        {
            foreach (var t in anims) t.Kill();
            anims.Clear();
        }

        // Cap horizontal debout : yaw voulu, en gardant le tilt d'import du FBX.
        private Quaternion Facing(Vector3 dir) => Quaternion.LookRotation(dir) * baseTilt;

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(ExitCar));
            if (drivingCar != null) drivingCar.ClearDriver();
            stacheSeq?.Kill();
            stunTween?.Kill();
            foreach (var t in anims) t.Kill();
            anims.Clear();
        }
    }
}
