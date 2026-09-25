#!/bin/bash
# Regenerates the managed collision cooker's goldens (lane p8a) from the NATIVE vphysics.so builds.
#
# For every job group <g> (shapes, brushes, clouds, multi, session):
#   <g>.in.gz        the cook jobs, in the oracle's line protocol (tools/p8a-oracle.c header)
#   <g>.sdk.txt.gz   SDK 2013 MP linux64 vphysics.so answers  (md5 95eb3dfb50e53c25d2c06047243faa03,
#                    BuildID 641e6bf36fc19e353a45d70857080b99749671f1)
#   <g>.tf2.txt.gz   TF2 linux64 vphysics.so answers          (md5 22f18bb2928ba302839a22fd19159654,
#                    BuildID ab4c2d0c4c56edf61af5a7501907d1b6aba51324)
# Answers keep one line per collide: "collide null", or "hex <VPHY blob>" for the first 150 collides
# of a group and "sha <SHA-256 of the blob> <length>" for the rest (the unit tier compares hashes;
# the full blobs feed the structural comparator's facts).
# SDK's answers use rsqrtss and so depend on the CPU; these were cut on an AMD Ryzen 9 9950X.
#
# usage: make-fixtures.sh <scratch-dir>
#   <scratch-dir> must hold the *.in job files named below, made by the generators in this
#   directory: run make-jobs.sh first (it holds the exact commands and seeds).
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
S=$1
SDK="$HOME/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer/bin/linux64"
TF2="$HOME/.steam/steam/steamapps/common/Team Fortress 2/bin/linux64"
test "$(md5sum < "$SDK/vphysics.so" | cut -d' ' -f1)" = 95eb3dfb50e53c25d2c06047243faa03
test "$(md5sum < "$TF2/vphysics.so" | cut -d' ' -f1)" = 22f18bb2928ba302839a22fd19159654
gcc -O1 -o "$S/p8a-oracle" "$HERE/tools/p8a-oracle.c" -ldl
for g in shapes brushes clouds multi session; do
  gzip -9n -c "$S/fx-$g.in" > "$HERE/$g.in.gz"
  for b in sdk tf2; do
    D=$SDK; [ $b = tf2 ] && D=$TF2
    LD_LIBRARY_PATH="$D" "$S/p8a-oracle" "$D/vphysics.so" < "$S/fx-$g.in" \
      | grep -E '^(hex |collide null)' | python3 "$HERE/tools/p8a-hashtail.py" 150 | gzip -9n > "$HERE/$g.$b.txt.gz"
  done
done
# Query goldens (brushes, clouds): SDK's volume/AABB/extent answers for each group's whole blobs,
# each line the oracle command followed by its answer. No traces (rays-per-blob 0), seed 7.
for g in brushes clouds; do
  gunzip -c "$HERE/$g.sdk.txt.gz" > "$S/qg-$g.answers"
  python3 "$HERE/tools/p8a-queries.py" "$S/qg-$g.answers" "$S/qg-$g.jobs" 0 7
  LD_LIBRARY_PATH="$SDK" "$S/p8a-oracle" "$SDK/vphysics.so" < "$S/qg-$g.jobs" | grep -E '^(q|e) ' > "$S/qg-$g.out"
  grep -E '^(Q|E) ' "$S/qg-$g.jobs" | paste -d' ' - "$S/qg-$g.out" | gzip -9n > "$HERE/$g.queries.sdk.txt.gz"
done
