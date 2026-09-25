# Stock vrad's shadow-caster set, and the gate that replaced it

`stock-lockdown-casters.txt` is stock vrad's own `g_RtEnv`, reduced to what a
gate can check, for four command lines on one map.

## The printed triangle count does not measure the caster set

The gate's claim was that stock prints `Total triangle count:` and that a
real map gives an exact number to match — 19,216 for `dm_lockdown`. That line
is the tail of the reference's `PrintBSPFileSizes`:

```c
for ( int i = 0; i < numfaces; i++ )
	triangleCount += dfaces[i].numedges - 2;
Msg("Total triangle count: %d\n", triangleCount );
```

It is a BSP-lump statistic over the LDR `dfaces` array — all models, sky and
nodraw faces included, every displaced face counted as **2** rather than as its
`2^p · 2^p · 2` tessellation, static props not counted at all. Measured both
ways on this map:

| run | `Total triangle count:` | accel build | caster triangles |
|---|---:|---:|---:|
| `-fast` | 19216 | 0.41 s | 42,933 |
| `-fast -StaticPropPolys` | **19216** | **1.27 s** | **118,211** |

`-StaticPropPolys` triples the acceleration-structure build and adds 75,278
caster triangles, and that number does not move. **Stock prints its caster count
nowhere.** Grepping `OptimizedTriangleList` through the reference's tool
sources finds one dump and no count.

## What replaces it: `-dumptrace`

The reference parses `-dumptrace` into `g_bDumpRtEnv`, and then
calls `WriteRTEnv("trace.txt")` — **after** all three add calls
and **before** `SetupAccelerationStructure()`. That placement is forced:
`ChangeIntoIntersectionFormat` overwrites the vertex
union with plane and edge equations, so after the KD build there are no
vertices left to dump.

`WriteRTEnv` writes every triangle of
`OptimizedTriangleList` in order, each as a three-point winding coloured by its
id bits: green `TRACE_ID_OPAQUE`, blue `TRACE_ID_SKY`, red
`TRACE_ID_STATICPROP`.

Colour alone does not separate brushes from displacements — both are
`TRACE_ID_OPAQUE`. **Position does.** vrad adds in one fixed order
— brush entities, world brushes, sky faces,
displacements, static props. So the colour *runs* are the sources, and this
map's four runs are world brushes, sky, displacements, props. (It has no
`vrad_brush_cast_shadows` entity, so the brush-entity run is absent rather than
empty — a map that had one would show five runs and the first two would both be
green.)

The file lands in the mod's `default_write_path` — `game/mod_sharp/trace.txt`
for `make toolgame`'s gameinfo — not beside the map and not in the tool's
working directory. Forty minutes went into finding that.

## The numbers

| source | base | `-StaticPropPolys` |
|---|---:|---:|
| world brushes | 23,549 | 23,549 |
| sky faces | 512 | 512 |
| displacements | 1,568 | 1,568 |
| static props | 17,304 | **92,582** |
| total | 42,933 | 118,211 |

**`-textureshadows` does not change the caster set at all.** `trace-base.txt`
and `trace-ts.txt` have the same sha256, and so do `trace-spp.txt` and
`trace-sppts.txt`. The original gate asked for both switches to be gated as if they changed the
caster set; one of them does not. What it changes is `FCACHETRI_TRANSPARENT` and
a material index on render-path prop triangles,
which `WriteRTEnv` does not print — so the
fixture holds all four runs anyway, and the two pairs being identical is itself
a fact.

## Getting stock to read this repo's golden map

