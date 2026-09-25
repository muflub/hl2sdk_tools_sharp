#!/usr/bin/env bash
# Run stock vrad with -dumptrace and keep the trace.txt it writes into the mod's
# write path. $1 is a tag, the rest are extra vrad arguments.
set -euo pipefail
W=${P4B_GAME_ROOT:?the toolgame tree holding maps/ and game/mod_sharp/}
SC=${P4B_SCRATCH:?the scratch directory holding dm_lockdown_v10.bsp}
tag="$1"; shift
cp "$SC/dm_lockdown_v10.bsp" "$W/maps/p4b_lockdown.bsp"
rm -f "$W/game/mod_sharp/trace.txt"
WINEPREFIX="$HOME/.local/share/sourcesharp-wineprefix" WINEDEBUG=-all \
SteamAppUser=sourcesharp \
"$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine" \
"${P4B_VRAD_EXE:?path to stock vrad.exe}" \
-fast -dumptrace "$@" -game "Z:$W/tools/mapgame" "Z:$W/maps/p4b_lockdown.bsp" \
> "$SC/stock-$tag.log" 2>&1
echo "$tag rc=$?"
mv "$W/game/mod_sharp/trace.txt" "$SC/trace-$tag.txt"
grep -c 'Material not found' "$SC/stock-$tag.log" || true
grep -c 'Error loading studio model' "$SC/stock-$tag.log" || true
grep 'acceleration structure' "$SC/stock-$tag.log" | head -1
