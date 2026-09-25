# p8a: generate cook jobs in the oracle line protocol.
# usage: p8a-gen.py kind count seed > jobs.in
# kinds: tri (3 random points), box (axis boxes), rbox (random boxes, V), wedge, prism, brush (random plane sets)
import sys, random, math
kind, count, seed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
rnd = random.Random(seed)
def f(v):
    return float.hex(float(v))
def emit_verts(pts):
    print('V %d' % len(pts))
    for p in pts:
        print('%s %s %s' % tuple(f(c) for c in p))
    print('C 1 0 0 0')
def emit_planes(planes, merge=0.0):
    print('P %d %s' % (len(planes), f(merge)))
    for p in planes:
        print('%s %s %s %s' % tuple(f(c) for c in p))
    print('C 1 0 0 0')
import struct
def fl(v):
    return struct.unpack('<f', struct.pack('<f', v))[0]
for i in range(count):
    if kind == 'tri':
        emit_verts([[fl(rnd.uniform(-512, 512)) for _ in range(3)] for _ in range(3)])
    elif kind == 'box':
        mins = [fl(rnd.choice([rnd.randint(-64, 64) * 8, rnd.uniform(-600, 600)])) for _ in range(3)]
        size = [fl(rnd.choice([rnd.randint(1, 64) * 4, rnd.uniform(0.5, 700)])) for _ in range(3)]
        maxs = [fl(mins[k] + size[k]) for k in range(3)]
        emit_planes([(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2])])
    elif kind == 'boxv':
        mins = [fl(rnd.randint(-64, 64) * 8) for _ in range(3)]
        maxs = [fl(mins[k] + rnd.randint(1, 64) * 4) for k in range(3)]
        emit_verts([[maxs[0] if b & 1 else mins[0], maxs[1] if b & 2 else mins[1], maxs[2] if b & 4 else mins[2]] for b in range(8)])
    elif kind == 'wedge':
        # axis box cut by one diagonal plane through two opposite edges
        mins = [fl(rnd.randint(-32, 32) * 16) for _ in range(3)]
        size = [fl(rnd.randint(1, 32) * 8) for _ in range(3)]
        maxs = [fl(mins[k] + size[k]) for k in range(3)]
        a, b = rnd.sample([0, 1, 2], 2)
        n = [0.0, 0.0, 0.0]
        n[a] = size[b]; n[b] = size[a]
        l = math.sqrt(n[a] ** 2 + n[b] ** 2); n = [fl(c / l) for c in n]
        d = fl(n[a] * maxs[a] + n[b] * mins[b])
        emit_planes([(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2]), (n[0], n[1], n[2], d)])
    elif kind == 'prism':
        sides = rnd.choice([3, 5, 6, 8])
        r = rnd.randint(8, 256); h0 = rnd.randint(-256, 0); h1 = h0 + rnd.randint(8, 256)
        planes = [(0, 0, 1, fl(h1)), (0, 0, -1, fl(-h0))]
        for s in range(sides):
            ang = 2 * math.pi * s / sides
            planes.append((fl(math.cos(ang)), fl(math.sin(ang)), 0.0, fl(r)))
        emit_planes(planes)
    elif kind == 'brush':
        n = rnd.randint(5, 14)
        planes = []
        for k in range(n):
            v = [rnd.gauss(0, 1) for _ in range(3)]
            l = math.sqrt(sum(c * c for c in v)); v = [fl(c / l) for c in v]
            planes.append((v[0], v[1], v[2], fl(rnd.uniform(16, 256))))
        emit_planes(planes)
    elif kind == 'cloud':
        n = rnd.randint(4, 60)
        mode = rnd.choice(['box', 'sphere', 'gauss'])
        pts = []
        for k in range(n):
            if mode == 'box':
                pts.append([fl(rnd.uniform(-100, 100)) for _ in range(3)])
            elif mode == 'sphere':
                v = [rnd.gauss(0, 1) for _ in range(3)]; l = math.sqrt(sum(c * c for c in v))
                r = rnd.uniform(5, 200)
                pts.append([fl(r * c / l) for c in v])
            else:
                pts.append([fl(rnd.gauss(0, 50)) for _ in range(3)])
        emit_verts(pts)
    elif kind == 'cyl':
        seg = rnd.choice([6, 8, 12, 16, 24, 32])
        r = rnd.uniform(4, 128); h = rnd.uniform(4, 256); z0 = rnd.uniform(-100, 100)
        pts = []
        for ring in range(rnd.choice([2, 3])):
            z = z0 + h * ring / 2
            for k in range(seg):
                a = 2 * math.pi * k / seg
                pts.append([fl(r * math.cos(a)), fl(r * math.sin(a)), fl(z)])
        emit_verts(pts)
    elif kind == 'sliver':
        mins = [fl(rnd.randint(-32, 32) * 16) for _ in range(3)]
        size = [fl(rnd.randint(1, 32) * 8) for _ in range(3)]
        k = rnd.randint(0, 2)
        size[k] = fl(rnd.choice([0.01, 0.05, 0.1, 0.25, 0.5, 1.0]))
        maxs = [fl(mins[j] + size[j]) for j in range(3)]
        emit_planes([(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2])])
    elif kind == 'merge':
        n = rnd.randint(5, 14)
        planes = []
        for k in range(n):
            v = [rnd.gauss(0, 1) for _ in range(3)]
            l = math.sqrt(sum(c * c for c in v)); v = [fl(c / l) for c in v]
            planes.append((v[0], v[1], v[2], fl(rnd.uniform(16, 256))))
        emit_planes(planes, fl(rnd.choice([0.1, 0.5, 1.0, 2.0])))
    elif kind == 'multi':
        k = rnd.randint(2, 12)
        base = [rnd.uniform(-500, 500) for _ in range(3)]
        for j in range(k):
            mins = [fl(base[a] + rnd.randint(-8, 8) * 32) for a in range(3)]
            maxs = [fl(mins[a] + rnd.randint(1, 8) * 16) for a in range(3)]
            print('P 6 0x0p+0')
            for pl in [(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2])]:
                print('%s %s %s %s' % tuple(f(c) for c in pl))
        print('C %d %d 0 0' % (k, rnd.choice([0, 1])))
    elif kind == 'drag':
        # a brush model: 1-6 convexes (brushes, boxes, wedges), outer hull when >1, drag areas on,
        # the emitter's epsilon shape (a fraction of the bounds' area), sometimes tiny
        k = rnd.randint(1, 6)
        base = [rnd.uniform(-500, 500) for _ in range(3)]
        for j in range(k):
            if rnd.random() < 0.5:
                mins = [fl(base[a] + rnd.randint(-4, 4) * 16) for a in range(3)]
                maxs = [fl(mins[a] + rnd.choice([rnd.randint(1, 16) * 8, rnd.uniform(0.5, 100)])) for a in range(3)]
                pl = [(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2])]
            else:
                n = rnd.randint(5, 12); pl = []
                c = [base[a] + rnd.uniform(-64, 64) for a in range(3)]
                for q in range(n):
                    v = [rnd.gauss(0, 1) for _ in range(3)]
                    l = math.sqrt(sum(x * x for x in v)); v = [x / l for x in v]
                    d = sum(v[a] * c[a] for a in range(3)) + rnd.uniform(8, 96)
                    pl.append((fl(v[0]), fl(v[1]), fl(v[2]), fl(d)))
            print('P %d 0x0p+0' % len(pl))
            for q in pl:
                print('%s %s %s %s' % tuple(f(x) for x in q))
        eps = fl(rnd.choice([0.25, 1.0, 4.0, 16.0, 64.0, rnd.uniform(0.01, 200)]))
        print('C %d %d 1 %s' % (k, 1 if k > 1 else 0, f(eps)))
    elif kind == 'soup':
        # a displacement-like triangle soup; materials 0..3 (0 exercises stock's material walk)
        n = rnd.randint(1, 40)
        print('Y %d' % n)
        c = [rnd.uniform(-300, 300) for _ in range(3)]
        for t in range(n):
            pts = [[fl(c[a] + rnd.uniform(-64, 64)) for a in range(3)] for _ in range(3)]
            if rnd.random() < 0.05:
                pts[2] = list(pts[0])  # degenerate
            print(' '.join(f(x) for p3 in pts for x in p3) + ' %d' % rnd.choice([0, 0, 1, 2, 3, 5]))
    elif kind == 'mesh':
        # a displacement grid: power 2-4, random heights, 2 triangles per quad, alternating diagonals
        pw = rnd.choice([2, 3, 3, 4]) if rnd.random() < 0.9 else 4
        side = (1 << pw) + 1
        size = rnd.choice([64.0, 128.0, 256.0, 512.0])
        o = [fl(rnd.uniform(-1000, 1000)) for _ in range(3)]
        amp = rnd.choice([0.0, 8.0, 32.0, 128.0])
        verts = []
        for y in range(side):
            for x in range(side):
                verts.append([fl(o[0] + size * x / (side - 1)), fl(o[1] + size * y / (side - 1)), fl(o[2] + rnd.uniform(-amp, amp))])
        tris = []
        for y in range(side - 1):
            for x in range(side - 1):
                i0 = y * side + x; i1 = i0 + 1; i2 = i0 + side; i3 = i2 + 1
                if (x + y) % 2 == 0:
                    tris += [(i0, i2, i1), (i1, i2, i3)]
                else:
                    tris += [(i0, i3, i1), (i0, i2, i3)]
        print('M %d %d 1' % (len(verts), len(tris)))
        for v in verts:
            print('%s %s %s' % tuple(f(x) for x in v))
        for t in tris:
            print('%d %d %d' % t)
    elif kind == 'bmodel':
        # a Hammer-like brush model: 1-4 grid-aligned boxes, some cut by a ramp plane through
        # grid points; vbsp's own shrink/merge (0) and its drag epsilon (1% of the smallest bounding
        # face, clamped to [1, 1024]); outer hull when more than one convex
        k = rnd.randint(1, 4)
        base = [rnd.randint(-64, 64) * 16 for _ in range(3)]
        lo = [1e30] * 3; hi = [-1e30] * 3
        for j in range(k):
            mins = [base[a] + rnd.randint(-8, 8) * 8 for a in range(3)]
            maxs = [mins[a] + rnd.choice([1, 2, 4, 8, 16, 32, 64]) * rnd.choice([1, 2, 4, 8]) for a in range(3)]
            lo = [min(lo[a], mins[a]) for a in range(3)]; hi = [max(hi[a], maxs[a]) for a in range(3)]
            pl = [(1, 0, 0, maxs[0]), (-1, 0, 0, -mins[0]), (0, 1, 0, maxs[1]), (0, -1, 0, -mins[1]), (0, 0, 1, maxs[2]), (0, 0, -1, -mins[2])]
            if rnd.random() < 0.5:
                a, b = rnd.sample([0, 1, 2], 2)
                da = maxs[a] - mins[a]; db = maxs[b] - mins[b]
                n = [0.0, 0.0, 0.0]; n[a] = db; n[b] = da
                l = math.sqrt(n[a] ** 2 + n[b] ** 2); n = [c / l for c in n]
                d = n[a] * maxs[a] + n[b] * mins[b]
                pl.append((fl(n[0]), fl(n[1]), fl(n[2]), fl(d)))
            print('P %d 0x0p+0' % len(pl))
            for q in pl:
                print('%s %s %s %s' % tuple(f(x) for x in q))
        size = [hi[a] - lo[a] for a in range(3)]
        area = min(size[(i + 1) % 3] * size[(i + 2) % 3] for i in range(3))
        eps = fl(min(max(fl(area) * fl(1e-2), 1.0), 1024.0))
        print('C %d %d 1 %s' % (k, 1 if k > 1 else 0, f(eps)))
print('X')
