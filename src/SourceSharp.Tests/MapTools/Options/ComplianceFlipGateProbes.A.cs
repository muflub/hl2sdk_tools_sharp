using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Tracing;

using SourceSharp.Tests.MapTools.Bsp;
using SourceSharp.Tests.MapTools.Bsp.Collision;
using SourceSharp.Tests.MapTools.Bsp.Csg;
using SourceSharp.Tests.MapTools.Bsp.Portals;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The vbsp tree/collision/tracing probes: each fingerprint is the call
/// sequence the quirk's existing per-quirk effect fact already makes, turned
/// into a function of the policy.
/// </summary>
internal static partial class ComplianceFlipGateProbes
{
    /// <summary>The recipe room of <c>VbspCompileTests</c>: a sealed 256-unit
    /// room of six 16-thick slabs with one spawn point.</summary>
    /// <param name="water">Whether a 64-high pool sits on the floor.</param>
    /// <returns>The document.</returns>
    private static VmfDocument RoomA(bool water = false)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        void Slab(string material, (float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(material, mins, maxs, id++));

        Slab(UnitMap.Plain, (-16, -16, -16), (272, 272, 0));      // floor
        Slab(UnitMap.Plain, (-16, -16, 256), (272, 272, 272));    // ceiling
        Slab(UnitMap.Plain, (-16, -16, 0), (0, 272, 256));        // -x
        Slab(UnitMap.Plain, (256, -16, 0), (272, 272, 256));      // +x
        Slab(UnitMap.Plain, (0, -16, 0), (256, 0, 256));          // -y
        Slab(UnitMap.Plain, (0, 256, 0), (256, 272, 256));        // +y

        if (water)
        {
            world.Children.Add(UnitMap.Box(UnitMap.Water, (0, 0, 0), (256, 256, 64), id++));
        }

        document.Chunks.Add(world);

        VmfChunk spawn = new(MapFileLoader.EntityChunk);
        spawn.AddKey("id", "101");
        spawn.AddKey("classname", "info_player_start");
        spawn.AddKey("origin", "128 128 128");
        document.Chunks.Add(spawn);

        return document;
    }

