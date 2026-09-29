//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// <see cref="VvisOptions.SeparatorPath"/>: how <see cref="VisSeparatorPath.Auto"/>
/// is resolved against a CPU, and that the two separator paths write the same
/// visibility on every map these facts can reach.
/// </summary>
/// <remarks>
/// <para>
/// The rule is tested on stubbed CPUs (<see cref="CpuCapabilities"/> is a
/// value, and <see cref="VisContext"/> takes one), so a Zen 5, a Zen 4 and an
/// Intel part are each checked on whatever runner CI happens to use.
/// </para>
/// <para>
/// The equivalence facts force each path explicitly, so both run on every
/// runner: where 512-bit vectors are not accelerated the Vector512 path runs
/// in the runtime's software fallback, exactly. They compare the whole
/// output -- the visibility lump, every PVS and PAS row, and at one thread the
/// flow's work counters, which pin that the two walks visited the same
/// chains -- on random portal sets, the hand-built vvis fixtures, and the
/// sandbox map; a map named by <see cref="EquivalenceBspVariable"/> (2fort,
/// say) runs the same comparison on demand.
/// </para>
/// </remarks>
public class VisSeparatorPathTests
{
    /// <summary>Names a post-vbsp <c>.bsp</c> (with its <c>.prt</c> beside it) to compare both paths on.</summary>
    public const string EquivalenceBspVariable = "SS_VVIS_SEPARATOR_BSP";

    private static readonly CpuCapabilities Zen5 = new(CpuCapabilities.Amd, 0x1A, Vector512Accelerated: true);
    private static readonly CpuCapabilities Zen4 = new(CpuCapabilities.Amd, 0x19, Vector512Accelerated: true);
    private static readonly CpuCapabilities IceLake = new(CpuCapabilities.Intel, 6, Vector512Accelerated: true);

    // ---------------------------------------------------------------- the Auto rule

    [Theory]
    [InlineData(CpuCapabilities.Amd, 0x1A, true, VisSeparatorPath.Vector512)]
    [InlineData(CpuCapabilities.Amd, 0x1B, true, VisSeparatorPath.Vector512)]
    [InlineData(CpuCapabilities.Amd, 0x1A, false, VisSeparatorPath.Vector256)]
    [InlineData(CpuCapabilities.Amd, 0x19, true, VisSeparatorPath.Vector256)]
    [InlineData(CpuCapabilities.Amd, 0x17, false, VisSeparatorPath.Vector256)]
    [InlineData(CpuCapabilities.Intel, 6, true, VisSeparatorPath.Vector256)]
    [InlineData(CpuCapabilities.Intel, 6, false, VisSeparatorPath.Vector256)]
    [InlineData(CpuCapabilities.Intel, 0x1A, true, VisSeparatorPath.Vector256)]
    [InlineData("", 0, true, VisSeparatorPath.Vector256)]
    [InlineData("", 0, false, VisSeparatorPath.Vector256)]
    public void AutoTakesVector512OnlyOnAnAcceleratedAmdFamily1AhOrLater(
        string vendor, int family, bool accelerated, VisSeparatorPath expected)
    {
        // AMD fam 1Ah (Zen 5) -> 512; 19h (Zen 4, double-pumped) -> 256;
        // Intel, even with AVX-512 -> 256; no acceleration -> 256.
        CpuCapabilities cpu = new(vendor, family, accelerated);
        Assert.Equal(expected, VisSeparatorPaths.Resolve(VisSeparatorPath.Auto, cpu));
    }

    [Theory]
    [InlineData(VisSeparatorPath.Vector256)]
    [InlineData(VisSeparatorPath.Vector512)]
    public void AForcedPathIsKeptOnEveryCpu(VisSeparatorPath forced)
    {
        foreach (CpuCapabilities cpu in new[] { Zen5, Zen4, IceLake, new CpuCapabilities(string.Empty, 0, false) })
        {
            Assert.Equal(forced, VisSeparatorPaths.Resolve(forced, cpu));
        }
    }

