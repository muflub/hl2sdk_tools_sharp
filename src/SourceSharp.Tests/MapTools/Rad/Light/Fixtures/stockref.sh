#!/bin/bash
# p4c: extra stock vrad references over the shared catalogue (and p4c's own fixtures).
# Stock vrad strips everything after the first dot of its argument, so each mode
# runs on a plain-named copy in its own directory:
# ref/p4c/b0/<n>.bsp + <n>.log stock vrad -threads 1 -verbose -bounce 0 (direct-only lightmaps)
# ref/p4c/both/<n>.bsp + <n>.log stock vrad -threads 1 -verbose -both (LDR + HDR worldlights)
# Input: catmaps/<n>.stockvis.bsp, else ref/p4c/in/<n>.bsp (+ <n>.rad if present).
# usage: stockref.sh [map names...] (default: every catalogue map)
set -u
W=${P4C_GAME_ROOT:?the toolgame tree holding tools/mapgame}
CAT=${CATMAPS_DIR:?the unified stock catalogue directory}
OUT=${P4C_STOCK_DIR:?the p4c reference output directory}
CAP=${RUN_CAPPED:?path to the capped-run wrapper}
TOOLS=${STOCK_WIN_TOOLS_BIN:?directory holding the stock win-x64 tools}
WINE="$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine"
export WINEPREFIX=$HOME/.local/share/sourcesharp-wineprefix WINEDEBUG=-all SteamAppUser=sourcesharp
G="Z:$W/tools/mapgame"
mkdir -p "$OUT/b0" "$OUT/both"
names=("$@")
if [ ${#names[@]} -eq 0 ]; then
  for f in "$CAT"/*.stockvis.bsp; do names+=("$(basename "$f" .stockvis.bsp)"); done
fi
for n in "${names[@]}"; do
  src="$CAT/$n.stockvis.bsp"; rad=""
  if [ ! -f "$src" ]; then src="$OUT/in/$n.bsp"; rad="$OUT/in/$n.rad"; fi
  for mode in b0 both; do
    d="$OUT/$mode"
    [ -s "$d/$n.log" ] && grep -q 'Ready to Finish' "$d/$n.log" && continue
    cp "$src" "$d/$n.bsp"
    [ -n "$rad" ] && [ -f "$rad" ] && cp "$rad" "$d/$n.rad"
    case $mode in b0) args=(-bounce 0);; both) args=(-both);; esac
    /usr/bin/time -v $CAP 3G "$WINE" "$TOOLS/vrad.exe" -threads 1 -verbose "${args[@]}" -game "$G" "Z:$d/$n.bsp" > "$d/$n.log" 2>&1 || echo "vrad FAIL $n $mode"
  done
  echo "done $n"
done
echo STOCKREF_DONE
