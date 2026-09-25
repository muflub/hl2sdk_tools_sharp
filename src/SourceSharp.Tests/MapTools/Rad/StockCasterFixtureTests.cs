using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// What stock's own caster dumps say, before this port is asked to match them.
/// </summary>
/// <remarks>
/// <para>
/// These facts do not exercise a line of the port. They exercise the fixture,
/// and they exist because the fixture is the only statement in this lane about
/// what stock does -- if it is malformed, truncated, or regenerated against a
/// different map, every parity fact downstream goes green for the wrong reason.
/// </para>
/// <para>
/// Two of them also record measurements that contradict the plan, which is the
/// other reason to write them down as assertions rather than as prose: the
/// caster set is invariant under <c>-textureshadows</c>, and stock's
/// <c>Total triangle count:</c> is not the caster count.
/// </para>
/// </remarks>
public sealed class StockCasterFixtureTests
{
    private static readonly IReadOnlyDictionary<string, StockCasterRun> Runs =
        StockCasterReference.Load();

    /// <summary>The fixture holds the four runs the README names.</summary>
    [Fact]
    public void FixtureHoldsFourRuns()
    {
        Assert.Equal(
            ["base", "spp", "sppts", "ts"],
            Runs.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Every run separates into vrad's four add calls, in order.</summary>
    /// <param name="tag">The run.</param>
    /// <remarks>
    /// The order is <c>, 2278, 2279</c>. There is no
    /// <c>brush-entity</c> run because <c>dm_lockdown</c> has no entity
    /// carrying <c>vrad_brush_cast_shadows</c>; a map that had one would show
    /// five runs, with two adjacent green ones.
    /// </remarks>
    [Theory]
    [InlineData("base")]
    [InlineData("spp")]
    [InlineData("ts")]
    [InlineData("sppts")]
    public void EveryRunHasTheFourSourcesInAddOrder(string tag)
    {
        Assert.Equal(
            ["world-brush", "sky", "displacement", "static-prop"],
            Runs[tag].Sources.Select(s => s.Name).ToArray());
    }

    /// <summary>Brushes and displacements are both <c>TRACE_ID_OPAQUE</c>.</summary>
    /// <remarks>
    /// The reason the fixture separates its sources by POSITION and not by
    /// colour. If this ever stopped being true the fixture's generator would be
    /// reading a different vrad.
    /// </remarks>
    [Fact]
    public void BrushesAndDisplacementsShareOneColour()
    {
        StockCasterRun run = Runs["base"];
        Assert.Equal("opaque", run.Source("world-brush").Colour);
        Assert.Equal("opaque", run.Source("displacement").Colour);
        Assert.Equal("sky", run.Source("sky").Colour);
        Assert.Equal("staticprop", run.Source("static-prop").Colour);
    }

    /// <summary><c>-textureshadows</c> leaves the caster set alone.</summary>
    /// <remarks>
    /// Measured, and it refutes §4b's "-StaticPropPolys and -textureshadows
    /// change the caster set; gate both". One of them does. The raw dumps have
    /// equal sha256 in both pairs; this asserts the part of that which survives
    /// into the fixture, which is every count, bound and sum of all four
    /// sources.
    /// </remarks>
    [Theory]
    [InlineData("base", "ts")]
    [InlineData("spp", "sppts")]
    public void TextureShadowsDoesNotChangeTheCasterSet(string without, string with)
    {
        IReadOnlyList<StockCasterSourceRun> a = Runs[without].Sources;
        IReadOnlyList<StockCasterSourceRun> b = Runs[with].Sources;

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            AssertSameRun(a[i], b[i]);
        }
    }

    /// <summary><c>-StaticPropPolys</c> changes only the static props.</summary>
    [Fact]
    public void StaticPropPolysChangesOnlyTheStaticProps()
    {
        StockCasterRun a = Runs["base"];
        StockCasterRun b = Runs["spp"];

        foreach (string name in new[] { "world-brush", "sky", "displacement" })
        {
            AssertSameRun(a.Source(name), b.Source(name));
        }

        Assert.NotEqual(a.Source("static-prop").Count, b.Source("static-prop").Count);
    }

    /// <summary>
    /// Compares two source runs by what they say, not by object identity.
    /// </summary>
    /// <remarks>
    /// <c>StockCasterSourceRun</c> is a record, but two of its members are
    /// <see cref="IReadOnlyList{T}"/> and a record's generated equality
    /// compares those by REFERENCE. Two runs parsed out of the same file
    /// therefore never compare equal no matter what they hold, and a fact
    /// written as <c>Assert.Equal(runA, runB)</c> fails for a reason that has
    /// nothing to do with the thing it is asking about. This walks the head
    /// and tail instead.
    /// </remarks>
    private static void AssertSameRun(StockCasterSourceRun a, StockCasterSourceRun b)
    {
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Colour, b.Colour);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Min, b.Min);
        Assert.Equal(a.Max, b.Max);
        Assert.Equal(a.SumX, b.SumX);
        Assert.Equal(a.SumY, b.SumY);
        Assert.Equal(a.SumZ, b.SumZ);
        Assert.Equal(a.Head, b.Head);
        Assert.Equal(a.Tail, b.Tail);
    }

    /// <summary>The counts stock gave, written down.</summary>
    /// <param name="tag">The run.</param>
    /// <param name="source">The source.</param>
    /// <param name="expected">How many triangles stock emitted.</param>
    /// <remarks>
    /// A no-backsliding gate. If the fixture is regenerated against a different
    /// map, a different vrad, or a differently prepared <c>sprp</c> lump, these
    /// go red before any parity fact has a chance to go green against the wrong
    /// reference.
    /// </remarks>
    [Theory]
    [InlineData("base", "world-brush", 23549)]
    [InlineData("base", "sky", 512)]
    [InlineData("base", "displacement", 1568)]
    [InlineData("base", "static-prop", 17304)]
    [InlineData("spp", "static-prop", 92582)]
    public void StockEmittedTheseCounts(string tag, string source, int expected)
    {
        Assert.Equal(expected, Runs[tag].Source(source).Count);
    }

    /// <summary>The two totals, which is what the acceleration structure sees.</summary>
    [Fact]
    public void StaticPropPolysNearlyTriplesTheScene()
    {
        Assert.Equal(42933, Runs["base"].TotalTriangles);
        Assert.Equal(118211, Runs["spp"].TotalTriangles);
    }

    /// <summary>Each source's head and tail are contiguous and in order.</summary>
    /// <param name="tag">The run.</param>
    /// <remarks>
    /// The head and tail are what pin triangle ORDER, so a fixture whose
    /// indices had drifted would let an out-of-order port pass.
    /// </remarks>
    [Theory]
    [InlineData("base")]
    [InlineData("spp")]
    public void HeadAndTailAreContiguousRuns(string tag)
    {
        foreach (StockCasterSourceRun source in Runs[tag].Sources)
        {
            Assert.Equal(32, source.Head.Count);
            Assert.Equal(32, source.Tail.Count);

            for (int i = 0; i < source.Head.Count; i++)
            {
                Assert.Equal(i, source.Head[i].Index);
            }

            for (int i = 0; i < source.Tail.Count; i++)
            {
                Assert.Equal(source.Count - source.Tail.Count + i, source.Tail[i].Index);
            }
        }
    }

    /// <summary>The sum tolerance is derived from the dump's precision.</summary>
    /// <remarks>
    /// `%5.2f` rounds each coordinate by at most 0.005, and a run holds
    /// `3 * Count` of them. Pinned so that widening it later is a decision
    /// somebody makes on purpose rather than a number that drifts.
    /// </remarks>
    [Fact]
    public void SumToleranceIsHalfAPrintedDigitPerCoordinate()
    {
        StockCasterSourceRun sky = Runs["base"].Source("sky");
        Assert.Equal(512, sky.Count);
        Assert.Equal(3 * 512 * 0.005, sky.SumTolerance, 9);
    }
}
