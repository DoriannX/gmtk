# City Assets — liste & avancement

Style : **low-poly stylisé mais détaillé** (pas juste des boîtes — clim, gouttières,
enseignes, garde-corps, câbles...). **Ville végétalisée** : la verdure est une couche
à part entière, présente sur presque tous les assets (jardinières, façades, toits,
arbres de rue, mauvaises herbes dans les fissures).

Pipeline : Blender (modeling) → export → import Unity. Un asset à la fois → validation
utilisateur → déclinaison en variantes → suivant.

Légende statut : ⬜ à faire · 🔨 en cours · 🎨 variantes · ✅ validé

---

## Règles de détail (appliquer à TOUS les assets)
- Silhouette lisible + **1-3 détails signature** par asset (jamais une boîte nue).
- Bevel léger sur arêtes principales (casse le côté "cube").
- Flat shading, couleurs par matériau (pas de texture UV pour l'instant).
- Ajouter systématiquement de la **végétation** quand ça a du sens.
- Budget poly indicatif : props 50-300 tris, bâtiments 300-1500, véhicules 400-1200.

---

## 1. Bâtiments
Kit + détails : clim, gouttières, enseignes, stores, câbles, antennes toit.
Végétal : jardinières fenêtre, plantes balcon, lierre façade, toit végétalisé.

- ✅ Building_Shop_A — commerce détaillé + végétal organique + perron + frise/horloge, palette chaude.
- ✅ Variantes Shop_A (×5 : A-E) : hauteurs R+1→R+3, couleurs murs/auvent/frise/porte, densité verdure, +/- auvent. Builder paramétré `build_shop(tag,ox,wall,awn,fri,door,sign,n_rows,dens,has_awn)`.
- ✅ Immeuble habitation R+4/5/6 (Building_Apartment_A) — 2 faces détaillées + toit habité + végétal facetté Shop_A. Hero : `docs/city-refs/Building_Apartment_A.png`. Objet base + objet `_Veg` séparé (LOD).
- ✅ Variantes Apartment (×5 : A–E) — `build_apartment(tag,ox,wall,trim,door,dens,seed,tree)` : hauteurs R+4→R+6, teintes murs chaudes (Beige/Peach/Sand/Rose/Butter), trim coral/turquoise/miel, densité verdure sparse→lush. Overview : `docs/city-refs/Building_Apartment_variants.png`. **Exportées FBX → `Assets/Models/City/Building_Apartment_{A..E}.fbx`** (base+veg par fichier, pivot au sol, -Z/Y). TODO : vérifier/remapper matériaux URP/Lit + créer prefabs.
- ✅ Immeuble d'angle chanfreiné (Building_Corner_A) — pan coupé en angle de rue portant l'entrée (porte+auvent+enseigne+lampes+pots), 3 faces détaillées, RDC vitrines, R+5 fenêtres à croix, garde-corps barreaux coral, jardinières étages alternés, descentes EP, toit habité (édicule/cuve/antenne/parabole/jardin+arbre). Hero : `docs/city-refs/Building_Corner_A.png`. Base ~2.9k + `_Veg` ~18.9k. Builder : `scripts/blender/corner_builder.py`.
- ✅ Variantes Corner (×5 : A–E) — `build_corner(tag,ox,wall,trim,sign,nfloors,dens_alt,seed)` : hauteurs R+4→R+6, murs beige/pêche/sable/rose/crème, trim coral/turquoise/miel, densité verdure sparse→lush. **Matériaux per-tag** (sinon écrase les canoniques partagés). Overview : `docs/city-refs/Building_Corner_variants.png`. **Exportées FBX → `Assets/Models/City/Building_Corner_{A..E}.fbx`** (base+veg par fichier, pivot centré/sol, -Z/Y, import Unity OK sans erreur). TODO : remap matériaux URP/Lit + prefabs.
- ✅ Petite maison de ville étroite (Building_Townhouse_A) — **toit à 2 pentes (pignon)** = silhouette signature (vs toits plats apparts/corner). Corps R+2 étroit (façade 3.4), porte (hotte/lampe/numéro), fenêtres à volets, lucarne pignon, fenêtres 4 faces, **rangs de tuiles**, cheminée traversante, végétal (jardinières fleuries/lierre/pots). Hero : `docs/city-refs/Building_Townhouse_A.png`. Base ~1.8k + `_Veg` ~13.5k. Builder : `scripts/blender/townhouse_builder.py`.
- ✅ Variantes Townhouse (×5 : A–E) — `build_townhouse(tag,ox,wall,trim,tile,nup,dens,seed)` : hauteurs **R+1↔R+2** (nup), murs beige/pêche/beurre/rose/sable, volets coral/turquoise/miel, tuiles terracotta/brun/rouge, densité verdure. **Matériaux per-tag** (`TH_{tag}_*`). Overview : `docs/city-refs/Building_Townhouse_variants.png`. **Exportées FBX → `Assets/Models/City/Building_Townhouse_{A..E}.fbx`** (base+veg = 2 meshes/fichier, pivot centré/sol, -Z/Y, import Unity OK sans erreur). TODO : remap matériaux URP/Lit + prefabs.
- ✅ Tour moderne verre/béton (Building_Modern_A) — **haute tour** (podium lobby + curtain wall + penthouse retrait/terrasse-jardin), dalles cantilever + piliers d'angle, 4 faces vitrées, brise-soleil, balcons plantés, RDC storefront grillé + porte double vitrée cerclée. Palette chaude (béton crème, spandrels coral/turquoise/jaune). Hero : `docs/city-refs/Building_Modern_A.png`. Base ~2.4k + `_Veg` ~9.8k. Builder : `scripts/blender/modern_builder.py`.
- ✅ Variantes Modern (×5 : A–E) — `build_modern(tag,ox,conc,spand,NF,dens)` : hauteurs **R+5→R+8** (NF), teintes béton crème/sable/rosé, spandrels per-tag (coral/turquoise/jaune/vert), densité verdure. **Matériaux per-tag** (`MD_{tag}_*`). Overview : `docs/city-refs/Building_Modern_variants.png`. **Exportées FBX → `Assets/Models/City/Modern_{A..E}.fbx`** (base+veg = 2 meshes/fichier, pivot centré/sol, -Z/Y, import Unity OK sans erreur, hiérarchie `[Modern_x, Modern_x_Veg]` vérifiée). TODO : remap matériaux URP/Lit + prefabs.
- ⬜ Toits végétalisés (variantes de toit interchangeables)

## 2. Rue / Sol
Kit modulaire route. **Module carré 8×8** (unités Unity), chaussée 2 voies (largeur 5.3),
bordure béton clair à ±2.60 (top 0.18), trottoir dès 2.70 (dalles claires + joints).
Toutes les tiles partagent ce gabarit → **snappent sur grille de 8**. Builder générique
`scripts/blender/street_builder.py::build_tile(tag,ox,dirs,crossing,verge)` (dirs =
sous-ensemble de {N,S,E,W}). Pivot export = centre tile au sol (surface route z=0).
Végétal SOL = **feuilles mortes plates (forme feuille) + brins fins dans les jointures**
(pas de blobs ronds au sol — refus user). Blobs `organic()` = OK canopées/gazon.

- ✅ Trottoir droit (bordure + dalle) — intégré aux tiles (dalles claires + joints + herbes).
- ✅ Route droite (Road_Straight_A) — 2 voies + axe pointillé + rives + 4 avaloirs grille +
  plaque d'égout détaillée + joints dilatation/fissure + feuilles/herbes. Hero :
  `docs/city-refs/Road_Straight_A.png`. Base ~600 + veg ~480 tris.
- ✅ Virage (Road_Corner_A) / croisement T (Road_T_A) / croisement X (Road_X_A) — bordures
  d'angle en L, blocs trottoir d'angle, lignes d'arrêt aux bouches (T/X), manhole centrale.
- ✅ Passage piéton (Road_Crosswalk_A) — zébra + lignes d'arrêt (droite N/S).
- ✅ Bande enherbée (Road_Verge_A) — bandes gazon latérales + **arbres alignés facettés**
  (tronc + canopée organic) + touffes + feuilles. Hero : `docs/city-refs/Road_Verge_A.png`.
  Veg ~7k (arbres) → LOD/décimation plus tard. Overview kit : `docs/city-refs/Street_Kit_overview.png`.
  **Exportées FBX → `Assets/Models/City/Street/Road_{Straight,Corner,T,X,Crosswalk,Verge}_A.fbx`**
  (base+`_Veg` = 2 meshes/fichier, pivot centre/sol, import Unity 0 erreur). TODO : remap
  matériaux URP/Lit + prefabs + variantes couleur (A→B..E) si besoin.
- ⬜ Place / dalle piétonne

## 3. Props urbains
Petits objets = 70% du ressenti "vivant". Beaucoup > gros détaillés.
**Style commun** : fonte teal `City_Iron_Green` + accents laiton `City_Brass`, palette chaude,
facetté, échelle calée sur le kit rue. Builder `scripts/blender/prop_builder.py`. Chaque prop =
base + `*_Veg` séparé. FBX → `Assets/Models/City/Props/`.

- ✅ Lampadaire (Prop_Lamppost_A) — fonte teal + laiton, lanterne **émissive** (glow), bras
  courbe, **panier fleuri suspendu** (bol terracotta + fleurs pétales/cœur variées) sur hook
  dédié. Hero : `docs/city-refs/Prop_Lamppost_A.png`. ~4 m. Variantes ×2-3 : à faire.
- ✅ Banc public (Prop_Bench_A) — flancs fonte + lattes bois (assise+dossier+accoudoirs).
- ✅ Poubelle de rue (Prop_Bin_A) — corps hexa fonte + bandes laiton + chapeau sur écarteurs.
- ✅ Feu tricolore (Prop_TrafficLight_A) — 3 feux **émissifs** (R/A/V) + casquettes + module piéton.
- ✅ Arrêt de bus / abribus (Prop_Shelter_A) — structure fonte + **verre transparent** (alpha),
  banc bois intérieur, **toit végétalisé**, totem panneau bus (décalé DEHORS de l'abri).
- ✅ Bouche incendie (Prop_Hydrant_A) — rouge pompier + caps/bande laiton.
- ✅ Borne / plot (Prop_Bollard_A) — fonte + bague laiton + boule finiale.
- ✅ Panneau signalisation (Prop_Sign_A) — **STOP en vrai texte mesh** (FONT→convert) sur octogone
  rouge + panneau directionnel **fanion pointu d'un seul tenant** + barre blanche.
- ✅ Déchets (Prop_Litter_A) — **sacs plastique** (sphère bombée + goulot pincé + nœud) + détritus
  (canette/gobelet/papiers). PAS des cailloux (1er jet refusé).
- ✅ Jardinière fleurie sur pied (Prop_Planter_A) — bac terracotta + pieds fonte + fleurs
  (pétales+cœur) + retombantes. Vegetalisé.
- **Variantes couleur fonte (4 gammes, retour user)** : Teal / Bordeaux / Navy / Anthracite.
  Livrées en **swap matériau Unity** (1 mesh + slot `City_Iron_Green` → 4 mat URP
  `Assets/Materials/City/City_Iron_{Teal,Bordeaux,Navy,Anthracite}`), PAS 16 FBX bakés.
  RGB : Teal(.19,.33,.30) Bordeaux(.44,.19,.20) Navy(.17,.24,.37) Anthracite(.24,.25,.27).
  Sheets : `docs/city-refs/Props_Furniture.png` `Props_Small.png` `Props_Colorways.png`.
  **10 props FBX → `Assets/Models/City/Props/` (import Unity OK, hiérarchie vérifiée).**
- ⬜ Vitrine / enseigne / néon (×4-6)
- ⬜ Clim / antenne / gouttière (détails façade réutilisables)
- ⬜ Câbles / fils électriques
- ⬜ Détails sol : déchets, plaque d'égout, flaque

## 4. Végétation (couche dédiée — priorité utilisateur 🌱)
Placeables autonomes, facetté low-poly, feuillage = `_Veg` séparé (vent). Builder
`scripts/blender/veg_builder.py`. FBX → `Assets/Models/City/Vegetation/` (11 assets).

- ✅ Arbre de rue ×4 essences/tailles (Veg_Tree_A rond, _Big grand, _Small petit,
  _Blossom cerisier fleuri rose/blanc) + ✅ Conifère (Veg_Conifer_A, pin étagé). Hero :
  `docs/city-refs/Veg_Trees.png`, `Veg_Tree_A.png`.
- ✅ Buisson (Veg_Bush_A) + buisson fleuri (Veg_Bush_Flower).
- ✅ Haie (Veg_Hedge_A) — bloc taillé + dessus bombé fusionné (fix flottement).
- ✅ Plante en pot fleurie (Veg_Pot_A) + arbre en pot / topiaire (Veg_Pot_Tree).
- ✅ Touffe d'herbe (Veg_Grass_A). Hero shrubs : `docs/city-refs/Veg_Shrubs.png`.
- ⬜ Lierre / plante grimpante façade (module drapable) · Toit végétalisé (module) — si besoin
  (déjà présents intégrés sur bâtiments/abribus).

## 5. Véhicules
Cartoon low-poly chunky (bbox biseautée), front = -Y, échelle kit rue. Builder
`scripts/blender/vehicle_builder.py`. **Couleur = slot `Vehicle_Body`** → recolor par
**swap matériau URP** (10 gammes `Assets/Materials/Vehicles/Vehicle_{Coral,Teal,Yellow,Red,
Sky,Orange,Mint,Cream,Lavender,Plum}`), PAS de FBX bakés. FBX → `Assets/Models/City/Vehicles/`.

- ✅ Voiture citadine (Vehicle_Car_A) — cabine = **bandeau vitré vertical + toit + montants
  A/B/C**, roues jantées, pare-chocs chrome, phares/feux, plaque, rétros rattachés. Hero :
  `docs/city-refs/Vehicle_Car_A.png`.
- ✅ Berline (Vehicle_Sedan_A) — plus longue, greenhouse 3 vitres.
- ✅ Camionnette (Vehicle_Van_A) — caisse haute, pare-brise+vitres avant, porte latérale.
- ✅ Bus (Vehicle_Bus_A) — long, rangée de vitres+montants, porte, bande, 2 essieux.
- ✅ Vélo (Vehicle_Bike_A) — cadre losange, **roues tore ajouré+rayons**, pédales à 180°.
- ✅ Moto (Vehicle_Moto_A) — topline réservoir/selle/coque, fourche inclinée, gros pneus
  jantés, phare rond, moteur à ailettes, échappement. Hero : `docs/city-refs/Vehicle_Moto_A.png`.
- ⬜ Camion (porteur / semi) · Scooter · Trottinette · vélo cargo (si besoin)

## 6. Piétons (vie mouvante) 🚶
Cast de variantes pour `Assets/Scripts/Gameplay/Pedestrian.cs` (proto CarTest, style
Rayman : tête/mains flottantes, LISSE smooth+subsurf). Base sculptée réutilisée
(`Assets/ModelBlender/character.blend`) + features modelées + **rig d'os léger**
(bone-parenting). Builder `scripts/blender/pedestrian_builder.py`.

- ✅ **Cast ×8** (Oldman, Woman, Kid, Business, Hipster, Granny, Capguy, Sunhat) —
  genres/âges variés : coiffes (chauve/long/bun/ponytail), chapeaux (casquette/
  beanie/paille), lunettes, barbes/moustache, robes/jupes/tablier, sacs (à main/dos/
  mallette), corpulences (kid réduit). Hero : `docs/city-refs/Pedestrians.png`.
  **Drop-in `Pedestrian.cs`** : pièces `PedHead/PedHandL/PedHandR/PedStacheL/R`
  conservées ; rig d'os `Root/Body/Head/HandL/HandR` (Head pivot au cou) pour
  l'Animator Unity. **Exportés FBX → `Assets/Models/City/Pedestrians/Pedestrian_{Tag}.fbx`**
  (orientation identique au Pedestrian.fbx existant : size 1.15×1.28×0.82, rootRot 270X ;
  import Unity OK, 0 erreur, HeadBone présent, MeshRenderers rigides). Leçons §27-30.
  TODO : remap matériaux URP/Lit + prefabs + brancher dans la scène + (option) clips Animator.

---

## Journal d'avancement
- 2026-07-23 — **Oiseaux ×2** (pigeon + oiseau de vol lointain) modélisés, validés,
  exportés, importés Unity `Assets/Models/City/Birds/`. **Pigeon** = drop-in pour
  `Assets/Scripts/Gameplay/Pigeon.cs` (remplace le placeholder primitif) : cute chunky
  LISSE, corps plombé/poitrine bombée, aile REPLIÉE plaquée sur le flanc (bord foncé),
  queue relevée, bec/yeux/pattes orange ; parts `Head/WingL/WingR` (pivot épaule, battent
  autour de Z). **SkyBird** = silhouette de vol vue de loin (ailes déployées balayées +
  dièdre, queue fourchue, low-poly, dos sombre/ventre clair). Bugs+fixes : yeux placés hors
  du rayon de la tête → flottaient (recaler SUR la surface) ; aile qui décollait → racine
  embed dans le corps + bout qui se tuck. Builder `scripts/blender/pigeon_builder.py`.
  Heros : `docs/city-refs/Pigeon.png`, `docs/city-refs/SkyBird.png`. Import Unity 0 erreur,
  orientation rootRot 270X (comme Pedestrian/Pigeon existants). TODO : brancher Pigeon.fbx
  dans `Assets/Prefabs/Pigeon.prefab` + prefab SkyBird + matériaux URP.
- 2026-07-23 — **Piétons : cast ×8 riggé** (Oldman/Woman/Kid/Business/Hipster/Granny/
  Capguy/Sunhat) modélisé, validé, exporté, importé Unity `Assets/Models/City/Pedestrians/`.
  Retour user MARQUANT : ne pas empiler des primitives — **réutiliser l'anatomie sculptée
  de la base** (`character.blend`) + **modeler** les features (cheveux casque+mèches extrudées,
  chapeaux épousant/posés, lunettes tores, barbes, robes à anneaux, sacs bevel+tore). Puis
  **rig d'os léger** (demande user) via **bone-parenting** (Root/Body/Head/HandL/HandR, Head
  pivot au cou) → Animator Unity + drop-in `Pedestrian.cs` (noms de pièces conservés).
  Bugs+fixes consignés `docs/city-session-lessons.md` §27-30 : (27) reuse base sculptée vs
  primitives ; (28) chapeaux (casquette épouse le crâne, paille se pose dessus « rentrait dans
  la tête », pas de cheveux sous chapeau) ; (29) rig bone-parenting, matrix_world peu fiable →
  moustache reste enfant de PedHead, export axis identique à l'existant ; (30) pièges contexte
  MCP (temp_override pour ops, copie manuelle, append hors fichier courant). Builder
  `scripts/blender/pedestrian_builder.py`.
- 2026-07-23 — **Végétation dédiée ×11** (4 arbres + conifère + 2 buissons + haie + 2 pots +
  herbe) modélisée, validée, exportée, importée Unity `Assets/Models/City/Vegetation/`.
  Feuillage `_Veg` séparé (trees/conifère/pot/herbe = 2 meshes ; buisson/haie tout-feuillage =
  1 mesh). Retours user lessons §26 : **haie** = touffes calées sur la longueur du bloc +
  descendues pour fusionner (sinon bouts flottent — « des trucs qui flottent ») ; arbre fleuri =
  blossom DENSE rose+blanc ; conifère = cônes étagés. Builder `veg_builder.py`.
- 2026-07-23 — **Véhicules ×6 + 10 gammes recolor** (voiture, berline, camionnette, bus,
  vélo, moto) modélisés, validés, exportés, importés Unity `Assets/Models/City/Vehicles/`.
  Nouveau type géo = carrosserie biseautée (`bbox`). Couleur = slot `Vehicle_Body` → **swap
  matériau URP** (10 mat `Assets/Materials/Vehicles/`), pas de FBX bakés. Retours user consignés
  lessons §23-25 : cabine = bandeau vitré vertical+montants (pas de pare-brise incliné bricolé) ;
  vérifier SENS des rotations + pare-chocs qui chevauchent (flottait) + rétro rattaché ; moto =
  silhouette topline+fourche inclinée (« ressemblait pas à une moto ») ; vélo roues tore ajouré +
  pédales à 180°. Builder `vehicle_builder.py`.
- 2026-07-23 — **Props urbains lot complet (10) + 4 gammes couleur**. Ajout 5 petits props
  (bouche incendie, borne, panneau STOP, déchets, jardinière fleurie). Fixes retours user :
  totem abribus sorti de l'abri ; panneau directionnel refait en fanion pointu (losange qui
  sortait) ; **STOP écrit en vrai texte mesh** (objet FONT converti) ; déchets = sacs plastique
  (lisaient comme cailloux). 4 gammes fonte (Teal/Bordeaux/Navy/Anthracite) livrées en **swap
  matériau URP** (pas de FBX bakés). **RÈGLE PROCESS durcie** (mémoire [[validate-before-export]])
  : ne plus jamais importer Unity avant validation user explicite — j'avais importé les 5 premiers
  props sans OK. Leçons §16-19. 10 FBX (ré)importés `Assets/Models/City/Props/`.
