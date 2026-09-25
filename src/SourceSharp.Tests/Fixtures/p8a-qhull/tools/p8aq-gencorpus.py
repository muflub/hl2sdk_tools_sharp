#!/usr/bin/env python3
"""p8aq-gencorpus.py - deterministic point-set corpus for the qhull 2.6 port gate.

Writes corpus/<name>.pts files in the oracle's input format:
    set <name> <n>
    <x> <y> <z>        C99 hex floats (float.hex), exact doubles
Every coordinate is a float32 value widened to double, in metres, like IVP's input
(Hammer inches * 0.0254f rounded to float), magnitudes about 0.001 .. 500.

Usage: p8aq-gencorpus.py <outdir> [randomcount]
"""
import math
import os
import random
import struct
import sys


def f32(x):
    return struct.unpack('<f', struct.pack('<f', x))[0]


INCH = f32(0.0254)


def m_from_in(x):
    """HL inches -> metres the way the engine does it: float(x) * 0.0254f in float."""
    return f32(f32(x) * INCH)


def fmt(v):
    return float(v).hex()


class Out:
    def __init__(self, path):
        self.f = open(path, 'w')
        self.n = 0
        self.names = set()

    def add(self, name, pts):
        assert name not in self.names, name
        self.names.add(name)
        self.f.write('set %s %d\n' % (name, len(pts)))
        for p in pts:
            self.f.write('%s %s %s\n' % (fmt(f32(p[0])), fmt(f32(p[1])), fmt(f32(p[2]))))
        self.n += 1

    def close(self):
        self.f.close()


def rot_matrix(rng):
    # random rotation from a random unit quaternion
    u1, u2, u3 = rng.random(), rng.random(), rng.random()
    q = (math.sqrt(1 - u1) * math.sin(2 * math.pi * u2), math.sqrt(1 - u1) * math.cos(2 * math.pi * u2),
         math.sqrt(u1) * math.sin(2 * math.pi * u3), math.sqrt(u1) * math.cos(2 * math.pi * u3))
    x, y, z, w = q
    return ((1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)))


def xform(pts, rng, scale=1.0, rotate=False, center=(0, 0, 0)):
    r = rot_matrix(rng) if rotate else ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    out = []
    for p in pts:
        q = [sum(r[i][k] * p[k] for k in range(3)) * scale + center[i] for i in range(3)]
        out.append(tuple(f32(c) for c in q))
    return out


def box(ex, ey, ez):
    return [(sx * ex, sy * ey, sz * ez) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]


def inch_box(rng):
    """an axis brush in inches, snapped to the grid, converted to metres"""
    g = rng.choice([1, 2, 4, 8, 16, 0.5, 0.25, 0.125])
    x0 = rng.randint(-512, 512) * g
    y0 = rng.randint(-512, 512) * g
    z0 = rng.randint(-512, 512) * g
    dx = rng.randint(1, 64) * g
    dy = rng.randint(1, 64) * g
    dz = rng.randint(1, 64) * g
    pts = []
    for sx in (0, 1):
        for sy in (0, 1):
            for sz in (0, 1):
                pts.append((m_from_in(x0 + sx * dx), m_from_in(y0 + sy * dy), m_from_in(z0 + sz * dz)))
    rng.shuffle(pts)
    return pts


def wedge(ex, ey, ez):
    return [(-ex, -ey, -ez), (ex, -ey, -ez), (-ex, ey, -ez), (ex, ey, -ez), (-ex, ey, ez), (ex, ey, ez)]


def prism(n, r, h, phase=0.0):
    pts = []
    for i in range(n):
        a = phase + 2 * math.pi * i / n
        pts.append((r * math.cos(a), r * math.sin(a), -h))
        pts.append((r * math.cos(a), r * math.sin(a), h))
    return pts


def pyramid(n, r, h, phase=0.0):
    pts = [(r * math.cos(phase + 2 * math.pi * i / n), r * math.sin(phase + 2 * math.pi * i / n), 0.0) for i in range(n)]
    pts.append((0.0, 0.0, h))
    return pts


def sphere_pts(rng, n, r):
    pts = []
    for _ in range(n):
        while True:
            v = (rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1))
            l = math.sqrt(sum(c * c for c in v))
            if l > 1e-6:
                break
        pts.append(tuple(r * c / l for c in v))
    return pts


