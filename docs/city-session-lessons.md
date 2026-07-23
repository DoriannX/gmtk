# Leçons de session — modeling ville (à lire avant tout nouvel asset)

Erreurs réelles commises en produisant **Building_Apartment_A** (immeuble R+5) et
comment elles ont été réglées. But : ne pas les refaire. Complète les PIÈGES de
`docs/city-pipeline.md`.

---

## Bugs géométriques rencontrés + fix

1. **Échelle divisée par 2 (le gros piège).**
   `bpy.ops.mesh.primitive_cube_add(size=1)` crée un cube **unité ±0.5** (dim
   pleine = 1). Si le helper fait `o.scale = size/2`, la dimension réelle vaut
   **size/2** → tout le bâtiment sort à la moitié de la taille voulue.
   Symptôme trompeur : les détails placés par **coordonnée absolue** (fenêtres à
   `cx=±1.9`) tombent **hors** du mur rétréci → colonnes de fenêtres qui
   « flottent » détachées sur le côté. On a cru à une illusion de caméra pendant
   4 itérations.
   **Fix :** `o.scale = (size[0], size[1], size[2])` (dim pleine, le cube unité
   fait déjà ±0.5).
   **Réflexe :** après un build, **vérifier la bbox** (`min/max v.co.x`) contre
   les dims voulues AVANT de juger un « flottement ». Ne pas blâmer la caméra
   tant que la bbox n'est pas confirmée.

2. **Objets qui volent sur le toit.**
   Empilement Z avec des trous : bases de l'antenne/parabole/dalle posées
   au-dessus de la surface du toit → flottent.
   **Fix :** définir **une** surface `ROOF` et poser chaque élément base=ROOF via
   un helper `stand(loc,size)` (centre = base + size_z/2) / `post(x,y,base,h)`
   (centre = base + h/2). Jamais de Z « à l'œil » pour les objets posés.

3. **Fenêtres de la face latérale = rectangles crème aveugles.**
   Verre placé vers l'**intérieur** du mur (`SIDE-0.02`) → caché derrière la
   paroi ; seul le cadre dépassait → panneau plein.
   **Fix :** le verre doit **dépasser vers l'extérieur**. Le signe dépend de la
   face : façade avant `-Y` → dehors = `F-` (F négatif) ; face `+X` → dehors =
   `SIDE+`. Confirme le sens « sortir de la façade » avant de placer.

4. **Fenêtres du dernier étage qui rentrent dans la corniche/toit.**
   Le haut de la fenêtre du dernier niveau dépassait le bas de la corniche.
   **Fix :** baisser + réduire les fenêtres du dernier étage (facteur de hauteur
   d'étage 0.55→0.50, hauteur fenêtre 0.9→0.82) pour dégager. Vérifier
   `cz_haut_fenêtre < z_bas_corniche` au dernier niveau.

5. **Plantes retombantes qui flottent sous les balcons.**
   Clump posé sous la dalle (`bz-0.18`), rien ne le relie → flotte dans le vide.
   **Fix :** une plante retombante doit **draper sur le rail avant** en
   chevauchant dalle + jardinière (objet source). Toute végétation « suspendue »
   doit toucher un support visible.

6. **Feuillage ballonné (subsurf).**
   Ajouter un modifier **Subdivision Surface** sur les clumps les lisse en boules
   → casse le style. L'utilisateur n'aime pas.
   **Fix :** reproduire l'`organic()` de Shop_A **à l'identique** : icosphère
   `sub=2` + bruit cohérent **2 octaves** (`*1.7`+`*3.6`, poids 0.7/0.3) déplacé
   le long de la normale + jitter ±0.05 + **bmesh bevel** (offset 0.03, 1 seg) +
   scale ~uniforme + **flat shading**. **PAS de subsurf.** Pour « plus de poly +
   plus organique » : monter `sub` et l'amplitude du bruit, PAS lisser au subsurf.

---

## Matériaux : réutiliser les noms canoniques `palette()`

Les couleurs de `Building_Apartment_A` avaient les **bonnes valeurs RGB** mais ont
d'abord été créées sous des **noms inventés** (`City_Wall_Cream`, `City_Stone_Warm`,
`City_Coral_Trim`, `City_Turq`…) → **doublons** de matériaux vs la palette
canonique → divergence à l'export Unity. Corrigé en renommant vers les noms de
`scripts/blender/city_builder.py::palette()`.
**Règle :** toujours réutiliser les noms EXACTS de `palette()` — ne jamais créer
un nouveau matériau pour une couleur qui existe déjà. Noms canoniques :
`City_Wall_Beige, City_Window_Glass, City_Roof_Grey, City_Metal_Dk,
City_Terracotta, City_Leaf, City_Leaf_Dk, City_Flower, City_Trim_Red,
City_Door_Wood, City_Frame_White, City_Sign`. (Idéalement : appeler `palette()`
en début de build plutôt que redéfinir les matériaux à la main.)

