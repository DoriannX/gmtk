# Ambiance cyberpunk / nuit étoilée — comment l'appliquer à une scène

Look nuit clair de lune + néons, indépendant du pipeline toon. Testé dans `GymRoom.unity`.

## Assets existants (déjà en projet, réutilisables tels quels)

- **Skybox shader** : `Assets/Shaders/Sky/StarNightSky.shader` (`GMTK/StarNightSky`)
  - champ d'étoiles 2 couches + scintillement, bande milky-way (`fbm`), dégradé vertical,
    glow cyberpunk cyan↔magenta sur l'horizon, **lune réaliste** (disque phasé + maria + limbe doux, PAS un soleil violet).
  - lune : `_MoonDir` (position), `_MoonPhaseDir` (lumière de phase → croissant/gibbeuse),
    `_MoonSize` ~0.030, `_MoonColor` ~1.2 (pas HDR à 3), `_MoonHalo` (tightness, exp), `_MoonHaloInt` ~0.5.
- **Skybox material** : `Assets/Materials/SkyCyberpunk.mat` (déjà réglé night). L'assigner à `RenderSettings.skybox`.
- **Post profile** : `Assets/Settings/CyberpunkPostProfile.asset` — Bloom(1.6, tint violet) + ColorAdjustments + WhiteBalance froid/magenta + Tonemapping ACES + Vignette + ChromaticAberration + FilmGrain. **Séparé de `ToonPostProfile.asset`** (celui-ci reste le look toon).

## Étapes pour une nouvelle scène

1. **Skybox** : `RenderSettings.skybox = SkyCyberpunk.mat` ; `DynamicGI.UpdateEnvironment()`.
2. **Global Volume** : créer un GameObject, `Volume` (`isGlobal=true`, `priority=1`, `sharedProfile = CyberpunkPostProfile.asset`).
3. **Caméra** : sur la `UniversalAdditionalCameraData` de la caméra → `renderPostProcessing = true`.
4. **Directional Light = clair de lune** : couleur bleu froid `(0.62,0.70,1.0)`, `intensity ~1.15`, ombres Soft,
   rotation alignée au côté de la lune (`Euler(38,200,0)` dans GymRoom).
5. **Ambient** (Trilight) : sky `(0.16,0.19,0.32)`, equator `(0.12,0.14,0.24)`, ground `(0.04,0.04,0.07)`.
   Fog exp, couleur `(0.05,0.06,0.14)`, density ~0.006.
6. **Néons** : semer des `Light` Point colorées (cyan/magenta/orange/vert/violet/rose-rouge) proches du sol
   (~2.5 au-dessus) pour des flaques lumineuses nettes qui bloom. GymRoom : 12 lights sous `RoomLights`,
   `intensity ~20`, `range ~20`, shadows None. Régler le nombre/intensité selon la taille de la zone jouable.

## Pièges

- **Play mode bloque la sauvegarde de scène** (`MarkSceneDirty`/`SaveOpenScenes` throw "cannot be used during play mode").
  Sortir du play mode avant d'appliquer les réglages scène + sauver. Les `.mat`/`.asset` (SaveAssets) passent, eux.
- **Volume overrides** : créer les composants via `CreateInstance<T>()` + `prof.components.Add` + `AssetDatabase.AddObjectToAsset` + `hideFlags=HideInHierarchy`, sinon effacés au domain reload (cf [[toon-postprocess-pipeline]]).
- **Shader.Find juste après création** : peut renvoyer null si l'import n'est pas fini → `manage_asset import` sur le .shader puis retry.
- **Outline** : le FullScreenPass `ToonOutline` (dans `PC_Renderer.asset`) est **désactivé** (`isActive=false`) pour ce test, pas supprimé. Le réactiver = repasser `isActive=true`.

## Vérif visuelle

La FollowCamera peut être mal orientée (voiture en trick). Pour juger le ciel/la lune : capture positionnée
`manage_camera screenshot view_position=[0,4,-10] view_target=[28,42,68]` (vise `_MoonDir`).
