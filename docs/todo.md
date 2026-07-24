# Todo — projet gmtk

## 🔧 Build / CI
- [ ] **Auto-build → itch.io** : pipeline butler (itch CLI). Build Unity → push channel itch. GitHub Action ou script local.
- [ ] **Deploy rapide Steam Deck** : build Linux/Proton → push auto vers le Deck (SSH/rsync ou Devkit). Boucle test 1-clic pour la partie console.

## 🛹 Grind system (skate-like)
- [ ] Edge detection : collecter les edges du level (mesh edges ou colliders dédiés).
- [ ] Check angle d'arrivée + alignement vélocité vs edge → snap si valide.
- [ ] État `Grinding` : contraindre le vélo sur la spline de l'edge, garder le momentum, sortie propre.
- [ ] FX + score pendant le grind.

## 🏛️ Zones content
- [ ] **Zoo** : animaux vivants (enclos, créatures animées, mouvement, vivant/bruyant).
- [ ] **Musée** : objets/expos statiques (salles, œuvres, socles, calme/contemplatif).
  - Diff : zoo = vivant/animé, musée = statique. Ambiance et gameplay différents par zone.

## 🎯 Quêtes & progression
- [ ] Système de quête de zone : "va à la zone X".
- [ ] Flèche de quête minimaliste (indicateur de direction écran/monde).
- [ ] Points de zone (zones = objectifs scorés).
- [ ] Score en **jauge** (barre qui se remplit).
- [ ] Compteur **popularité** qui décrémente (timer/pression sur le joueur).

## 🛣️ Outil routes
- [ ] Road tool : tracer routes par points (spline), générer le mesh le long, + variations (largeur, intersections, virages, types).
  - À voir : réutiliser l'existant (CityBuilder / assets routes dans `Assets/Models/City`) vs. spline+mesh procédural (SplineMesh, Unity Splines package, ou custom).

## 🧱 Level design
- [ ] Map LD **blocking** (blocs primitifs) pour le parcours.
- [ ] Mini-puzzles à base de blocs.
