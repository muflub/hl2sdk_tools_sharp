//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// <see cref="StockQuirk.VbspVectorNormalise"/> at its three face-stage sites:
/// the subdivider's split, the t-junction fixer's edge direction, and the
/// merge test's edge normals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every fact here searches for its input.</b> The Stock side is the
/// CPU's <c>rsqrtss</c> (or ARM's <c>frsqrte</c>) refined once, and which
/// inputs it rounds differently from an exact divide is itself
/// CPU-dependent: a vector that separates the two sides on an Intel part may
/// normalise identically on an AMD one. A fixed input would therefore prove
/// the switch on one runner and silently prove nothing on another. Each fact
/// instead scans a fixed, deterministic family of inputs, requires the site
/// to agree with its own side's arithmetic on every one it runs, and requires
/// at least one input where the two sides' outcomes differ, so the fact fails
/// rather than passes vacuously on a CPU where the switch could not be seen.
/// </para>
/// <para>
/// The expected values are computed here from the same public operations the
/// site uses (<see cref="Vec3.NormaliseLikeStock"/>, <see cref="Vec3.Normalise"/>,
/// <see cref="Vec3.Dot"/>, a clip in a fresh <see cref="WindingArena"/>), so
/// "the Stock side equals the estimate" and "the Correct side equals the exact
/// divide" are bit-for-bit statements.
/// </para>
/// </remarks>
public sealed class VbspVectorNormaliseTests
{
    // ---------------------------------------------------------------- subdivision

    /// <summary>
    /// Under Stock, the first cut of an oversized face lands where the
    /// estimate's normal and luxels-per-unit put it, and on some lightmap axis
    /// that is not where the exact divide puts it.
    /// </summary>
    [Fact]
    public void StockSubdivisionCutsAtTheEstimatedDistance()
    {
        int differing = 0;

        foreach (Vec3 axis in LightmapAxes())
        {
            Vec3[] actual = FirstBackPiece(ComplianceOptions.Stock, axis);

            Assert.Equal(ExpectedBackPiece(axis, stock: true), actual);
            differing += actual.SequenceEqual(ExpectedBackPiece(axis, stock: false)) ? 0 : 1;
        }

        Assert.True(differing > 0, "no lightmap axis separates the estimate from the exact divide on this CPU");
    }

    /// <summary>
    /// Under Correct, the first cut lands where the exact divide puts it, and
    /// on some lightmap axis that is not where the estimate would.
    /// </summary>
    [Fact]
    public void CorrectSubdivisionCutsAtTheExactDistance()
    {
        int differing = 0;

        foreach (Vec3 axis in LightmapAxes())
        {
            Vec3[] actual = FirstBackPiece(ComplianceOptions.Correct, axis);

            Assert.Equal(ExpectedBackPiece(axis, stock: false), actual);
            differing += actual.SequenceEqual(ExpectedBackPiece(axis, stock: true)) ? 0 : 1;
        }

        Assert.True(differing > 0, "no lightmap axis separates the estimate from the exact divide on this CPU");
    }

    /// <summary>
    /// The quirk alone moves the cut: Correct with only this quirk flipped
    /// is the Stock side's arithmetic.
    /// </summary>
    [Fact]
    public void FlippingOnlyThisQuirkGivesTheSubdividerTheEstimate()
    {
        ComplianceOptions flipped = ComplianceOptions.Correct.Flipping(StockQuirk.VbspVectorNormalise);

        foreach (Vec3 axis in LightmapAxes())
        {
            Assert.Equal(ExpectedBackPiece(axis, stock: true), FirstBackPiece(flipped, axis));
        }
    }

    /// <summary>
    /// A family of lightmap u axes: non-axial, of assorted lengths, all in the
    /// z = 0 plane so they measure the test square, and all positive so the
    /// square's minimum projection is its origin corner.
    /// </summary>
    private static IEnumerable<Vec3> LightmapAxes()
    {
        for (int i = 1; i <= 48; i++)
        {
            yield return new Vec3((i * 0.013f) + 0.02f, ((49 - i) * 0.007f) + 0.011f, 0f);
        }

        // The axial ones every real map is full of, at lightmapscale 16 and 1.
        yield return new Vec3(1f / 16f, 0f, 0f);
        yield return new Vec3(1f, 0f, 0f);
    }

