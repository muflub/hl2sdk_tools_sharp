#!/usr/bin/env python3
"""Second instrument: raw Python over the BSP, no C# involved.

Answers the three questions the caster gate rests on -- how many displacements
and of what power, whether any entity casts brush shadows, and how many sky
faces there are -- so that the managed numbers are checked against something
that shares no code with them.
"""
import struct
import sys
from collections import Counter

LUMP = dict(entities=0, texdata=2, verts=3, texinfo=6, faces=7, leafs=10,
            edges=12, surfedges=13, models=14, leafbrushes=17, brushes=18,
            brushsides=19, dispinfo=26, dispverts=33, disptris=48, nodes=5,
            facesHdr=58)

SURF_SKY = 0x0004
CONTENTS_SOLID = 0x1
CONTENTS_OPAQUE = 0x80
CONTENTS_MOVEABLE = 0x4000
MASK_OPAQUE = CONTENTS_SOLID | CONTENTS_MOVEABLE | CONTENTS_OPAQUE


def main(path):
    d = open(path, 'rb').read()
    lumps = [struct.unpack_from('<iiii', d, 8 + i * 16) for i in range(64)]

    def raw(name):
        o, l, v, _ = lumps[LUMP[name]]
        return d[o:o + l], v

    ents, _ = raw('entities')
    text = ents.decode('latin-1')
    print('entities lump %d bytes, vrad_brush_cast_shadows occurrences: %d'
          % (len(ents), text.count('vrad_brush_cast_shadows')))

    di, _ = raw('dispinfo')
    stride = 176
    n = len(di) // stride
    powers = Counter()
    tris = 0
    contents = Counter()
    for i in range(n):
        base = i * stride
        power, = struct.unpack_from('<i', di, base + 20)
        cont, = struct.unpack_from('<i', di, base + 32)
        powers[power] += 1
        contents[cont] += 1
        tris += (1 << power) * (1 << power) * 2
    print('dispinfo: %d entries of %d bytes, powers %s, contents %s'
          % (n, stride, dict(powers), dict(contents)))
    print('  sum 2^p*2^p*2 over all = %d' % tris)
    opaque = 0
    for i in range(n):
        base = i * stride
        power, = struct.unpack_from('<i', di, base + 20)
        cont, = struct.unpack_from('<i', di, base + 32)
        if cont & MASK_OPAQUE:
            opaque += (1 << power) * (1 << power) * 2
    print('  sum over MASK_OPAQUE only = %d' % opaque)

    faces, fv = raw('faces')
    ti, _ = raw('texinfo')
    fstride = 56
    tstride = 72
    nf = len(faces) // fstride
    sky = 0
    skytris = 0
    edgetotal = 0
    for i in range(nf):
        b = i * fstride
        numedges, = struct.unpack_from('<h', faces, b + 8)
        texinfo, = struct.unpack_from('<h', faces, b + 10)
        edgetotal += numedges - 2
        if texinfo < 0:
            continue
        flags, = struct.unpack_from('<i', ti, texinfo * tstride + 64)
        if flags & SURF_SKY:
            sky += 1
            skytris += numedges - 2
    print('faces: %d, sum(numedges-2) = %d  <-- stock "Total triangle count"' % (nf, edgetotal))
    print('  SURF_SKY faces %d, their fan triangles %d' % (sky, skytris))

    models, _ = raw('models')
    print('models: %d' % (len(models) // 48))

    brushes, _ = raw('brushes')
    nb = len(brushes) // 12
    op = 0
    for i in range(nb):
        cont, = struct.unpack_from('<i', brushes, i * 12 + 8)
        if cont & MASK_OPAQUE:
            op += 1
    print('brushes: %d, MASK_OPAQUE %d' % (nb, op))


if __name__ == '__main__':
    main(sys.argv[1])
