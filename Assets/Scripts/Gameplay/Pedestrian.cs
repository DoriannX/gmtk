using UnityEngine;
using UnityEngine.AI;
using DG.Tweening;
using Gameplay.City;

namespace Gameplay
{
    // Pieton lambda : erre dans la rue (marche -> pause -> nouvelle direction).
    // Anims 100% DOTween sur les pieces du modele Rayman-style :
    // idle = respiration + tete qui flotte, marche = dandine + bras qui pompent.
    // Un pivot intermediaire est cree au runtime pour ne pas ecraser la
    // rotation d'import du FBX avec les tweens.
    // La locomotion passe par un NavMeshAgent (pathfinding trottoirs + evitement) ;
    // RequireComponent le serialise sur le prefab -> pose sur NavMesh des le chargement.
    [RequireComponent(typeof(NavMeshAgent))]
    public class Pedestrian : MonoBehaviour
    {
        [Header("Errance (NavMesh trottoirs)")]
        [SerializeField] private float walkSpeed = 1.4f;
        [SerializeField] private Vector2 idleDuration = new Vector2(1f, 3f);
        [SerializeField] private float wanderRadius = 18f; // rayon d'errance autour du spawn (sur le NavMesh)
        [SerializeField] private float agentRadius = 0.35f; // < rayon de bake (0.5) : passe dans les trottoirs etroits
        [SerializeField] private float agentAccel = 12f;    // demarrage/arret nerveux (cartoon)
        [SerializeField] private float faceTurnSpeed = 520f; // deg/s d'orientation vers la vitesse

        [Header("Conscience de l'environnement")]
        [Tooltip("Regarde a gauche/droite avant de s'engager sur la chaussee.")]
        [SerializeField] private float carLookRadius = 6f;   // portee de detection des voitures en traversee
        [SerializeField] private float curbPause = 0.35f;    // temps d'arret au bord avant de traverser
        [SerializeField] private float flinchStop = 0.45f;   // duree du sursaut/gel quand une voiture fonce

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
        // Les modeles variantes s'importent avec une echelle racine ~100 (unites cm)
        // alors que le proto d'origine etait a 1. Les tweens de POSITION locale sur
        // tete/mains sont donc multiplies par le lossyScale du noeud -> tetes/mains
        // qui s'envolent. animUnit = 1/lossyScale ramene chaque offset positionnel a
        // du monde reel (proto=1 -> inchange, variante=100 -> x0.01). Les rotations
        // et l'echelle (respiration, moustache) ne sont PAS affectees par le scale.
        private float animUnit = 1f;
        // Le FBX est Z-up : la rotation d'import (ex. -90 X) le met debout. On la
        // capture pour la RECONSERVER a chaque cap, sinon LookRotation (Y-up) le
        // couche dans le sol. Cap final = LookRotation(dir) * baseTilt.
        private Quaternion baseTilt;

        private bool walking;
        private float stateTimer;
        private readonly System.Collections.Generic.List<Tween> anims = new();

        // NavMesh : l'agent PILOTE la position (pathfinding trottoirs + evitement RVO
        // entre pietons). On coupe sa rotation : le cap cartoon est gere a la main
        // (Facing + waddle). On le DESACTIVE pendant knockback/conduite (tweens/teleport).
        private NavMeshAgent agent;
        private RoadNetwork roads;            // pour savoir si on est sur la chaussee
        private float laneHalf;               // demi-largeur de la chaussee (hors trottoirs)
        private float awareTimer;             // cadence des scans d'environnement
        private float navRetry;               // cadence des tentatives de raccrochage au NavMesh
        private int navRetries = 12;          // ~6 s d'essais, puis on laisse tomber
        private float cautionLeft;            // gel volontaire (bord de trottoir / esquive)
        private bool wasOnRoad;               // detection front sidewalk->chaussee (regard avant traversee)

