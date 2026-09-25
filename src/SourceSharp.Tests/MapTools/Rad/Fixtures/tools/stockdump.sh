#!/usr/bin/env bash
# Run stock vrad with -dumptrace and keep the trace.txt it writes into the mod's
# write path.  $1 is a tag, the rest are extra vrad arguments.
set -euo pipefail
W=/home/lodle/git/source-sdk-2013/.claude/worktrees/agent-acd662586dfe371ac
SC=/tmp/claude-1000/-home-lodle-git-source-sdk-2013/5d27cfb4-a4d7-46f6-bce5-e9eea81f8534/scratchpad/p4b
tag="$1"; shift
cp "$SC/dm_lockdown_v10.bsp" "$W/maps/p4b_lockdown.bsp"
rm -f "$W/game/mod_sharp/trace.txt"
WINEPREFIX="$HOME/.local/share/source-sdk-wineprefix" WINEDEBUG=-all \
SteamAppUser=sourcesharp \
"$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine" \
"/home/lodle/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer/bin/vrad.exe" \
-fast -dumptrace "$@" -game "Z:$W/tools/mapgame" "Z:$W/maps/p4b_lockdown.bsp" \
> "$SC/stock-$tag.log" 2>&1
echo "$tag rc=$?"
mv "$W/game/mod_sharp/trace.txt" "$SC/trace-$tag.txt"
grep -c 'Material not found' "$SC/stock-$tag.log" || true
grep -c 'Error loading studio model' "$SC/stock-$tag.log" || true
grep 'acceleration structure' "$SC/stock-$tag.log" | head -1