- 2026-07-23 — **Props urbains ×5** (lampadaire, banc, poubelle, feu tricolore, abribus)
  modélisés, validés, exportés, importés Unity `Assets/Models/City/Props/`. Nouveau style
  commun props : **fonte teal + laiton**, échelle calée kit rue. Builder `prop_builder.py`.
  3 leçons user consignées `docs/city-session-lessons.md` §16-18 : (16) lanterne/feux qui
  "glow" = matériau **émissif** ; (17) verre lisible = **transparent** (alpha blend) sinon
  lit caisse fermée ; (18) fleur lisible = **pétales + cœur jaune** (pas un blob vert), panier
  = bol visible + fleurs colorées dessus sur hook dégagé. Signature végétalisée gardée
  (panier fleuri lampadaire, toit végétal abribus).
- 2026-07-23 — **Kit Rue/Sol** (6 tiles) modélisé, validé, exporté, importé Unity. Premier
  asset **modulaire** de la ville : module 8×8, chaussée 5.3, gabarit commun → snap grille.
  Builder GÉNÉRIQUE `street_builder.py::build_tile(dirs,crossing,verge)` : un seul code sort
  droite {N,S} / virage {N,E} / T {N,S,E} / X {N,S,E,W} + flags passage-piéton (zébra) &
  bande-enherbée (gazon + arbres alignés). Détails : bordure béton, trottoir dalles+joints,
  avaloirs grille à barreaux, plaque d'égout (jonc/tampon/nervures/plots/trous). **Retour user
  marquant** : verdure SOL = feuilles mortes plates en FORME de feuille + brins fins dans les
  jointures (blobs ronds au sol = REFUSÉS) ; bitume réchauffé (pas gris froid) ; joints tar
  affinés. Consigné `docs/city-session-lessons.md` §13-15. FBX base+`_Veg` → `Assets/Models/City/Street/`.
