# The KD-tree's oracle: the stock tracer, compiled

**Two scenes**, four files each, every set from one run:

| prefix | scene |
|---|---|
| `kd-scene` | 2,000 triangles in general position, 8,000 rays, seed 31337, 600-unit cube |
| `kd-boxes` | 1,800 triangles of axis-aligned boxes at jittered positions, 6,000 rays, seed 90210, 800-unit cube |

| file | what it is |
|---|---|
| `<prefix>.tris.bin` | `int32 count`, then `count` records of nine `float32` (three vertices) |
| `<prefix>.rays.bin` | `int32 count`, then `count` records of six `float32` (start xyz, end xyz) |
| `<prefix>.tree.bin` | stock's TREE: node count, then `(children, split)` int pairs; index count, then the index list; six floats of scene bounds; triangle count, then per triangle the plane normal, `d`, id, six edge coefficients and two coordinate-select bytes |
| `<prefix>.answers.bin` | `int32 count`, then `count` `int32` hit triangle indices (-1 = miss), then `count` `float32` hit distances |

## The oracle is the whole tracer, not an extract

The reference's KD tracer is compiled **unchanged** and linked against a
driver that supplies the six symbols it needs from tier0 and mathlib
(`g_pMemAlloc`, `Error`, `Four_Zeros`, `Four_Ones`, `Four_Epsilons`,
`LightDesc_t::RecalculateDerivedValues`). So the tree in `kd-scene.tree.bin` is
the reference's surface-area heuristic and the reference's partition order, not
a reading of them.

**The reference drop ships no ray-tracer unit tests and no recorded
regression output.** Compiling the reference tracer itself is a stronger check
than a recorded output file would have been: it compares the acceleration
structure node for node, where a recorded output would only have compared
answers.

## Why there are two scenes, and how that was found out

`kd-scene` is 2,000 triangles in general position: three vertices scattered
around a random centre, so triangles overlap, straddle splits and face every
direction. **Nothing is axis-aligned and nothing is on a lattice** — a prior
lane measured 20,319 differing bits in 5 M rays on lattice geometry, falling to
4 with a 0.35-unit jitter, because on a lattice a tracer measures its own
epsilons rather than its traversal. Stock builds 10,995 nodes and 14,806 index
entries from it.

**Mutation testing said that scene was not enough.** Against it alone, five
mutations of stock's own build and traversal rules stayed GREEN. None was a
weak fact; each mutation never reached its subject.

`kd-boxes` is a room of axis-aligned boxes at jittered positions — what vrad
actually feeds this tracer: world brush faces and prop hulls. The boxes do not
line up with each other or with any grid, so this is not a lattice in the sense
above; what is wanted is axis-PARALLEL faces. Adding it rescued one of the
five:

| mutation | scene 1 alone | both scenes |
|---|---|---|
| empty-side growing removed | green | **RED** |
| a triangle lying in the split plane goes LEFT | green | green — see below |
| depth cap 21 → 18 | green | green |
| reciprocal estimate → exact divide | green | green |
| mailbox disabled | green | green — correct, see below |

The full result over both scenes, 19 facts:

| mutation | facts failed |
|---|---:|
| SAH traversal cost 75 → 70 | 3 |
| SAH intersection cost 167 → 160 | 6 |
| candidate stride `1 + n/10` → `1 + n/8` | 6 |
| empty-side growing removed | 3 |
| `BoxSurfaceArea` computed entirely in double | 6 |
| `NormaliseLikeStock` → an exact normalise | 3 |
| near and far child swapped | 5 |
| a triangle lying in the split plane goes LEFT | **0** |
| depth cap 21 → 18 | **0** |
| reciprocal estimate → exact divide | **0** |
| mailbox disabled | **0** |

Four still green, and they are not the same kind of thing:

- **The split-plane triangle is DEAD CODE, in stock as well as here.**
  `ClassifyAgainstAxisSplit`'s third test can never run: if `minc == maxc == c`
  then either `c >= splitValue` and the first test returned POSITIVE, or
  `c < splitValue` and, since `maxc` is also `c`, the second returned NEGATIVE.
  No scene can make this mutation red, because no input reaches the line.
- **The mailbox staying green is correct**, not a gap. It is a pure
  optimisation — testing the same triangle twice gives the same answer — so a
  fact that went red when it was removed would be testing the wrong thing.
