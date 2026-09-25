# p8a: decode a VPHY blob (hex on stdin or file) into its compact surface, ledge tree, ledges, triangles.
import sys, struct
def load(arg):
    s = open(arg).read() if arg != '-' else sys.stdin.read()
    for l in s.splitlines():
        if l.startswith('hex '):
            return bytes.fromhex(l[4:].strip())
    return bytes.fromhex(s.strip())
b = load(sys.argv[1] if len(sys.argv) > 1 else '-')
f = lambda o: struct.unpack_from('<f', b, o)[0]
i32 = lambda o: struct.unpack_from('<i', b, o)[0]
u32 = lambda o: struct.unpack_from('<I', b, o)[0]
print('VPHY', b[0:4], 'ver', hex(struct.unpack_from('<h', b, 4)[0]), 'type', struct.unpack_from('<h', b, 6)[0],
      'surfsize', i32(8), 'drag', [f(12), f(16), f(20)], 'axismap', i32(24), 'total', len(b))
S = 28
print('surface: mass_center', [f(S), f(S+4), f(S+8)], 'inertia', [f(S+12), f(S+16), f(S+20)], 'radius', f(S+24))
w = u32(S+28)
print('  max_factor', w & 255, 'byte_size', w >> 8, 'ledgetree_root_off', i32(S+32), 'dummy', [i32(S+36), i32(S+40), hex(u32(S+44))])
def ledge(o, ind):
    pts = i32(o); cd = i32(o+4); w = u32(o+8); nt = struct.unpack_from('<h', b, o+12)[0]; fut = struct.unpack_from('<h', b, o+14)[0]
    print(ind + 'LEDGE @%d point_off %d client/node %d has_children %d is_compact %d dummy %d size %d n_tri %d future %d' % (
        o - S, pts, cd, w & 3, (w >> 2) & 3, (w >> 4) & 15, (w >> 8) * 16, nt, fut))
    maxp = 0
    for t in range(nt):
        to = o + 16 + 16 * t
        tw = u32(to)
        edges = []
        for e in range(3):
            ew = u32(to + 4 + 4 * e)
            sp = ew & 0xffff; opp = (ew >> 16) & 0x7fff
            if opp & 0x4000: opp -= 0x8000
            edges.append((sp, opp, ew >> 31)); maxp = max(maxp, sp)
        print(ind + '  tri %d idx %d pierce %d mat %d virt %d edges %s' % (t, tw & 0xfff, (tw >> 12) & 0xfff, (tw >> 24) & 127, tw >> 31, edges))
    for p in range(maxp + 1):
        po = o + pts + 16 * p
        print(ind + '  pt %d (%.9g %.9g %.9g) h %.9g  [%08x %08x %08x %08x]' % (p, f(po), f(po+4), f(po+8), f(po+12), u32(po), u32(po+4), u32(po+8), u32(po+12)))
def node(o, ind):
    r = i32(o); cl = i32(o+4)
    print(ind + 'NODE @%d right %d ledge %d center (%.9g %.9g %.9g) radius %.9g box %s free %d' % (
        o - S, r, cl, f(o+8), f(o+12), f(o+16), f(o+20), list(b[o+24:o+27]), b[o+27]))
    if r == 0:
        ledge(o + cl, ind + '  ')
    else:
        if cl:
            print(ind + ' hull:'); ledge(o + cl, ind + '  ')
        node(o + 28, ind + '  ')
        node(o + r, ind + '  ')
node(S + i32(S+32), '')
