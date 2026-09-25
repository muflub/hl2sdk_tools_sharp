#!/bin/bash
# p4c: extra stock vrad references over the shared catalogue (and p4c's own fixtures).
# Stock vrad strips everything after the first dot of its argument, so each mode
# runs on a plain-named copy in its own directory:
#   ref/p4c/b0/<n>.bsp   + <n>.log   stock vrad -threads 1 -verbose -bounce 0  (direct-only lightmaps)
#   ref/p4c/both/<n>.bsp + <n>.log   stock vrad -threads 1 -verbose -both       (LDR + HDR worldlights)
# Input: catmaps/<n>.stockvis.bsp, else ref/p4c/in/<n>.bsp (+ <n>.rad if present).
# usage: p4c-stockref.sh [map names...]   (default: every catalogue map)
set -u
W=/home/lodle/git/source-sdk-2013/.claude/worktrees/agent-a3a01371543c92a1b
CAT=$HOME/.cache/maptools/ref/catmaps
OUT=$HOME/.cache/maptools/ref/p4c
CAP=$HOME/.cache/maptools/bin/run-capped
TOOLS=/home/lodle/sdk2013-win-tools/bin/x64
WINE="$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine"
export WINEPREFIX=$HOME/.local/share/source-sdk-wineprefix WINEDEBUG=-all SteamAppUser=sourcesharp
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