- **The depth cap and the reciprocal estimate are genuinely unverified.**
  Neither recorded scene builds a branch deeper than 18, and no ray in either
  is decided by the reciprocal's last bits. Both are reproduced from stock and
  both are believed right; neither is *checked*. A scene big enough to reach
  depth 21 would settle the first, and that is a fixture this lane did not
  build.

## Reproducing

```
python3 <scratch>/build_oracle_kd.py "$PWD/src" <scratch>
<scratch>/oracle_kd 2000 8000 31337 <scratch>/kd-scene answer 1 300 0
<scratch>/oracle_kd 1800 6000 90210 <scratch>/kd-boxes answer 1 400 1
```

Arguments: triangles, rays, seed, output prefix, `answer|bench`, reps, scene
half-extent, scene kind (0 general position, 1 axis-aligned boxes).

## Results

**Correctness.** Both scenes, all checks, zero differences:

| check | result |
|---|---|
| node count | 10,995 = stock's |
| every node's packed `children` word and split bits | identical |
| triangle index list, entry for entry | identical (14,806) |
| every triangle's plane, six edge coefficients and projection axes | identical bits |
| scene bounds | identical |
| hit triangle per ray | 0 mismatches |
| hit distance per ray | 0 mismatches, compared as BITS |

Two sizes, checked against a compiled `sizeof` of stock's own header:

| struct | stock | here |
|---|---:|---:|
| `CacheOptimizedKDNode` | 8 | 8 |
| `CacheOptimizedTriangle` | **48** | 48 |

The reference's header comment says "this structure is 16longs=64 bytes for
cache line packing" over the triangle, and the original plan repeated the 64.
The compiler says 48 — four floats
of plane, an int id, six floats of edge equation, four bytes — so the comment
is stale and the plan inherited it.

**One float-precision boundary decided the whole tree.** `BoxSurfaceArea` is
`2.0*((d0*d2)+(d0*d1)+(d1*d2))`: the three products and two sums are FLOAT and
only the doubling is promoted. Computing the expression in double instead — the
obvious reading of "2.0 makes it a double expression" — gave a tree with **two
nodes too many**, one split that stock declines, on this 2,000-triangle scene.
The node-for-node comparison is what caught it; a ray comparison would not have,
because the extra split changed no answer.

**Throughput**, single-threaded, same scene and rays, interleaved with stock so
the ratio is taken inside a round (the box ran two other lanes throughout):

| | Mray/s | ratio |
|---|---:|---:|
| stock `Trace4Rays` | 0.43 | 1.00x |
| `KdRayTracer`, Release | 0.39 | **0.91x** |

**This does not meet the lane's gate**, which asks for faster than stock per
ray, and the honest reading is below.

What moved it, measured in the same interleaved way:

| change | ratio after |
|---|---:|
| first working version | 0.77x |
| node loaded once per visit, children/indices/stack/mailbox through `Unsafe.Add`, split axis indexed instead of a ternary chain | 0.81x |
| the per-candidate coordinate-select also indexed instead of a ternary chain | **0.91x** |
| `[SkipLocalsInit]` on the packet traversal | no measurable change |

Where the rest is, and which parts are measured rather than guessed:

- **Measured**: the two changes above were worth 1.18x between them, and both
  were removing branches the managed compiler could not, which says the
  remaining gap is of the same kind — per-node and per-candidate overhead, not
  algorithm. The structures are stock's exactly (the node-for-node fact proves
  it), so there is no algorithmic difference left to find.
- **Not measured, and named as such**: which of the remaining overhead is
  bounds checks the JIT still emits, which is the `Vector128.Create` broadcast
  sequence against `_mm_set1_ps`, and which is the packet-splitting fallback.
  Separating them needs the generated code read instruction by instruction, or
  a hardware counter — and `perf` on this box refuses at
  `perf_event_paranoid`, so instructions-per-ray, which would settle it
  immediately, could not be taken.
- **Arithmetic, not a measurement**: this scene's rays have independent random
  directions, so the chance that four consecutive rays share a direction sign
  octant is 1/512. Almost every packet therefore takes stock's
  mismatched-sign path and becomes two to four separate traversals. That is
  true of BOTH sides equally so it does not explain the ratio, but it does mean
  this measurement is mostly of the fallback rather than of the 4-wide packet
  path, and a coherent ray set would measure something different. vrad's real
  shadow rays (sample to one light) are coherent; its sky and ambient fans are
  not.
