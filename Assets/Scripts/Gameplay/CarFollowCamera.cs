using UnityEngine;
using UnityEngine.InputSystem;

namespace Gameplay
{
    // Camera TPS : orbite autour de la cible controlee a la souris ou au stick droit
    // (yaw + pitch), distance fixe avec lissage. Curseur verrouille en jeu, Escape
    // pour liberer. Prototype pour tester le feel voiture.
    public class CarFollowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;

        [Header("Orbite")]
        [SerializeField] private float distance = 8f;
        [SerializeField] private float height = 1.5f; // point vise au dessus de la cible
        [SerializeField] private float mouseSensitivity = 0.15f; // deg par pixel de delta souris
        [SerializeField] private float stickSensitivity = 220f;  // deg par seconde a fond de stick droit
        [SerializeField] private float minPitch = -10f;
        [SerializeField] private float maxPitch = 70f;

        [Header("Collision")]
        [SerializeField] private LayerMask blockers = ~0;
        [SerializeField] private float probeRadius = 0.35f; // rayon du balayage, evite de raser les angles
        [SerializeField] private float minDistance = 1.6f;  // en deca on est dans le joueur
        [SerializeField] private float returnSpeed = 8f;    // m/s pour ressortir, en m/s

        private float yaw;
        private float pitch = 20f;
        private float shakeAmplitude;
        private float shakeTimer;
        private float currentDistance;
        private bool releasedForUi;   // c'est un ecran d'UI qui a rendu la souris, pas le joueur
        private readonly RaycastHit[] hits = new RaycastHit[8];

        // Secousse ponctuelle (boost, impacts). Decroit lineairement sur la duree.
        public void Shake(float amplitude, float duration)
        {
            shakeAmplitude = Mathf.Max(shakeAmplitude, amplitude);
            shakeTimer = Mathf.Max(shakeTimer, duration);
        }

        private void Start()
        {
            if (target != null)
                yaw = target.eulerAngles.y;
            currentDistance = distance;
            LockCursor(true);
        }

        private void LateUpdate()
        {
            if (target == null) return;

            // UN ECRAN D'UI A LA MAIN (pause, options, fin de run) : on REND la souris.
            // Sinon elle reste verrouillee au centre et invisible -- le joueur ne peut ni
            // survoler ni cliquer RETRY / QUIT. On coupe aussi le look : la camera n'a rien
            // a suivre, le jeu est gele derriere.
            if (UI.MenuFlow.Blocking || RunEnd.Finished)
            {
                if (Cursor.lockState == CursorLockMode.Locked) { LockCursor(false); releasedForUi = true; }
                return;
            }

            // Retour au jeu : on reprend la souris tout de suite. Sans ca, reprendre la partie
            // au clavier (Echap) laissait le curseur libre et le look souris mort jusqu'au
            // prochain clic. On ne re-verrouille que si c'est NOUS qui avions relache.
            if (releasedForUi) { releasedForUi = false; LockCursor(true); }

            HandleCursorToggle();

            if (Cursor.lockState == CursorLockMode.Locked && Mouse.current != null)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                yaw += delta.x * mouseSensitivity;
                pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, minPitch, maxPitch);
            }

            if (Gamepad.current != null)
            {
                // Stick = vitesse angulaire (deg/s), contrairement a la souris qui donne
                // deja un deplacement : sans deltaTime le look partirait en vrille au framerate.
                Vector2 look = Gamepad.current.rightStick.ReadValue() * (stickSensitivity * Time.deltaTime);
                yaw += look.x;
                pitch = Mathf.Clamp(pitch - look.y, minPitch, maxPitch);
            }

            Vector3 pivot = target.position + Vector3.up * height;
            Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 dir = orbit * Vector3.back;

            transform.position = pivot + dir * Distance(pivot, dir);
            transform.LookAt(pivot);

            if (shakeTimer > 0f)
            {
                shakeTimer -= Time.deltaTime;
                float falloff = Mathf.Clamp01(shakeTimer / 0.3f);
                float t = Time.time * 35f;
                Vector3 offset = new Vector3(
                    (Mathf.PerlinNoise(t, 0.5f) - 0.5f),
                    (Mathf.PerlinNoise(0.5f, t) - 0.5f),
                    0f) * (2f * shakeAmplitude * falloff);
                transform.position += transform.rotation * offset;
                if (shakeTimer <= 0f) shakeAmplitude = 0f;
            }
        }

        // Distance reelle de la camera : on balaie une sphere du pivot vers la
        // position voulue et on s'arrete au premier decor rencontre. Rentrer est
        // immediat (sinon la camera passe une frame DANS le mur), ressortir est
        // amorti (sinon elle claque des qu'on frole un poteau).
        private float Distance(Vector3 pivot, Vector3 dir)
        {
            float wanted = distance;

            int count = Physics.SphereCastNonAlloc(pivot, probeRadius, dir, hits, distance,
                                                   blockers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                // Les colliders du joueur englobent le pivot : sans ce filtre la
                // camera se collerait a lui en permanence. Un contact demarre a
                // l'interieur ressort d'ailleurs avec distance 0.
                if (hits[i].distance <= 0f) continue;
                if (hits[i].transform.IsChildOf(target)) continue;
                wanted = Mathf.Min(wanted, hits[i].distance);
            }

            wanted = Mathf.Max(minDistance, wanted);
            currentDistance = wanted < currentDistance
                ? wanted
                : Mathf.MoveTowards(currentDistance, wanted, returnSpeed * Time.deltaTime);
            return currentDistance;
        }

        private void HandleCursorToggle()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                LockCursor(false);
            if (Cursor.lockState != CursorLockMode.Locked
                && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                LockCursor(true);
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        public void SetTarget(Transform newTarget) => target = newTarget;
    }
}
