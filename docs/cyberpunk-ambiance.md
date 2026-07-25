# Pipeline visuelle — cyberpunk × cartoon imprimé

Source unique du look du jeu. Le cyberpunk et le cartoon ne sont **pas** deux pipelines
séparées à faire cohabiter : c'est un seul look, et les valeurs ci-dessous ne se règlent
pas indépendamment les unes des autres.

Scène de référence pour juger le rendu : **`Assets/Scenes/road.unity`**. Pas `GymRoom`
(géométrie placeholder, sol damier haute albédo qui délave), pas `City` (morte), pas
`CarTest` (bac à sable jour laissé volontairement sur `ToonPostProfile` + `SkyGradient`).

---

## Les trois principes

Tout le reste découle de là. Si un réglage futur contredit un de ces trois points, c'est
le réglage qui a tort.

1. **Le néon est la key light, pas la décoration.** `ToonLit` calcule ses bandes toon à
   partir des lumières additionnelles. Ce sont elles qui dessinent l'image ; le clair de
   lune ne fait qu'asseoir la silhouette générale. Baisser les néons pour « faire plus
   nuit » éteint le cel-shading.
2. **L'ombre est une couleur choisie, pas une absence de lumière.** `_ShadowTint` violet
   saturé. Un gris désaturé rend l'image boueuse dès que l'ambient remonte.
3. **La rim porte la lisibilité.** En nuit, les silhouettes ne se détachent pas par le
   contraste de valeur mais par un liseré coloré. C'est ce qui rend le jeu jouable à
   vitesse de ride. **Mais une rim ne s'allume que si la lumière est DERRIÈRE l'objet**
   (`saturate(-dot(L, V))`) — c'est ce que simule une rim light. Sans ce filtre, un
   grand plan horizontal vu en rasant a `rim ≈ 1` sur tout l'écran et vire au blanc.
4. **L'intensité d'une lampe règle sa PORTÉE, pas sa brillance.** La flaque néon est une
   bande de couleur bornée (`saturate(attenuation * intensité)`), pas une décroissance
   physique. Une lampe à 31 juste au-dessus d'une route la cramerait sinon.

**Ce que ce look n'est PAS** : ni un cel-shading de jour posé dans le noir (l'erreur
d'origine), ni un rendu PBR nocturne avec un filtre par-dessus.

---

## Appliquer le look — les menus

`Assets/Scripts/Gameplay/Editor/CyberpunkLookApplier.cs` exécute tout ce qui suit :

| Menu | Effet |
|---|---|
| `Tools/Ambiance/Nouvelle scene cyberpunk cartoon` | Crée une scène neuve déjà réglée (propose de sauver la scène courante avant) |
| `Tools/Ambiance/Appliquer le look cyberpunk cartoon` | Applique le look à la scène ouverte, sans rien détruire — le Global Volume et la directionnelle existants sont recyclés, pas dupliqués |
| `Tools/Ville/Semer des neons` | **Étape suivante obligatoire** — sans néons il ne reste que le clair de lune et la ville est illisible |

Les menus règlent : skybox, ambient, fog, clair de lune, Global Volume, et le
post-process + clear Skybox sur les caméras. Le log de la console récapitule ce qui a
été touché.

Volontairement hors de leur périmètre : les néons (ils ont besoin des routes et bâtiments
déjà posés), les matériaux (ils portent déjà `GMTK/ToonLit`), et `PC_Renderer` /
`PC_RPAsset` — l'outline et le grading HDR sont des réglages **projet**, pas scène.

Limite connue : `Ctrl+Z` ne restaure pas l'ancien ciel / ambient / fog. L'objet qui porte
les `RenderSettings` n'est pas accessible publiquement, donc ces trois-là échappent au
système d'undo. Le reste (lumière, volume, caméras) est bien annulable.

## Valeurs de référence

### Scène (`RenderSettings`)

| Réglage | Valeur | Pourquoi |
|---|---|---|
| Ambient mode | Trilight | Pas de lightmap, pas de probe, pas de GI temps réel dans ce projet : **l'ambient est 100 % de l'indirect** |
| sky / equator / ground | `(0.19,0.21,0.35)` / `(0.14,0.16,0.27)` / `(0.07,0.07,0.12)` | Volontairement modeste. Le gain de lisibilité doit venir du néon ; monter l'ambient délave sans rien résoudre (testé à `0.28/0.31/0.50` → laiteux) |
| Fog | ExponentialSquared, densité `0.0035`, couleur `(0.09,0.11,0.20)` | Pose la profondeur sans manger le décor à 60 m |
| Skybox | `Assets/Materials/SkyCyberpunk.mat` | + `DynamicGI.UpdateEnvironment()` après changement |
| Directionnelle | couleur `(0.74,0.80,1.00)`, intensité `1.6`, Soft, `Euler(38, 200, 0)` | Couleur peu saturée : un `(0.62,0.70,1.0)` perd 38 % d'énergie sur le rouge pour rien. Rotation alignée sur `_MoonDir` du skybox |