    private static readonly Vec3[] Square =
    [
        new Vec3(0f, 0f, 0f),
        new Vec3(1024f, 0f, 0f),
        new Vec3(1024f, 1024f, 0f),
        new Vec3(0f, 1024f, 0f),
    ];

    /// <summary>
    /// Subdivides the 1024-unit square under one lightmap axis and returns the
    /// back piece of the FIRST cut, which the subdivider hangs on
    /// <c>Split[1]</c> before recursing. The v axis is zero, so nothing is
    /// cut along it and the first cut is always the u one.
    /// </summary>
    private static Vec3[] FirstBackPiece(ComplianceOptions compliance, Vec3 axis)
    {
        FaceBuildContext context = FaceStageFixture.Create(compliance: compliance);

        TexInfo tex = default;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = axis.X;
        tex.LightmapVecsLuxelsPerWorldUnits[1] = axis.Y;
        tex.LightmapVecsLuxelsPerWorldUnits[2] = axis.Z;
        int index = context.TexInfos.Add(tex);

        Face face = FaceStageFixture.Face(context, Square);
        face.TexInfo = index;

        new FaceSubdivider(context).SubdivideFace(face, face);

        Assert.True(face.IsDead, "the square was not subdivided");
        return FaceStageFixture.Points(context, face.Split[1]!);
    }

    /// <summary>
    /// The first cut, computed from the subdivider's own formula:
    /// <c>dist = (mins + 32 - 1) / luxelsPerWorldUnit</c> along the normalised
    /// axis, clipped with the same epsilon.
    /// </summary>
    private static Vec3[] ExpectedBackPiece(Vec3 axis, bool stock)
    {
        float mins = 999999f;
        foreach (Vec3 p in Square)
        {
            mins = MathF.Min(mins, Vec3.Dot(p, axis));
        }

        (Vec3 normal, float luxelsPerWorldUnit) = stock ? axis.NormaliseLikeStock() : axis.Normalise();
        float dist = (mins + 32f - 1f) / luxelsPerWorldUnit;

        WindingArena arena = new();
        arena.ClipEpsilon(
            arena.Create(Square), normal, dist, GeometryEpsilons.OnEpsilonFloat, out _, out Winding back);
        return [.. arena.Points(back)];
    }

    // ---------------------------------------------------------------- t-junctions

    /// <summary>
    /// Under Stock, whether a vertex a quarter unit off an edge splits it is
    /// decided on the estimated edge direction, and for some vertex that is
    /// not the exact direction's answer.
    /// </summary>
    [Fact]
    public void StockEdgeFixingProjectsOntoTheEstimatedDirection()
    {
        (Vec3 end, Vec3 vertex) = FindEdgeStraddle();
        bool splits = EdgeSplits(end, vertex, stock: true);

        (int tjunctions, int points) = FixOneEdge(ComplianceOptions.Stock, end, vertex);

        Assert.Equal(splits ? 1 : 0, tjunctions);
        Assert.Equal(splits ? 4 : 3, points);
        Assert.NotEqual(EdgeSplits(end, vertex, stock: false), splits);
    }

    /// <summary>
    /// Under Correct, the same decision is taken on the exact direction, and
    /// comes out the other way for the vertex the estimate decides wrongly.
    /// </summary>
    [Fact]
    public void CorrectEdgeFixingProjectsOntoTheExactDirection()
    {
        (Vec3 end, Vec3 vertex) = FindEdgeStraddle();
        bool splits = EdgeSplits(end, vertex, stock: false);

        (int tjunctions, int points) = FixOneEdge(ComplianceOptions.Correct, end, vertex);

        Assert.Equal(splits ? 1 : 0, tjunctions);
        Assert.Equal(splits ? 4 : 3, points);
        Assert.NotEqual(EdgeSplits(end, vertex, stock: true), splits);
    }

    /// <summary>
    /// A vertex well inside the tolerance splits the edge on both sides: the
    /// switch moves only the boundary cases.
    /// </summary>
    [Fact]
    public void AVertexSquarelyOnTheEdgeSplitsItUnderBothPolicies()
    {
        Vec3 end = new(20f, 9f, 0f);
        Vec3 vertex = new(10.1f, 4.5f, 0f);

        Assert.Equal((1, 4), FixOneEdge(ComplianceOptions.Stock, end, vertex));
        Assert.Equal((1, 4), FixOneEdge(ComplianceOptions.Correct, end, vertex));
    }