- 2026-07-23 — **Building_Modern_A–E** (tour moderne verre/béton) modélisée, validée,
  5 variantes + FBX + import Unity (2 meshes base+`_Veg`, 0 erreur). Nouveau type =
  **tour haute en couches** (podium/curtain wall/penthouse retrait+terrasse), dalles
  cantilever, 4 faces vitrées, brise-soleil, balcons plantés, palette gardée chaude.
  Retour user marquant (3 tours) : **porte vitrée illisible sur pan de verre continu**
  → fix = casser la plaque (grille métal storefront) + cercler/faire saillir la porte,
  garder le verre (essai porte opaque bois REFUSÉ). Leçon process : clarifier la
  *direction* d'un fix ambigu AVANT de reconstruire. Consigné `docs/city-session-lessons.md`
  §11-12. Builder : `scripts/blender/modern_builder.py::build_modern(tag,ox,conc,spand,NF,dens)`.
- 2026-07-23 — **Building_Townhouse_A–E** (maison de ville étroite, toit pignon)
  modélisée, validée, 5 variantes + FBX + import Unity. Nouveautés méthode :
  toit 2 pentes = **dalles SOLIDES fermées** (`slope_slab`, rives bouchées, pas de
  prisme plein qui fait un gros triangle moche — refus user) ; **rangs de tuiles**
  (`tile_courses`, marches proéminentes non coplanaires) ; **cheminée traversante**
  (bas sous la surface de pente, jamais de flottement) ; pignons = triangle extrudé
  (`gable_solid`). 2 retours user consignés dans `docs/city-session-lessons.md`
  §9-10 : (9) toit = solide fermé, vérifier 0 arête ouverte ; (10) **RÈGLE GÉNÉRALE
  jamais 2 faces coplanaires superposées = z-fight**. Builder :
  `scripts/blender/townhouse_builder.py`. Import Unity : 5 FBX, 2 meshes chacun
  (base+`_Veg`), 0 erreur.
