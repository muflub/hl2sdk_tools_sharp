#!/usr/bin/env python3
"""Reduce stock vrad's -dumptrace output to a committable reference.

The raw dumps are 5.6 MB and 15 MB, which is not something to put in a test
fixture, and their floats are printed at two decimals anyway.  What is worth
keeping is what a gate can actually check:

  * per-source triangle count and bounds -- the count catches a missing or
    duplicated emission, the bounds catch a missing transform;
  * a coordinate checksum over the whole run, which catches a permutation that
    the count and bounds both survive;
  * the first and last 32 triangles of each run verbatim, which pins the ORDER
    -- and order is not cosmetic here, because a KD-tree hit reports a triangle
    INDEX and every world brush triangle in a map shares one identity.

Written by a script from stock's own output rather than typed, and stamped with
what produced it.
"""
import hashlib
import subprocess
import sys

NAMES = {
    ('0.000', '0.996', '0.000'): 'opaque',
    ('0.000', '0.000', '0.996'): 'sky',
    ('0.996', '0.000', '0.000'): 'staticprop',
}

# The colour runs in add order (vrad.cpp:2240, 2277, 2278, 2279).  dm_lockdown
# has no vrad_brush_cast_shadows entity, so its brush-entity run is absent and
# the first green run is the world's own brushes.
RUN_NAMES = ['world-brush', 'sky', 'displacement', 'static-prop']

EDGE = 32


def triangle_area(pts):
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = [
        tuple(float(v) for v in p) for p in pts]
    ux, uy, uz = bx - ax, by - ay, bz - az
    vx, vy, vz = cx - ax, cy - ay, cz - az
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    return 0.5 * (nx * nx + ny * ny + nz * nz) ** 0.5


def tris(path):
    with open(path) as f:
        while True:
            head = f.readline()
            if not head:
                return
            k = int(head)
            pts = []
            col = None
            for _ in range(k):
                parts = f.readline().split()
                pts.append(tuple(parts[:3]))
                col = tuple(parts[3:6])
            yield NAMES.get(col, str(col)), pts


def runs_of(path):
    out = []
    for name, pts in tris(path):
        if not out or out[-1][0] != name:
            out.append([name, []])
        out[-1][1].append(pts)
    return out


def emit(path, tag, extra_args, out):
    rs = runs_of(path)
    if len(rs) != len(RUN_NAMES):
        raise SystemExit('expected %d colour runs in %s, found %d: %s'
                         % (len(RUN_NAMES), path, len(rs), [r[0] for r in rs]))
    print('run %s' % tag, file=out)
    print('args -fast -dumptrace%s' % extra_args, file=out)
    for label, (colour, items) in zip(RUN_NAMES, rs):
        mins = [1e30] * 3
        maxs = [-1e30] * 3
        total = [0.0] * 3
        area = 0.0
        for pts in items:
            for p in pts:
                for j in range(3):
                    v = float(p[j])
                    mins[j] = min(mins[j], v)
                    maxs[j] = max(maxs[j], v)
                    total[j] += v
            area += triangle_area(pts)
        print('source %s %s %d' % (label, colour, len(items)), file=out)
        print('  mins %.2f %.2f %.2f' % tuple(mins), file=out)
        print('  maxs %.2f %.2f %.2f' % tuple(maxs), file=out)
        print('  sums %.2f %.2f %.2f' % tuple(total), file=out)
        # The physically meaningful total: how much surface this source puts in
        # front of a light.  A count can be right with the geometry wrong and a
        # bounding box can be right with the interior wrong; area is what a
        # shadow is actually made of, and it is the only aggregate here that
        # survives a few triangles moving between the two runs.
        print('  area %.3f' % area, file=out)
        head = items[:EDGE]
        tail = items[-EDGE:] if len(items) > EDGE else []
        for what, chunk, base in (('head', head, 0),
                                  ('tail', tail, len(items) - len(tail))):
            for n, pts in enumerate(chunk):
                print('  %s %d %s' % (what, base + n,
                                      ' '.join(v for p in pts for v in p)),
                      file=out)
    print('end %s' % tag, file=out)
    print('', file=out)


def main():
    bsp = sys.argv[1]
    dumps = sys.argv[2:]
    commit = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True,
                            text=True).stdout.strip()
    with open('/dev/stdout', 'w') as out:
        print('# Stock vrad shadow-caster reference, captured by', file=out)
        print('# scratchpad/p4b/make_fixture.py from vrad.exe -dumptrace output.', file=out)
        print('#', file=out)
        print('# vrad: Valve Software - vrad.exe SSE (Feb 17 2025), SDK Base 2013', file=out)
        print('#       Multiplayer, run under Proton Experimental wine.', file=out)
        print('# map:  dm_lockdown.bsp with its sprp game lump upgraded from', file=out)
        print('#       version 5 to version 10 (gamebspfile.h:244-262 field', file=out)
        print('#       mapping, all 261 props kept), because SDK-2013 vrad', file=out)
        print('#       refuses version 5 outright and a stage that loads props', file=out)
        print('#       cannot be gated against a map with none.', file=out)
        print('# original sha256 %s' % hashlib.sha256(open(bsp, 'rb').read()).hexdigest(),
              file=out)
        if commit:
            print('# tree: %s' % commit, file=out)
        print('', file=out)
        for path in dumps:
            tag = path.rsplit('trace-', 1)[1].rsplit('.txt', 1)[0]
            extra = {'base': '', 'spp': ' -StaticPropPolys',
                     'ts': ' -textureshadows',
                     'sppts': ' -StaticPropPolys -textureshadows'}[tag]
            emit(path, tag, extra, out)


if __name__ == '__main__':
    main()
