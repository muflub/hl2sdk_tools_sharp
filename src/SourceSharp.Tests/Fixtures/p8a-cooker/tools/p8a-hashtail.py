# Keeps the first N "hex" answers whole and replaces later ones with "sha <sha256> <length>".
import sys, hashlib
keep = int(sys.argv[1])
n = 0
for line in sys.stdin:
    if line.startswith('hex '):
        n += 1
        if n > keep:
            b = bytes.fromhex(line[4:].strip())
            line = 'sha %s %d\n' % (hashlib.sha256(b).hexdigest(), len(b))
    sys.stdout.write(line)
