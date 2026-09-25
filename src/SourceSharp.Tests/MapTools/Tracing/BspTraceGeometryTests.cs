using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// What the load-time flattening promises the walk, checked against a real
/// map.
/// </summary>
public sealed class BspTraceGeometryTests : IClassFixture<BspParityFixture>
{
    /// <summary><c>PLANE_Z</c> from <c>bspfile.h</c>: the last axial type.</summary>
    private const int PlaneZ = 2;

    private readonly BspParityFixture _fixture;

    /// <summary>Takes the shared flattened map.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is null.</exception>
    public BspTraceGeometryTests(BspParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>
    /// The axis classified from the normal agrees with the lump's own
    /// <c>type</c> field on every node of a real map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact behind the decision NOT to read <c>dplane_t.type</c>,
    /// which is what stock's shortcut branches on. Classifying from the normal
    /// can never change an answer -- an exactly axial normal makes the
    /// shortcut identical to the dot product -- while trusting <c>type</c>
    /// could, on a plane whose normal is nearly axial or axial and negative.
    /// The safe definition is only USEFUL if it fires as often as stock's, and
    /// this says it fires on exactly the same nodes here.
    /// </para>
    /// <para>
    /// If a map ever fails this, the shortcut is still correct and this port
    /// still agrees with itself; what it would mean is that stock takes the
    /// shortcut somewhere this port does not, and the parity facts are where
    /// that would show up as a number.
    /// </para>
    /// </remarks>
    [Fact]
    public void AxisFromTheNormalAgreesWithThePlaneTypeField()
    {
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(_fixture.Bsp[BspLump.Planes]);
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(_fixture.Bsp[BspLump.Nodes]);

        int disagreements = 0;
        int axialNodes = 0;
        for (int n = 0; n < nodes.Length; n++)
        {
            int stockAxis = planes[nodes[n].PlaneNum].Type;
            int ourAxis = _fixture.Geometry.NodeAxis(n);
            bool stockSaysAxial = stockAxis <= PlaneZ;
            bool weSayAxial = ourAxis <= PlaneZ;

            if (weSayAxial)
            {
                axialNodes++;
            }

            if (stockSaysAxial != weSayAxial || (weSayAxial && stockAxis != ourAxis))
            {
                disagreements++;
            }
        }

        Assert.Equal(0, disagreements);

        // And the shortcut has to be worth having, which it is not if almost
        // no node takes it.
        Assert.True(
            axialNodes > nodes.Length / 4,
            $"only {axialNodes} of {nodes.Length} nodes are axial, so the shortcut in the "
            + "descent loop is not paying for its branch");
    }

    /// <summary>
    /// Every node's candidate run stays inside the node-face list.
    /// </summary>
    /// <remarks>
    /// The walk indexes that list with <c>Unsafe.Add</c> and no bounds check,
    /// on the argument that the build validated the indices once. This is that
    /// validation, stated as a fact rather than left as a claim in a comment.
    /// </remarks>
    [Fact]
    public void EveryNodesCandidateRunIsInsideTheNodeFaceList()
    {
        int total = _fixture.Geometry.NodeCandidateCount;
        for (int n = 0; n < _fixture.Geometry.NodeCount; n++)
        {
            (int first, int solid, int sky) = _fixture.Geometry.NodeFaceRun(n);
            Assert.InRange(first, 0, total);
            Assert.InRange(first + solid + sky, 0, total);
        }
    }

    /// <summary>
    /// Every leaf's candidate run stays inside the leaf-face list.
    /// </summary>
    [Fact]
    public void EveryLeafsCandidateRunIsInsideTheLeafFaceList()
    {
        int total = _fixture.Geometry.LeafCandidateCount;
        for (int l = 0; l < _fixture.Geometry.LeafCount; l++)
        {
            (int first, int count) = _fixture.Geometry.LeafFaceRun(l);
            Assert.InRange(first, 0, total);
            Assert.InRange(first + count, 0, total);
        }
    }

    /// <summary>
    /// Every candidate names a face the map has.
    /// </summary>
    [Fact]
    public void EveryCandidateNamesARealFace()
    {
        int faces = _fixture.Geometry.SurfaceCount;
        for (int i = 0; i < _fixture.Geometry.NodeCandidateCount; i++)
        {
            Assert.InRange(_fixture.Geometry.NodeCandidate(i), 0, faces - 1);
        }

        for (int i = 0; i < _fixture.Geometry.LeafCandidateCount; i++)
        {
            Assert.InRange(_fixture.Geometry.LeafCandidate(i), 0, faces - 1);
        }
    }

    /// <summary>
    /// Every child index names a node the map has, or a leaf it has.
    /// </summary>
    [Fact]
    public void EveryChildNamesARealNodeOrLeaf()
    {
        for (int n = 0; n < _fixture.Geometry.NodeCount; n++)
        {
            (int child0, int child1) = _fixture.Geometry.NodeChildren(n);
            foreach (int child in new[] { child0, child1 })
            {
                if (child >= 0)
                {
                    Assert.InRange(child, 0, _fixture.Geometry.NodeCount - 1);
                }
                else
                {
                    Assert.InRange(-child - 1, 0, _fixture.Geometry.LeafCount - 1);
                }
            }
        }
    }

    /// <summary>
    /// A map whose lumps name indices it does not have is refused, not traced.
    /// </summary>
    /// <remarks>
    /// This is the red half of the bounds-check argument. The walk trusts the
    /// build; the build has to earn it, and a fact that only ever sees a
    /// well-formed map cannot show that it does.
    /// </remarks>
    [Fact]
    public void ANodeNamingAMissingPlaneIsRefused()
    {
        BspData broken = CloneWithBrokenNodePlane(_fixture.Bsp);
        InvalidBspException error =
            Assert.Throws<InvalidBspException>(() => BspTraceGeometry.Build(broken));
        Assert.Contains("names plane", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Displacement faces are counted rather than quietly dropped.
    /// </summary>
    /// <remarks>
    /// The number is the size of this tracer's known hole: stock clips every
    /// leaf against displacements and this does not. A map with none is a map
    /// where the two are comparable; a map with some is not, and the caller
    /// needs to be able to tell which it has.
    /// </remarks>
    [Fact]
    public void DisplacementFacesAreCountedRatherThanIgnored()
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(_fixture.Bsp[BspLump.Faces]);
        int expected = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].DispInfo != -1)
            {
                expected++;
            }
        }

        Assert.Equal(expected, _fixture.Geometry.SkippedDisplacementFaces);
    }

    private static BspData CloneWithBrokenNodePlane(BspData source)
    {
        BspData copy = new()
        {
            FileVersion = source.FileVersion,
            MapRevision = source.MapRevision,
        };

        for (int slot = 0; slot < BspData.HeaderLumps; slot++)
        {
            copy[slot] = source[slot];
        }

        BspLumpData original = source[BspLump.Nodes];
        byte[] bytes = original.Data.ToArray();
        Span<DNode> nodes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, DNode>(bytes.AsSpan());
        nodes[0].PlaneNum = int.MaxValue;
        copy[BspLump.Nodes] =
            new BspLumpData(bytes, original.Version, original.UncompressedSize);
        return copy;
    }
}
