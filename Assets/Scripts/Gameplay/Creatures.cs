using UnityEngine;

namespace Gameplay
{
    // Hub d'evenement : une creature (pigeon, pieton...) vient d'etre tuee a `pos`.
    // Les temoins (pietons) s'y abonnent pour reagir (horreur + fuite), et ScoreGauge
    // pour retirer de la popularite.
    // `byPlayer` distingue le carnage du JOUEUR de celui d'une voiture IA : seul le
    // premier coute des points, sinon une collision entre PNJ punirait le joueur.
    public static class Creatures
    {
        public static event System.Action<Vector3, bool> Killed;

        public static void ReportKill(Vector3 pos, bool byPlayer = true) => Killed?.Invoke(pos, byPlayer);
    }
}