### Néons (`CityNeonSeeder.cs`, menus `Tools/Ville/…`)

| | intensité | portée | hauteur |
|---|---|---|---|
| Lampadaires de rue (tous les 16 m, côtés alternés) | **15** | **32** | 4.5 |
| Enseignes (1 bâtiment sur 5) | **11** | **24** | 3.5 |

**L'intensité règle la taille du cœur saturé de la flaque, pas sa brillance** (la
couverture vaut `saturate(distanceAttenuation * intensité)`). Testé à 31 : le cœur mange
toute la flaque → disque franc sans dégradé, et les flaques se recouvrent en un aplat
uniforme sur toute la chaussée. La brillance se règle avec `_NeonPoolStrength`.

`shadows = None` obligatoire : 112 lumières à ombres tuent le framerate.

### `Assets/Settings/CyberpunkPostProfile.asset`

| Override | Valeur | Pourquoi |
|---|---|---|
| Bloom | intensité `1.2`, seuil 0.85, scatter 0.75, tint violet | 1.6 délavait une fois les noirs relevés |
| ColorAdjustments | postExposure `+0.3`, contrast **`+10`**, saturation `+32`, colorFilter `(0.94,0.94,1)` | **Le contrast `+28` d'origine était la cause n°1 de l'écrasement** : il pivote au gris moyen, donc sur une image majoritairement sombre il repousse tout vers le bas. Le `+0.1` de postExposure (≈ +7 % linéaire) ne compensait rien |
| WhiteBalance | temperature `-11` | On garde le froid, sans la perte d'énergie du `-18` |
| Tonemapping | ACES | Testé contre Neutral : **indiscernable** sur cette scène (trop peu de valeurs > 1 hors néons). Ne pas y passer de temps |
| Vignette | `0.26` | `0.42` fermait trop |
| **LiftGammaGain** | lift `(1, 1, 1.03, 0.010)` | **L'ajout clé.** Relève les noirs sans toucher aux hautes lumières : c'est ce qui rend une nuit lisible sans la délaver. Attention au format : le **`.w` est l'offset additif réel** (`ColorUtils.ColorToLift`), `xyz` moins sa luma donne la teinte. Mettre la couleur voulue dans `xyz` et laisser `w` à 0 ne lève rien |
| ChromaticAberration | `0.5` | Assumé comme **défaut de repérage d'impression**, pas comme un artefact |
| FilmGrain | type 2, `0.35` | Le grain du papier |

### `Assets/Settings/PC_RPAsset.asset`

`m_ColorGradingMode: 1` (**HDR**). En LDR tout est clampé 0–1 avant le LUT : les néons
HDR ne peuvent pas dépasser, le bloom perd son intérêt et le toe ACES écrase les
midtones. `m_SupportsHDR` était déjà à 1 — le LDR était une incohérence, pas un choix.

### `Assets/Shaders/Toon/ToonLit.shader` — matériaux