def brush_from_planes(rng, nplanes, radius, inches=True):
    """random convex polytope: vertices are triple-plane intersections inside all planes"""
    planes = []
    for ax in range(3):
        for s in (-1, 1):
            n = [0.0, 0.0, 0.0]
            n[ax] = float(s)
            planes.append((n, radius * (0.6 + 0.4 * rng.random())))
    for _ in range(nplanes):
        v = [rng.gauss(0, 1) for _ in range(3)]
        l = math.sqrt(sum(c * c for c in v))
        v = [c / l for c in v]
        if inches:  # Hammer-like: integer-ish plane normals
            v = [round(c * 8) / 8 for c in v]
            l = math.sqrt(sum(c * c for c in v))
            if l < 1e-9:
                continue
            v = [c / l for c in v]
        planes.append((v, radius * (0.3 + 0.7 * rng.random())))
    verts = []
    m = len(planes)
    for i in range(m):
        for j in range(i + 1, m):
            for k in range(j + 1, m):
                a, b, c = planes[i][0], planes[j][0], planes[k][0]
                det = (a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0])
                       + a[2] * (b[0] * c[1] - b[1] * c[0]))
                if abs(det) < 1e-9:
                    continue
                da, db, dc = planes[i][1], planes[j][1], planes[k][1]
                # Cramer's rule for n.x = d
                x = (da * (b[1] * c[2] - b[2] * c[1]) - a[1] * (db * c[2] - b[2] * dc) + a[2] * (db * c[1] - b[1] * dc)) / det
                y = (a[0] * (db * c[2] - b[2] * dc) - da * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * dc - db * c[0])) / det
                z = (a[0] * (b[1] * dc - db * c[1]) - a[1] * (b[0] * dc - db * c[0]) + da * (b[0] * c[1] - b[1] * c[0])) / det
                p = (x, y, z)
                if all(sum(pl[0][q] * p[q] for q in range(3)) <= pl[1] + 1e-6 * radius for pl in planes):
                    if not any(sum((p[q] - w[q]) ** 2 for q in range(3)) < (1e-6 * radius) ** 2 for w in verts):
                        verts.append(p)
    return verts


def to_metres_inches(pts, rng, snap):
    out = []
    for p in pts:
        if snap:
            p = tuple(round(c * snap) / snap for c in p)
        out.append(tuple(m_from_in(c) for c in p))
    return out


