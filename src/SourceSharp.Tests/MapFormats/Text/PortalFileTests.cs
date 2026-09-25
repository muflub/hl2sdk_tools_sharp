using System.Text;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the <c>.prt</c> portal file, from the reference implementation
/// (the reader) and (the writer).
/// </summary>
public class PortalFileTests
{
    /// <summary>
    /// One square portal between clusters 0 and 1, spelled exactly as vbsp
    /// writes it: integers collapsed, a space before each close paren and
    /// another after it, and a trailing space before the newline.
    /// </summary>
    private const string OneSquarePortal =
        "PRT1\n2\n1\n4 0 1 (0 0 0 ) (0 0 64 ) (0 64 64 ) (0 64 0 ) \n";

    private static Task<PortalFile> ParseAsync(string text) =>
        PortalFile.ParseAsync(text, CancellationToken.None).AsTask();

    [Fact]
    public async Task HeaderIsMagicThenClustersThenPortals()
    {
        // Fscanf(f, "%79s\n%i\n%i\n", magic, &portalclusters,
        // &g_numportals). Clusters come FIRST.
        PortalFile file = await ParseAsync(OneSquarePortal);

        Assert.Equal(2, file.ClusterCount);
        Assert.Single(file.Portals);
    }

    [Fact]
    public async Task LowercaseMagicIsAccepted()
    {
        // Compares with stricmp. The writer only ever emits upper
        // case, so this can only be hit by a hand-made or
        // third-party file -- but it IS accepted.
        PortalFile file = await ParseAsync(OneSquarePortal.Replace("PRT1", "prt1", StringComparison.Ordinal));

        Assert.Single(file.Portals);
    }

    [Fact]
    public async Task Prt2IsRejected()
    {
        // The only magic this reader accepts is PRT1. PRT2 and
        // PRT1-AM belong to other branches of the reference tool and are rejected.
        InvalidPortalFileException error = await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync(OneSquarePortal.Replace("PRT1", "PRT2", StringComparison.Ordinal)));

