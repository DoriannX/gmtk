# Verticalité — plan

La map actuelle est plate. Objectif : de la hauteur, parce que le vélo/moto a déjà
tout ce qu'il faut pour en profiter (boost aérien, wall-ride, grind, rampes) et
que rien dans la ville ne le lui demande aujourd'hui.

Outils de référence : **la scène `road.unity`** (RoadNetwork + CityBrush +
GrindRailBaker + CityGroundBuilder). `CityBuilder`/`CityGrid` sont hors sujet ici.

## Décisions (validées en interview)

| Axe | Décision |
|---|---|
| Nature | Relief de terrain **+** toits praticables |
| Montée | Élan/rampes, grind qui monte, rues en pente (pas d'ascenseur) |
| Rôle | Terrain de tricks, livraisons en altitude, points de vue |
| Échelle | Moyenne, 3-8 m (Tony Hawk) |
| Réseau de toits | **Continu par quartier** — on traverse le quartier sans toucher terre |
| Chute | **Ne coûte que du temps** (vitesse perdue), pas de punition sèche |
| Livraison en altitude | On s'arrête et on dépose |
| Répartition | **Un seul quartier vertical**, le reste à plat |
| Toits | **Plateformes posées à la main** au-dessus des bâtiments peints |
| Descente | Les quatre : saut dans le vide, câble/tyrolienne grindable, toboggan, rues en pente |
| Premier incrément | **Ponts / voies surélevées** |

## Ce qui est DÉJÀ câblé (à ne pas réécrire)

- **Routes en Y** — `RoadNetwork.Node.pos` est un `Vector3` et `snapToGround` est
  décochable, commentaire à l'appui : *« Décocher pour lever un nœud en Y (ponts) »*
  ([RoadNetwork.cs:63](../Assets/Scripts/Gameplay/City/RoadNetwork.cs:63)).
- **Routes qui montent sans vriller** — `RoadMeshWarp` propage le repère par
  transport parallèle *« pas de flip à 180 deg quand la tangente passe près de la
  verticale (rampes) »* ([RoadMeshWarp.cs:19](../Assets/Scripts/Gameplay/City/RoadMeshWarp.cs:19)).
  Une bretelle qui monte est une route comme une autre.
- **Grind gratuit sur tout ce qu'on construit** — `GrindRailBaker` scanne tous les
  meshes de la scène et extrait les rebords grindables jusqu'à ~45°
  ([GrindRailBaker.cs:17](../Assets/Scripts/Gameplay/Editor/GrindRailBaker.cs:17)).
  Un pont, une plateforme, un câble tendu : le bake les rend grindables sans code.
- **Props sur les toits** — `CityBrush` a déjà une couche qui saupoudre sur les
  bâtiments peints, toits compris.

## Pièges identifiés AVANT de commencer

1. **Trou de sol sous les ponts.** `CityGroundBuilder` perce le sol sous chaque
   route. Une route levée à +6 m percerait quand même le sol 6 m plus bas → trou
   béant sous le pont. Il faut conditionner la découpe à la hauteur de la route
   au-dessus du sol.
2. **Caméra sans anti-collision.** `CarFollowCamera` orbite à distance fixe sans
   aucun raycast d'occlusion ([CarFollowCamera.cs:64](../Assets/Scripts/Gameplay/CarFollowCamera.cs:64)).
   Sur un toit ou sous un pont elle traversera la géométrie. C'est LE risque de
   confort n°1 de toute la verticalité.
3. **Sol plat par construction.** Le relief de terrain (choix retenu à terme)
   demande que `CityGroundBuilder` mange un champ de hauteur, et que les nœuds de
   route s'y calent. C'est le gros chantier — d'où le fait de commencer par les ponts.
4. **Trafic et piétons en hauteur.** L'IA voiture suit le réseau routier : elle
   empruntera les ponts (souhaitable ?) ; le `RuntimeNavBaker` doit gérer les
   surfaces surélevées ou les ignorer.

## Incrément 1 — ponts et voies surélevées

Le seul incrément qui donne de la hauteur jouable ce soir, parce que le système de
route sait déjà le faire.

- Éditeur : hauteur par nœud, éditable franchement (un slider Y dans l'inspecteur
  du nœud sélectionné, à côté du slider de rotation existant, plus une poignée
  verticale en scène). Aujourd'hui `Handles.FreeMoveHandle` bouge en écran, ce qui
  rend le déplacement en Y hasardeux.
- Poser 2-3 ouvrages : une bretelle qui monte, une traversée de rue, une retombée.
- Corriger le trou de sol sous ouvrage (piège 1).
- Bake grind → vérifier que les rebords du pont sortent en rails.
- **Render + validation utilisateur avant la suite.**

Après validation seulement : quartier vertical (plateformes + passerelles + câbles
de descente), puis relief de sol, puis livraisons en altitude.

### État : fait, en attente de validation en jeu

Scène `Assets/Scenes/Game.unity` : rue est-ouest + rue nord-sud (croisement 4 voies),
un ouvrage qui enjambe la rue est-ouest à x=40 (crête à +7 m, tablier plein mesuré
par raycast à 7,06 m), une rampe de saut qui finit en l'air à +6,3 m, le joueur posé
à son pied.

Trois correctifs, tous à la racine :

- `RoadNetwork.ProbeRoad` prend un `maxHeightAbove` optionnel (infini par défaut, donc
  aucun appelant existant ne change) ; `CityGroundBuilder` le passe à 1,5 m. Le sol
  n'est plus percé sous un ouvrage — vérifié au raycast : `Sol` présent à z=±12..25
  sous le tablier, et absent seulement là où la route touche vraiment le sol.
- `RoadNetworkEditor` : champ « hauteur » sur le nœud sélectionné + flèche verticale
  en scène, et le drag horizontal conserve la hauteur au lieu de rabattre l'ouvrage
  à la rue.
- `GrindRailBaker` scannait via `FindObjectsByType`, **qui ignore les objets
  `DontSave`** — c'est-à-dire toute la géométrie générée par `RoadNetwork`. Aucune
  route, aucun pont, aucune rampe n'a jamais produit de rail, y compris dans
  `road.unity`. Corrigé en descendant depuis les racines de scène.

Piège découvert en corrigeant le baker : les **pointillés de la ligne blanche** (0,8 m
de long) passaient tous les filtres et donnaient 234 rails en pleine chaussée sur une
seule rue — la moto aurait grindé sur le marquage au sol. Le filtre par longueur
d'arête ne peut pas les distinguer d'une bordure (la déformation de route découpe le
trottoir tous les ~1 m) ; ce qui les sépare, c'est la longueur **une fois chaînée**.
D'où `MinRail = 2.5 m` appliqué après chaînage. Résultat : 860 rails, 0 en chaussée,
100 sur le pont, 50 sur la rampe, le plus long faisant 92 m.

**Quatrième correctif, trouvé en regardant le pont par en dessous** : les assets du kit
sont des **coques ouvertes** — `SM_Tile_droit` n'a que 8 triangles orientés vers le bas
sur 254, `SM_Dead_end` 10 sur 184. En ville plate personne ne s'en aperçoit ; dès qu'un
segment est levé, on voit le marquage au sol par en dessous.

- `RoadMeshWarp` ferme le tablier par un ruban plat à la hauteur du bas de tuile,
  normales vers le bas, deux sommets par station. Ajouté sur tous les segments et pas
  seulement les ouvrages : au sol il finit sous le trottoir, invisible, et ça évite une
  branche à maintenir.
- `RoadNetwork.AddCap` coiffe de même les croisements et impasses **posés au-dessus de
  1,5 m** — le cas de l'impasse qui termine une rampe de saut. Visuel seulement, pas de
  collider : l'asset porte déjà le sien pour la face roulable.

Vérifié à la verticale par en dessous : plein, aucune percée. Les traits sombres visibles
de biais sont de la géométrie de l'asset, pas des trous.

**Cinquième correctif — les pentes étaient bosselées.** La spline est parfaitement lisse
(cassure de pente max 0,039), donc le défaut venait du mesh : la chaussée et les trottoirs
de `SM_Tile_droit` sont des **bandes pleine longueur** — 26 triangles couvrent les 8 m
d'un seul tenant, contre 196 petits triangles pour les seuls marquages. Le warp ne déplace
que des sommets ; sans sommet au milieu, la surface reste la **corde** de la courbe.
Mesuré sur la rampe : 37 cm de creux au milieu de chaque répétition, avec une cassure
d'angle à chaque couture.

`RoadMeshWarp.Subdivide` découpe donc la tuile en tranches d'un mètre max avant de la
déformer (coupe de l'arête la plus étirée en Z par son milieu, en boucle). Écart au tracé
en pleine voie : **2 cm au lieu de 37**.

Le même défaut existait à l'**horizontale** : dans un virage, seuls les pointillés
suivaient la courbe, la chaussée restait une corde. Invisible parce qu'on ne roule pas
verticalement dessus.

Coût : 254 → ~1186 triangles par répétition de tuile. `RoadNetwork` prépare donc **deux**
tuiles, brute et découpée, et n'utilise la découpée que sur les segments dont la spline
s'écarte d'une droite de plus de 3 cm sur une longueur de tuile (`Curves`, mesure la flèche
sur fenêtre glissante — pas la courbure globale, une longue courbe douce reste plate à
l'échelle d'une tuile). Les rues droites restent à la tuile brute.

**Sixième correctif — le tangage des nœuds, et la vraie cause des bosses.**
`Arm()` construisait la direction de bras avec `Quaternion.Euler(0f, yaw, 0f)` :
**strictement horizontale**. Un bras partait donc à plat même sur une rampe, et la spline
devait remonter en S pour rattraper la pente — plat au nœud, bosse au milieu. La courbure
était **artificielle**, fabriquée par la jonction et non par le tracé.

`Junction.pitch` couche maintenant le nœud dans la pente, et oriente à la fois l'asset et
ses bras (`NodeRotation`). Le tangage est un moindres carrés sur les branches : coucher le
nœud de `p` donne au bras d'azimut θ une pente `p·cos θ`, donc exact à 1 et 2 branches
(impasse, virage — les seuls cas où un ouvrage se termine) et compromis à 3/4, comme le fit
de yaw qui existait déjà.

Résultat sur la rampe : amplitude en voie **2 mm** (contre 20 mm avec la seule découpe, et
370 mm avant tout correctif), et la S a disparu du tracé. Les nœuds au sol restent à 0,0°,
la crête du pont aussi ; les deux nœuds symétriques de l'ouvrage sortent à ±7,7°.

**Pivot du tangage** (`PitchLift`). Un carrefour en dévers bascule autour de son centre : ce
qui s'enfonce d'un côté ressort de l'autre, et les routes qui en partent suivent. Une
**impasse** non — rien ne continue derrière elle, et la basculer sur son centre enterre son
bout fermé, c'est-à-dire le pied de rampe. Elle bascule donc autour de ce bout fermé, qui
reste posé au niveau du nœud pendant que le bras se lève : le pied de rampe devient un coin
posé par terre. Le même relevé est appliqué au bras dans `Arm()`, sinon le segment repart du
sol et laisse une marche.

Vérifié : tous les carrefours au sol descendent à -0,13 m (la seule épaisseur de tuile sous
la chaussée, comme un carrefour plat), et le profil de la rampe ne présente aucune marche
aux trois raccords asset/segment.

**Bornes du tangage.** Un nœud entre une rampe qui monte et une rue plate prenait la moyenne
des deux : il se couchait à mi-chemin, et la branche plate partait en piquant, s'enfonçait
dans le sol puis remontait. `ComputePitch` borne donc le tangage — **aucun bras ne part vers
le bas quand sa branche ne descend pas**. La contrainte est asymétrique : un bras qui part
trop haut ne gêne pas, il est en l'air et la spline le rattrape sans rien traverser.
(L'horizontale tient lieu de sol tant que le projet n'a pas de relief ; à revoir avec le
terrain.)

Effet mesuré : le nœud de raccord passe de couché à **0,0°**, la rue plate monte régulièrement
sans jamais plonger, et la rampe reste droite (le nœud du milieu est bridé de 11,8° à 9,5°,
amplitude en voie toujours ≤ 14 mm sur tous les segments de l'ouvrage).

**Piège d'atelier** : `Tools/Ville/Generer le sol` doit être relancé après avoir ajouté des
nœuds, sinon le sol recouvre la nouvelle route et les mesures au raycast tapent le sol.

## Le grind décrochait tous les 4 mètres

Défaut **antérieur** à tout ce qui précède (169 rails dans la bande de bordure avant la
découpe des tuiles, 164 après — la découpe n'y était pour rien).

Cause : le trottoir du kit est **dallé**, avec un joint transversal de 2 cm tous les 3,86 m.
La bordure est donc géométriquement interrompue à chaque dalle, la soudure du baker ne fait
qu'1 cm, et une rue de 70 m ressortait en 18 rails de 3,9 m. Le grind décrochait à chaque
dalle.

- `GrindRailBaker.Join` recolle deux rails dont les extrémités se touchent à 35 cm près, à
  trois conditions d'alignement : les deux directions entre elles, **et chacune avec le
  trou**. Cette dernière est ce qui empêche de coudre deux bordures parallèles voisines —
  leur trou est perpendiculaire à leur direction. Le filtre `MinRail` passe désormais
  **après** le recollage : un bout de 2 m isolé est du bruit, recollé il fait 70 m.
- Le recollage a ressuscité les **marquages peints** du carrefour (6 rails de 2,5 m en
  pleine chaussée). D'où `MinStep` : il faut une vraie marche sous l'arête. Le baker
  calculait déjà `minRel`, la profondeur sous le rail — il ne s'en servait pas. Bordure
  27 cm, peinture 2 cm, seuil à 6.

Résultat : bordures de **57 à 77 m** d'un tenant (rail le plus long de la scène : 145 m),
**0 rail en chaussée**, 58 sur le pont, 29 sur la rampe.

Restent 399 rails transversaux de 4 m : les joints de dallage eux-mêmes, en travers du
trottoir. Ils n'attrapent pas quand on roule le long de la rue (`grindAlign = 0.6` sur
`ArcadeCarController` exige un cap ~parallèle), seulement si on traverse le trottoir. À
supprimer si ça gêne — leur signature est d'avoir un jumeau parallèle à 2 cm.

`RoadMeshWarp.Subdivide` a par ailleurs été réécrit en **tranchage par plans** au lieu d'une
bissection de l'arête la plus longue. C'était une fausse piste sur ce bug, mais la version
par plans est correcte là où l'autre ne l'était pas : deux triangles voisins coupent leur
arête commune au même endroit, donc pas de T-junction ni de micro-fissure après déformation.

Reste à juger manette en main : la pente est-elle avalable, la crête envoie-t-elle
bien en l'air, et surtout la caméra (piège 2, toujours ouvert).

## Piege camera : la camera qui traverse le decor

Abandonne, essai 1 : silhouette du joueur en `ZTest Greater` / `ZWrite Off`
(degrade cyan -> magenta, rayures ecran facon hologramme). Ca montrait ou est le
joueur, pas ou on va — inutilisable pour conduire.

Abandonne, essai 2 : un shader qui perce un trou dans les occulteurs. Le decoupage par
pixel marchait (case de BD, bord en scanlines glitchees) mais le rendu n'a pas
convaincu, et l'effet ne couvrait de toute facon que les materiaux `GMTK/ToonLit`
-- le kit ville est en URP/Lit. Ce qui reste de l'essai, en negatif : c'est le
shader de l'OCCULTEUR qui doit clipper, aucun post-process ne peut recuperer une
geometrie qui n'a pas ete dessinee, et la position du trou depend de la camera
QUI REND (calculee pour la seule FollowCamera, la Scene View la dessinait a cote).

Retenu : collision de camera, dans `CarFollowCamera`. Un `SphereCastNonAlloc` du
pivot vers la position voulue, on s'arrete au premier decor rencontre.

- Rayon 0.35 m plutot qu'un rayon simple : la camera ne rase plus les angles.
- Rentrer est immediat, ressortir est amorti a 8 m/s. L'inverse ferait passer une
  frame DANS le mur ; symetrique, la camera claquerait des qu'on frole un poteau.
- Les colliders du joueur englobent le pivot : sans le filtre `IsChildOf(target)`
  la camera se collerait a lui en permanence (un contact demarre a l'interieur
  ressort avec distance 0, d'ou le double filtre).
- Distance mini 1.6 m, en deca on est dans le joueur.

Mesure sur Game.unity, pivot au carrefour : a pitch -10 la camera partait a
y = 0.11 m, soit sous la chaussee ; elle est maintenant bloquee a 6.26 m du pivot
et reste a y = 0.41. A pitch 0 et 20 rien ne bloque, la distance nominale est
conservee.

La geometrie generee par RoadNetwork porte bien des MeshCollider, mais elle est
en `HideFlags.DontSave` : juste apres un `OpenScene` elle n'existe pas encore et
tout raycast renvoie zero. Verifier la physique dans une scene fraichement
ouverte donne donc de faux negatifs.
