# Proto CarTest — notes de session (2026-07-23)

Prototype de feel véhicule cartoon (inspiration **Make Way** / Mario Kart) + piéton NPC, dans la scène `Assets/Scenes/CarTest.unity`. Branche `banner`. Tout est jetable/tunable, rien n'est final.

## Scène `CarTest`

```
Ground            plane 200x200 (matériau grid)
Sun               directional light
Car               racine PHYSIQUE (BoxCollider 1.2x0.6x2.4, Rigidbody, ArcadeCarController)
├── Body          pivot visuel (tilt/squash cosmétiques, PAS la physique)
│   └── TruckVisual   instance Truck.fbx (scale 0.75, rot 270°X import Blender)
│       └── ... Cab, Trailer, Wheel (sphère), HubL/R, Windshield, feux, etc.
│           └── Smokestack > ExhaustSmoke (ParticleSystem, parenté à la cheminée)
├── DriftSmoke    PS fumée drift/burnout/atterrissage (piloté par le controller)
├── BoostFlames   PS flammes boost (jet arrière orange)
├── SpeedLines    PS traits de vitesse anime (render Stretch, en anneau)
├── BoostRing     PS onde de choc (mesh anneau, sim LOCALE + recul -5 z, alignment Local)
└── SkidTrail     TrailRenderer trace de gomme (20s, fade alpha, gris translucide)
FollowCamera      tag MainCamera (!), Camera + CarFollowCamera (orbite TPS souris + Shake())
Pedestrian        wrapper + instance Pedestrian.fbx + script Pedestrian (DOTween)
```

## Contrôles (Core.InputActions, wrapper clavier direct)

- **ZQSD/flèches** : conduite (marche av/ar + direction)
- **Shift gauche** : drift (grip coupé, steer ×2.5, contre-braquage visuel 22°) ; relâche après 0.7s = mini-turbo
- **Shift + accél à l'arrêt** : **burnout** (roue patine, charge 1.5s max, relâche Shift = launch proportionnel)
- **Espace** : saut cartoon (buffer 0.15s + coyote 0.12s, squash & stretch DOTween)
- **E / Ctrl gauche** : boost (impulse + vitesse max relevée 0.9s, cooldown 2.5s)
- **F** : phares on/off
- **Souris** : caméra orbite ; **Échap** libère le curseur, clic gauche re-verrouille

## Scripts (Assets/Scripts/Gameplay/)