## Règle de vérification (checklist post-build, avant de montrer un render)

- **Bbox** conforme aux dimensions voulues (attrape le bug d'échelle).
- **Rien ne flotte** : chaque objet posé touche une surface (toit, dalle, sol,
  jardinière). Rendre une vue **plongeante** (toit) + une vue **frontale** (faces
  + fenêtres) — chaque angle attrape une classe de défaut différente.
- **Fenêtres** : verre en saillie vers l'extérieur, visible.
- **Dernier étage** dégage la corniche/toit.

---

## Retours & préférences utilisateur (cette session)

- **Validation incrémentale stricte** : un incrément à la fois, render + validation
  à CHAQUE étape (déjà dans le pipeline, confirmé).
- **Balcons à barreaux** > parapet plein. Préfère les garde-corps ajourés
  (poteaux + rail + barreaux) qu'on voit à travers.
- **Beaucoup de détails archi** avant le végétal (« ajoute encore des détails »):
  volets, gouttières, clims, appuis, toit habité (cheminée, édicule, antenne,
  parabole, garde-corps), entrée (marche, plaque, lampe), fenêtres sur toutes les
  faces (pas de mur nu).
- **Végétal = style Shop_A** (facetté low-poly), jamais lissé/ballonné.
- **Plus de poly / plus organique** sur le végétal est souhaité — via bruit +
  bevel + subdiv de l'icosphère, pas via subsurf.
- Repère vite les **géométries qui flottent ou s'interpénètrent** — les corriger
  proprement (grounding), pas les ignorer.
- Veut une **galerie de références** (`docs/city-refs/`) et que chaque nouvel
  asset la consulte pour rester cohérent.

---

## Bugs géométriques — Building_Townhouse_A (maison de ville étroite, toit pignon)

9. **Toit en plans empilés = on voit l'intérieur / backface.** Premier toit 2
   pentes fait de **plans simples** : pente dessus + pente dessous décalée en Z +
   bouts (verges) **laissés ouverts**. EEVEE cull les backfaces → on voyait
   l'intérieur du toit par les bords/bouts, et les 2 plans parallèles (dessus vs
   dessous à 0.14) = **coplanaires** → z-fight.
   **Fix (bon look, retour user) :** garder les **2 pans distincts** (le look 2
   pavés que le user aime) mais faire **chaque pan = dalle SOLIDE fermée** : quad
   dessus + quad dessous décalé de −T en Z + les **4 côtés** (dont les 2 rives/
   verges) → 6 faces, `recalc_face_normals`, 0 arête ouverte. Ainsi l'épaisseur du
   toit reste visible et les rives sont bouchées.
   **NE PAS** remplacer par un **prisme triangulaire plein** (verge = 1 gros tri
   plein + soffite) : ça fait un « gros triangle plat moche », le user a refusé.
   Pignons = triangle **extrudé en épaisseur** (prisme WALL), pas un plan simple.
   **Réflexe :** après un mesh custom, vérifier `sum(len(e.link_faces)!=2)==0`
   (aucune arête ouverte) AVANT de juger le render.

