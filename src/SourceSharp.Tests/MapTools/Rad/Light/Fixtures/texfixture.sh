#!/bin/bash
# p4c: build the texlight fixture with stock tools. Each stage runs on a plain-named
# copy in its own directory, because stock vvis/vrad strip everything after the first dot.
# ref/p4c/src/p4c_texlights.{vmf,rad,bsp,prt} vbsp -v
# ref/p4c/in/p4c_texlights.{bsp,prt,rad} + vvis -threads 1 (the managed gate's input)
# ref/p4c/rad/p4c_texlights.{bsp,rad,log} + vrad -threads 1 -verbose
set -u
W=${P4C_GAME_ROOT:?the toolgame tree holding tools/mapgame}
OUT=${P4C_STOCK_DIR:?the p4c reference output directory}
CAP=${RUN_CAPPED:?path to the capped-run wrapper}
TOOLS=${STOCK_WIN_TOOLS_BIN:?directory holding the stock win-x64 tools}
WINE="$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine"
export WINEPREFIX=$HOME/.local/share/sourcesharp-wineprefix WINEDEBUG=-all SteamAppUser=sourcesharp
G="Z:$W/tools/mapgame"
n=p4c_texlights
mkdir -p "$OUT/src" "$OUT/in" "$OUT/rad"
python3 "$(cd "$(dirname "$0")" && pwd)/make_texlight_fixture.py" "$OUT/src"
$CAP 3G "$WINE" "$TOOLS/vbsp.exe" -v -game "$G" "Z:$OUT/src/$n.vmf" > "$OUT/src/$n.vbspv.log" 2>&1 || { echo "vbsp FAIL"; exit 1; }
cp "$OUT/src/$n.bsp" "$OUT/src/$n.prt" "$OUT/src/$n.rad" "$OUT/in/"
$CAP 3G "$WINE" "$TOOLS/vvis.exe" -threads 1 -game "$G" "Z:$OUT/in/$n.bsp" > "$OUT/in/$n.vvis.log" 2>&1 || echo "vvis FAIL"
cp "$OUT/in/$n.bsp" "$OUT/in/$n.rad" "$OUT/rad/"
/usr/bin/time -v $CAP 3G "$WINE" "$TOOLS/vrad.exe" -threads 1 -verbose -game "$G" "Z:$OUT/rad/$n.bsp" > "$OUT/rad/$n.log" 2>&1 || echo "vrad FAIL"
grep -E 'texlights parsed|patches|direct lights|Maximum resident' "$OUT/rad/$n.log"
echo TEXFIXTURE_DONE
