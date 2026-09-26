//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;

// MapTools has TWO SurfaceFlags: the Materials one is the SURF_* bits a
// compiler writes into a texinfo, which is what tests; the Tracing
// one is that tracer's own per-face summary. Importing
// SourceSharp.MapTools.Tracing for TracedTriangle brings the second into scope, so the one under
// test is named explicitly rather than left to resolution order.
using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// A map assembled lump by lump, so a filter can be asked about one brush.
/// </summary>
/// <remarks>
/// <para>
/// The real map answers "does the whole load agree with stock"; it cannot
/// answer "is this brush skipped because it is not opaque, or because its only
/// side is sky". A six-sided cube with one field changed can, and each of the
/// filters below is one changed field.
/// </para>
/// <para>
/// The plane lump is built in <c>planenum ^ 1</c> PAIRS, because that is how
/// <c>AddBrushToRaytraceEnvironment</c> finds the plane to clip against.
/// A builder that emitted six planes rather than twelve
/// would clip every side against its own front and produce an empty brush,
/// which is the sort of thing a fixture gets wrong quietly.
/// </para>
/// </remarks>
internal sealed class SyntheticBsp
{
    private readonly List<DPlane> _planes = [];
    private readonly List<DBrushSide> _sides = [];
    private readonly List<DBrush> _brushes = [];
    private readonly List<TexInfo> _texInfos = [];
    private readonly List<ushort> _leafBrushes = [];
    private readonly List<DLeaf> _leaves = [];
    private readonly List<DNode> _nodes = [];
    private readonly List<DModel> _models = [];
    private readonly List<DFace> _faces = [];
    private readonly List<Vec3> _vertexes = [];
    private readonly List<DEdge> _edges = [];
    private readonly List<int> _surfEdges = [];

    /// <summary>Starts a map with one flagless texinfo at index 0.</summary>
    public SyntheticBsp()
    {
        AddTexInfo(SurfaceFlags.None);

        // Edge 0 is never referenced by a real face, because a surfedge of -0
        // is indistinguishable from +0. A dummy keeps the test's edge indices
        // matching what a compiler would write.
        _edges.Add(default);
        _vertexes.Add(Vec3.Zero);
    }

    /// <summary>Which leaf struct the built map's LUMP_LEAFS declares.</summary>
    public int LeafLumpVersion { get; set; } = 1;

    /// <summary>Adds a texinfo with the given surface flags.</summary>
    /// <param name="flags">The flags.</param>
    /// <returns>Its index.</returns>
    public short AddTexInfo(SurfaceFlags flags)
    {
        _texInfos.Add(new TexInfo { Flags = (int)flags });
        return (short)(_texInfos.Count - 1);
    }

    /// <summary>
    /// Adds an axis-aligned cube as six brush sides, with its twelve planes.
    /// </summary>
    /// <param name="min">The cube's lower corner, on every axis.</param>
    /// <param name="max">The cube's upper corner, on every axis.</param>
    /// <param name="contents">The brush's contents.</param>
    /// <param name="bevelX">
    /// When given, a SEVENTH side at this x, marked <c>bevel</c>. It is a real
    /// side of the brush in every other respect.
    /// </param>
    /// <returns>The brush's index.</returns>
    public int AddCube(
        float min, float max, BrushContents contents, float? bevelX = null)
    {
        int firstSide = _sides.Count;
        AddSidePair(new Vec3(1f, 0f, 0f), max);
        AddSidePair(new Vec3(-1f, 0f, 0f), -min);
        AddSidePair(new Vec3(0f, 1f, 0f), max);
        AddSidePair(new Vec3(0f, -1f, 0f), -min);
        AddSidePair(new Vec3(0f, 0f, 1f), max);
        AddSidePair(new Vec3(0f, 0f, -1f), -min);

        if (bevelX is { } x)
        {
            AddSidePair(new Vec3(1f, 0f, 0f), x);
            DBrushSide bevel = _sides[^1];
            bevel.Bevel = 1;
            _sides[^1] = bevel;
        }

        _brushes.Add(new DBrush
        {
            FirstSide = firstSide,
            NumSides = _sides.Count - firstSide,
            Contents = (int)contents,
        });

        return _brushes.Count - 1;
    }

    /// <summary>Rewrites one side of a brush.</summary>
    /// <param name="brush">The brush's index.</param>
    /// <param name="ordinal">The side's position within the brush.</param>
    /// <param name="texInfo">The side's texinfo index.</param>
    /// <param name="dispInfo">The side's dispinfo, non-zero for a displacement.</param>
    public void SetSide(int brush, int ordinal, short texInfo, short dispInfo)
    {
        int index = _brushes[brush].FirstSide + ordinal;
        DBrushSide side = _sides[index];
        side.TexInfo = texInfo;
        side.DispInfo = dispInfo;
        _sides[index] = side;
    }

    /// <summary>Adds a leaf holding the given brushes, in the given order.</summary>
    /// <param name="brushes">The brush indices.</param>
    /// <returns>The node reference for the leaf: <c>-1 - leafIndex</c>.</returns>
    public int AddLeaf(params int[] brushes)
    {
        int first = _leafBrushes.Count;
        foreach (int brush in brushes)
        {
            _leafBrushes.Add((ushort)brush);
        }

        _leaves.Add(new DLeaf
        {
            FirstLeafBrush = (ushort)first,
            NumLeafBrushes = (ushort)brushes.Length,
        });

        return -1 - (_leaves.Count - 1);
    }

