using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Controleur voiture arcade cartoon type "Make Way" / Mario Kart :
    // acceleration brutale, drift au bouton (espace) qui coupe le grip,
    // mini-turbo a la sortie de drift, roulis visuel exagere du body.
    // Prototype jetable pour tester le feel en 3e personne.
    [RequireComponent(typeof(Rigidbody))]
    public class ArcadeCarController : MonoBehaviour
    {
        public enum GroundMode { MonoWheel, FourPoint }

        [Header("Moteur")]
        [SerializeField] private float acceleration = 45f;
        [SerializeField] private float reverseAcceleration = 20f;
        [SerializeField] private float maxSpeed = 22f;

        [Header("Direction")]
        [SerializeField] private float steerSpeed = 160f; // deg/s a pleine vitesse
        [SerializeField] private float minSpeedToSteer = 0.5f;

        [Header("Grip")]
        [SerializeField] private float tireGrip = 11f;         // grip lateral (accel par m/s de slip) : plus haut = plus accroche
        [SerializeField] private float driftGripMul = 0.16f;   // grip reduit en drift -> glisse savonnette
        [SerializeField] private float driftSteerMultiplier = 1.8f;
        [SerializeField] private float groundDrag = 0.4f;

        [Header("Modele sol (comparer les deux)")]
        [SerializeField] private GroundMode groundMode = GroundMode.FourPoint; // FourPoint = stable partout ; MonoWheel = 1 roue centrale (passe sur les poutres etroites)

        [Header("Suspension mono-roue (ressort central + equilibrage)")]
        [SerializeField] private float monoTravel = 0.35f;     // debattement de la roue centrale
        [SerializeField] private float alignSpeed = 400f;      // deg/s : equilibrage (aligne la caisse a la normale sol)
        [SerializeField] private float alignProbeLength = 2.5f; // portee (descente) des 4 sondes de plan
        [SerializeField] private Vector2 alignProbeExtents = new Vector2(0.6f, 2.5f); // ecartement des sondes (x lateral, z avant/arriere) : DOIT depasser le bumper -> anticipe la pente AVANT que le nez cogne

        [Header("Suspension 4 points (raycast, invisible)")]
        [SerializeField] private Vector2 suspensionHalfExtents = new Vector2(0.5f, 1.0f); // demi-voie (x) / demi-empattement (z) des 4 appuis
        [SerializeField] private float suspensionMountHeight = 0.25f; // Y local des ancrages sur le chassis
        [SerializeField] private float suspensionRest = 0.6f;  // longueur repos du ressort (mount -> sol au repos)
        [SerializeField] private float springStiffness = 60f;  // raideur (accel/m de compression) PAR appui
        [SerializeField] private float springDamper = 7f;      // amortisseur (accel/(m/s)) -> pas de rebond
        [SerializeField] private bool useSuspension = true;    // OFF (test) = pas de ressort, le camion repose sur son BoxCollider
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float normalSmoothSpeed = 12f; // lissage de la normale sol moyenne (drive) -> pas de pop

        [Header("Mini-turbo (sortie de drift)")]
        [SerializeField] private float boostMinDriftTime = 0.7f; // duree de drift pour armer le boost
        [SerializeField] private float boostImpulse = 8f;

        [Header("Saut (impulsion verticale)")]
        [SerializeField] private float jumpVelocity = 7f;       // m/s vers le haut -> hauteur ~ v^2/2g (7 -> ~2.5m)
        [SerializeField] private float jumpBufferTime = 0.15f;  // presse un peu avant d'atterrir = saute quand meme
        [SerializeField] private float coyoteTime = 0.12f;     // saute juste apres avoir quitte le sol
        [SerializeField] private float stretchOnJump = 1.35f;
        [SerializeField] private float squashOnLand = 0.65f;

        [Header("Air control")]
        [SerializeField] private float airSteerSpeed = 150f; // deg/s en l'air (IA / yaw de base)

        [Header("Figures (air control joueur)")]
        [SerializeField] private float airSpinSpeed = 300f;  // yaw en l'air (spins) : Shift + A/D
        [SerializeField] private float airFlipSpeed = 300f;  // pitch (flip) : Shift + W/S
        [SerializeField] private float airLevelSpeed = 220f; // redressement auto (deg/s) hors mode figure -> retombe sur roues

        [Header("Saut / air")]
        [SerializeField] private float fallMultiplier = 1.8f;       // gravite x en chute -> arc snappy

        [Header("Visuel cartoon")]
        [SerializeField] private Transform visualBody; // child cosmetique, tilte sans toucher la physique
        [SerializeField] private Transform wheelVisual; // roue unique, tourne selon la vitesse
        [SerializeField] private float wheelRadius = 0.51f;
        [SerializeField] private float wheelVisualDroop = 0.35f; // debattement VISUEL de la mono-roue deco (cosmetique, sans physique)
        [SerializeField] private float maxRollAngle = 18f; // roulis dans les virages
        [SerializeField] private float maxPitchAngle = 14f; // penche en avant quand roule en avant, en arriere en marche arriere
        [SerializeField] private float tiltLerpSpeed = 8f;
        [SerializeField] private float driftVisualYaw = 22f; // contre-braquage visuel du body en drift

        [Header("Boost")]
        [SerializeField] private float boostSpeedGain = 9f;      // impulse immediat
        [SerializeField] private float boostExtraMaxSpeed = 10f; // vitesse max relevee pendant le boost
        [SerializeField] private float boostDuration = 0.9f;
        [SerializeField] private float boostCooldown = 2.5f;
        [SerializeField] private float boostFovKick = 12f;       // feedback camera
        [SerializeField] private float boostPitchLean = 7f;      // lean avant supplementaire

        [Header("VFX")]
        [SerializeField] private ParticleSystem driftSmoke;
        [SerializeField] private float driftSmokeRate = 45f;
        [SerializeField] private int landPuffCount = 10;
        [SerializeField] private ParticleSystem boostFlames;
        [SerializeField] private float boostFlamesRate = 150f;
        [SerializeField] private int boostBurstCount = 25; // burst au declenchement
        [SerializeField] private ParticleSystem exhaustSmoke; // chug d'echappement a l'accel
        [SerializeField] private float exhaustAccelRate = 25f;
        [SerializeField] private float exhaustIdleRate = 3f;

        [Header("Feux de recul")]
        [SerializeField] private Renderer[] reverseLightRenderers; // lentilles arriere, emission togglee
        [SerializeField] private Light[] reverseLightSources;      // vrais spots arriere
        [SerializeField] private Color reverseLightColor = new Color(1f, 0.08f, 0.05f);
        [SerializeField] private float reverseLightIntensity = 3f;

        [Header("Phares (toggle F)")]
        [SerializeField] private Renderer[] headlightRenderers; // lentilles avant, emission togglee
        [SerializeField] private Light[] headlightSources;      // vrais spots avant
        [SerializeField] private Color headlightColor = new Color(1f, 0.95f, 0.75f);
        [SerializeField] private float headlightEmission = 3f;

        [Header("Juice accel")]
        [SerializeField] private float fovSpeedGain = 8f; // FOV gonfle avec la vitesse

        [Header("Burnout depart")]
        [SerializeField] private float burnoutMaxSpeed = 2f;     // en dessous -> burnout possible
        [SerializeField] private float burnoutMaxCharge = 1.5f;  // secondes de charge max
        [SerializeField] private float burnoutMinCharge = 0.35f; // charge mini pour lancer
        [SerializeField] private float burnoutLaunchGain = 13f;  // impulse a charge pleine
        [SerializeField] private float burnoutSpinVisual = 900f; // deg/s de patinage roue
        [SerializeField] private TrailRenderer skidTrail;        // trace de gomme au sol

        [Header("Juice boost")]
        [SerializeField] private ParticleSystem speedLines;   // trainees anime autour du camion
        [SerializeField] private float speedLinesRate = 70f;
        [SerializeField] private ParticleSystem boostRing;    // onde de choc au declenchement
        [SerializeField] private float boostShakeAmplitude = 0.18f;
        [SerializeField] private float boostShakeDuration = 0.35f;
        [SerializeField] private float boostFovPunch = 1.9f;  // overshoot instantane du FOV (x kick)
        [SerializeField] private int burnoutPuffCount = 12;   // fumee sous la roue au depart

        private Rigidbody rb;
        private float driftTime;
        private bool wasDriftBtn;    // bouton drift au frame precedent (mini-turbo a la relache)
        private float currentSteer; // steer lisse pour le tilt visuel
        private float smoothWheelY;  // debattement lisse de la roue visuelle (evite le snap)
        private bool wheelYInit;
        private Vector3 groundNormalSmooth = Vector3.up; // normale sol lissee (anti-pop d'orientation)

        // Spin roue : angle cumule applique en local -> la roue (ronde) suit
        // le tilt du body, ce qui reste credible vu son profil sphere.
        private float wheelAngle;
        private Vector3 wheelSpinAxisLocal;
        private Quaternion wheelRestLocalRot;
        private Vector3 wheelRestLocalPos;   // roue "montee" (haut du debattement)

        // Boost
        private float boostTimer;
        private float boostCooldownTimer;
        private bool boostQueued;
        private Camera followCam;
        private float baseFov;
        private CarFollowCamera followCamCtrl;
        private float fovPunchValue; // surplus de FOV du punch, decroit tout seul

        // Burnout
        private float burnoutCharge;
        private bool burnoutActive;

        // Feux de recul (etat courant pour ne toucher les materiaux qu'au changement)
        private bool reverseLightsOn;
        private bool headlightsOn;

        // Squash & stretch (DOTween)
        private bool wasGrounded = true;
        private Vector3 visualBaseScale = Vector3.one;
        private Tween scaleTween;

        // Saut : buffer echantillonne en Update (wasPressedThisFrame rate des
        // FixedUpdate sinon) + coyote time
        private float jumpBufferTimer;
        private float coyoteTimer;

        // Etat expose pour les interactions (roadkill pieton) : vitesse plane
        // et boost actif decident du niveau de reaction du pieton percute.
        public float Speed { get { var v = rb.linearVelocity; v.y = 0f; return v.magnitude; } }
        public bool IsBoosting => boostTimer > 0f;
        public Rigidbody Body => rb;
        public void RampPop() => PunchScaleY(stretchOnJump); // juice quand un tremplin lance la voiture

        // --- Etat expose pour le TrickSystem (detection de figures) ---
        public bool Grounded { get; private set; }
        public bool Drifting { get; private set; }
        // rotation aerienne cumulee (deg signes), remise a zero au decollage. Vient
        // des increments d'input appliques -> pas de galere de gimbal euler.
        public float AirSpinDeg { get; private set; }
        public float AirFlipDeg { get; private set; }
        // Vrai quand le joueur pilote activement une figure en l'air (Shift maintenu).
        public bool DoingTrick { get; private set; }
        // Orientation a l'instant du contact (up . monde), AVANT le redressement auto :
        // > ~0.5 = retombe sur les roues. Lu par le TrickSystem pour le fail de figure.
        public float LandUprightDot { get; private set; } = 1f;
        // Remet la voiture d'aplomb (garde le cap) -> atterrissage cartoon indulgent.
        public void LevelOut()
        {
            Vector3 e = rb.rotation.eulerAngles;
            rb.MoveRotation(Quaternion.Euler(0f, e.y, 0f));
        }

        // Source d'inputs : clavier joueur par defaut, ou un RacerAI (le "fou").
        private ICarInput inputSrc;
        public ICarInput Input { get => inputSrc ??= KeyboardCarInput.I; set => inputSrc = value; }
        // true = voiture du joueur humain. Une IA (RacerAI) met false : pas de
        // pilotage camera/FOV, et ses accidents ne declenchent ni shake ni hitstop.
        public bool IsPlayer { get; set; } = true;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            // Suspension gere l'assiette : COM au centre (pas trop bas, sinon elle
            // se bat contre le pitch de la suspension). Amortissement angulaire pour
            // eviter le wobble, lineaire leger pour garder l'elan.
            rb.centerOfMass = new Vector3(0f, -0.15f, 0f);
            rb.linearDamping = groundDrag;
            rb.angularDamping = 3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            if (wheelVisual != null)
            {
                wheelRestLocalRot = wheelVisual.localRotation;
                wheelRestLocalPos = wheelVisual.localPosition; // position "montee" (haut du debattement)
                // axe d'essieu (droite de la voiture) exprime dans le repere local de la roue
                wheelSpinAxisLocal = wheelVisual.InverseTransformDirection(transform.right);
            }
            if (visualBody != null)
                visualBaseScale = visualBody.localScale;

            // Camera.main exige le tag MainCamera -> fallback par type si oublie
            followCam = Camera.main;
            if (followCam == null)
            {
                followCamCtrl = FindAnyObjectByType<CarFollowCamera>();
                if (followCamCtrl != null) followCam = followCamCtrl.GetComponent<Camera>();
            }
            else
            {
                followCamCtrl = followCam.GetComponent<CarFollowCamera>();
            }
            if (followCam != null) baseFov = followCam.fieldOfView;

            // Phares eteints au spawn (ecrase aussi l'emission baked du TruckLight
            // sur les lentilles avant, sinon elles brillent moteur coupe)
            SetHeadlights(false);
        }

        private void Update()
        {
            // Echantillonne les inputs cote frame : wasPressedThisFrame lu en
            // FixedUpdate rate des appuis (0 ou N ticks physique par frame)
            if (Input.JumpPressed)
                jumpBufferTimer = jumpBufferTime;
            if (Input.BoostPressed && boostCooldownTimer <= 0f)
                boostQueued = true;
            if (Input.HeadlightPressed)
                SetHeadlights(!headlightsOn);
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Vector2 input = Input.Movement;
            float throttle = input.y;
            float steer = input.x;

            // ================= SUSPENSION 4 POINTS (raycast, invisible) =================
            // 4 ressorts-amortisseurs aux coins du chassis. Chacun sonde le sol sous lui et
            // pousse la caisse -> l'assiette suit NATURELLEMENT le terrain (pente, bosse), sans
            // aucun alignement force : c'est la fin du snap. Le BoxCollider sert de BUTEE DURE
            // (la caisse ne peut pas s'enfoncer sous le sol), les ressorts n'assurent que la
            // course douce au-dessus. La mono-roue est purement VISUELLE (voir PlantWheel).
            bool grounded = false;
            Vector3 groundNormal = Vector3.up;
            if (useSuspension && groundMode == GroundMode.FourPoint)
            {
                // --- 4 ressorts aux coins : assiette naturelle, stable, mais straddle les surfaces etroites (poutre) ---
                Vector3 normalSum = Vector3.zero;
                int groundedCorners = 0;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 local = new Vector3(
                        ((i & 1) == 0 ? -1f : 1f) * suspensionHalfExtents.x,
                        suspensionMountHeight,
                        ((i & 2) == 0 ? -1f : 1f) * suspensionHalfExtents.y);
                    Vector3 corner = transform.TransformPoint(local);
                    if (Physics.Raycast(corner, -transform.up, out RaycastHit chit,
                            suspensionRest + 0.1f, groundMask, QueryTriggerInteraction.Ignore)
                        && chit.collider.attachedRigidbody != rb)
                    {
                        grounded = true;
                        groundedCorners++;
                        normalSum += chit.normal;
                        float compression = suspensionRest - chit.distance; // >0 = comprime
                        if (compression > 0f)
                        {
                            float relVel = Vector3.Dot(rb.GetPointVelocity(corner), transform.up);
                            float f = Mathf.Max(compression * springStiffness - relVel * springDamper, 0f);
                            rb.AddForceAtPosition(transform.up * f, corner, ForceMode.Acceleration);
                        }
                    }
                }
                if (groundedCorners > 0) groundNormal = (normalSum / groundedCorners).normalized;
            }
            else if (useSuspension)
            {
                // --- MONO-ROUE : un seul ressort central + butee dure. Contact ponctuel ->
                // passe sur les surfaces etroites (poutre). L'equilibrage (align, plus bas)
                // tient la caisse droite (monocycle). Le BoxCollider n'est PAS sous la roue,
                // donc c'est la butee de penetration qui empeche la roue de traverser le sol.
                // SPHERECAST (rayon = roue) : la suspension "voit" une VRAIE roue ronde. Elle
                // accroche l'arete des marches / obstacles (< rayon) et le ressort la fait MONTER
                // dessus, au lieu de buter comme un rayon fin -> grimpe les escaliers, roule sur
                // aretes/bosses. Garde le debattement du ressort (un collider rigide le tuerait).
                Vector3 mountWorld = WheelMountWorld();
                // Depart du spherecast AU-DESSUS du mount : si la sphere overlap deja un collider
                // au depart, SphereCast ne le detecte pas -> plus de support -> on passe a travers
                // le sol a l'atterrissage. On leve l'origine pour que la sphere parte hors du sol.
                float castLift = wheelRadius + monoTravel;
                Vector3 castOrigin = mountWorld + transform.up * castLift;
                if (Physics.SphereCast(castOrigin, wheelRadius, -transform.up, out RaycastHit mhit,
                        castLift + monoTravel + 0.1f, groundMask, QueryTriggerInteraction.Ignore)
                    && mhit.collider.attachedRigidbody != rb)
                {
                    grounded = true;
                    groundNormal = mhit.normal.sqrMagnitude > 0.01f ? mhit.normal : Vector3.up;
                    float rawTravel = mhit.distance - castLift; // <0 = roue au repos overlap le sol (marche/arete plus haute que le mount)
                    float travel = Mathf.Clamp(rawTravel, 0f, monoTravel);
                    float compression = monoTravel - travel;
                    float relVel = Vector3.Dot(rb.GetPointVelocity(mountWorld), transform.up);
                    float f = Mathf.Max(compression * springStiffness - relVel * springDamper, 0f);
                    rb.AddForceAtPosition(transform.up * f, mountWorld, ForceMode.Acceleration);
                    // BUTEE DURE (roue solide) : si le sol depasse la position de repos de la roue,
                    // elle rentrerait dedans -> on remonte la caisse de la penetration + annule la
                    // vitesse descendante. La roue ne traverse plus (monte sur la poutre/marche/atterrissage).
                    if (rawTravel < 0f)
                    {
                        rb.position += transform.up * (-rawTravel);
                        float downV = Vector3.Dot(rb.linearVelocity, transform.up);
                        if (downV < 0f) rb.linearVelocity -= transform.up * downV;
                    }

                    // ASSIETTE ANTICIPEE : on estime le PLAN porteur a partir des POINTS de contact
                    // aux 4 coins (fit de plan, pas moyenne de normales -> correct sur pentes raides).
                    // Avant-arriere -> pitch, gauche-droite -> roll. Quand l'avant passe sur la rampe,
                    // le plan s'incline -> le nez leve et grimpe. Il faut les 4 coins ; sinon (coin dans
                    // le vide / hors portee, ex. poutre) on garde la normale de la roue. Sonde seule (0 force).
                    Vector3 c0, c1, c2, c3;
                    if (ProbeCorner(0, out c0) & ProbeCorner(1, out c1)
                      & ProbeCorner(2, out c2) & ProbeCorner(3, out c3))
                    {
                        Vector3 fwd = ((c2 + c3) - (c0 + c1)) * 0.5f; // avant - arriere
                        Vector3 rgt = ((c1 + c3) - (c0 + c2)) * 0.5f; // droite - gauche
                        Vector3 n = Vector3.Cross(fwd, rgt);
                        if (Vector3.Dot(n, transform.up) < 0f) n = -n;
                        if (n.sqrMagnitude > 1e-4f) groundNormal = n.normalized;
                    }
                }
            }
            // Detection sol ROBUSTE : le ray suspension (-transform.up) rate quand le
            // camion est sur le toit/le flanc (il pointe en l'air) -> la caisse touche
            // le sol mais grounded restait false -> l'echec de figure n'etait jamais
            // detecte (LandUprightDot lu trop tard, deja redresse). Ray VERS LE BAS
            // MONDE depuis la caisse : contact sol quelle que soit l'orientation.
            // GATE sur l'inclinaison (up.monde < 0.5) : seulement quand la suspension
            // rate A CAUSE de l'orientation. Sinon (camion droit mais bas, ex. juste
            // apres un saut) ce ray verrait le sol -> faux "au sol" -> air control
            // bloque et figure comptee 2x. Droit -> suspension seule decide.
            if (!grounded && Vector3.Dot(transform.up, Vector3.up) < 0.5f)
            {
                float bodyLen = suspensionRest + 0.4f;
                if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit bhit, bodyLen,
                        groundMask, QueryTriggerInteraction.Ignore)
                    && bhit.collider.attachedRigidbody != rb)
                {
                    grounded = true;
                    groundNormal = bhit.normal;
                }
            }
            bool drifting = grounded && Input.DriftHeld && rb.linearVelocity.magnitude > 3f;
            Grounded = grounded; Drifting = drifting;

            // Normale lissee : hit.normal saute d'un tick a l'autre au bas d'une rampe /
            // sur une arete -> la caisse poppait a la nouvelle assiette. On low-pass la
            // normale pour des transitions douces. Snap direct a l'atterrissage (pas de
            // lerp depuis une normale peri-mee), sinon lissage exponentiel independant du framerate.
            if (grounded)
                groundNormalSmooth = wasGrounded
                    ? Vector3.Slerp(groundNormalSmooth, groundNormal, 1f - Mathf.Exp(-dt * normalSmoothSpeed))
                    : groundNormal;

            // ================= SAUT (impulsion verticale) =================
            coyoteTimer = grounded ? coyoteTime : coyoteTimer - dt;
            if (jumpBufferTimer > 0f && coyoteTimer > 0f)
            {
                // impulsion verticale nette -> hauteur previsible (v^2/2g), independante de la
                // raideur suspension. On repart d'une vitesse verticale propre (pas de cumul negatif).
                Vector3 v = rb.linearVelocity;
                if (v.y < 0f) v.y = 0f;
                rb.linearVelocity = v + Vector3.up * jumpVelocity;
                PunchScaleY(stretchOnJump);
                jumpBufferTimer = 0f;
                coyoteTimer = 0f;
            }
            jumpBufferTimer -= dt;

            // Juice atterrissage / decollage
            if (!wasGrounded && grounded)
            {
                LandUprightDot = Vector3.Dot(transform.up, Vector3.up);
                PunchScaleY(squashOnLand);
                if (driftSmoke != null)
                {
                    var p = new ParticleSystem.EmitParams
                    {
                        position = wheelVisual != null
                            ? wheelVisual.position + Vector3.down * (wheelRadius * 0.8f)
                            : transform.position + Vector3.down * 0.3f,
                        applyShapeToPosition = true
                    };
                    for (int i = 0; i < landPuffCount; i++)
                        driftSmoke.Emit(p, 1);
                }
            }
            if (wasGrounded && !grounded)
                AirSpinDeg = AirFlipDeg = 0f;
            wasGrounded = grounded;

            // ================= BURNOUT =================
            // burnout = joueur, a l'ARRET (vitesse TOTALE basse). Avant on testait la
            // vitesse AVANT -> en donut (mouvement lateral) elle est basse -> burnout
            // parasite qui freine et casse le donut. Vitesse totale = OK.
            bool burnoutHold = IsPlayer && grounded && Input.DriftHeld
                && throttle > 0.5f && Speed < burnoutMaxSpeed;
            if (burnoutHold)
            {
                burnoutCharge = Mathf.Min(burnoutCharge + dt, burnoutMaxCharge);
                Vector3 v = rb.linearVelocity;
                v.x *= 0.85f; v.z *= 0.85f;
                rb.linearVelocity = v;
            }
            else if (burnoutActive)
            {
                if (burnoutCharge >= burnoutMinCharge)
                {
                    float t = burnoutCharge / burnoutMaxCharge;
                    TriggerBoost(burnoutLaunchGain * t, 0.4f + 0.6f * t);
                }
                burnoutCharge = 0f;
            }
            burnoutActive = burnoutHold;

            // ================= MOTEUR =================
            if (grounded && !burnoutActive)
            {
                // pousse le long du sol (projete sur la pente) -> grimpe les rampes
                float force = throttle > 0f ? acceleration : reverseAcceleration;
                Vector3 driveDir = Vector3.ProjectOnPlane(transform.forward, groundNormalSmooth).normalized;
                rb.AddForce(driveDir * (throttle * force), ForceMode.Acceleration);
            }

            // Boost bouton
            boostTimer -= dt;
            boostCooldownTimer -= dt;
            if (boostQueued)
            {
                boostQueued = false;
                TriggerBoost(boostSpeedGain, boostDuration);
                boostCooldownTimer = boostCooldown;
            }
            // Mini-turbo : le drift charge tant qu'on glisse ; le boost ne part QU'A
            // LA RELACHE du bouton drift (Shift). Avant il partait des que `drifting`
            // s'eteignait -> en donut la vitesse dip sous 3 -> boost parasite mid-drift.
            if (drifting) driftTime += dt;
            bool driftBtn = Input.DriftHeld;
            if (wasDriftBtn && !driftBtn)
            {
                if (driftTime >= boostMinDriftTime)
                    TriggerBoost(boostImpulse, boostDuration * 0.6f);
                driftTime = 0f;
            }
            wasDriftBtn = driftBtn;

            // ================= FX (inchange) =================
            if (driftSmoke != null)
            {
                var emission = driftSmoke.emission;
                emission.rateOverTime = drifting ? driftSmokeRate
                    : burnoutActive ? driftSmokeRate * 1.6f : 0f;
            }
            if (skidTrail != null)
                skidTrail.emitting = grounded && (drifting || burnoutActive);
            if (boostFlames != null)
            {
                var emission = boostFlames.emission;
                emission.rateOverTime = boostTimer > 0f ? boostFlamesRate : 0f;
            }
            if (exhaustSmoke != null)
            {
                var emission = exhaustSmoke.emission;
                emission.rateOverTime = grounded && throttle > 0.1f ? exhaustAccelRate : exhaustIdleRate;
            }
            SetReverseLights(throttle < -0.1f);
            if (speedLines != null)
            {
                var emission = speedLines.emission;
                emission.rateOverTime = boostTimer > 0f ? speedLinesRate : 0f;
            }

            // ================= VITESSE / GRIP / DIRECTION =================
            // Clamp vitesse horizontale (verticale = suspension/gravite preservee)
            float maxNow = maxSpeed + (boostTimer > 0f ? boostExtraMaxSpeed : 0f);
            Vector3 flatVel = rb.linearVelocity;
            float yVel = flatVel.y; flatVel.y = 0f;
            if (flatVel.magnitude > maxNow) flatVel = flatVel.normalized * maxNow;
            flatVel.y = yVel;
            rb.linearVelocity = flatVel;

            // Grip lateral en FORCE : contre le glissement de cote aux roues. En drift
            // le grip s'effondre -> la caisse part en glisse (savonnette arcade).
            if (grounded)
            {
                float slip = Vector3.Dot(rb.linearVelocity, transform.right);
                float g = drifting ? tireGrip * driftGripMul : tireGrip;
                rb.AddForce(transform.right * (-slip * g), ForceMode.Acceleration);
            }

            // Chute plus mordante -> arc de saut snappy
            if (!grounded && rb.linearVelocity.y < 0f)
                rb.AddForce(Physics.gravity * (fallMultiplier - 1f), ForceMode.Acceleration);

            // ================= DIRECTION =================
            if (grounded && groundMode == GroundMode.FourPoint)
            {
                // 4 points : pitch/roll viennent des ressorts. On pilote UNIQUEMENT le taux de
                // lacet (yaw) autour de up, en gardant les composantes pitch/roll de l'angular
                // velocity. Le grip lateral courbe la vitesse -> la caisse tourne, la vitesse suit.
                Vector3 planarVel = Vector3.ProjectOnPlane(rb.linearVelocity, groundNormalSmooth);
                float speed = planarVel.magnitude;
                if (speed > minSpeedToSteer)
                {
                    float speedFactor = Mathf.Clamp01(speed / (maxSpeed * 0.5f));
                    float direction = Vector3.Dot(rb.linearVelocity, transform.forward) >= 0f ? 1f : -1f;
                    float steerMult = drifting ? driftSteerMultiplier : 1f;
                    float desiredYaw = steer * steerSpeed * steerMult * speedFactor * direction * Mathf.Deg2Rad; // rad/s
                    Vector3 up = transform.up;
                    Vector3 av = rb.angularVelocity;
                    float curYaw = Vector3.Dot(av, up);
                    rb.angularVelocity = av + up * (desiredYaw - curYaw); // impose le yaw, garde pitch/roll
                }
            }
            else if (grounded)
            {
                // MONO-ROUE : equilibrage actif. Orientation imposee (MoveRotation) = up aligne
                // a la normale sol + lacet integre. Tient la caisse droite (monocycle), marche en pente.
                Vector3 planarVel = Vector3.ProjectOnPlane(rb.linearVelocity, groundNormalSmooth);
                float speed = planarVel.magnitude;
                Vector3 heading = transform.forward;
                if (speed > minSpeedToSteer)
                {
                    float speedFactor = Mathf.Clamp01(speed / (maxSpeed * 0.5f));
                    float direction = Vector3.Dot(rb.linearVelocity, transform.forward) >= 0f ? 1f : -1f;
                    float steerMult = drifting ? driftSteerMultiplier : 1f;
                    float yaw = steer * steerSpeed * steerMult * speedFactor * direction * dt;
                    heading = Quaternion.AngleAxis(yaw, groundNormalSmooth) * heading; // tourne autour de la normale
                }
                Vector3 fwdOnGround = Vector3.ProjectOnPlane(heading, groundNormalSmooth);
                if (fwdOnGround.sqrMagnitude < 1e-4f)
                    fwdOnGround = Vector3.ProjectOnPlane(transform.forward, groundNormalSmooth);
                Quaternion target = Quaternion.LookRotation(fwdOnGround.normalized, groundNormalSmooth);
                rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, alignSpeed * dt));
                rb.angularVelocity = Vector3.zero; // equilibrage : pas de rotation parasite
            }
            else if (IsPlayer)
            {
                if (Input.TrickHeld)
                {
                    // MODE FIGURE (clic droit maintenu) : flips (throttle -> pitch) +
                    // spins (steer -> yaw), AUCUN redressement -> les figures persistent
                    // et un flip rate finit sur le toit (RATE honnete a l'impact). Seul
                    // ce mode alimente AirSpin/FlipDeg -> seules les figures voulues
                    // sont scorees par le TrickSystem.
                    float yaw = steer * airSpinSpeed * dt;
                    float pitch = throttle * airFlipSpeed * dt;
                    rb.MoveRotation(rb.rotation * Quaternion.Euler(pitch, yaw, 0f));
                    AirSpinDeg += yaw; AirFlipDeg += pitch;
                    DoingTrick = Mathf.Abs(steer) > 0.15f || Mathf.Abs(throttle) > 0.15f;
                }
                else
                {
                    // DEFAUT : rotation sur soi (yaw via A/D). throttle IGNORE -> pas de
                    // flip hors mode figure (c'etait le tilt parasite). Le spin sur soi
                    // reste une vraie figure SPIN (alimente AirSpinDeg -> detecte/score).
                    // Auto-redressement pitch/roll en gardant le cap -> retombe toujours
                    // sur les roues (le yaw survit, upright reprend son cap). Une SEULE
                    // MoveRotation par tick : on compose yaw + redressement avant d'appliquer.
                    bool spinning = Mathf.Abs(steer) > 0.15f;
                    DoingTrick = spinning;
                    Quaternion r = rb.rotation;
                    if (spinning)
                    {
                        float yaw = steer * airSpinSpeed * dt;
                        r *= Quaternion.Euler(0f, yaw, 0f);
                        AirSpinDeg += yaw;
                    }
                    Quaternion upright = Quaternion.Euler(0f, r.eulerAngles.y, 0f);
                    r = Quaternion.RotateTowards(r, upright, airLevelSpeed * dt);
                    rb.MoveRotation(r);
                }
            }
            else
            {
                float yaw = steer * airSteerSpeed * dt;
                rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yaw, 0f));
            }

            float signedSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
            UpdateVisualTilt(steer, signedSpeed, drifting);
        }

        // COSMETIQUE : la mono-roue est purement visuelle (la physique est aux 4 appuis).
        // On la pose au contact du sol par un ray central sous l'ancrage authored : au sol
        // elle touche, en l'air elle pend a wheelVisualDroop. Zero impact physique. Cible en
        // MONDE puis convertie en local -> gere scale/rotation du parent (Body 0.75). Lissee.
        private void PlantWheel()
        {
            if (wheelVisual == null) return;
            if (!useSuspension) { wheelVisual.localPosition = wheelRestLocalPos; return; }
            Transform p = wheelVisual.parent;
            Vector3 mountWorld = WheelMountWorld();
            float travel = wheelVisualDroop; // en l'air : roue pendante
            if (Physics.Raycast(mountWorld, -transform.up, out RaycastHit h,
                    wheelVisualDroop + wheelRadius, groundMask, QueryTriggerInteraction.Ignore)
                && h.collider.attachedRigidbody != rb)
                travel = Mathf.Clamp(h.distance - wheelRadius, 0f, wheelVisualDroop);
            if (!wheelYInit) { smoothWheelY = travel; wheelYInit = true; }
            smoothWheelY = Mathf.Lerp(smoothWheelY, travel, 1f - Mathf.Exp(-Time.deltaTime * 20f));
            Vector3 targetWorld = mountWorld - transform.up * smoothWheelY;
            wheelVisual.localPosition = p != null ? p.InverseTransformPoint(targetWorld) : targetWorld;
        }

        // Sonde le sol sous un coin (i : bit0 = droite, bit1 = avant) le long de la verticale
        // chassis. Renvoie le point de contact monde. Sert au fit de plan de l'assiette (mono).
        private bool ProbeCorner(int i, out Vector3 point)
        {
            Vector3 lc = new Vector3(
                ((i & 1) == 0 ? -1f : 1f) * alignProbeExtents.x,
                suspensionMountHeight,
                ((i & 2) == 0 ? -1f : 1f) * alignProbeExtents.y);
            // Origine LEVEE au-dessus du coin puis cast vers le bas : sinon une rampe qui
            // monte DEVANT est plus haute que l'origine plate -> le rayon la rate. En partant
            // d'en haut, la sonde voit la pente qui monte -> anticipation correcte.
            Vector3 origin = transform.TransformPoint(lc) + transform.up * alignProbeLength;
            if (Physics.Raycast(origin, -transform.up, out RaycastHit ah,
                    alignProbeLength * 2f, groundMask, QueryTriggerInteraction.Ignore)
                && ah.collider.attachedRigidbody != rb)
            { point = ah.point; return true; }
            point = Vector3.zero; return false;
        }

        // Ancrage visuel de la roue = sa position de repos authored, dans le monde
        // (gere le parent Body scale/rotation).
        private Vector3 WheelMountWorld()
        {
            if (wheelVisual == null) return transform.position;
            Transform p = wheelVisual.parent;
            return p != null ? p.TransformPoint(wheelRestLocalPos) : wheelRestLocalPos;
        }

        private void LateUpdate()
        {
            PlantWheel();
            // Spin roue seulement au sol : en l'air la vitesse avant persiste et la
            // roue "avancerait" toute seule. Hors sol -> gel de la pose.
            SpinWheel(Grounded ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f);

            // Feedback camera : FOV gonfle avec la vitesse + kick de boost.
            // Le punch decroit progressivement et la camera COURT apres la cible
            // (lerp rapide) -> montee violente mais pas instantanee.
            if (IsPlayer && followCam != null)
            {
                fovPunchValue = Mathf.Lerp(fovPunchValue, 0f, Time.deltaTime * 2.5f);
                float speedRatio = Mathf.Clamp01(rb.linearVelocity.magnitude / maxSpeed);
                float targetFov = baseFov + speedRatio * fovSpeedGain
                    + (boostTimer > 0f ? boostFovKick : 0f) + fovPunchValue;
                followCam.fieldOfView = Mathf.Lerp(followCam.fieldOfView, targetFov, Time.deltaTime * 9f);
            }
        }

        private void TriggerBoost(float speedGain, float duration)
        {
            rb.AddForce(transform.forward * speedGain, ForceMode.VelocityChange);
            boostTimer = Mathf.Max(boostTimer, duration);

            // --- le paquet de juice ---
            PunchScaleY(0.72f); // gros squat de lancement
            if (boostFlames != null) boostFlames.Emit(boostBurstCount);
            fovPunchValue = boostFovKick * boostFovPunch; // punch progressif via le lerp camera
            if (IsPlayer && followCamCtrl != null)
                followCamCtrl.Shake(boostShakeAmplitude, boostShakeDuration);
            if (boostRing != null) // onde de choc au sol (parentee au camion, elle suit)
                boostRing.Emit(1);
            if (driftSmoke != null && wheelVisual != null) // burnout sous la roue
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = wheelVisual.position + Vector3.down * (wheelRadius * 0.7f),
                    applyShapeToPosition = true
                };
                for (int i = 0; i < burnoutPuffCount; i++)
                    driftSmoke.Emit(p, 1);
            }
        }

        // Tilt purement cosmetique sur le child visuel : roulis dans le virage
        // (double en drift), penche en avant/arriere selon la vitesse signee
        // (feel mono-roue type gyropode). Zero impact physique.
        private void UpdateVisualTilt(float steer, float signedSpeed, bool drifting)
        {
            if (visualBody == null) return;

            currentSteer = Mathf.Lerp(currentSteer, steer, Time.fixedDeltaTime * tiltLerpSpeed);

            float rollMult = drifting ? 2f : 1f;
            float roll = -currentSteer * maxRollAngle * rollMult;
            float pitch = Mathf.Clamp(signedSpeed / maxSpeed, -1f, 1f) * maxPitchAngle;
            if (boostTimer > 0f) pitch += boostPitchLean; // pique du nez pendant le boost
            // gros angle visuel de derapage : le body pivote dans le sens du drift
            float yawOffset = drifting ? currentSteer * driftVisualYaw : 0f;

            Quaternion desired = Quaternion.Euler(pitch, yawOffset, roll);
            visualBody.localRotation = Quaternion.Slerp(visualBody.localRotation, desired, Time.fixedDeltaTime * tiltLerpSpeed);
        }

        // Squash & stretch via DOTween : pic rapide puis retour elastique.
        // Conservation de volume approx sur XZ.
        private void PunchScaleY(float peakY)
        {
            if (visualBody == null) return;
            scaleTween?.Kill();
            float sxz = 1f + (1f - peakY) * 0.5f;
            Vector3 peak = Vector3.Scale(visualBaseScale, new Vector3(sxz, peakY, sxz));
            scaleTween = DOTween.Sequence()
                .Append(visualBody.DOScale(peak, 0.08f).SetEase(Ease.OutQuad))
                .Append(visualBody.DOScale(visualBaseScale, 0.5f).SetEase(Ease.OutElastic, 1.05f));
        }

        // Emission des lentilles arriere via instance de materiau (.material) :
        // on ne salit pas l'asset partage, et on n'ecrit qu'au changement d'etat.
        private void SetReverseLights(bool on)
        {
            if (reverseLightsOn == on) return;
            reverseLightsOn = on;
            SetLamp(reverseLightRenderers, reverseLightSources, on, reverseLightColor * reverseLightIntensity);
        }

        private void SetHeadlights(bool on)
        {
            headlightsOn = on;
            SetLamp(headlightRenderers, headlightSources, on, headlightColor * headlightEmission);
        }

        // Lentilles (emission via instance .material, l'asset partage reste propre)
        // + vrais Light toggles ensemble.
        private void SetLamp(Renderer[] renderers, Light[] sources, bool on, Color emission)
        {
            if (renderers != null)
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    r.material.EnableKeyword("_EMISSION");
                    r.material.SetColor("_EmissionColor", on ? emission : Color.black);
                }
            if (sources != null)
                foreach (var l in sources)
                    if (l != null) l.enabled = on;
        }

        // Rotation de la roue unique proportionnelle a la vitesse au sol :
        // angle cumule applique en localRotation autour de l'axe d'essieu capture au spawn
        private void SpinWheel(float signedSpeed)
        {
            if (wheelVisual == null) return;
            wheelAngle += signedSpeed / wheelRadius * Mathf.Rad2Deg * Time.deltaTime;
            if (burnoutActive) wheelAngle += burnoutSpinVisual * Time.deltaTime; // patinage
            wheelAngle = Mathf.Repeat(wheelAngle, 360f);
            wheelVisual.localRotation = wheelRestLocalRot * Quaternion.AngleAxis(wheelAngle, wheelSpinAxisLocal);
        }
    }
}
