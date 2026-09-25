#!/usr/bin/env python3
"""Count and bound the triangles in vrad's -dumptrace output.

WriteRTEnv colours each winding by which TRACE_ID bits its triangle id carries:
green = TRACE_ID_OPAQUE (brushes and displacements), blue = TRACE_ID_SKY,
red = TRACE_ID_STATICPROP.  The colour is therefore the caster SOURCE, which
is what the 4b gate compares per source.
"""
import sys
from collections import Counter

NAMES = {
    ('0.000', '0.996', '0.000'): 'opaque',
    ('0.000', '0.000', '0.996'): 'sky',
    ('0.996', '0.000', '0.000'): 'staticprop',
    ('0.000', '0.000', '0.000'): 'none',
}


def main(path):
    counts = Counter()
    boxes = {}
    total = 0
    mins = [1e30] * 3
    maxs = [-1e30] * 3
    with open(path) as f:
        while True:
            head = f.readline()
            if not head:
                break
            k = int(head)
            col = None
            for _ in range(k):
                parts = f.readline().split()
                xyz = [float(v) for v in parts[:3]]
                col = tuple(parts[3:6])
                for j in range(3):
                    mins[j] = min(mins[j], xyz[j])
                    maxs[j] = max(maxs[j], xyz[j])
                b = boxes.setdefault(col, [1e30] * 3 + [-1e30] * 3)
                for j in range(3):
                    b[j] = min(b[j], xyz[j])
                    b[3 + j] = max(b[3 + j], xyz[j])
            counts[col] += 1
            total += 1
    print('triangles %d' % total)
    for col, ct in sorted(counts.items(), key=lambda kv: -kv[1]):
        b = boxes[col]
        print('  %-11s %7d  mins (%.2f %.2f %.2f)  maxs (%.2f %.2f %.2f)'
              % ((NAMES.get(col, str(col)), ct) + tuple(b[:3]) + tuple(b[3:])))
    print('scene mins (%.2f %.2f %.2f)  maxs (%.2f %.2f %.2f)'
          % (tuple(mins) + tuple(maxs)))


if __name__ == '__main__':
    main(sys.argv[1])
