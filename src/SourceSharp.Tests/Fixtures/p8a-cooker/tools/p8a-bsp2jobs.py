# p8a: every brush of a Source BSP (LUMP_BRUSHES/BRUSHSIDES/PLANES) as a ConvexFromPlanes job.
# Bevel sides are skipped (ivp.cpp BuildConvexForBrush uses the brush's real planes).
# usage: p8a-bsp2jobs.py out.in map.bsp [max]
import sys, struct
out, path = sys.argv[1], sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 1 << 30
b = open(path, 'rb').read()
ident, version = struct.unpack_from('<4si', b, 0)
def lump(i):
    ofs, ln, ver, fourcc = struct.unpack_from('<iiii', b, 8 + 16 * i)
    return b[ofs:ofs + ln]
planes = lump(1); brushes = lump(18); sides = lump(19)
P = [struct.unpack_from('<ffffi', planes, o) for o in range(0, len(planes), 20)]
S = [struct.unpack_from('<Hhhh', sides, o) for o in range(0, len(sides), 8)]
n = 0
with open(out, 'w') as f:
    for o in range(0, len(brushes), 12):
        first, num, contents = struct.unpack_from('<iii', brushes, o)
        pl = []
        for s in S[first:first + num]:
            planenum, texinfo, disp, bevel = s
            if bevel:
                continue
            nx, ny, nz, d, t = P[planenum]
            pl.append((nx, ny, nz, d))
        if len(pl) < 4:
            continue
        f.write('P %d 0x0p+0\n' % len(pl))
        for p in pl:
            f.write('%s %s %s %s\n' % tuple(float.hex(c) for c in p))
        f.write('C 1 0 0 0\n')
        n += 1
        if n >= limit:
            break
    f.write('X\n')
print(n, 'brushes from', path, ident, version)
