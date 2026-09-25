#!/bin/bash
# Regenerates the qhull port's goldens: p8aq-<group>.pts.gz (point sets) and
# p8aq-<group>.expected.gz (the C oracle's dump for each set, IVP retry sequence included).
#
# The oracle is Qhull 2.6 (1999/04/19) C, patched twice and nothing else:
# p8aq-patch-idhash.py qh_gethash hashes vertex ids, not pointers, under -DP8AQ_IDHASH
# (3-d results are identical either way; measured on all sets)
# p8aq-patch-binorder.py every floating-point expression IVP's options reach, regrouped as
# the reference collision build's -ffast-math codegen evaluates them
# built -O2 -ffp-contract=off -fno-fast-math (p8aq-build-ref.sh: binary build/p8aq-ref-bin).
# The Qhull 2.6 sources are not committed; unpack qhull-2.6 into $L/qhull26 first. The scripts
# work under L (qhullport/ beneath it); point P8AQ_SCRATCH at a scratch directory.
set -eu
T=$(cd "$(dirname "$0")" && pwd)
L=${P8AQ_SCRATCH:-$HOME/p8aq}
P=$L/qhullport
mkdir -p "$P/corpus"
cp "$T/p8aq-ref.c" "$P/"
python3 "$T/p8aq-patch-idhash.py"
python3 "$T/p8aq-patch-binorder.py"
bash "$T/p8aq-build-ref.sh"
python3 "$T/p8aq-gencorpus.py" "$P/corpus"
for f in "$P"/corpus/*.pts; do
  "$P/build/p8aq-ref-bin" "$f" > "${f%.pts}.expected"
done
bash "$T/p8aq-golden.sh"
cp "$P"/golden/*.gz "$T/.."
