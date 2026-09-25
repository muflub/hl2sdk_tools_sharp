#!/bin/bash
# Regenerates the cook-job inputs (fx-<group>.in) for make-fixtures.sh, and checks them against the
# committed <group>.in.gz. Deterministic: seeded generators plus three real-map brush sources.
#
# usage: make-jobs.sh <scratch-dir> <catmaps-vmf-dir> <ss_sandbox.vmf> <dm_lockdown.bsp>
#   catmaps-vmf-dir: the integrator's catalogue VMFs (~/.cache/maptools/ref/catmaps at cut time)
set -eu
G=$(cd "$(dirname "$0")" && pwd)
T=$(cd "$G/.." && pwd)
O=$1
CATMAPS=$2
SANDBOX=$3
LOCKDOWN=$4
mkdir -p "$O"
cd "$O"
printf 'P 6 0\n1 0 0 16\n-1 0 0 16\n0 1 0 16\n0 -1 0 16\n0 0 1 16\n0 0 -1 16\nC 1 0 0 0\n' > p8a-cube.in
python3 "$G/p8a-gen.py" box 400 11 > p8a-box.in
python3 "$G/p8a-gen.py" wedge 400 12 > p8a-wedge.in
python3 "$G/p8a-gen.py" prism 400 13 > p8a-prism.in
python3 "$G/p8a-gen.py" brush 1000 14 > p8a-brush.in
python3 "$G/p8a-gen.py" boxv 300 15 > p8a-boxv.in
python3 "$G/p8a-gen.py" tri 500 1 > p8a-tri500.in
python3 "$G/p8a-gen.py" cloud 500 21 > p8a-cloud.in
python3 "$G/p8a-gen.py" cyl 300 22 > p8a-cyl.in
python3 "$G/p8a-gen.py" sliver 300 23 > p8a-sliver.in
python3 "$G/p8a-gen.py" merge 300 24 > p8a-merge.in
python3 "$G/p8a-gen.py" multi 200 25 > p8a-multi.in
python3 "$G/p8a-vmf2jobs.py" p8a-catmaps.in "$CATMAPS"/*.vmf
python3 "$G/p8a-vmf2jobs.py" p8a-sandbox.in "$SANDBOX"
python3 "$G/p8a-bsp2jobs.py" p8a-lockdown.in "$LOCKDOWN"
python3 "$G/p8a-concat.py" fx-shapes.in p8a-cube.in p8a-box.in p8a-wedge.in p8a-prism.in p8a-sliver.in p8a-tri500.in p8a-boxv.in
python3 "$G/p8a-concat.py" fx-brushes.in p8a-catmaps.in p8a-sandbox.in p8a-lockdown.in p8a-brush.in p8a-merge.in
python3 "$G/p8a-concat.py" fx-clouds.in p8a-cloud.in p8a-cyl.in
cp p8a-multi.in fx-multi.in
# the session group: brush models with drag areas, displacement soups, virtual meshes
python3 "$G/p8a-gen.py" bmodel 120 51 > fx-b.in
python3 "$G/p8a-gen.py" soup 120 52 > fx-s.in
python3 "$G/p8a-gen.py" mesh 40 53 > fx-m.in
python3 "$G/p8a-concat.py" fx-session.in fx-b.in fx-s.in fx-m.in
for g in shapes brushes clouds multi session; do
  a=$(md5sum < "fx-$g.in" | cut -d' ' -f1)
  b=$(gunzip -c "$T/$g.in.gz" | md5sum | cut -d' ' -f1)
  echo "$g regenerated=$a committed=$b $([ "$a" = "$b" ] && echo SAME || echo DIFFERENT)"
done
