using UnityEngine;
using UnityEngine.InputSystem;

namespace Core
{
    // Wrapper de bas niveau autour du nouveau Input System (Keyboard.current / Mouse.current)
    // pour ne pas bloquer le dev pendant la jam en attendant la creation d'un vrai
    // .inputactions asset dans l'editeur. A terme, remplacer ce wrapper par un
    // InputActionAsset genere avec l'Input Actions Editor si le temps le permet.
    public static class InputActions
    {
        public static Vector2 GetMovementAxis()
        {
            if (Keyboard.current == null) return Vector2.zero;

            float x = 0f;
            float y = 0f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) y -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) y += 1f;

            // Pas de normalisation diagonale : throttle et steer sont des canaux
            // independants pour un vehicule (normaliser affamait la marche arriere
            // en virage : 0.707 * reverseAcceleration < friction sol -> stall).
            return new Vector2(x, y);
        }

        public static bool GetActionHeld()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.spaceKey.isPressed;
        }

        public static bool GetDriftHeld()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.leftShiftKey.isPressed;
        }

        public static bool GetJumpPressed()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.spaceKey.wasPressedThisFrame;
        }

        public static bool GetBoostPressed()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.eKey.wasPressedThisFrame || Keyboard.current.leftCtrlKey.wasPressedThisFrame;
        }

        public static bool GetActionPressed()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.spaceKey.wasPressedThisFrame || Keyboard.current.eKey.wasPressedThisFrame;
        }

        public static bool GetHeadlightPressed()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.fKey.wasPressedThisFrame;
        }

        // Modificateur "figures en l'air" : clic droit maintenu. Sans lui, l'air
        // control se limite a une rotation sur soi (yaw) ; avec, flips + spins.
        public static bool GetTrickHeld()
        {
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
        }

        public static bool GetPausePressed()
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.escapeKey.wasPressedThisFrame;
        }
    }
}