        // controle externe (PedestrianChatter) : approche + attente scriptees
        private enum Mode { Wander, WalkTo, Wait, Stunned, Flee, Driving }
        private Mode mode = Mode.Wander;
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
            // Pose debout d'import, CAP RETIRE. On enleve le yaw monde de la rotation
            // capturee : `Facing` multiplie chaque cap voulu par baseTilt, donc un pieton
            // pose avec une orientation quelconque (le semeur en donnait une au hasard, et
            // la main dans la scene en donne aussi) injecterait ce cap dans TOUS ses caps
            // suivants -- le personnage marche alors de travers en permanence.
            Quaternion imported = transform.rotation;
            baseTilt = Quaternion.Inverse(Quaternion.Euler(0f, imported.eulerAngles.y, 0f)) * imported;

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
            // facteur de compensation d'echelle (voir animUnit) : pris sur un membre anime
            Transform scaleRef = head != null ? head : (handL != null ? handL : handR);
            if (scaleRef != null && scaleRef.lossyScale.x > 1e-4f) animUnit = 1f / scaleRef.lossyScale.x;
            spawnPos = transform.position;
            bubble = GetComponent<SpeechBubble>();
            selfCol = GetComponent<Collider>();

            // Agent de navigation : locomotion + evitement. Rotation/height off (on
            // gere le cap et la pose d'import nous-memes ; l'agent ne touche que XZ).
            agent = GetComponent<NavMeshAgent>();
            if (agent == null) agent = gameObject.AddComponent<NavMeshAgent>();
            agent.updateRotation = false;
            agent.updateUpAxis = false;
            agent.radius = agentRadius;
            agent.height = 1.6f;
            agent.speed = walkSpeed;
            agent.acceleration = agentAccel;
            agent.angularSpeed = 999f;                 // pivote instantane (le visuel lisse via Facing)
            agent.stoppingDistance = 0.25f;
            agent.autoBraking = true;
            agent.avoidancePriority = Random.Range(20, 80); // brise la symetrie du RVO -> pas de blocage mutuel
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.GoodQualityObstacleAvoidance;
        }

        private void Start()
        {
            roads = FindFirstObjectByType<RoadNetwork>();
            // La chaussee est la bande centrale du trace, bordures et trottoirs exclus :
            // c'est exactement RoadwayHalfWidth, mesuree sur la tuile du kit.
            laneHalf = roads != null ? roads.RoadwayHalfWidth : 2.35f;
            SnapToNavMesh();
            // Cap de depart tire au sort, APRES la capture de baseTilt : c'est la seule
            // facon de varier l'orientation de la foule sans la fausser (cf. Awake).
            transform.rotation = Facing(Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward);
            EnterIdle();
        }

        // Pose le pieton sur le NavMesh le plus proche (il a pu spawner au bord d'un
        // trottoir, ou hors emprise). Recale spawnPos sur le point reel -> l'errance
        // reste ancree sur le reseau marchable.
        //
        // On coupe/rallume l'agent au lieu d'un simple Warp : le NavMesh n'existe qu'au
        // premier LateUpdate (RuntimeNavBaker, la route est reconstruite au premier Update),
        // donc l'agent s'est cree AVANT lui et Unity l'a refuse ("Failed to create agent
        // because it is not close enough to the NavMesh"). Seul un cycle enabled le recree.
        private void SnapToNavMesh()
        {
            if (agent == null) return;
            if (NavMesh.SamplePosition(transform.position, out var hit, 6f, NavMesh.AllAreas))
            {
                agent.enabled = false;
                transform.position = hit.position;
                agent.enabled = true;
                spawnPos = hit.position;
            }
            else
            {
                agent.enabled = false; // pas de NavMesh dessous : reste inerte plutot que d'erreur
            }
        }

        // Le NavMesh arrive apres le Start des pietons : celui qui n'a rien trouve reessaie
        // quelques secondes, sinon il resterait plante la toute la partie. Au-dela on
        // abandonne (il a spawne hors emprise) plutot que de sonder a vie.
        private void RetryNavMesh(float dt)
        {
            if (navRetries <= 0 || agent == null) return;
            if (agent.enabled && agent.isOnNavMesh) { navRetries = 0; return; }
            navRetry -= dt;
            if (navRetry > 0f) return;
            navRetry = 0.5f;
            navRetries--;
            agent.enabled = true;      // rallume avant de sonder : Snap le recoupera si besoin
            SnapToNavMesh();
        }

