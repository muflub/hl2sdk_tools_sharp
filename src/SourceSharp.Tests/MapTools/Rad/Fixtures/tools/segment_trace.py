#!/usr/bin/env python3
"""Segment vrad's -dumptrace output into its insertion-order runs.

vrad adds in one fixed order:
  ExtractBrushEntityShadowCasters  -> TRACE_ID_OPAQUE      (green)
  AddBrushesForRayTrace, brushes   -> TRACE_ID_OPAQUE      (green)
  AddBrushesForRayTrace, sky faces -> TRACE_ID_SKY         (blue)
  StaticDispMgr::AddPolysForRayTrace -> TRACE_ID_OPAQUE    (green)
  StaticPropMgr::AddPolysForRayTrace -> TRACE_ID_STATICPROP (red)

so the colour runs separate brushes from displacements even though both carry
TRACE_ID_OPAQUE.
"""
import sys

NAMES = {
    ('0.000', '0.996', '0.000'): 'opaque',
    ('0.000', '0.000', '0.996'): 'sky',
    ('0.996', '0.000', '0.000'): 'staticprop',
}


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
                pts.append(tuple(float(v) for v in parts[:3]))
                col = tuple(parts[3:6])
            yield NAMES.get(col, str(col)), pts


def main(path):
    runs = []
    for name, pts in tris(path):
        if not runs or runs[-1][0] != name:
            runs.append([name, 0, [1e30] * 3 + [-1e30] * 3])
        runs[-1][1] += 1
        b = runs[-1][2]
        for p in pts:
            for j in range(3):
                b[j] = min(b[j], p[j])
                b[3 + j] = max(b[3 + j], p[j])
    for name, count, b in runs:
        print('%-11s %7d  mins (%8.2f %8.2f %8.2f)  maxs (%8.2f %8.2f %8.2f)'
              % ((name, count) + tuple(b[:3]) + tuple(b[3:])))


if __name__ == '__main__':
    main(sys.argv[1])
