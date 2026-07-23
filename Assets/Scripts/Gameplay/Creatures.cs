using UnityEngine;

namespace Gameplay
{
    // Hub d'evenement : une creature (pigeon, pieton...) vient d'etre tuee a `pos`.
    // Les temoins (pietons) s'y abonnent pour reagir (horreur + fuite).
    public static class Creatures
    {
        public static event System.Action<Vector3> Killed;
        public static void ReportKill(Vector3 pos) => Killed?.Invoke(pos);
    }
}
