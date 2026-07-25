# Pipeline assets ville — méthode de référence

Comment on a produit Building_Shop_A + ses 5 variantes. **Appliquer la même
méthode à tous les assets restants** (bâtiments, rue/sol, props, véhicules, végétal).

Style cible : **low-poly stylisé détaillé**, **ville végétalisée**, palette
**chaleureuse/joyeuse** (voir mémoire `city-color-palette` + `docs/city-assets.md`).

---

## 0. Outils
- Modeling : **Blender via MCP** (`mcp__Blender__execute_blender_code` = code `bpy`).
- Validation : `render_viewport_to_path` → lire le PNG (le fichier sort dans le dossier
  temp Blender `.../blender_mcp/…`, pas forcément le chemin demandé — lire ce chemin-là).
- Import moteur : **UnityMCP** `import_model_file` (copie le FBX sous `Assets/`).

## 0.5 AVANT de commencer un nouvel asset (cohérence)
- Lire **`docs/city-session-lessons.md`** (erreurs déjà commises + checklist).
- Regarder **tous les renders de `docs/city-refs/`** pour caler le style commun
  (détail, silhouette, végétal facetté, palette, échelle relative). Le nouvel
  asset doit s'harmoniser avec la galerie.
- Une fois l'asset validé : rendre un hero 3/4 et l'**ajouter à `docs/city-refs/`**
  (voir `docs/city-refs/README.md`).

## 0.6 APPRENTISSAGE PERPÉTUEL (étape obligatoire, automatique)
À chaque asset (et dès qu'un bug/retour survient), capitaliser SANS qu'on le
redemande : consigner erreurs+causes+fixes+retours dans `docs/city-session-lessons.md`,
ajouter le hero à `docs/city-refs/`, MAJ `docs/city-assets.md` + `city_builder.py`
si méthode réutilisable. But : plus on fait d'assets, plus c'est rapide et sans
re-buter sur les mêmes pièges.

## 1. Boucle de travail (IMPÉRATIVE : un asset à la fois)
1. Modéliser **une** base simple, rendre, faire valider par l'utilisateur.
2. Ajouter **un** incrément à la fois (détails, puis végétal, puis couleurs…),
   rendre et valider à CHAQUE étape. Ne pas empiler 5 changements d'un coup.
3. Quand l'asset est validé → décliner en variantes → asset suivant.
4. Noter l'avancement dans `docs/city-assets.md` (statut ⬜🔨🎨✅ + journal).

## 2. Technique de modeling (code bpy)
- **Composition d'objets primitifs** posés (helpers `box()`, `cyl()`), PUIS `join()`
  en un seul mesh. Pas de booléens pour les fenêtres (cf. pièges) — le verre est une
  boîte posée en léger relief, pas un trou.
- Convention repère : **face avant = -Y**. « Sortir » de la façade = Y plus négatif,
  « rentrer » = Y plus positif.
- Détails signature obligatoires (jamais une boîte nue) : cadres+meneaux+appuis de
  fenêtres, corniches inter-étages, plinthe, perron (marches), clim latérale (relief +
  louvres + tuyau), gouttière, enseigne suspendue (bras en Y), frise haute + horloge,
  cheminée, aération, antenne.
- **Végétal organique** : fonction `organic()` = icosphère **subdiv 2** dont chaque
  vertex est déplacé par **bruit cohérent 2 octaves** (`mathutils.noise`) le long de la
  normale + jitter, puis `bevel`, **flat shading**, scale/rotation aléatoires. C'est ce
  qui casse l'aspect « icosphère lisse ». Placer : jardinières sous fenêtres (sur les
  corniches), lierre grimpant sur un angle, toit végétalisé, pots à l'entrée.
- Flat shading partout : `poly.use_smooth=False`.

## 3. Paramétrer pour les variantes
Encapsuler le build dans **une fonction** prenant : offset X, couleurs (mur, auvent,
frise, porte, enseigne), nb d'étages, densité végétale, présence auvent… puis boucler
sur une liste de specs. Ex : `build_shop(tag,ox,wall,awn,fri,door,sign,n_rows,dens,has_awn)`.
4-5 variantes qui changent hauteur + couleurs + densité verdure suffisent à peupler.

## 4. Couleurs
Matériaux Blender `City_*` (Principled BSDF, Base Color, roughness ~0.7), partagés
entre assets pour la cohérence ; créer des matériaux dédiés par variante seulement pour
ce qui change (mur/auvent/frise/porte/enseigne). Palette exacte : mémoire
`city-color-palette`. Éclairage de rendu calme (soleil ~2.8, world strength 0.30, view
transform **Standard**) sinon les teintes délavent vers le blanc.

## 5. Export → Unity
- Par asset : `origin_set` (géométrie) → `location=(0,0,0)` → `export_scene.fbx`
  avec `use_selection=True` (un objet à la fois, sinon ils se mélangent).
- `import_model_file(source_path=<fbx>, output_folder="Models/City", name=...)`.
- Dossier Unity : `Assets/Models/City/`.

## PIÈGES rencontrés (à éviter absolument)
1. **Objets orphelins** : si un script `bpy` crashe en plein milieu, les objets déjà
   créés RESTENT dans la scène et polluent tous les renders suivants (géométrie
   fantôme). → Toujours nettoyer au début : supprimer tout objet hors
   `{asset courant, Camera, CitySun, Light}` + purger les meshes orphelins.
2. **Booléens séquentiels EXACT** ratent des découpes → fenêtres « avalées » par le
   mur. Si un jour on carve vraiment : fusionner tous les cutters en UN objet puis UN
   seul boolean. Mais par défaut : pas de boolean, verre en relief.
3. **Signe de profondeur inversé** : une boîte « en retrait » posée dans un mur plein
   est simplement cachée (pas de trou). Le verre doit affleurer/dépasser, pas rentrer.
4. **`join()` sans deselect** fusionne aussi les objets déjà sélectionnés (ex : les
   bâtiments précédents). → `select_all(DESELECT)` avant de sélectionner les parts à
   joindre, et après.
5. `execute_blender_code` exige `result = {...}` (dict) en fin de script.
6. Poids feuillage : subdiv2 → ~10k tris/bâtiment. OK en « hero » ; pour instancier la
   ville, prévoir un LOD/decimate du végétal.

## Historique (Shop_A)
v1 base 72 tris → +détails archi → réalign corniches → fix bras enseigne → perron →
végétal → clim refaite + pots repositionnés → frise+horloge haut de façade →
feuillage organique (noise) → subdiv2 → palette chaude → 5 variantes paramétrées.