- 2026-07-23 — **Feuillage = enfant séparé à l'export** (demande user : oscillation
  vent). Vérif : Corner A–E & Apartment A–E déjà `base + *_Veg` séparés ✓. **Shop
  A–E étaient fusionnés en un seul mesh** (`build_shop`→`finish()` joignait tout)
  → corrigé : `organic()` pousse dans liste `VEG` séparée, `build_shop` renvoie
  `(base, veg)`. Ré-exportés + réimportés Unity (overwrite via copie filesystem +
  refresh, guid conservé). Règle consignée dans `docs/city-session-lessons.md`
  (§ Objet séparé pour le végétal) + mémoire.
- 2026-07-23 — **Building_Corner_A–E** exportés FBX + importés Unity
  `Assets/Models/City/` (base+veg séparés, pivot centré/sol, -Z/Y). Sans erreur.
- 2026-07-23 — **Building_Corner_A** (immeuble d'angle chanfreiné R+5) modélisé
  & validé. Nouvelle méthode : footprint pentagonal (coin coupé 45°) extrudé
  via `prism()` bmesh + `obox()` (boîte orientée par angle de face) → fenêtres
  posées sur les 3 faces dont le chanfrein sans trigo à la main. Entrée sur le
  pan coupé (porte/auvent/enseigne/marche/lampes/pots), RDC vitrines, garde-corps
  barreaux coral, jardinières étages alternés, descentes EP, toit habité. Base
  ~2.9k + `_Veg` séparé ~18.9k. Builder rangé dans `scripts/blender/corner_builder.py`.
  2 bugs rencontrés + fixes → **`docs/city-session-lessons.md`** (§7 fenêtres qui
  se chevauchent car cadres trop larges/faces étroites → moins de colonnes + gap
  ≥0.4 ; §8 croix/meneaux coplanaires au verre = z-fight → avancer le meneau).
  Retour user : capitaliser au fur et à mesure SANS qu'on le demande + regarder
  ses propres renders avant d'affirmer qu'un fix tient. Reste : variantes + FBX + prefabs.

- 2026-07-23 — **Building_Apartment_A** (immeuble R+5) modélisé & validé. Base
  détaillée (volets, gouttières, clims, appuis, toit habité cheminée/édicule/
  antenne/parabole/garde-corps, entrée marche+plaque+lampe, fenêtres 2 faces) +
  végétal facetté style Shop_A (jardinières balcons+retombantes, jardinières
  fenêtres, lierre angle, toit végétalisé+arbre, pots entrée). Base ~12.4k tris,
  veg ~15.1k tris (objet séparé `_Veg`). Hero rangé dans `docs/city-refs/`.
  6 bugs rencontrés + fixes → consignés dans **`docs/city-session-lessons.md`**
  (gros piège : `scale=size/2` divise le bâtiment par 2 → détails « flottent »).
  Puis 5 variantes paramétrées (`build_apartment`) + export FBX + import Unity
  `Assets/Models/City/`. Reste : remap matériaux URP/Lit + prefabs.

- 2026-07-23 — Pipeline documentée dans `docs/city-pipeline.md`. Export FBX des 5
  variantes → import Unity `Assets/Models/City/Shop_{A..E}.fbx` (origine au sol,
  face -Z, up Y). TODO : vérifier/remapper matériaux en URP/Lit (palette chaude).

- 2026-07-23 — Setup Blender MCP. Building_Shop_A v1 (72 verts) modélisé & rendu.
  Décision : monter le niveau de détail + végétaliser toute la ville.
- 2026-07-23 — Building_Shop_A finalisé étape par étape (objets posés, pas de boolean) :
  détails archi (cadres/meneaux/corniches/plinthe/clim/gouttière/enseigne/cheminée/antenne),
  perron 3 marches, végétal complet (jardinières, lierre, toit végétalisé, pots entrée). 978 verts.
  Piège noté : un crash de script laisse des objets orphelins dans la scène → toujours nettoyer
  les objets hors {asset, Camera, lights} avant de re-render.
  Workflow render validation : caméra 3/4 (11,-12,7) + render_viewport_to_path → lecture PNG.
