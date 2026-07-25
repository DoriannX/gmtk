# Liste UI à produire — brief artistes

Contexte : jeu de vélo arcade en ville. Le joueur enchaîne figures (spins/flips),
grinds sur rails, et livraisons. Une jauge de points **fond en continu** : il faut scorer pour
survivre.

## Direction artistique : cyberpunk cartoon néon

Ville de nuit, ciel étoilé + lune, flaques de lumière néon au sol, bloom violet fort,
grain + aberration chromatique. Le tout reste **cartoon** : formes lisibles, contours épais,
anims qui bouncent (squash/stretch, overshoot), pas de réalisme sale.

Conséquences directes pour l'UI :
- **Le fond est sombre et déjà très saturé.** L'UI doit passer par-dessus des néons cyan/magenta.
  Donc : formes pleines et opaques plutôt que translucide, et un contour sombre presque noir
  (`0.13, 0.10, 0.16`, celui déjà utilisé par les bulles de dialogue) pour détacher du fond.
- **Le bloom mange les petits éléments lumineux.** Éviter les traits fins clairs sur fond sombre,
  ils vont baver. Un texte clair doit être posé sur une plaque sombre, pas directement sur la ville.
- Palette néon de référence (cf `docs/cyberpunk-ambiance.md`) : cyan, magenta, violet, orange,
  vert acide, rose-rouge. La palette chaude d'origine survit uniquement sur les **assets de ville**
  (façades, props) — l'UI, elle, part sur les néons.
- Le glow doit être **dessiné dans l'asset** (halo peint autour de la forme), pas attendu du bloom :
  le post-process varie selon les zones et on ne peut pas compter dessus pour la lisibilité.
- Prévoir chaque élément lisible **posé sur un fond noir ET sur un fond néon saturé**.

État actuel : **tout est placeholder généré en code** (OnGUI / textures runtime / UI Toolkit nu).
Aucun asset UI n'existe. Tout ce qui suit est donc à créer de zéro.

Formats attendus (sauf indication contraire) : PNG 32 bits alpha prémultipliée non, sRGB,
puissance de 2 quand possible, sprites destinés au 1920×1080 de référence (prévoir ×2 pour la 4K).
Les panneaux/barres qui s'étirent doivent être **9-slice** (indiquer les marges dans le nom ou un
fichier joint). Les animations : soit spritesheet horizontale (indiquer nb frames + fps), soit
éléments séparés qu'on anime en DOTween côté code (préféré).

---

## P0 — HUD de jeu (le plus visible, en jeu 100 % du temps)

### 1. Jauge de score / popularité (`ScoreGauge.cs`)
Barre horizontale qui se vide en continu et remonte par à-coups. Coin haut-gauche.
- `gauge_frame` — cadre/coque de la barre, 9-slice, ~420×38 px de design
- `gauge_fill` — remplissage (tileable horizontalement), 3 états couleur : normal / haut / **critique** (< 25 %, doit alarmer)
- `gauge_cap` — embout/tête de la barre (petit pic lumineux au bord du fill)
- `gauge_icon` — icône de la ressource (cœur ? étoile ? "hype" ?) posée à gauche du cadre — **à trancher : que représente la jauge en fiction ?**
- `gauge_lowpulse` — halo/glow overlay pour l'état critique (clignote)
- Chiffres de score : voir police (§7)

### 2. Slaps de figures (`TrickHud.cs`)
Textes "sticker" qui claquent à l'écran pendant un saut (SPIN / FLIP / DOUBLE FLIP / … / MONSTER FLIP),
puis le score du saut, le drift, la banque de combo, ou "RATÉ !".
- `slap_bg` — fond sticker derrière le texte, 9-slice, style autocollant BD (contour épais + ombre portée dure)
- 3 variantes de couleur/forme : **bien** (jaune/orange), **gros palier** (rose/cyan), **raté** (rouge cassé, contour ébréché)
- `combo_badge` — pastille de multiplicateur ×2 … ×10 (une forme, chiffre en texte)
- `slap_burst` — éclat/rayons derrière le sticker au moment du palier (une seule image, on la fait tourner/scaler)
- Optionnel mais fort : lettrages dessinés à la main pour les paliers clés (`MONSTER FLIP`, `RATÉ !`)

