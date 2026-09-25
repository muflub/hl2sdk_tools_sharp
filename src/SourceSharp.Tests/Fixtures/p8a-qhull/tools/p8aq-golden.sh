#!/bin/bash
# p8aq-golden.sh: pack the unit-tier goldens: corpus/*.pts and corpus/*.expected (the
# binary-order oracle's dumps) gzipped into golden/ (drop-in for SourceSharp.Tests/Fixtures/p8a-qhull/)
P=$HOME/.cache/maptools/lanes/p8a/qhullport
mkdir -p "$P/golden"
rm -f "$P"/golden/*.gz
cd "$P/corpus" || exit 2
for f in *.pts *.expected; do
  gzip -9 -n -c "$f" > "$P/golden/$f.gz"
done
du -ch *.pts *.expected | tail -1
du -ch "$P"/golden/*.gz | tail -1
ls "$P/golden"
