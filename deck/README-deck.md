# Build auto → Steam Deck (Proton) + itch.io

Un build cloud (GitHub Actions), zéro build sur ta machine. Sur le Deck :
**1 clic en Gaming Mode = récupère le dernier build + joue**, sans desktop.

---

## 1. Secrets GitHub (une seule fois)

`Settings > Secrets and variables > Actions` sur le repo.

### Licence Unity Perso (obligatoire)

> ⚠️ L'ancien flux "site" (générer un `.alf` → uploader sur `license.unity3d.com`
> → télécharger le `.ulf`) est **mort** pour les licences Perso depuis ~2023.
> On obtient maintenant le `.ulf` par **activation locale via Unity Hub**. Le
> secret `UNITY_LICENSE` reste la bonne méthode (OK avec unity-builder@v4 + Unity 6).

1. Sur ta machine, Unity Hub → **Preferences > Licenses > Add** → *Get a free
   personal license*. (Ça active la licence contre les serveurs Unity et écrit le `.ulf`.)
2. Récupère le fichier `.ulf` :
   - Windows : `C:\ProgramData\Unity\Unity_lic.ulf`
   - Mac : `/Library/Application Support/Unity/Unity_lic.ulf`
   - Linux : `~/.local/share/unity3d/Unity/Unity_lic.ulf`
3. Ouvre-le, copie **tout le contenu XML** → secret **`UNITY_LICENSE`**.
4. Secrets **`UNITY_EMAIL`** / **`UNITY_PASSWORD`** = ton compte Unity (réactivation en CI).

> La licence Perso expire au bout d'un moment → il faut re-générer le `.ulf` et
> refaire l'étape 3. Si ça devient pénible, `game-ci/unity-license-activate`
> (auto-login Puppeteer + TOTP) régénère le `.ulf` tout seul dans le CI.

### itch.io (optionnel, sinon l'étape est sautée)

1. Clé API : https://itch.io/user/settings/api-keys → secret **`BUTLER_API_KEY`**.
2. Crée le projet sur itch.io (ex : `gmtk`).
3. Onglet **Variables** (pas Secrets) → variable **`ITCH_TARGET`** = `ton-user-itch/gmtk`.

Tant que `ITCH_TARGET` est vide, l'étape itch ne tourne pas — le reste marche.

---

## 2. Lancer un build

`Actions` (web ou app mobile GitHub) → workflow **Deck Build** → **Run workflow**.
Ça build en cloud et publie la Release `deck-latest` (+ itch si configuré).

---

## 3. Setup Steam Deck (une seule fois, Desktop Mode)

1. Copie `update.sh` dans `~/Games/gmtk/` sur le Deck (crée le dossier).
   ```bash
   mkdir -p ~/Games/gmtk
   cp update.sh ~/Games/gmtk/
   chmod +x ~/Games/gmtk/update.sh
   ```
2. Lance-le une fois pour tirer le premier build :
   ```bash
   ~/Games/gmtk/update.sh
   ```
3. Steam → **Add a Non-Steam Game** → **Browse** → `~/Games/gmtk/gmtk.exe`.
4. Clic droit sur le raccourci → **Properties** :
   - **Compatibility** → coche **Force the use of a specific Steam Play tool** → **Proton** (Experimental ou GE).
   - **Launch Options** → colle :
     ```
     bash ~/Games/gmtk/update.sh ; %command%
     ```
5. (option) Renomme le raccourci `gmtk`.

---

## 4. Quotidien

- **Builder** : bouton *Run workflow* sur GitHub (ou rien, si tu veux je mets l'auto sur push).
- **Jouer** : Gaming Mode → clic **gmtk**. `update.sh` tire le dernier build, puis Proton lance le jeu. Si offline, lance le build déjà présent.

Aucun ré-import, aucun download manuel. Le raccourci pointe toujours vers le même `.exe`.