### 3. Cadran d'équilibre de grind (`GrindBalanceHud.cs`)
Cadran rond cartoon avec aiguille, affiché à côté du vélo pendant un grind uniquement.
- `dial_base` — fond du cadran (rond, ~184 px)
- `dial_track` — arc gradué vert (centre) → rouge (bords)
- `dial_needle` — aiguille (pivot en bas de l'image, à préciser)
- `dial_danger` — overlay rouge/glow quand on approche de la chute
- `dial_glass` — reflet/verre par-dessus (optionnel)

### 4. Flèche / boussole de quête (`QuestArrow.cs`)
Actuellement une flèche 3D générée en mesh, flottant au-dessus du joueur.
- Soit une **texture/matériau cartoon** pour cette flèche 3D (contour, dégradé, motif animé)
- Soit un **indicateur écran** en plus : `offscreen_arrow` + `waypoint_pin` avec distance (à trancher)

### 5. Panneau de zone de quête (`QuestZone.cs`)
Panneau flottant au-dessus de la zone, avec label ("RESTE DANS LA ZONE") + jauge de maintien.
- `zone_panel` — panneau 9-slice type pancarte/banderole
- `zone_hold_ring` ou `zone_hold_bar` — jauge de remplissage du temps de maintien
- `zone_locked` — cadenas + affichage "score requis" pour les zones verrouillées
- `zone_done` — tampon/coche de validation ("OK !")
- Icônes de quête : `icon_pickup` (colis à récupérer) et `icon_dropoff` (destination)

### 6. Bulles de dialogue NPC (`SpeechBubble.cs`, `PedestrianChatter`, `CarChatter`)
Aujourd'hui dessinées en code (coins ronds, contour épais, queue).
- `bubble_speech` — 9-slice + queue séparée (`bubble_tail`) pour pouvoir la placer
- `bubble_thought` — variante pensée + 2-3 petites bulles
- Variantes d'humeur utiles : neutre / colère (contour dentelé) / surprise
- Petites icônes d'émotion (juron BD, points d'exclamation, étoiles)

---

## P1 — Écrans (existent en UI Toolkit nu, à habiller)

Fichiers : `Assets/UI/UXML/MainMenu.uxml`, `PauseMenu.uxml`, `GameOver.uxml` + `Assets/UI/USS/Common.uss`.
Contenu actuel : un titre + 2 boutons chacun, zéro style.

### 7. Kit commun
- **Police(s)** : 1 police titre très typée (cartoon, lourde) + 1 police lisible pour les chiffres/scores.
  Fournir en OTF/TTF **avec la licence**. Prévoir accents FR (é è à ç).
- `button_normal` / `button_hover` / `button_pressed` / `button_disabled` — 9-slice
- `button_focus` — état **sélectionné à la manette** (obligatoire : le jeu se joue au pad / Steam Deck)
- `panel_bg` — fond de panneau 9-slice
- `overlay_vignette` / `overlay_dim` — assombrissement derrière les menus
- Curseur (si on garde la souris au menu)

### 8. Menu principal
- Logo / titre du jeu (le placeholder dit littéralement "GAME TITLE" — **il faut un nom**)
- Fond de menu (illustration ou scène 3D floutée + éléments UI par-dessus)
- Boutons : Jouer, Options, Quitter

### 9. Pause
- Bandeau "PAUSE"
- Boutons : Reprendre, Options, Retour menu

### 10. Game Over / fin de run
- Bandeau "GAME OVER" (le trigger : jauge vide)
- Écran de résultats : score final, nb livraisons, meilleur combo, meilleur grind — prévoir un **cartouche de stats** (fond + lignes) et un tampon de rang (S/A/B/C) si on va jusque-là
- Boutons : Rejouer, Menu

---

## P2 — Utile mais pas bloquant

### 11. Prompts d'input (manette + clavier)
Mapping déjà en place (`InputActions.cs`) : A saut, LB drift/figures, B/RB boost, Y phares, Start pause,
sticks direction/caméra. Besoin de la **planche de boutons** :
- Xbox (A/B/X/Y, LB/RB, LT/RT, sticks, dpad, Start/Select)
- Steam Deck si on veut être propre (mêmes formes, labels Deck)
- Touches clavier (WASD, flèches, Espace, F…) — style capuchon de touche
- Format : sprites carrés uniformes (64×64), même cadrage, pour être posés inline dans du texte

### 12. Tutoriel / onboarding
- Encarts d'aide contextuels ("Maintiens A pour un super saut") : `hint_panel` 9-slice + zone icône

### 13. Options
- Sliders (volume musique / SFX — `AudioManager` existe), toggles, flèches de sélection
- Sprites : `slider_track`, `slider_fill`, `slider_knob`, `toggle_on/off`, `arrow_left/right`

### 14. Divers
- Écran de chargement (`SceneLoader.cs`) : fond + indicateur d'attente animé (spritesheet ou éléments à faire tourner)
- Mini-carte / plan de ville (si on en veut une : cadre + icônes zones + point joueur)
- Compteur de vitesse cartoon (optionnel, `ArcadeCarController.Speed` dispo)
- Icônes de zones thématiques : Zoo, Musée (cf. `docs/todo.md`)

---

## Hors périmètre artistes

`DebugOverlay.cs` = overlay de debug build, volontairement moche, ne pas habiller.

## Questions à trancher avec l'équipe

1. **Nom du jeu** (bloque le logo).
2. Que représente la jauge qui fond en fiction ? (popularité / hype / endurance) → détermine l'icône et la couleur.
3. Diégétique ou pas : le HUD est-il "collé à l'écran" (stickers BD, assumé) ou intégré au monde ? Aujourd'hui c'est mi-figue mi-raisin (cadran de grind flottant près du vélo, jauge en coin d'écran).
   Le cyberpunk ouvre une 3e voie : HUD "écran de casque / interface embarquée", scanlines et glitch,
   ce qui justifie tout d'un coup. À trancher, ça change tout le kit.