    /// <summary>
    /// Whether the fixer's <c>TestEdge</c> splits the edge from the origin to
    /// <paramref name="end"/> at <paramref name="vertex"/>, computed with the
    /// fixer's own operations: the projection must fall strictly inside the
    /// edge and the rejection must be within <see cref="TJunctionFixer.OffEpsilon"/>.
    /// </summary>
    private static bool EdgeSplits(Vec3 end, Vec3 vertex, bool stock)
    {
        Vec3 start = Vec3.Zero;
        (Vec3 dir, float len) = stock ? (end - start).NormaliseLikeStock() : (end - start).Normalise();
        float dist = Vec3.Dot(vertex - start, dir);

        if (dist <= 0f || dist >= len)
        {
            return false;
        }

        Vec3 exact = start + (dir * dist);
        float error = (vertex - exact).Length();
        return !(error > TJunctionFixer.OffEpsilon);
    }

    /// <summary>
    /// Finds an edge and a vertex about a quarter unit off its middle whose
    /// split the two arithmetics decide differently. The vertex is stepped a
    /// float ulp at a time, and the edges are non-axial so that the estimate
    /// turns the direction rather than only scaling it.
    /// </summary>
    private static (Vec3 End, Vec3 Vertex) FindEdgeStraddle()
    {
        (float X, float Y)[] ends = [(20, 9), (17, 11), (23, 7), (13, 19), (29, 5), (11, 27), (31, 13), (19, 23)];

        foreach ((float ex, float ey) in ends)
        {
            Vec3 end = new(ex, ey, 0f);
            Vec3 outward = new Vec3(ey, -ex, 0f).Normalise().Normalised;
            Vec3 centre = (end * 0.5f) + (outward * 0.25f);

            for (int i = -16; i <= 16; i++)
            {
                for (int j = -16; j <= 16; j++)
                {
                    Vec3 vertex = new(StepUlps(centre.X, i), StepUlps(centre.Y, j), 0f);

                    if (EdgeSplits(end, vertex, stock: true) != EdgeSplits(end, vertex, stock: false))
                    {
                        return (end, vertex);
                    }
                }
            }
        }

        Assert.Fail("no vertex near any edge separates the estimate from the exact divide on this CPU");
        return default;
    }

    /// <summary>
    /// Fixes the first edge of the triangle (origin, <paramref name="end"/>,
    /// (0, 40)) with a second face that owns <paramref name="vertex"/> and two
    /// points well below the edge.
    /// </summary>
    private static (int TJunctions, int Points) FixOneEdge(ComplianceOptions compliance, Vec3 end, Vec3 vertex)
    {
        FaceBuildContext context = FaceStageFixture.Create(compliance: compliance);
        TJunctionFixer fixer = new(context);

        Face triangle = FaceStageFixture.Face(context, [Vec3.Zero, end, new Vec3(0f, 40f, 0f)]);
        Face other = FaceStageFixture.Face(context,
        [
            vertex,
            vertex + new Vec3(0f, -5f, 0f),
            vertex + new Vec3(5f, -5f, 0f),
        ]);

        Face? head = triangle;
        triangle.Next = other;
        fixer.EmitFaceVertexes(ref head, triangle);
        fixer.EmitFaceVertexes(ref head, other);

        // The weld must have stored the vertex as given, or the prediction
        // above was made for a different point.
        Assert.Contains(vertex, context.Vertices.Vertexes);

        fixer.FixFaceEdges(ref head, triangle);
        return (context.Counters.TJunctions, triangle.NumPoints);
    }

    // ---------------------------------------------------------------- merging

    /// <summary>
    /// Under Stock, the merge test's convexity check at the shared edge takes
    /// the estimated edge normal, and for some nearly collinear point that
    /// decides the merge differently from the exact normal.
    /// </summary>
    [Fact]
    public void StockMergeTestTakesTheEstimatedNormal()
    {
        (Vec3 a, Vec3 b) = FindMergeStraddle();
        bool merges = Merges(a, b, stock: true);

        Assert.Equal(merges, !TryMerge(ComplianceOptions.Stock, a, b).IsNull);
        Assert.NotEqual(Merges(a, b, stock: false), merges);
    }