    /// <summary>Adds a branch node over two children.</summary>
    /// <param name="child0">The first child, as a node reference.</param>
    /// <param name="child1">The second child.</param>
    /// <returns>The node's index, which is its own node reference.</returns>
    public int AddNode(int child0, int child1)
    {
        DNode node = default;
        node.Children[0] = child0;
        node.Children[1] = child1;
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    /// <summary>Adds a model.</summary>
    /// <param name="headNode">Its head node reference.</param>
    /// <param name="firstFace">Its first face index.</param>
    /// <param name="numFaces">How many faces it has.</param>
    /// <returns>The model's index.</returns>
    public int AddModel(int headNode, int firstFace = 0, int numFaces = 0)
    {
        _models.Add(new DModel
        {
            HeadNode = headNode,
            FirstFace = firstFace,
            NumFaces = numFaces,
        });

        return _models.Count - 1;
    }

    /// <summary>Adds a four-sided face.</summary>
    /// <param name="texInfo">The face's texinfo index.</param>
    /// <param name="corners">Its four corners, in winding order.</param>
    /// <param name="negativeSurfEdges">
    /// Whether to write the face's surfedges NEGATIVE, so the reader must take
    /// <c>v[1]</c> of a reversed edge rather than <c>v[0]</c> of a forward one.
    /// </param>
    /// <returns>The face's index.</returns>
    public int AddQuadFace(short texInfo, Vec3[] corners, bool negativeSurfEdges = false)
    {
        int firstVertex = _vertexes.Count;
        _vertexes.AddRange(corners);

        int firstSurfEdge = _surfEdges.Count;
        for (int i = 0; i < corners.Length; i++)
        {
            ushort a = (ushort)(firstVertex + i);
            ushort b = (ushort)(firstVertex + ((i + 1) % corners.Length));

            DEdge edge = default;
            if (negativeSurfEdges)
            {
                edge.V[0] = b;
                edge.V[1] = a;
                _edges.Add(edge);
                _surfEdges.Add(-(_edges.Count - 1));
            }
            else
            {
                edge.V[0] = a;
                edge.V[1] = b;
                _edges.Add(edge);
                _surfEdges.Add(_edges.Count - 1);
            }
        }

        _faces.Add(new DFace
        {
            FirstEdge = firstSurfEdge,
            NumEdges = (short)corners.Length,
            TexInfo = texInfo,
            DispInfo = -1,
        });

        return _faces.Count - 1;
    }

    /// <summary>Packs everything written so far into lumps.</summary>
    /// <param name="entities">The entity lump's text, or null for none.</param>
    /// <param name="hdrFaces">
    /// When true, the faces go to LUMP_FACES_HDR as WELL as LUMP_FACES, with the
    /// HDR copy carrying a marker texinfo so the two can be told apart.
    /// </param>
    /// <returns>The map.</returns>
    public BspData Build(string? entities = null, bool hdrFaces = false)
    {
        BspData bsp = new();
        bsp[BspLump.Planes] = Lump(_planes);
        bsp[BspLump.BrushSides] = Lump(_sides);
        bsp[BspLump.Brushes] = Lump(_brushes);
        bsp[BspLump.TexInfo] = Lump(_texInfos);
        bsp[BspLump.LeafBrushes] = Lump(_leafBrushes);
        bsp[BspLump.Nodes] = Lump(_nodes);
        bsp[BspLump.Models] = Lump(_models);
        bsp[BspLump.Faces] = Lump(_faces);
        bsp[BspLump.Vertexes] = Lump(_vertexes);
        bsp[BspLump.Edges] = Lump(_edges);
        bsp[BspLump.SurfEdges] = Lump(_surfEdges);

        if (hdrFaces)
        {
            bsp[BspLump.FacesHdr] = Lump(_faces);
        }

        bsp[BspLump.Leafs] = LeafLumpVersion == 0
            ? Lump(_leaves.ConvertAll(ToVersion0), 0)
            : Lump(_leaves, LeafLumpVersion);

        if (entities is not null)
        {
            byte[] text = new byte[entities.Length + 1];
            for (int i = 0; i < entities.Length; i++)
            {
                text[i] = (byte)entities[i];
            }

            bsp[BspLump.Entities] = new BspLumpData(text, 0, 0);
        }

        return bsp;
    }

    private static DLeafVersion0 ToVersion0(DLeaf leaf) => new()
    {
        Contents = leaf.Contents,
        Cluster = leaf.Cluster,
        AreaFlags = leaf.AreaFlags,
        FirstLeafFace = leaf.FirstLeafFace,
        NumLeafFaces = leaf.NumLeafFaces,
        FirstLeafBrush = leaf.FirstLeafBrush,
        NumLeafBrushes = leaf.NumLeafBrushes,
        LeafWaterDataId = leaf.LeafWaterDataId,
    };

    private static BspLumpData Lump<T>(List<T> items, int version = 0)
        where T : unmanaged
    {
        T[] array = [.. items];
        return new BspLumpData(MemoryMarshal.AsBytes(array.AsSpan()).ToArray(), version, 0);
    }

    private void AddSidePair(Vec3 normal, float dist)
    {
        _sides.Add(new DBrushSide
        {
            PlaneNum = (ushort)_planes.Count,
            TexInfo = 0,
            DispInfo = 0,
            Bevel = 0,
        });

        _planes.Add(new DPlane { Normal = normal, Dist = dist });
        _planes.Add(new DPlane { Normal = -normal, Dist = -dist });
    }
}

/// <summary>
/// <c>dm_lockdown</c> loaded once, with its brush and sky casters built from it.
/// </summary>
/// <remarks>
/// The walk clips 15,653 brush sides against their own brushes, so it is worth
/// doing once for the class; every fact over it is read-only.
/// </remarks>
public sealed class BrushShadowCasterFixture
{
    /// <summary>Loads the map and runs both of vrad's brush load calls, in order.</summary>
    public BrushShadowCasterFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        Bsp = BspFile.LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        Entities = EntityLump.Parse(Bsp[BspLump.Entities]);

        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddBrushEntities(Bsp, Entities, builder);
        BrushShadowCasters.AddWorld(Bsp, useHdrFaces: false, builder);
        Set = builder.Build();
    }

