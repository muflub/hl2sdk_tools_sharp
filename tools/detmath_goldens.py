#!/usr/bin/env python3
"""Writes the golden table for the deterministic math library's facts.

  pip install mpmath
  python3 tools/detmath_goldens.py > src/SourceSharp.Tests/MapFormats/Numerics/DetMathGoldens.cs

Every expected value is the correctly rounded result (round to nearest, ties to
even) of the real function, computed with mpmath at 3000 bits and rounded here
with exact integer arithmetic. mpmath is an independent implementation, so the
table checks DetMath/DetMathF against something other than themselves; and
since a correctly rounded result is unique, the same table must pass on every
OS and CPU, which is what the determinism facts assert.

An argument whose result lies within 2^-2900 of a rounding boundary (an exact
case, such as a power that is exactly a float midpoint) cannot be decided from
a 3000-bit approximation, so the script stops rather than guess; those cases
are written as hand-derived facts instead.
"""

import math
import random
import struct
import sys

import mpmath
from mpmath import mp, mpf

mp.prec = 3000

SINGLE = (24, -126, 127)
DOUBLE = (53, -1022, 1023)


def f32(x):
    # struct's 'f' packing overflows rather than rounding to infinity, so a
    # double is rounded to float by the same exact routine as everything else.
    return round_to(mpf(x), SINGLE)


def bits32(x):
    return struct.unpack('<I', struct.pack('<f', x))[0]


def bits64(x):
    return struct.unpack('<Q', struct.pack('<d', x))[0]


def round_to(v, fmt):
    """Correctly rounds the mpf v to the format; returns a Python float."""
    p, emin, emax = fmt
    if isinstance(v, float) and math.isnan(v):
        return v
    if mpmath.isnan(v):
        return float('nan')
    if mpmath.isinf(v):
        return float(v)
    if v == 0:
        return 0.0
    sign = -1.0 if v < 0 else 1.0
    a = abs(v)
    e = int(mpmath.floor(mpmath.log(a, 2)))
    # guard against log rounding at exact powers of two
    while mpf(2) ** e > a:
        e -= 1
    while mpf(2) ** (e + 1) <= a:
        e += 1
    if e > emax:
        return sign * float('inf')
    q = max(e - (p - 1), emin - (p - 1))
    scaled = a / mpf(2) ** q
    fl = int(mpmath.floor(scaled))
    frac = scaled - fl
    # Only a midpoint is ambiguous: a value within 2^-2900 of a representable
    # number rounds to it from either side.
    if abs(frac - mpf(0.5)) < mpf(2) ** -2900:
        raise ValueError('too close to a rounding boundary to decide: %s' % mpmath.nstr(v, 30))
    if frac > 0.5:
        fl += 1
    m = fl
    if m == 2 ** p:
        m //= 2
        q += 1
    if q + p - 1 > emax:
        return sign * float('inf')
    return sign * math.ldexp(float(m), q)


def single(v):
    r = round_to(v, SINGLE)
    return bits32(r)


def double(v):
    return bits64(round_to(v, DOUBLE))


def double_then_single(v):
    return bits32(f32(round_to(v, DOUBLE)))


