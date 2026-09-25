#!/usr/bin/env python3
"""How much of the world-brush difference is geometry and how much is printing.

The base winding is scaled by MAX_COORD_INTEGER*4 = 131072 before it is clipped
(polylib.cpp:296), so a one-ULP difference in the normalised `vup` reaches the
clipped corner as something of order 0.01 -- which is exactly the last digit
stock's %5.2f prints.  A set difference taken at two decimals therefore counts
"the same triangle, printed differently" alongside "a triangle that is not
there at all".  Re-binning at coarser resolutions separates the two.
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
            out.append((col, tuple(pts)))
            if limit and len(out) >= limit:
                break
    return out


def quantise(tris, step):
    return Counter(
        tuple(tuple(round(c / step) for c in p) for p in pts) for _, pts in tris)


def main(stock_path, ours_path, count_stock, count_ours):
    stock = read(stock_path, True, count_stock)
    ours = read(ours_path, False, count_ours)
    print('stock %d, ours %d' % (len(stock), len(ours)))
    for step in (0.01, 0.1, 0.5, 1.0, 2.0, 4.0):
        a = quantise(stock, step)
        b = quantise(ours, step)
        print('  bin %5.2f   in stock only %5d   in ours only %5d'
              % (step, sum((a - b).values()), sum((b - a).values())))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]))