    /// <summary>
    /// Under Correct, the check takes the exact normal, and decides the same
    /// point the other way.
    /// </summary>
    [Fact]
    public void CorrectMergeTestTakesTheExactNormal()
    {
        (Vec3 a, Vec3 b) = FindMergeStraddle();
        bool merges = Merges(a, b, stock: false);

        Assert.Equal(merges, !TryMerge(ComplianceOptions.Correct, a, b).IsNull);
        Assert.NotEqual(Merges(a, b, stock: true), merges);
    }

    private static readonly Vec3 PlaneNormal = new(0f, 0f, -1f);

    private static readonly Vec3 SharedStart = Vec3.Zero;

    private static readonly Vec3 SharedEnd = new(10f, 0f, 0f);

    /// <summary>
    /// Whether <see cref="FaceMerger.TryMergeWinding"/> merges the triangles
    /// (start, end, <paramref name="a"/>) and (end, start, <paramref name="b"/>),
    /// computed with the merge test's own operations: the cross of the plane
    /// normal with the edge that meets each end of the shared edge,
    /// normalised, dotted with the other triangle's point, against
    /// <see cref="FaceMerger.ContinuousEpsilon"/>.
    /// </summary>
    private static bool Merges(Vec3 a, Vec3 b, bool stock)
    {
        Vec3 normal = Normalise(Vec3.Cross(PlaneNormal, SharedStart - a), stock);
        double dot = Vec3.Dot(b - SharedStart, normal);

        if (dot > FaceMerger.ContinuousEpsilon)
        {
            return false;
        }

        normal = Normalise(Vec3.Cross(PlaneNormal, a - SharedEnd), stock);
        dot = Vec3.Dot(b - SharedEnd, normal);
        return !(dot > FaceMerger.ContinuousEpsilon);
    }

    private static Vec3 Normalise(Vec3 v, bool stock) =>
        stock ? v.NormaliseLikeStock().Normalised : v.Normalise().Normalised;

    /// <summary>
    /// Finds a pair of triangles on either side of the shared edge whose far
    /// points are CONTINUOUS_EPSILON short of collinear through the shared
    /// start, stepped a float ulp at a time until the two arithmetics decide
    /// the convexity check differently.
    /// </summary>
    private static (Vec3 A, Vec3 B) FindMergeStraddle()
    {
        (float X, float Y)[] backs = [(-3, 7), (-5, 11), (-7, 3), (-2, 13), (-11, 6), (-9, 17), (-13, 5), (-6, 19)];

        foreach ((float ax, float ay) in backs)
        {
            Vec3 a = new(ax, ay, 0f);
            Vec3 along = (SharedStart - a).Normalise().Normalised;
            Vec3 across = Vec3.Cross(PlaneNormal, SharedStart - a).Normalise().Normalised;
            Vec3 centre = SharedStart + (along * 5f) + (across * (float)FaceMerger.ContinuousEpsilon);

            for (int i = -16; i <= 16; i++)
            {
                for (int j = -16; j <= 16; j++)
                {
                    Vec3 b = new(StepUlps(centre.X, i), StepUlps(centre.Y, j), 0f);

                    if (Merges(a, b, stock: true) != Merges(a, b, stock: false))
                    {
                        return (a, b);
                    }
                }
            }
        }

        Assert.Fail("no point near collinear separates the estimate from the exact divide on this CPU");
        return default;
    }

    private static Winding TryMerge(ComplianceOptions compliance, Vec3 a, Vec3 b)
    {
        FaceBuildContext context = FaceStageFixture.Create(compliance: compliance);
        FaceMerger merger = new(context);

        Winding w1 = context.Windings.Create([SharedStart, SharedEnd, a]);
        Winding w2 = context.Windings.Create([SharedEnd, SharedStart, b]);
        return merger.TryMergeWinding(w1, w2, PlaneNormal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The float <paramref name="steps"/> representable values away from <paramref name="value"/>.</summary>
    private static float StepUlps(float value, int steps)
    {
        for (; steps > 0; steps--)
        {
            value = MathF.BitIncrement(value);
        }

        for (; steps < 0; steps++)
        {
            value = MathF.BitDecrement(value);
        }

        return value;
    }
}
