using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <c>dm_lockdown</c>'s displacements, tessellated and loaded, built once for
/// the class.
/// </summary>
/// <remarks>
/// Reading the map and walking forty height fields is not expensive, but every
/// fact below is read-only over the result and there are a dozen of them.
/// </remarks>
public sealed class DisplacementCasterFixture
{
    /// <summary>Loads the map and runs the displacement load path over it.</summary>
    public DisplacementCasterFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        Bsp = BspFile.LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Surfaces = DisplacementShadowCasters.Build(Bsp);

        ShadowCasterBuilder builder = new();
        DisplacementShadowCasters.Add(Bsp, builder);
        Casters = builder.Build();
    }

    /// <summary>The loaded map.</summary>
    public BspData Bsp { get; }

    /// <summary>Every displacement, in LUMP_DISPINFO order.</summary>
    public IReadOnlyList<DisplacementSurface> Surfaces { get; }

    /// <summary>The caster triangles the displacements contributed.</summary>
    public ShadowCasterSet Casters { get; }

    /// <summary>The displacement run's count and bounds.</summary>
    public ShadowCasterStats Stats => Casters.Stats(ShadowCasterSource.Displacement);
}

/// <summary>
/// The displacement half of the 4b load gate, against stock's own numbers for
/// <c>dm_lockdown</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two recorded numbers are a count and a bounding box, and neither is
/// sufficient alone. A count is blind to where the geometry is -- a surface
/// built from the wrong start corner has exactly the right count -- and a
/// bounding box is blind to everything inside it. So the facts below that
/// carry no stock number are not padding: they gate the per-displacement
/// structure that the two aggregate numbers average away.
/// </para>
/// <para>
/// Stock printed the bounds to two decimal places, so they are compared to
/// 0.01 and the count is compared exactly. Comparing the bounds exactly would
/// be asserting digits nobody recorded.
/// </para>
/// </remarks>
public sealed class DisplacementShadowCasterTests : IClassFixture<DisplacementCasterFixture>
{
    private readonly DisplacementCasterFixture _fixture;

