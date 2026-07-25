# Direction animation — cartoon "Overcooked"

Cible : **très cartoon façon Overcooked**. Bounce, squash & stretch, overshoot, timing exagéré. **100 % DOTween** (pas d'Animator ni de clips importés).

## Principes

- **Easings à rebond** pour les poses clés : `Ease.OutBack`, `Ease.OutElastic`, `Ease.OutBounce`.
- **Boucles idle** en `Ease.InOutSine` + `LoopType.Yoyo` (respiration, flottement) — jamais d'arrêt sec.
- **Squash & stretch** : scale non-uniforme sur un pivot à chaque impact / atterrissage / saut / prise de parole.
- **Overshoot systématique** : aucune arrivée linéaire, tout dépasse puis revient.
- **Déphasage** : micro-délais entre membres (bras, tête, ailes) pour donner la vie.
- **Punch** sur les impacts (`DOPunchRotation`, `DOPunchScale`), **`DOJump`** pour sauts/envols avec arc.
- Amplitudes et vitesses **exagérées au-delà du réaliste** — lisibilité de lecture > réalisme.

## Réf existante

`Assets/Scripts/Gameplay/Pedestrian.cs` — pattern de référence : pivot runtime porteur des tweens, respiration Yoyo, `DOPunchRotation`, `DOJump` (culbute roadkill). S'en inspirer pour tout nouveau comportement animé (oiseaux, pigeons, props, UI).

Se combine avec le rendu toon post-process (voir mémoire projet `toon-postprocess-pipeline`).