    [Fact]
    public void ResolvingRefusesAnUndefinedPathOrNoCpu()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VisSeparatorPaths.Resolve((VisSeparatorPath)7, Zen5));
        Assert.Throws<ArgumentNullException>(() => VisSeparatorPaths.Resolve(VisSeparatorPath.Auto, null!));
    }

    [Theory]
    [InlineData(0x00B40F00, 0x1A)] // Zen 5: base F + extended B
    [InlineData(0x00A60F12, 0x19)] // Zen 4: base F + extended A
    [InlineData(0x00800F82, 0x17)] // Zen+: base F + extended 8
    [InlineData(0x00000F00, 0x0F)] // base F, no extension
    [InlineData(0x000606A6, 6)]    // Ice Lake-SP: base 6, extension ignored
    [InlineData(0x00F00600, 6)]    // an extended family beside a base below F counts for nothing
    public void TheDisplayFamilyAddsTheExtensionOnlyToASaturatedBase(int signature, int family) =>
        Assert.Equal(family, CpuCapabilities.DisplayFamily(signature));

    [Fact]
    public void TheVendorIsReadFromEbxEdxEcxInThatOrder()
    {
        static int Word(string four) => BitConverter.ToInt32(System.Text.Encoding.ASCII.GetBytes(four));

        Assert.Equal(CpuCapabilities.Amd, CpuCapabilities.VendorFromRegisters(Word("Auth"), Word("enti"), Word("cAMD")));
        Assert.Equal(CpuCapabilities.Intel, CpuCapabilities.VendorFromRegisters(Word("Genu"), Word("ineI"), Word("ntel")));
        Assert.True(new CpuCapabilities(CpuCapabilities.Amd, 0x1A, true).IsAmd);
        Assert.False(new CpuCapabilities(CpuCapabilities.Intel, 6, true).IsAmd);
    }

    [Fact]
    public void DetectingDescribesThisCpu()
    {
        CpuCapabilities cpu = CpuCapabilities.Detect();
        Assert.Equal(Vector512.IsHardwareAccelerated, cpu.Vector512Accelerated);
        if (X86Base.IsSupported)
        {
            Assert.Equal(12, cpu.Vendor.Length);
            Assert.True(cpu.Family > 0);
        }
        else
        {
            Assert.Equal(string.Empty, cpu.Vendor);
            Assert.Equal(0, cpu.Family);
        }

        // And the flow's own resolution agrees with the rule on it.
        Assert.Equal(
            VisSeparatorPaths.Resolve(VisSeparatorPath.Auto, cpu),
            Vvis.ResolveSeparators(new VisContext()));
    }

    // ---------------------------------------------------------------- the option

    [Fact]
    public void TheOptionDefaultsToAutoAndRefusesAnUndefinedPath()
    {
        Assert.Equal(VisSeparatorPath.Auto, VvisOptions.Default.SeparatorPath);
        Assert.Equal(VisSeparatorPath.Vector512, new VvisOptions { SeparatorPath = VisSeparatorPath.Vector512 }.SeparatorPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VvisOptions { SeparatorPath = (VisSeparatorPath)3 });
    }

    [Fact]
    public void AFlowRunsOnlyAResolvedPath()
    {
        (BspData _, PortalSet portals) = VvisTightenTests.Grid();
        VisPortalState state = new(portals.Count);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VisPortalFlow(portals, state, BitVectorPath.Auto, separators: VisSeparatorPath.Auto));
        _ = new VisPortalFlow(portals, state, BitVectorPath.Auto, separators: VisSeparatorPath.Vector512);
        _ = new VisPortalFlow(portals, state, BitVectorPath.Auto);
    }

    [Theory]
    [InlineData(VisSeparatorPath.Auto)]
    [InlineData(VisSeparatorPath.Vector512)]
    public void ThePathIsNotPartOfTheStageCacheKey(VisSeparatorPath path)
    {
        // It cannot change a byte, so a map vised on one path is served to a
        // compile asking for the other.
        (BspData map, _) = VvisTightenTests.Grid();
        byte[] portalText = VvisTightenTests.GridFile().ToBytes();
        Assert.Equal(
            VvisStageCache.InputDigest(portalText, map, VvisOptions.Default with { SeparatorPath = VisSeparatorPath.Vector256 }),
            VvisStageCache.InputDigest(portalText, map, VvisOptions.Default with { SeparatorPath = path }));
    }

    [Theory]
    [InlineData(VisSeparatorPath.Auto, "zen5", VisSeparatorPath.Vector512)]
    [InlineData(VisSeparatorPath.Auto, "zen4", VisSeparatorPath.Vector256)]
    [InlineData(VisSeparatorPath.Auto, "icelake", VisSeparatorPath.Vector256)]
    [InlineData(VisSeparatorPath.Vector512, "icelake", VisSeparatorPath.Vector512)]
    [InlineData(VisSeparatorPath.Vector256, "zen5", VisSeparatorPath.Vector256)]
    public async Task ACompileResolvesAutoAgainstTheCpuItIsGiven(VisSeparatorPath requested, string cpu, VisSeparatorPath ran)
    {
        (BspData map, PortalSet portals) = VvisTightenTests.Grid();
        VisResult result = await Vvis.ComputeAsync(
            map,
            portals,
            new VisContext
            {
                Options = VvisOptions.Default with { SeparatorPath = requested },
                Parallelism = new CompileParallelism { MaxDegree = 2 },
                Cpu = cpu switch { "zen5" => Zen5, "zen4" => Zen4, _ => IceLake },
            },
            CancellationToken.None);

        Assert.Equal(ran, result.SeparatorPath);
    }

    [Fact]
    public async Task TheSplitFlowAndATraceReportThePathTheyRan()
    {
        (BspData map, PortalSet portals) = VvisTightenTests.Grid();
        VisContext context = new()
        {
            Options = VvisOptions.Default with { SeparatorPath = VisSeparatorPath.Vector512 },
            Parallelism = new CompileParallelism { MaxDegree = 2 },
        };

        VisFlow flow = await Vvis.FlowAsync(portals, default, context, CancellationToken.None);
        Assert.Equal(VisSeparatorPath.Vector512, flow.SeparatorPath);
        VisResult finished = await Vvis.FinishAsync(map, flow, context, CancellationToken.None);
        Assert.Equal(VisSeparatorPath.Vector512, finished.SeparatorPath);

        (BspData traced, PortalSet tracePortals) = VvisTightenTests.Grid();
        VisResult trace = await Vvis.ComputeAsync(
            traced,
            tracePortals,
            context with { Options = context.Options with { Trace = (0, 99) } },
            CancellationToken.None);
        Assert.Equal(VisSeparatorPath.Vector512, trace.SeparatorPath);
    }

    // ---------------------------------------------------------------- equivalence

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task BothPathsWriteTheSameVisibilityOnRandomPortalSets(int seed)
    {
        // A random grid of rooms whose walls hold a random convex polygon
        // window each (three to nine sides) or none: sources and passes with
        // more than four edges, so a list takes two or three runs, and more
        // than four vertices, so the runs hold planes back across chunks.
        PortalFile file = RandomRooms(new Random(seed), out int clusters);
        await AssertSamePathsAsync(() => (VisFixture.Map(clusters), PortalSet.FromPortalFile(file)), $"random rooms {seed}");
    }

    [Fact]
    public async Task BothPathsWriteTheSameVisibilityOnTheWindowGrid() =>
        await AssertSamePathsAsync(VvisTightenTests.Grid, "window grid");

    [Fact]
    public async Task BothPathsWriteTheSameVisibilityOnTheCorridorAndTheUntightenedWalk()
    {
        static (BspData, PortalSet) Corridor()
        {
            PortalFile file = VisFixture.Portals(
                3,
                VisFixture.WindowAtX(0, 1, x: 0f, yMin: 0f, yMax: 16f),
                VisFixture.WindowAtX(1, 2, x: 64f, yMin: 0f, yMax: 16f));
            return (VisFixture.Map(3), PortalSet.FromPortalFile(file));
        }

        await AssertSamePathsAsync(Corridor, "corridor");
        await AssertSamePathsAsync(VvisTightenTests.Grid, "untightened grid", VvisOptions.Untightened);
    }

    [Fact]
    public async Task TwoCompilesOnDifferentPathsAtOnceWriteTheSameBytes()
    {
        // The service case: two compiles in one process, one on each path,
        // concurrently. Nothing is shared between them, so each writes what
        // it writes alone.
        PortalFile file = RandomRooms(new Random(17), out int clusters);
        Task<(BspData, VisResult)> narrow = RunAsync(
            (VisFixture.Map(clusters), PortalSet.FromPortalFile(file)), VisSeparatorPath.Vector256, 4, VvisOptions.Default);
        Task<(BspData, VisResult)> wide = RunAsync(
            (VisFixture.Map(clusters), PortalSet.FromPortalFile(file)), VisSeparatorPath.Vector512, 4, VvisOptions.Default);
        (BspData a, VisResult ra) = await narrow;
        (BspData b, VisResult rb) = await wide;
        AssertSameOutput(a, ra, b, rb, "concurrent", compareWork: false);
    }

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task BothPathsWriteTheSameVisibilityOnTheSandbox()
    {
        (BspData bsp, byte[] prt) = await CompileSandboxAsync();
        PortalFile portals = await PortalFile.ParseAsync(prt);
        await AssertSamePathsAsync(() => (Clone(bsp), PortalSet.FromPortalFile(portals)), "ss_sandbox", degrees: [4]);
    }

    [EquivalenceBspFact]
    public async Task BothPathsWriteTheSameVisibilityOnTheNamedMap()
    {
        // 2fort takes minutes of CPU per path, too long for every run of the
        // suite; point the variable at its post-vbsp .bsp to run it.
        string bspPath = Environment.GetEnvironmentVariable(EquivalenceBspVariable)!;
        BspData bsp;
        await using (FileStream stream = File.OpenRead(bspPath))
        {
            bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        }

        PortalFile portals;
        await using (FileStream stream = File.OpenRead(Path.ChangeExtension(bspPath, ".prt")))
        {
            portals = await PortalFile.ReadAsync(stream, CancellationToken.None);
        }

        await AssertSamePathsAsync(
            () => (Clone(bsp), PortalSet.FromPortalFile(portals)), Path.GetFileName(bspPath), degrees: [Environment.ProcessorCount]);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task AssertSamePathsAsync(
        Func<(BspData Map, PortalSet Portals)> input,
        string what,
        VvisOptions? options = null,
        int[]? degrees = null)
    {
        foreach (int degree in degrees ?? [1, 4])
        {
            (BspData a, VisResult ra) = await RunAsync(input(), VisSeparatorPath.Vector256, degree, options ?? VvisOptions.Default);
            (BspData b, VisResult rb) = await RunAsync(input(), VisSeparatorPath.Vector512, degree, options ?? VvisOptions.Default);
            Assert.Equal(VisSeparatorPath.Vector256, ra.SeparatorPath);
            Assert.Equal(VisSeparatorPath.Vector512, rb.SeparatorPath);

            // At one thread the walk is a pure function of the map, so the
            // work counters must match too: the same chains, candidates and
            // separator clips, which no mere agreement of the rows implies.
            AssertSameOutput(a, ra, b, rb, $"{what} at {degree} threads", compareWork: degree == 1);
        }
    }

    private static async Task<(BspData Map, VisResult Result)> RunAsync(
        (BspData Map, PortalSet Portals) input, VisSeparatorPath path, int degree, VvisOptions options)
    {
        VisResult result = await Vvis.ComputeAsync(
            input.Map,
            input.Portals,
            new VisContext
            {
                Options = options with { SeparatorPath = path },
                Parallelism = new CompileParallelism { MaxDegree = degree },
            },
            CancellationToken.None);
        return (input.Map, result);
    }

    private static void AssertSameOutput(BspData a, VisResult ra, BspData b, VisResult rb, string what, bool compareWork)
    {
        Assert.True(ra.TotalVisibleClusters > 0, $"{what}: nothing visible");
        Assert.Equal(a[BspLump.Visibility].Data.ToArray(), b[BspLump.Visibility].Data.ToArray());
        Assert.Equal(a[BspLump.Leafs].Data.ToArray(), b[BspLump.Leafs].Data.ToArray());
        Assert.Equal(ra.ClusterCount, rb.ClusterCount);
        for (int c = 0; c < ra.ClusterCount; c++)
        {
            Assert.True(ra.Pvs(c).SequenceEqual(rb.Pvs(c)), $"{what}: PVS row {c}");
            Assert.True(ra.Pas(c).SequenceEqual(rb.Pas(c)), $"{what}: PAS row {c}");
        }

        Assert.Equal(ra.TotalVisibleClusters, rb.TotalVisibleClusters);
        Assert.Equal(ra.DeepestFlow, rb.DeepestFlow);
        if (compareWork)
        {
            Assert.Equal(ra.Work, rb.Work);
        }
    }

    /// <summary>
    /// A grid of rooms, each wall holding one random convex polygon window
    /// or none, wound as <see cref="VisFixture.WindowAtX"/> and
    /// <see cref="VisFixture.WindowAtY"/> wind theirs.
    /// </summary>
    private static PortalFile RandomRooms(Random random, out int clusters)
    {
        const float cell = 64f;
        int width = 3 + random.Next(5);
        int height = 3 + random.Next(5);
        clusters = width * height;
        int Cluster(int i, int j) => (j * width) + i;

        List<FilePortal> portals = [];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                if (i + 1 < width && random.Next(8) != 0)
                {
                    // A window in the plane x = (i + 1) * cell, over (y, z).
                    Vec3[] points = Window(random, j * cell, cell);
                    float x = (i + 1) * cell;
                    portals.Add(new FilePortal(
                        Cluster(i, j), Cluster(i + 1, j), [.. points.Select(p => new Vec3(x, p.X, p.Y))]));
                }

                if (j + 1 < height && random.Next(8) != 0)
                {
                    // In the plane y = (j + 1) * cell, over (x, z), wound the
                    // other way round, as WindowAtY is.
                    Vec3[] points = Window(random, i * cell, cell);
                    float y = (j + 1) * cell;
                    portals.Add(new FilePortal(
                        Cluster(i, j), Cluster(i, j + 1), [.. points.Reverse().Select(p => new Vec3(p.X, y, p.Y))]));
                }
            }
        }

        return VisFixture.Portals(clusters, [.. portals]);
    }

    /// <summary>
    /// A convex polygon of three to nine points, counter-clockwise in a
    /// wall's (along, up) plane, inside the wall's extent, on a 1/8 grid half
    /// the time.
    /// </summary>
    private static Vec3[] Window(Random random, float wallStart, float cell)
    {
        int sides = 3 + random.Next(7);
        float radius = 4f + (random.NextSingle() * 20f);
        float along = wallStart + radius + 2f + (random.NextSingle() * (cell - (2f * radius) - 4f));
        float up = radius + 2f + (random.NextSingle() * (cell - (2f * radius) - 4f));
        float phase = random.NextSingle();
        bool quantize = random.Next(2) == 0;
        Vec3[] points = new Vec3[sides];
        for (int k = 0; k < sides; k++)
        {
            (float s, float c) = VisClipLanesTests.Turn(phase + ((float)k / sides));
            float a = along + (c * radius);
            float u = up + (s * radius);
            points[k] = quantize ? new Vec3(MathF.Round(a * 8f) / 8f, MathF.Round(u * 8f) / 8f, 0f) : new Vec3(a, u, 0f);
        }

        return points;
    }

    private static BspData Clone(BspData bsp)
    {
        BspData copy = new();
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            BspLumpData data = bsp[lump];
            if (!data.IsEmpty)
            {
                copy.SetLump(lump, data.Data.ToArray(), data.Version);
            }
        }

        return copy;
    }

    private static async Task<(BspData Bsp, byte[] Portals)> CompileSandboxAsync()
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in SyntheticContent.Build())
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile("maps/ss_sandbox.vmf", vmf);
        IContentFileSystem content = new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        VbspContext context = new(VbspOptions.Default, content) { MapBase = "ss_sandbox" };
        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create("maps/ss_sandbox.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);
        return (vbsp.Bsp!, vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
    }

    /// <summary>Skips unless <see cref="EquivalenceBspVariable"/> names a <c>.bsp</c> with a <c>.prt</c> beside it.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class EquivalenceBspFactAttribute : FactAttribute
    {
        public EquivalenceBspFactAttribute()
        {
            string? path = Environment.GetEnvironmentVariable(EquivalenceBspVariable);
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".prt")))
            {
                Skip = $"set {EquivalenceBspVariable} to a post-vbsp .bsp (e.g. 2fort's) to compare both separator paths on it";
            }
        }
    }
}