| Propriété | Valeur | Pourquoi |
|---|---|---|
| `_ShadowTint` | `(0.20, 0.15, 0.36)` | Les façades ont un albédo clair (0.68–0.78). Un tint à 0.55 donne une ombre à 0.43, soit un gris moyen — pas une nuit. Remonté de `0.13` après suppression de la fausse rim, qui « éclairait » tout à tort |
| `_AmbientStrength` | `1.0` | Remplace un `* 0.35` codé en dur qui privait les surfaces toon des 2/3 de l'ambient reçu par une URP/Lit voisine — d'où l'écart de luminosité entre les deux shaders dans une même scène |
| `_RimNeonBoost` | `1.2` | Part de la rim teintée par les néons voisins |
| `_NeonPoolStrength` | `2.0` | Plafond de la flaque néon. Sans lui, `add.color` (qui contient déjà l'intensité 31) part à ~7 en linéaire sous un lampadaire |
| `_RampSteps` | `2` | Le registre imprimé veut peu de bandes |
| `_HalftoneScale` | `0.3` **mètre** | Taille de cellule **en unités monde**, pas en pixels. Une trame en espace écran reste collée à l'écran pendant que le décor défile dessous — ça lit comme des saletés sur l'objectif, pas comme une trame imprimée. Ancrée au monde (projection triplanaire sur l'axe dominant de la normale), elle voyage avec les surfaces et prend la perspective |
| `_Unlit` | `1` sur les enseignes | Aplat de couleur pur, non éclairé, en HDR (ex. `(2.4, 0.35, 2.2)`) pour franchir le seuil de bloom |

**Grands plans horizontaux (`City_Rue.mat` — ex-`City_Sol.mat`, `RoadMat`) : `_RimColor` = noir,
`_RimAmount` = 0.55.** Le noir éteint la rim de la lune (une route n'a pas de
silhouette, la rim n'y produit qu'une nappe blanche) pendant que le `_RimAmount` bas
laisse passer la rim **néon**, elle filtrée par le test « lumière derrière ». Ne pas
remettre `_RimColor` blanc sur ces matériaux.

**`RoadMat` est embarqué dans `road.unity`**, ce n'est pas un `.mat` sur disque — un
grep sur le guid du shader ne le trouve pas. Le régler via les `sharedMaterials` des
renderers sous `Roads`.

### Contour d'encre

Feature `ToonOutline` dans `Assets/Settings/PC_Renderer.asset` — **`m_Active: 1`**,
`injectionPoint: 550` (AfterRenderingPostProcessing), `requirements: 3` (depth+normals).

`Assets/Shaders/Toon/Materials/OutlineEdgeDetect.mat` :

| Propriété | Valeur | Pourquoi |
|---|---|---|
| `_OutlineColor` | `(0.05, 0.02, 0.11)` | Quasi-noir violacé. Une encre *claire* n'a de sens que si la scène est noire — une fois la scène rallumée, le noir franc redevient le bon choix |
| `_OutlineThickness` | `2.4` | Le registre imprimé veut un trait qui pèse |
| `_InkJitter` | `0.35` | Module l'épaisseur par un bruit écran → encre posée plutôt que trait vectoriel. `0` = comportement d'origine |
| `_DepthThreshold` | **`1.1`** | **Ne pas baisser.** Une surface vue en rasant (le sol) produit de grosses différences de profondeur entre pixels voisins sans arête réelle → bruit sur toute la chaussée |
| `_NormalThreshold` / `_NormalSensitivity` | `0.45` / `2.0` | Ce sont les normales qui doivent capter les arêtes utiles, pas la profondeur |

---

## Pièges

- **Play mode bloque la sauvegarde de scène** (`MarkSceneDirty` / `SaveOpenScenes` lèvent
  « cannot be used during play mode »). Sortir du play mode avant d'appliquer + sauver.
  Les `.mat` / `.asset` (`SaveAssets`) passent, eux.
- **Auto Refresh est désactivé** : un `.shader` ou `.cs` édité hors éditeur ne compile que
  quand la fenêtre Unity reprend le focus. Forcer avec
  `AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate)` puis vérifier
  `ShaderUtil.GetShaderMessages`. Un menu peut relancer l'ancien code sans le dire.
- **Volume overrides** : créer les composants par `CreateInstance<T>()` +
  `prof.components.Add` + `AssetDatabase.AddObjectToAsset` + `hideFlags =
  HideInHierarchy`, sinon ils sont effacés au domain reload.
- **`Shader.Find` juste après création** peut renvoyer `null` si l'import n'est pas fini.
- **Forward+ et `_ADDITIONAL_LIGHTS`** : le renderer est en Forward+
  (`m_RenderingMode: 2`), URP y éteint `_ADDITIONAL_LIGHTS` et passe par la boucle
  clusterisée. Garde correcte :
  `#if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP)`.
- **Ne jamais faire entrer une grandeur à décroissance continue dans un quantizer à
  bandes** : `ToonRamp(distanceAttenuation)` tombe dans la bande 0 et la lumière
  disparaît. Quantifier l'angle seul, l'atténuation multiplie la couleur.
- **`FindFirstObjectByType<Light>()` ne renvoie pas la directionnelle** — dans `road` il
  renvoie une néon. Filtrer sur `l.type == LightType.Directional`.
- **`City.unity` est sérialisée en binaire** : pas de diff lisible, toute modification
  passe obligatoirement par Unity MCP. (Scène morte par ailleurs.)
- **`road.unity` n'est pas commitée** (untracked) : elle n'apparaît pas dans les
  `git diff`, ce qui peut faire croire qu'une modification n'a pas été appliquée.
- Voir `docs/city-session-lessons.md` (section pièges `GMTK/ToonLit`) pour le détail des
  bugs shader et de leurs symptômes.

---

## Vérification visuelle

Vue jouable de référence dans `road`, près du `PlayerTruck` :

```
manage_camera screenshot view_position=[-70,4,-6] view_rotation=[4,70,0]
```

Second angle utile : `view_position=[-20,3.5,10] view_rotation=[2,200,0]`.

`view_target` attend un **nom de GameObject**, pas des coordonnées — utiliser
`view_rotation` pour viser librement.

Le critère qui compte n'est pas la carte postale mais la lisibilité **en mouvement** :
est-ce qu'on lit les rampes, les rails de grind et les zones de quête en roulant.

**Non vérifié à ce jour** : la lisibilité des `QuestZone` / `QuestZoneDropoff` (shader
`GMTK/NeonZone`) avec `Bloom.intensity` descendu à 1.2 — aucune instance de zone de quête
n'est présente dans `road.unity`, donc aucun render n'a pu le confirmer. Ce sont des
éléments de **gameplay** : leur lisibilité prime sur l'esthétique, à contrôler dès qu'une
zone est posée dans la scène.
