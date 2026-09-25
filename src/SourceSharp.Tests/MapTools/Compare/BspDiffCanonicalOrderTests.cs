using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// The same geometry in a different index order: equal under the canonical
/// kind, unequal as bytes.
/// </summary>
/// <remarks>
/// This is the whole reason <see cref="DiffKind.CanonicalSet"/> exists. Nothing
/// in the BSP format requires a particular plane order or a particular starting
/// edge for a face, so two compilers that agree about the map can disagree
/// about both; an instrument that called that a difference would fail the port
/// for being written in C# rather than for being wrong.
/// </remarks>
public sealed class BspDiffCanonicalOrderTests : IClassFixture<LockdownDiffFixture>
{
    private readonly LockdownDiffFixture _map;

    /// <summary>Takes the loaded golden map.</summary>
    /// <param name="map">The fixture.</param>
    public BspDiffCanonicalOrderTests(LockdownDiffFixture map) => _map = map;

    [Fact]
    public async Task PermutedPlanesCompareEqualUnderTheCanonicalKind()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = PermutePlanePairs(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff planes = report.For(BspLump.Planes);
        Assert.Equal(DiffKind.CanonicalSet, planes.Kind);
        Assert.True(planes.Identical, planes.ToText());
    }

    [Fact]
    public async Task PermutedPlanesDoNotCompareEqualAsBytes()
    {
        // The other half of the same fact. Without it, "identical" could mean
        // the permutation never happened.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = PermutePlanePairs(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.False(report.For(BspLump.Planes).BytesIdentical);
        Assert.False(report.BytesIdentical);
    }

    [Fact]
    public async Task PermutedPlanesLeaveTheFacesCanonicallyEqualToo()
    {
        // The face key holds the plane's VALUE, not its number, which is what
        // lets a renumbering pass through the face comparison untouched.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = PermutePlanePairs(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.True(report.For(BspLump.Faces).Identical, report.For(BspLump.Faces).ToText());
        Assert.False(report.For(BspLump.Faces).BytesIdentical);
    }

    [Fact]
    public async Task PermutedPlanesDoShowUpInAnExactLump()
    {
        // BRUSHSIDES stores plane NUMBERS and is compared exactly, so the
        // renumbering is visible there. That is the contrast the canonical kind
        // exists against, stated as a fact rather than as prose.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = PermutePlanePairs(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff sides = report.For(BspLump.BrushSides);
        Assert.Equal(DiffKind.Exact, sides.Kind);
        Assert.False(sides.Identical);
    }

    [Fact]
    public async Task AFaceRingThatStartsAtADifferentEdgeIsTheSameFace()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = RotateOneFaceRing(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.True(report.For(BspLump.Faces).Identical, report.For(BspLump.Faces).ToText());
        Assert.False(report.For(BspLump.SurfEdges).BytesIdentical);
    }

    [Fact]
    public async Task AFaceWithAMOVEDVertexIsNotTheSameFace()
    {
        // The control on the rotation fact: a canonicalisation that threw the
        // ring away entirely would also make a rotated ring compare equal.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        MoveAVertexFaceZeroStandsOn(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.False(report.For(BspLump.Faces).Identical);
    }

    /// <summary>
    /// Moves a vertex that face 0's ring actually walks through, so the change
    /// is guaranteed to reach the face comparison.
    /// </summary>
    private static void MoveAVertexFaceZeroStandsOn(BspData bsp)
    {
        DFace face = BspStructView.As<DFace>(bsp[BspLump.Faces])[0];
        Assert.True(face.NumEdges > 0, "face 0 has edges");

        int surfEdge = BspStructView.As<int>(bsp[BspLump.SurfEdges])[face.FirstEdge];
        DEdge edge = BspStructView.As<DEdge>(bsp[BspLump.Edges])[Math.Abs(surfEdge)];
        ushort vertex = surfEdge >= 0 ? edge.V[0] : edge.V[1];

        Span<Vec3> vertexes = DiffMaps.MutableLump<Vec3>(bsp, BspLump.Vertexes);
        vertexes[vertex] = new Vec3(vertexes[vertex].X + 8.0f, vertexes[vertex].Y, vertexes[vertex].Z);
    }