    /// <summary>The loaded map.</summary>
    public BspData Bsp { get; }

    /// <summary>Its entity lump.</summary>
    public List<BspEntity> Entities { get; }

    /// <summary>The casters the two calls produced.</summary>
    public ShadowCasterSet Set { get; }
}

/// <summary>
/// The 4b gate for <see cref="BrushShadowCasters"/>: stock's own caster counts
/// and bounds on the committed golden map, and the filters themselves on
/// synthetic brushes.
/// </summary>
/// <remarks>
/// <para>
/// The golden numbers come from stock vrad's <c>-dumptrace</c> on this
/// worktree's <c>dm_lockdown.bsp</c>, which writes the caster list in load
/// order, so the three runs are separable by position. They are MEASURED, not
/// derived: nothing in this file recomputes 23,549 from the map, which is the
/// point of gating on them.
/// </para>
/// <para>
/// Bounds are compared to 0.01 rather than exactly. Stock's dumper prints two
/// decimal places, so the recorded corner is a rounded number and an exact
/// comparison would be asserting against the formatter.
/// </para>
/// </remarks>
public sealed class BrushShadowCasterTests : IClassFixture<BrushShadowCasterFixture>
{
    /// <summary>Stock's world-brush triangle count on <c>dm_lockdown</c>.</summary>
    private const int StockWorldBrushTriangles = 23549;

    /// <summary>
    /// This port's world-brush triangle count on the same map: FOUR SHORT of
    /// stock's, and not because of anything in
    /// <see cref="BrushShadowCasters"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap is <c>BaseWindingForPlane</c>'s normalise, and it was measured
    /// rather than reasoned about. An independent model of the whole walk.
    /// stock's <c>BaseWindingForPlane</c> and <c>ChopWindingInPlace</c>
    /// rewritten outside this tree, every operation rounded to 32 bits -- run
    /// over this map produces 23,545 when it normalises with a divide per
    /// component and 23,549 when it multiplies by a reciprocal, which is the
    /// only thing changed between the two runs. 23,545 is what this port
    /// produces and 23,549 is what stock's <c>-dumptrace</c> recorded.
    /// </para>
    /// <para>
    /// <c>VectorNormalize</c> computes <c>1/sqrt(lengthSqr + 1e-10)</c> once and
    /// multiplies three times, rounding twice per component;
    /// <see cref="Vec3.Normalise"/> divides, and says in its own remarks that
    /// this "gives different last bits". Normally that is invisible. Here it is
    /// not, because <c>AddBrushToRaytraceEnvironment</c> clips with an epsilon
    /// of exactly ZERO: a base-winding corner a few ULPs
    /// to one side turns a <c>SIDE_ON</c> point into a <c>SIDE_FRONT</c> one,
    /// the clipped winding gains or loses a point, and the fan around it gains
    /// or loses a triangle. Four times, in 15,653 brush sides.
    /// </para>
    /// <para>
    /// So closing it belongs in <c>WindingArena.BaseWindingForPlane</c>, not
    /// here: every consumer of that method -- vbsp's brush faces and vvis's
    /// portals as well as this -- carries the same divergence, and
    /// <see cref="Vec3.NormaliseLikeStock"/> already exists for exactly this
    /// situation. It is a deliberate, documented trade (reproducible on every
    /// machine, versus bit-identical to stock on one), so this file pins the
    /// consequence rather than working around it.
    /// </para>
    /// </remarks>
    private const int PortWorldBrushTriangles = 23545;

    /// <summary>Stock's sky-face triangle count on the same map.</summary>
    private const int StockSkyTriangles = 512;

    /// <summary>The tolerance the recorded bounds are printed to.</summary>
    private const float BoundsTolerance = 0.01f;

    private static readonly Vec3 StockWorldBrushMin = new(-4992f, 1328f, -176f);
    private static readonly Vec3 StockWorldBrushMax = new(-2412f, 7104f, 656f);
    private static readonly Vec3 StockSkyMin = new(-4976f, 1216f, -16f);
    private static readonly Vec3 StockSkyMax = new(-2400f, 6464f, 1136f);

    private readonly BrushShadowCasterFixture _fixture;

