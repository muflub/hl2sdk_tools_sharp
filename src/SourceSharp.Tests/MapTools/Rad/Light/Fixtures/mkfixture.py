#!/usr/bin/env python3
"""p4c: record what stock vrad produced for each catalogue map and for the
p4c texlight fixture, as the flat text the managed gate compares against.

Inputs (all stock x64 tools under wine, -threads 1):
  CAT/<n>.vradv.log, CAT/<n>.stockrad.bsp   vrad -verbose        (the catalogue)
  P4C/rad/<n>.log,   P4C/rad/<n>.bsp        vrad -verbose        (p4c's own fixtures)
  P4C/both/<n>.bsp                          vrad -verbose -both  (HDR worldlights)

usage: mkfixture.py <catmaps dir> <p4c ref dir> > stock-vrad-catalogue.txt
"""
import os
import re
import struct
import sys

LUMP_WORLDLIGHTS = 15
LUMP_WORLDLIGHTS_HDR = 54


def lump(path, index):
    with open(path, "rb") as f:
        data = f.read()
    ident, version = struct.unpack_from("<4si", data, 0)
    assert ident == b"VBSP", ident
    fileofs, filelen, lumpver, fourcc = struct.unpack_from("<iii4s", data, 8 + index * 16)
    return data[fileofs:fileofs + filelen]


def worldlights(path, index):
    raw = lump(path, index)
    assert len(raw) % 88 == 0, len(raw)
    return [struct.unpack_from("<3f3f3fiii7f3i", raw, i * 88) for i in range(len(raw) // 88)]


def logvals(path):
    text = open(path, "r", errors="replace").read()

    def one(pat, cast=int, default=-1):
        m = re.search(pat, text, re.M)
        return cast(m.group(1)) if m else default

    return {
        "faces": one(r"^(\d+) faces\s*$"),
        "sqin": one(r"square feet \[([0-9.]+) square inches\]", str, "0.00"),
        "before": one(r"(\d+) patches before subdivision"),
        "after": one(r"(\d+) patches after subdivision"),
        "lights": one(r"(\d+) direct lights"),
        "degenerate": one(r"(\d+) degenerate faces", int, 0),
    }


def wl_line(tag, i, w):
    ox, oy, oz, ir, ig, ib, nx, ny, nz = w[0:9]
    cluster, wtype, style = w[9], w[10], w[11]
    stopdot, stopdot2, expo, radius, ca, la, qa = w[12:19]
    flags, texinfo, owner = w[19], w[20], w[21]
    nums = [ox, oy, oz, ir, ig, ib, nx, ny, nz, stopdot, stopdot2, expo, radius, ca, la, qa]
    return (f"{tag} {i} {wtype} {style} {cluster} "
            + " ".join(repr(float(x)) for x in nums)
            + f" {flags} {texinfo} {owner}")


def main():
    cat, p4c = sys.argv[1], sys.argv[2]
    entries = []
    for n in sorted(os.listdir(cat)):
        if n.endswith(".vradv.log"):
            name = n[: -len(".vradv.log")]
            entries.append((name, f"{cat}/{name}.vradv.log", f"{cat}/{name}.stockrad.bsp"))
    rad = f"{p4c}/rad"
    if os.path.isdir(rad):
        for n in sorted(os.listdir(rad)):
            if n.endswith(".log"):
                name = n[:-4]
                entries.append((name, f"{rad}/{name}.log", f"{rad}/{name}.bsp"))

    print("# Stock vrad reference. Regenerate with Fixtures/mkfixture.py.")
    print("# Stock x64 reference radiosity build (Feb 17 2025) run under wine,")
    print("#   -threads 1 -verbose -game <tools/mapgame> <map>   (and -both for wlh)")
    print("# on stock vbsp + vvis -threads 1 output. A count stock did not print is -1")
    print("# (a map with no vis never subdivides and prints no patch lines).")
    print("#")
    print("# map <name>")
    print("# counts <faces> <patchesBefore> <patchesAfter> <directLights> <degenerateFaces>")
    print("# area <total square inches, as vrad printed %.2f>")
    print("# wl  <index> <type> <style> <cluster> <origin x y z> <intensity r g b> <normal x y z>")
    print("#     <stopdot> <stopdot2> <exponent> <radius> <constant> <linear> <quadratic>")
    print("#     <flags> <texinfo> <owner>                          LUMP_WORLDLIGHTS, LDR run")
    print("# wlh the same fields, LUMP_WORLDLIGHTS_HDR of the -both run")
    print()
    for name, log, bsp in entries:
        v = logvals(log)
        print(f"map {name}")
        print(f"counts {v['faces']} {v['before']} {v['after']} {v['lights']} {v['degenerate']}")
        print(f"area {v['sqin']}")
        for i, w in enumerate(worldlights(bsp, LUMP_WORLDLIGHTS)):
            print(wl_line("wl", i, w))
        both = f"{p4c}/both/{name}.bsp"
        if os.path.exists(both):
            for i, w in enumerate(worldlights(both, LUMP_WORLDLIGHTS_HDR)):
                print(wl_line("wlh", i, w))
        print()


if __name__ == "__main__":
    main()
