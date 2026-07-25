#!/usr/bin/env bash
# Pulls the latest "deck-latest" Windows build from GitHub into ~/Games/gmtk/.
# Repo is public -> no token needed.
# Logs everything to ~/Games/gmtk/update.log so runs launched by Steam are traceable.
#
# Steam (non-Steam game) launch options, one click = update + play:
#   bash -c 'bash /home/deck/Games/gmtk/update.sh; exec "$@"' -- %command%

DEST="${HOME:-/home/deck}/Games/gmtk"
LOG="$DEST/update.log"
URL="https://github.com/DoriannX/gmtk/releases/download/deck-latest/gmtk-windows.zip"

mkdir -p "$DEST"

{
  echo "===== $(date) ====="
  echo "HOME=$HOME  USER=$(whoami)"

  TMP="$(mktemp -d)"
  echo "downloading $URL"
  if ! curl -fL --retry 3 -o "$TMP/gmtk.zip" "$URL"; then
    echo "download FAILED (offline?). Keeping current build."
    rm -rf "$TMP"
    echo "done (no change)."
    exit 0
  fi
  echo "downloaded $(stat -c%s "$TMP/gmtk.zip" 2>/dev/null) bytes"

  # Selective wipe: remove the previous BUILD only. Never touch update.sh or the log.
  rm -rf "$DEST"/gmtk.exe "$DEST"/*_Data "$DEST"/UnityPlayer.dll \
         "$DEST"/UnityCrashHandler64.exe "$DEST"/*.dll "$DEST"/MonoBleedingEdge 2>/dev/null

  unzip -q -o "$TMP/gmtk.zip" -d "$DEST" && echo "unzip OK"
  rm -rf "$TMP"

  echo "exe: $(ls -la "$DEST"/gmtk.exe 2>&1)"
  echo "done."
} >> "$LOG" 2>&1
