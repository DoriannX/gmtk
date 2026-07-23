# Voitures PNJ (trafic autonome) — CarTest

Voitures IA qui se baladent seules dans la map avec comportement "humain", modèle **ProBuilder**, juice cartoon partout (DOTween), collisions IA-IA et joueur-IA avec FX. Scène `Assets/Scenes/CarTest.unity`, branche `banner`. Tout jetable/tunable.

## Fichiers

- `Assets/Scripts/Gameplay/AICarController.cs` — IA conduite + juice + collisions (le gros).
- `Assets/Scripts/Gameplay/CarChatter.cs` — bulles de pensée solo (RequireComponent `SpeechBubble`).
- `Assets/Scripts/Gameplay/CrashFx.cs` — VFX de collision static/runtime (étoiles, onde, mot comic).
- `Assets/Prefabs/TrafficCar.prefab` — le véhicule. 6 instances `TrafficCar_i` en scène.
- Matériaux : `TrafficBody_0..5.mat` (carrosseries chaleureuses), `TrafficGlass.mat`, `TrafficWheel.mat`, `TrafficHub.mat`.

## Modèle ProBuilder (hiérarchie prefab)

```
TrafficCar            BoxCollider (1.7x1.4x3.4, center y0.7) + Rigidbody KINEMATIQUE
│                     (useGravity off, ContinuousSpeculative) + AICarController + SpeechBubble + CarChatter
├── Visual            pivot cosmétique : porte lean/pitch/squash/idle/crash (root reste plat)
│   ├── Body          cube 1.6x0.5x3.2 @ y0.75 (carrosserie coloree)
│   └── Cabin         cube 1.3x0.5x1.4 @ (0,1.2,-0.25) (vitre sombre)
└── Wheels            holder HORS du pivot Visual -> roues restent au sol (pas de float)
    ├── Steer0/1      roues AVANT sous un pivot de braquage
    │   └── WheelN    cylindre r0.4 h0.3, localRot Z=90 (essieu le long du X)
    │       └── Spoke barre claire sur la face ext -> rend le spin lisible
    ├── Wheel2/3      roues arriere (spin seul), + Spoke
```

Construit via `execute_code` (ProBuilder `ShapeGenerator.GenerateCube/GenerateCylinder`, retirer le `MeshCollider` auto des pièces).

## IA conduite (déplacement cinématique, pas de physique)

