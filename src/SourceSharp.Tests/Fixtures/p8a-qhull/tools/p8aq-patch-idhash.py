#!/usr/bin/env python3
"""Copy qhull 2.6 into csrc/ and patch qh_gethash (poly.c) so that, under
-DP8AQ_IDHASH, it hashes vertex ids instead of vertex pointers.  Without the
define the code is the original.  This is the only change to the qhull sources."""
import os, re, shutil
L = os.environ.get('P8AQ_SCRATCH', os.path.expanduser('~/p8aq'))
src = os.path.join(L, 'qhull26')
dst = os.path.join(L, 'qhullport', 'csrc')
os.makedirs(dst, exist_ok=True)
for f in os.listdir(src):
    if f.endswith(('.c', '.h', '.txt')):
        shutil.copy(os.path.join(src, f), os.path.join(dst, f))
p = os.path.join(dst, 'poly.c')
s = open(p).read()
a = s.index('unsigned qh_gethash (int hashsize')
b = s.index('} /* gethash */')
body = s[a:b]
nb = body.replace('(ptr_intT)(*elemp)', 'P8AQ_K(*elemp)')
nb = re.sub(r'\(ptr_intT\)elemp\[(\d)\]', r'P8AQ_K(elemp[\1])', nb)
nb = nb.replace('(ptr_intT) skipelem', 'P8AQ_K(skipelem)').replace('(ptr_intT)skipelem', 'P8AQ_K(skipelem)')
nb = nb.replace('if ((elem= (ptr_intT)*elemp++) != P8AQ_K(skipelem)) {',
                'elem= P8AQ_K(*elemp); elemp++;\n      if (elem != P8AQ_K(skipelem)) {')
pre = '''/* p8aq (SourceSharp port, 2026-09): with -DP8AQ_IDHASH, hash vertex ids
   instead of vertex pointers so the oracle is deterministic and matches the
   C# port.  Without P8AQ_IDHASH this is the original qhull 2.6 code. */
#ifdef P8AQ_IDHASH
#define P8AQ_K(p) ((p) ? (ptr_intT)((vertexT *)(p))->id: (ptr_intT)0)
#else
#define P8AQ_K(p) ((ptr_intT)(p))
#endif
'''
assert '(ptr_intT)*elemp' not in nb, nb
s = s[:a] + pre + nb + s[b:]
open(p, 'w').write(s)
print('patched', p)
