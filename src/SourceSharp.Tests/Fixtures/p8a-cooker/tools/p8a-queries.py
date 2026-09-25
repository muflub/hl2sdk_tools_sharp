# p8a: build a native query job for every blob in an answer file: load it back through vphysics
# (UnserializeCollide) and ask volume/area/AABB/mass centre, CollideGetExtent along 6 axes and
# 8 seeded diagonals, and TraceBox along seeded rays (points and a 2x2x2 box) aimed through the
# solid's AABB. The same seeds for every file, so two files with the same blobs give the same jobs.
# usage: p8a-queries.py answers.txt out.jobs [rays-per-blob] [seed]
import sys, random, struct, math
src, out = sys.argv[1], sys.argv[2]
nrays = int(sys.argv[3]) if len(sys.argv) > 3 else 16
seed = int(sys.argv[4]) if len(sys.argv) > 4 else 7
blobs = [l[4:].strip() for l in open(src) if l.startswith('hex ')]
dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]
r0 = random.Random(seed)
for k in range(8):
    v = [r0.gauss(0, 1) for _ in range(3)]
    l = math.sqrt(sum(c * c for c in v))
    dirs.append(tuple(c / l for c in v))
def fl(v):
    return float.hex(struct.unpack('<f', struct.pack('<f', v))[0])
def aabb_of(hexblob):
    # scan the ledge points in the blob for a coarse box (HL units) to aim rays; exactness not needed
    b = bytes.fromhex(hexblob)
    xs = []
    S = 28
    for o in range(S + 48, len(b) - 15, 16):
        pass
    return None
with open(out, 'w') as f:
    # collide 0: the swept object for TraceCollide, a 4x4x4 box
    f.write('B -0x1p+1 -0x1p+1 -0x1p+1 0x1p+1 0x1p+1 0x1p+1\nC 1 0 0 0\n')
    for jj, h in enumerate(blobs):
        j = jj + 1
        rnd = random.Random(seed * 1000003 + j)
        f.write('L ' + h + '\n')
        f.write('Q %d\n' % j)
        for d in dirs:
            f.write('E %d %s %s %s\n' % (j, fl(d[0]), fl(d[1]), fl(d[2])))
        # rays: from a random point on a sphere of radius R around the solid's centre towards a
        # random point near the centre; R and centre come from a Q answer we do not have here, so
        # aim at the origin frame of the blob's mass centre (read from the header, IVP metres)
        b = bytes.fromhex(h)
        mx, my, mz = struct.unpack_from('<fff', b, 28)
        rad = struct.unpack_from('<f', b, 52)[0]
        cx, cy, cz = mx / 0.0254, mz / 0.0254, -my / 0.0254
        R = max(rad / 0.0254, 1.0)
        for r in range(nrays):
            v = [rnd.gauss(0, 1) for _ in range(3)]; l = math.sqrt(sum(c * c for c in v)); v = [c / l for c in v]
            s = [cx + 2.5 * R * v[0], cy + 2.5 * R * v[1], cz + 2.5 * R * v[2]]
            t = [cx + rnd.uniform(-0.8, 0.8) * R, cy + rnd.uniform(-0.8, 0.8) * R, cz + rnd.uniform(-0.8, 0.8) * R]
            e = [t[k] + (t[k] - s[k]) for k in range(3)]
            hs = 0.0 if r % 2 == 0 else 2.0
            f.write('T %d %s %s %s %s %s %s %s %s %s\n' % (j, fl(s[0]), fl(s[1]), fl(s[2]), fl(e[0]), fl(e[1]), fl(e[2]), fl(hs), fl(hs), fl(hs)))
            if r % 4 == 1:
                f.write('S %d 0 %s %s %s %s %s %s\n' % (j, fl(s[0]), fl(s[1]), fl(s[2]), fl(e[0]), fl(e[1]), fl(e[2])))
    f.write('X\n')
print(len(blobs), 'blobs')
