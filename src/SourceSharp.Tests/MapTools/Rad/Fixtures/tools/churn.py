#!/usr/bin/env python3
"""Compare the managed caster dump with stock's, per source run.

Both files are WriteRTEnv's format at two decimals, so a triangle is comparable
as its three printed vertex triples.  Stock's file carries a colour per vertex
and the managed one does not, so the managed side is segmented by the counts the
stock side reports -- which is sound only because the counts of three of the
four runs are already known equal.
"""
import sys
from collections import Counter

NAMES = {
    ('0.000', '0.996', '0.000'): 'opaque',
    ('0.000', '0.000', '0.996'): 'sky',
    ('0.996', '0.000', '0.000'): 'staticprop',
}


def read(path, coloured):
    out = []
    with open(path) as f:
        while True:
            head = f.readline()
            if not head:
                return out
            k = int(head)
            pts = []
            col = None
            for _ in range(k):
                parts = f.readline().split()
                pts.append(tuple(parts[:3]))
                if coloured:
                    col = tuple(parts[3:6])
            out.append((NAMES.get(col, col), tuple(pts)))


def runs(tris):
    out = []
    for name, pts in tris:
        if not out or out[-1][0] != name:
            out.append([name, []])
        out[-1][1].append(pts)
    return out


def main(stock_path, ours_path, labels):
    stock = runs(read(stock_path, True))
    ours = read(ours_path, False)

    at = 0
    for (colour, s), label in zip(stock, labels):
        # The managed run is the SAME LENGTH as stock's for every source but the
        # world brushes; for that one the length is taken from the caller.
        n = LENGTHS[label]
        o = [pts for _, pts in ours[at:at + n]]
        at += n

        sc = Counter(s)
        oc = Counter(o)
        only_stock = sum((sc - oc).values())
        only_ours = sum((oc - sc).values())
        same_order = sum(1 for a, b in zip(s, o) if a == b)
        print('%-12s stock %6d  ours %6d   in stock only %4d  in ours only %4d  '
              'positionally equal %6d'
              % (label, len(s), len(o), only_stock, only_ours, same_order))


LENGTHS = {}

if __name__ == '__main__':
    stock_path, ours_path = sys.argv[1], sys.argv[2]
    labels = sys.argv[3].split(',')
    for pair in sys.argv[4].split(','):
        k, v = pair.split('=')
        LENGTHS[k] = int(v)
    main(stock_path, ours_path, labels)
