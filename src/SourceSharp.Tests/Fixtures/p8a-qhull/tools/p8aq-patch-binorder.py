#!/usr/bin/env python3
"""p8aq-patch-binorder.py - make csrc-binorder/: the oracle sources (csrc/, qhull 2.6 +
P8AQ_IDHASH) with every floating-point expression the IVP options reach rewritten to the
grouping GCC 10.3 -ffast-math emitted in vphysics.so (SDK build; objdump addresses cited).
Compile it -ffp-contract=off -fno-fast-math: the explicit parentheses then fix the order.

Each rewrite is asserted to apply exactly once."""
import os
import shutil

P = os.path.expanduser('~/.cache/maptools/lanes/p8a/qhullport')
src = os.path.join(P, 'csrc')
dst = os.path.join(P, 'csrc-binorder')
if os.path.exists(dst):
    shutil.rmtree(dst)
shutil.copytree(src, dst)

EDITS = [
    # ---- geom.h: fabs_ is andpd (sign clear) everywhere it is used on these paths
    ('geom.h',
     '#define fabs_( a ) ((( a ) < 0 ) ? -( a ):( a ))',
     '/* p8aq binorder: GCC -ffast-math emits fabs_ as andpd (clears the sign of -0.0),\n'
     '   e.g. aa5ac qh_gausselim, ac2dc qh_setfacetplane, 87fef qh_maxsimplex */\n'
     '#define fabs_( a ) fabs( a )'),
    # ---- geom.c qh_distplane, hull_dim 3 (a94d8)
    ('geom.c',
     '    *dist= facet->offset + point[0] * normal[0] + point[1] * normal[1] + point[2] * normal[2];',
     '    /* p8aq binorder a94d8: (x*nx + y*ny) + (offset + z*nz) */\n'
     '    *dist= (point[0] * normal[0] + point[1] * normal[1]) + (facet->offset + point[2] * normal[2]);'),
    # ---- geom.c qh_findbest searchdist (a9cee) and mincutoff (a9879)
    ('geom.c',
     '    searchdist= qh max_outside + 2 * qh DISTround\n                + fmax_( qh MINvisible, qh MAXcoplanar);',
     '    /* p8aq binorder a9cee: (2*DISTround + fmax) + max_outside */\n'
     '    searchdist= (2 * qh DISTround + fmax_( qh MINvisible, qh MAXcoplanar)) + qh max_outside;'),
    ('geom.c',
     '    mincutoff= -(qh DISTround - fmax_(qh MINvisible, qh MAXcoplanar));',
     '    /* p8aq binorder a9879: fmax - DISTround */\n'
     '    mincutoff= fmax_(qh MINvisible, qh MAXcoplanar) - qh DISTround;'),
    # ---- geom.c qh_gausselim: n = a * (1/pivot) (aa68a divsd 1.0/pivot, aa6e3 mulsd)
    ('geom.c',
     '    pivot= *pivotrow++;  /* signed value of pivot, and remainder of row */\n    for(i= k+1; i < numrow; i++) {',
     '    pivot= *pivotrow++;  /* signed value of pivot, and remainder of row */\n'
     '    p8aq_recip= 1.0/pivot;  /* p8aq binorder aa68a: reciprocal of the pivot */\n'
     '    for(i= k+1; i < numrow; i++) {'),
    ('geom.c',
     '      n= (*ai++)/pivot;   /* divzero() not needed since |pivot| >= |*ai| */',
     '      n= (*ai++) * p8aq_recip;   /* p8aq binorder aa6e3 */'),
    ('geom.c',
     '  realT n, pivot, pivot_abs= 0.0, temp;',
     '  realT n, pivot, pivot_abs= 0.0, temp, p8aq_recip;'),
    # ---- geom.c qh_getcenter: sum * (1.0/count) (aa9ff, aaa64)
    ('geom.c',
     '    *coord /= count;',
     '    *coord= *coord * (1.0/count);  /* p8aq binorder aa9ff/aaa64 */'),
    # ---- geom.c qh_getdistance: return maxsd(-mind, maxd) (aabcc)
    ('geom.c',
     '  mind= -mind;\n  if (maxd > mind)\n    return maxd;\n  else\n    return mind;',
     '  mind= -mind;\n  /* p8aq binorder aabcc: maxsd -> (-mind > maxd) ? -mind : maxd */\n'
     '  if (mind > maxd)\n    return mind;\n  else\n    return maxd;'),
    # ---- geom.c qh_sethyperplane_det, dim 3 (ab478..ab5e8)
    ('geom.c',
     '    normal[0]= det2_(dY(2,0), dZ(2,0),\n\t\t     dY(1,0), dZ(1,0));\n'
     '    normal[1]= det2_(dX(1,0), dZ(1,0),\n\t\t     dX(2,0), dZ(2,0));\n'
     '    normal[2]= det2_(dX(2,0), dY(2,0),\n\t\t     dX(1,0), dY(1,0));',
     '    /* p8aq binorder ab478/ab4c1/ab505: a*d + b*(p0 - p1) instead of a*d - b*c */\n'
     '    normal[0]= dY(2,0) * dZ(1,0) + dZ(2,0) * (rows[0][1] - rows[1][1]);\n'
     '    normal[1]= dX(1,0) * dZ(2,0) + dZ(1,0) * (rows[0][0] - rows[2][0]);\n'
     '    normal[2]= dX(2,0) * dY(1,0) + dY(2,0) * (rows[0][0] - rows[1][0]);'),
    ('geom.c',
     '        dist= *offset + (point[0]*normal[0] + point[1]*normal[1]\n'
     '\t       + point[2]*normal[2]);\n'
     '        if (dist > maxround || dist < -maxround) {',
     '        /* p8aq binorder ab5be: (z*nz + offset) + (x*nx + y*ny) */\n'
     '        dist= (point[2]*normal[2] + *offset) + (point[0]*normal[0] + point[1]*normal[1]);\n'
     '        if (dist > maxround || dist < -maxround) {'),
    # ---- geom2.c qh_determinant, dim 3 (85afc)
    ('geom2.c',
     '    det= det3_(rows[0][0], rows[0][1], rows[0][2],\n'
     '\t\t rows[1][0], rows[1][1], rows[1][2],\n'
     '\t\t rows[2][0], rows[2][1], rows[2][2]);',
     '    /* p8aq binorder 85afc: (a1*(b2c3-b3c2) + b1*(c2a3-c3a2)) + c1*(a2b3-b2a3) */\n'
     '    det= (rows[0][0] * (rows[1][1]*rows[2][2] - rows[1][2]*rows[2][1])\n'
     '          + rows[1][0] * (rows[2][1]*rows[0][2] - rows[2][2]*rows[0][1]))\n'
     '         + rows[2][0] * (rows[0][1]*rows[1][2] - rows[1][1]*rows[0][2]);'),
    # ---- geom2.c qh_detroundoff (861d9, 8639d)
    ('geom2.c',
     '  qh ANGLEround= 1.01 * qh hull_dim * REALepsilon;',
     '  qh ANGLEround= qh hull_dim * (1.01 * REALepsilon);  /* p8aq binorder 861e5: folded constant */'),
    ('geom2.c',
     '    qh ONEmerge= sqrt (qh hull_dim) * qh MAXwidth *\n'
     '      sqrt (1.0 - maxangle * maxangle) + qh DISTround;  ',
     '    /* p8aq binorder 863a9..863d1: sqrt((1 - m*m) * dim) * MAXwidth + DISTround */\n'
     '    qh ONEmerge= sqrt ((1.0 - maxangle * maxangle) * qh hull_dim) * qh MAXwidth + qh DISTround;'),
    # ---- geom2.c qh_joggleinput (898dd..89961)
    ('geom2.c',
     '  randa= 2.0 * qh JOGGLEmax/qh_RANDOMmax;',
     '  /* p8aq binorder 898ee: JOGGLEmax * (2.0/qh_RANDOMmax), folded constant */\n'
     '  randa= qh JOGGLEmax * (2.0/qh_RANDOMmax);'),
    ('geom2.c',
     '    *(coordp++)= *(inputp++) + (randr * randa + randb);',
     '    /* p8aq binorder 89954..89961: (input - JOGGLEmax) + randr*randa */\n'
     '    *(coordp++)= (*(inputp++) - qh JOGGLEmax) + randr * randa;'),
    # ---- geom2.c qh_maxmin (87de2)
    ('geom2.c',
     '    qh NEARzero[k]= 80 * qh MAXsumcoord * REALepsilon;',
     '    qh NEARzero[k]= (80 * REALepsilon) * qh MAXsumcoord;  /* p8aq binorder 87de2: folded constant */'),
]

for fn, old, new in EDITS:
    p = os.path.join(dst, fn)
    s = open(p).read()
    n = s.count(old)
    assert n == 1, (fn, n, old[:60])
    s = s.replace(old, new)
    open(p, 'w').write(s)
print('csrc-binorder: %d rewrites' % len(EDITS))