    /// <summary>
    /// Reverses the order of the plane PAIRS and remaps every stored plane
    /// number to match.
    /// </summary>
    /// <remarks>
    /// Pairs, not individual planes: vbsp emits every plane beside its own
    /// negation and the engine reaches the second as <c>planenum ^ 1</c>, so
    /// splitting a pair would produce a map that is not merely reordered. The
    /// remap covers every lump in <c>dm_lockdown.bsp</c> that stores a plane
    /// number -- faces, original faces, nodes, brush sides and areaportals.
    /// </remarks>
    private static BspData PermutePlanePairs(BspData source)
    {
        BspData bsp = DiffMaps.Clone(source);

        ReadOnlySpan<DPlane> original = BspStructView.As<DPlane>(source[BspLump.Planes]);
        int count = original.Length;
        Assert.True(count > 0 && count % 2 == 0, "dm_lockdown.bsp has an even number of planes");

        int pairs = count / 2;
        int[] map = new int[count];
        for (int pair = 0; pair < pairs; pair++)
        {
            int target = pairs - 1 - pair;
            map[(pair * 2) + 0] = (target * 2) + 0;
            map[(pair * 2) + 1] = (target * 2) + 1;
        }

        Span<DPlane> permuted = DiffMaps.MutableLump<DPlane>(bsp, BspLump.Planes);
        for (int i = 0; i < count; i++)
        {
            permuted[map[i]] = original[i];
        }

        foreach (BspLump faceLump in new[] { BspLump.Faces, BspLump.OriginalFaces, BspLump.FacesHdr })
        {
            if (bsp[faceLump].IsEmpty)
            {
                continue;
            }

            Span<DFace> faces = DiffMaps.MutableLump<DFace>(bsp, faceLump);
            for (int i = 0; i < faces.Length; i++)
            {
                faces[i].PlaneNum = (ushort)map[faces[i].PlaneNum];
            }
        }

        Span<DNode> nodes = DiffMaps.MutableLump<DNode>(bsp, BspLump.Nodes);
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i].PlaneNum = map[nodes[i].PlaneNum];
        }

        Span<DBrushSide> sides = DiffMaps.MutableLump<DBrushSide>(bsp, BspLump.BrushSides);
        for (int i = 0; i < sides.Length; i++)
        {
            sides[i].PlaneNum = (ushort)map[sides[i].PlaneNum];
        }

        if (!bsp[BspLump.AreaPortals].IsEmpty)
        {
            Span<DAreaPortal> portals = DiffMaps.MutableLump<DAreaPortal>(bsp, BspLump.AreaPortals);
            for (int i = 0; i < portals.Length; i++)
            {
                portals[i].PlaneNum = map[portals[i].PlaneNum];
            }
        }

        return bsp;
    }

    /// <summary>
    /// Rotates one face's run of surfedges by one, so its ring starts at a
    /// different vertex and winds the same way.
    /// </summary>
    private static BspData RotateOneFaceRing(BspData source)
    {
        BspData bsp = DiffMaps.Clone(source);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);

        int first = -1;
        int edges = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].NumEdges >= 3)
            {
                first = faces[i].FirstEdge;
                edges = faces[i].NumEdges;
                break;
            }
        }

        Assert.True(first >= 0, "dm_lockdown.bsp has a face with at least three edges");

        Span<int> surfEdges = DiffMaps.MutableLump<int>(bsp, BspLump.SurfEdges);
        int head = surfEdges[first];
        for (int i = 0; i < edges - 1; i++)
        {
            surfEdges[first + i] = surfEdges[first + i + 1];
        }

        surfEdges[first + edges - 1] = head;
        return bsp;
    }
}