    /// <summary>Takes the shared map.</summary>
    /// <param name="fixture">The fixture.</param>
    public DisplacementShadowCasterTests(DisplacementCasterFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    /// <summary>The bounds tolerance stock's own printing forces.</summary>
    private const double BoundsTolerance = 0.01;

    /// <summary><c>dm_lockdown</c> holds forty displacements.</summary>
    [Fact]
    public void LockdownHasFortyDisplacements()
    {
        Assert.Equal(40, _fixture.Surfaces.Count);
    }

    /// <summary>
    /// Thirty-seven of them are power 2 and three are power 3.
    /// </summary>
    /// <remarks>
    /// Measured from LUMP_DISPINFO, not inferred from the triangle total. The
    /// inference is tempting -- <c>32x + 128y = 1568</c> with <c>x + y = 40</c>
    /// has the unique non-negative solution (37, 3) -- but it is only unique
    /// because no power-4 displacement is in the map, and a fact that would
    /// have to be re-derived if one appeared is not a fact about the map. It
    /// also fixes the SHAPE of the total: 1,568 is two different displacement
    /// sizes and not one, so the per-power relation below is exercised at two
    /// powers rather than one.
    /// </remarks>
    [Fact]
    public void ThirtySevenDisplacementsArePowerTwoAndThreeArePowerThree()
    {
        Dictionary<int, int> histogram = [];
        foreach (DisplacementSurface surface in _fixture.Surfaces)
        {
            histogram[surface.Power] = histogram.GetValueOrDefault(surface.Power) + 1;
        }

        Assert.Equal(new Dictionary<int, int> { [2] = 37, [3] = 3 }, histogram);
    }

    /// <summary>Stock loads 1,568 displacement caster triangles.</summary>
    [Fact]
    public void TriangleCountMatchesStock()
    {
        Assert.Equal(1568, _fixture.Stats.Triangles);
    }

    /// <summary>Their lower bound is stock's, to the two places stock printed.</summary>
    [Fact]
    public void LowerBoundMatchesStock()
    {
        Vec3 min = _fixture.Stats.Min;

        Assert.Equal(-4160.0, min.X, BoundsTolerance);
        Assert.Equal(1344.0, min.Y, BoundsTolerance);
        Assert.Equal(-134.04, min.Z, BoundsTolerance);
    }

    /// <summary>Their upper bound is stock's, to the two places stock printed.</summary>
    [Fact]
    public void UpperBoundMatchesStock()
    {
        Vec3 max = _fixture.Stats.Max;

        Assert.Equal(-2764.0, max.X, BoundsTolerance);
        Assert.Equal(5572.0, max.Y, BoundsTolerance);
        Assert.Equal(128.0, max.Z, BoundsTolerance);
    }

    /// <summary>
    /// Every displacement tessellates into <c>2^p * 2^p * 2</c> triangles.
    /// </summary>
    /// <remarks>
    /// <c>GetTriSize</c>, <c>dispcoll_common.h:311</c>. This is the relation
    /// the aggregate count cannot see: 1,568 is also what 49 power-2
    /// displacements would give.
    /// </remarks>
    [Fact]
    public void EachDisplacementHasTwoTrianglesPerCell()
    {
        Assert.All(
            _fixture.Surfaces,
            surface => Assert.Equal(
                (1 << surface.Power) * (1 << surface.Power) * 2,
                surface.TriangleCount));
    }

    /// <summary>
    /// Every displacement has <c>(2^p + 1)^2</c> vertices.
    /// </summary>
    /// <remarks>
    /// <c>GetSize</c>, <c>dispcoll_common.h:305</c>. The <c>+1</c> is the
    /// difference between posts and cells, and getting it wrong is a whole
    /// missing row and column that the triangle count would still accept --
    /// the indices would simply run off the end.
    /// </remarks>
    [Fact]
    public void EachDisplacementHasPostSpacingSquaredVertices()
    {
        Assert.All(
            _fixture.Surfaces,
            surface => Assert.Equal(
                surface.PostSpacing * surface.PostSpacing,
                surface.Vertices.Length));
    }

    /// <summary>No triangle names a vertex the displacement does not have.</summary>
    [Fact]
    public void EveryTriangleIndexIsInRange()
    {
        foreach (DisplacementSurface surface in _fixture.Surfaces)
        {
            int count = surface.Vertices.Length;
            foreach (int index in surface.TriangleIndices)
            {
                Assert.InRange(index, 0, count - 1);
            }
        }
    }

    /// <summary>
    /// Every triangle of a displacement uses three distinct vertices.
    /// </summary>
    /// <remarks>
    /// Cheap, and it is the one structural error the index generator could make
    /// that stays inside the bounds check above: a wrong <c>nWidth</c> stride
    /// in <c>BuildTriTLtoBR</c> would fold a row onto itself.
    /// </remarks>
    [Fact]
    public void EveryTriangleUsesThreeDistinctVertices()
    {
        foreach (DisplacementSurface surface in _fixture.Surfaces)
        {
            ReadOnlySpan<int> indices = surface.TriangleIndices;
            for (int t = 0; t < indices.Length; t += 3)
            {
                Assert.NotEqual(indices[t], indices[t + 1]);
                Assert.NotEqual(indices[t + 1], indices[t + 2]);
                Assert.NotEqual(indices[t], indices[t + 2]);
            }
        }
    }

    /// <summary>
    /// Every vertex sits inside its own base face's bounds, grown by the
    /// largest distance that displacement's field moves a vertex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact that would catch a surface built on the wrong FACE, or
    /// with the field vector applied with its components permuted, or with the
    /// bilinear interpolation run over the corners in the wrong order -- none
    /// of which the aggregate bounding box can see, because the aggregate is
    /// the union over the whole map and a displacement landing on top of its
    /// neighbour stays inside it.
    /// </para>
    /// <para>
    /// The bound is sound rather than tight: a vertex cannot leave the
    /// quadrilateral's own bounding box before the field is applied, because
    /// the flat vertex is a convex combination of the four corners, and the
    /// field then moves it by at most
    /// <see cref="DisplacementSurface.MaxDisplacement"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryVertexLiesInsideItsFaceBoundsPlusTheDisplacement()
    {
        foreach (DisplacementSurface surface in _fixture.Surfaces)
        {
            ReadOnlySpan<Vec3> corners = surface.CornerPoints;

            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float minZ = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            foreach (Vec3 corner in corners)
            {
                minX = MathF.Min(minX, corner.X);
                minY = MathF.Min(minY, corner.Y);
                minZ = MathF.Min(minZ, corner.Z);
                maxX = MathF.Max(maxX, corner.X);
                maxY = MathF.Max(maxY, corner.Y);
                maxZ = MathF.Max(maxZ, corner.Z);
            }

            // A hair of slack for the float path, on top of the displacement
            // itself: the flat vertex is accumulated rather than lerped, so a
            // corner post can land a fraction of a unit outside the corner it
            // was built from.
            float slack = surface.MaxDisplacement + 0.01f;

            foreach (Vec3 vertex in surface.Vertices)
            {
                Assert.InRange(vertex.X, minX - slack, maxX + slack);
                Assert.InRange(vertex.Y, minY - slack, maxY + slack);
                Assert.InRange(vertex.Z, minZ - slack, maxZ + slack);
            }
        }
    }

    /// <summary>
    /// Corner 0 of every displacement is the face corner nearest its
    /// <see cref="DispInfo.StartPosition"/>.
    /// </summary>
    /// <remarks>
    /// Re-derived here straight from the lump, rather than trusting the same
    /// code that built the surface. A rotated start corner is the silent
    /// failure of this port: it leaves the count, the per-power relation and
    /// even the map-wide bounding box intact and puts every vertex in the
    /// wrong place.
    /// </remarks>
    [Fact]
    public void CornerZeroIsTheCornerNearestTheStartPosition()
    {
        ReadOnlySpan<DispInfo> dispInfo =
            BspStructView.As<DispInfo>(_fixture.Bsp[BspLump.DispInfo]);

        for (int d = 0; d < _fixture.Surfaces.Count; d++)
        {
            DisplacementSurface surface = _fixture.Surfaces[d];
            Vec3 start = dispInfo[d].StartPosition;

            float nearest = float.PositiveInfinity;
            foreach (Vec3 corner in surface.CornerPoints)
            {
                nearest = MathF.Min(nearest, (start - corner).LengthSquared());
            }

            Assert.Equal(nearest, (start - surface.CornerPoints[0]).LengthSquared());
        }
    }

    /// <summary>Every displacement caster carries the bare opaque id.</summary>
    /// <remarks>
    /// <c>vrad_dispcoll.cpp:1078</c> passes <c>TRACE_ID_OPAQUE</c> and nothing
    /// else -- no displacement index, unlike the static props. So a shadow ray
    /// cannot skip the displacement it started on, and displacement
    /// self-shadowing in stock is handled by the sample offset instead.
    /// </remarks>
    [Fact]
    public void EveryCasterCarriesTheOpaqueId()
    {
        foreach (TracedTriangle triangle in _fixture.Casters.Triangles)
        {
            Assert.Equal(TraceId.Opaque, triangle.Id);
        }
    }

    /// <summary>Every displacement caster blocks light completely.</summary>
    /// <remarks>
    /// <c>fullCoverage.x = 1.0f</c> (<c>vrad_dispcoll.cpp:1076</c>), and the
    /// other two components are left UNINITIALISED -- which is safe only
    /// because the tracer reads nothing but <c>x</c>.
    /// </remarks>
    [Fact]
    public void EveryCasterHasFullCoverage()
    {
        foreach (float coverage in _fixture.Casters.Coverage)
        {
            Assert.Equal(1.0f, coverage);
        }
    }

    /// <summary>No displacement caster is alpha-tested.</summary>
    /// <remarks>
    /// Displacements are the one load path with no texture-shadow support at
    /// all: <c>AddPolysForRayTrace</c> passes no material index and no flags,
    /// so a displacement painted with an alpha-tested material casts the shadow
    /// of the whole triangle. Pinned so that adding the support later is a
    /// decision.
    /// </remarks>
    [Fact]
    public void NoCasterIsTransparent()
    {
        Assert.Equal(0, _fixture.Casters.TransparentCount);
        Assert.All(
            _fixture.Casters.MaterialIndices.ToArray(),
            index => Assert.Equal(-1, index));
    }

    /// <summary>
    /// Every displacement in <c>dm_lockdown</c> is opaque, so the map cannot
    /// exercise the skip.
    /// </summary>
    /// <remarks>
    /// Stated as a fact rather than assumed, because it is what makes the two
    /// synthetic facts below necessary: if the map ever gains a non-opaque
    /// displacement this fact fails and the recorded 1,568 stops meaning what
    /// it means today.
    /// </remarks>
    [Fact]
    public void EveryLockdownDisplacementIsOpaque()
    {
        Assert.All(_fixture.Surfaces, surface => Assert.True(surface.IsOpaque));
        Assert.All(
            _fixture.Surfaces,
            surface => Assert.Equal(BrushContents.Solid, surface.Contents));
    }

    /// <summary>An opaque displacement contributes all of its triangles.</summary>
    [Fact]
    public void AnOpaqueDisplacementContributesEveryTriangle()
    {
        ShadowCasterBuilder builder = new();
        DisplacementShadowCasters.Add([Synthetic(BrushContents.Solid)], builder);

        Assert.Equal(32, builder.Count);
    }

    /// <summary>A non-opaque displacement contributes nothing.</summary>
    /// <remarks>
    /// The other half of <c>vrad_dispcoll.cpp:1066</c>, and the half no map in
    /// this tree can reach. Without it the early-out could be missing entirely
    /// and every fact above would still pass.
    /// </remarks>
    [Fact]
    public void ANonOpaqueDisplacementContributesNothing()
    {
        ShadowCasterBuilder builder = new();
        DisplacementShadowCasters.Add([Synthetic(BrushContents.Empty)], builder);

        Assert.Equal(0, builder.Count);
    }

    /// <summary>
    /// A grate is solid to movement and casts no shadow.
    /// </summary>
    /// <remarks>
    /// The reason the mask is <c>MASK_OPAQUE</c> and not <c>MASK_SOLID</c>,
    /// spelled out: the two differ by <c>CONTENTS_GRATE</c> and
    /// <c>CONTENTS_WINDOW</c>, and using the wrong one would darken every
    /// grating in every map.
    /// </remarks>
    [Fact]
    public void AGrateDisplacementContributesNothing()
    {
        ShadowCasterBuilder builder = new();
        DisplacementShadowCasters.Add([Synthetic(BrushContents.Grate)], builder);

        Assert.Equal(0, builder.Count);
    }

    /// <summary>
    /// The start corner is the nearest of the four, not the first exact match.
    /// </summary>
    /// <remarks>
    /// Driven with a start position that matches NO corner exactly, which is
    /// the real case: vbsp wrote it through a different float path from the
    /// vertices, so an equality test would find nothing and stock's loop would
    /// have to fall back on corner 0.
    /// </remarks>
    [Fact]
    public void TheStartCornerIsTheNearestCornerAndNotAnExactMatch()
    {
        Vec3[] points =
        [
            new Vec3(0, 0, 0),
            new Vec3(0, 64, 0),
            new Vec3(64, 64, 0),
            new Vec3(64, 0, 0),
        ];

        Assert.Equal(2, DisplacementSurface.FindStartIndex(new Vec3(63.9f, 64.1f, 0.05f), points));
    }

    /// <summary>
    /// The start corner rotates the surface: corner 0 becomes the start.
    /// </summary>
    [Fact]
    public void TheStartCornerRotatesTheCornerPoints()
    {
        DisplacementSurface surface = Synthetic(BrushContents.Solid, startCorner: 3);

        Assert.Equal(new Vec3(64, 0, 0), surface.CornerPoints[0]);
        Assert.Equal(new Vec3(0, 0, 0), surface.CornerPoints[1]);
    }

    /// <summary>
    /// A displacement with no field applied is its own flat base quad.
    /// </summary>
    /// <remarks>
    /// The bilinear interpolation on its own, with the field zeroed, so that a
    /// mistake in it cannot hide behind a displacement offset. Corner posts sit
    /// on the quad's corners and the centre post sits at the centre.
    /// </remarks>
    [Fact]
    public void AZeroFieldDisplacementIsItsOwnFlatQuad()
    {
        DisplacementSurface surface = Synthetic(BrushContents.Solid);

        // Power 2, so five posts a side; index i * 5 + j, where i runs along
        // the corner 0 -> corner 1 edge and j along corner 0 -> corner 3. That
        // orientation is not arbitrary: it is which pair of opposite edges
        // GenerateDispSurf interpolates between (builddisp.cpp:1937-1942),
        // and transposing it mirrors the height field about its diagonal.
        Assert.Equal(new Vec3(0, 0, 0), surface.Vertices[0]);
        Assert.Equal(new Vec3(64, 0, 0), surface.Vertices[4]);
        Assert.Equal(new Vec3(0, 64, 0), surface.Vertices[20]);
        Assert.Equal(new Vec3(64, 64, 0), surface.Vertices[24]);
        Assert.Equal(new Vec3(32, 32, 0), surface.Vertices[12]);
    }

    /// <summary>
    /// A field vector moves the post it belongs to, and only that post.
    /// </summary>
    /// <remarks>
    /// The <c>vec * dist</c> product, isolated. It is the one term of stock's
    /// three that a compiled map ever carries, so a port that dropped it would
    /// produce a perfectly flat "displacement" whose triangle count and whose
    /// x/y bounds are both still right.
    /// </remarks>
    [Fact]
    public void AFieldVectorMovesOnlyItsOwnPost()
    {
        DispVert[] verts = new DispVert[25];
        verts[12] = new DispVert { Vector = new Vec3(0, 0, 1), Dist = 48.0f, Alpha = 0 };

        DisplacementSurface surface = Synthetic(BrushContents.Solid, verts: verts);

        Assert.Equal(new Vec3(32, 32, 48), surface.Vertices[12]);
        Assert.Equal(new Vec3(0, 0, 0), surface.Vertices[0]);
        Assert.Equal(48.0f, surface.MaxDisplacement);
    }

    /// <summary>
    /// A 64-unit square power-2 displacement anchored at one of its corners,
    /// with whatever contents a fact wants to give it.
    /// </summary>
    /// <param name="contents">The contents to put in the lump entry.</param>
    /// <param name="startCorner">Which winding point to anchor to.</param>
    /// <param name="verts">
    /// The 25 field entries, or null for an undisplaced flat quad.
    /// </param>
    /// <returns>The tessellated surface.</returns>
    private static DisplacementSurface Synthetic(
        BrushContents contents,
        int startCorner = 0,
        DispVert[]? verts = null)
    {
        Vec3[] points =
        [
            new Vec3(0, 0, 0),
            new Vec3(0, 64, 0),
            new Vec3(64, 64, 0),
            new Vec3(64, 0, 0),
        ];

        DispInfo info = new()
        {
            StartPosition = points[startCorner],
            DispVertStart = 0,
            DispTriStart = 0,
            Power = 2,
            Contents = (int)contents,
        };

        return DisplacementSurface.Create(
            0, 0, info, points, verts ?? new DispVert[25]);
    }
}