def main():
    rng = random.Random(20260927)
    rows = []  # (kind, name, a_bits, b_bits, expected_bits)

    def f_args():
        pts = [1e-30, 1e-10, 2.0 ** -20, 1e-4, 0.1, 0.5, 0.785398185, 1.0, 1.5707963, 1.57079637, 2.0,
               3.14159274, 3.1415925, 4.71238899, 6.28318548, 10.0, 100.0, 1000.0, 12345.678,
               33554432.0, 1e10, 1e20, 3.4e38, 1.17549435e-38, 1.4e-45]
        pts += [d * f32(math.pi / 180) for d in range(0, 361, 15)]
        pts += [rng.uniform(-10, 10) for _ in range(40)]
        pts += [rng.uniform(-1e6, 1e6) for _ in range(10)]
        out = []
        for x in pts:
            x = f32(x)
            out += [x, -x]
        return sorted(set(out), key=lambda t: (abs(t), t))

    for x in f_args():
        if x == 0:
            continue
        m = mpf(x)
        rows.append(('F1', 'Sin', bits32(x), 0, single(mp.sin(m))))
        rows.append(('F1', 'Cos', bits32(x), 0, single(mp.cos(m))))
        rows.append(('F1', 'Tan', bits32(x), 0, single(mp.tan(m))))

    unit = [1.0, 0.99999994, 0.9999, 0.9, 0.70710677, 0.5, 0.25, 0.1, 1e-3, 2.0 ** -20, 1e-8, 1e-30, 1.4e-45]
    unit += [rng.uniform(-1, 1) for _ in range(60)]
    for x in sorted(set(f32(u) for u in unit + [-u for u in unit])):
        if x == 0:
            continue
        m = mpf(x)
        rows.append(('F1', 'Asin', bits32(x), 0, single(mp.asin(m))))
        if x != 1:
            rows.append(('F1', 'Acos', bits32(x), 0, single(mp.acos(m))))

    pairs = [(1, 1), (1, -1), (-1, -1), (-1, 1), (1e-30, 1), (1, 1e-30), (1.4e-45, 3.4e38), (3.4e38, 1.4e-45),
             (-1.4e-45, -3.4e38), (0.5, -2), (3, 4), (-3, 4), (1, 0.00001)]
    pairs += [(rng.uniform(-100, 100), rng.uniform(-100, 100)) for _ in range(80)]
    for y, x in pairs:
        y, x = f32(y), f32(x)
        rows.append(('F2', 'Atan2', bits32(y), bits32(x), single(mp.atan2(mpf(y), mpf(x)))))

    pw = [(0.5, 2.2), (0.5, 1 / 2.2), (2, 0.5), (10, 38), (10, -45), (1.0001, 100000), (0.9999, 100000),
          (3, 40), (7, -20), (0.1, 3.3), (1e-20, 2.5), (123.456, 7.89), (2, 127.99), (2, -149.5),
          (0.75, 0.3), (0.999, 0.001), (65504, 8), (1.5, 218.7), (-2, 3), (-2, -3), (-0.5, 11)]
    pw += [(rng.uniform(0, 1), rng.choice([0.5, 1.3, 2, 2.2, 3.7, 10, 50])) for _ in range(60)]
    pw += [(rng.uniform(0.01, 100), rng.uniform(-20, 20)) for _ in range(40)]
    for x, y in pw:
        x, y = f32(x), f32(y)
        v = mp.power(mpf(x), mpf(y))
        rows.append(('F2', 'Pow', bits32(x), bits32(y), single(v)))

    # Double functions and the ToSingle forms, over the call sites' argument shapes.
    dargs = [math.pi, math.pi / 2, -math.pi / 2, 1e-300, 5e-324, 1e-10, 0.5, 1.0, 2.0, 10.0, 1e5, 1e22, 1.7e308]
    dargs += [(d / 180.0) * math.pi for d in range(-360, 361, 15)]
    dargs += [f32(d * f32(1.0 / 180)) * math.pi for d in (-90, -45, 30, 60, 90, 300)]
    dargs += [rng.uniform(-4, 4) for _ in range(30)]
    for x in sorted(set(dargs)):
        if x == 0:
            continue
        m = mpf(x)
        rows.append(('D1', 'Sin', bits64(x), 0, double(mp.sin(m))))
        rows.append(('D1', 'Cos', bits64(x), 0, double(mp.cos(m))))
        rows.append(('S1', 'SinToSingle', bits64(x), 0, double_then_single(mp.sin(m))))
        rows.append(('S1', 'CosToSingle', bits64(x), 0, double_then_single(mp.cos(m))))

    largs = [5e-324, 1e-300, 1e-10, 0.1, 0.5, 0.99999, 1 - 2 ** -53, 1 + 2 ** -52, 1.00001, 2.0, 10.0, 1e10, 1.7e308]
    largs += [f32(rng.uniform(1e-7, 1.0)) for _ in range(40)]
    for x in sorted(set(largs)):
        rows.append(('D1', 'Log', bits64(x), 0, double(mp.log(mpf(x)))))

    dpw = [(r / 255.0, 2.2) for r in range(1, 256, 7)]
    dpw += [(i / 1024.0, 1.0 / f32(2.2)) for i in range(1, 4096, 97)]
    dpw += [(f32(rng.uniform(0, 2000)) / 256.0, 1.0 / 2.2) for _ in range(30)]
    dpw += [(0.3, 2.5), (1.5, 300.25), (2.0, 0.5), (1e-5, 0.1), (7.0, -3.0), (1 + 2 ** -52, 2.0 ** 60)]
    for x, y in dpw:
        v = mp.power(mpf(x), mpf(y))
        rows.append(('D2', 'Pow', bits64(x), bits64(y), double(v)))
        rows.append(('S2', 'PowToSingle', bits64(x), bits64(y), double_then_single(v)))

    w = sys.stdout.write
    w('//========= Copyright Valve Corporation, All rights reserved. ============//\n')
    w('//\n')
    w('// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:\n')
    w('// https://github.com/ValveSoftware/source-sdk-2013\n')
    w('//\n')
    w('//=============================================================================//\n')
    w('\n')
    w('// <auto-generated>\n')
    w('// Written by tools/detmath_goldens.py from mpmath at 3000 bits. Do not edit;\n')
    w('// rerun the script.\n')
    w('// </auto-generated>\n')
    w('\n')
    w('namespace SourceSharp.Tests.MapFormats.Numerics;\n')
    w('\n')
    w('/// <summary>Correctly rounded results computed independently of DetMath.</summary>\n')
    w('internal static class DetMathGoldens\n')
    w('{\n')
    w('    /// <summary>\n')
    w('    /// Kind (F: float arguments and result, D: double, S: double arguments\n')
    w('    /// and a float result; 1 or 2 arguments), function, argument bits, and\n')
    w('    /// the expected result bits.\n')
    w('    /// </summary>\n')
    w('    public static readonly (string Kind, string Function, ulong A, ulong B, ulong Expected)[] Rows =\n')
    w('    [\n')
    for kind, name, a, b, e in rows:
        w('        ("%s", "%s", 0x%XUL, 0x%XUL, 0x%XUL),\n' % (kind, name, a, b, e))
    w('    ];\n')
    w('}\n')


if __name__ == '__main__':
    main()