    /// <summary>Takes the shared map.</summary>
    /// <param name="fixture">The fixture.</param>
    public BrushShadowCasterTests(BrushShadowCasterFixture fixture) => _fixture = fixture;

    // ---- the golden map, against stock's recorded answers ---.

    /// <summary>The world-brush run produces the count this port produces.</summary>
    /// <remarks>
    /// An absolute number rather than a comparison, so that ANY change to the
    /// walk fails here even if it happens to move the gap to stock by the same
    /// amount in the other direction.
    /// </remarks>
    [Fact]
    public void WorldBrushTriangleCountIsStable() => Assert.Equal(
        PortWorldBrushTriangles,
        _fixture.Set.Stats(ShadowCasterSource.WorldBrush).Triangles);

    /// <summary>
    /// And it is exactly four short of stock's, which is the winding
    /// normalise and nothing in this file.
    /// </summary>
    /// <remarks>
    /// See <see cref="PortWorldBrushTriangles"/> for the measurement. This fact
    /// exists so that the gap cannot widen unnoticed: a real defect in the
    /// brush walk would move it, and a fix in
    /// <c>WindingArena.BaseWindingForPlane</c> would close it -- both of which
    /// should stop the build and be looked at rather than absorbed.
    /// </remarks>
    [Fact]
    public void WorldBrushTriangleCountIsFourShortOfStock() => Assert.Equal(
        4,
        StockWorldBrushTriangles
            - _fixture.Set.Stats(ShadowCasterSource.WorldBrush).Triangles);

    /// <summary>And they occupy stock's bounding box, lower corner.</summary>
    [Fact]
    public void WorldBrushMinimumMatchesStock() => AssertClose(
        StockWorldBrushMin, _fixture.Set.Stats(ShadowCasterSource.WorldBrush).Min);

    /// <summary>And its upper corner.</summary>
    [Fact]
    public void WorldBrushMaximumMatchesStock() => AssertClose(
        StockWorldBrushMax, _fixture.Set.Stats(ShadowCasterSource.WorldBrush).Max);

    /// <summary>The sky run has exactly as many triangles as stock's.</summary>
    [Fact]
    public void SkyTriangleCountMatchesStock() => Assert.Equal(
        StockSkyTriangles, _fixture.Set.Stats(ShadowCasterSource.Sky).Triangles);

    /// <summary>And they occupy stock's bounding box, lower corner.</summary>
    [Fact]
    public void SkyMinimumMatchesStock() => AssertClose(
        StockSkyMin, _fixture.Set.Stats(ShadowCasterSource.Sky).Min);

    /// <summary>And its upper corner.</summary>
    [Fact]
    public void SkyMaximumMatchesStock() => AssertClose(
        StockSkyMax, _fixture.Set.Stats(ShadowCasterSource.Sky).Max);

    /// <summary>
    /// No entity on the golden map opts into brush shadows, which is why the
    /// brush-entity run below is expected to be empty.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed. An empty run is also what a load path that
    /// never ran produces, and without this fact the two are the same green.
    /// </remarks>
    [Fact]
    public void LockdownHasNoEntityOptingIntoBrushShadows() => Assert.DoesNotContain(
        _fixture.Entities,
        entity => entity.Get("vrad_brush_cast_shadows") is { Length: > 0 } value && value != "0");

    /// <summary>So the brush-entity run contributes nothing on this map.</summary>
    [Fact]
    public void BrushEntityRunIsEmptyOnLockdown() => Assert.Equal(
        0, _fixture.Set.Stats(ShadowCasterSource.BrushEntity).Triangles);

    /// <summary>The whole set is the two non-empty runs and nothing else.</summary>
    [Fact]
    public void TotalIsTheSumOfTheRuns() => Assert.Equal(
        PortWorldBrushTriangles + StockSkyTriangles, _fixture.Set.Count);

    /// <summary>World brushes are added before sky faces, as vrad adds them.</summary>
    /// <remarks>
    /// A triangle's only identity is its index, so this ordering is the thing a
    /// recorded comparison against stock's dump depends on.
    /// </remarks>
    [Fact]
    public void WorldBrushesComeBeforeSkyFaces()
    {
        ReadOnlySpan<TracedTriangle> triangles = _fixture.Set.Triangles;
        Assert.Equal(TraceId.Opaque, triangles[0].Id);
        Assert.Equal(TraceId.Sky, triangles[^1].Id);
    }

    /// <summary>Every triangle carries a coverage of one.</summary>
    /// <remarks>
    /// Stock fills only <c>fullCoverage.x</c> and leaves y and z uninitialised
    ///; the builder keeps the one component anything
    /// reads.
    /// </remarks>
    [Fact]
    public void EveryBrushCasterIsFullyOpaque()
    {
        foreach (float coverage in _fixture.Set.Coverage)
        {
            Assert.Equal(1f, coverage);
        }
    }