10. **RÈGLE GÉNÉRALE (retour user 2026-07-23) : jamais deux faces coplanaires
    superposées.** Deux faces dans le même plan (ex. dessus/dessous de toit
    parallèles, détail plaqué à ras d'une surface, deux boîtes à ras) = **z-fight
    / glitch de faces** garanti. Toujours soit **fusionner en un solide**, soit
    **décaler franchement** (une face strictement devant l'autre, cf §8 meneaux) —
    jamais à égalité. Intersection en T / traversée (une boîte qui coupe une
    surface) est OK ; c'est la **coplanarité exacte** qui glitche.
    - Faîtière/rive : box qui **surplombe** (faces verticales vs pente inclinée =
      non coplanaires), pas plaquée à ras de la pente.

---

## Bugs & retours — Building_Modern_A (tour moderne verre/béton)

11. **Porte vitrée illisible = "porte-sur-verre" (3 tours pour cerner le besoin).**
    RDC entièrement vitré : une porte en verre posée sur un **pan de verre continu**
    est invisible (même teinte, même plan) → on ne lit pas la porte. Le user a
    d'abord dit « bizarre », puis j'ai mal cerné : j'ai (a) tenté une **porte
    opaque bois** → REFUSÉ (il voulait garder le verre), puis compris que **le vrai
    problème = la grande plaque de verre**, pas la porte.
    **Fix retenu :** garder la **porte EN VERRE** mais (1) **casser la plaque** :
    storefront = **grille métal** (rail bas + rail haut/imposte + meneaux verticaux)
    qui découpe le verre en travées encadrées ; (2) **cercler + faire saillir la
    porte** : cadre métal (montants + linteau) + 2 vantaux **bordés de métal**
    (`leaf_frame` derrière, verre devant) + poignées, le tout sur un **plan avancé**
    (offsets Y distincts : verre fixe −0.08, cadre −0.10, vantail −0.16, poignée
    −0.21) → la porte **ressort** de la façade et se lit.
    **Règle durable :** tout **élément vitré sur fond vitré** (porte, imposte,
    vantail) se distingue par **un cadre opaque (métal) + une saillie de profondeur**
    — jamais coplanaire, jamais même matériau sans cadre. Prolonge §10.
    **Réflexe process :** quand un retour user est ambigu sur la *direction* du fix
    (garder/enlever le verre ?), **poser la question ciblée AVANT de reconstruire**
    plutôt que deviner — j'ai gaspillé 2 rebuilds en devinant.

12. **Nouveau type = tour moderne (massing en couches).** Silhouette signature vs
    les 4 bâtiments chauds trapus : **haute**, **podium lobby** + **tour curtain
    wall** + **penthouse en retrait** (crée une terrasse-jardin sur le toit de la
    tour principale). Horizontalité moderne = **dalles cantilever** débordantes à
    chaque étage + **piliers d'angle** béton. **4 faces vitrées** (aucune face nue —
    le dos aussi, sinon « face nue » signalée). Accents modernes : **brise-soleil**
    horizontaux, **balcons plantés** cantilever. **Palette reste CHAUDE/JOYEUSE**
    (béton crème `MD_*_conc`, verre bleu ciel, spandrels coral/turquoise/jaune) —
    un immeuble moderne n'impose PAS des gris froids ici.
    Méthode réutilisable : `scripts/blender/modern_builder.py::build_modern(tag,ox,
    conc,spand,NF,dens)`. Matériaux **per-tag** pour béton + spandrels (`MD_<tag>_*`)
    afin de ne pas écraser les canoniques partagés (cf. Corner/Townhouse) ; verre/
    blanc/métal/terracotta/feuilles restent canoniques.

---

## Kit Rue/Sol — premier asset MODULAIRE (tiles qui snappent)

13. **Gabarit commun = condition du snap.** Un kit de rue n'est utile que si toutes les
    tiles s'alignent sur une grille. Fixer AVANT de modéliser : **module carré 8×8**,
    chaussée demi-largeur `CA=2.65`, bordure `C=2.60` (top 0.18), trottoir dès `SW=2.70`
    (top 0.15), **surface route à z=0**, **pivot export = centre tile au sol**. Toute tile
    (droite/virage/T/X/passage/verge) réutilise EXACTEMENT ces constantes → bords identiques
    → elles se posent bord à bord sans retouche. Ne jamais improviser une largeur par tile.

14. **Builder GÉNÉRIQUE par jeu de directions >> un builder par tile.** Modéliser
    séparément droite, virage, T, X = 4× le travail + 4× le risque d'incohérence. À la place :
    `build_tile(dirs)` avec `dirs ⊆ {N,S,E,W}` = **bras de chaussée présents**. Recette :
    chaussée = carré central + un bras par dir ; trottoirs = 4 blocs d'angle (toujours) +
    remplissage des côtés absents ; bordures = flancs de bras (présents) + fermeture des côtés
    absents + 4 caps d'angle. Résultat : {N,S}=droite, {N,E}=virage, {N,S,E}=T, {N,S,E,W}=X,
    un seul code. Flags `crossing` (zébra) & `verge` (gazon+arbres) = variantes du même moule.
    **Piège coplanarité évité** : segments de bordure disjoints (flancs y∈[2.7,4], caps à
    ±2.6, fermetures x∈[-2.5,2.5]) → aucun chevauchement coïncident = pas de z-fight (cf §10).

15. **RÈGLE DURABLE (retour user) — verdure au SOL ≠ blobs ronds.** Les touffes `organic()`
    (icosphère bruitée) lisent comme des **buissons** : bien pour balcons/toits/canopées, mais
    **posées au sol elles sont refusées**. Pour le sol : (a) **feuilles mortes** = petit
    polygone **ovale pointu (2 bouts)** extrudé fin (`fallen_leaf`), teintes chaudes
    (or/ambre/rouille/terracotta), éparpillées + amas au caniveau ; (b) **mauvaises herbes**
    = **brins fins** (petites boîtes verticales inclinées en éventail, `weed_tuft`) plantés
    **dans les jointures** (caniveau bordure/chaussée, joints de dalles, fissures). Corollaire
    bitume : garder **chaud** (≈0.40,0.37,0.33) — un gris neutre lit « froid » à l'écran malgré
    la valeur ; joints de dilatation/fissures = matériau tar **réchauffé + affiné** (pas de
    barre noire dure). Détail fonte qui paie : plaque d'égout = jonc + tampon + bossage +
    nervures radiales + anneau de plots + 2 trous ; avaloir = grille à barreaux + fond sombre.

## Props urbains — matériaux & lisibilité (fonte teal + laiton)

16. **"Glow" = matériau ÉMISSIF, pas juste une couleur claire.** Une lanterne / un feu
    tricolore doit **émettre** : Principled → `Emission Color` + `Emission Strength` (helper
    `mat_emit`). Un simple base color clair reste éteint/plat. (Blender 5.1 : les entrées
    s'appellent `Emission Color`/`Emission Strength` ; fallback `Emission` pour vieilles
    versions.) Lanterne glow ≈ strength 3, feux R/A/V ≈ 2.2.

17. **Verre = TRANSPARENT sinon "caisse fermée".** L'abribus premier jet avait un verre
    opaque (base color seule) → il lisait comme une **benne/caisse verte fermée**, pas un abri
    vitré. Fix : `City_Window_Glass` = Principled **Alpha 0.30 + `material.blend_method='BLEND'`**
    (EEVEE). Alors la structure fonte, le banc intérieur et le fond se lisent à travers → abri
    évident. (Côté Unity : refaire un matériau URP transparent au remap ; l'alpha Blender ne
    traverse pas le FBX.)

18. **Fleur LISIBLE = pétales + cœur, PAS un blob.** Un panier fait d'un gros `organic()` vert
    + quelques boules roses cachées dessous lit **"buisson"** (retour user : « on comprend pas
    que c'est des fleurs »). Recette qui marche : (a) **bol terracotta visible** (cône) + lèvre ;
    (b) **petit** dôme vert (ne domine pas) ; (c) **retombantes** vertes par-dessus le bord ;
    (d) **fleurs distinctes DESSUS** = `flower()` : 5 pétales (icosphères aplaties en couronne,
    légèrement relevées) + **cœur jaune**, en **couleurs variées** (rose/jaune/blanc/lavande/
    corail). Et suspendre le panier sur un **hook dédié dégagé** (pas collé dans le bras/la
    lanterne — 1er jet coincé, refusé). Généralisable à toute jardinière/massif fleuri.
    Échelle props : calée sur le kit rue (lampadaire ~4 m, feu ~3.2 m, abribus ~2.5 m).

## Props — petits props, texte, lisibilité, variantes (retours user)

19. **PROCESS (le plus important) — ne jamais exporter/importer avant validation user.**
    J'ai importé les 5 premiers props dans Unity sans OK, puis proposé des variantes juste après
    → « t'es allé beaucoup trop vite ». Ordre strict : render → retours → OK explicite → PUIS
    export/import. Voir mémoire [[validate-before-export]]. Corollaire render : quand on montre
    « les N props », faire **une vue lineup où les N tiennent dans le cadre** (un premier lineup
    coupait lampadaire+abribus aux bords → « je vois pas les 5 props »). Cadrer, vérifier le
    render soi-même avant d'envoyer.

20. **Détails props qui ont buggé + fix.**
    - **Élément qui rentre dans un autre** (totem abribus dans la structure ; losange de flèche
      qui sort du panneau) → toujours vérifier les intersections en vue rapprochée ; sortir
      franchement l'élément (totem à x2.55 hors du toit à x1.78) ; construire une flèche
      directionnelle comme **UN seul polygone pointu** (`pennant` : rectangle+pointe extrudé),
      jamais rectangle + losange séparé qui flotte.
    - **Sacs poubelle ≠ cailloux** : un `organic()` gris lit "rocher". Un sac = sphère bombée
      (`bag`) avec **goulot pincé en haut** (v.co.x/y *0.6 au-dessus de z0.4) + **nœud** (petit
      cône) + plastique sombre. Ajouter de vrais détritus colorés (canette, gobelet, papiers).

21. **Texte lisible = vrai mesh (FONT → convert), pas un décor abstrait.** Pour écrire "STOP"
    (demande user) : `bpy.data.curves.new(name,'FONT')` → `curve.body="STOP"`, `align_x/y='CENTER'`,
    `extrude=0.01`, `size=...` ; orienter `rotation_euler=(radians(90),0,0)` pour faire face -Y
    (lettres droites) ; `bpy.ops.object.convert(target='MESH')` ; puis join dans l'objet. Coûte
    ~800 tris (tessellation police) mais lit parfaitement. Généralisable à toute plaque/enseigne.

22. **Variantes de COULEUR = swap matériau, pas 16 FBX bakés (choix user).** Géométrie identique,
    seule la teinte fonte change → exporter **1 mesh/prop** (slot `City_Iron_Green`) et fournir N
    **matériaux URP** à swapper (`Assets/Materials/City/City_Iron_{Teal,Bordeaux,Navy,Anthracite}`).
    Bien plus léger que dupliquer chaque prop par couleur. (Diffère des bâtiments A–E où la GÉO
    changeait → là il fallait des FBX séparés.) 4 gammes validées ; palette chaude respectée même
    en Bordeaux/Navy/Anthracite (accent laiton conservé).

## Véhicules — carrosserie, rotations, silhouette (retours user)

23. **Cabine voiture = bandeau vitré VERTICAL + toit + montants A/B/C.** 1re version : corps +
    cabine + capot + coffre en boîtes qui se chevauchent + **pare-brise incliné avec rotation à
    l'envers** (haut penché vers l'avant) + verre géant qui dépasse du toit = brouillon illisible.
    Fix qui marche : corps bas unique (= capot+coffre), **une** cabine = `greenhouse()` : un
    **bandeau de verre vertical** (pas de rake → zéro bug de rotation) + un toit posé dessus +
    des **montants fins** (A avant, B milieu, C arrière) en carrosserie qui découpent le verre en
    fenêtres. Lit clean, cute, sans poutres parasites. Vérifier aussi : le verre transparent laisse
    voir à travers → garder les montants fins pour ne pas montrer de « poutres » épaisses.

24. **Vérifier le SENS de CHAQUE rotation + rien ne flotte.** `R_x(θ)` : un point du dessus
    (+Z) part vers **-Y pour θ>0**. Toujours rendre et regarder : un pare-brise « penché du
    mauvais côté » = signe à inverser. Rien ne doit flotter : un **pare-choc doit CHEVAUCHER la
    coque** (le poser juste devant la face laisse un gap = il flotte — vécu sur le van AV *et* AR) ;
    un **rétro** se rattache par un petit bras (`cyl_between`) au montant A, pas suspendu dans le
    vide. Seules les **roues** sont tournées (axe X, `rot=(0,π/2,0)`), tout le reste à plat.

25. **Moto = silhouette reconnaissable, sinon "ça ressemble pas à une moto".** Recette qui a
    marché (après un 1er jet refusé, trop sparse) : **topline continue** réservoir→selle→coque
    arrière (formes qui s'enchaînent) + **fourche INCLINÉE** (rake) vers la roue avant + **gros
    pneus jantés** (pneu fat + jante alu + moyeu) + **phare rond** (anneau chrome + lentille
    émissive) + **moteur à ailettes** (bloc + fines lamelles chrome) + **échappement** (tube +
    silencieux chrome) le long du bas. Juger une moto en vue de **PROFIL** (vue-clé).
    **Vélo** : roues = **tore ajouré + rayons** (pas un disque plein = ça lit "roue de brouette") ;
    **pédales/manivelles à 180°** (une avant-basse, une arrière-haute), jamais au même niveau.
    Recolor véhicules = **swap matériau** (slot `Vehicle_Body`), comme les props (cf §22).

## Végétation dédiée — haie, arbre fleuri, conifère (retours user)

26. **Haie : les touffes du dessus DOIVENT être calées sur la longueur du bloc ET fusionner
    dedans.** Bug vécu : bloc long de `±Lh/2` mais touffes placées sur `±(Lh-0.3)` (≈±1.7 pour
    un bloc ±1.0) → les touffes des bouts **flottent dans le vide** (« je sais pas c'est quoi
    ça mais y'a des trucs qui flottent »). Fix : (a) parcourir y **de `-Lh/2+0.22` à
    `Lh/2-0.22`** ; (b) placer les touffes **centre SOUS le dessus du bloc** (ex. cz 0.56 quand
    le dessus est à 0.63) → elles **s'enfoncent** dans le bloc = dessus bombé **continu**, aucune
    ne flotte. Règle générale : une touffe qui « pose » sur une surface doit **chevaucher** la
    surface (centre au niveau ou sous le dessus), jamais poser dessus avec un gap.
    - **Arbre fleuri** : le blossom doit être **dense** (≈24 clumps, taille 0.16-0.26, rose +
      blanc mélangés) sur un houppier **vert sombre**, sinon le rose est invisible (1er jet à 12
      petits blooms = « rose trop faible »). Juger de face, pas seulement de loin.
    - **Conifère** = **cônes étagés** décroissants (verts alternés clair/sombre) + tronc court +
      pointe. **Arbre feuillu** : ancrer le houppier par **4 branches** (`cyl_between` tronc→
      couronne) pour qu'il ne flotte pas au-dessus du tronc.
    - Rappel cadrage (récurrent) : pour montrer N assets, **caméra où les N tiennent** ; une
      2e rangée derrière est masquée par la 1re → rendre depuis le côté sans obstacle.

## Bugs géométriques — Building_Corner_A (immeuble d'angle chanfreiné)

7. **Fenêtres qui se chevauchent (cadres trop larges vs espacement).**
   3 colonnes sur une face étroite (droite = 3.2 de large) avec cadre `w+0.22`
   → cadres voisins qui se rentrent dedans. Symptôme : à angle 3/4 le pilastre
   fin (gap 0.08–0.22) foreshorte à ~0 → lit « fenêtres collées/fusionnées »
   même quand la géométrie ne se chevauche pas vraiment.
   **Fix :** garantir `frame_width < espacement` ET viser un **gap ≥ 0.4** pour
   que le pilastre reste lisible même foreshorté. Concrètement : face étroite →
   **moins de colonnes** (droite passée de 3→2), fenêtres **plus étroites**
   (glass 1.0→0.8, marge cadre 0.22→0.16), pilastres beige larges.
   **Réflexe :** vérifier l'espacement par une vue **ortho droite face** (annule
   le foreshorten) ET une vue 3/4 (révèle le foreshorten qui gêne le joueur).

8. **Croix/meneaux qui s'enfoncent dans le verre (z-fight).**
   Meneaux (`mv/mh`) posés au même offset normal que la face avant du verre
   → coplanaires → z-fighting, la croix « rentre » dans la vitre. Verre face
   avant à `offset_verre + demi_profondeur` ; si le meneau a la même face avant,
   il ne ressort pas.
   **Fix :** avancer le meneau pour qu'il **ressorte franchement** du verre
   (offset meneau > offset verre + ~0.05). Ici offset 0.02→**0.07** (face meneau
   ~0.19 vs verre ~0.14). Règle générale : tout détail plaqué (meneau, poignée,
   enseigne) doit avoir sa face avant **strictement devant** la surface support,
   jamais coplanaire.

## Retours & préférences utilisateur — Building_Corner_A

- **Capitaliser SANS qu'on le demande** (rappel explicite user, 2026-07-23) :
  consigner erreurs/fixes/retours au fur et à mesure est une étape obligatoire
  du pipeline, pas sur demande. J'avais oublié → faute. Voir [[city-perpetual-learning]].
- **Regarder mes propres renders avant d'affirmer « c'est réglé »** : ne pas
  déclarer un fix validé sur du raisonnement géométrique seul — rendre + vérifier
  visuellement à l'angle qui pose problème (le user a dû insister « regarde
  toi-même » alors que j'affirmais que c'était bon).
- **Nouveau type de bâtiment = immeuble d'angle chanfreiné** (pan coupé en angle
  de rue portant l'entrée). Géo : footprint pentagonal (rectangle avec 1 coin
  coupé à 45°) extrudé en `prism()` bmesh — pas de boolean.

