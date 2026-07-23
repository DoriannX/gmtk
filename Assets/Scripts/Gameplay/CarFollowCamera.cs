using UnityEngine;
using UnityEngine.InputSystem;

namespace Gameplay
{
    // Camera TPS : orbite autour de la cible controlee a la souris (yaw + pitch),
    // distance fixe avec lissage. Curseur verrouille en jeu, Escape pour liberer.
    // Prototype pour tester le feel voiture.
    public class CarFollowCamera : MonoBehaviour
    {
        [SerializeField] private Transform target;

        [Header("Orbite")]
        [SerializeField] private float distance = 8f;
        [SerializeField] private float height = 1.5f; // point vise au dessus de la cible
        [SerializeField] private float mouseSensitivity = 0.15f; // deg par pixel de delta souris
        [SerializeField] private float minPitch = -10f;
        [SerializeField] private float maxPitch = 70f;

        private float yaw;
        private float pitch = 20f;
        private float shakeAmplitude;
        private float shakeTimer;

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
            LockCursor(true);
        }

        private void LateUpdate()
        {
            if (target == null) return;

            HandleCursorToggle();

            if (Cursor.lockState == CursorLockMode.Locked && Mouse.current != null)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                yaw += delta.x * mouseSensitivity;
                pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, minPitch, maxPitch);
            }

            Vector3 pivot = target.position + Vector3.up * height;
            Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
            transform.position = pivot + orbit * (Vector3.back * distance);
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