    /// <summary>
    /// An HDR run on this map reads the same faces, because its HDR face lump
    /// is empty.
    /// </summary>
    /// <remarks>
    /// <c>dm_lockdown.bsp</c>'s LUMP_FACES_HDR is zero bytes long, which is
    /// stock's "copy the LDR faces in and point at the copy" case.
    /// Reading the LDR lump directly must give the same
    /// answer, and this is the fact that says so rather than the comment.
    /// </remarks>
    [Fact]
    public void HdrRunUsesTheLdrFacesWhenTheHdrLumpIsEmpty()
    {
        Assert.True(_fixture.Bsp[BspLump.FacesHdr].IsEmpty);

        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddWorld(_fixture.Bsp, useHdrFaces: true, builder);

        Assert.Equal(
            StockSkyTriangles, builder.Build().Stats(ShadowCasterSource.Sky).Triangles);
    }

    // ---- the filters, on one cube at a time ---.

    /// <summary>A six-sided opaque cube is twelve triangles.</summary>
    [Fact]
    public void OpaqueCubeIsTwoTrianglesPerSide() =>
        Assert.Equal(12, WorldTriangles(Cube(BrushContents.Solid)));

    /// <summary>Its triangles are laid out over the cube's own corners.</summary>
    [Fact]
    public void OpaqueCubeSpansItsOwnBounds()
    {
        ShadowCasterStats stats = World(Cube(BrushContents.Solid))
            .Stats(ShadowCasterSource.WorldBrush);
        AssertClose(new Vec3(0f, 0f, 0f), stats.Min);
        AssertClose(new Vec3(64f, 64f, 64f), stats.Max);
    }

    /// <summary>
    /// A brush whose contents is outside <c>MASK_OPAQUE</c> contributes nothing.
    /// </summary>
    [Fact]
    public void NonOpaqueBrushContributesNothing() =>
        Assert.Equal(0, WorldTriangles(Cube(BrushContents.Window)));

    /// <summary><c>CONTENTS_MOVEABLE</c> is opaque, which is the surprising half.</summary>
    [Fact]
    public void MoveableBrushIsOpaque() =>
        Assert.Equal(12, WorldTriangles(Cube(BrushContents.Moveable)));

    /// <summary><c>CONTENTS_GRATE</c> blocks a player and not a photon.</summary>
    [Fact]
    public void GrateBrushContributesNothing() =>
        Assert.Equal(0, WorldTriangles(Cube(BrushContents.Grate)));

