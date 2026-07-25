using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Gameplay
{
    // Camera de vol libre pour visiter le Zoo (ou debug une scene). Clic droit maintenu
    // = regarder (souris), ZQSD/WASD = deplacer, E/Espace monte, A/Ctrl descend,
    // Shift = rapide, molette = ajuste la vitesse de base.
    // Utilise le new Input System (present : InputActions.cs est genere depuis lui).
    public class FlyCamera : MonoBehaviour
    {
        [SerializeField] private float speed = 12f;
        [SerializeField] private float boost = 4f;      // multiplicateur Shift
        [SerializeField] private float lookSensitivity = 0.12f;

        private float yaw, pitch;

        private void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            yaw = e.y; pitch = e.x;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // Regard : clic droit maintenu.
            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                yaw += d.x * lookSensitivity;
                pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            // Molette -> vitesse de base.
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                speed = Mathf.Clamp(speed * (scroll > 0 ? 1.1f : 0.9f), 1f, 300f);

            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed || kb.zKey.isPressed || kb.upArrowKey.isPressed) move += transform.forward;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move -= transform.forward;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += transform.right;
            if (kb.aKey.isPressed || kb.qKey.isPressed || kb.leftArrowKey.isPressed) move -= transform.right;
            if (kb.eKey.isPressed || kb.spaceKey.isPressed) move += Vector3.up;
            if (kb.leftCtrlKey.isPressed || kb.cKey.isPressed) move -= Vector3.up;

            float s = speed * (kb.leftShiftKey.isPressed ? boost : 1f);
            transform.position += move.normalized * s * Time.unscaledDeltaTime;
#endif
        }
    }

    // Oriente le label pour qu'il reste lisible face a la camera (aussi en editeur).
    [ExecuteAlways]
    public class Billboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Application.isPlaying ? Camera.main : null;
#if UNITY_EDITOR
            // En editeur, s'aligner sur la camera de la vue Scene.
            if (cam == null && UnityEditor.SceneView.lastActiveSceneView != null)
                cam = UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            // Copier la rotation camera -> le +Z du TextMesh regarde comme la camera
            // (texte a l'endroit, jamais en miroir).
            transform.rotation = cam.transform.rotation;
        }
    }
}