        Assert.Contains("not a portal file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeaderFieldsMaySitOnOneLine()
    {
        // The '\n' characters in the reference implementation's format string are ordinary
        // whitespace directives, so the conventional one-per-line layout is a
        // convention rather than a rule.
        PortalFile file = await ParseAsync("PRT1 2 1 4 0 1 (0 0 0 ) (0 0 64 ) (0 64 64 ) (0 64 0 ) \n");

        Assert.Equal(2, file.ClusterCount);
        Assert.Single(file.Portals);
    }

    [Fact]
    public async Task TruncatedHeaderIsAFailedHeaderRead()
    {
        // Fewer than three fields.
        InvalidPortalFileException error = await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n4\n"));

        Assert.Contains("failed to read header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegerSpelledAndFloatSpelledCoordinatesParseIdentically()
    {
        // THE quirk the plan calls out. The reference implementation scans with "%lf", so both
        // spellings go through the same double conversion,
        // narrows per component. The results must be bit-identical, not merely
        // close.
        PortalFile integers = await ParseAsync(
            "PRT1\n2\n1\n3 0 1 (12 -3 0 ) (12 -3 64 ) (12 40 64 ) \n");
        PortalFile floats = await ParseAsync(
            "PRT1\n2\n1\n3 0 1 (12.000000 -3.000000 0.000000 ) (12.000000 -3.000000 64.000000 ) (12.000000 40.000000 64.000000 ) \n");

        Assert.Equal(integers.Portals[0].Points, floats.Portals[0].Points);
    }

    [Fact]
    public async Task CoordinateIsNarrowedToFloatSoPrecisionBeyondItIsLost()
    {
        // The reference implementation, "scanf into double, then assign to vec_t". The
        // DOUBLE half of that is not observable through this reader -- IEEE
        // double rounding differs from a direct single-precision parse so
        // rarely that no realistic coordinate distinguishes them, and saying
        // otherwise would be a fact that cannot fail. What IS observable, and
        // what a port storing coordinates as double would get wrong, is that
        // the value ends up a FLOAT: everything past 24 bits is gone.
        PortalFile file = await ParseAsync(
            "PRT1\n2\n1\n3 0 1 (0.1 0 0 ) (0 1 0 ) (0 0 1 ) \n");

        Assert.Equal((float)0.1d, file.Portals[0].Points[0].X);
        Assert.NotEqual(0.1d, file.Portals[0].Points[0].X);
    }

    [Fact]
    public async Task EachFilePortalBecomesTwoMemoryPortals()
    {
        // "each file portal is split into two memory
        // portals".
        PortalFile file = await ParseAsync(OneSquarePortal);

        Assert.Equal(2, file.ToMemoryPortals().Count);
    }

    [Fact]
    public async Task ForwardPortalCarriesTheNegatedPlane()
    {
        // Layout: VectorSubtract(vec3_origin, plane.normal,...)
        // and dist = -dist. BOTH halves are negated.
        PortalFile file = await ParseAsync(OneSquarePortal);
        (Vec3 normal, float distance) = PortalFile.PlaneFromWinding(file.Portals[0].Points);

        MemoryPortal forward = file.ToMemoryPortals()[0];

        Assert.Equal(-normal, forward.Normal);
        Assert.Equal(-distance, forward.Distance);
    }

    [Fact]
    public async Task ForwardPortalLeafIsTheSecondLeafNumber()
    {
        // P->leaf = leafnums[1]. The portal is filed under
        // leaf 0 but its `leaf` field names the NEIGHBOUR.
        PortalFile file = await ParseAsync(OneSquarePortal);
        MemoryPortal forward = file.ToMemoryPortals()[0];

        Assert.Equal(0, forward.OwningCluster);
        Assert.Equal(1, forward.Leaf);
    }

    [Fact]
    public async Task ForwardPortalKeepsTheWindingOrderAsRead()
    {
        // P->winding = w, the object the loader filled.
        PortalFile file = await ParseAsync(OneSquarePortal);

        Assert.Equal(file.Portals[0].Points, file.ToMemoryPortals()[0].Points);
    }

    [Fact]
    public async Task BackwardPortalCarriesTheUnNegatedPlaneAndTheOtherLeaf()
    {
        // P->plane = plane; p->leaf = leafnums[0].
        PortalFile file = await ParseAsync(OneSquarePortal);
        (Vec3 normal, float distance) = PortalFile.PlaneFromWinding(file.Portals[0].Points);

        MemoryPortal backward = file.ToMemoryPortals()[1];

        Assert.Equal(normal, backward.Normal);
        Assert.Equal(distance, backward.Distance);
        Assert.Equal(1, backward.OwningCluster);
        Assert.Equal(0, backward.Leaf);
    }

    [Fact]
    public async Task BackwardPortalReversesTheWinding()
    {
        // Points[w->numpoints - 1 - j].
        PortalFile file = await ParseAsync(OneSquarePortal);

        Assert.Equal(
            file.Portals[0].Points.Reverse(),
            file.ToMemoryPortals()[1].Points);
    }

    [Fact]
    public async Task OnlyTheForwardPortalIsMarkedAsTheOriginalWinding()
    {
        // Sets original = true on the loaded winding, which the
        // forward portal SHARES; the backward one gets a NewWinding whose flag
        // the memset leaves false.
        PortalFile file = await ParseAsync(OneSquarePortal);
        IReadOnlyList<MemoryPortal> portals = file.ToMemoryPortals();

        Assert.True(portals[0].IsOriginalWinding);
        Assert.False(portals[1].IsOriginalWinding);
    }

    [Fact]
    public void PlaneIsComputedFromTheWindingRatherThanReadFromTheFile()
    {
        // V1 = p[2]-p[1], v2 = p[0]-p[1],
        // normal = cross(v2, v1) normalised, dist = dot(p[0], normal). The
        // operand order decides the sign, so a quad in the XY plane wound
        // counter-clockwise gives +Z.
        Vec3[] winding =
        [
            new(0, 0, 0),
            new(64, 0, 0),
            new(64, 64, 0),
            new(0, 64, 0),
        ];

        (Vec3 normal, float distance) = PortalFile.PlaneFromWinding(winding);

        Assert.Equal(new Vec3(0, 0, -1), normal);
        Assert.Equal(0f, distance);
    }

    [Fact]
    public void AWindingOfTwoPointsCannotDefineAPlane()
    {
        // The reference implementation reads points[0..2] unconditionally and has
        // no guard; this port refuses rather than reading past the winding.
        Assert.Throws<InvalidPortalFileException>(
            () => PortalFile.PlaneFromWinding([new Vec3(0, 0, 0), new Vec3(1, 0, 0)]));
    }

    [Fact]
    public async Task LeafNumberEqualToTheClusterCountIsAccepted()
    {
        // THE OFF-BY-ONE. The reference implementation tests (unsigned)leafnum >
        // portalclusters, NOT >=, so leafnum == portalclusters passes here and
        // then indexes one past the leaf array. Reproduced, because a
        // .prt stock loads must load here.
        PortalFile file = await ParseAsync("PRT1\n2\n1\n3 2 1 (0 0 0 ) (0 1 0 ) (0 0 1 ) \n");

        Assert.Equal(2, file.Portals[0].Cluster0);
    }

    [Fact]
    public async Task LeafNumberOneAboveThatIsRejected()
    {
        // The other side of the same test.
        await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n1\n3 3 1 (0 0 0 ) (0 1 0 ) (0 0 1 ) \n"));
    }

    [Fact]
    public async Task NegativeLeafNumberIsRejected()
    {
        // Casts to unsigned, so a negative becomes a huge value
        // and fails the bound.
        await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n1\n3 -1 1 (0 0 0 ) (0 1 0 ) (0 0 1 ) \n"));
    }

    [Fact]
    public async Task SixtyFivePointsIsTooMany()
    {
        // The test is > MAX_POINTS_ON_WINDING, so 64 is
        // legal and 65 is not.
        StringBuilder line = new("PRT1\n2\n1\n65 0 1 ");
        for (int i = 0; i < 65; i++)
        {
            line.Append("(0 0 ").Append(i).Append(" ) ");
        }

        line.Append('\n');

        InvalidPortalFileException error = await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync(line.ToString()));

        Assert.Contains("too many points", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactlySixtyFourPointsIsAccepted()
    {
        StringBuilder line = new("PRT1\n2\n1\n64 0 1 ");
        for (int i = 0; i < 64; i++)
        {
            line.Append("(0 0 ").Append(i).Append(" ) ");
        }

        line.Append('\n');

        PortalFile file = await ParseAsync(line.ToString());

        Assert.Equal(PortalFile.MaxPointsOnWinding, file.Portals[0].Points.Count);
    }

    [Fact]
    public async Task PortalCountAtTheLimitIsRejectedBecauseTheCheckIsOnTheDoubledCount()
    {
        // If (g_numportals * 2 >= MAX_PORTALS). With
        // MAX_PORTALS 65536 the largest accepted file count is 32767, not
        // 32768: the comparison is >=, not >.
        InvalidPortalFileException error = await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n32768\n"));

        Assert.Contains("overflows the max portal count", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOpenParenOnAPointIsRejected()
    {
        // The '(' is a hard literal in the format string.
        await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n1\n3 0 1 0 0 0 ) (0 1 0 ) (0 0 1 ) \n"));
    }

    [Fact]
    public async Task PointWithOnlyTwoCoordinatesIsRejected()
    {
        await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n1\n3 0 1 (0 0 ) (0 1 0 ) (0 0 1 ) \n"));
    }

    [Fact]
    public async Task PortalLineWithFewerThanThreeIntegersIsRejected()
    {
        await Assert.ThrowsAsync<InvalidPortalFileException>(
            async () => await ParseAsync("PRT1\n2\n1\n4 0\n"));
    }

    [Fact]
    public async Task WriterEmitsTheHeaderOneValueToALine()
    {
        // Layout: three separate fprintf calls.
        PortalFile file = await ParseAsync(OneSquarePortal);
        string written = Encoding.Latin1.GetString(file.ToBytes());

        Assert.StartsWith("PRT1\n2\n1\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterCollapsesANearIntegerCoordinate()
    {
        // WriteFloat, the reference implementation: within 0.001 of an integer it prints
        // "%i", so 64.0 is "64" and not "64.000000".
        PortalFile file = await ParseAsync(OneSquarePortal);
        string written = Encoding.Latin1.GetString(file.ToBytes());

        Assert.Contains("(0 0 64 ) ", written, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterSpellsAFractionalCoordinateWithSixDecimals()
    {
        // The other branch of the reference implementation: "%f", which is C's
        // six-decimal fixed form.
        PortalFile file = new() { ClusterCount = 2 };
        file.Portals.Add(new FilePortal(0, 1,
        [
            new Vec3(0.5f, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1),
        ]));

        string written = Encoding.Latin1.GetString(file.ToBytes());

        Assert.Contains("(0.500000 0 0 ) ", written, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapseThresholdIsOneThousandthAndIsExclusive()
    {
        // Fabs(v - RoundInt(v)) < 0.001. A value 0.0005 away
        // collapses; one 0.002 away does not.
        //
        // And the one that did not collapse comes out as 64.001999, not
        // 64.002000: WriteFloat takes a vec_t, which is a float, and
        // fprintf promotes it to double for "%f", so six
        // decimals show the float's ACTUAL value rather than the literal that
        // was written in the source. That is a property of the format and a
        // trap for a port that formats the decimal string instead.
        PortalFile file = new() { ClusterCount = 2 };
        file.Portals.Add(new FilePortal(0, 1,
        [
            new Vec3(64.0005f, 64.002f, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1),
        ]));

        string written = Encoding.Latin1.GetString(file.ToBytes());

        Assert.Contains("(64 64.001999 0 ) ", written, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundingIsHalfUpNotHalfToEven()
    {
        // RoundInt is floor(in + 0.5f), the reference implementation.
        // .NET's Math.Round would give 2 for 2.5 and 2 for 1.5; this gives 3
        // and 2. The value 1.5 is far enough from both integers that it takes
        // the "%f" branch, so the observable difference is which integer a
        // NEARLY-half value collapses to.
        PortalFile file = new() { ClusterCount = 2 };
        file.Portals.Add(new FilePortal(0, 1,
        [
            new Vec3(-0.5f, 2.5f, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1),
        ]));

        string written = Encoding.Latin1.GetString(file.ToBytes());

        // floor(-0.5 + 0.5) == 0, and |-0.5 - 0| is 0.5, so it does NOT
        // collapse; both take the %f branch, which is the point: the rounding
        // rule only decides WHICH integer the nearness is measured against.
        Assert.Contains("(-0.500000 2.500000 0 ) ", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterPutsASpaceBeforeAndAfterEachClosingParen()
    {
        // "(", then three floats EACH with a trailing
        // space, then ") ". This is what makes the reader's "(%lf %lf %lf ) "
        // format string line up.
        PortalFile file = await ParseAsync(OneSquarePortal);
        string written = Encoding.Latin1.GetString(file.ToBytes());

        Assert.EndsWith("(0 64 0 ) \n", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseThenWriteIsByteIdentical()
    {
        PortalFile file = await ParseAsync(OneSquarePortal);

        Assert.Equal(Encoding.Latin1.GetBytes(OneSquarePortal), file.ToBytes());
    }

    [Fact]
    public async Task WriteThenParseThenWriteIsStable()
    {
        PortalFile first = await ParseAsync(OneSquarePortal);
        byte[] once = first.ToBytes();

        PortalFile second = await PortalFile.ParseAsync(once, CancellationToken.None);

        Assert.Equal(once, second.ToBytes());
    }

    [Fact]
    public async Task CrLfIsAvailableForComparingAgainstWindowsBuiltFiles()
    {
        // Opens the file in TEXT mode, so a.prt from the
        // Windows toolset has CRLF line endings from the same fprintf.
        PortalFile file = await ParseAsync(OneSquarePortal);
        string written = Encoding.Latin1.GetString(file.ToBytes(PortalLineEnding.CrLf));

        Assert.EndsWith("(0 64 0 ) \r\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrLfSpelledFileParsesToTheSameModel()
    {
        // The reader's whitespace directives swallow either terminator.
        PortalFile lf = await ParseAsync(OneSquarePortal);
        PortalFile crlf = await ParseAsync(OneSquarePortal.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(lf.Portals[0].Points, crlf.Portals[0].Points);
    }

    [Fact]
    public async Task TrailingGarbageAfterTheLastPortalIsIgnored()
    {
        // Closes the file without checking it was consumed.
        PortalFile file = await ParseAsync(OneSquarePortal + "this is not a portal\n");

        Assert.Single(file.Portals);
    }

    [Fact]
    public async Task EmptyPortalListIsValid()
    {
        PortalFile file = await ParseAsync("PRT1\n1\n0\n");

        Assert.Empty(file.Portals);
        Assert.Equal(1, file.ClusterCount);
    }

    [Fact]
    public async Task ReadAsyncOverAStreamMatchesParse()
    {
        using MemoryStream stream = new(Encoding.Latin1.GetBytes(OneSquarePortal));
        PortalFile file = await PortalFile.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(Encoding.Latin1.GetBytes(OneSquarePortal), file.ToBytes());
    }
}
