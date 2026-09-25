using SourceSharp.MapTools.Bsp.Faces;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Sharing an edge between the two faces that meet along it
///(<c>GetEdge2</c>).
/// </summary>
public class EdgeTableTests
{
    [Fact]
    public void AFreshEdgeGetsANonNegativeIndex()
    {
        EdgeTable edges = Table(out FaceCounters counters);
        Face f = Face(1);

        Assert.Equal(0, edges.GetEdge(4, 5, f, noShare: false));
        Assert.Equal(1, counters.TryEdges);
    }

    [Fact]
    public void TheReverseOfAnEdgeSharesItAndComesBackNegated()
    {
        EdgeTable edges = Table(out _);

        int forward = edges.GetEdge(4, 5, Face(1), noShare: false);
        int backward = edges.GetEdge(5, 4, Face(1), noShare: false);

        Assert.Equal(1, edges.Count);
        Assert.Equal(-forward, backward);
    }

    [Fact]
    public void ASharedEdgeRecordsBothFaces()
    {
        EdgeTable edges = Table(out _);

        Face a = Face(1);
        Face b = Face(1);

        int index = edges.GetEdge(4, 5, a, noShare: false);
        edges.GetEdge(5, 4, b, noShare: false);

        Assert.Same(a, edges.FacesOf(index)[0]);
        Assert.Same(b, edges.FacesOf(index)[1]);
    }

    [Fact]
    public void AThirdFaceOnTheSameLineGetsItsOwnEdge()
    {
        EdgeTable edges = Table(out _);

        edges.GetEdge(4, 5, Face(1), noShare: false);
        edges.GetEdge(5, 4, Face(1), noShare: false);

        // Both slots are taken, so the third has to make a new edge.
        Assert.Equal(1, edges.GetEdge(5, 4, Face(1), noShare: false));
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void FacesOfDifferentContentsNeverShareAnEdge()
    {
        EdgeTable edges = Table(out _);

        edges.GetEdge(4, 5, Face(1), noShare: false);

        Assert.Equal(1, edges.GetEdge(5, 4, Face(2), noShare: false));
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void TheSameDirectionIsNotAShareableEdge()
    {
        EdgeTable edges = Table(out _);

        edges.GetEdge(4, 5, Face(1), noShare: false);

        // Only the exact reverse matches: two faces wound the same way along
        // one line are on the same side of it.
        Assert.Equal(1, edges.GetEdge(4, 5, Face(1), noShare: false));
    }

    [Fact]
    public void NoShareAlwaysMakesANewEdge()
    {
        EdgeTable edges = Table(out _);

        edges.GetEdge(4, 5, Face(1), noShare: true);

        Assert.Equal(1, edges.GetEdge(5, 4, Face(1), noShare: true));
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void TheVertexIndexSurvivesUntilItIsResetAndNotAfter()
    {
        EdgeTable edges = Table(out _);

        edges.GetEdge(4, 5, Face(1), noShare: false);
        edges.ResetLookup();

        // The edge is still in the table, but nothing can find it to share.
        Assert.Equal(1, edges.GetEdge(5, 4, Face(1), noShare: false));
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void AnEdgeRemembersItsTwoVertices()
    {
        EdgeTable edges = Table(out _);

        int index = edges.AddEdge(4, 5, Face(1));

        Assert.Equal(4, edges.Edges[index].V[0]);
        Assert.Equal(5, edges.Edges[index].V[1]);
    }

    // FindReverseEdge is CreateOrigFace's back-edge scan:
    // for j from firstmodeledge, the first edge stored (v1, v0) whose first
    // face has the asking contents and whose second slot is free.

    [Fact]
    public void ReverseEdgeOfTwoFreeCandidatesIsTheLowerIndex()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0)); // the reserved edge 0
        edges.AddEdge(5, 4, Face(1));
        edges.AddEdge(7, 8, Face(1));
        edges.AddEdge(5, 4, Face(1));

        Assert.Equal(1, edges.FindReverseEdge(4, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeSkipsACandidateWhoseSecondSlotIsTaken()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(5, 4, Face(1));
        edges.AddEdge(5, 4, Face(1));
        edges.ShareEdge(1, Face(1));

        // stock's "check for multiple backward edges" continue
        Assert.Equal(2, edges.FindReverseEdge(4, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeSkipsACandidateOfOtherContents()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(5, 4, Face(2));
        edges.AddEdge(5, 4, Face(1));

        Assert.Equal(2, edges.FindReverseEdge(4, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeNeverReturnsAnEdgeBeforeTheFirstModelEdge()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(5, 4, Face(1)); // the previous model's
        edges.AddEdge(5, 4, Face(1));

        Assert.Equal(2, edges.FindReverseEdge(4, 5, 1, firstEdge: 2));
    }

    [Fact]
    public void ReverseEdgePastTheLastEdgeIsNotFound()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(5, 4, Face(1));

        Assert.Equal(-1, edges.FindReverseEdge(4, 5, 1, firstEdge: 2));
    }

    [Fact]
    public void ReverseEdgeIgnoresAnEdgeWoundTheSameWay()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(4, 5, Face(1));

        Assert.Equal(-1, edges.FindReverseEdge(4, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeSeesAnEdgeMadeByGetEdgeAfterALookupReset()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.GetEdge(5, 4, Face(1), noShare: false);
        edges.ResetLookup();

        // The scan reads dedges, not GetEdge2's per-model vertex index.
        Assert.Equal(1, edges.FindReverseEdge(4, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeOfAVertexBeyondAShortMatchesNothing()
    {
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        edges.AddEdge(5, 4, Face(1));

        // stock compares an int with the stored unsigned short
        Assert.Equal(-1, edges.FindReverseEdge(4 + 65536, 5, 1, firstEdge: 1));
    }

    [Fact]
    public void ReverseEdgeAgreesWithStocksLinearScanOnRandomTraffic()
    {
        var random = new Random(12345);
        EdgeTable edges = Table(out _);
        edges.AddEdge(0, 0, Face(0));
        int firstEdge = 1;
        int mismatches = 0, shared = 0, made = 0;

        for (int step = 0; step < 20000; step++)
        {
            if (random.Next(500) == 0)
            {
                firstEdge = edges.Count; // a new model
            }

            int v0 = random.Next(12);
            int v1 = random.Next(12);
            Face f = Face(random.Next(3));

            int expected = LinearScan(edges, v0, v1, f.Contents, firstEdge);
            int actual = edges.FindReverseEdge(v0, v1, f.Contents, firstEdge);
            if (expected != actual)
            {
                mismatches++;
            }

            if (expected != -1)
            {
                edges.ShareEdge(expected, f);
                shared++;
            }
            else
            {
                edges.AddEdge(v0, v1, f);
                made++;
            }
        }

        // both roads were exercised, and the index never chose differently
        Assert.True(shared > 1000 && made > 1000, $"shared {shared}, made {made}");
        Assert.Equal(0, mismatches);
    }

    //, verbatim
    private static int LinearScan(EdgeTable edges, int e0, int e1, int contents, int firstEdge)
    {
        for (int j = firstEdge; j < edges.Count; j++)
        {
            if (e0 == edges.Edges[j].V[1] && e1 == edges.Edges[j].V[0] && edges.FacesOf(j)[0]!.Contents == contents)
            {
                if (edges.FacesOf(j)[1] is not null)
                {
                    continue;
                }

                return j;
            }
        }

        return -1;
    }

    private static EdgeTable Table(out FaceCounters counters)
    {
        counters = new FaceCounters();
        return new EdgeTable(counters);
    }

    private static Face Face(int contents) => new(0) { Contents = contents };
}