`dm_lockdown.bsp` is BSP v19 carrying a **version-5** `sprp` game lump;
stock vrad demands version 10 (the game-lump header's version constant) and refuses with
`Cannot load the static props... Re-vbsp the map.` There is no `.vmf` in the
tree.

An earlier lane worked around this by replacing `sprp` with an empty,
well-formed v10 lump — 12 bytes — which is sound for a stage that does not read
props. **This one does**, so the workaround would have gated the prop path
against a map with no props and passed. This fixture is taken against an
**upgraded** lump instead: all 261 props re-serialised into the v10 struct using
the same field mapping the reference's upgrade path uses for the v5 → v10 path
(`forcedFadeScale` from v5's own field, dx levels and lightmap resolution zero,
`STATIC_PROP_NO_PER_TEXEL_LIGHTING` ORed in). Origin, angles, model index, solid
type and `STATIC_PROP_NO_SHADOW` all carry across untouched, which is what the
caster set is made of.

Stock accepts it: all four runs `rc=0`, zero `Material not found!:`, zero
`Error loading studio model`. The committed `dm_lockdown.bsp` was not modified —
its sha256 is in the fixture header, and the scratch copy is what vrad read.

## What this port produces against it

| source | stock | this port | area agreement |
|---|---:|---:|---:|
| world brushes | 23,549 | 23,545 | **2.63 ppm** |
| sky faces | 512 | 512 | **0.34 ppm** |
| displacements | 1,568 | 1,568 | **7.40 ppm** |
| static props, `-StaticPropPolys` | 92,582 | **92,582** | **36.61 ppm** |
| static props, default | 17,304 | 2,724 | — (no vphysics) |

Sky, displacements and the render-mesh prop path match stock's count exactly,
along with the bounding box, the coordinate sums, the total area and the first
and last 32 triangles in order.

The world brushes are four short, and four is the NET rather than the churn —
53 triangles are in stock's run and not here, 49 are here and not in stock's,
and 44 more are the same triangle printed differently in the last of stock's two
decimals. The cause is not in the brush path: stock's `BaseWindingForPlane`
normalises with `VectorNormalize` (`rsqrtss` plus one Newton-Raphson step)
where `WindingArena` uses an exact
per-component divide, the base winding is scaled by `MAX_COORD_INTEGER * 4`
before clipping, and the clip epsilon is exactly zero
— so a few ULPs arrive at the clipped corner as a hundredth of
a unit and flip corners across the plane in both directions. The differing
triangles are slivers with median area zero and the two totals differ by 2.6
parts per million. `Vec3.NormaliseLikeStock` exists and would close it, at the
cost of I4's cross-machine determinism, because `rsqrtss` is
implementation-defined — a design fork for whoever owns `WindingArena`, not a
bug in this lane.

## The fixture's shape

Per run, per source: the count, the bounding box, the sum of every vertex
coordinate, the total surface area, and the first and last 32 triangles
verbatim.

* **Count** catches a missed or duplicated emission.
* **Bounds** catch a missing transform — a prop set that forgot the per-prop
  origin collapses onto the models' own hull bounds around the origin, which no
  map's bounds resemble.
* **Sums** catch a permutation that count and bounds both survive. They are
  compared with a derived tolerance, not a chosen one: stock prints `%5.2f`, so
  each of a run's `3 × Count` coordinates was rounded by at most 0.005 before
  being summed, and `StockCasterSourceRun.SumTolerance` is exactly that.
* **Area** is the physically meaningful total — what a shadow is actually made
  of — and it is the only aggregate that survives a few triangles moving
  between the two runs, which is why the world brushes are gated on it.
* **Head and tail** pin the ORDER, which is not cosmetic: a KD-tree hit reports
  a triangle *index*, and every world brush triangle on a map shares one
  identity, so position is the only thing that names a triangle. The head is
  anchored to the start of the run and the tail to its end, so that a run which
  is a few triangles short still answers "do these begin and end the same"
  rather than reporting a mismatch at every position after the first loss.

## What `-StaticPropPolys` costs, measured

Release build, cold, on this map. The claim was that the switch costs serial
time rather than ray time; this says where the serial time is.

| | default | `-StaticPropPolys` |
|---|---:|---:|
| load: brushes | 30.4 ms | 22.4 ms |
| load: displacements | 4.0 ms | 0.2 ms |
| load: static props | 20.6 ms | 27.2 ms |
| **load total** | **55.0 ms** | **49.7 ms** |
| **KD build** | **204.3 ms** | **976.6 ms** |
| KD nodes | 49,669 | 175,147 |
| stock's KD build | 0.41 s | 1.27 s |

**The load barely notices it** — seven milliseconds more in the prop pass, and
the totals are within run-to-run noise of each other because the three other
sources are byte-identical between the two runs. **The whole cost is the
acceleration structure**: 204 ms to 977 ms, a factor of 4.8, for 4.2× the
triangles. That is precisely the one option a faster tracer cannot
help, localised.

Against stock, this port's KD build is **2.0× faster** at 28,349 triangles and
**1.3× faster** at 118,207 — so it is ahead on both and its scaling is worse
(4.8× against stock's 3.1×), which is where a 4p pass should look.

**The Debug JIT is worth 37× here, not the 2.4× another lane recorded for
tracing code.** The same two builds under `dotnet test` without `-c Release`
report 7,632 ms and 37,876 ms. MinOpts does not inline `Vector128` intrinsics,
and the KD builder is full of them. Any number taken from this fact without
`-c Release` is off by more than an order of magnitude.

To take them again:

```
MSBUILDDISABLENODEREUSE=1 dotnet test src/sourcesharp/managed/SourceSharp.Tests/SourceSharp.Tests.csproj \
    -c Release -m:1 --filter "FullyQualifiedName~Rad.ShadowCasterLoaderStockParityTests" \
    --logger "console;verbosity=detailed"
```

`SS_CASTER_DUMP=<path>` on that run also writes this port's own caster dump in
`WriteRTEnv`'s format, for a position-by-position diff against stock's
`trace.txt`. `Fixtures/tools/churn.py`, `churn2.py` and `churnarea.py` take that
diff apart: which triangles differ, how much of the difference is print
resolution rather than geometry, and what the differing triangles are worth in
area.

## Reproducing

```
# 1. upgrade the sprp lump (this repo's map is v5, stock wants v10)
python3 <scratch>/upgrade_sprp.py game/mod_sharp/maps/dm_lockdown.bsp <scratch>/dm_lockdown_v10.bsp

# 2. one stock run per option set; trace.txt lands in game/mod_sharp/
make toolgame
cp <scratch>/dm_lockdown_v10.bsp maps/p4b_lockdown.bsp
WINEPREFIX=~/.local/share/sourcesharp-wineprefix WINEDEBUG=-all SteamAppUser=sourcesharp \
  "$HOME/.steam/steam/steamapps/common/Proton - Experimental/files/bin/wine" \
  "$HOME/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer/bin/vrad.exe" \
  -fast -dumptrace [-StaticPropPolys] [-textureshadows] \
  -game "Z:$PWD/tools/mapgame" "Z:$PWD/maps/p4b_lockdown.bsp"
mv game/mod_sharp/trace.txt <scratch>/trace-<tag>.txt

# 3. reduce the four dumps to the committed fixture
python3 <scratch>/make_fixture.py game/mod_sharp/maps/dm_lockdown.bsp \
    <scratch>/trace-{base,spp,ts,sppts}.txt \
    > MapTools/Rad/Fixtures/stock-lockdown-casters.txt
```

`upgrade_sprp.py`, `stockdump.sh`, `parse_trace.py` and `make_fixture.py`
live in `tools/` beside this README.

## What this fixture cannot say

The **17,304** default-path prop triangles come out of vphysics: stock hands the
`.phy` solids to `IPhysicsCollision::VCollideLoad` and reads triangles back
through `ICollisionQuery`. There is no
managed triangle count for a `.phy` in this tree and there cannot be one until
the vphysics binding lands. The number is recorded here so that the day
it does land, it has something to be wrong against.
