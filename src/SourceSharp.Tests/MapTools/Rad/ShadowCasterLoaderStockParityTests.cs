using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;
using SourceSharp.Tests.MapTools.Io;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// The whole load path run twice on the golden map, with and without
/// <c>-StaticPropPolys</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built once for the class because it reads 18,000 files out of the installed
/// game's VPKs to get at the prop models, and every fact below is read-only
/// over the result.
/// </para>
/// <para>
/// <see cref="NullPropCollisionSource"/> deliberately: the default path's
/// triangles come out of vphysics and this tree has no binding to it yet, so
/// every prop lands on stock's AABB branch. That is wrong in a way the caster
/// count SHOWS -- 2,724 against stock's 17,304 -- rather than wrong in a way
/// that looks right, which is the whole argument for the seam.
/// </para>
/// </remarks>
public sealed class ShadowCasterLoaderFixture : IAsyncLifetime
{
    /// <summary>Why the content-dependent half is unavailable, or null.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The default run.</summary>
    public ShadowCasterLoadReport Default { get; private set; }

    /// <summary>The <c>-StaticPropPolys</c> run.</summary>
    public ShadowCasterLoadReport PropPolys { get; private set; }

    /// <summary>Stock's own answers.</summary>
    internal IReadOnlyDictionary<string, StockCasterRun> Stock { get; private set; } =
        new Dictionary<string, StockCasterRun>();

    private ContentFileSystem? _content;

    /// <summary>Loads the map, mounts the game, and runs both passes.</summary>
    /// <returns>A task that completes when both runs are done.</returns>
    public async Task InitializeAsync()
    {
        Stock = StockCasterReference.Load();

        SkipReason = InstalledGameContent.SkipReason;
        if (SkipReason is not null)
        {
            return;
        }

        await using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = await BspFile.LoadAsync(stream, CancellationToken.None);

        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);
        VPath gameInfo = host.ToVirtualPath(
            Path.Combine(InstalledGameContent.BaseDirectory, "hl2mp", "gameinfo.txt"));
        VPath baseDirectory = host.ToVirtualPath(InstalledGameContent.BaseDirectory);

        GameContentMounter.Result mounted =
            await GameContentMounter.MountAsync(guarded, gameInfo, baseDirectory);
        _content = mounted.Content;

        Default = await ShadowCasterLoader.LoadAsync(
            bsp, VradOptions.Default, _content, NullPropCollisionSource.Instance);

        PropPolys = await ShadowCasterLoader.LoadAsync(
            bsp,
            VradOptions.Default with { StaticPropPolys = true },
            _content,
            NullPropCollisionSource.Instance);
    }

    /// <summary>Unmounts the game content.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    public async Task DisposeAsync()
    {
        if (_content is not null)
        {
            await _content.DisposeAsync();
        }
    }
}