## Méthode réutilisable émergée (corner_builder)

- **`prism(footprint, z0, z1, mat)`** : extrude un polygone plan (bmesh, recalc
  normals) → corps chanfreiné en un mesh propre. Bandeaux d'étage / corniche /
  plinthe / parapet = mêmes prisms à `scale_fp()` près.
- **`obox(px,py,pz,ang,sx,sy,sz)`** : boîte orientée en Z (angle = `atan2(ny,nx)`),
  local X=normale/profondeur, Y=tangente/largeur → **pose fenêtres/détails sur
  n'importe quelle face y compris le chanfrein 45°** sans trigo à la main.
- **`window(base,nrm,u,z,w,h)`** générique par face (normale + point-origine +
  offset tangent u) → réutilisable pour toute façade orientée.
- Code complet : `scripts/blender/corner_builder.py`.

## Objet séparé pour le végétal — RÈGLE D'EXPORT OBLIGATOIRE

Le feuillage DOIT être un **objet distinct `*_Veg`, enfant séparé de la base
dans le FBX — JAMAIS fusionné dans le mesh de base**. Deux raisons :
1. **Oscillation au vent** (demande user, 2026-07-23) : Unity a besoin du
   feuillage comme **MeshRenderer distinct** pour lui coller un shader de vent
   (sway par-vertex). Un seul mesh veg suffit — pas besoin d'un objet par touffe,
   le shader oscille les sommets. Mais s'il est fusionné à la base, impossible.
