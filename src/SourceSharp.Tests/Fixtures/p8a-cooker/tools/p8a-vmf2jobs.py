# p8a: every brush (solid) in the given VMFs as a ConvexFromPlanes job, one collide per solid.
# Plane from the side's three points the way vbsp's PlaneFromPoints does it:
# n = normalize((p0 - p1) x (p2 - p1)), d = p0. n (computed in double, rounded to float)
# usage: p8a-vmf2jobs.py out.in a.vmf [b.vmf...] (also writes out.in.index: map solid-id per job)
import sys, re, struct, math
def fl(v):
    return struct.unpack('<f', struct.pack('<f', v))[0]
out = open(sys.argv[1], 'w')
index = open(sys.argv[1] + '.index', 'w')
njobs = 0
for path in sys.argv[2:]:
    text = open(path, errors='replace').read()
    # crude block walk: find 'solid' blocks and their 'side' plane lines
    for m in re.finditer(r'\bsolid\s*\{', text):
        depth = 0
        i = m.end() - 1
        start = i
        while True:
            c = text[i]
            if c == '{':
                depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0:
                    break
            i += 1
        block = text[start:i]
        sid = re.search(r'"id"\s*"(\d+)"', block)
        planes = []
        for pm in re.finditer(r'"plane"\s*"\(([^)]*)\)\s*\(([^)]*)\)\s*\(([^)]*)\)"', block):
            p = [[float(x) for x in pm.group(k).split()] for k in (1, 2, 3)]
            t1 = [p[0][k] - p[1][k] for k in range(3)]
            t2 = [p[2][k] - p[1][k] for k in range(3)]
            n = [t1[1] * t2[2] - t1[2] * t2[1], t1[2] * t2[0] - t1[0] * t2[2], t1[0] * t2[1] - t1[1] * t2[0]]
            l = math.sqrt(sum(c * c for c in n))
            if l == 0:
                continue
            n = [c / l for c in n]
            d = sum(p[0][k] * n[k] for k in range(3))
            planes.append([fl(n[0]), fl(n[1]), fl(n[2]), fl(d)])
        if len(planes) < 4:
            continue
        out.write('P %d 0x0p+0\n' % len(planes))
        for pl in planes:
            out.write('%s %s %s %s\n' % tuple(float.hex(c) for c in pl))
        out.write('C 1 0 0 0\n')
        index.write('%s %s\n' % (path.split('/')[-1], sid.group(1) if sid else '?'))
        njobs += 1
out.write('X\n')
print(njobs, 'solids')
