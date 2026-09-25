using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// Neighbour finding and allowed vertices, <c>public/disp_common.cpp</c>,
/// on hand-built layouts.
/// </summary>
public sealed class DispNeighbourFinderTests
{
    private static readonly Vec3[] A = DispFixtures.UnitFloor();

    private static readonly Vec3[] Right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);

    private static readonly Vec3[] Diagonal = DispFixtures.FloorQuad(new Vec3(256, 256, 0), 256, 256);

    /// <summary>Boxes that share a face touch: <c>DoBBoxesTouch</c>, <c>disp_common.cpp:806</c>.</summary>
    [Fact]
    public void AbuttingBoxesTouch()
    {
        DispBox a = new(Vec3.Zero, new Vec3(1, 1, 1));
        DispBox b = new(new Vec3(1, 0, 0), new Vec3(2, 1, 1));

        Assert.True(DispNeighbourFinder.BoxesTouch(a, b));
    }

    /// <summary>Separated boxes do not touch.</summary>
    [Fact]
    public void SeparatedBoxesDoNotTouch()
    {
        DispBox a = new(Vec3.Zero, new Vec3(1, 1, 1));
        DispBox b = new(new Vec3(1.01f, 0, 0), new Vec3(2, 1, 1));

        Assert.False(DispNeighbourFinder.BoxesTouch(a, b));
    }

    /// <summary>
    /// The neighbour box is the base quad's, puffed by 0.1:
    /// <c>GetDispBox</c>, <c>disp_common.cpp:770-786</c>.
    /// </summary>
    [Fact]
    public void TheNeighbourBoxIsTheBaseQuadPuffed()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, A[0], (_, _) => 99), A);

        DispBox box = DispNeighbourFinder.GetDispBox(core);

        Assert.Equal(new Vec3(-0.1f, -0.1f, -0.1f), box.Min);
        Assert.Equal(new Vec3(256.1f, 256.1f, 0.1f), box.Max);
    }

    /// <summary>
    /// An edge is found only in its own winding direction:
    /// <c>FindEdge</c>, <c>disp_common.cpp:822</c>.
    /// </summary>
    [Fact]
    public void AnEdgeIsFoundInItsWindingDirection()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, A[0]), A);

        Assert.Equal(2, DispNeighbourFinder.FindEdge(core, A[2], A[3]));
    }

    /// <summary>The reversed pair is not an edge.</summary>
    [Fact]
    public void AReversedEdgeIsNotFound()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, A[0]), A);

        Assert.Equal(-1, DispNeighbourFinder.FindEdge(core, A[3], A[2]));
    }

    /// <summary>
    /// Point equality is per component within the tolerance, inclusive:
    /// <c>VectorsAreEqual</c>, <c>mathlib.h</c>.
    /// </summary>
    [Fact]
    public void VectorsWithinTheToleranceAreEqual()
    {
        Assert.True(DispNeighbourFinder.VectorsAreEqual(Vec3.Zero, new Vec3(0.005f, 0, 0), 0.01f));
        Assert.False(DispNeighbourFinder.VectorsAreEqual(Vec3.Zero, new Vec3(0.02f, 0, 0), 0.01f));
    }

    /// <summary>
    /// Two equal quads side by side are each other's whole-edge neighbours:
    /// <c>SetupEdgeNeighbors</c>, <c>disp_common.cpp:910</c>.
    /// </summary>
    [Fact]
    public void SideBySideQuadsAreEdgeNeighbours()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, A[0]), A), (DispFixtures.Heightfield(2, Right[0]), Right)]);

        DispSubNeighbor s = r[0].Info.EdgeNeighbors[(int)DispEdge.Right].SubNeighbors[0];

        Assert.Equal(1, s.Neighbor);
        Assert.Equal((byte)NeighborSpan.CornerToCorner, s.Span);
    }

    /// <summary>The relation is symmetric: the neighbour's left edge names the first.</summary>
    [Fact]
    public void TheEdgeRelationIsSymmetric()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, A[0]), A), (DispFixtures.Heightfield(2, Right[0]), Right)]);

        Assert.Equal(0, r[1].Info.EdgeNeighbors[(int)DispEdge.Left].SubNeighbors[0].Neighbor);
    }

    /// <summary>An edge with nothing beside it has no neighbour.</summary>
    [Fact]
    public void AnOpenEdgeHasNoNeighbour()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, A[0]), A), (DispFixtures.Heightfield(2, Right[0]), Right)]);

        Assert.False(r[0].Info.EdgeNeighbors[(int)DispEdge.Left].SubNeighbors[0].IsValid());
    }

    /// <summary>
    /// Two flat quads touching at one point are corner neighbours:
    /// <c>SetupCornerNeighbors</c>, <c>disp_common.cpp:979</c>.
    /// </summary>
    [Fact]
    public void QuadsMeetingAtAPointAreCornerNeighbours()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, A[0]), A), (DispFixtures.Heightfield(2, Diagonal[0]), Diagonal)]);

        DispCornerNeighbors c = r[0].Info.CornerNeighbors[(int)DispCorner.UpperRight];

        Assert.Equal(1, c.NumNeighbors);
        Assert.Equal(1, c.Neighbors[0]);
    }

    /// <summary>
    /// Corner matching reads DISPLACED corners, so a raised corner breaks it:
    /// <c>disp_common.cpp:1003</c> with <c>GetCornerPoint</c>.
    /// </summary>
    [Fact]
    public void ARaisedCornerIsNotACornerNeighbour()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
        [
            (DispFixtures.Heightfield(2, A[0], (x, y) => (x == 4 && y == 4) ? 1 : 0), A),
            (DispFixtures.Heightfield(2, Diagonal[0]), Diagonal),
        ]);

        Assert.Equal(0, r[0].Info.CornerNeighbors[(int)DispCorner.UpperRight].NumNeighbors);
    }

    /// <summary>
    /// Equal powers side by side keep every vertex:
    /// <c>SetupAllowedVerts</c>, <c>disp_common.cpp:1269</c>.
    /// </summary>
    [Fact]
    public void EqualPowersKeepEveryVertex()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(3, A[0]), A), (DispFixtures.Heightfield(3, Right[0]), Right)]);

        DispInfo info = r[0].Info;
        Assert.All(((ReadOnlySpan<uint>)info.AllowedVerts).ToArray(), w => Assert.Equal(0xFFFFFFFFu, w));
    }

    /// <summary>
    /// A finer displacement beside a coarser one loses the edge vertices the
    /// coarse one cannot match: <c>DisableUnallowedVerts_R</c>,
    /// <c>disp_common.cpp:1225</c>.
    /// </summary>
    [Fact]
    public void AFinerNeighbourLosesUnmatchedEdgeVertices()
    {
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(3, A[0]), A), (DispFixtures.Heightfield(2, Right[0]), Right)]);

        CoreDispInfo fine = r[0].Core;

        // (8, 1) is on the right edge at an odd step: the power-2 side has no partner.
        Assert.False(fine.AllowedVertsGet(DispFixtures.Index(fine, 8, 1)));
        Assert.True(fine.AllowedVertsGet(DispFixtures.Index(fine, 8, 2)));
    }
}