def dup_variants(rng, pts, name, out):
    # exact duplicates
    d = list(pts) + [rng.choice(pts) for _ in range(max(1, len(pts) // 3))]
    rng.shuffle(d)
    out.add(name + '_dup', d)
    # near duplicates: 1 float ulp and 1e-6 relative
    nd = list(pts)
    for p in rng.sample(pts, max(1, len(pts) // 2)):
        c = list(p)
        k = rng.randrange(3)
        c[k] = struct.unpack('<f', struct.pack('<I', struct.unpack('<I', struct.pack('<f', f32(c[k])))[0] + 1))[0]
        nd.append(tuple(c))
    out.add(name + '_ulp', nd)
    nd2 = list(pts)
    for p in rng.sample(pts, max(1, len(pts) // 2)):
        nd2.append(tuple(f32(c * (1 + rng.uniform(-1e-6, 1e-6)) + rng.uniform(-1e-6, 1e-6)) for c in p))
    out.add(name + '_near', nd2)


def gen_shapes(rng, out):
    k = 0
    for i in range(60):
        out.add('abox%d' % i, inch_box(rng))
    for i in range(40):
        s = 10 ** rng.uniform(-3, 2.7)
        ex, ey, ez = [s * rng.uniform(0.05, 1) for _ in range(3)]
        c = [rng.uniform(-500, 500) * rng.choice([0, 0.01, 1]) for _ in range(3)]
        out.add('sbox%d' % i, xform(box(ex, ey, ez), rng, 1.0, False, c))
    for i in range(40):
        s = 10 ** rng.uniform(-2, 2)
        ex, ey, ez = [s * rng.uniform(0.05, 1) for _ in range(3)]
        c = [rng.uniform(-100, 100) for _ in range(3)]
        out.add('rbox%d' % i, xform(box(ex, ey, ez), rng, 1.0, True, c))
    for i in range(40):
        s = 10 ** rng.uniform(-2, 2)
        out.add('wedge%d' % i, xform(wedge(s, s * rng.uniform(0.1, 2), s * rng.uniform(0.1, 2)), rng, 1.0, i % 2 == 1,
                                     [rng.uniform(-50, 50) for _ in range(3)]))
    for i in range(40):
        s = 10 ** rng.uniform(-2, 2)
        out.add('tprism%d' % i, xform(prism(3, s, s * rng.uniform(0.1, 3), rng.uniform(0, 1)), rng, 1.0, i % 2 == 1,
                                      [rng.uniform(-50, 50) for _ in range(3)]))
    for i in range(40):
        s = 10 ** rng.uniform(-2, 2)
        out.add('hprism%d' % i, xform(prism(6, s, s * rng.uniform(0.1, 3), rng.uniform(0, 1) * (i % 3 != 0)), rng, 1.0,
                                      i % 2 == 1, [rng.uniform(-50, 50) for _ in range(3)]))
    for i in range(40):
        n = rng.randint(4, 32)
        s = 10 ** rng.uniform(-2, 2)
        out.add('nprism%d' % i, xform(prism(n, s, s * rng.uniform(0.05, 3), rng.uniform(0, 1)), rng, 1.0, i % 2 == 1,
                                      [rng.uniform(-50, 50) for _ in range(3)]))
    for i in range(40):
        n = rng.randint(3, 24)
        s = 10 ** rng.uniform(-2, 2)
        out.add('pyr%d' % i, xform(pyramid(n, s, s * rng.uniform(0.05, 3), rng.uniform(0, 1) * (i % 2)), rng, 1.0,
                                   i % 3 == 1, [rng.uniform(-50, 50) for _ in range(3)]))
    for i in range(30):
        n = rng.randint(4, 64)
        s = 10 ** rng.uniform(-2, 2)
        out.add('sphere%d' % i, xform(sphere_pts(rng, n, s), rng, 1.0, False, [rng.uniform(-50, 50) for _ in range(3)]))


def gen_brushes(rng, out):
    for i in range(300):
        r = rng.choice([4, 8, 16, 32, 64, 128, 512, 2048])
        pts = brush_from_planes(rng, rng.randint(0, 14), r, inches=True)
        if len(pts) < 4:
            continue
        c = (rng.randint(-4096, 4096), rng.randint(-4096, 4096), rng.randint(-4096, 4096))
        pts = [(p[0] + c[0], p[1] + c[1], p[2] + c[2]) for p in pts]
        snap = rng.choice([None, None, 8, 32, 1])
        pts = to_metres_inches(pts, rng, snap)
        rng.shuffle(pts)
        out.add('brush%d' % i, pts)
    for i in range(150):
        r = 10 ** rng.uniform(-2.5, 2.5)
        pts = brush_from_planes(rng, rng.randint(1, 20), r, inches=False)
        if len(pts) < 4:
            continue
        pts = xform(pts, rng, 1.0, False, [rng.uniform(-300, 300) for _ in range(3)])
        out.add('poly%d' % i, pts)


def gen_dups(rng, out):
    for i in range(60):
        pts = inch_box(rng)
        dup_variants(rng, pts, 'dbox%d' % i, out)
    for i in range(60):
        r = rng.choice([8, 32, 128])
        pts = brush_from_planes(rng, rng.randint(2, 10), r)
        if len(pts) < 4:
            continue
        pts = to_metres_inches(pts, rng, None)
        dup_variants(rng, pts, 'dbrush%d' % i, out)
    for i in range(40):
        n = rng.randint(3, 16)
        s = 10 ** rng.uniform(-2, 2)
        pts = xform(prism(n, s, s * rng.uniform(0.1, 2), rng.uniform(0, 1)), rng, 1.0, True, [0, 0, 0])
        dup_variants(rng, pts, 'dprism%d' % i, out)


def gen_coplanar(rng, out):
    for i in range(80):
        g = rng.randint(2, 7)
        s = 10 ** rng.uniform(-2, 2)
        pts = []
        faces = rng.choice([6, 6, 3, 1])
        for f in range(faces):
            ax = f % 3
            sgn = -1 if f < 3 else 1
            for a in range(g + 1):
                for b in range(g + 1):
                    u = -s + 2 * s * a / g
                    v = -s + 2 * s * b / g
                    p = [0, 0, 0]
                    p[ax] = sgn * s
                    p[(ax + 1) % 3] = u
                    p[(ax + 2) % 3] = v
                    pts.append(tuple(p))
        # keep it a solid: add the 8 corners when not all six faces are present
        if faces < 6:
            pts += box(s, s, s)
        pts = xform(pts, rng, 1.0, i % 4 == 3, [rng.uniform(-50, 50) * (i % 2) for _ in range(3)])
        # dedupe exact repeats is NOT done: grid edges repeat on purpose
        rng.shuffle(pts)
        out.add('grid%d' % i, pts)
    for i in range(40):
        # coplanar clutter inside faces of a brush
        pts = inch_box(rng)
        xs = sorted(set(p[0] for p in pts))
        ys = sorted(set(p[1] for p in pts))
        zs = sorted(set(p[2] for p in pts))
        extra = []
        for _ in range(rng.randint(4, 40)):
            ax = rng.randrange(3)
            p = [f32(rng.uniform(xs[0], xs[-1])), f32(rng.uniform(ys[0], ys[-1])), f32(rng.uniform(zs[0], zs[-1]))]
            p[ax] = [xs, ys, zs][ax][rng.randrange(2) * -1]
            extra.append(tuple(p))
        allp = pts + extra
        rng.shuffle(allp)
        out.add('facecl%d' % i, allp)


def gen_clouds(rng, out):
    for i in range(200):
        n = rng.choice([4, 5, 6, 8, 12, 16, 24, 32, 48, 64, 100, 200, 300, 500]) if i >= 20 else rng.randint(4, 12)
        s = 10 ** rng.uniform(-3, 2.7)
        c = [rng.uniform(-300, 300) * rng.choice([0, 1]) for _ in range(3)]
        if i % 2:
            pts = [(rng.uniform(-s, s), rng.uniform(-s, s), rng.uniform(-s, s)) for _ in range(n)]
        else:
            sx, sy, sz = s, s * rng.uniform(0.05, 1), s * rng.uniform(0.05, 1)
            pts = [(rng.gauss(0, sx), rng.gauss(0, sy), rng.gauss(0, sz)) for _ in range(n)]
        pts = xform(pts, rng, 1.0, i % 3 == 0, c)
        out.add('cloud%d' % i, pts)


def gen_degenerate(rng, out):
    out.add('three', [(0, 0, 0), (1, 0, 0), (0, 1, 0)])
    out.add('two', [(0, 0, 0), (1, 0, 0)])
    out.add('allsame8', [(0.5, 0.25, 0.125)] * 8)
    out.add('allsame4', [(1.5, -2.25, 3.0)] * 4)
    for i in range(40):
        n = rng.randint(4, 40)
        s = 10 ** rng.uniform(-2, 2)
        z = rng.uniform(-10, 10)
        pts = [(rng.uniform(-s, s), rng.uniform(-s, s), z) for _ in range(n)]
        out.add('flat%d' % i, xform(pts, rng, 1.0, i % 2 == 1, [0, 0, 0]))
    for i in range(40):
        n = rng.randint(4, 40)
        s = 10 ** rng.uniform(-2, 2)
        th = s * 10 ** rng.uniform(-9, -4)
        pts = [(rng.uniform(-s, s), rng.uniform(-s, s), rng.uniform(-th, th)) for _ in range(n)]
        out.add('thin%d' % i, xform(pts, rng, 1.0, i % 2 == 1, [rng.uniform(-10, 10) for _ in range(3)]))
    for i in range(20):
        n = rng.randint(4, 20)
        s = 10 ** rng.uniform(-2, 2)
        d = [rng.gauss(0, 1) for _ in range(3)]
        pts = [tuple(t * c for c in d) for t in [rng.uniform(-s, s) for _ in range(n)]]
        out.add('line%d' % i, pts)
    for i in range(20):
        n = rng.randint(4, 12)
        s = 10 ** rng.uniform(-2, 2)
        th = s * 10 ** rng.uniform(-8, -5)
        pts = [(rng.uniform(-s, s), rng.uniform(-th, th), rng.uniform(-th, th)) for _ in range(n)]
        out.add('needle%d' % i, pts)
    for i in range(20):
        # the same x for all points (qh_maxsimplex "same x coordinate")
        n = rng.randint(4, 20)
        x = rng.uniform(-5, 5)
        pts = [(x, rng.uniform(-1, 1), rng.uniform(-1, 1)) for _ in range(n)]
        out.add('samex%d' % i, pts)
    for i in range(20):
        # thin slabs of brushes: a 1/8 inch thick plate
        pts = inch_box(rng)
        zs = sorted(set(p[2] for p in pts))
        pts = [(p[0], p[1], zs[0] if p[2] == zs[0] else f32(zs[0] + m_from_in(0.125) * 10 ** rng.uniform(-4, 0))) for p in pts]
        out.add('plate%d' % i, pts)
    for i in range(40):
        # far from the origin (large NEARzero): the first joggles are too small to help
        c = [rng.uniform(-500, 500) for _ in range(3)]
        n = rng.randint(4, 16)
        kind = i % 4
        if kind == 0:
            pts = [tuple(c)] * n
        elif kind == 1:
            d = [rng.gauss(0, 1) for _ in range(3)]
            pts = [tuple(c[k] + t * d[k] for k in range(3)) for t in [rng.uniform(-2, 2) for _ in range(n)]]
        elif kind == 2:
            pts = [(c[0] + rng.uniform(-1, 1), c[1] + rng.uniform(-1, 1), c[2]) for _ in range(n)]
        else:
            pts = [(c[0] + rng.uniform(-1e-4, 1e-4), c[1] + rng.uniform(-1e-4, 1e-4), c[2] + rng.uniform(-1e-9, 1e-9))
                   for _ in range(n)]
        out.add('far%d' % i, pts)


def gen_lattice(rng, out):
    """exactly coplanar / collinear configurations: integer lattices scaled by float units"""
    for i in range(400):
        m = rng.randint(1, 5)
        n = rng.randint(4, 60)
        unit = rng.choice([m_from_in(1), m_from_in(8), m_from_in(0.125), 0.25, 1.0, 0.001 * rng.randint(1, 9)])
        c = [rng.randint(-100, 100) * unit * rng.choice([0, 1]) for _ in range(3)]
        pts = [(c[0] + rng.randint(0, m) * unit, c[1] + rng.randint(0, m) * unit, c[2] + rng.randint(0, rng.choice([1, m])) * unit)
               for _ in range(n)]
        out.add('lat%d' % i, pts)
    for i in range(200):
        # a box plus points in the planes of its faces, outside the face (coplanar horizons)
        a = rng.randint(1, 6)
        unit = rng.choice([m_from_in(1), m_from_in(4), 0.5, 0.01])
        pts = [(x * unit, y * unit, z * unit) for x in (0, a) for y in (0, a) for z in (0, a)]
        for _ in range(rng.randint(1, 12)):
            ax = rng.randrange(3)
            p = [rng.randint(-a, 2 * a) * unit for _ in range(3)]
            p[ax] = rng.choice([0, a]) * unit
            pts.append(tuple(p))
        rng.shuffle(pts)
        if i % 3 == 0:
            pts = xform(pts, rng, 1.0, True, [0, 0, 0])
        out.add('coext%d' % i, pts)
    for i in range(200):
        # union of 2..5 axis boxes on a grid (compound brushes / studio collision soups)
        unit = rng.choice([m_from_in(1), m_from_in(2), m_from_in(16), 0.1])
        pts = []
        for _ in range(rng.randint(2, 5)):
            x0, y0, z0 = [rng.randint(0, 6) for _ in range(3)]
            dx, dy, dz = [rng.randint(1, 4) for _ in range(3)]
            pts += [((x0 + sx * dx) * unit, (y0 + sy * dy) * unit, (z0 + sz * dz) * unit)
                    for sx in (0, 1) for sy in (0, 1) for sz in (0, 1)]
        rng.shuffle(pts)
        out.add('union%d' % i, pts)
    for i in range(150):
        # points of a cube surface lattice, random subset, plus float-rounded rotations
        g = rng.randint(2, 6)
        pts = [(a, b, c) for a in range(g + 1) for b in range(g + 1) for c in range(g + 1)
               if a in (0, g) or b in (0, g) or c in (0, g)]
        pts = rng.sample(pts, max(4, int(len(pts) * rng.uniform(0.3, 1.0))))
        s = rng.choice([m_from_in(1), 0.05, 1.0])
        pts = xform(pts, rng, s, i % 2 == 1, [rng.uniform(-10, 10) * (i % 3 == 0) for _ in range(3)])
        out.add('cubelat%d' % i, pts)


def gen_random(rng, out, count):
    kinds = ['box', 'brush', 'cloud', 'gauss', 'prism', 'dup', 'grid', 'thin', 'sphere']
    for i in range(count):
        k = kinds[i % len(kinds)]
        if k == 'box':
            pts = inch_box(rng)
            if rng.random() < 0.3:
                pts = xform(pts, rng, 1.0, True, [0, 0, 0])
        elif k == 'brush':
            pts = brush_from_planes(rng, rng.randint(0, 12), rng.choice([8, 16, 64, 256]))
            if len(pts) < 4:
                pts = box(1, 1, 1)
            pts = to_metres_inches(pts, rng, rng.choice([None, 8]))
        elif k == 'cloud':
            n = rng.randint(4, 64)
            s = 10 ** rng.uniform(-3, 2.5)
            pts = [(rng.uniform(-s, s), rng.uniform(-s, s), rng.uniform(-s, s)) for _ in range(n)]
        elif k == 'gauss':
            n = rng.randint(4, 64)
            s = 10 ** rng.uniform(-3, 2.5)
            pts = [(rng.gauss(0, s), rng.gauss(0, s), rng.gauss(0, s * rng.uniform(0.01, 1))) for _ in range(n)]
        elif k == 'prism':
            n = rng.randint(3, 20)
            s = 10 ** rng.uniform(-2, 2)
            pts = xform(prism(n, s, s * rng.uniform(0.05, 2), rng.uniform(0, 1)), rng, 1.0, rng.random() < 0.5, [0, 0, 0])
        elif k == 'dup':
            base = inch_box(rng)
            pts = base + [rng.choice(base) for _ in range(rng.randint(1, 8))]
            rng.shuffle(pts)
        elif k == 'grid':
            g = rng.randint(2, 4)
            s = 10 ** rng.uniform(-2, 2)
            pts = []
            for a in range(g + 1):
                for b in range(g + 1):
                    for c in range(g + 1):
                        if a in (0, g) or b in (0, g) or c in (0, g):
                            pts.append((-s + 2 * s * a / g, -s + 2 * s * b / g, -s + 2 * s * c / g))
            pts = xform(pts, rng, 1.0, rng.random() < 0.5, [0, 0, 0])
            rng.shuffle(pts)
        elif k == 'thin':
            n = rng.randint(4, 24)
            s = 10 ** rng.uniform(-2, 2)
            th = s * 10 ** rng.uniform(-7, -2)
            pts = [(rng.uniform(-s, s), rng.uniform(-s, s), rng.uniform(-th, th)) for _ in range(n)]
        else:
            pts = sphere_pts(rng, rng.randint(4, 40), 10 ** rng.uniform(-2, 2))
        c = [rng.uniform(-400, 400) * rng.choice([0, 0, 1]) for _ in range(3)]
        pts = [(f32(p[0] + c[0]), f32(p[1] + c[1]), f32(p[2] + c[2])) for p in pts]
        out.add('r%d_%s' % (i, k), pts)


def main():
    outdir = sys.argv[1]
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 3000
    os.makedirs(outdir, exist_ok=True)
    gens = [('shapes', gen_shapes), ('brushes', gen_brushes), ('dups', gen_dups), ('coplanar', gen_coplanar),
            ('clouds', gen_clouds), ('degenerate', gen_degenerate), ('lattice', gen_lattice)]
    for idx, (name, g) in enumerate(gens):
        out = Out(os.path.join(outdir, 'p8aq-%s.pts' % name))
        g(random.Random(1000 + idx), out)
        out.close()
        print(name, out.n)
    out = Out(os.path.join(outdir, 'p8aq-random.pts'))
    gen_random(random.Random(4242), out, count)
    out.close()
    print('random', out.n)


if __name__ == '__main__':
    main()
