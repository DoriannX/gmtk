# Génération de la map — plan

Ville du jeu (city driving chaos, toon, palette chaleureuse). Le trafic IA erre
aujourd'hui sur un Ground 200x200 sans lanes (`Assets/Scenes/CarTest.unity`,
branche `banner`). TODO déjà noté dans `docs/ai-traffic-cars.md` :
*« vrais lanes/waypoints quand la ville existe »*. La map, c'est cette ville.

## Décisions (validées avec l'utilisateur)

- **Niveau fixe unique** — pas de variation entre parties, pas de génération runtime.
- **Quartier** — 7x5 = 35 blocs (grille 22x16 cellules, cellSize 8 -> map 176x128).
- **Énigmes disséminées** — des spots précis dans la map hébergent des énigmes à
  résoudre pour collecter des trucs. Map lisible + placement contrôlé requis.

## Verdict : le générateur est un OUTIL D'AUTEUR, pas du runtime

Faux binaire « procédural vs main ». Vrai axe = **pièces modulaires sur grille**.
Pour ce cas (niveau fixe + énigmes placées) :

- **Générer l'ossature** (grille route + trottoirs + blocs bâtiments génériques)
  via un **bouton menu Unity éditeur**. Pose une fois, commit la scène.
- **Carver des blocs « spéciaux »** laissés vides/marqués → énigmes placées **à la
  main** (place, parc, ruelle, cour…).
- Tweak grille → re-run le bouton. Runtime = zéro génération.

= os générés (rapide, routes alignées, waypoints gratuits) + sens placé main
(énigmes lisibles). Pas de bruit type Perlin (imprévisible, mauvais pour un feel
jam contrôlé, temps perdu sur un générateur non justifié).

## État

- **Scène** : `Assets/Scenes/City.unity` (map définitive, séparée du proto CarTest).
- **Étape 0 : FAITE** — `CityGrid.cs` (données + layout ASCII) + `CityGridAuthoring.cs`
  (gizmo preview). GO `CityGrid` en scène, layout 5x3 blocs validé top-down.
- **Étape 1 : FAITE (vrais assets)** — `CityBuilder.cs` (sur le GO CityGrid,
  `[ContextMenu] Build City`, éditeur-only via `#if UNITY_EDITOR`). Instancie les VRAIS
  assets `Assets/Models/City` :
  - Route : tuile choisie par **bitmask des 4 voisins** (droite/virage/T/croix) auto-scalée
    à cellSize + tournée. Offsets d'orientation par défaut = champs tunables
    (`straightOffset/cornerOffset/tOffset`) au cas où une famille de tuile est mal orientée.
  - Block : un bâtiment/cellule (variante **déterministe** par hash de cellule), orienté
    façade vers la rue ; coin de pâté -> famille `Building_Corner_`.
  - Special : laissé vide (lot/plaza vert pour hand-author l'énigme).
  Échelles FBX **très incohérentes** entre assets -> auto-fit par `Fit()`.
  - Routes : scale **non-uniforme** pour remplir exactement la cellule -> tuiles jointives,
    réseau connecté.
  - Bâtiments : **1 par pâté** (flood-fill des cellules Block contiguës), centré, scale
    uniforme pour remplir le pâté -> blocs nets, fini le jumble 4/cellule.
  PIÈGE RÉSOLU : chaque tuile/bâtiment a un enfant `*_Veg` (végétation) qui **déborde** et
  faussait les bounds -> routes rétrécies (trous) + bâtiments minuscules. `Fit()` ignore
  désormais les renderers `*_Veg` pour le calcul de scale (ils rendent quand même).
  À VÉRIFIER plus tard : (1) orientations virage/T exactes (tune `cornerOffset`/`tOffset`) ;
  (2) façade bâtiment (`buildingFront`) ; (3) **colliders bâtiments** (FBX sans collider ->
  la voiture les traverse ; ajouter BoxCollider par bâtiment au build).
- **Rampe** : `Assets/Prefabs/Ramp.prefab` (wedge ProBuilder monte vers +Z, trigger
  + `Ramp.cs`, mat orange chaud). À instancier lors du build map.

## Étapes (ordre d'accroche)

Chaque étape = **un incrément → render → validation utilisateur** avant la
suivante (règle pipeline `docs/city-pipeline.md` + mémoire `validate-before-export`).

### Étape 0 — Grille & données *(fondation)*
`CityGrid` : tableau 2D de cellules `{ Road | Block | Special }`. Layout fixe
défini en dur (ou dessiné dans un petit inspector éditeur). Taille ~5x4 blocs.
**Seule source de vérité** — route, waypoints, blocs, spawns en dérivent tous.

### Étape 1 — Réseau route *(tout s'accroche dessus)*
Tileset modulaire (ProBuilder/Blender) : `droite, virage, T, croix, impasse`.
Le générateur lit `CityGrid`, instancie la bonne tuile + rotation par cellule
`Road`. Sol + trottoirs.

### Étape 2 — Lanes / waypoints *(débloque le TODO IA)*
Waypoints dérivés direct de la grille route (centre de voie par cellule +
connexions). Brancher `AICarController` sur ces rails → retire l'errance random
et `mapBound`. Le trafic suit de vraies rues.

### Étape 3 — Blocs bâtiments *(peuplement)*
Remplir les cellules `Block` avec les prefabs Blender + variantes (pipeline
existant). Densité/hauteur variables. **LOD végétal obligatoire** ici (~10k
tris/bâtiment × 15-30 bâtiments = lourd, cf. `docs/city-pipeline.md` piège 6).

### Étape 4 — Blocs spéciaux + énigmes *(le gameplay)*
Cellules `Special` laissées ouvertes → chaque spot d'énigme + collectible placé
à la main. Le système d'énigme est un sujet à part, spécifié quand la map tient.

### Étape 5 — Props & juice *(finition)*
Lampadaires, feux, arbres, poubelles aux jonctions. Palette par quartier. Spawn
player/pigeon/piéton. Bornes de map.

## Invariant

`CityGrid` = seule source. Change la grille → route, waypoints, blocs, spawns
suivent. Rien ne hardcode de positions en doublon de la grille.
