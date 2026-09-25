# p8a: concatenate job files (dropping their X terminators) into one job file ending in X.
# usage: p8a-concat.py out.in a.in b.in ...
import sys
with open(sys.argv[1], 'w') as out:
    for p in sys.argv[2:]:
        for l in open(p):
            if l.strip() == 'X':
                continue
            out.write(l)
    out.write('X\n')