2. **LOD/décimation** indépendante (cf. piège subdiv2 ~10-15k tris).

**Vérif post-import Unity** : la hiérarchie du FBX doit être
`Root › {Base [mesh], Base_Veg [mesh]}` (2 enfants mesh), PAS un seul mesh.
Contrôler avec un walk de la hiérarchie (AssetDatabase.LoadAssetAtPath + parcours
des MeshFilter).

**Piège rencontré (Shop A–E)** : `build_shop` faisait `finish()` qui joignait
TOUT (base + organic) en un seul mesh → feuillage collé, pas d'oscillation
possible. Fix : `organic()` pousse dans une liste `VEG` séparée (pas `CUR`),
et le builder renvoie `(base, veg)` = deux objets joints séparément. Corner &
Apartment le faisaient déjà (`base_parts` / `veg_parts`).

**Réflexe pour tout nouveau builder** : séparer feuillage vs rigide DÈS le
départ (organic → liste veg), renvoyer `(base_obj, veg_obj)`, exporter les deux
sélectionnés ensemble → FBX à 2 meshes enfants.

---

## Piétons — variantes (`scripts/blender/pedestrian_builder.py`)

Cast de 8 persos (Oldman/Woman/Kid/Business/Hipster/Granny/Capguy/Sunhat) pour
`Assets/Scripts/Gameplay/Pedestrian.cs`. Style **Rayman** : tête-œuf + mains
flottantes, corps-œuf, **LISSE** (smooth + Subsurf 1, PAS facetté). Base sculptée
= `Assets/ModelBlender/character.blend` (`PedHead/HandL/HandR/StacheL/StacheR`,
front -Y, Z-up). FBX riggés → `Assets/Models/City/Pedestrians/Pedestrian_<Tag>.fbx`.