### ArcadeCarController.cs — tout le feel voiture
- Physique arcade : AddForce avant (**sol uniquement**, pas de poussée en l'air), clamp vitesse plane, grip latéral fractionnel (0.25 normal / 0.012 drift), steering proportionnel vitesse, inversé en marche arrière. Air : yaw libre 150°/s, pas d'accél.
- **Saut** : input échantillonné en `Update()` (wasPressedThisFrame raté en FixedUpdate sinon) → buffer + coyote. Vélocité verticale négative annulée avant l'impulse.
- **Visuel** : tilt sur `Body` (roulis virages ×2 drift, pitch = vitesse signée — penche en avant quand roule en avant, gyropode), squash & stretch via DOTween (`PunchScaleY` : pic 0.08s + retour OutElastic).
- **Roue** : sphère, spin en **LateUpdate** (l'Animator Idle écrit aussi sur Wheel → on repasse derrière), angle cumulé appliqué en localRotation autour de l'axe capturé au spawn. JAMAIS Rotate() en axe monde (dérive multi-axes).
- **Boost/burnout** : `TriggerBoost(gain, durée)` centralise → FOV punch progressif (valeur qui décroît + lerp caméra 9/s), camera Shake, burst flammes, onde de choc, burnout puffs sous la roue, squat 0.72.
- **VFX pilotés** : rateOverTime selon état (drift smoke, flames, speed lines, exhaust chug si throttle au sol), `skidTrail.emitting` si drift/burnout au sol.
- Caméra récupérée via `Camera.main` **+ fallback FindAnyObjectByType** (le tag MainCamera manquait → FOV/shake silencieusement morts, déjà vécu).
- **Feux de recul** : `ReverseLightL/R` (duplicats des Headlight, parentés à `Trailer`, face arrière, base rouge sombre), matériau `Assets/ModelBlender/ReverseLightMat.mat` (copie TruckLight). Allumés (émission rouge + vrais spots `ReverseBeamL/R`) dès que `throttle < -0.1` (intention marche arrière, comme une boîte de vitesses). NB : pas ajoutés au Truck.fbx source Blender (MCP Blender non connecté) — si le FBX est réexporté, les feux scène survivent (objets Unity séparés) mais penser à les repositionner si l'arrière change.
- **Phares (touche F)** : toggle via `InputActions.GetHeadlightPressed` (échantillonné en Update, piège #4). Lentilles avant HeadlightL/R (émission via instance `.material` — TruckLight a une émission baked ~2, écrasée à l'Awake par `SetHeadlights(false)` sinon elles brillent moteur coupé) + vrais spots `HeadlightBeamL/R` (enfants des lentilles, plongée 16°, warm, range 30). `SetLamp(renderers, lights, on, emission)` centralise lentilles+spots ; les 4 spots sont sans ombres, désactivés par défaut en scène.

### CarFollowCamera.cs — TPS orbite
- Souris = yaw/pitch (clamp -10/70°), distance fixe, position directe (pas de smoothing — demandé), `Shake(amplitude, durée)` public (Perlin, falloff).

### Pedestrian.cs — NPC DOTween
- Errance : marche 2-5s direction aléatoire → pause 1-3s → repart. Rayon 12m autour du spawn (au-delà, biaisé retour).
- **Pivot créé au runtime** entre wrapper et FBX : les tweens ne doivent JAMAIS toucher la racine FBX (rotation d'import 270°X écrasée sinon).
- Idle **continu** : respiration sine ample (scale 1.04, 1.5s), tête qui flotte déphasée + wobble lent, mains en opposition de phase — tout InOutSine yoyo, aucun rebond sec (le "boing boing" a été retiré à la demande).
- Marche : dandine ±7°, rebond, bras qui pompent ±28°, cadence 0.24s.
- Poses de base capturées à l'Awake, reset à chaque changement d'état (sinon dérive).
- Pas de collider — le camion passe à travers (à décider : écrasement ? esquive ?).

### SpeechBubble.cs + PedestrianChatter.cs — bulles de dialogue NPC
- `SpeechBubble` : canvas world-space construit 100% au runtime (pas de prefab), sprite de bulle BD **dessiné procéduralement** (SDF → Texture2D : coins ronds, contour épais, queue triangle pour la parole, petites bulles pour la pensée), Text legacy (`LegacyRuntime.ttf`, pas de dépendance TMP Essentials), billboard caméra en LateUpdate, pop OutBack + wobble continu DOTween. Taille calculée sur le texte réel (`TextGenerator.GetPreferredWidth/Height`). Pensée = fond bleuté + italique gris-bleu, parole = blanc + gras.
- ⚠️ Canvas créé en **Start()**, pas Awake : `Pedestrian.Awake` reparente TOUS les enfants sous son Pivot animé — un enfant créé en Awake se fait embarquer et danse avec le corps.
- Tunables en constantes (pas SerializeField) : évite les vieilles valeurs sérialisées figées en scène pendant qu'on itère.
- `PedestrianChatter` : timer silence 4-10s → soit pensée solo, soit conversation si autre piéton libre dans 7m (registre statique, pas de physics — les piétons n'ont pas de collider). Conversation scénarisée : l'initiateur **marche vers** son confrère (l'autre s'arrête et attend), arrivé à 1.5m ils se tournent **face à face**, puis starter → réponse → 50% relance, et chacun repart errer. Timeout d'approche 10s (abandon propre si bloqué). Répliques éditables dans l'inspecteur.
- `Pedestrian` expose une API pour le chatter : `WalkTo(target, stopDist, cb)` / `StandAndWait()` / `FaceTowards(pos)` / `ResumeWander()` (mode Wander/WalkTo/Wait dans Update), + `MustacheFrenzy(duration)` — jiggle léger et rapide des brins `PedStacheL/R` (rot ±8° à 0.09s + scale ×1.12 à 0.11s, yoyo, brins déphasés de 0.045s) pendant que le piéton parle, retour OutBack propre. (v1 ±35°/×1.55 jugée trop énorme.)
- Scène : `PedestrianChatter` ajouté sur `Pedestrian`, `Pedestrian2` dupliqué à côté pour tester les conversations.
- **Test play sans focus** : `EditorApplication.Step()` en boucle via execute_code pompe des frames même éditeur non focus — contourne le piège #7 pour vérifier les comportements dans le temps.

### Roadkill piéton (Pedestrian.cs + BloodFx.cs)
- Piétons `Pedestrian`/`Pedestrian2` : **CapsuleCollider trigger** (h1.8 r0.4 centre y0.9) + **Rigidbody kinematic** (pas de gravité) → `OnTriggerEnter` reçoit le camion (dont la BoxCollider solide + Rigidbody dynamique déclenchent l'event). Le camion **traverse** (trigger, pas de poussée physique).
- Réaction 3 paliers selon `car.Speed` (vitesse plane) + `car.IsBoosting` (exposés sur `ArcadeCarController`) : lent → `Knockback` léger (recul 1.3m, `DOPunchRotation`, bulle « Hé oh ! »), rapide (≥`pissedSpeed` 12) → projeté 4.5m en `DOJump` + culbute 360° + insulte, boost **ou** ≥`goreSpeed` 20 → `Explode` : `BloodFx.Spawn` puis `Destroy`. Mode `Stunned` suspend l'errance le temps du vol, `Recover` remet le pivot droit et reprend le wander.
- **BloodFx** (statique, 100% runtime, aucun asset à câbler) : burst de ~35 globs **mesh sphère** rouges (URP Particles/Unlit, gravité 2.2, sim World, fade en fin de vie, auto-`Destroy` 3s) + flaque `SpriteRenderer` posée à plat (raycast sol, yaw aléatoire, pop OutBack) au splat **dessiné par code** (blob à rayon irrégulier via harmoniques + gouttelettes). Mat/mesh/sprite cachés statiquement. ponytail : pas de pooling ni fade des flaques (à ajouter si ça sature).
- `PedestrianChatter` : gardes `if (other == null)` ajoutées (un confrère écrasé en pleine conversation ne doit pas crasher la coroutine / `EndConversation`).

### AICarController.cs — voitures PNJ (trafic autonome)
- Voitures qui se baladent seules dans la map, comportement "humain". **Modele ProBuilder** (pas Blender) : root `TrafficCar` = carrosserie cube + cabine cube + 4 roues cylindre (essieu le long du X, `localRotation Z=90`), `BoxCollider` sur le root, prefab `Assets/Prefabs/TrafficCar.prefab`. 6 instances `TrafficCar_i` spawnees dans la scene, carrosseries couleurs chaleureuses aleatoires (`TrafficBody_0..5.mat`, `TrafficGlass.mat`, `TrafficWheel.mat`).
- **Deplacement cinematique** (transform, pas de Rigidbody) facon `Pedestrian` : avance sur le cap courant, y reste a 0. Accel douce (`accel`) / freinage mordant (`brake`) vers une vitesse voulue.
- **Detection obstacles** : 3 antennes `Physics.Raycast` devant (centre + 2 laterales `sideAngle`), origine au pare-choc (`+forward*1.2, up*rayHeight`). Nearest <= `stopDistance` -> arret net ; entre stop et `feelerLength` -> ralentit proportionnel ; cote plus degage -> braque pour esquiver. Ignore ses propres colliders (`IsChildOf`), `QueryTriggerInteraction.Ignore` (traverse les pietons trigger, s'arrete sur les vrais colliders : autres voitures, player Car, murs).
- **Errance** : change de cap tous les `headingHold` s (`wanderTurn` deg), demi-tour auto si hors `mapBound` (90 = Ground 200). **Pauses humaines** : arret volontaire `pauseLength` s tous les `pauseEvery` s (feu rouge / hesitation).
- Roues : spin `SpinWheels` autour de l'essieu (local up apres orientation) selon la vitesse — invisible sur cylindre lisse (ajouter jante/tread si on veut le voir tourner).
- **Hierarchie prefab** : `root` > `Visual` (carrosserie Body+Cabin, porte le juice/lean) + `Wheels` (holder HORS du pivot Visual -> roues restent au sol, pas de float quand la caisse penche ; piege 4-roues vs monoroue player). Roues avant sous un pivot `Steer0/Steer1` (braquage), arriere directement sous `Wheels`. Chaque roue a un enfant `Spoke` (barre claire `TrafficHub.mat` sur la face exterieure) pour rendre le spin lisible (cylindre lisse = spin invisible).
  - **Spin** : `SpinWheels` tourne `-deg` autour du local up (essieu) -> omega +X = roulis avant (verifie omega=(+16.7,0,0), roue_Ydev=0 = pas de float).
  - **Braquage** : `SteerFrontWheels` -> pivots `frontSteer[]` yaw = `clamp(yawRate*steerGain, +/-maxSteer 30)` lisse. Verifie : virage droite roues avant +29.7, gauche -29.7. Deux tableaux serialises sur le controller : `wheels[]` (les 4, spin) + `frontSteer[]` (les 2 pivots avant).
- **Juice cartoon** (DOTween, comme player car + pietons) sur le pivot `Visual` (root reste plat pour raycast/deplacement) : roulis en virage (`leanFactor`/`maxLean` 12deg, depuis le deg/s de braquage), tangage plonge au frein / cabre a l'accel (`pitchFactor`/`maxPitch` 9deg, depuis l'accel m/s^2), lisses en `Lerp`. Squash `DOPunchScale` a l'arret, stretch au demarrage (anticipation), respiration `DOLocalMoveY` yoyo a l'arret. `OnDisable` -> `visual.DOKill()`. **Verifie** en play : roll ~12, pitch ~4, scaleY 0.76..1.20, idle bob 0.05, 0 erreur.
- **Verifie** (play + `EditorApplication.Step` en boucle) : les 6 roulent, restent a y=0, dans les bornes ; test mur face -> freine et s'arrete ~1.5 m avant. 0 erreur console.
- Piege execute_code : **pas de Roslyn** sur cet editeur -> `compiler:"codedom"` (C# 6). Pas de `using` en tete de corps de methode, pas d'interpolation `$""`, fully-qualifier `UnityEngine.ProBuilder.*`, `System.*`, `UnityEngine.Object` (ambigu avec `object`). ProBuilder : `ShapeGenerator.GenerateCube/GenerateCylinder(PivotLocation, ...)`, retire le `MeshCollider` auto des pieces.
- **Collision entre voitures (IA-IA)** : registre statique `Cars`, `CheckBump` teste l'intersection des AABB (`BoxCollider.bounds`) chaque frame (cooldown anti-repeat). Au choc les DEUX voitures reagissent (`Bump` -> `ReactBump` sur chacune) : recul a l'oppose (`recoilDir` porte la vitesse maintenant, `recoilTime`), stun `bumpStun` 0.7s (vitesse 0), reroute cap oppose, squash + `DOPunchRotation` (secousse ; rotation manuelle du juice suspendue pendant le stun via `if(stunLeft<=0)`), bulle klaxon. Verifie : 2 voitures face a face -> stun+bulle sur les 2, se separent a ~13.
- **Collision joueur -> voiture PNJ** : le prefab a un **Rigidbody kinematique** (useGravity off, ContinuousSpeculative) -> collider mobile qui recoit les collisions du player car (RB dynamique) et le bloque solide (pas de traversee). `OnCollisionEnter` -> si `GetComponentInParent<ArcadeCarController>()` : `hard = clamp(max(relativeVelocity, player.Speed)/15)`, `=1` si boost, min 0.4. NB : IA-IA reste en AABB manuel (2 kinematiques = pas d'event physique entre elles).
- **JUICE de collision** (partage IA-IA et joueur, un seul burst de FX au point de contact via `SpawnCrashFx`, reaction corps via `ReactCar` sur chaque voiture) :
  - **Hitstop** (`Hitstop.Punch`) + **camera shake** (`CarFollowCamera.Shake`) : effets "ressenti joueur", **uniquement si le joueur est implique** (`SpawnCrashFx(..., playerInvolved)`). Un accident IA-IA a l'autre bout de la map ne fait NI shake NI ralenti (verifie : IA-IA loin -> minTS 1.0 / shakeTimer 0, sparks OK ; joueur->IA -> hitstop 0.16 + shake 0.58). FX locaux (particules/onde/mot comic) toujours joues quel que soit l'auteur.
  - **`CrashFx.Play(point,hard)`** (nouveau, static, 100% runtime, aucun asset) : gerbe d'etoiles (ParticleSystem mesh sphere doree, Emit 14+36*hard), onde de choc (anneau sprite dessine par code, DOScale+DOFade billboard), mot comic ("POW!/VLAN!/BADABOUM!..." TextMesh billboard `LegacyRuntime.ttf`, pop OutBack + monte + shrink).
  - **`ReactCar`** : projection (recoil*(1+hard*2.4)), stun, **tete-a-queue** (root yaw `crashSpinVel` 220-700 deg/s pendant le recul, gate dans Update qui saute le lerp de cap), **squash&stretch sequence** (splat->etirement->OutElastic), **saut** (`DOLocalMoveY` up->OutBounce ; idle desactive pendant stun pour ne pas voler la position), **tonneau** complet (`DOLocalRotate 360 FastBeyond360`) si `hard>0.55` sinon punch rotation, **flash blanc** carrosserie (`DOVirtual.Float` sur `_BaseColor`), bulle ("AAAAH !!/MA CAISSE !!" si gros choc).
  - **`hard` (0..1) exponentiel** : `lin = clamp((impact-1.5)/13)` (joueur, impact=max(relativeVelocity,Speed)) puis `hard = pow(lin, impactCurve)` (defaut 2.2, tunable inspecteur, =1 si boost) ; IA-IA `pow(clamp((speedA+speedB-1.5)/12), impactCurve)`. Contact quasi nul (`hard<=0.005`) ignore. Courbe verifiee : v6->hard0.10, v11->0.50, v20->1.0 -> chocs lents/moyens discrets, seuls les slams explosent. Gates : hitstop si `hard>0.4`, shockwave si `>0.25`, comic pop si `>0.35`, tonneau complet si `>0.6` (sinon punch scale*hard). Sparks `3+40*hard`, spin `Lerp(30,700,hard)`, stun `0.12+hard`, recoil `0.35+2.2*hard`, squash `Lerp(0.04,0.42,hard)`, saut `0.75*hard` (skip si <0.03), flash `Lerp(base,blanc,hard)`, camShake amp `1.3*hard`.
  - Verifie (echelle) : frole v2.5 -> stun0.18/spin82/6sparks/pas de hitstop-shockwave-pop ; moyen ~v6 -> stun0.44/spin255/16sparks/shockwave ; slam v22 -> stun1.10/spin700/43sparks/hitstop0.16/shockwave+pop. timeScale revient a 1.0, 0 erreur.
- **Bulles voitures** : `CarChatter` (RequireComponent `SpeechBubble`, comme le pieton mais solo, pas de conversation) -> pensees auto (`thoughtChance` 0.7, quiet 6-14s), lignes theme bagnole. `AICarController` partage la meme `SpeechBubble` pour le klaxon de collision (CarChatter s'efface si `bubble.IsShowing`). Prefab TrafficCar = +`SpeechBubble` +`CarChatter`.
- **Fix bug pieton "67"** : `PedestrianChatter.sixSevenChance` etait a 1.0 (100% du temps le meme "SIIIX SEVEEEN", jamais de pensees/convos). Remis a 0.15 (defaut code + valeurs serialisees sur Pedestrian/Pedestrian2 via SerializedObject). Piege : changer le defaut code ne touche pas la valeur deja serialisee en scene -> fixer les instances.
- TODO possibles : freins visuels (feux stop), vrais lanes/waypoints quand la ville existe, variantes de modele.

### Core/InputActions.cs
Wrapper statique Keyboard.current. Ajouts session : `GetActionHeld` (espace maintenu — plus utilisé), `GetDriftHeld` (shift), `GetJumpPressed` (espace), `GetBoostPressed` (E/Ctrl).
- **`GetMovementAxis` ne normalise PLUS la diagonale** : throttle et steer sont des canaux indépendants pour un véhicule. La normalisation (0.707) affamait la marche arrière en virage : `0.707 × reverseAcceleration (20) = 14 m/s²` < friction sol effective (COM abaissé à -0.5 fait plonger l'arête de la BoxCollider → friction qui grandit avec la force appliquée) → **stall total en S+D/S+A** alors que S seul (20 m/s²) passait à peine (~3 m/s).

## Assets Blender (Assets/ModelBlender/)

- **Truck.fbx** : camion cartoon mono-roue. Cab + Trailer séparés (gap 6cm anti z-fight), roue sphère écrasée + moyeux, détails (pare-brise, fenêtres, phares, pare-chocs, cheminée) **parentés à Cab** pour suivre l'anim. Anim "Idle" (bob 30f, loop) → `TruckAnimator.controller` sur TruckVisual.
- **Pedestrian.fbx** : bonhomme style Rayman/Overcooked — tête flottante (gap), corps œuf habillé (pull vert, pantalon, boutons), moustache guidon en sweep, nez bouton, sourcils fins, couronne de cheveux dégarnie + favoris, mains-moufles flottantes + pouces boules. Un seul mesh organique par pièce (lathe de profil + subsurf, sweeps le long de courbes avec taper).
- **SmokePuff.fbx** : blob 4 icosphères (particules fumée + flammes).
- **BoostRing.fbx** : anneau plat (onde de choc).
- Matériaux Unity créés : SmokeMat, BoostFlameMat, SpeedLineMat, SkidMat (URP Unlit/Particles).
- ⚠️ **Le .blend n'est pas versionné/sauvé automatiquement** — Ctrl+S dans Blender pour garder le personnage/camion sources.

## Pièges appris (à ne pas re-payer)

1. **Ne jamais laisser un scale non-uniforme sur la racine physique** (l'ancien cube Car écrasait le FBX enfant) — taille dans le BoxCollider, scale racine = 1.
2. **Blender FBX** : front = -Y Blender → +Z Unity, import rot 270°X sur la racine. `matrix_parent_inverse` non-identité = positions cassées à l'export → toujours baker dans `matrix_basis`.
3. **Animator vs script** : le bake FBX crée des canaux pour TOUS les objets — un clip peut figer un objet qu'un script anime. Script en LateUpdate = il gagne.
4. **`wasPressedThisFrame` en FixedUpdate = inputs ratés** — échantillonner en Update, consommer en FixedUpdate.
5. **`Camera.main` exige le tag MainCamera** — sinon null silencieux.
6. **DOTween sur hiérarchie FBX** : pivot intermédiaire obligatoire.
7. Éditeur Unity sans focus = play mode quasi figé entre commandes MCP — les tests particules/anims se font manette en main.

## Idées notées non faites

- Piéton : ~~collider + réaction au camion~~ ✅ fait (roadkill 3 paliers + explosion sang, voir section dédiée). Reste : ragdoll physique vrai, fuite paniquée à l'approche
- Spawner de piétons multiples, variations de couleurs
- Drift : particules d'étincelles au mini-turbo, trails par niveau de charge (bleu/orange)
- Caméra : collision murs (raycast), zoom molette
- Vrai .inputactions asset (remplacer le wrapper), support manette
- Sons (AudioManager déjà dans Core, rien branché)
