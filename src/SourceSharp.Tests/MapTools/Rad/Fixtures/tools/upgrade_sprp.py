#!/usr/bin/env python3
"""Rewrite a BSP's sprp game lump from version 5 to version 10, in a copy.

gamebspfile.h:244-262 -- the V4 upgrade path sets forcedFadeScale 1.0, dx levels
0, lightmap res 0 and ORs in STATIC_PROP_NO_PER_TEXEL_LIGHTING (0x800); V5 then
overwrites forcedFadeScale from its own field.  Nothing about the shadow-caster
set changes: origin, angles, model index, solid type and the NO_SHADOW flag all
carry across unchanged, which is the whole point of doing this rather than
emptying the lump.
"""
import struct
import sys
import shutil

NO_PER_TEXEL = 0x800


def upgrade(src, dst):
    shutil.copyfile(src, dst)
    d = bytearray(open(dst, 'rb').read())
    lumps = [list(struct.unpack_from('<iiii', d, 8 + i * 16)) for i in range(64)]
    go, gl, gv, _ = lumps[35]
    cnt, = struct.unpack_from('<i', d, go)
    entries = [list(struct.unpack_from('<ihhii', d, go + 4 + i * 16)) for i in range(cnt)]
    sprp = None
    for e in entries:
        if e[0].to_bytes(4, 'little')[::-1] == b'sprp':
            sprp = e
    if sprp is None:
        raise SystemExit('no sprp')
    gid, _flags, ver, fo, fl = sprp
    if ver == 10:
        print('already v10')
        return
    if ver != 5:
        raise SystemExit('only v5 handled, got %d' % ver)

    n, = struct.unpack_from('<i', d, fo)
    off = fo + 4 + n * 128
    nleaf, = struct.unpack_from('<i', d, off)
    off += 4 + nleaf * 2
    nprop, = struct.unpack_from('<i', d, off)
    off += 4
    out = bytearray(d[fo:off])
    for k in range(nprop):
        b = off + k * 60
        org = d[b:b + 12]
        ang = d[b + 12:b + 24]
        ptype, firstleaf, leafcount = struct.unpack_from('<HHH', d, b + 24)
        solid = d[b + 30]
        v5flags = d[b + 31]
        skin, fmin, fmax = struct.unpack_from('<iff', d, b + 32)
        lorg = d[b + 44:b + 56]
        ffs, = struct.unpack_from('<f', d, b + 56)
        newflags = v5flags | NO_PER_TEXEL
        out += org + ang
        out += struct.pack('<HHHBB', ptype, firstleaf, leafcount, solid, 0)
        out += struct.pack('<iff', skin, fmin, fmax)
        out += lorg
        out += struct.pack('<fHHIHH', ffs, 0, 0, newflags, 0, 0)

    blocks = {e[0]: bytes(d[e[3]:e[3] + e[4]]) for e in entries}
    blocks[gid] = bytes(out)

    header_size = 4 + cnt * 16
    newblock = bytearray()
    cursor = go + header_size
    for e in entries:
        e[3] = cursor + len(newblock)
        e[4] = len(blocks[e[0]])
        if e[0] == gid:
            e[2] = 10
        newblock += blocks[e[0]]

    tail_start = go + gl
    newgl = header_size + len(newblock)
    delta = newgl - gl

    newhdr = bytearray(struct.pack('<i', cnt))
    for e in entries:
        newhdr += struct.pack('<ihhii', e[0], e[1], e[2], e[3], e[4])

    d[go:tail_start] = bytes(newhdr) + bytes(newblock)
    moved = 0
    for i in range(64):
        if lumps[i][1] and lumps[i][0] > go:
            lumps[i][0] += delta
            moved += 1
    lumps[35][1] = newgl
    for i in range(64):
        struct.pack_into('<iiii', d, 8 + i * 16, *lumps[i])
    open(dst, 'wb').write(bytes(d))
    print('sprp v5 -> v10: %d props, lump %d -> %d bytes, %d lumps shifted %+d'
          % (nprop, gl, newgl, moved, delta))


if __name__ == '__main__':
    upgrade(sys.argv[1], sys.argv[2])