27. **NE PAS empiler des primitives quand une base MODELÉE existe (retour user
    fort).** 1er jet = sphères/boîtes collées → refusé (« tu détailles aucune forme
    avec les autres outils comme pour le 1er perso »). Fix qui marche : **DUPLIQUER
    l'anatomie sculptée de la base** (tête/nez/oreilles/yeux/sourcils/mains/
    moustache/corps, meshes indépendants via `o.copy()`+`data.copy()`) pour toutes
    les variantes, et **MODELER** seulement les features nouvelles au bmesh :
    cheveux = casque uvsphere ouvert sur le visage + **mèches extrudées**
    (`extrude_edge_only` + descente) + `SOLIDIFY` ; lunettes = **tores** (double
    boucle major/minor) + pont + branches (`cyl_between`) ; barbes = coque uvsphere
    coupée qui épouse la mâchoire ; robes/jupes = **anneaux de quads** à rayon
    ondulé (plis) ; sacs = cube `bevel` + anse tore. Résultat = même qualité que le
    1er perso. **Parenting keep-transform** (`matrix_parent_inverse` = identité si
    enfant direct du root, sinon `parent.matrix_basis.inverted()`) pour écrire en
    coords monde sans double-offset, échelle **bakée dans le mesh** (scale objet=1
    → pas de shear quand le script tourne les pièces).