- Avance sur le cap courant, y=0 fixe. Accel douce (`accel`) / freinage mordant (`brake`) vers une vitesse voulue. Vitesse de croisière propre par voiture (`cruiseSpeed ± speedVariation`).
- **Detection obstacles** : 3 antennes `Physics.Raycast` devant (centre + 2 latérales `sideAngle`), origine au pare-choc. Nearest ≤ `stopDistance` → arrêt net ; entre stop et `feelerLength` → ralentit proportionnel ; côté plus dégagé → braque pour esquiver. Ignore ses propres colliders (`IsChildOf`), `QueryTriggerInteraction.Ignore` (traverse les piétons trigger, s'arrête sur vrais colliders).
- **Errance** : change de cap tous les `headingHold` s, demi-tour auto hors `mapBound` (90 = Ground 200). **Pauses humaines** : arrêt volontaire `pauseLength` s tous les `pauseEvery` s (feu rouge / hésitation).
- **Roues** : `SpinWheels` tourne `-deg` autour du local up (essieu) → omega +X = roulis avant. `SteerFrontWheels` → pivots avant yaw = `clamp(yawRate*steerGain, ±maxSteer 30°)` lissé. Deux tableaux sérialisés : `wheels[]` (4, spin) + `frontSteer[]` (2 pivots).

## Juice conduite (DOTween sur le pivot Visual)

- Roulis en virage (`maxLean` 12° depuis le braquage deg/s), tangage plonge-au-frein / cabre-à-l'accel (`maxPitch` 9°), lissés en `Lerp`.
- Squash `DOPunchScale` à l'arrêt, stretch au démarrage, respiration `DOLocalMoveY` yoyo à l'arrêt.
- `OnDisable` → `visual.DOKill()`.

## Collisions

### IA ↔ IA
Registre statique `Cars`, `CheckBump` teste l'intersection des AABB (`BoxCollider.bounds`) chaque frame (cooldown anti-repeat). Les DEUX voitures encaissent (`Bump` → `ReactCar` sur chacune), un seul burst de FX au milieu. **Pas de shake ni hitstop** (voir gate ci-dessous).

### Joueur → IA
Le prefab a un **Rigidbody kinematique** → collider mobile qui reçoit les collisions du player car (RB dynamique, tag Player) et le bloque solide (pas de traversée). `OnCollisionEnter` → si `GetComponentInParent<ArcadeCarController>()` : réaction scalée.

### Intensité `hard` (0..1) EXPONENTIELLE
- `lin = clamp((impact-1.5)/13)` (joueur : impact = `max(relativeVelocity, player.Speed)`), IA-IA : `clamp((speedA+speedB-1.5)/12)`.
- `hard = pow(lin, impactCurve)` — **impactCurve 2.2** (sérialisé, tunable). `=1` si `player.IsBoosting`. Contact quasi nul (`hard<=0.005`) ignoré.
- Courbe vérifiée : v6→0.10, v11→0.50, v20→1.0. Chocs lents/moyens discrets, seuls les slams explosent.

### Ce que fait un choc (tout scalé par `hard`)
- **FX locaux TOUJOURS** (`CrashFx.Play`, au point de contact, peu importe l'auteur) : gerbe d'étoiles (`3+40*hard` particules mesh dorées), onde de choc si `hard>0.25` (anneau sprite dessiné par code), mot comic si `hard>0.35` ("POW!/VLAN!/BADABOUM!..." TextMesh billboard).
- **Ressenti joueur SEULEMENT si joueur impliqué** (`SpawnCrashFx(..., playerInvolved)`) : **hitstop** (`Hitstop.Punch`) si `hard>0.4`, **camera shake** (`CarFollowCamera.Shake` amp `1.3*hard`). Un accident IA-IA à l'autre bout de la map ne secoue rien.
- **Réaction corps** (`ReactCar` sur chaque voiture) : projection recoil `(0.35+2.2*hard)`, **tête-à-queue** (root yaw `Lerp(30,700,hard)` deg/s pendant le recul), **squash&stretch** `Lerp(0.04,0.42,hard)`, **saut** `0.75*hard` (skip <0.03), **tonneau complet** `DOLocalRotate 360` si `hard>0.6` sinon punch, **flash blanc** `Lerp(base,blanc,hard)`, bulle ("AAAAH!!/MA CAISSE!!" si gros choc, sinon klaxon). Idle désactivé pendant le stun (le saut possède la position) ; rotation manuelle du juice suspendue pendant le stun (laisse jouer le tonneau/punch).

Échelle vérifiée : frôle v2.5 → stun0.18/spin82/6sparks/rien d'autre ; moyen ~v6 → stun0.44/spin255/16sparks/onde ; slam v22 → stun1.10/spin700/43sparks/hitstop0.16/onde+pop. timeScale revient propre à 1.0.

## Bulles (CarChatter)
Pensées auto solo (pas de conversation, contrairement au piéton) : `thoughtChance` 0.7, quiet 6-14s, lignes thème bagnole. Partage la même `SpeechBubble` que le klaxon de collision (s'efface si `bubble.IsShowing`).

## Pièges payés
- `execute_code` : **pas de Roslyn** → `compiler:"codedom"` (C#6). Pas de `using` en tête, pas d'interpolation `$""`, fully-qualifier `UnityEngine.ProBuilder.*` / `System.*` / `UnityEngine.Object`.
- **Wheels dans le pivot juice = float** sur 4 roues (le lean penche tout). Les sortir sous un holder à plat.
- **Cylindre lisse = spin invisible** → ajouter un rayon (Spoke) pour le voir tourner.
- **Deux kinematiques = pas d'event physique** → IA-IA en AABB manuel, joueur-IA en OnCollisionEnter (dynamique vs kinematique).
- **Shake/hitstop = ressenti joueur**, jamais sur un choc lointain qui n'implique pas le joueur.
- Ajouter un champ `[SerializeField]` ne touche pas les instances déjà en scène → default C# utilisé (OK) ; mais changer un default ne modifie PAS une valeur déjà sérialisée (cf. bug piéton `sixSevenChance`).

## TODO possibles
Débris qui se détachent, marques de pneu au sol, freins visuels (feux stop), vrais lanes/waypoints quand la ville existe, variantes de modèle, sons (klaxon/crash).
