using UnityEngine;

namespace Gameplay
{
    // Flammes de tuyere : les 4 reacteurs de la moto crachent quand on accelere, et
    // beaucoup plus fort pendant un boost.
    //
    // Deux canaux lisses separement plutot qu'un seul niveau : le gaz monte et descend
    // mollement (on veut une flamme qui respire), le boost doit claquer a l'allumage et
    // retomber un peu moins vite. Un seul lissage donnerait soit un gaz nerveux, soit un
    // boost sans impact.
    public class BikeThrusters : MonoBehaviour
    {
        [Header("Cibles")]
        [SerializeField] private ArcadeCarController car;
        [SerializeField] private ParticleSystem[] flames; // les 4 tuyeres

        // Quatre tuyeres tirent en meme temps : chacune doit rester DISCRETE, sinon les
        // panaches se recouvrent et l'outline les fond en une bouillie de facettes.
        // Vie courte + vitesse elevee = jet net plutot que nuage.
        [Header("Debit (par tuyere)")]
        [SerializeField] private float idleRate = 5f;
        [SerializeField] private float accelRate = 34f;
        [SerializeField] private float boostRate = 55f;

        [Header("Taille")]
        [SerializeField] private float idleSize = 0.03f;
        [SerializeField] private float accelSize = 0.075f;
        [SerializeField] private float boostSize = 0.09f;

        [Header("Vitesse d'ejection")]
        [SerializeField] private float idleSpeed = 2f;
        [SerializeField] private float accelSpeed = 7.5f;
        [SerializeField] private float boostSpeed = 11f;

        [Header("Duree de vie")]
        [SerializeField] private float idleLife = 0.09f;
        [SerializeField] private float accelLife = 0.18f;
        [SerializeField] private float boostLife = 0.22f;

        [Header("Lumiere projetee")]
        // Une seule lampe pour les 4 tuyeres : le pipeline plafonne a 8 lumieres
        // additionnelles par objet et la ville en aligne deja 112. Quatre lampes ici
        // evinceraient les neons de facade autour du joueur pour un gain invisible.
        [SerializeField] private Light glow;
        [SerializeField] private float idleIntensity = 0.6f;
        [SerializeField] private float accelIntensity = 2.6f;
        [SerializeField] private float boostIntensity = 9f;
        [SerializeField] private float idleRange = 2.5f;
        [SerializeField] private float accelRange = 5f;
        [SerializeField] private float boostRange = 11f;
        [SerializeField] private Color coolColor = new Color(0.30f, 0.85f, 1f);   // au ralenti : cyan, comme le coeur de flamme
        [SerializeField] private Color hotColor = new Color(1f, 0.25f, 0.85f);    // au boost : magenta, comme la queue
        [SerializeField] private float lightCutoff = 0.03f;

        [Header("Reponse")]
        [SerializeField] private float driveAttack = 9f;   // montee du gaz
        [SerializeField] private float driveRelease = 5f;  // retombee du gaz
        [SerializeField] private float boostAttack = 26f;  // le boost doit claquer
        [SerializeField] private float boostRelease = 6f;
        [SerializeField] private int boostBurst = 8;       // bouffee a l'allumage, par tuyere

        private float drive;   // 0..1, niveau de gaz lisse
        private float boost;   // 0..1, niveau de boost lisse
        private bool wasBoosting;

        private void Awake()
        {
            if (car == null) car = GetComponentInParent<ArcadeCarController>();
            if (car == null || flames == null || flames.Length == 0)
            {
                Debug.LogWarning($"{name} : BikeThrusters sans controleur ou sans tuyere, effet desactive.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // Marche arriere : pas de flamme. Le gaz negatif ne doit pas allumer les reacteurs.
            float driveTarget = Mathf.Clamp01(car.Throttle);
            drive = Damp(drive, driveTarget, driveTarget > drive ? driveAttack : driveRelease, dt);

            bool boosting = car.IsBoosting;
            boost = Damp(boost, boosting ? 1f : 0f, boosting ? boostAttack : boostRelease, dt);

            if (boosting && !wasBoosting) Burst();
            wasBoosting = boosting;

            // Le boost ecrase le gaz : a boost plein on est au maximum quel que soit le gaz.
            float rate = Mathf.Lerp(Mathf.Lerp(idleRate, accelRate, drive), boostRate, boost);
            float size = Mathf.Lerp(Mathf.Lerp(idleSize, accelSize, drive), boostSize, boost);
            float speed = Mathf.Lerp(Mathf.Lerp(idleSpeed, accelSpeed, drive), boostSpeed, boost);
            float life = Mathf.Lerp(Mathf.Lerp(idleLife, accelLife, drive), boostLife, boost);

            foreach (var ps in flames)
            {
                if (ps == null) continue;
                var emission = ps.emission;
                emission.rateOverTime = rate;
                var main = ps.main;
                // Courbes posees en entier (min ET max). Les modules sont en TwoConstants :
                // un simple startSizeMultiplier n'ecrase que la borne haute et laisse la
                // borne basse d'origine, ce qui donne des particules trop grosses et une
                // plage inversee des que la consigne descend sous cette borne.
                main.startSize = new ParticleSystem.MinMaxCurve(size * 0.55f, size);
                main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.75f, speed);
                main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.7f, life);
            }

            if (glow == null) return;
            // Le niveau lumineux suit la meme composition que les particules : le boost
            // ecrase le gaz. La teinte glisse du cyan au magenta avec le boost, pour que
            // la tache au sol raconte la meme chose que la flamme.
            float level = Mathf.Max(drive, boost);
            bool lit = level > lightCutoff;
            if (glow.enabled != lit) glow.enabled = lit;
            if (!lit) return;
            glow.intensity = Mathf.Lerp(Mathf.Lerp(idleIntensity, accelIntensity, drive), boostIntensity, boost);
            glow.range = Mathf.Lerp(Mathf.Lerp(idleRange, accelRange, drive), boostRange, boost);
            glow.color = Color.Lerp(coolColor, hotColor, boost);
        }

        private void Burst()
        {
            foreach (var ps in flames)
                if (ps != null) ps.Emit(boostBurst);
        }

        // Lissage exponentiel independant du framerate.
        private static float Damp(float current, float target, float rate, float dt)
            => Mathf.Lerp(current, target, 1f - Mathf.Exp(-rate * dt));
    }
}
