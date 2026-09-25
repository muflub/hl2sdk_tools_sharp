#!/bin/bash
# Build the qhull 2.6 C oracle driver.
# p8aq-ref -O2 -ffp-contract=off -fno-fast-math (the oracle; vertex-id hash)
# p8aq-ref-fast -O2 -ffast-math (information only)
# p8aq-ref-ptr oracle flags, ORIGINAL pointer hash in qh_gethash (for the pointer-hash study)
# p8aq-ref-bin oracle flags on csrc-binorder: the binary-order oracle (the reference build's grouping)
set -e
L=${P8AQ_SCRATCH:-$HOME/p8aq}
P=$L/qhullport
Q=$P/csrc
B=$P/build
mkdir -p "$B"
SRC="qhull.c geom.c geom2.c poly.c poly2.c merge.c global.c stat.c mem.c qset.c io.c user.c"
build() {
  local out=$1; shift
  local Q=${QSRC:-$P/csrc}
  local objs=()
  mkdir -p "$B/$out.o"
  for f in $SRC; do
    gcc -std=gnu89 -w "$@" -I"$Q" -c "$Q/$f" -o "$B/$out.o/${f%.c}.o"
    objs+=("$B/$out.o/${f%.c}.o")
  done
  gcc -std=gnu99 -Wall "$@" -I"$Q" -c "$P/p8aq-ref.c" -o "$B/$out.o/p8aq-ref.o"
  gcc "$@" -o "$B/$out" "${objs[@]}" "$B/$out.o/p8aq-ref.o" -lm
}
build p8aq-ref      -O2 -ffp-contract=off -fno-fast-math -DP8AQ_IDHASH
build p8aq-ref-fast -O2 -ffast-math -DP8AQ_IDHASH
build p8aq-ref-ptr  -O2 -ffp-contract=off -fno-fast-math
# the binary-order oracle: csrc-binorder (p8aq-patch-binorder.py) = vphysics.so's FP grouping
QSRC=$P/csrc-binorder build p8aq-ref-bin -O2 -ffp-contract=off -fno-fast-math -DP8AQ_IDHASH
ls -la "$B"/p8aq-ref*
