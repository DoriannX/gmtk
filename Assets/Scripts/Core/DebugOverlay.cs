using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Core
{
    // Overlay de diagnostic (build uniquement) : version du build en cours + etat brut
    // des peripheriques vus par l'Input System. Sert a trancher "le Deck n'expose
    // aucune manette" (lizard mode / Steam Input mal configure) contre "le code de
    // lecture du pad est casse" : sans ca chaque hypothese coute un aller-retour CI.
    //
    // S'auto-instancie au demarrage : aucun cablage de scene, donc rien a defaire
    // dans GymRoom.unity quand on le retirera.
    // Bascule : F3 au clavier, Select/View a la manette (si elle est detectee).
    public sealed class DebugOverlay : MonoBehaviour
    {
        private static bool visible = true;
        private readonly StringBuilder sb = new StringBuilder();
        private GUIStyle style;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Spawn()
        {
            var go = new GameObject("~DebugOverlay");
            go.AddComponent<DebugOverlay>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame) visible = !visible;
            if (Gamepad.current != null && Gamepad.current.selectButton.wasPressedThisFrame) visible = !visible;
        }

        private void OnGUI()
        {
            if (!visible) return;

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(14, Screen.height / 45),
                    richText = false,
                    alignment = TextAnchor.UpperLeft
                };
            }

            sb.Clear();
            sb.Append("BUILD ").Append(Application.version)
              .Append("  |  Unity ").Append(Application.unityVersion)
              .Append("  |  ").Append(Application.platform)
              .Append("  |  F3/Select pour masquer\n");

            sb.Append("devices: ");
            foreach (var d in InputSystem.devices)
                sb.Append('[').Append(d.GetType().Name).Append(" \"").Append(d.displayName).Append("\"] ");
            sb.Append('\n');

            var pad = Gamepad.current;
            if (pad == null)
            {
                sb.Append("Gamepad.current = NULL  -> aucune manette vue par l'Input System.\n");
            }
            else
            {
                Vector2 ls = pad.leftStick.ReadValue();
                Vector2 rs = pad.rightStick.ReadValue();
                sb.Append("Gamepad.current = ").Append(pad.displayName).Append('\n');
                sb.AppendFormat("LS {0:0.00},{1:0.00}   RS {2:0.00},{3:0.00}   LT {4:0.00}   RT {5:0.00}\n",
                    ls.x, ls.y, rs.x, rs.y, pad.leftTrigger.ReadValue(), pad.rightTrigger.ReadValue());
                sb.Append("boutons: ");
                if (pad.buttonSouth.isPressed) sb.Append("A ");
                if (pad.buttonEast.isPressed) sb.Append("B ");
                if (pad.buttonWest.isPressed) sb.Append("X ");
                if (pad.buttonNorth.isPressed) sb.Append("Y ");
                if (pad.leftShoulder.isPressed) sb.Append("LB ");
                if (pad.rightShoulder.isPressed) sb.Append("RB ");
                if (pad.startButton.isPressed) sb.Append("Start ");
                if (pad.dpad.ReadValue() != Vector2.zero) sb.Append("Dpad ");
                sb.Append('\n');
            }

            // Ce que le jeu lit vraiment, apres le melange clavier/pad : si cette ligne
            // reste a zero alors que LS bouge au dessus, le bug est dans InputActions.
            Vector2 mv = InputActions.GetMovementAxis();
            sb.AppendFormat("InputActions: move {0:0.00},{1:0.00}  drift {2}  jump {3}  boost {4}  trick {5}",
                mv.x, mv.y, InputActions.GetDriftHeld(), InputActions.GetJumpHeld(),
                InputActions.GetBoostPressed(), InputActions.GetTrickHeld());

            string text = sb.ToString();
            var rect = new Rect(12f, 10f, Screen.width - 24f, style.fontSize * 8f);
            var shadow = rect;
            shadow.x += 2f;
            shadow.y += 2f;
            var prev = GUI.color;
            GUI.color = Color.black;
            GUI.Label(shadow, text, style);
            GUI.color = Color.yellow;
            GUI.Label(rect, text, style);
            GUI.color = prev;
        }
    }
}
