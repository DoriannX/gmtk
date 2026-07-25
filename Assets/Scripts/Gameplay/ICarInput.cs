using UnityEngine;

namespace Gameplay
{
    // Source d'inputs d'une ArcadeCarController. Le joueur lit le clavier ;
    // un RacerAI fournit les memes canaux pour piloter la meme voiture physique.
    public interface ICarInput
    {
        Vector2 Movement { get; }   // x = steer, y = throttle
        bool DriftHeld { get; }
        bool JumpPressed { get; }
        bool JumpHeld { get; }      // maintenu = charge le super saut ; relache = declenche
        bool BoostPressed { get; }
        bool HeadlightPressed { get; }
        bool TrickHeld { get; }   // modificateur figures en l'air (clic droit) : sans -> yaw seul
    }

    // Source par defaut : le clavier du joueur (wrappe Core.InputActions).
    public sealed class KeyboardCarInput : ICarInput
    {
        public static readonly KeyboardCarInput I = new KeyboardCarInput();
        public Vector2 Movement => Core.InputActions.GetMovementAxis();
        public bool DriftHeld => Core.InputActions.GetDriftHeld();
        public bool JumpPressed => Core.InputActions.GetJumpPressed();
        public bool JumpHeld => Core.InputActions.GetJumpHeld();
        public bool BoostPressed => Core.InputActions.GetBoostPressed();
        public bool HeadlightPressed => Core.InputActions.GetHeadlightPressed();
        public bool TrickHeld => Core.InputActions.GetTrickHeld();
    }
}
