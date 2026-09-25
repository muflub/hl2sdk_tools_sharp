#!/usr/bin/env python3
"""What the world-brush churn is actually made of: area.

A winding corner that flips between SIDE_ON and SIDE_FRONT under an epsilon of
exactly zero adds or removes a fan triangle at that corner -- and a corner that
is within a few ULPs of the clipping plane makes a triangle with almost no area.
If every differing triangle is a sliver, the divergence cannot cast or fail to
cast a shadow, and the count difference is cosmetic.  If some are not, it can.
"""
import sys
from collections import Counter


def read(path, coloured, limit=None):
    out = []
    with open(path) as f:
        while True:
            head = f.readline()
            if not head:
                break
            k = int(head)
            pts = []
            col = None
            for _ in range(k):
                parts = f.readline().split()
                pts.append(tuple(float(v) for v in parts[:3]))
                if coloured:
                    col = tuple(parts[3:6])
            out.append(tuple(pts))
            if limit and len(out) >= limit:
                break
    return out


def area(t):
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = t
    ux, uy, uz = bx - ax, by - ay, bz - az
    vx, vy, vz = cx - ax, cy - ay, cz - az
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    return 0.5 * (nx * nx + ny * ny + nz * nz) ** 0.5


def key(t, step=1.0):
    return tuple(tuple(round(c / step) for c in p) for p in t)


def main(stock_path, ours_path, ns, no):
    stock = read(stock_path, True, ns)
    ours = read(ours_path, False, no)

    a = Counter(key(t) for t in stock)
    b = Counter(key(t) for t in ours)
    only_stock = a - b
    only_ours = b - a

    for label, src, keys in (('in stock only', stock, only_stock),
                             ('in ours only', ours, only_ours)):
        want = Counter(keys)
        areas = []
        for t in src:
            k = key(t)
            if want[k] > 0:
                want[k] -= 1
                areas.append(area(t))
        areas.sort()
        n = len(areas)
        print('%-14s %3d triangles; area min %.6f  median %.6f  max %.4f; '
              '%d of them under 1 square unit'
              % (label, n, areas[0], areas[n // 2], areas[-1],
                 sum(1 for x in areas if x < 1.0)))
        print('               largest five: %s'
              % ', '.join('%.3f' % x for x in areas[-5:]))
    print('total brush area  stock %.1f  ours %.1f'
          % (sum(area(t) for t in stock), sum(area(t) for t in ours)))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]))
