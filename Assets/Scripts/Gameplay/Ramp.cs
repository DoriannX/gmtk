using UnityEngine;
using System.Collections.Generic;

namespace Gameplay
{
    // Tremplin ARCADE : un trigger (BoxCollider isTrigger) qui lance toute voiture
    // entrant lancee dans le bon sens. Impulse propre (haut + avant) proportionnel
    // a la vitesse -> saut fiable, zero physique galere (pas de mesh solide a
    // grimper). Le mesh courbe reste purement visuel. Sert aussi de repere au RacerAI.
    public class Ramp : MonoBehaviour
    {
        [SerializeField] private float approachBack = 16f;   // point d'elan devant l'entree (RacerAI)
        [SerializeField] private float minLaunchSpeed = 5f;  // vitesse min le long de la montee pour lancer
        [SerializeField] private float launchRefSpeed = 18f; // vitesse "pleine" -> impulse max
        [SerializeField] private float launchUp = 10f;       // impulse vertical a pleine vitesse (m/s)
        [SerializeField] private float launchForward = 6f;   // impulse avant a pleine vitesse (m/s)
        [SerializeField] private float relaunchCooldown = 0.6f;

        private float cooldownUntil;

        private static readonly List<Ramp> All = new();
        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        public static Ramp Nearest(Vector3 p)
        {
            Ramp best = null; float bestSq = float.MaxValue;
            foreach (var r in All)
            {
                if (r == null) continue;
                float sq = (r.transform.position - p).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = r; }
            }
            return best;
        }

        // Point d'elan : en amont de l'entree (cote bas = -forward), au sol.
        public Vector3 ApproachPoint()
        {
            Vector3 p = transform.position - transform.forward * approachBack;
            p.y = 0f;
            return p;
        }

        private void OnTriggerEnter(Collider other) => TryLaunch(other);
        private void OnTriggerStay(Collider other) => TryLaunch(other); // filet si l'enter est rate a grande vitesse

        private void TryLaunch(Collider other)
        {
            if (Time.time < cooldownUntil) return;
            var car = other.GetComponentInParent<ArcadeCarController>();
            if (car == null) return;
            var rb = car.Body;
            if (rb == null) return;

            Vector3 up = transform.forward; // sens de la montee
            float along = Vector3.Dot(rb.linearVelocity, up);
            if (along < minLaunchSpeed) return; // trop lent ou mauvais sens

            cooldownUntil = Time.time + relaunchCooldown;
            float t = Mathf.Clamp01(along / launchRefSpeed);

            Vector3 v = rb.linearVelocity;
            if (v.y < 0f) v.y = 0f;
            rb.linearVelocity = v;
            rb.angularVelocity = Vector3.zero; // decolle a plat
            rb.AddForce(Vector3.up * (launchUp * (0.45f + 0.55f * t)) + up * (launchForward * t),
                ForceMode.VelocityChange);
            car.RampPop();
        }
    }
}
