using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Ralenti d'impact ("hitstop" facon slow-mo) : baisse Time.timeScale puis le
    // fait remonter. Le tween tourne en temps reel (SetUpdate(true)) donc il n'est
    // pas ralenti par sa propre baisse, et il survit a la destruction de
    // l'appelant (DOTween a son propre runner).
    //
    // Cle du "smooth" : on scale AUSSI Time.fixedDeltaTime avec le timeScale,
    // sinon la physique tourne au meme pas absolu et saccade (ca "lag"). Et on
    // vise ~0.2-0.3, pas 0.05 : trop bas = quasi-freeze qui se lit comme un hang.
    public static class Hitstop
    {
        private static Sequence seq;
        private static readonly float baseFixed = Time.fixedDeltaTime; // pas physique nominal (0.02)

        // scale = timeScale au creux (0.25 = slow-mo lisible qui bouge encore),
        // hold  = temps reel maintenu au creux,
        // ramp  = temps reel pour remonter a 1.
        public static void Punch(float scale = 0.25f, float hold = 0.35f, float ramp = 0.55f)
        {
            seq?.Kill();
            SetScale(scale);
            seq = DOTween.Sequence()
                .SetUpdate(true) // ignore timeScale : avance en temps reel
                .AppendInterval(hold)
                .Append(DOVirtual.Float(scale, 1f, ramp, SetScale)
                    .SetEase(Ease.InOutSine))
                .OnKill(() => SetScale(1f));
        }

        private static void SetScale(float t)
        {
            Time.timeScale = t;
            Time.fixedDeltaTime = baseFixed * t; // physique suit -> reste lisse
        }
    }
}