28. **Chapeaux : chacun se pose DIFFÉREMMENT (bug user « le chapeau rentre dans la
    tête »).** Casquette/beanie **ÉPOUSENT le crâne** (dôme rayon ~ tête, assis au
    **front** ~z+0.07, garder l'hémisphère haut, pas de gap ; casquette + **visière
    lèvre courbe** 3 rangs accrochée au bord avant ; beanie + **revers tore**).
    Chapeau de paille = se **POSE SUR LE DESSUS** : calotte + bord AU NIVEAU DU
    SOMMET du crâne (`z ≈ head_top+0.0`, crâne top ~z+0.31), sinon la calotte
    s'enfonce. **Sous un chapeau : PAS de casque de cheveux** (une « frange »
    partielle coupée en anneau = tête en tranches/décapitée → refusé) : le chapeau
    couvre, `hair="none"`. Beanie descendu trop bas cache les yeux/lunettes → le
    remonter au front.

29. **Rig d'os LÉGER = bone-parenting, PAS skinning (demande user « rig légèrement
    pour animer sur Unity »).** Armature `Root→Body→Head(pivot au COU)→…`,
    `Body→HandL/HandR`. Chaque pièce top-level **object-parentée à son os**
    (`parent_type='BONE'`, `parent_bone=...`) → l'Animator Unity bouge les os,
    pièces restent des **MeshRenderers rigides** (style Rayman) et **gardent leurs
    noms** (`PedHead…` → `Pedestrian.cs` les retrouve via FindDeep). **PIÈGE : le
    `matrix_world` d'un objet bone-parenté est PEU FIABLE** → ne PAS bone-parenter
    des sous-pièces nichées (la moustache a été projetée à (0,-0.98,1.22)). Fix :
    laisser `PedStacheL/R` **enfants de PedHead** (suivent l'os Head ; le jiggle
    `MustacheFrenzy` reste code-driven sur le mesh). Export `axis_forward='-Z'
    axis_up='Y'` → orientation Unity **identique à l'existant** (`Pedestrian.fbx` :
    size 1.15×1.28×0.82, rootRot 270 X). Vérif Unity : `HeadBone=True`, `PedHead/
    HandL/HandR` présents, `SMR=0` (MeshRenderers, pas skinned), debout (Y = plus
    grande dim). Note : os + tween-code partagent pas le même repère → le rig sert
    surtout à animer via **Animator Unity** ; le DOTween existant fonctionne
    (retrouve les transforms) mais ses axes locaux peuvent demander un léger retune.

30. **Pièges CONTEXTE MCP Blender (headless).** `bpy.ops.object.mode_set`,
    `bpy.ops.export_scene.fbx`, `bpy.ops.object.duplicate` lisent
    `context.active_object`/`selected_objects` **indisponibles** → wrapper
    `with bpy.context.temp_override(active_object=o, selected_objects=[...], ...)`.
    Dupliquer sans ops = **copie manuelle** (`o.copy()` + reparent vers les copies).
    `bpy.data.libraries.load(CHAR)` **impossible depuis le fichier courant** (si
    character.blend est ouvert) → partir d'un `bpy.ops.wm.read_homefile(use_empty=
    True)` (recréer World/Light/Camera). `bpy.context.screen` peut être `None` →
    itérer `window_manager.windows[].screen`.

**Piège import Unity** : `import_model_file` sur un chemin déjà existant crée un
doublon `Nom_1.fbx` au lieu d'écraser. Pour ré-exporter par-dessus : soit copier
le FBX sur le chemin `Assets/...` existant en filesystem puis `refresh_unity`
(garde le .meta/guid — préféré), soit supprimer l'ancien avant d'importer.