/// <summary>
/// The 4b gate: per-source triangle counts and bounds against stock.
/// </summary>
/// <remarks>
/// <para>
/// Three of the four sources are gated exactly, because all three are entirely
/// managed code: world brushes, sky faces, displacements, and -- under
/// <c>-StaticPropPolys</c> -- static props from the render mesh.
/// </para>
/// <para>
/// The fourth, the default <c>.phy</c> prop path, cannot be: stock's triangles
/// come back out of vphysics through <c>ICollisionQuery</c>
/// (<c>vradstaticprops.cpp:1847-1857</c>) and there is no managed decoder for
/// an IVP compact ledge tree in this tree, nor should there be. What IS gated
/// there is everything around the hole: which props are skipped, which branch
/// each takes, and the exact shape of stock's AABB fallback including its
/// <c>id + 1</c> defect.
/// </para>
/// </remarks>
[Collection("shadow-casters")]
public sealed class ShadowCasterLoaderStockParityTests
    : IClassFixture<ShadowCasterLoaderFixture>
{
    /// <summary>How far a bound may sit from stock's, in units.</summary>
    /// <remarks>
    /// Stock's dumper prints <c>%5.2f</c> (<c>vrad.cpp:1345</c>), so a
    /// coordinate it recorded is within half of 0.01 of the one it held. The
    /// tolerance is that rounding and nothing else: the arithmetic itself is
    /// expected to agree.
    /// </remarks>
    private const float BoundTolerance = 0.005f;

    private readonly ShadowCasterLoaderFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>Takes the shared runs and the runner's output sink.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <param name="output">Where measurements are written.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public ShadowCasterLoaderStockParityTests(
        ShadowCasterLoaderFixture fixture, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(output);
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// How many brush triangles the normalise fork costs, measured.
    /// </summary>
    /// <remarks>
    /// Four, of 23,549. Not a defect in the brush path: stock's
    /// <c>BaseWindingForPlane</c> normalises with <c>VectorNormalize</c>
    /// (<c>polylib.cpp:290</c> into <c>vector.h:2225-2251</c>), which is
    /// <c>rsqrtss</c> plus one Newton-Raphson step and three multiplies, while
    /// <c>WindingArena.BaseWindingForPlane</c> uses an exact per-component
    /// divide. The two agree to within a few ULPs -- and
    /// <c>AddBrushToRaytraceEnvironment</c> clips with an epsilon of exactly
    /// zero (<c>trace.cpp:516</c>), so a few ULPs is all it takes to flip a
    /// base-winding corner from on-plane to in-front and gain or lose one fan
    /// triangle. It happens four times in 15,653 brush sides.
    /// </remarks>
    private const int NormaliseForkBrushTriangles = 4;

    /// <summary>World brush triangles: stock's count, less the normalise fork.</summary>
    /// <remarks>
    /// <para>
    /// NOT A TOLERANCE, AND NOT THIS LANE'S TO CLOSE. The exact divide is a
    /// deliberate project-wide choice -- <c>Vec3.Normalise</c> says so in as
    /// many words, and <c>Vec3.NormaliseLikeStock</c> exists precisely because
    /// <c>rsqrtss</c>'s result is implementation-defined and therefore
    /// machine-dependent, which would cost I4's cross-machine determinism to
    /// buy stock parity. <c>WindingArena</c> belongs to another lane, and
    /// every consumer of <c>BaseWindingForPlane</c> -- vbsp's brush faces,
    /// vvis's portals -- carries the same fork.
    /// </para>
    /// <para>
    /// So the number is pinned from both sides instead: a real defect in the
    /// brush path widens the gap and this fails, and the day the fork is
    /// closed it also fails and has to be rewritten on purpose. What it never
    /// does is drift.
    /// </para>
    /// </remarks>
    [InstalledGameFact]
    public void WorldBrushCountIsStocksLessTheNormaliseFork()
    {
        Assert.Equal(
            _fixture.Stock["base"].Source("world-brush").Count - NormaliseForkBrushTriangles,
            _fixture.Default.Set.Stats(ShadowCasterSource.WorldBrush).Triangles);
    }

    /// <summary>World brush triangles: stock's bounding box.</summary>
    [InstalledGameFact]
    public void WorldBrushBoundsMatchStock() => AssertBounds("base", "world-brush",
        ShadowCasterSource.WorldBrush, _fixture.Default);

    /// <summary>
    /// Writes every caster triangle to the file named by
    /// <c>SS_CASTER_DUMP</c>, in stock's <c>WriteRTEnv</c> format.
    /// </summary>
    /// <remarks>
    /// Not a check -- it has no assertion beyond the file existing -- and it
    /// skips unless the variable is set. It exists because the committed
    /// fixture is a REDUCTION of stock's dump (counts, bounds, sums, the first
    /// and last 32) and there are questions only the whole thing can answer:
    /// which triangles differ, and whether a count deficit is a clean loss or a
    /// churn of losses and gains. This lane needed exactly that once, and
    /// guessing at it produced a tolerance that was wrong by construction.
    /// Point it at a file and diff against stock's own <c>trace.txt</c>; the
    /// README beside the fixture carries the command that produces one.
    /// </remarks>
    [InstalledGameFact]
    public void CasterDumpIsWrittenWhenAsked()
    {
        string? path = Environment.GetEnvironmentVariable("SS_CASTER_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            _output.WriteLine("SS_CASTER_DUMP is not set; nothing written");
            return;
        }

        foreach ((string suffix, ShadowCasterLoadReport report) in new[]
        {
            (".base", _fixture.Default),
            (".spp", _fixture.PropPolys),
        })
        {
            using StreamWriter writer = new(path + suffix);
            foreach (TracedTriangle triangle in report.Set.Triangles)
            {
                // vrad.cpp:1341-1351, %5.2f per coordinate, so that a diff
                // against stock's file is a diff of the same text.
                writer.WriteLine("3");
                foreach (Vec3 v in new[] { triangle.V0, triangle.V1, triangle.V2 })
                {
                    writer.WriteLine(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0,5:F2} {1,5:F2} {2,5:F2}",
                        v.X,
                        v.Y,
                        v.Z));
                }
            }

            _output.WriteLine($"{report.Set.Count} triangles to {path}{suffix}");
        }
    }

    /// <summary>
    /// World brush triangles: stock's total shadow-casting AREA, to a part in
    /// ten thousand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Area rather than a coordinate sum, and only for this source, because
    /// the two runs do not hold the same triangles. Measured against stock's
    /// full dump: 53 triangles are in stock's brush run and not in this port's,
    /// 49 are in this port's and not in stock's, and 44 more are the same
    /// triangle printed differently in the last of stock's two decimal places.
    /// A coordinate sum carries all of that wholesale.
    /// </para>
    /// <para>
    /// THE COUNT DEFICIT IS NOT THE CHURN, and assuming it was produced a
    /// tolerance that was wrong by construction on the first attempt. Four is
    /// the NET; a hundred triangles move. They move because
    /// <c>AddBrushToRaytraceEnvironment</c> clips with an epsilon of exactly
    /// zero (<c>trace.cpp:516</c>) and the base winding is scaled by
    /// <c>MAX_COORD_INTEGER * 4</c> before it is clipped
    /// (<c>polylib.cpp:296</c>), so the normalise fork's few ULPs arrive at the
    /// clipped corner as a hundredth of a unit and flip corners across the
    /// plane in both directions.
    /// </para>
    /// <para>
    /// Area is what a shadow is made of, so it is the right quantity to hold
    /// to account, and the churn barely touches it: the differing triangles are
    /// slivers, median area zero, and the two totals are
    /// <c>80,159,708.07</c> against <c>80,159,480.4</c> -- a divergence of
    /// <b>2.8 parts per million</b>. The tolerance here is a part in ten
    /// thousand, thirty-five times that, and a systematic error is orders
    /// above it again: a dropped transform, a wrong scale, a lost brush.
    /// </para>
    /// </remarks>
    [InstalledGameFact]
    public void WorldBrushAreaMatchesStock() => AssertArea("base", "world-brush",
        ShadowCasterSource.WorldBrush, _fixture.Default);

    /// <summary>Sky face triangles: stock's total area.</summary>
    [InstalledGameFact]
    public void SkyAreaMatchesStock() => AssertArea("base", "sky",
        ShadowCasterSource.Sky, _fixture.Default);

    /// <summary>Displacement triangles: stock's total area.</summary>
    [InstalledGameFact]
    public void DisplacementAreaMatchesStock() => AssertArea("base", "displacement",
        ShadowCasterSource.Displacement, _fixture.Default);

    /// <summary><c>-StaticPropPolys</c> prop triangles: stock's total area.</summary>
    [InstalledGameFact]
    public void StaticPropPolysAreaMatchesStock() => AssertArea("spp", "static-prop",
        ShadowCasterSource.StaticProp, _fixture.PropPolys);

    /// <summary>World brush triangles: stock's first and last 32, in order.</summary>
    [InstalledGameFact]
    public void WorldBrushHeadAndTailMatchStock() => AssertHeadAndTail("base", "world-brush",
        ShadowCasterSource.WorldBrush, _fixture.Default);

    /// <summary>Sky face triangles: stock's count exactly.</summary>
    [InstalledGameFact]
    public void SkyCountMatchesStock() => AssertCount("base", "sky",
        ShadowCasterSource.Sky, _fixture.Default);

    /// <summary>Sky face triangles: stock's bounding box.</summary>
    [InstalledGameFact]
    public void SkyBoundsMatchStock() => AssertBounds("base", "sky",
        ShadowCasterSource.Sky, _fixture.Default);

    /// <summary>Sky face triangles: stock's coordinate sums.</summary>
    [InstalledGameFact]
    public void SkyCoordinateSumsMatchStock() => AssertSums("base", "sky",
        ShadowCasterSource.Sky, _fixture.Default);

    /// <summary>Sky face triangles: stock's first and last 32, in order.</summary>
    [InstalledGameFact]
    public void SkyHeadAndTailMatchStock() => AssertHeadAndTail("base", "sky",
        ShadowCasterSource.Sky, _fixture.Default);

    /// <summary>Displacement triangles: stock's count exactly.</summary>
    [InstalledGameFact]
    public void DisplacementCountMatchesStock() => AssertCount("base", "displacement",
        ShadowCasterSource.Displacement, _fixture.Default);

    /// <summary>Displacement triangles: stock's bounding box.</summary>
    [InstalledGameFact]
    public void DisplacementBoundsMatchStock() => AssertBounds("base", "displacement",
        ShadowCasterSource.Displacement, _fixture.Default);

    /// <summary>Displacement triangles: stock's coordinate sums.</summary>
    [InstalledGameFact]
    public void DisplacementCoordinateSumsMatchStock() => AssertSums("base", "displacement",
        ShadowCasterSource.Displacement, _fixture.Default);

    /// <summary>Displacement triangles: stock's first and last 32, in order.</summary>
    [InstalledGameFact]
    public void DisplacementHeadAndTailMatchStock() => AssertHeadAndTail("base", "displacement",
        ShadowCasterSource.Displacement, _fixture.Default);

    /// <summary><c>-StaticPropPolys</c> prop triangles: stock's count exactly.</summary>
    /// <remarks>
    /// The lane's headline number. 92,582 triangles out of 261 props, read from
    /// the <c>.vvd</c> and <c>.dx80.vtx</c> of 56 distinct models through
    /// stock's own body-part / model / LOD 0 / mesh / strip-group / strip walk.
    /// </remarks>
    [InstalledGameFact]
    public void StaticPropPolysCountMatchesStock() => AssertCount("spp", "static-prop",
        ShadowCasterSource.StaticProp, _fixture.PropPolys);

    /// <summary><c>-StaticPropPolys</c> prop triangles: stock's bounding box.</summary>
    /// <remarks>
    /// The bound is what proves the per-prop transform. A render-mesh path that
    /// forgot <c>AngleMatrix( prop.m_Angles, prop.m_Origin )</c> would emit
    /// exactly 92,582 triangles piled on the origin.
    /// </remarks>
    [InstalledGameFact]
    public void StaticPropPolysBoundsMatchStock() => AssertBounds("spp", "static-prop",
        ShadowCasterSource.StaticProp, _fixture.PropPolys);

    /// <summary><c>-StaticPropPolys</c> prop triangles: stock's coordinate sums.</summary>
    [InstalledGameFact]
    public void StaticPropPolysCoordinateSumsMatchStock() => AssertSums("spp", "static-prop",
        ShadowCasterSource.StaticProp, _fixture.PropPolys);

    /// <summary><c>-StaticPropPolys</c> prop triangles: stock's first and last 32.</summary>
    [InstalledGameFact]
    public void StaticPropPolysHeadAndTailMatchStock() => AssertHeadAndTail("spp", "static-prop",
        ShadowCasterSource.StaticProp, _fixture.PropPolys);

    /// <summary>The three non-prop sources are unchanged by the switch.</summary>
    /// <remarks>
    /// Stock's own dumps agree; this is the managed side of the same statement,
    /// and it is what makes the load-cost measurement below attributable to the
    /// prop pass alone.
    /// </remarks>
    [InstalledGameFact]
    public void StaticPropPolysLeavesTheRestOfTheSceneAlone()
    {
        foreach (ShadowCasterSource source in new[]
        {
            ShadowCasterSource.BrushEntity,
            ShadowCasterSource.WorldBrush,
            ShadowCasterSource.Sky,
            ShadowCasterSource.Displacement,
        })
        {
            Assert.Equal(_fixture.Default.Set.Stats(source), _fixture.PropPolys.Set.Stats(source));
        }
    }

    /// <summary>The map has no brush entity casting shadows, so that run is empty.</summary>
    /// <remarks>
    /// Checked rather than assumed. A raw scan of the entity lump finds zero
    /// occurrences of <c>vrad_brush_cast_shadows</c>, which is also why stock's
    /// dump has four colour runs and not five -- if it had one, the fixture's
    /// first green run would be a mixture and every brush number here would be
    /// measuring the wrong thing.
    /// </remarks>
    [InstalledGameFact]
    public void LockdownHasNoBrushEntityCasters()
    {
        Assert.True(_fixture.Default.Set.Stats(ShadowCasterSource.BrushEntity).IsEmpty);
    }

    /// <summary>
    /// Without vphysics every prop falls to stock's AABB branch, and the count
    /// says so.
    /// </summary>
    /// <remarks>
    /// 261 props, 34 carrying <c>STATIC_PROP_NO_SHADOW</c>, 227 boxes of twelve
    /// triangles each (<c>AddAxisAlignedRectangularSolid</c>, six quads) =
    /// 2,724. Stock's own figure is 17,304. The 14,580-triangle gap is the
    /// vphysics hole, stated as a number rather than as a comment, so that the
    /// day <c>IPropCollisionSource</c> gets a real implementation this fact
    /// fails and has to be rewritten deliberately.
    /// </remarks>
    [InstalledGameFact]
    public void WithoutVphysicsEveryPropFallsToItsBox()
    {
        StaticPropShadowCasterReport props = _fixture.Default.Props;
        Assert.Equal(261, props.PropsConsidered);
        Assert.Equal(34, props.PropsSkippedNoShadow);
        Assert.Equal(0, props.PropsFromCollision);
        Assert.Equal(227, props.PropsFromHullBox);
        Assert.Equal(227 * 12, props.TrianglesAdded);

        Assert.Equal(
            17304,
            _fixture.Stock["base"].Source("static-prop").Count);
    }

    /// <summary>Under <c>-StaticPropPolys</c> no prop takes the box branch.</summary>
    /// <remarks>
    /// The render-mesh path needs no vphysics at all, which is why it is the
    /// half that can be gated exactly. If any prop had fallen back here, the
    /// 92,582 above would be arithmetic luck.
    /// </remarks>
    [InstalledGameFact]
    public void StaticPropPolysTakesTheRenderMeshForEveryCastingProp()
    {
        StaticPropShadowCasterReport props = _fixture.PropPolys.Props;
        Assert.Equal(261, props.PropsConsidered);
        Assert.Equal(34, props.PropsSkippedNoShadow);
        Assert.Equal(227, props.PropsFromRenderMesh);
        Assert.Equal(0, props.PropsFromHullBox);
        Assert.Equal(StaticPropAbandonReason.None, props.Abandoned);
    }

    /// <summary>Nothing is transparent without <c>-textureshadows</c>.</summary>
    [InstalledGameFact]
    public void NoTriangleIsTransparentWithoutTextureShadows()
    {
        Assert.Equal(0, _fixture.Default.Set.TransparentCount);
        Assert.Equal(0, _fixture.PropPolys.Set.TransparentCount);
    }

    /// <summary>The whole scene builds a KD tree, and it holds every triangle.</summary>
    /// <remarks>
    /// The load path's only consumer. Stock's own build of this scene is the
    /// 0.41 s it prints at <c>vrad.cpp:2290</c>.
    /// </remarks>
    [InstalledGameFact]
    public void TheSceneBuildsATracer()
    {
        KdRayTracer tracer = _fixture.Default.Set.BuildTracer();
        Assert.Equal(_fixture.Default.Set.Count, tracer.TriangleCount);
        Assert.True(tracer.NodeCount > 1);
    }

    /// <summary>What <c>-StaticPropPolys</c> costs, load and KD build.</summary>
    /// <remarks>
    /// <para>
    /// §4b: "<c>-StaticPropPolys</c> costs SERIAL time, not ray time" -- stock
    /// went 4.03 s to 15.00 s on 2fort with every traced stage unchanged. This
    /// fact measures the managed half of the same thing on the golden map and
    /// prints it, because the number is a ratio on one box and baking a
    /// threshold in would make the fact fail on a slower machine for reasons
    /// that have nothing to do with the code.
    /// </para>
    /// <para>
    /// ANY NUMBER THIS PRINTS UNDER <c>dotnet test</c> WITHOUT <c>-c
    /// Release</c> IS A MEASUREMENT OF THE DEBUG JIT, which another lane in
    /// this project measured at 2.4x on tracing code and which voided four of
    /// its own results before anyone noticed.
    /// </para>
    /// </remarks>
    [InstalledGameFact]
    public void StaticPropPolysLoadCostIsReported()
    {
        ShadowCasterLoadReport a = _fixture.Default;
        ShadowCasterLoadReport b = _fixture.PropPolys;

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        KdRayTracer plain = a.Set.BuildTracer();
        double plainBuild = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        KdRayTracer polys = b.Set.BuildTracer();
        double polysBuild = watch.Elapsed.TotalMilliseconds;

        _output.WriteLine(
            $"load   brushes {a.BrushMilliseconds:F1} ms  disps {a.DisplacementMilliseconds:F1} ms"
            + $"  props {a.PropMilliseconds:F1} ms  total {a.TotalMilliseconds:F1} ms"
            + $"  ({a.Set.Count} triangles)");
        _output.WriteLine(
            $"load+  brushes {b.BrushMilliseconds:F1} ms  disps {b.DisplacementMilliseconds:F1} ms"
            + $"  props {b.PropMilliseconds:F1} ms  total {b.TotalMilliseconds:F1} ms"
            + $"  ({b.Set.Count} triangles)");
        _output.WriteLine(
            $"kd     {plainBuild:F1} ms ({plain.NodeCount} nodes) -> {polysBuild:F1} ms "
            + $"({polys.NodeCount} nodes); stock prints 0.41 s -> 1.27 s for the same pair");

        // A floor, not a threshold: the switch has to make the scene bigger.
        Assert.True(b.Set.Count > a.Set.Count);
    }

    private static int OffsetOf(ShadowCasterSet set, ShadowCasterSource source)
    {
        // The sources are contiguous and in enum order because ShadowCasterLoader
        // calls them in that order; there is no offset recorded anywhere else.
        int offset = 0;
        for (int i = 0; i < (int)source; i++)
        {
            offset += set.Stats((ShadowCasterSource)i).Triangles;
        }

        return offset;
    }

    private void AssertCount(
        string tag, string name, ShadowCasterSource source, ShadowCasterLoadReport report)
    {
        Assert.Equal(_fixture.Stock[tag].Source(name).Count, report.Set.Stats(source).Triangles);
    }

    private void AssertBounds(
        string tag, string name, ShadowCasterSource source, ShadowCasterLoadReport report)
    {
        StockCasterSourceRun stock = _fixture.Stock[tag].Source(name);
        ShadowCasterStats ours = report.Set.Stats(source);

        AssertVec(stock.Min, ours.Min, $"{name} mins");
        AssertVec(stock.Max, ours.Max, $"{name} maxs");
    }

    private void AssertSums(
        string tag, string name, ShadowCasterSource source, ShadowCasterLoadReport report)
    {
        StockCasterSourceRun stock = _fixture.Stock[tag].Source(name);
        (double x, double y, double z) = SumOf(report, source);

        Assert.Equal(stock.SumX, x, stock.SumTolerance);
        Assert.Equal(stock.SumY, y, stock.SumTolerance);
        Assert.Equal(stock.SumZ, z, stock.SumTolerance);
    }

    /// <summary>
    /// How far a total area may sit from stock's, relative.
    /// </summary>
    /// <remarks>
    /// A part in ten thousand. The largest divergence any source actually
    /// shows is the world brushes' 2.8 parts per million, so this is
    /// thirty-five times the measured worst case and orders below anything
    /// systematic.
    /// </remarks>
    private const double AreaTolerance = 1.0e-4;

    private void AssertArea(
        string tag, string name, ShadowCasterSource source, ShadowCasterLoadReport report)
    {
        StockCasterSourceRun stock = _fixture.Stock[tag].Source(name);

        int offset = OffsetOf(report.Set, source);
        ReadOnlySpan<TracedTriangle> triangles =
            report.Set.Triangles.Slice(offset, report.Set.Stats(source).Triangles);

        double area = 0;
        foreach (TracedTriangle triangle in triangles)
        {
            Vec3 u = triangle.V1 - triangle.V0;
            Vec3 v = triangle.V2 - triangle.V0;
            area += 0.5 * Vec3.Cross(u, v).Length();
        }

        double allowed = stock.Area * AreaTolerance;
        _output.WriteLine(
            $"{name} area: stock {stock.Area:F1}, ours {area:F1}, "
            + $"{Math.Abs(area - stock.Area) / stock.Area * 1e6:F2} ppm");

        Assert.Equal(stock.Area, area, allowed);
    }

    private static (double X, double Y, double Z) SumOf(
        ShadowCasterLoadReport report, ShadowCasterSource source)
    {
        int offset = OffsetOf(report.Set, source);
        ReadOnlySpan<TracedTriangle> triangles =
            report.Set.Triangles.Slice(offset, report.Set.Stats(source).Triangles);

        double x = 0;
        double y = 0;
        double z = 0;
        foreach (TracedTriangle triangle in triangles)
        {
            x += (double)triangle.V0.X + triangle.V1.X + triangle.V2.X;
            y += (double)triangle.V0.Y + triangle.V1.Y + triangle.V2.Y;
            z += (double)triangle.V0.Z + triangle.V1.Z + triangle.V2.Z;
        }

        return (x, y, z);
    }

    private void AssertHeadAndTail(
        string tag, string name, ShadowCasterSource source, ShadowCasterLoadReport report)
    {
        StockCasterSourceRun stock = _fixture.Stock[tag].Source(name);
        int offset = OffsetOf(report.Set, source);
        int ours = report.Set.Stats(source).Triangles;
        ReadOnlySpan<TracedTriangle> triangles = report.Set.Triangles;

        // The head is indexed from the START of the run and the tail from its
        // END, and that difference is load-bearing rather than a convenience.
        // A run that is four triangles short -- which the world brushes are,
        // see NormaliseForkBrushTriangles -- has every absolute index after the
        // loss shifted by four, so comparing stock's tail at stock's own index
        // reports a mismatch at the first tail entry and names coordinates from
        // a completely unrelated brush. Anchoring each end to its own end asks
        // the question that was meant: do the two runs start the same and end
        // the same.
        foreach (StockCasterTriangle expected in stock.Head)
        {
            Compare(expected, triangles[offset + expected.Index], $"head {expected.Index}");
        }

        for (int i = 0; i < stock.Tail.Count; i++)
        {
            int fromEnd = stock.Tail.Count - i;
            Compare(stock.Tail[i], triangles[offset + ours - fromEnd], $"tail -{fromEnd}");
        }

        void Compare(StockCasterTriangle expected, TracedTriangle actual, string where)
        {
            AssertVec(expected.V0, actual.V0, $"{name} {where} v0");
            AssertVec(expected.V1, actual.V1, $"{name} {where} v1");
            AssertVec(expected.V2, actual.V2, $"{name} {where} v2");
        }
    }

    private static void AssertVec(Vec3 expected, Vec3 actual, string what)
    {
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= BoundTolerance
            && MathF.Abs(expected.Y - actual.Y) <= BoundTolerance
            && MathF.Abs(expected.Z - actual.Z) <= BoundTolerance,
            $"{what}: stock {expected}, ours {actual}");
    }
}
