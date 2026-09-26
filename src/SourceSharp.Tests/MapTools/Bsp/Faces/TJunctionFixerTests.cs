//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Welding a face's points and splitting its edges at t-junctions
///(<c>FixFaceEdges</c>).
/// </summary>
public class TJunctionFixerTests
{
    [Fact]
    public void AFaceWithNoTJunctionsKeepsItsFourVertices()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        Face face = Square(context, 0f, 64f);
        Face? head = face;
        fixer.EmitFaceVertexes(ref head, face);
        fixer.FixFaceEdges(ref head, face);

        Assert.Equal(4, face.NumPoints);
        Assert.Equal(0, context.Counters.TJunctions);
    }

    [Fact]
    public void AVertexInTheMiddleOfAnEdgeIsAddedToIt()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        // A 128-wide square, and a smaller square butted against the middle of
        // its bottom edge so that (64,0) is a t-junction on it.
        Face big = Square(context, 0f, 128f);
        Face small = FaceStageFixture.Face(context,
        [
            new Vec3(0f, -64f, 0f),
            new Vec3(64f, -64f, 0f),
            new Vec3(64f, 0f, 0f),
            new Vec3(0f, 0f, 0f),
        ]);

        Face? head = big;
        big.Next = small;

        fixer.EmitFaceVertexes(ref head, big);
        fixer.EmitFaceVertexes(ref head, small);
        fixer.FixFaceEdges(ref head, big);

        Assert.Equal(5, big.NumPoints);
        Assert.Equal(1, context.Counters.TJunctions);
    }

    [Fact]
    public void TheAddedVertexIsTheOneThatCausedTheJunction()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        Face big = Square(context, 0f, 128f);
        Face small = FaceStageFixture.Face(context,
        [
            new Vec3(0f, -64f, 0f),
            new Vec3(64f, -64f, 0f),
            new Vec3(64f, 0f, 0f),
            new Vec3(0f, 0f, 0f),
        ]);

        Face? head = big;
        big.Next = small;
        fixer.EmitFaceVertexes(ref head, big);
        fixer.EmitFaceVertexes(ref head, small);
        fixer.FixFaceEdges(ref head, big);

        bool found = false;

        foreach (int v in FaceStageFixture.VertexNumbers(big))
        {
            Vec3 p = context.Vertices[v];

            if (p.X == 64f && p.Y == 0f)
            {
                found = true;
            }
        }

        Assert.True(found, "the t-junction vertex is not in the fixed face");
    }

    [Fact]
    public void AFaceWholeOnTopOfAnotherCollapsesToNothing()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        // A degenerate "face" whose four points weld to one vertex: every edge
        // is p1 == p2, so nothing is emitted and the face collapses.
        Face face = FaceStageFixture.Face(context,
        [
            new Vec3(10f, 10f, 0f),
            new Vec3(10.01f, 10f, 0f),
            new Vec3(10f, 10.01f, 0f),
            new Vec3(10.01f, 10.01f, 0f),
        ]);

        Face? head = face;
        fixer.EmitFaceVertexes(ref head, face);
        fixer.FixFaceEdges(ref head, face);

        Assert.Equal(0, face.NumPoints);
        Assert.Equal(1, context.Counters.CollapsedFaces);
        Assert.Equal(4, context.Counters.DegenerateEdges);
    }

    [Fact]
    public void AFaceWithTJunctionsOnEveryEdgeHasNoCleanStartingVertex()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        Face big = Square(context, 0f, 128f);
        Face? head = big;
        fixer.EmitFaceVertexes(ref head, big);

        // Put a welded vertex in the middle of all four edges by emitting a
        // diamond whose corners sit there.
        Face diamond = FaceStageFixture.Face(context,
        [
            new Vec3(64f, 0f, 0f),
            new Vec3(128f, 64f, 0f),
            new Vec3(64f, 128f, 0f),
            new Vec3(0f, 64f, 0f),
        ]);

        big.Next = diamond;
        fixer.EmitFaceVertexes(ref head, diamond);
        fixer.FixFaceEdges(ref head, big);

        Assert.True(big.BadStartVert);
        Assert.Equal(1, context.Counters.BadStartVerts);
        Assert.Equal(8, big.NumPoints);
    }

    [Fact]
    public void ABadStartVertexOnTheWorldBuildsACrackSewingPrimitive()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        context.EntityNumber = 0;
        TJunctionFixer fixer = new(context);

        Face big = Square(context, 0f, 128f);
        Face? head = big;
        fixer.EmitFaceVertexes(ref head, big);

        Face diamond = FaceStageFixture.Face(context,
        [
            new Vec3(64f, 0f, 0f),
            new Vec3(128f, 64f, 0f),
            new Vec3(64f, 128f, 0f),
            new Vec3(0f, 64f, 0f),
        ]);

        big.Next = diamond;
        fixer.EmitFaceVertexes(ref head, diamond);
        fixer.FixFaceEdges(ref head, big);

        Assert.Equal(1, big.NumPrims);
        Assert.Single(context.Primitives.Primitives);

        // An 8-gon fans into 6 triangles, so 18 indices.
        Assert.Equal(18, context.Primitives.Indices.Count);
    }

    [Fact]
    public void ABadStartVertexOnABrushModelBuildsNoPrimitive()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        context.EntityNumber = 1;
        TJunctionFixer fixer = new(context);

        Face big = Square(context, 0f, 128f);
        Face? head = big;
        fixer.EmitFaceVertexes(ref head, big);

        Face diamond = FaceStageFixture.Face(context,
        [
            new Vec3(64f, 0f, 0f),
            new Vec3(128f, 64f, 0f),
            new Vec3(64f, 128f, 0f),
            new Vec3(0f, 64f, 0f),
        ]);

        big.Next = diamond;
        fixer.EmitFaceVertexes(ref head, diamond);
        fixer.FixFaceEdges(ref head, big);

        Assert.True(big.BadStartVert);
        Assert.Empty(context.Primitives.Primitives);
    }

    [Fact]
    public void AFaceWithMoreThanThirtyTwoVerticesIsFragmented()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        // A 40-gon: EmitFaceVertexes welds 40 superverts and FaceFromSuperverts
        // has to split the face because MAXEDGES is 32.
        Vec3[] points = new Vec3[40];

        for (int i = 0; i < points.Length; i++)
        {
            double angle = 2.0 * Math.PI * i / points.Length;
            points[i] = new Vec3(
                (float)(512.0 * Math.Cos(angle)), (float)(512.0 * Math.Sin(angle)), 0f);
        }

        Face face = FaceStageFixture.Face(context, points);
        Face? head = face;
        fixer.EmitFaceVertexes(ref head, face);

        Assert.True(face.IsDead, "a fragmented face must be marked split");
        Assert.Equal(1, context.Counters.FaceOverflows);

        int live = 0;

        for (Face? f = head; f is not null; f = f.Next)
        {
            if (!f.IsDead)
            {
                live++;
                Assert.InRange(f.NumPoints, 1, Face.MaxEdges);
            }
        }

        Assert.Equal(2, live);
    }

    [Fact]
    public void AnEdgeWithTooManyVerticesIsAnError()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);

        // MAX_SUPERVERTS points strung along one edge of a long face.
        Face bar = FaceStageFixture.Face(context,
        [
            new Vec3(0f, 0f, 0f),
            new Vec3(1200f, 0f, 0f),
            new Vec3(1200f, 64f, 0f),
            new Vec3(0f, 64f, 0f),
        ]);

        Face? head = bar;
        fixer.EmitFaceVertexes(ref head, bar);

        for (int i = 1; i < 600; i++)
        {
            context.Vertices.GetVertexNumber(
                new Vec3(i * 2f, 0f, 0f));
        }

        Assert.Throws<InvalidOperationException>(() => fixer.FixFaceEdges(ref head, bar));
    }

    // ---- Triangulate_r --------------------------------------------------

    [Fact]
    public void TriangulatingATriangleReturnsItUnchanged()
    {
        List<int> output = [];
        FaceVertEdges[] poly = [Edges(0, 2), Edges(0, 1), Edges(1, 2)];

        TJunctionFixer.TriangulateRecursive(output, [0, 1, 2], poly);

        Assert.Equal([0, 1, 2], output);
    }

    [Fact]
    public void TriangulatingASquareProducesTwoTriangles()
    {
        List<int> output = [];
        FaceVertEdges[] poly = [Edges(0, 3), Edges(0, 1), Edges(1, 2), Edges(2, 3)];

        TJunctionFixer.TriangulateRecursive(output, [0, 1, 2, 3], poly);

        Assert.Equal(6, output.Count);
    }

    [Fact]
    public void TwoVerticesSharingAnEdgeAreNotADiagonal()
    {
        Assert.False(TJunctionFixer.IsDiagonal(Edges(0, 1), Edges(1, 2)));
    }

    [Fact]
    public void TwoVerticesSharingNoEdgeAreADiagonal()
    {
        Assert.True(TJunctionFixer.IsDiagonal(Edges(0, 1), Edges(2, 3)));
    }

    [Fact]
    public void TheNoEdgeMarkerIsNeverAMatch()
    {
        // Both vertices are on no edge at all, which must not read as "both on
        // edge -1" and therefore not a diagonal.
        Assert.True(TJunctionFixer.IsDiagonal(FaceVertEdges.Empty, FaceVertEdges.Empty));
    }

    [Fact]
    public void APolygonWithNoDiagonalIsAnError()
    {
        // Every vertex on the same two edges: nothing can be cut.
        FaceVertEdges[] poly = [Edges(0, 1), Edges(0, 1), Edges(0, 1), Edges(0, 1)];

        Assert.Throws<InvalidOperationException>(
            () => TJunctionFixer.TriangulateRecursive([], [0, 1, 2, 3], poly));
    }

    // ---- FixTjuncs ordering ---------------------------------------------

    [Fact]
    public void FixTjuncsDoesNotResetTheBadStartVertCount()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);
        context.Counters.BadStartVerts = 5;

        // A bare leaf: FixTjuncs walks it, finds nothing, and the only
        // observable is what it reset on the way in.
        fixer.FixTjuncs(new SourceSharp.MapTools.Bsp.Portals.BspNode(0), null);

        Assert.Equal(5, context.Counters.BadStartVerts);
    }

    [Fact]
    public void FixTjuncsKeepsThePreviousModelsVertices()
    {
        // clears hashverts/vertexchain only; numvertexes
        // runs on across models, so the world's vertices survive a submodel's
        // FixTjuncs and the submodel numbers after them.
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);
        context.Vertices.GetVertexNumber(new Vec3(7f, 7f, 7f));

        fixer.FixTjuncs(new SourceSharp.MapTools.Bsp.Portals.BspNode(0), null);

        // the error vertex 0 and the (7,7,7) the "previous model" emitted
        Assert.Equal(2, context.Vertices.Count);
    }

    [Fact]
    public void FixTjuncsForgetsThePreviousModelsWeld()
    {
        // The same point in the next model gets a NEW vertex: the hash that
        // would have found the old one was cleared.
        FaceBuildContext context = FaceStageFixture.Create();
        TJunctionFixer fixer = new(context);
        int first = context.Vertices.GetVertexNumber(new Vec3(7f, 7f, 7f));

        fixer.FixTjuncs(new SourceSharp.MapTools.Bsp.Portals.BspNode(0), null);
        int second = context.Vertices.GetVertexNumber(new Vec3(7f, 7f, 7f));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AReservedErrorVertexMakesTheFirstWeldedVertexOne()
    {
        // BeginBSPFile: numvertexes = 1, "leave vertex 0 as an error".
        FaceBuildContext context = FaceStageFixture.Create();

        Assert.Equal(1, context.Vertices.GetVertexNumber(new Vec3(7f, 7f, 7f)));
    }

    [Fact]
    public void AReservedErrorVertexIsNeverWeldedOnto()
    {
        FaceBuildContext context = FaceStageFixture.Create();

        Assert.NotEqual(0, context.Vertices.GetVertexNumber(Vec3.Zero));
    }

    [Fact]
    public void ResetLeavesOnlyTheErrorVertex()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        context.Vertices.GetVertexNumber(new Vec3(7f, 7f, 7f));

        context.Vertices.Reset();

        Assert.Equal(1, context.Vertices.Count);
    }

    private static FaceVertEdges Edges(int a, int b) => new(a, b);

    private static Face Square(FaceBuildContext context, float lo, float hi) =>
        FaceStageFixture.Face(context,
        [
            new Vec3(lo, lo, 0f),
            new Vec3(hi, lo, 0f),
            new Vec3(hi, hi, 0f),
            new Vec3(lo, hi, 0f),
        ]);
}