    /// <summary>A <c>SURF_SKY</c> side of an opaque brush contributes nothing.</summary>
    /// <remarks>
    /// The other five sides still do: the test is per side, not per brush, so a
    /// port that rejected the whole brush would read as ten rather than twelve
    /// here too. Ten is the answer either way -- which is why
    /// <see cref="SkySideIsTheOneThatIsMissing"/> exists beside this.
    /// </remarks>
    [Fact]
    public void SkySideOfABrushContributesNothing()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid);
        map.SetSide(brush, 0, map.AddTexInfo(SurfaceFlags.Sky), 0);
        map.AddModel(map.AddLeaf(brush));

        Assert.Equal(10, WorldTriangles(map));
    }

    /// <summary>And it is the <c>+x</c> side, not some other one.</summary>
    [Fact]
    public void SkySideIsTheOneThatIsMissing()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid);
        map.SetSide(brush, 0, map.AddTexInfo(SurfaceFlags.Sky), 0);
        map.AddModel(map.AddLeaf(brush));

        ShadowCasterStats stats = World(map).Stats(ShadowCasterSource.WorldBrush);

        // Five sides remain and four of them still touch x = 64 along an edge,
        // so the bound does not move; what moves is that no triangle LIES in
        // that plane any more.
        foreach (TracedTriangle triangle in World(map).Triangles)
        {
            Assert.False(
                triangle.V0.X == 64f && triangle.V1.X == 64f && triangle.V2.X == 64f);
        }

        AssertClose(new Vec3(64f, 64f, 64f), stats.Max);
    }

    /// <summary>A side with a dispinfo contributes nothing.</summary>
    /// <remarks>
    /// Displacements reach the ray tracer through
    /// <c>StaticDispMgr-&gt;AddPolysForRayTrace</c> instead, as tessellated
    /// geometry; adding the flat brush side as well would double the blocker.
    /// </remarks>
    [Fact]
    public void DisplacementSideContributesNothing()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid);
        map.SetSide(brush, 0, 0, 1);
        map.AddModel(map.AddLeaf(brush));

        Assert.Equal(10, WorldTriangles(map));
    }

    /// <summary>
    /// A side whose texinfo is -1 is not treated as sky, and still contributes.
    /// </summary>
    /// <remarks>
    /// 542 of <c>dm_lockdown</c>'s 15,653 brush sides are like this. Stock reads
    /// <c>texinfo[-1]</c> -- off the front of a <c>CUtlVector</c>'s allocation --
    /// so there is no behaviour to port, only a choice, and the world triangle
    /// count on the golden map is what says the choice agrees with what stock
    /// happened to read.
    /// </remarks>
    [Fact]
    public void NegativeTexInfoOnASideIsNotSky()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid);
        map.SetSide(brush, 0, -1, 0);
        map.AddModel(map.AddLeaf(brush));

        Assert.Equal(12, WorldTriangles(map));
    }

    /// <summary>A bevel side does not clip the brush's other sides.</summary>
    /// <remarks>
    /// The bevel here is the plane <c>x = 32</c> through the middle of a cube
    /// that runs 0 to 64. Were it used as a clipper the <c>+x</c> side would be
    /// clipped out of existence and everything else halved, for twelve
    /// triangles -- the same COUNT as a plain cube, which is why this fact
    /// checks the surviving extent and the next one checks the count.
    /// </remarks>
    [Fact]
    public void BevelSideDoesNotClipTheOtherSides()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid, bevelX: 32f);
        map.AddModel(map.AddLeaf(brush));

        AssertClose(
            new Vec3(64f, 64f, 64f), World(map).Stats(ShadowCasterSource.WorldBrush).Max);
    }

    /// <summary>But the bevel side is still a side, and contributes its own face.</summary>
    /// <remarks>
    /// Stock's <c>bevel</c> test is inside the CLIP loop only
    ///; the outer loop over sides does not look at it. So
    /// a bevel plane cutting through the middle of a brush emits a triangle pair
    /// buried inside the solid, and this port emits it too.
    /// </remarks>
    [Fact]
    public void BevelSideStillContributesItsOwnFace()
    {
        SyntheticBsp map = new();
        int brush = map.AddCube(0f, 64f, BrushContents.Solid, bevelX: 32f);
        map.AddModel(map.AddLeaf(brush));

        Assert.Equal(14, WorldTriangles(map));
    }

    // ---- the leaf walk ----

    /// <summary>A brush listed by two leaves is added once.</summary>
    [Fact]
    public void ABrushInTwoLeavesIsAddedOnce()
    {
        SyntheticBsp map = new();
        int a = map.AddCube(0f, 64f, BrushContents.Solid);
        int b = map.AddCube(128f, 192f, BrushContents.Solid);
        map.AddModel(map.AddNode(map.AddLeaf(a, b), map.AddLeaf(b, a)));

        Assert.Equal(24, WorldTriangles(map));
    }

    /// <summary>The walk takes child 0 before child 1.</summary>
    /// <remarks>
    /// The order is the triangle order and the triangle order is the identity,
    /// so a walk that recursed the other way would produce a numerically
    /// identical scene that no recorded answer could be compared against.
    /// </remarks>
    [Fact]
    public void LeafWalkTakesChildZeroFirst()
    {
        SyntheticBsp map = new();
        int near = map.AddCube(0f, 64f, BrushContents.Solid);
        int far = map.AddCube(128f, 192f, BrushContents.Solid);
        map.AddModel(map.AddNode(map.AddLeaf(far), map.AddLeaf(near)));

        // The far cube's every coordinate is at least 128; the near cube's is at
        // most 64, so one vertex separates them.
        Assert.True(World(map).Triangles[0].V0.X >= 128f);
    }

    /// <summary>And a leaf's brushes in leafbrush-lump order within it.</summary>
    [Fact]
    public void LeafBrushesAreTakenInLumpOrder()
    {
        SyntheticBsp map = new();
        int near = map.AddCube(0f, 64f, BrushContents.Solid);
        int far = map.AddCube(128f, 192f, BrushContents.Solid);
        map.AddModel(map.AddLeaf(far, near));

        Assert.True(World(map).Triangles[0].V0.X >= 128f);
    }

    /// <summary>The 56-byte version 0 leaf is read as well as the 32-byte one.</summary>
    /// <remarks>
    /// Not hypothetical: the golden map's LUMP_LEAFS is at version 0, so the
    /// facts above this one all take the older branch. This one is the other
    /// branch.
    /// </remarks>
    [Fact]
    public void VersionOneLeafLumpIsReadToo()
    {
        SyntheticBsp map = new() { LeafLumpVersion = 1 };
        map.AddModel(map.AddLeaf(map.AddCube(0f, 64f, BrushContents.Solid)));

        Assert.Equal(12, WorldTriangles(map));
    }

    /// <summary>And the version 0 one gives the same answer.</summary>
    [Fact]
    public void VersionZeroLeafLumpGivesTheSameAnswer()
    {
        SyntheticBsp map = new() { LeafLumpVersion = 0 };
        map.AddModel(map.AddLeaf(map.AddCube(0f, 64f, BrushContents.Solid)));

        Assert.Equal(12, WorldTriangles(map));
    }

    /// <summary>Only model 0's brushes are added by the world run.</summary>
    [Fact]
    public void OnlyModelZeroIsWalkedByTheWorldRun()
    {
        SyntheticBsp map = new();
        int near = map.AddCube(0f, 64f, BrushContents.Solid);
        int far = map.AddCube(128f, 192f, BrushContents.Solid);
        map.AddModel(map.AddLeaf(near));
        map.AddModel(map.AddLeaf(far));

        Assert.Equal(12, WorldTriangles(map));
    }

    /// <summary>A map with no models produces nothing rather than throwing.</summary>
    [Fact]
    public void MapWithNoModelsProducesNothing()
    {
        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddWorld(new SyntheticBsp().Build(), useHdrFaces: false, builder);

        Assert.Equal(0, builder.Count);
    }

    // ---- sky faces ----

    /// <summary>A four-edge sky face is fanned into two triangles.</summary>
    [Fact]
    public void SkyFaceIsFannedIntoTwoTriangles()
    {
        Assert.Equal(2, SkyTriangles(SkyFaceMap(SurfaceFlags.Sky)));
    }

    /// <summary>They carry <see cref="TraceId.Sky"/> and not <c>Opaque</c>.</summary>
    [Fact]
    public void SkyFaceTrianglesCarryTheSkyId()
    {
        ShadowCasterSet set = World(SkyFaceMap(SurfaceFlags.Sky));
        Assert.Equal(TraceId.Sky, set.Triangles[0].Id);
    }

    /// <summary>A face without <c>SURF_SKY</c> contributes no sky triangle.</summary>
    [Fact]
    public void NonSkyFaceContributesNothing() =>
        Assert.Equal(0, SkyTriangles(SkyFaceMap(SurfaceFlags.None)));

    /// <summary><c>SURF_SKY2D</c> alone is not <c>SURF_SKY</c>.</summary>
    /// <remarks>
    /// The two are separate bits and stock tests only <c>SURF_SKY</c>,
    /// so a 2D-sky-only surface is not a caster. vbsp
    /// usually sets both, which is what makes the distinction easy to lose.
    /// </remarks>
    [Fact]
    public void Sky2DAloneContributesNothing() =>
        Assert.Equal(0, SkyTriangles(SkyFaceMap(SurfaceFlags.Sky2D)));

    /// <summary>The fan is around the face's first point.</summary>
    [Fact]
    public void SkyFanIsAroundPointZero()
    {
        ShadowCasterSet set = World(SkyFaceMap(SurfaceFlags.Sky));
        Assert.Equal(set.Triangles[0].V0, set.Triangles[1].V0);
    }

    /// <summary>A negative surfedge takes the edge's SECOND vertex.</summary>
    /// <remarks>
    /// The sign says the face walks the edge backwards, so
    /// the winding is the same either way -- a port that took <c>v[0]</c>
    /// regardless would reverse every face with negative surfedges and still
    /// produce the right COUNT.
    /// </remarks>
    [Fact]
    public void NegativeSurfEdgeTakesTheSecondVertex()
    {
        ShadowCasterSet forward = World(SkyFaceMap(SurfaceFlags.Sky));
        ShadowCasterSet reversed = World(SkyFaceMap(SurfaceFlags.Sky, negativeSurfEdges: true));

        Assert.Equal(forward.Triangles[0], reversed.Triangles[0]);
    }

    /// <summary>The HDR face lump is used when the run is HDR and it has faces.</summary>
    [Fact]
    public void HdrFaceLumpIsUsedWhenItHasFaces()
    {
        SyntheticBsp map = new();
        short sky = map.AddTexInfo(SurfaceFlags.Sky);
        map.AddQuadFace(sky, UnitQuad);
        map.AddModel(map.AddLeaf(), firstFace: 0, numFaces: 1);

        BspData bsp = map.Build(hdrFaces: true);
        Assert.False(bsp[BspLump.FacesHdr].IsEmpty);

        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddWorld(bsp, useHdrFaces: true, builder);

        Assert.Equal(2, builder.Build().Stats(ShadowCasterSource.Sky).Triangles);
    }

    // ---- brush entities ----

    /// <summary>An entity with the key on contributes its model's brushes.</summary>
    [Fact]
    public void BrushEntityWithTheKeyContributesItsBrushes() => Assert.Equal(
        12, BrushEntityTriangles("\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*1\""));

    /// <summary>An entity without the key contributes nothing.</summary>
    [Fact]
    public void BrushEntityWithoutTheKeyContributesNothing() =>
        Assert.Equal(0, BrushEntityTriangles("\"model\" \"*1\""));

    /// <summary>The key set to zero is the same as absent.</summary>
    [Fact]
    public void BrushEntityWithTheKeyOffContributesNothing() => Assert.Equal(
        0, BrushEntityTriangles("\"vrad_brush_cast_shadows\" \"0\"\n\"model\" \"*1\""));

    /// <summary>Model 0 -- the world -- is rejected.</summary>
    /// <remarks>
    /// <c>modelIndex &gt; 0</c>. An entity pointing at
    /// the world would otherwise add every world brush a second time, at the
    /// entity's origin.
    /// </remarks>
    [Fact]
    public void BrushEntityNamingModelZeroContributesNothing() => Assert.Equal(
        0, BrushEntityTriangles("\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*0\""));

    /// <summary>A model index the map does not have is rejected.</summary>
    [Fact]
    public void BrushEntityNamingAMissingModelContributesNothing() => Assert.Equal(
        0, BrushEntityTriangles("\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*9\""));

    /// <summary>A studio model name is rejected, by the same range test.</summary>
    /// <remarks>
    /// Stock never checks for the leading <c>*</c>: it runs <c>atol</c> from the
    /// SECOND character on, so <c>"models/x.mdl"</c> parses as
    /// <c>atol("odels/x.mdl")</c> = 0 and falls out of the range test rather
    /// than any syntax check.
    /// </remarks>
    [Fact]
    public void BrushEntityNamingAStudioModelContributesNothing() => Assert.Equal(
        0,
        BrushEntityTriangles(
            "\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"models/props/x.mdl\""));

    /// <summary>The entity's origin moves its brushes.</summary>
    [Fact]
    public void BrushEntityIsTranslatedByItsOrigin()
    {
        ShadowCasterSet set = BrushEntitySet(
            "\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*1\"\n\"origin\" \"100 200 300\"");
        ShadowCasterStats stats = set.Stats(ShadowCasterSource.BrushEntity);

        AssertClose(new Vec3(100f, 200f, 300f), stats.Min);
        AssertClose(new Vec3(164f, 264f, 364f), stats.Max);
    }

    /// <summary>And its angles rotate them, yaw first.</summary>
    /// <remarks>
    /// A <c>QAngle</c> is pitch, yaw, roll, so <c>"0 90 0"</c> is a 90 degree
    /// YAW: the cube at 0..64 in x and y lands at -64..0 in x and 0..64 in y.
    /// Reading the key as pitch-first would leave x alone and move z, which is
    /// the mistake this pins.
    /// </remarks>
    [Fact]
    public void BrushEntityIsRotatedByItsAngles()
    {
        ShadowCasterSet set = BrushEntitySet(
            "\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*1\"\n\"angles\" \"0 90 0\"");
        ShadowCasterStats stats = set.Stats(ShadowCasterSource.BrushEntity);

        AssertClose(new Vec3(-64f, 0f, 0f), stats.Min);
        AssertClose(new Vec3(0f, 64f, 64f), stats.Max);
    }

    /// <summary>
    /// A brush-entity triangle carries <see cref="TraceId.Opaque"/>, the same id
    /// a world brush does.
    /// </summary>
    /// <remarks>
    /// Which is why <see cref="ShadowCasterSource"/> exists at all: the finished
    /// set cannot say which run produced a triangle, so the run is recorded as
    /// it is built.
    /// </remarks>
    [Fact]
    public void BrushEntityTrianglesAreIndistinguishableFromWorldOnes()
    {
        ShadowCasterSet set = BrushEntitySet(
            "\"vrad_brush_cast_shadows\" \"1\"\n\"model\" \"*1\"");

        Assert.Equal(TraceId.Opaque, set.Triangles[0].Id);
    }

    // ---- argument checks ----

    /// <summary><see cref="BrushShadowCasters.AddWorld"/> rejects a null map.</summary>
    [Fact]
    public void AddWorldRejectsANullMap() => Assert.Throws<ArgumentNullException>(
        () => BrushShadowCasters.AddWorld(null!, false, new ShadowCasterBuilder()));

    /// <summary>And a null builder.</summary>
    [Fact]
    public void AddWorldRejectsANullBuilder() => Assert.Throws<ArgumentNullException>(
        () => BrushShadowCasters.AddWorld(new SyntheticBsp().Build(), false, null!));

    /// <summary><see cref="BrushShadowCasters.AddBrushEntities"/> rejects a null list.</summary>
    [Fact]
    public void AddBrushEntitiesRejectsANullEntityList() =>
        Assert.Throws<ArgumentNullException>(() => BrushShadowCasters.AddBrushEntities(
            new SyntheticBsp().Build(), null!, new ShadowCasterBuilder()));

    // ---- helpers ----

    /// <summary>A unit quad in the z = 0 plane, for the sky-face facts.</summary>
    private static Vec3[] UnitQuad =>
    [
        new Vec3(0f, 0f, 0f),
        new Vec3(64f, 0f, 0f),
        new Vec3(64f, 64f, 0f),
        new Vec3(0f, 64f, 0f),
    ];

    private static void AssertClose(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(expected.X, actual.X, BoundsTolerance);
        Assert.Equal(expected.Y, actual.Y, BoundsTolerance);
        Assert.Equal(expected.Z, actual.Z, BoundsTolerance);
    }

    private static SyntheticBsp Cube(BrushContents contents)
    {
        SyntheticBsp map = new();
        map.AddModel(map.AddLeaf(map.AddCube(0f, 64f, contents)));
        return map;
    }

    private static SyntheticBsp SkyFaceMap(SurfaceFlags flags, bool negativeSurfEdges = false)
    {
        SyntheticBsp map = new();
        map.AddQuadFace(map.AddTexInfo(flags), UnitQuad, negativeSurfEdges);
        map.AddModel(map.AddLeaf(), firstFace: 0, numFaces: 1);
        return map;
    }

    private static ShadowCasterSet World(SyntheticBsp map)
    {
        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddWorld(map.Build(), useHdrFaces: false, builder);
        return builder.Build();
    }

    private static int WorldTriangles(SyntheticBsp map) =>
        World(map).Stats(ShadowCasterSource.WorldBrush).Triangles;

    private static int SkyTriangles(SyntheticBsp map) =>
        World(map).Stats(ShadowCasterSource.Sky).Triangles;

    /// <summary>
    /// One entity, plus a worldspawn ahead of it so the entity's own index is
    /// not zero, over a map whose model 1 is a cube.
    /// </summary>
    private static ShadowCasterSet BrushEntitySet(string pairs)
    {
        SyntheticBsp map = new();
        int world = map.AddLeaf();
        int cube = map.AddLeaf(map.AddCube(0f, 64f, BrushContents.Solid));
        map.AddModel(world);
        map.AddModel(cube);

        BspData bsp = map.Build($"{{\n\"classname\" \"worldspawn\"\n}}\n{{\n{pairs}\n}}\n");

        ShadowCasterBuilder builder = new();
        BrushShadowCasters.AddBrushEntities(
            bsp, EntityLump.Parse(bsp[BspLump.Entities]), builder);
        return builder.Build();
    }

    private static int BrushEntityTriangles(string pairs) =>
        BrushEntitySet(pairs).Stats(ShadowCasterSource.BrushEntity).Triangles;
}
