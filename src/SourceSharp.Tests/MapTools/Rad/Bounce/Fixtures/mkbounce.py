#!/usr/bin/env python3
"""p4d: record stock vrad's transfer and bounce lines for every map it bounced.

Inputs (stock x64 vrad.exe under wine, -threads 1 -verbose):
  CAT/<n>.vradv.log      the unified corpus (ref/catmaps-all)
  P4C/rad/<n>.log        p4c's texlight fixture (ref/p4c)

The reference prints the transfer line (always):
    transfers %d, max %d
and, under -verbose only, the bounce line:
    \tBounce #%i added RGB(%.0f, %.0f, %.0f)

A map stock did not bounce (no vis data) prints neither
and is left out.

usage: mkbounce.py <catmaps-all dir> <p4c ref dir> > stock-vrad-bounce.txt
"""
import glob
import os
import re
import sys

TRANSFERS = re.compile(r"^transfers (\d+), max (\d+)\s*$", re.M)
BOUNCE = re.compile(r"^\tBounce #(\d+) added RGB\((-?\d+), (-?\d+), (-?\d+)\)\s*$", re.M)


def record(name, path, out):
    text = open(path, "r", errors="replace").read()
    t = TRANSFERS.findall(text)
    b = BOUNCE.findall(text)
    if not t:
        if b:
            raise SystemExit(f"{path}: bounce lines without a transfers line")
        return
    if len(t) != 1:
        raise SystemExit(f"{path}: {len(t)} transfers lines")
    out.append(f"map {name}")
    out.append(f"transfers {t[0][0]} {t[0][1]}")
    for i, (n, r, g, bl) in enumerate(b, 1):
        if int(n) != i:
            raise SystemExit(f"{path}: bounce #{n} out of order")
        out.append(f"bounce {n} {r} {g} {bl}")
    out.append("")


def main():
    cat, p4c = sys.argv[1], sys.argv[2]
    out = [
        "# p4d stock bounce reference. Regenerate with Fixtures/mkbounce.py.",
        "# Stock x64 vrad.exe (Valve Software - vrad.exe SSE, Feb 17 2025) under wine,",
        "#   vrad.exe -threads 1 -verbose -game <tools/mapgame> <map>",
        "# on stock vbsp + vvis -threads 1 output (ref/catmaps-all; p4c_texlights from ref/p4c/rad).",
        "#",
        "# map <name>",
        "# transfers <total_transfer> <max_transfer>       vrad.cpp:1933",
        "# bounce <n> <r> <g> <b>                          vrad.cpp:1715, %.0f each",
        "",
    ]
    for log in sorted(glob.glob(os.path.join(cat, "*.vradv.log"))):
        record(os.path.basename(log)[: -len(".vradv.log")], log, out)
    record("p4c_texlights", os.path.join(p4c, "rad", "p4c_texlights.log"), out)
    sys.stdout.write("\n".join(out))


main()
