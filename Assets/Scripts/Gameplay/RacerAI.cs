using UnityEngine;

namespace Gameplay
{
    // "Fou de la course" : IA qui pilote une ArcadeCarController comme le joueur,
    // mais a fond — plein gaz, boosts reguliers, drifts dans les virages, et
    // fonce dans les tremplins pour sauter. Fournit les inputs via ICarInput ;
    // aucune physique ici, tout passe par le controller (feel identique joueur).
    [RequireComponent(typeof(ArcadeCarController))]
    public class RacerAI : MonoBehaviour, ICarInput
    {
        [Header("Terrain")]
        [SerializeField] private float mapBound = 85f;
        [SerializeField] private float arriveDist = 8f;        // distance pour valider une cible

        [Header("Conduite de fou")]
        [SerializeField] private float steerFullAngle = 35f;   // angle -> braquage max
        [SerializeField] private float driftAngle = 34f;       // au-dela : drift le virage
        [SerializeField] private Vector2 boostEvery = new Vector2(2f, 3.5f);
        [SerializeField] private float boostStraightAngle = 25f;// boost seulement si a peu pres droit

        [Header("Tremplins")]
        [SerializeField, Range(0f, 1f)] private float rampSeekChance = 0.55f;
        [SerializeField] private float rampBoostDist = 24f;    // fonce/boost vers le tremplin sous cette distance

        private ArcadeCarController car;
        private Vector3 target;
        private Ramp targetRamp;
        private float boostTimer;
        private bool boostPulse, jumpPulse;
        private float steerCmd, throttleCmd;
        private bool driftCmd;

        private void Awake()
        {
            car = GetComponent<ArcadeCarController>();
            car.Input = this;               // le controller lit MES inputs
            car.IsPlayer = false;           // IA : pas de camera/FOV, pas de shake/hitstop sur ses accidents
        }

        private void Start()
        {
            PickTarget();
            boostTimer = Random.Range(boostEvery.x, boostEvery.y);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            boostPulse = false; jumpPulse = false;

            Vector3 pos = transform.position;
            Vector3 to = target - pos; to.y = 0f;
            float dist = to.magnitude;

            // cible atteinte, ou sorti de la map -> nouvelle cible
            if (dist < arriveDist || Mathf.Abs(pos.x) > mapBound || Mathf.Abs(pos.z) > mapBound)
            {
                PickTarget();
                to = target - pos; to.y = 0f; dist = to.magnitude;
            }

            // steer : angle signe vers la cible dans le plan
            Vector3 fwd = transform.forward; fwd.y = 0f;
            float ang = fwd.sqrMagnitude > 1e-4f && to.sqrMagnitude > 1e-4f
                ? Vector3.SignedAngle(fwd.normalized, to.normalized, Vector3.up) : 0f;
            steerCmd = Mathf.Clamp(ang / steerFullAngle, -1f, 1f);
            throttleCmd = 1f; // plein gaz

            // drift dans les virages serres (sauf tout droit ou cible tres proche)
            driftCmd = Mathf.Abs(ang) > driftAngle && dist > arriveDist * 1.5f;

            // boost periodique quand c'est droit (sinon on part en tete-a-queue)
            boostTimer -= dt;
            if (boostTimer <= 0f && Mathf.Abs(ang) < boostStraightAngle)
            {
                boostPulse = true;
                boostTimer = Random.Range(boostEvery.x, boostEvery.y);
            }

            // approche tremplin : boost dans la montee + petit pop au decollage
            if (targetRamp != null && dist < rampBoostDist)
            {
                driftCmd = false;
                if (Mathf.Abs(ang) < 20f) boostPulse = true;
                if (dist < arriveDist * 1.3f) jumpPulse = true;
            }
        }

        private void PickTarget()
        {
            targetRamp = Random.value < rampSeekChance ? Ramp.Nearest(transform.position) : null;
            if (targetRamp != null)
                target = targetRamp.ApproachPoint();
            else
                target = new Vector3(Random.Range(-mapBound, mapBound), 0f, Random.Range(-mapBound, mapBound));
        }

        // ---- ICarInput ----
        public Vector2 Movement => new Vector2(steerCmd, throttleCmd);
        public bool DriftHeld => driftCmd;
        public bool JumpPressed => jumpPulse;
        public bool BoostPressed => boostPulse;
        public bool HeadlightPressed => false;
    }
}
