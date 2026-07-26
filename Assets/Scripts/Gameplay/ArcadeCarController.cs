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
        [SerializeField] private float alignProbeLength = 1.2f; // levee + portee (descente) des sondes de plan : COURT -> hugge le sol, ne peut pas atteindre un tablier de pont au-dessus
        [SerializeField] private Vector2 alignProbeExtents = new Vector2(0.6f, 1.6f); // ecartement des sondes (x lateral, z avant/arriere) : juste au-dela du bumper -> anticipe la pente sans sonder trop loin

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

        [Header("Saut (maintien = charge -> super saut + spin)")]
        [SerializeField] private float jumpVelocity = 7f;         // saut de base (tap) : hauteur ~ v^2/2g
        [SerializeField] private float superJumpVelocity = 13f;   // saut a pleine charge
        [SerializeField] private float jumpChargeMax = 1.0f;      // temps de maintien pour la charge max
        [SerializeField] private float superSpinSpeed = 540f;     // vitesse du spin en l'air (deg/s)
        [SerializeField] private float coyoteTime = 0.12f;        // saute juste apres avoir quitte le sol
        [SerializeField] private float stretchOnJump = 1.35f;
        [SerializeField] private float squashOnLand = 0.65f;

        [Header("Juice super saut (cartoon punchy)")]
        [SerializeField] private float chargeSquashY = 0.55f;   // squash vertical a pleine charge (coile le ressort)
        [SerializeField] private float superStretch = 1.8f;     // gros stretch au lancement (BOING)
        [SerializeField] private float superLandSquash = 0.45f; // ecrasement a l'atterrissage du super saut
        [SerializeField] private float superShakeAmp = 0.4f;
        [SerializeField] private float superShakeDur = 0.45f;
        [SerializeField] private float superFovPunch = 2.8f;    // multiplicateur du kick FOV
        [SerializeField] private int superFlameBurst = 32;      // burst de flammes au decollage
        [SerializeField] private int superDustCount = 22;       // nuage de poussiere sous le camion
        [SerializeField] private float superHitstopScale = 0.45f; // creux de slow-mo au lancement (1 = off)

        [Header("Air control")]
        [SerializeField] private float airSteerSpeed = 150f; // deg/s en l'air (IA / yaw de base)

        [Header("Figures (air control joueur)")]
        [SerializeField] private float airSpinSpeed = 300f;  // yaw en l'air (spins) : Shift + A/D
        [SerializeField] private float airFlipSpeed = 300f;  // pitch (flip) : Shift + W/S
        [SerializeField] private float airLevelSpeed = 220f; // redressement auto (deg/s) hors mode figure -> retombe sur roues
        [SerializeField] private float landAlignDist = 3f; // portee COURTE du ray SOUS le camion pour pre-aligner l'atterrissage : petit -> ne chasse pas les surfaces lointaines/croisees
        [Header("Grind (rail)")]
        [SerializeField] private GrindRailNetwork railNet;     // reseau de rails bake ; auto-trouve dans la scene si vide
        [SerializeField] private float grindMinSpeed = 4f;     // en coast, sous ca -> tombe du rail
        [SerializeField] private float grindEnterSpeed = 0.8f; // vitesse MINI pour s'accrocher
        [SerializeField] private float grindGrab = 0.5f;       // distance MAX roue<->rail pour s'accrocher (petit -> pas de snap de loin qui gene les autres moves)
        [SerializeField, Range(0f, 1f)] private float grindAlign = 0.6f; // |dot(vitesse, rail)| mini pour accrocher (1 = pile parallele)
        [SerializeField] private float grindRideHeight = 0f;   // clearance en plus de wheelRadius (monter si la caisse clippe le rail)
        [SerializeField] private float grindThrottle = 30f;    // accel/frein le long du rail (gaz)
        [SerializeField] private float grindLean = 18f;        // roulis PHYSIQUE : la moto penche sur le cote pendant le grind (deg)
        [SerializeField] private float grindYawPose = 25f;     // pose FIXE : rotation sur l'axe Y MONDE (yaw) de la moto en grind (deg)
        [SerializeField] private float grindVisualLean = 22f;  // roulis VISUEL sur la caisse seule (cosmetique, en plus)
        [SerializeField] private float grindGravity = 1f;      // gravite projetee le long du rail (garde l'elan en descente)
        [SerializeField] private float grindJump = 8f;         // pop vertical de sortie (bail au saut)

        [Header("Grind equilibre")]
        [SerializeField] private float grindInstability = 7f;      // pendule inverse : + on penche + on tombe vite (bas = facile)
        [SerializeField] private float grindBalanceControl = 30f;  // autorite du steer : doit battre l'instabilite MEME au seuil (sinon rattrapage impossible = inutile)
        [SerializeField] private float grindBalanceDamp = 0.8f;    // amorti de la vitesse d'equilibre (overshoot maitrisable)
        [SerializeField] private float grindFallThreshold = 2.2f;  // |balance| au-dela duquel on tombe (haut = plus permissif)
        [SerializeField] private float grindBalanceLean = 30f;     // roll visuel de la moto au desequilibre max (deg)
        [SerializeField] private float grindEject = 4f;            // ejection laterale a la chute -> evite de se raccrocher aussitot

        [Header("Debug")]
        [SerializeField] private bool drawDebugGizmos = true; // dessine TOUTES les detections physiques (activer Gizmos dans la Game view)

        [Header("Saut / air")]
        [SerializeField] private float fallMultiplier = 1.8f;       // gravite x en chute -> arc snappy

        [Header("Visuel cartoon")]
        [SerializeField] private Transform visualBody; // child cosmetique, tilte sans toucher la physique
        [SerializeField] private Transform wheelVisual; // roue unique, tourne selon la vitesse
        [SerializeField] private Transform[] wheelExtraParts; // autres morceaux de la roue (jante, pneu...) : reparentes sous wheelVisual au reveil
        [SerializeField] private float wheelRadius = 0.51f;
        [SerializeField] private float wheelVisualDroop = 0.35f; // debattement VISUEL de la mono-roue deco (cosmetique, sans physique)
        [SerializeField] private float maxRollAngle = 18f; // roulis dans les virages
        [SerializeField] private float maxPitchAngle = 14f; // penche en avant quand roule en avant, en arriere en marche arriere
        [SerializeField] private float tiltLerpSpeed = 8f;
        [SerializeField] private float driftVisualYaw = 22f; // contre-braquage visuel du body en drift

        [Header("Boost")]
        [SerializeField] private float boostSpeedGain = 9f;      // impulse immediat
        [SerializeField] private float boostExtraMaxSpeed = 10f; // vitesse max relevee pendant le boost
        [SerializeField, Range(0f, 1f)] private float airBoostRedirect = 0.6f; // boost en l'air : part de la vitesse redirigee vers le nez (0 = pousse pure, 1 = dash plein nez)
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
        [SerializeField] private ParticleSystem boostRing;    // onde de choc SUPER SAUT (decollage/atterrissage), a plat au sol
        [SerializeField] private ParticleSystem boostGroundRing; // onde de choc du BOOST : VERTICALE, face avant, le camion la traverse
        [SerializeField] private float ringGroundOffset = 0f; // reglage fin : hauteur du ring sous le pied de la roue (super saut)
        [SerializeField] private float boostRingHeight = 0.8f; // hauteur du ring de boost sur la caisse (centre du vehicule)
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
        private float jumpCharge;    // temps de maintien du bouton saut (grounded) -> puissance
        private bool wasJumpHeld;    // detection de la relache (charge -> saute)
        private bool wasCharging;    // chargeait au tick precedent (pour relacher le squash sans saut)
        private bool superJumpActive; // super saut en cours -> gros juice a l'atterrissage
        private float airSpinBank;   // spin restant a jouer en l'air (offert par le super saut)
        private float coyoteTimer;
        private bool grinding;      // colle a un rail -> ecrase le pilotage normal
        private int grindPath;      // index du chemin dans le reseau
        private float grindS;       // arc-length courant sur le chemin
        private float grindSpeed;   // vitesse signee le long du chemin (sens +s)
        private float grindDirSign; // cap fixe : sens de la moto sur le rail (choisi a l'entree), independant du sens de deplacement
        private float grindBalance;       // equilibre -1..1, 0 = centre ; |x|>=1 -> chute
        private float grindBalanceVel;    // vitesse d'equilibre (inertie -> overshoot de l'autre cote)
        private float grindReattachTimer; // anti re-accroche apres une chute
        public bool OnRail => grinding;
        // Etat d'equilibre normalise pour l'UI : 0 = centre, +/-1 = seuil de chute.
        public float GrindBalanceNorm => grindFallThreshold > 0f ? grindBalance / grindFallThreshold : grindBalance;
        // Vitesse le long du rail (0 hors grind) -> le score grind se base sur la DISTANCE, pas le temps.
        public float GrindSpeedAbs => grinding ? Mathf.Abs(grindSpeed) : 0f;

        // Etat expose pour les interactions (roadkill pieton) : vitesse plane
        // et boost actif decident du niveau de reaction du pieton percute.
        public float Speed { get { var v = rb.linearVelocity; v.y = 0f; return v.magnitude; } }
        public bool IsBoosting => boostTimer > 0f;
        // Gaz brut du frame physique (-1 marche arriere .. +1 plein gaz), lu par les FX
        // de tuyere. Ecrit avant le court-circuit grind pour rester valide sur un rail.
        public float Throttle { get; private set; }
        public Rigidbody Body => rb;
        public void RampPop() => PunchScaleY(stretchOnJump); // juice quand un tremplin lance la voiture

        // --- Etat expose pour le TrickSystem (detection de figures) ---
        public bool Grounded { get; private set; }
        public bool Drifting { get; private set; }
        // rotation aerienne cumulee (deg signes), remise a zero au decollage. Integre la
        // rotation REELLE du corps (physique + input), pas juste l'input -> un flip lance
        // depuis une rampe verticale (camion deja incline au decollage) compte sa rotation
        // complete et l'atterrissage valide.
        public float AirSpinDeg { get; private set; }
        public float AirFlipDeg { get; private set; }
        private Quaternion airRotPrev = Quaternion.identity; // orientation du corps au tick precedent (pour integrer la rotation aerienne)
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
            if (railNet == null) railNet = FindAnyObjectByType<GrindRailNetwork>();
            // Suspension gere l'assiette : COM au centre (pas trop bas, sinon elle
            // se bat contre le pitch de la suspension). Amortissement angulaire pour
            // eviter le wobble, lineaire leger pour garder l'elan.
            rb.centerOfMass = new Vector3(0f, -0.15f, 0f);
            rb.linearDamping = groundDrag;
            rb.angularDamping = 3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // Sort la roue de visualBody -> le squash de la caisse ne la deforme plus. La roue etait
            // enfant d'un scale non-uniforme + rotation (spin) -> impossible a contre-scaler (shear).
            // Sous root (scale 1) elle reste ronde et plantee ; le squash n'affecte que les meshes caisse.
            if (wheelVisual != null && visualBody != null && wheelVisual.IsChildOf(visualBody))
                wheelVisual.SetParent(transform, true); // garde la transform monde (roue = petite-fille de Body)
            if (wheelVisual != null)
            {
                // Le modele decoupe la roue en plusieurs meshes (pneu, jante, moyeu...) : on les
                // colle sous wheelVisual pour qu'ils suivent spin + droop sans code en plus.
                if (wheelExtraParts != null)
                    foreach (Transform part in wheelExtraParts)
                        if (part != null && part != wheelVisual) part.SetParent(wheelVisual, true);
                wheelRestLocalRot = wheelVisual.localRotation;
                wheelRestLocalPos = wheelVisual.localPosition; // capture dans le nouveau parent (root)
                wheelSpinAxisLocal = wheelVisual.InverseTransformDirection(transform.right);
            }
            if (visualBody != null)
                visualBaseScale = visualBody.localScale;

            // BoostRing = onde de choc AU SOL : simulation MONDE (EmitParams.position lu en monde,
            // reste au sol) + alignment MONDE (le mesh du ring ne suit plus l'orientation du camion).
            // On l'emet ensuite couche a plat (rotation 90 sur X) -> normale vers le haut.
            SetupGroundRing(boostRing);
            SetupGroundRing(boostGroundRing);

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
            Throttle = throttle;

            // GRIND : si colle a une arete, physique de rail exclusive -> on court-circuite tout le reste.
            if (UpdateGrind(dt, throttle, steer)) return;

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

                    // ASSIETTE ANTICIPEE (PITCH seul) : 2 sondes sur l'axe CENTRAL (avant/arriere,
                    // x=0) -> anticipe la pente pour lever le nez. PAS de sonde laterale : sur une
                    // poutre / arete etroite, des sondes de cote taperaient le flanc ou le vide et
                    // feraient rouler la caisse doucement de cote. Le roll reste donne par la roue
                    // (equilibrage). Sonde seule (aucune force).
                    Vector3 fptC, rptC;
                    // Rejette une sonde separee du CONTACT ROUE reel par un ecart vertical trop grand
                    // (> pente ~45 deg sur la distance sondee) : c'est un tablier de pont / plafond /
                    // marche infranchissable au-dessus, pas la surface qu'on roule. Sinon le point avant
                    // sur le pont donnait un plan quasi vertical -> le camion se cabrait/flippait dessous.
                    Vector3 contact = mhit.point;
                    float maxStep = alignProbeExtents.y;
                    if (ProbeGround(0f, alignProbeExtents.y, out fptC)
                      & ProbeGround(0f, -alignProbeExtents.y, out rptC)
                      && Mathf.Abs(Vector3.Dot(fptC - contact, transform.up)) <= maxStep
                      && Mathf.Abs(Vector3.Dot(rptC - contact, transform.up)) <= maxStep)
                    {
                        // Roll depuis la NORMALE ROUE (baseN), pas depuis transform.right : sinon le roll
                        // s'auto-reference et ne se corrige jamais (on restait tilt en descendant de la
                        // poutre). Ici l'axe droit vient du sol -> le roll se recale toujours a plat.
                        Vector3 baseN = groundNormal;
                        Vector3 rightAxis = Vector3.Cross(baseN, transform.forward);
                        if (rightAxis.sqrMagnitude < 1e-4f) rightAxis = transform.right;
                        Vector3 fv = fptC - rptC;                        // avant - arriere -> pitch (anticipation)
                        Vector3 n = Vector3.Cross(fv, rightAxis.normalized);
                        if (Vector3.Dot(n, baseN) < 0f) n = -n;
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

            // ================= SAUT (maintien = charge -> super saut) =================
            // Maintenir le bouton au sol charge la puissance ; a la RELACHE on saute. Tap = saut
            // de base. Charge quasi PLEINE (>= superReq, 90% du max) -> SUPER saut (juice + hauteur,
            // pas de spin offert). Impulsion verticale nette -> hauteur previsible (v^2/2g).
            coyoteTimer = grounded ? coyoteTime : coyoteTimer - dt;
            bool jumpHeld = Input.JumpHeld;
            bool charging = grounded && jumpHeld;
            // Super saut = charge QUASI PLEINE (90% du max), pas un seuil bas absolu : un
            // appui normal ne doit PAS declencher le super. Ratio -> robuste quelle que soit
            // la valeur serialisee de jumpChargeMax sur le vehicule (moto, etc).
            float superReq = jumpChargeMax * 0.9f;
            if (charging)
            {
                jumpCharge = Mathf.Min(jumpCharge + dt, jumpChargeMax);
                // ANTICIPATION cartoon : la caisse se COMPRIME (coil) de plus en plus, et VIBRE
                // quand le super saut est arme -> lecture visuelle de la charge.
                if (visualBody != null)
                {
                    scaleTween?.Kill();
                    float c = Mathf.Clamp01(jumpCharge / superReq);
                    float sq = Mathf.Lerp(1f, chargeSquashY, c);
                    float sxz = 1f + (1f - sq) * 0.6f;
                    float wob = jumpCharge >= superReq ? Mathf.Sin(Time.unscaledTime * 55f) * 0.04f : 0f;
                    // Scale autour du pivot du body (a la base de la caisse) : la caisse s'ecrase
                    // vers le bas. La roue (enfant) est contre-scalee dans PlantWheel -> elle NE
                    // retrecit PAS et reste plantee au sol (c'est ce qui faisait "voler" avant).
                    visualBody.localScale = Vector3.Scale(visualBaseScale, new Vector3(sxz + wob, sq, sxz - wob));
                }
            }
            bool jumped = false;
            if (wasJumpHeld && !jumpHeld && coyoteTimer > 0f)
            {
                float t = Mathf.Clamp01(jumpCharge / jumpChargeMax);
                float vJump = Mathf.Lerp(jumpVelocity, superJumpVelocity, t);
                Vector3 v = rb.linearVelocity;
                if (v.y < 0f) v.y = 0f;
                rb.linearVelocity = v + Vector3.up * vJump;
                if (jumpCharge >= superReq)
                {
                    // ponytail: pas de spin offert (retire, c'etait nul). Le super saut = juice
                    // + hauteur, les figures restent au joueur (mode figure). airSpinBank reste 0.
                    superJumpActive = true;
                    SuperJumpJuice(); // gros paquet cartoon
                }
                else PunchScaleY(stretchOnJump);
                coyoteTimer = 0f;
                jumped = true;
            }
            if (wasCharging && !charging && !jumped && visualBody != null)
                PunchScaleY(1f); // relache sans sauter -> le ressort revient a sa forme
            if (!jumpHeld || !grounded) jumpCharge = 0f; // pas de charge en l'air / apres relache
            wasJumpHeld = jumpHeld;
            wasCharging = charging;

            // Juice atterrissage / decollage
            if (!wasGrounded && grounded)
            {
                // Aligne-t-on le UP a la SURFACE d'atterrissage (pas au vertical monde) ? Sur un
                // mur / une pente, atterrir roues-contre-la-surface est PROPRE : up ~ groundNormal
                // -> dot ~1. Raté = un flanc/le toit tape la surface -> dot faible. Robuste tout-sens.
                LandUprightDot = Vector3.Dot(transform.up, groundNormal);
                airSpinBank = 0f; // spin de super saut non termine -> pas de report au saut suivant
                // Atterrissage du SUPER saut = gros IMPACT cartoon (ecrase fort + shake + slow-mo).
                bool superLand = superJumpActive;
                superJumpActive = false;
                PunchScaleY(superLand ? superLandSquash : squashOnLand);
                Vector3 landPos = GroundUnderTruck();
                if (superLand)
                {
                    if (IsPlayer && followCamCtrl != null) followCamCtrl.Shake(superShakeAmp * 0.85f, superShakeDur * 0.7f);
                    Hitstop.Punch(0.5f, 0.03f, 0.14f);
                    EmitGroundRing(boostRing); // onde de choc super saut au pied de la roue
                }
                if (driftSmoke != null)
                {
                    var p = new ParticleSystem.EmitParams { position = landPos, applyShapeToPosition = true };
                    int puffs = superLand ? superDustCount : landPuffCount;
                    for (int i = 0; i < puffs; i++)
                        driftSmoke.Emit(p, 1);
                }
            }
            if (wasGrounded && !grounded)
            {
                AirSpinDeg = AirFlipDeg = 0f;
                airRotPrev = rb.rotation; // reference de rotation au decollage
            }
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
            // Mini-turbo : le drift charge tant qu'on glisse ; le boost ne part QU'A LA RELACHE du
            // bouton drift (Shift). GATE sur le gaz : le boost ne part que si on ACCELERE encore a
            // la relache -> pour l'annuler, lacher le gaz avant de relacher le drift. (Avant il partait
            // aussi quand on s'arretait de foncer, ce qui etait chiant.)
            if (drifting) driftTime += dt;
            bool driftBtn = Input.DriftHeld;
            if (wasDriftBtn && !driftBtn)
            {
                if (driftTime >= boostMinDriftTime && throttle > 0.1f)
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
            // Pendant un boost EN L'AIR : pas de clamp -> le dash dans la direction du nez survit
            // (redirige/accelere en 3D). Au sol ou hors boost : clamp horizontal normal.
            bool airBoosting = !grounded && boostTimer > 0f;
            float maxNow = maxSpeed + (boostTimer > 0f ? boostExtraMaxSpeed : 0f);
            Vector3 flatVel = rb.linearVelocity;
            float yVel = flatVel.y; flatVel.y = 0f;
            if (!airBoosting && flatVel.magnitude > maxNow) flatVel = flatVel.normalized * maxNow;
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
                // spin offert par le super saut : se consomme en l'air (yaw bonus, compte comme SPIN)
                float bankYaw = 0f;
                if (airSpinBank > 0f)
                {
                    bankYaw = Mathf.Min(superSpinSpeed * dt, airSpinBank);
                    airSpinBank -= bankYaw;
                }
                if (Input.TrickHeld)
                {
                    // MODE FIGURE (clic droit maintenu) : flips (throttle -> pitch) +
                    // spins (steer -> yaw), AUCUN redressement -> les figures persistent
                    // et un flip rate finit sur le toit (RATE honnete a l'impact). Seul
                    // ce mode alimente AirSpin/FlipDeg -> seules les figures voulues
                    // sont scorees par le TrickSystem.
                    float yaw = steer * airSpinSpeed * dt + bankYaw;
                    float pitch = throttle * airFlipSpeed * dt;
                    rb.MoveRotation(rb.rotation * Quaternion.Euler(pitch, yaw, 0f));
                    DoingTrick = Mathf.Abs(steer) > 0.15f || Mathf.Abs(throttle) > 0.15f;
                }
                else
                {
                    // DEFAUT : rotation sur soi (yaw via A/D) + spin du super saut. throttle IGNORE
                    // -> pas de flip hors mode figure. Le spin sur soi reste une vraie figure SPIN
                    // (alimente AirSpinDeg -> detecte/score). Auto-redressement pitch/roll en gardant
                    // le cap -> retombe sur les roues. Une SEULE MoveRotation : yaw + redressement composes.
                    bool spinning = Mathf.Abs(steer) > 0.15f;
                    DoingTrick = spinning || bankYaw != 0f;
                    Quaternion r = rb.rotation;
                    float yaw = (spinning ? steer * airSpinSpeed * dt : 0f) + bankYaw;
                    if (yaw != 0f)
                        r *= Quaternion.Euler(0f, yaw, 0f);
                    // Cible de redressement : aligne le UP a la normale de la surface qu'on va
                    // VRAIMENT toucher en tombant (ray court vers le bas, seulement en descente),
                    // en gardant le cap -> on retombe a plat sur la pente en dessous. Sinon (on monte,
                    // rien de proche dessous, ou surface non-posable) -> a plat monde. On ne s'aligne
                    // JAMAIS sur les murs/plafonds/formes croisees -> plus de rotations parasites en
                    // loop / au decollage sur les vagues / sous un pont.
                    Quaternion upright;
                    Vector3 landN;
                    if (PredictLandingNormal(out landN))
                    {
                        Vector3 head = Vector3.ProjectOnPlane(r * Vector3.forward, landN);
                        if (head.sqrMagnitude < 1e-4f) head = Vector3.ProjectOnPlane(transform.forward, landN);
                        upright = Quaternion.LookRotation(head.normalized, landN);
                    }
                    else
                    {
                        upright = Quaternion.Euler(0f, r.eulerAngles.y, 0f);
                    }
                    r = Quaternion.RotateTowards(r, upright, airLevelSpeed * dt);
                    rb.MoveRotation(r);
                }
            }
            else
            {
                float yaw = steer * airSteerSpeed * dt;
                rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yaw, 0f));
            }

            // SCORE FIGURES : integre la rotation REELLE du corps en l'air (rampe + physique +
            // input) et la decompose sur les axes locaux. SPIN (lacet) toujours compte ; FLIP
            // (tangage) seulement en mode figure (clic droit) pour garder "flips = voulus".
            // Orientation-independant -> un flip depuis une rampe verticale compte en entier.
            if (!grounded)
            {
                Quaternion delta = rb.rotation * Quaternion.Inverse(airRotPrev);
                float ang; Vector3 axis;
                delta.ToAngleAxis(out ang, out axis);
                if (axis.sqrMagnitude > 0.9f && Mathf.Abs(ang) > 0.0001f)
                {
                    if (ang > 180f) ang -= 360f; // plus court chemin
                    Vector3 rv = axis * ang;
                    AirSpinDeg += Vector3.Dot(rv, transform.up);
                    if (IsPlayer && Input.TrickHeld) AirFlipDeg += Vector3.Dot(rv, transform.right);
                }
            }
            airRotPrev = rb.rotation;

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

        // Sonde le sol sous un point local (x, mountHeight, z) le long de la verticale chassis.
        // Origine LEVEE puis cast vers le bas : sinon une rampe qui monte DEVANT est plus haute
        // que l'origine plate -> le rayon la rate. En partant d'en haut, la sonde voit la montee.
        private bool ProbeGround(float localX, float localZ, out Vector3 point)
        {
            Vector3 lc = new Vector3(localX, suspensionMountHeight, localZ);
            Vector3 origin = transform.TransformPoint(lc) + transform.up * alignProbeLength;
            if (Physics.Raycast(origin, -transform.up, out RaycastHit ah,
                    alignProbeLength * 2f, groundMask, QueryTriggerInteraction.Ignore)
                && ah.collider.attachedRigidbody != rb)
            { point = ah.point; return true; }
            point = Vector3.zero; return false;
        }

        // Normale de la surface d'atterrissage, pour le PRE-alignement en l'air. Uniquement vers
        // le BAS (la gravite nous y ramene), COURT (landAlignDist) et seulement en DESCENTE :
        //  - on monte (decollage) -> pas d'alignement (sinon on chasse les vagues sous nous) ;
        //  - surface trop loin -> pas d'alignement (sinon on chasse le sol lointain d'un loop) ;
        //  - surface non-posable (normal.y trop faible = mur/plafond) -> ignoree (sinon on
        //    s'aligne sur les murs du loop ou le dessous d'un pont).
        // Le VRAI alignement mur/pente se fait au CONTACT (roues au sol -> groundNormal), pas ici :
        // chasser les murs en l'air est incompatible avec ne pas paniquer en loop / pres d'un mur.
        private bool PredictLandingNormal(out Vector3 normal)
        {
            normal = Vector3.up;
            if (rb.linearVelocity.y > 0.5f) return false; // en montee : on ne s'aligne pas
            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit h, landAlignDist,
                    groundMask, QueryTriggerInteraction.Ignore)
                && h.collider.attachedRigidbody != rb && h.normal.y > 0.35f)
            { normal = h.normal; return true; }
            return false;
        }

        // Point de CONTACT ROUE / SOL (pour centrer les FX au sol : onde de choc, poussiere).
        // Raycast vers le bas depuis la roue (pas le centre du camion, qui est decale). Fallback
        // = bas de la roue si rien touche.
        private Vector3 GroundUnderTruck()
        {
            Vector3 origin = wheelVisual != null ? wheelVisual.position : transform.position;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit h, 6f,
                    groundMask, QueryTriggerInteraction.Ignore)
                && h.collider.attachedRigidbody != rb)
                return h.point;
            return origin + Vector3.down * wheelRadius;
        }

        // ================= GRIND (rail) =================
        // Aimante la moto sur la ligne d'arete detectee (GrindDetector) et la fait glisser le
        // long, comme un rail de skate. ENTREE : CanGrind (arete + nez ~parallele) + assez de
        // vitesse. SORTIE : fin de rail (plus d'arete), trop lent, ou SAUT (bail avec pop). Tant
        // qu'on grind, on ecrase le pilotage normal (return true -> FixedUpdate s'arrete la).
        private bool UpdateGrind(float dt, float throttle, float steer)
        {
            if (railNet == null) return false;
            if (grindReattachTimer > 0f) grindReattachTimer -= dt;
            Vector3 wheelMount = WheelMountWorld();

            // ---- ATTACHE : cherche le rail bake le plus proche + assez aligne ----
            if (!grinding)
            {
                if (grindReattachTimer > 0f) return false; // vient de tomber -> pas de re-accroche immediate
                if (rb.linearVelocity.magnitude < grindEnterSpeed) return false;
                // mesure depuis le BAS de roue (contact), pas le centre : sinon la hauteur de roue
                // (~wheelRadius) mangeait deja tout le budget -> les bords de box ne s'accrochaient plus.
                Vector3 wheelBottom = wheelMount - Vector3.up * wheelRadius;
                if (!railNet.QueryNearest(wheelBottom, grindGrab, out int path, out float s, out _, out Vector3 tan))
                    return false;
                Vector3 vDir = rb.linearVelocity.normalized;
                if (Mathf.Abs(Vector3.Dot(vDir, tan)) < grindAlign) return false; // pas assez parallele
                grinding = true;
                grindPath = path;
                grindS = s;
                grindSpeed = Vector3.Dot(rb.linearVelocity, tan); // signe = sens de parcours sur le chemin
                grindDirSign = grindSpeed >= 0f ? 1f : -1f;        // cap fige : la moto gardera ce sens meme en reculant
                grindBalance = (UnityEngine.Random.value < 0.5f ? -1f : 1f) * UnityEngine.Random.Range(0.3f, 0.45f); // desequilibre initial notable -> a rattraper direct
                grindBalanceVel = 0f;
                rb.isKinematic = true; // pilotage 100% chemin -> le solver ne peut plus se battre (zero jitter/enfoncement)
            }

            // ---- SUIT le chemin par arc-length ----
            grindSpeed += throttle * grindThrottle * dt * grindDirSign;  // gaz relatif au CAP moto (sinon inverse sur les rails orientes -s)
            grindS += grindSpeed * dt;                                   // avance sur le chemin

            // EQUILIBRE (pendule inverse) : sans rien faire on penche de + en + du cote ou on penche.
            // Le STEER rattrape ; l'inertie fait repartir de l'autre cote (overshoot) -> a gerer.
            grindBalanceVel += grindBalance * grindInstability * dt;   // instabilite (proportionnelle a la penche)
            grindBalanceVel -= steer * grindBalanceControl * dt;       // correction joueur (steer vers le cote oppose a la penche)
            grindBalanceVel *= Mathf.Max(0f, 1f - grindBalanceDamp * dt);
            grindBalance += grindBalanceVel * dt;

            // on decroche au BOUT du rail, au SAUT, ou si l'equilibre est perdu (|balance|>=1). Pas de
            // decrochage a l'arret : on peut rester immobile tant qu'on tient l'equilibre.
            bool bail = Input.JumpHeld;
            bool fell = Mathf.Abs(grindBalance) >= grindFallThreshold;
            bool onPath = railNet.Sample(grindPath, grindS, out Vector3 pt, out Vector3 tang);
            if (!onPath || bail || fell)
            {
                // sortie : rend le rb dynamique et relance la vitesse le long du rail (garde l'elan).
                railNet.Sample(grindPath, Mathf.Clamp(grindS, 0f, railNet.Length(grindPath)), out _, out Vector3 exitTan);
                float sp = Mathf.Max(Mathf.Abs(grindSpeed), grindEnterSpeed);
                rb.isKinematic = false;
                Vector3 outVel = exitTan * (grindSpeed >= 0f ? 1f : -1f) * sp;
                if (bail) outVel += Vector3.up * grindJump;
                if (fell)
                {
                    // EJECTE sur le cote (sens de la chute) + un peu en l'air -> on ne se raccroche pas aussitot
                    Vector3 side = Vector3.Cross(Vector3.up, exitTan).normalized;
                    outVel += side * (Mathf.Sign(grindBalance) * grindEject) + Vector3.up * (grindEject * 0.25f);
                    grindReattachTimer = 0.5f;
                }
                rb.linearVelocity = outVel;
                grinding = false;
                return false;
            }

            // gravite projetee sur le rail -> garde/prend de l'elan en descente
            grindSpeed += Vector3.Dot(Physics.gravity, tang) * grindGravity * dt; // pente -> elan
            // BOOST pendant le grind (le pilotage normal est court-circuite ici, on le gere nous-memes)
            boostTimer -= dt; boostCooldownTimer -= dt;
            if (boostQueued)
            {
                boostQueued = false;
                TriggerBoost(boostSpeedGain, boostDuration);   // flammes/FOV/shake + boostTimer
                boostCooldownTimer = boostCooldown;
                grindSpeed += boostSpeedGain * grindDirSign;    // vraie poussee le long du rail
            }
            // MEME plafond qu'au sol (+ rallonge de boost) -> pas plus rapide que la conduite normale
            float maxNow = maxSpeed + (boostTimer > 0f ? boostExtraMaxSpeed : 0f);
            grindSpeed = Mathf.Clamp(grindSpeed, -maxNow, maxNow);

            // CAP FIXE : la moto garde le sens choisi a l'entree (pas le signe de la vitesse) ->
            // freiner/reculer = glisse en arriere SANS retourner la moto (comme au sol).
            Vector3 facing = tang * grindDirSign;

            // POSE la roue SUR le rail (roue = wheelRadius au-dessus du point), corps au-dessus
            Vector3 desiredMount = pt + Vector3.up * (wheelRadius + grindRideHeight);
            rb.MovePosition(rb.position + (desiredMount - wheelMount));

            // ORIENTATION : yaw pose fixe (axe Y monde) + ROLL = etat d'equilibre (la moto penche selon
            // grindBalance -> le joueur VOIT le desequilibre et rattrape au steer).
            Vector3 facingYawed = Quaternion.AngleAxis(grindYawPose, Vector3.up) * facing;
            Vector3 balUp = Quaternion.AngleAxis(grindBalance * grindBalanceLean, facingYawed) * Vector3.up;
            Quaternion target = Quaternion.LookRotation(facingYawed, balUp);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, alignSpeed * dt));

            Grounded = true; Drifting = false; wasGrounded = true;
            // POSE VISUELLE de grind : la caisse s'incline sur le cote (roulis local z), cosmetique.
            if (visualBody != null)
            {
                Quaternion pose = Quaternion.Euler(0f, 0f, grindVisualLean);
                visualBody.localRotation = Quaternion.Slerp(visualBody.localRotation, pose, Time.fixedDeltaTime * tiltLerpSpeed);
            }
            return true;
        }

        // ============ DEBUG : dessine TOUTES les detections physiques du controleur ============
        // Chaque cast est REJOUE ici (lecture seule) avec la transform courante -> on voit
        // exactement ce que chaque sonde touche. Rouge sur un hit = surface accrochee. Activer
        // "Gizmos" dans la Game view en Play pour voir en jeu. Legende (couleur du rayon) :
        //  JAUNE = suspension (spherecast mono OU 4 rays coins) | CYAN = sondes d'assiette (pitch)
        //  MAGENTA = ray sol robuste (anti-toit) | VERT = pre-alignement atterrissage | GRIS = roue
        //  visuelle | BLEU = point FX au sol.
        private void OnDrawGizmos()
        {
            if (!drawDebugGizmos) return;
            if (rb == null) rb = GetComponent<Rigidbody>();
            Vector3 up = transform.up;
            // --- suspension ---
            if (groundMode == GroundMode.FourPoint)
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector3 local = new Vector3(
                        ((i & 1) == 0 ? -1f : 1f) * suspensionHalfExtents.x,
                        suspensionMountHeight,
                        ((i & 2) == 0 ? -1f : 1f) * suspensionHalfExtents.y);
                    GizmoCast(transform.TransformPoint(local), -up, suspensionRest + 0.1f, 0f, Color.yellow, "susp");
                }
            }
            else
            {
                float castLift = wheelRadius + monoTravel;
                Vector3 mountWorld = WheelMountWorld();
                GizmoCast(mountWorld + up * castLift, -up, castLift + monoTravel + 0.1f, wheelRadius, Color.yellow, "susp-mono");
            }
            // --- sondes d'assiette (pitch central avant/arriere) ---
            GizmoCast(transform.TransformPoint(new Vector3(0f, suspensionMountHeight, alignProbeExtents.y)) + up * alignProbeLength,
                -up, alignProbeLength * 2f, 0f, Color.cyan, "assiette-av");
            GizmoCast(transform.TransformPoint(new Vector3(0f, suspensionMountHeight, -alignProbeExtents.y)) + up * alignProbeLength,
                -up, alignProbeLength * 2f, 0f, Color.cyan, "assiette-ar");
            // --- ray sol robuste (anti-toit/flanc), n'agit que si incline mais on le montre toujours ---
            GizmoCast(transform.position, Vector3.down, suspensionRest + 0.4f, 0f, Color.magenta, "sol-robuste");
            // --- pre-alignement atterrissage (air, descente, court) ---
            GizmoCast(transform.position, Vector3.down, landAlignDist, 0f, Color.green, "pre-align");
            // --- roue visuelle (cosmetique) ---
            if (wheelVisual != null)
                GizmoCast(WheelMountWorld(), -up, wheelVisualDroop + wheelRadius, 0f, new Color(0.6f, 0.6f, 0.6f), "roue");
            // --- point FX au sol ---
            Vector3 fxOrigin = wheelVisual != null ? wheelVisual.position : transform.position;
            GizmoCast(fxOrigin, Vector3.down, 6f, 0f, Color.blue, "fx-sol");
        }

        // Rejoue un cast (ray si radius=0, sinon spherecast) et le dessine : ligne coloree, spheres
        // aux extremites si spherecast, et en ROUGE le hit + sa normale (la surface accrochee).
        private void GizmoCast(Vector3 origin, Vector3 dir, float dist, float radius, Color col, string label)
        {
            dir = dir.normalized;
            bool hit;
            RaycastHit h;
            if (radius > 0f)
                hit = Physics.SphereCast(origin, radius, dir, out h, dist, groundMask, QueryTriggerInteraction.Ignore)
                    && (rb == null || h.collider.attachedRigidbody != rb);
            else
                hit = Physics.Raycast(origin, dir, out h, dist, groundMask, QueryTriggerInteraction.Ignore)
                    && (rb == null || h.collider.attachedRigidbody != rb);
            Vector3 end = hit ? h.point : origin + dir * dist;
            Gizmos.color = col;
            Gizmos.DrawLine(origin, end);
            if (radius > 0f)
            {
                Gizmos.DrawWireSphere(origin, radius);
                Gizmos.DrawWireSphere(end, radius);
            }
            if (hit)
            {
                Gizmos.color = Color.red; // hit = surface accrochee par cette sonde
                Gizmos.DrawSphere(h.point, 0.08f);
                Gizmos.DrawLine(h.point, h.point + h.normal * 0.7f); // normale accrochee
#if UNITY_EDITOR
                UnityEditor.Handles.color = col;
                UnityEditor.Handles.Label(h.point + h.normal * 0.75f, label);
#endif
            }
        }

        // Prepare une onde de choc "au sol" : detachee du camion (sinon Emit() sans position
        // spawn au centre du camion), simulee en MONDE (reste posee), alignee monde, et sans
        // velocity-over-lifetime (le VOL faisait deriver l'onde en grandissant -> centre glissait).
        private static void SetupGroundRing(ParticleSystem ring)
        {
            if (ring == null) return;
            ring.transform.SetParent(null, true);
            var main = ring.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var vol = ring.velocityOverLifetime;
            vol.enabled = false;
            var rend = ring.GetComponent<ParticleSystemRenderer>();
            if (rend != null) rend.alignment = ParticleSystemRenderSpace.World;
        }

        // Onde de choc de BOOST : ring VERTICAL centre sur la caisse, plan = plan XY du camion
        // (normale = forward) -> le camion la traverse en accelerant. World-sim + VOL off : reste
        // en place, le vehicule fonce a travers.
        private void EmitBoostRing()
        {
            if (boostGroundRing == null) return;
            Vector3 pos = transform.position + transform.up * boostRingHeight;
            var rp = new ParticleSystem.EmitParams { position = pos, rotation3D = transform.eulerAngles };
            boostGroundRing.Emit(rp, 1);
        }

        // Onde de choc a plat, centree au PIED de la roue = position roue - rayon (+ offset de reglage).
        // Simple calcul de hauteur, pas de raycast : le ring est deja World-sim, on lui passe la position monde.
        private void EmitGroundRing(ParticleSystem ring)
        {
            if (ring == null) return;
            Vector3 c = wheelVisual != null ? wheelVisual.position : transform.position;
            Vector3 foot = c + Vector3.down * (wheelRadius + ringGroundOffset);
            var rp = new ParticleSystem.EmitParams { position = foot, rotation3D = new Vector3(90f, 0f, 0f) };
            ring.Emit(rp, 1);
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
            if (Grounded)
            {
                rb.AddForce(transform.forward * speedGain, ForceMode.VelocityChange);
            }
            else
            {
                // EN L'AIR : vrai dash 3D dans la direction du NEZ. Redirige une partie de la
                // vitesse vers l'avant du camion (le boost pousse ou pointe le nez, y compris en
                // montee) puis ajoute le gain. Le clamp horizontal est desactive pendant un boost
                // aerien (plus bas) -> le dash n'est pas mange.
                Vector3 v = rb.linearVelocity;
                Vector3 redirected = Vector3.Lerp(v, transform.forward * v.magnitude, airBoostRedirect);
                rb.linearVelocity = redirected + transform.forward * speedGain;
            }
            boostTimer = Mathf.Max(boostTimer, duration);

            // --- le paquet de juice ---
            PunchScaleY(0.72f); // gros squat de lancement
            if (boostFlames != null) boostFlames.Emit(boostBurstCount);
            fovPunchValue = boostFovKick * boostFovPunch; // punch progressif via le lerp camera
            if (IsPlayer && followCamCtrl != null)
                followCamCtrl.Shake(boostShakeAmplitude, boostShakeDuration);
            EmitBoostRing(); // onde de choc BOOST verticale, face avant
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
            // Scale autour du pivot (base de la caisse). La roue est contre-scalee dans PlantWheel.
            scaleTween = DOTween.Sequence()
                .Append(visualBody.DOScale(peak, 0.08f).SetEase(Ease.OutQuad))
                .Append(visualBody.DOScale(visualBaseScale, 0.5f).SetEase(Ease.OutElastic, 1.05f));
        }

        // Gros paquet de juice cartoon au DECOLLAGE du super saut : stretch violent, shake camera,
        // punch FOV, micro slow-mo (OOMPH), onde de choc + flammes + nuage de poussiere sous la caisse.
        private void SuperJumpJuice()
        {
            PunchScaleY(superStretch);                                  // BOING vertical
            if (IsPlayer && followCamCtrl != null) followCamCtrl.Shake(superShakeAmp, superShakeDur);
            fovPunchValue = boostFovKick * superFovPunch;               // punch FOV progressif
            if (superHitstopScale < 1f) Hitstop.Punch(superHitstopScale, 0.04f, 0.16f); // freeze-frame
            if (boostFlames != null) boostFlames.Emit(superFlameBurst); // gerbe de flammes
            // onde de choc + poussiere AU SOL, centrees au pied de la roue
            Vector3 groundPos = GroundUnderTruck();
            EmitGroundRing(boostRing);
            if (driftSmoke != null)
            {
                var ep = new ParticleSystem.EmitParams { position = groundPos, applyShapeToPosition = true };
                for (int i = 0; i < superDustCount; i++) driftSmoke.Emit(ep, 1);
            }
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
