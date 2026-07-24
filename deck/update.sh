#!/usr/bin/env bash
# Pulls the latest "deck-latest" Windows build from GitHub and unzips it into
# ~/Games/gmtk/. Repo is public, so no token needed.
# Called automatically before launch via Steam launch options:
#   bash ~/Games/gmtk/update.sh ; %command%
set -euo pipefail

DEST="$HOME/Games/gmtk"
URL="https://github.com/DoriannX/gmtk/releases/download/deck-latest/gmtk-windows.zip"
TMP="$(mktemp -d)"

echo "[gmtk] downloading latest build..."
if ! curl -fL --retry 3 -o "$TMP/gmtk.zip" "$URL"; then
  echo "[gmtk] download failed (offline?). Keeping current build." >&2
  rm -rf "$TMP"
  exit 0   # don't block launch; Steam still runs the old build after this
fi

echo "[gmtk] unpacking to $DEST"
mkdir -p "$DEST"
rm -rf "$DEST"/*          # wipe old files so renamed/removed assets don't linger
unzip -q -o "$TMP/gmtk.zip" -d "$DEST"
rm -rf "$TMP"
echo "[gmtk] up to date."
