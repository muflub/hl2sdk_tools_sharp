#!/bin/bash
# Regenerates the qhull port's goldens (lane p8a): p8aq-<group>.pts.gz (point sets) and
# p8aq-<group>.expected.gz (the C oracle's dump for each set, IVP retry sequence included).
#
# The oracle is Qhull 2.6 (1999/04/19) C, patched twice and nothing else:
#   p8aq-patch-idhash.py    qh_gethash hashes vertex ids, not pointers, under -DP8AQ_IDHASH
#                           (3-d results are identical either way; measured on all sets)
#   p8aq-patch-binorder.py  every floating-point expression IVP's options reach, regrouped as
#                           GCC 10.3 -ffast-math emitted it in SDK 2013 vphysics.so (objdump
#                           addresses cited per rewrite)
# built -O2 -ffp-contract=off -fno-fast-math (p8aq-build-ref.sh: binary build/p8aq-ref-bin).
# The Qhull 2.6 sources are not committed; unpack qhull-2.6 into $L/qhull26 first. The scripts
# work under L=~/.cache/maptools/lanes/p8a (qhullport/ beneath it), as when the goldens were cut.
set -eu
T=$(cd "$(dirname "$0")" && pwd)
L=$HOME/.cache/maptools/lanes/p8a
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