        private void OnEnable() => Creatures.Killed += OnCreatureKilled;
        private void OnDisable() => Creatures.Killed -= OnCreatureKilled;

        private void Update()
        {
            if (mode == Mode.Stunned) return; // projete par le camion, le tween pilote
            if (mode == Mode.Driving) return; // au volant : cache, c'est la voiture qui roule

            float dt = Time.deltaTime;
            RetryNavMesh(dt);          // le NavMesh n'est bake qu'au premier LateUpdate
            FaceVelocity(dt);          // oriente le corps vers le deplacement reel de l'agent
            UpdateAwareness(dt);       // regard au bord + esquive des voitures (peut geler l'agent)

            // Gel volontaire (pause au bord / sursaut) : on tient la position, pas d'arrivee.
            if (cautionLeft > 0f)
            {
                cautionLeft -= dt;
                if (cautionLeft <= 0f && agent != null && agent.isOnNavMesh) agent.isStopped = false;
                return;
            }

            if (mode == Mode.Flee)
            {
                fleeTimer -= dt;
                if (fleeTimer <= 0f || AgentArrived()) { mode = Mode.Wander; EnterIdle(); }
                return;
            }
            if (mode == Mode.WalkTo)
            {
                if (AgentArrived())
                {
                    EnterIdle();
                    mode = Mode.Wait;
                    var cb = onArrived; onArrived = null;
                    cb?.Invoke();
                }
                return;
            }
            if (mode == Mode.Wait) return;

            // Errance : marche vers une cible NavMesh -> arrive -> idle -> nouvelle cible.
            if (walking)
            {
                if (AgentArrived()) EnterIdle();
            }
            else
            {
                stateTimer -= dt;
                if (stateTimer <= 0f)
                {
                    if (TrySeekCar()) return;
                    EnterWalk();
                }
            }
        }

        // Arrive a destination : chemin calcule, distance restante sous le seuil, quasi arrete.
        private bool AgentArrived()
        {
            if (agent == null || !agent.isOnNavMesh) return true;
            if (agent.pathPending) return false;
            return agent.remainingDistance <= agent.stoppingDistance + 0.05f
                   && agent.velocity.sqrMagnitude < 0.04f;
        }

