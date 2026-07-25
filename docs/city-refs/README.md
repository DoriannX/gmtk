# Galerie de référence — assets ville

Renders « hero » validés de chaque asset ville. **But : cohérence.**

## RÈGLE (avant de produire un nouvel asset)
1. **Regarder tous les PNG de ce dossier** pour caler le style commun : niveau de
   détail, silhouette, densité et rendu du végétal (facetté Shop_A), palette
   chaleureuse, éclairage de rendu, échelle relative entre assets.
2. Le nouvel asset doit **s'harmoniser** avec la galerie (mêmes matériaux
   `City_*`, même traitement du feuillage, mêmes proportions plausibles).
3. Une fois l'asset **validé par l'utilisateur**, rendre un hero 3/4
   (~1000×1000, éclairage calme du pipeline, fond neutre) et **l'ajouter ici**
   nommé `<NomAsset>.png`, puis référencer la ligne ci-dessous.

## Convention de render hero
- Caméra 3/4, bâtiment cadré plein, ~1000×1000.
- Éclairage pipeline (soleil warm ~2.8, world strength 0.30, view transform
  **Standard**, exposure -0.2) — voir mémoire `city-color-palette`.
- Fond neutre (world gris par défaut OK).

## Assets
| Asset | Fichier | Statut | Tris (base + veg) |
|-------|---------|--------|-------------------|
| Immeuble habitation R+5 | `Building_Apartment_A.png` | ✅ validé | ~12.4k + ~15.1k |
| Immeuble d'angle chanfreiné R+5 | `Building_Corner_A.png` | ✅ validé | ~2.9k + ~18.9k |
| Maison de ville étroite (toit pignon) | `Building_Townhouse_A.png` (+ `_variants.png`) | ✅ validé | ~1.8k + ~13.5k |
| Tour moderne verre/béton R+6 | `Building_Modern_A.png` (+ `_variants.png`) | ✅ validé | ~2.4k + ~9.8k |
| Commerce Shop_A (+A–E) | _à backfiller_ | ✅ validé | ~10k |
| Kit Rue/Sol — route droite | `Road_Straight_A.png` | ✅ validé | ~600 + ~480 |
| Kit Rue/Sol — bande enherbée | `Road_Verge_A.png` | ✅ validé | ~540 + ~7.2k (arbres) |
| Kit Rue/Sol — overview 6 tiles | `Street_Kit_overview.png` | ✅ validé | droite/virage/T/X/passage/verge |
| Prop — lampadaire (panier fleuri) | `Prop_Lamppost_A.png` | ✅ validé | ~216 + ~3k |
| Prop — banc | `Prop_Bench_A.png` | ✅ validé | ~102 |
| Prop — poubelle | `Prop_Bin_A.png` | ✅ validé | ~80 |
| Prop — feu tricolore | `Prop_TrafficLight_A.png` | ✅ validé | ~120 |
| Prop — abribus (toit végétal) | `Prop_Shelter_A.png` | ✅ validé | ~144 + ~3.9k |
| Props — mobilier (lineup) | `Props_Furniture.png` | ✅ validé | lampadaire/banc/poubelle/feu/abribus |
| Props — petits (lineup) | `Props_Small.png` | ✅ validé | hydrant/borne/panneau/déchets/jardinière |
| Props — panneau STOP (texte mesh) | `Prop_Sign_A.png` | ✅ validé | ~890 |
| Props — 4 gammes couleur fonte | `Props_Colorways.png` | ✅ validé | Teal/Bordeaux/Navy/Anthracite (swap mat) |
| Véhicule — voiture citadine | `Vehicle_Car_A.png` | ✅ validé | ~670 |
| Véhicule — berline | `Vehicle_Sedan_A.png` | ✅ validé | |
| Véhicule — camionnette | `Vehicle_Van_A.png` | ✅ validé | |
| Véhicule — bus | `Vehicle_Bus_A.png` | ✅ validé | |
| Véhicule — vélo | `Vehicle_Bike_A.png` | ✅ validé | ~460 |
| Véhicule — moto | `Vehicle_Moto_A.png` | ✅ validé | |
| Végétation — arbres ×5 | `Veg_Trees.png` | ✅ validé | rond/grand/petit/fleuri/conifère |
| Végétation — arbre rond (hero) | `Veg_Tree_A.png` | ✅ validé | ~52 + ~2.4k |
| Végétation — arbustes ×6 | `Veg_Shrubs.png` | ✅ validé | buisson/haie/pot/topiaire/herbe |
| Piétons — cast ×8 (riggé) | `Pedestrians.png` | ✅ validé | anatomie base sculptée réutilisée + features modelées + rig d'os léger |
| Oiseau — pigeon (drop-in Pigeon.cs) | `Pigeon.png` | ✅ validé | corps plombé, aile repliée, Head/WingL/WingR |
| Oiseau — vol lointain (silhouette ciel) | `SkyBird.png` | ✅ validé | ailes déployées balayées, queue fourchue, low-poly |

> Shop_A est validé mais son hero n'a pas encore été re-rendu ici — à ajouter au
> prochain passage Blender (ouvrir/rebuild puis render hero).
