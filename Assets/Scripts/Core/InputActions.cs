using UnityEngine;
using UnityEngine.InputSystem;

namespace Core
{
    // Wrapper de bas niveau autour du nouveau Input System (Keyboard.current /
    // Mouse.current / Gamepad.current) pour ne pas bloquer le dev pendant la jam en
    // attendant la creation d'un vrai .inputactions asset dans l'editeur. A terme,
    // remplacer ce wrapper par un InputActionAsset genere avec l'Input Actions Editor
    // si le temps le permet.
    //
    // Mapping manette (Xbox / Steam Deck) :
    //   stick gauche X + dpad     -> steer
    //   RT / LT (+ stick gauche Y)-> accelerer / freiner-reculer
    //   A                         -> saut (maintenu = super saut)
    //   LB                        -> drift / burnout
    //   B ou RB                   -> boost
    //   Y                         -> phares
    //   RT+LB non utilises en l'air : le modificateur figures est LB aussi (voir
    //   GetTrickHeld : au sol LB drifte, en l'air il debloque flips + spins).
    //   Start                     -> pause
    //   stick droit               -> camera (voir CarFollowCamera)
    public static class InputActions
    {
        // En dessous de ce seuil une gachette est consideree relachee (bruit analogique).
        private const float TriggerDeadzone = 0.08f;

        public static Vector2 GetMovementAxis()
        {
            float x = 0f;
            float y = 0f;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            }

            var pad = Gamepad.current;
            if (pad != null)
            {
                // Steer analogique : stick gauche, dpad en secours (tout ou rien).
                float padX = pad.leftStick.ReadValue().x;
                if (pad.dpad.left.isPressed) padX -= 1f;
                if (pad.dpad.right.isPressed) padX += 1f;

                // Throttle : gachettes d'abord (analogique), stick/dpad en secours pour
                // les manettes sans gachettes analogiques.
                float rt = pad.rightTrigger.ReadValue();
                float lt = pad.leftTrigger.ReadValue();
                if (rt < TriggerDeadzone) rt = 0f;
                if (lt < TriggerDeadzone) lt = 0f;
                float padY = rt - lt;
                if (padY == 0f) padY = pad.leftStick.ReadValue().y;
                if (pad.dpad.up.isPressed) padY += 1f;
                if (pad.dpad.down.isPressed) padY -= 1f;

                // Le clavier reste prioritaire s'il pousse plus fort : les deux
                // peripheriques restent utilisables en meme temps sans se voler l'axe.
                if (Mathf.Abs(padX) > Mathf.Abs(x)) x = padX;
                if (Mathf.Abs(padY) > Mathf.Abs(y)) y = padY;
            }

            // Pas de normalisation diagonale : throttle et steer sont des canaux
            // independants pour un vehicule (normaliser affamait la marche arriere
            // en virage : 0.707 * reverseAcceleration < friction sol -> stall).
            return new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
        }

        public static bool GetActionHeld()
        {
            return (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
                || (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed);
        }

        public static bool GetDriftHeld()
        {
            return (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed)
                || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);
        }

        public static bool GetJumpPressed()
        {
            return (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                || (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame);
        }

        public static bool GetJumpHeld()
        {
            return (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
                || (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed);
        }

        public static bool GetBoostPressed()
        {
            var kb = Keyboard.current;
            if (kb != null && (kb.eKey.wasPressedThisFrame || kb.leftCtrlKey.wasPressedThisFrame)) return true;
            var pad = Gamepad.current;
            return pad != null
                && (pad.buttonEast.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame);
        }

        public static bool GetActionPressed()
        {
            var kb = Keyboard.current;
            if (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame)) return true;
            var pad = Gamepad.current;
            return pad != null
                && (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame);
        }

        public static bool GetHeadlightPressed()
        {
            return (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
                || (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame);
        }

        // Modificateur "figures en l'air" : clic droit maintenu (ou LB a la manette,
        // le drift ne servant qu'au sol le partage de touche ne cree pas de conflit).
        // Sans lui, l'air control se limite a une rotation sur soi (yaw) ; avec, flips + spins.
        public static bool GetTrickHeld()
        {
            return (Mouse.current != null && Mouse.current.rightButton.isPressed)
                || (Gamepad.current != null && Gamepad.current.leftShoulder.isPressed);
        }

        public static bool GetPausePressed()
        {
            return (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);
        }
    }
}