        // Oriente le pieton vers sa vitesse reelle (l'agent gere les detours d'evitement).
        private void FaceVelocity(float dt)
        {
            if (agent == null || !agent.isOnNavMesh) return;
            Vector3 v = agent.velocity; v.y = 0f;
            if (v.sqrMagnitude < 0.04f) return;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Facing(v.normalized), faceTurnSpeed * dt);
        }

        // ---- Conscience : regard avant traversee + esquive des voitures ----
        // Front sidewalk->chaussee : marque une pause "je regarde" au bord. En pleine
        // traversee : si une voiture fonce vers lui, sursaut + gel (mais il PEUT se
        // faire ecraser -> le roadkill reste). Scans espaces (0.2s) pour le cout.
        private void UpdateAwareness(float dt)
        {
            if (agent == null || !agent.isOnNavMesh || roads == null) return;
            if (mode != Mode.Wander && mode != Mode.WalkTo && mode != Mode.Flee) return;
            if (!walking && mode == Mode.Wander) { wasOnRoad = false; return; }

            awareTimer -= dt;
            if (awareTimer > 0f) return;
            awareTimer = 0.2f;

            bool onRoad = OnRoadway(transform.position);

            // Bord du trottoir : il s'apprete a s'engager -> petite pause + coup d'oeil.
            if (onRoad && !wasOnRoad)
                LookBeforeCrossing();
            wasOnRoad = onRoad;

            // En traversee : une voiture arrive dessus -> sursaut/gel.
            if (onRoad && CarBearingDown())
                Flinch();
        }

        // Le point est-il sur la CHAUSSEE (donc expose) ? ProbeRoad projette sur l'axe du trace
        // le plus proche ; a moins d'une demi-chaussee de cet axe, on est dans la voie.
        //
        // ponytail: on ne teste que les SEGMENTS. Un carrefour renvoie une distance a son
        // CENTRE, pas a un axe, et le seuil n'y veut rien dire -- au pire un pieton traverse un
        // croisement sans marquer sa pause, la ou la traversee de rue, elle, est couverte.
        private bool OnRoadway(Vector3 world)
            => roads.ProbeRoad(world, 40f, out var probe)
               && probe.valid && probe.segment >= 0 && probe.distance < laneHalf;

        private void LookBeforeCrossing()
        {
            if (agent == null || !agent.isOnNavMesh) return;
            agent.isStopped = true;
            cautionLeft = curbPause;
            if (head != null) // tourne la tete G puis D (regarde des deux cotes)
                head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 22f), curbPause * 0.5f)
                    .SetEase(Ease.InOutSine).SetLoops(2, LoopType.Yoyo);
        }

        // Voiture proche orientee vers le pieton (danger imminent) ? Player + IA.
        private bool CarBearingDown()
        {
            var hits = Physics.OverlapSphere(transform.position, carLookRadius);
            foreach (var col in hits)
            {
                Transform ct = null; Vector3 fwd = Vector3.zero;
                var player = col.GetComponentInParent<ArcadeCarController>();
                if (player != null) { ct = player.transform; fwd = ct.forward; }
                else
                {
                    var ai = col.GetComponentInParent<AICarController>();
                    if (ai != null && ai.Occupied) { ct = ai.transform; fwd = ct.forward; }
                }
                if (ct == null) continue;
                Vector3 to = transform.position - ct.position; to.y = 0f;
                if (to.sqrMagnitude < 0.01f) return true;              // deja dessus
                if (Vector3.Dot(fwd, to.normalized) > 0.5f) return true; // roule vers moi
            }
            return false;
        }

        private void Flinch()
        {
            if (cautionLeft > 0f || agent == null || !agent.isOnNavMesh) return;
            agent.isStopped = true;
            cautionLeft = flinchStop;
            pivot.DOComplete();
            pivot.DOPunchScale(new Vector3(0.2f, -0.25f, 0.2f), flinchStop, 6, 0.7f); // sursaut recroqueville
            pivot.DOPunchPosition(-transform.forward.normalized * 0.15f, flinchStop, 5, 0.6f); // petit recul
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
            cautionLeft = 0f;
            KillAnims();
            DisableAgent();                    // teleporte dans la caisse : agent off
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
            EnableAgentHere(); // ressort de la caisse -> se recale sur le trottoir
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
            onArrived = arrived;
            walking = true;
            cautionLeft = 0f;
            if (agent != null && agent.isOnNavMesh)
            {
                // vise le point marchable le plus proche de la cible + regle la distance d'arret
                Vector3 t = target;
                if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas)) t = hit.position;
                agent.isStopped = false;
                agent.speed = walkSpeed;
                agent.stoppingDistance = Mathf.Max(0.25f, stopDist);
                agent.SetDestination(t);
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
            if (agent != null && !agent.enabled) EnableAgentHere();
            EnterIdle();
        }

        // ---- Meme "six seven" : se fige et fait le geste de la balance ----
        // (avant-bras leves paumes au ciel, les deux mains qui montent/descendent
        // en opposition — comme peser deux trucs). Moustache en furie pendant.
        public void SixSeven(float duration)
        {
            if (dead || mode == Mode.Stunned || mode == Mode.Flee || mode == Mode.Driving) return;
            mode = Mode.Wait;
            cautionLeft = 0f;
            if (agent != null && agent.isOnNavMesh) agent.isStopped = true;
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
            Vector3 fwd = new Vector3(0, -sixFwd * animUnit, 0);
            Vector3 inL = new Vector3(-sixInward * animUnit, 0, 0);
            Vector3 inR = new Vector3(sixInward * animUnit, 0, 0);
            Vector3 low = new Vector3(0, 0, (sixLift - sixSeesaw) * animUnit);
            Vector3 high = new Vector3(0, 0, (sixLift + sixSeesaw) * animUnit);
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

        private void OnCreatureKilled(Vector3 deathPos, bool byPlayer)
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
            cautionLeft = 0f;

            // fuit a l'oppose du carnage, en restant sur le reseau marchable
            fleeDir = transform.position - deathPos; fleeDir.y = 0f;
            if (fleeDir.sqrMagnitude < 0.01f) fleeDir = -transform.forward;
            fleeDir.Normalize();
            fleeTimer = Random.Range(fleeDuration.x, fleeDuration.y);

            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.speed = fleeRunSpeed;
                Vector3 goal = transform.position + fleeDir * wanderRadius;
                if (NavMesh.SamplePosition(goal, out var hit, wanderRadius, NavMesh.AllAreas))
                    agent.SetDestination(hit.position);
                else
                    agent.SetDestination(goal);
            }

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
            wasOnRoad = false;
            stateTimer = Random.Range(idleDuration.x, idleDuration.y);
            if (agent != null && agent.isOnNavMesh) { agent.isStopped = true; agent.stoppingDistance = 0.25f; }
            ResetPoses();

            // Idle continu : respiration ample et fluide, tout en sine,
            // jamais d'arret ni de rebond sec
            anims.Add(pivot.DOScale(new Vector3(0.955f, 1.10f, 0.955f), 0.55f)
                .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo)); // souffle

            if (head != null)
            {
                // la tete flotte en continu, legerement dephasee du souffle
                anims.Add(head.DOLocalMoveY(headBasePos.y + headBob * 4f * animUnit, 0.55f)
                    .SetDelay(BlendTime + 0.1f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
                anims.Add(head.DOLocalRotate(headBaseRot.eulerAngles + new Vector3(0, 0, 6f), 1.1f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            }

            // mains : balancement marque en opposition de phase
            if (handL != null)
                anims.Add(handL.DOLocalMoveY(handLBasePos.y + 0.06f * animUnit, 0.6f)
                    .SetDelay(BlendTime).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
            if (handR != null)
                anims.Add(handR.DOLocalMoveY(handRBasePos.y + 0.06f * animUnit, 0.6f)
                    .SetDelay(BlendTime + 0.3f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo));
        }

        private void EnterWalk()
        {
            // Cherche un point marchable au hasard autour du spawn (reste local, sur trottoir).
            if (!PickWanderDest(out Vector3 dest)) { EnterIdle(); return; }
            walking = true;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.speed = walkSpeed;
                agent.SetDestination(dest);
            }
            ResetPoses();
            StartWalkAnims();
        }

        // Point NavMesh aleatoire dans le rayon d'errance autour du spawn.
        private bool PickWanderDest(out Vector3 dest)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector2 r = Random.insideUnitCircle * wanderRadius;
                Vector3 p = spawnPos + new Vector3(r.x, 0f, r.y);
                if (NavMesh.SamplePosition(p, out var hit, wanderRadius * 0.5f, NavMesh.AllAreas))
                {
                    dest = hit.position;
                    return true;
                }
            }
            dest = spawnPos;
            return false;
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
            cautionLeft = 0f;
            DisableAgent(); // le tween DOJump pilote la position, l'agent ne doit pas se battre avec

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
            EnableAgentHere(); // se recale sur le trottoir la ou il a atterri
            EnterIdle();
        }

        // Reactive l'agent et le recale sur le NavMesh le plus proche de sa position
        // actuelle (apres un vol plane / une sortie de voiture).
        private void EnableAgentHere()
        {
            if (agent == null) return;
            if (!agent.enabled) agent.enabled = true;
            if (NavMesh.SamplePosition(transform.position, out var hit, 8f, NavMesh.AllAreas))
                agent.Warp(hit.position);
        }

        private void DisableAgent()
        {
            if (agent != null && agent.enabled) agent.enabled = false;
        }

        // Boost / impact ultra rapide : le pieton explose en confettis de barbaque.
        // Hitstop (slow-mo global) seulement si c'est LE JOUEUR qui tue.
        private void Explode(Vector3 hitDir, bool playerKill = true)
        {
            if (dead) return;
            dead = true;
            if (playerKill) Hitstop.Punch(); // slow-mo d'impact lisse
            BloodFx.Spawn(transform.position, hitDir);
            Creatures.ReportKill(transform.position, playerKill); // temoins paniquent + popularite
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