    private static async Task<VbspResult> CompileAsyncA(VmfDocument document, ComplianceOptions compliance)
    {
        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context);
    }

    private static async Task<VbspResult> CompileAsyncA(
        VmfDocument document, ComplianceOptions compliance, ICollisionCooker? cooker, int threads)
    {
        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        context.CollisionCooker = cooker;
        context.Parallelism = new CompileParallelism { MaxDegree = threads };
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context);
    }

    private static uint BitsA(float value) => BitConverter.SingleToUInt32Bits(value);

    private static byte[] PlaneBytesA(CollisionPlane plane) =>
        Fp.Bytes(
            new uint[]
            {
                BitsA(plane.Normal.X),
                BitsA(plane.Normal.Y),
                BitsA(plane.Normal.Z),
                BitsA(plane.Dist),
            });

    private static async Task<(PhysCollisionResult Result, FakeCollisionCooker Cooker)> WaterEmitAsyncA(
        ComplianceOptions compliance, float brushTop, int surfaceTexInfo)
    {
        // The recipe of PhysCollisionEmitterTests.WaterAsync (:427): a water
        // box to `brushTop`, its volume's surface at z = 20.
        CollisionFixture f = new();
        int metal = f.TexInfoFor("metal");
        f.Box(0, new Vec3(-32, -32, -32), new Vec3(32, 32, brushTop), CollisionContents.Water, metal);
        f.Water.Add(new WaterModel(
            0, CollisionContents.Water, true, new Vec3(0, 0, 1), 20f, 0, [0],
            surfaceTexInfo < 0 ? -1 : metal));
        FakeCollisionCooker cooker = new();
        PhysCollisionResult result = await PhysCollisionEmitter.EmitAsync(
            f.Build() with { Compliance = compliance }, cooker);
        return (result, cooker);
    }

    private static string PathOfA(LeakReport? report) =>
        report is null
            ? "no report"
            : string.Join(";", report.Path.Select(p => string.Create(
                CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}")));

    private static LeakReport? AreaportalLeakA(ComplianceOptions compliance)
    {
        // The recipe of ComplianceQuirkEffectTests.AreaportalLeakA (:208).
        PortalFixture f = PortalFixture.LeakingAreaportal(compliance);
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        EntityFlood.FillOutside(f.Tree.HeadNode);
        AreaFlood areas = new(f.Entities);
        areas.FloodAreas(f.Tree, f.Arena);
        return areas.AreaportalLeak;
    }

    private static LeakReport? UnnudgedOriginLeakA(ComplianceOptions compliance)
    {
        // The recipe of LeakTraceTests.Leaked (:179).
        PortalFixture f = PortalFixture.SealedRoom(compliance);
        f.BeyondXHigh.Contents = 0;
        f.Add("light", new Vec3(48f, 0f, 0f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        return LeakTrace.Trace(f.Tree, f.Arena, f.Entities);
    }

    private static IEnumerable<Vec3> SlantedNormalsA()
    {
        for (int i = 1; i <= 16; i++)
        {
            (Vec3 n, _) = new Vec3(i, 3f, 17f - i).Normalise();
            yield return n;
        }
    }

    private static byte[] BaseWindingRunA(ComplianceOptions compliance)
    {
        // The recipe of ComplianceQuirkEffectTests.BaseWindingBits (:247).
        List<object> parts = [];
        foreach (Vec3 normal in SlantedNormalsA())
        {
            WindingArena arena = new() { Compliance = compliance };
            Winding w = arena.BaseWindingForPlane(normal, 64f);
            parts.Add(Fp.Bytes(arena.Points(w)));
        }

        return Fp.Join([.. parts]);
    }

    private static async Task<byte[]> OctahedronPlaneBytesAsyncA(ComplianceOptions compliance)
    {
        // The recipe of ComplianceQuirkEffectTests.OctahedronPlanesAsync (:263).
        VmfDocument document = EdgeBevelShapes.Document();
        foreach (VmfChunk chunk in DescendantsA(document.Chunks))
        {
            foreach (VmfKey key in chunk.Keys.Where(k => k.Name == "material"))
            {
                key.Value = UnitMap.Plain;
            }
        }

        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        MapFile map = await MapFileLoader.LoadAsync(context, document);

        List<object> parts = [];
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane p = map.Planes[i];
            parts.Add(Fp.Bytes(new uint[] { BitsA(p.Normal.X), BitsA(p.Normal.Y), BitsA(p.Normal.Z), BitsA(p.Dist) }));
            parts.Add(Fp.Int((int)map.Planes.TypeOf(i)));
        }

        return Fp.Join([.. parts]);
    }

    private static IEnumerable<VmfChunk> DescendantsA(IEnumerable<VmfChunk> chunks)
    {
        foreach (VmfChunk chunk in chunks)
        {
            yield return chunk;
            foreach (VmfChunk child in DescendantsA(chunk.Chunks))
            {
                yield return child;
            }
        }
    }

    private static async Task<byte[]> TinySquareDecisionAsyncA(ComplianceOptions compliance)
    {
        // The recipe of ComplianceQuirkEffectTests.ThresholdSquareAsync (:305).
        VbspContext compile = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        MapFile map = await MapFileLoader.LoadAsync(
            compile, UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64)));

        BspBuildContext context = new(compile, map);
        Winding square = compile.Windings.Create(
        [
            new Vec3(0f, 0f, 0f),
            new Vec3(0.2f, 0f, 0f),
            new Vec3(0.2f, 0.2f, 0f),
            new Vec3(0f, 0.2f, 0f),
        ]);

        // The decision plus the winding it was taken on.
        return Fp.Join(
            Fp.Bool(BrushGeometry.WindingIsTiny(context, square)),
            Fp.Bytes(compile.Windings.Points(square)));
    }

    private static byte[] LeakReportBytesA(LeakReport? report) =>
        Fp.Join(
            Fp.Int(report?.EntityId ?? -1),
            Fp.Text(report?.ClassName),
            Fp.Text(PathOfA(report)));

    private static byte[] WaterLeafOrderBytesA(ComplianceOptions compliance)
    {
        // The recipe of WaterVolumeBuilderTests.TwoEqualPools (:141): two
        // equal-surface pools, discovered left then right.
        EqualPoolTreeA t = new();
        BspNode air = t.Leaf(0, 0, 64);
        BspNode left = t.Leaf(CollisionContents.Water, -48, 0, x: -500);
        BspNode right = t.Leaf(CollisionContents.Water, -48, 0, x: 500);
        t.Portal(left, air, 0);
        t.Portal(right, air, 0);
        WaterVolumeBuilder b = t.Builder(compliance);
        b.EmitWaterVolumesForModel(0, t.Node(air, t.Node(left, right)));

        // The two-equal-pool IsLowerLeaf arguments (both surfaces, equal
        // normal-z, new farther: the dead-branch row of the effect theory)
        // plus the leaf order that decision produced.
        List<object> parts =
        [
            Fp.Bool(WaterVolumeBuilder.IsLowerLeaf(true, 1f, 5f, true, 1f, 0f, compliance)),
        ];
        foreach (WaterModel model in b.WaterModels)
        {
            foreach (int leaf in model.Leaves)
            {
                parts.Add(Fp.Int(leaf));
            }
        }

        return Fp.Join([.. parts]);
    }

    /// <summary>The hand-built water tree of
    /// <c>WaterVolumeBuilderTests.Tree</c> (private there; copied per §11a).</summary>
    private sealed class EqualPoolTreeA
    {
        private readonly Dictionary<IBspNode, int> _disk = [];
        private int _next;

        public WindingArena Arena { get; } = new();

        public List<Plane> Planes { get; } = [new Plane(new Vec3(0, 0, 1), 0f), new Plane(new Vec3(0, 0, -1), 0f)];

        public List<int> Depths { get; } = [];

        public BspNode Leaf(int contents, float minZ, float maxZ, float x = 0)
        {
            BspNode leaf = new(_next++)
            {
                Contents = contents,
                Mins = new Vec3(x - 64, -64, minZ),
                Maxs = new Vec3(x + 64, 64, maxZ),
            };
            _disk[leaf] = _disk.Count;
            return leaf;
        }

        public BspNode Node(IBspNode front, IBspNode back) =>
            new(_next++) { PlaneNumber = 0, Front = front, Back = back };

        public void Portal(IBspNode front, IBspNode back, float z, bool visible = true, int texInfo = 9)
        {
            Portal p = new(_next++)
            {
                Winding = Arena.Create([new Vec3(-10, -10, z), new Vec3(10, -10, z), new Vec3(10, 10, z), new Vec3(-10, 10, z)]),
                Side = visible ? new MapBrushSide { PlaneNumber = 0, TexInfo = texInfo } : null,
            };
            TreePortals.AddPortalToNodes(p, front, back);
        }

        public WaterVolumeBuilder Builder(ComplianceOptions? compliance = null) =>
            new(Planes, Arena, n => _disk.TryGetValue(n, out int d) ? d : -1, (_, _) => 77, new Sink(Depths), compliance);

        private sealed class Sink(List<int> depths) : IWaterTexInfoSink
        {
            public int FindOrCreateWaterTexInfo(int baseTexInfo, float depth)
            {
                depths.Add((int)depth);
                return 100 + baseTexInfo;
            }
        }
    }

    private static KdRayTracer ZeroWallA(ComplianceOptions compliance) =>
        // The recipe of KdRayTracerZeroDirectionTests.Wall (:36).
        KdRayTracer.Build(
        [
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 0, 100), 0),
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(100, 0, 100), new Vec3(0, 0, 100), 0),
        ],
        compliance);

    private static byte[] ZeroDirectionHitBytesA(ComplianceOptions compliance)
    {
        // The recipe of KdRayTracerZeroDirectionTests.Closest (:48): the +0
        // ray one ulp inside x = 100, four copies so no packet-mate keeps the
        // packet alive.
        float justInside = MathF.BitDecrement(100f);
        KdRayTracer tracer = ZeroWallA(compliance);
        Ray ray = new(justInside, -1000f, 50f, 0f, 1f, 0f, 2000f);
        HitId[] hits = new HitId[4];
        tracer.TraceClosest([ray, ray, ray, ray], hits, RayTraceOptions.StockExact);
        return Fp.Join(Fp.Int(hits[0].Surface), Fp.Real(hits[0].Fraction));
    }

    /// <summary>The vbsp tree/collision/tracing family.</summary>
    /// <returns>The twelve probes.</returns>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesA() => new()
    {
        [StockQuirk.BaseWindingNormalise] = new FlipGateProbe(
            StockQuirk.BaseWindingNormalise,
            "winding arena (ComplianceQuirkEffectTests.BaseWindingForPlane)",
            ["winding point bytes"],
            WholeStage: false)
        {
            Fingerprint = policy => Task.FromResult(BaseWindingRunA(policy)),
        },

        [StockQuirk.EdgeBevelNormalise] = new FlipGateProbe(
            StockQuirk.EdgeBevelNormalise,
            "octahedron planes (ComplianceQuirkEffectTests.TheOctahedronsBevelPlanesMove)",
            ["loaded plane list bytes"],
            WholeStage: false)
        {
            Fingerprint = OctahedronPlaneBytesAsyncA,
        },

        [StockQuirk.AreaportalLeakWalk] = new FlipGateProbe(
            StockQuirk.AreaportalLeakWalk,
            "leaking areaportal (ComplianceQuirkEffectTests.TheAreaportalLeakLineLeavesTheRoomUnderStock)",
            ["areaportal leak path bytes"],
            WholeStage: false)
        {
            Fingerprint = policy => Task.FromResult(LeakReportBytesA(AreaportalLeakA(policy))),
        },

        [StockQuirk.WindingIsTinyEdgePromotion] = new FlipGateProbe(
            StockQuirk.WindingIsTinyEdgePromotion,
            "threshold square (ComplianceQuirkEffectTests.BrushGeometrysWindingIsTiny)",
            ["tiny-edge decision byte", "square winding bytes"],
            WholeStage: false)
        {
            Fingerprint = TinySquareDecisionAsyncA,
        },

        [StockQuirk.LeakFileUnnudgedOrigin] = new FlipGateProbe(
            StockQuirk.LeakFileUnnudgedOrigin,
            "leaked sealed room (LeakTraceTests.TheLastPointIsTheEntitysUnnudgedOriginUnderStock)",
            ["leak report id/classname/path bytes"],
            WholeStage: false)
        {
            Fingerprint = policy => Task.FromResult(LeakReportBytesA(UnnudgedOriginLeakA(policy))),
        },

        [StockQuirk.WaterLeafSortTie] = new FlipGateProbe(
            StockQuirk.WaterLeafSortTie,
            "two equal pools (WaterVolumeBuilderTests.StockVisitsEqualSurfaceLeavesInReverseTreeOrder)",
            ["water model leaf order bytes"],
            WholeStage: false)
        {
            Fingerprint = policy => Task.FromResult(WaterLeafOrderBytesA(policy)),
        },

        [StockQuirk.FluidSurfacePropIgnored] = new FlipGateProbe(
            StockQuirk.FluidSurfacePropIgnored,
            "metal-topped pool (PhysCollisionEmitterTests.StockFluidsAreAlwaysWater)",
            ["world keydata text"],
            WholeStage: false)
        {
            Fingerprint = async policy =>
            {
                (PhysCollisionResult r, _) = await WaterEmitAsyncA(policy, 20f, surfaceTexInfo: 0);
                return Fp.Text(r.Models[0].KeyText);
            },
        },

        [StockQuirk.ShellMassSentinelArea] = new FlipGateProbe(
            StockQuirk.ShellMassSentinelArea,
            "shell mass (PhysCollisionEmitterTests.StockShellMassCountsTheSentinelArea)",
            ["material text", "mass bits"],
            WholeStage: false)
        {
            Fingerprint = policy =>
            {
                // The recipe of PhysCollisionEmitterTests's shell-mass pair
                // (:237/:248): one shell face of area 100 over a huge volume.
                CollisionFixture f = new();
                (string material, float mass) = PhysCollisionEmitter.MassAndMaterial(
                    [f.Props.GetSurfaceIndex("shell")], [100f], null, 1e9f, f.Props, policy);
                return Task.FromResult(Fp.Join(Fp.Text(material), Fp.Real(mass)));
            },
        },

        [StockQuirk.WaterBrushNotClippedAtSurface] = new FlipGateProbe(
            StockQuirk.WaterBrushNotClippedAtSurface,
            "pool brush over its surface (PhysCollisionEmitterTests.StockAddsAWaterBrushCrossingTheSurfaceWhole)",
            ["cooked convex plane bytes"],
            WholeStage: false)
        {
            Fingerprint = async policy =>
            {
                (_, FakeCollisionCooker cooker) = await WaterEmitAsyncA(policy, 32f, surfaceTexInfo: -1);
                CollisionPlane[] planes = cooker.Session.PlaneCalls[^1];
                return Fp.Join([Fp.Int(planes.Length), .. planes.Select(PlaneBytesA)]);
            },
        },

        [StockQuirk.NodeAreaWrittenBeforeSet] = new FlipGateProbe(
            StockQuirk.NodeAreaWrittenBeforeSet,
            "sealed room vbsp (VbspCompileTests.UnderStockEveryNodeAreaIsZero)",
            [nameof(BspLump.Nodes)],
            WholeStage: true)
        {
            Fingerprint = async policy =>
            {
                VbspResult result = await CompileAsyncA(RoomA(), policy);
                return Fp.Bytes(BspStructView.As<DNode>(result.Bsp![BspLump.Nodes]));
            },
            ThreadsFingerprint = async (policy, threads) =>
            {
                VbspResult result = await CompileAsyncA(RoomA(), policy, cooker: null, threads);
                return Fp.Bytes(BspStructView.As<DNode>(result.Bsp![BspLump.Nodes]));
            },
            I2Diff = async (a, b) =>
            {
                VbspResult resultA = await CompileAsyncA(RoomA(), a);
                VbspResult resultB = await CompileAsyncA(RoomA(), b);
                return (resultA.Bsp!, resultB.Bsp!);
            },
        },

        [StockQuirk.FogVolumeLoopOverNoFaces] = new FlipGateProbe(
            StockQuirk.FogVolumeLoopOverNoFaces,
            "water room faces (VbspCollisionWiringTests.UnderCorrectTheWaterSurfaceFaceGetsItsFogVolume)",
            ["face lump bytes"],
            WholeStage: false)
        {
            Fingerprint = async policy =>
            {
                VbspResult result = await CompileAsyncA(RoomA(water: true), policy, new FakeCollisionCooker(), threads: 1);
                return Fp.Bytes(BspStructView.As<DFace>(result.Bsp![BspLump.Faces]));
            },
        },

        [StockQuirk.KdZeroDirectionReachCut] = new FlipGateProbe(
            StockQuirk.KdZeroDirectionReachCut,
            "zero-direction wall (KdRayTracerZeroDirectionTests.StockCutsAPositiveZeroRayShortOfTheWall)",
            ["hit surface/fraction bytes"],
            WholeStage: false)
        {
            Fingerprint = policy => Task.FromResult(ZeroDirectionHitBytesA(policy)),
        },
    };
}
