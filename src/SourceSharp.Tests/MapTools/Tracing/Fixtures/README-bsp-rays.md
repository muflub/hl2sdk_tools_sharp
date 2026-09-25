# The BSP surface tracer's oracle, and the numbers taken with it

Two binary files here, made in one run of the reference surface tracer
itself:

| file | what it is |
|---|---|
| `rays-lockdown-leafambient.bin` | 8,100 rays: `int32 count`, then `count` records of six `float32` (start xyz, end xyz) |
| `stock-lockdown-leafambient.bin` | stock's answers: `int32 count`, then `count` `int32` face indices (-1 = miss), then `count` `float32` fractions, then `count` bytes of `m_bHasLuxel` |

Made against `game/mod_sharp/maps/dm_lockdown.bsp` — BSP v19, 6,508 planes, 2,682 nodes,
6,536 faces, 2,768 texinfos, 2,725 leaves (LUMP_LEAFS at version 0), 7,986
leaffaces.

## Why these are not a golden output of this port

The harness does not reimplement stock. `build_oracle.py` assembles the
functions it gates on — `g_anorms[162]`, `RemoveColinearPoints`,
`PointInWinding`, `WindingFromFace`, `EnumerateNodesAlongRay_R` /
`EnumerateNodesAlongRay`, and `CLightSurface` — straight from the reference
implementation's sources and compiles them unchanged.

Everything else in the harness is scaffolding: a BSP loader, the globals those
functions read, a winding allocator, and a stub displacement manager. It
refuses to build if any of those functions stops carrying its name, so drift
is loud rather than silent.

The one piece of glue that is not stock is an overload of `WindingFromFace`
taking an rvalue. `CLightSurface` calls it with a temporary bound to a
non-const `Vector&`, which MSVC allows and GCC does not even with
`-fpermissive`; the overload forwards to stock's own definition and adds no
arithmetic.

**The reference drop ships no ready-made tracer test and no recorded
reference output.** Compiling stock's own tracer and
comparing against it is strictly better than a recorded text file anyway, and
it is what both tracers in this lane are gated on.

## The displacement stub is the point, not a shortcut

Stock ends every leaf with `StaticDispMgr()->ClipRayToDispInLeaf`. This port
does not — the displacement collision tree is lane 4b's — so the oracle's
displacement manager is stubbed to "no hit" as well. Otherwise the throughput
comparison would be between two different amounts of work.

`dm_lockdown` has displacement faces; `BspTraceGeometry.SkippedDisplacementFaces`
says how many, and a fact pins the number.

## The rays

Leaf ambient's own shape, from `ComputeAmbientFromSphericalSamples`: a random
point inside a non-solid leaf, rejected unless the point actually descends to
that leaf, and then the fixed 162-direction `g_anorms` fan out to
`COORD_EXTENT * 1.74`. Fifty sample points, seed 20260920.

**Not a lattice, deliberately.** A prior lane in this project measured 20,319
differing bits in 5 M rays on axis-aligned lattice geometry, falling to 4 once
the geometry was jittered by 0.35 units. On a lattice a tracer measures its own
epsilons rather than its traversal, and a parity gate over one is green for the
wrong reason.

## Reproducing

```
python3 <scratch>/build_oracle.py "$PWD/src" <scratch>
<scratch>/oracle_bsp game/mod_sharp/maps/dm_lockdown.bsp \
    <scratch>/rays.bin <scratch>/stock.bin gen:50:20260920
```

`oracle_bsp` also takes `bench <reps>` (time the full run), `walkonly <reps>`
(stock's traversal with a null enumerator, no candidate work) and `count`
(callbacks per ray).

## The measurements, and what the box did to them

Box: 32 hardware threads, two other lanes of this plan running concurrently
(load average 15–25 throughout). **Absolute figures drift by 1.6x with the
box's state** — stock's own number moved between 2.70 and 4.49 Mray/s with no
change to its binary — so every figure below is from an interleaved run where
stock and this port were measured within seconds of each other, and the ratio
inside a round is the number that means anything.

324,000 rays (2,000 sample points, seed 777), single-threaded, best of 7 for
the managed side and best of 12 for stock, four rounds:

| | Mray/s | ratio |
|---|---:|---:|
| stock `CLightSurface` + `EnumerateNodesAlongRay_R` | 4.12 | 1.00x |
| `BspSurfaceTracer`, **Release** | **6.06** | **1.47x** |
| `BspSurfaceTracer`, Debug | 2.55 | 0.62x |

**The Debug row is not a footnote.** `dotnet build` and `dotnet test` default to
Debug, the assembly then carries `DebuggableAttribute`, and the JIT compiles the
tracer with **MinOpts** — the disassembly says `; MinOpts code` and
`; debuggable code` in as many words. That is a 2.4x on this code, and it is
large enough to swallow every micro-optimisation in the lane: four probes
(hoisting ray components into locals, forcing the two callbacks inline, the
axial shortcut, removing the sky test) were measured under it first and gave
readings between "no effect" and "1.05x" that were simply wrong. **Any
throughput number taken through `dotnet test` without `-c Release` is a
measurement of the Debug JIT.**

Work per ray, from `oracle_bsp ... count` — this is what the optimisation
effort should have been aimed at from the start:

| | per ray |
|---|---:|
| `EnumerateNode` calls, full run | 2.193 |
| `EnumerateLeaf` calls, full run | 2.248 |
| `EnumerateNode` calls, traversal-only run | 13.18 |
| `EnumerateLeaf` calls, traversal-only run | 14.18 |

The full run enters six times fewer nodes than a walk that does not stop,
because it terminates at the first hit in front-to-back order. So the cost is
in the **descent loop**, which runs on every node the ray passes wholly on one
side of and never calls back at all — and that is why stock's axial shortcut,
which the first draft of this port dropped on an argument about branch
prediction, is worth 1.13x (5.94 against 5.26 Mray/s, Release, interleaved).

Stock's traversal-only run is **2.47 Mray/s against its own full run's 4.13**:
the early exit is worth more than everything else in the tracer.

## Correctness

| set | rays | face mismatches | fraction mismatches |
|---|---:|---:|---:|
| committed fixture | 8,100 | 0 | 0 |
| large set (seed 777) | 324,000 | 0 | 0 |

Fractions compared as **bits**, not within an epsilon. Every operation on the
path is reproduced in stock's order and operand order, so there is no rounding
left to differ, and an epsilon would hide the day that stops being true.

To run the large-set comparison:

```
SS_TRACE_RAY_SET=<scratch>/rays_bench.bin \
SS_TRACE_ANSWER_SET=<scratch>/stock_bench.bin \
dotnet test SourceSharp.Tests/SourceSharp.Tests.csproj -c Release \
    --filter FullyQualifiedName~MapTools.Tracing
```
