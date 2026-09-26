//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The grid walkers of the reference implementation and the
/// <c>CVertIndex</c> arithmetic they run on.
/// </summary>
public sealed class DispIteratorTests
{
    /// <summary>
    /// The circumference walk visits each boundary vertex once, starting at
    /// (0, 0) and going up the left edge: <c>CDispCircumferenceIterator</c>.
    /// </summary>
    [Fact]
    public void TheCircumferenceVisitsEachBoundaryVertexOnce()
    {
        DispCircumferenceIterator it = new(5);
        List<VertIndex> seen = [];
        while (it.Next())
        {
            seen.Add(it.VertIndex);
        }

        Assert.Equal(16, seen.Count);
        Assert.Equal(16, seen.Distinct().Count());
        Assert.Equal(new VertIndex(0, 0), seen[0]);
        Assert.Equal(new VertIndex(0, 1), seen[1]);
    }

    /// <summary>
    /// An edge with no neighbour yields nothing: <c>CDispSubEdgeIterator::Start</c>'s
    /// "setup so Next returns false".
    /// </summary>
    [Fact]
    public void AnEdgeWithoutANeighbourYieldsNothing()
    {
        CoreDispInfo core = new(2);
        core.SetListBase([core]);
        DispSubEdgeIterator it = default;

        it.Start(core, 0, 0);

        Assert.False(it.Next());
        Assert.Null(it.Neighbor);
    }

    /// <summary>
    /// A whole-edge walk between equal neighbours visits the interior
    /// vertices, paired with the neighbour's:.
    /// </summary>
    [Fact]
    public void AnEdgeWalkPairsEachVertexWithItsNeighbours()
    {
        Vec3[] right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Vec3.Zero), DispFixtures.UnitFloor()),
             (DispFixtures.Heightfield(2, right[0]), right)]);
        DispSubEdgeIterator it = default;
        it.Start(r[0].Core, (int)DispEdge.Right, 0);
        List<(VertIndex Mine, VertIndex Theirs)> pairs = [];

        while (it.Next())
        {
            pairs.Add((it.VertIndex, it.NeighborVertIndex));
        }

        Assert.Equal(3, pairs.Count);
        Assert.Equal((new VertIndex(4, 1), new VertIndex(0, 1)), pairs[0]);
    }

    /// <summary>With <c>bTouchCorners</c> the walk includes both end corners:.</summary>
    [Fact]
    public void TouchingCornersAddsBothEnds()
    {
        Vec3[] right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Vec3.Zero), DispFixtures.UnitFloor()),
             (DispFixtures.Heightfield(2, right[0]), right)]);
        DispSubEdgeIterator it = default;
        it.Start(r[0].Core, (int)DispEdge.Right, 0, touchCorners: true);
        int count = 0;

        while (it.Next())
        {
            count++;
        }

        Assert.Equal(5, count);
    }

    /// <summary>The last vertex of a walk reports itself: <c>IsLastVert</c>.</summary>
    [Fact]
    public void TheLastVertexOfAWalkReportsItself()
    {
        Vec3[] right = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);
        (IReadOnlyList<DisplacementResult> r, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Vec3.Zero), DispFixtures.UnitFloor()),
             (DispFixtures.Heightfield(2, right[0]), right)]);
        DispSubEdgeIterator it = default;
        it.Start(r[0].Core, (int)DispEdge.Right, 0);
        List<bool> last = [];

        while (it.Next())
        {
            last.Add(it.IsLastVert());
        }

        Assert.Equal([false, false, true], last);
    }

    /// <summary><c>CVertIndex</c> adds and subtracts per component.</summary>
    [Fact]
    public void VertIndicesAddPerComponent()
    {
        Assert.Equal(new VertIndex(4, -1), new VertIndex(3, 1) + new VertIndex(1, -2));
        Assert.Equal(new VertIndex(2, 3), new VertIndex(3, 1) - new VertIndex(1, -2));
    }

    /// <summary>The indexer reads x at 0 and y at 1: <c>CVertIndex::operator[]</c>.</summary>
    [Fact]
    public void TheIndexerReadsXThenY()
    {
        VertIndex v = new(7, 9);

        Assert.Equal(7, v[0]);
        Assert.Equal(9, v[1]);
    }

    /// <summary><c>With</c> replaces one component.</summary>
    [Fact]
    public void WithReplacesOneComponent()
    {
        Assert.Equal(new VertIndex(7, 2), new VertIndex(7, 9).With(1, 2));
    }
}
