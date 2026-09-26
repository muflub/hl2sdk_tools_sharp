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
/// <c>CCoreDispInfo</c>'s own passes, one
/// behaviour per fact, on hand-built quads.
/// </summary>
public sealed class CoreDispInfoTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    /// <summary>
    /// The four grid corners are the quad's points, in the start-rotated order:
    /// <c>GenerateDispSurf</c>.
    /// </summary>
    [Fact]
    public void TheGridCornersAreTheQuadPoints()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        int n = core.PostSpacing - 1;

        Assert.Equal(Floor[0], core.Vert(DispFixtures.Index(core, 0, 0)));
        Assert.Equal(Floor[3], core.Vert(DispFixtures.Index(core, n, 0)));
        Assert.Equal(Floor[1], core.Vert(DispFixtures.Index(core, 0, n)));
        Assert.Equal(Floor[2], core.Vert(DispFixtures.Index(core, n, n)));
    }

    /// <summary>
    /// A start position nearest point 2 rotates the grid so it begins there:
    /// <c>FindSurfPointStartIndex</c> + <c>AdjustSurfPointData</c>.
    /// </summary>
    [Fact]
    public void TheStartPositionChoosesTheGridOrigin()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[2]), Floor);

        Assert.Equal(Floor[2], core.Vert(0));
    }

    /// <summary>
    /// A displaced vertex is its flat position plus field direction times
    /// distance:.
    /// </summary>
    [Fact]
    public void ADisplacedVertexIsFlatPlusFieldTimesDistance()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, y) => (x * 10) + y), Floor);
        int i = DispFixtures.Index(core, 3, 1);

        Assert.Equal(core.FlatVerts[i] + new Vec3(0, 0, 31), core.Vert(i));
    }

    /// <summary>The flat vertices ignore the field: <c>m_FlatVert</c>.</summary>
    [Fact]
    public void TheFlatVerticesIgnoreTheField()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(3, Floor[0], (_, _) => 100), Floor);

        Assert.All(core.FlatVerts.ToArray(), v => Assert.Equal(0.0f, v.Z));
    }

    /// <summary>
    /// The interior spacing is the edge divided by <c>2^power</c>:
    /// <c>ooInt</c>.
    /// </summary>
    [Fact]
    public void TheGridSpacingIsTheEdgeOverTwoToThePower()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(4, Floor[0]), Floor);

        Assert.Equal(new Vec3(16, 32, 0), core.Vert(DispFixtures.Index(core, 1, 2)));
    }

    /// <summary>
    /// Two triangles per grid square: <c>GetTriCount</c>.
    /// </summary>
    [Theory]
    [InlineData(2, 32)]
    [InlineData(3, 128)]
    [InlineData(4, 512)]
    public void ThereAreTwoTrianglesPerSquare(int power, int expected)
    {
        Assert.Equal(expected, new CoreDispInfo(power).TriCount);
    }

    /// <summary>
    /// An even flat index splits bottom-left to top-right:
    /// <c>BuildTriBLtoTR</c>.
    /// </summary>
    [Fact]
    public void AnEvenSquareSplitsBottomLeftToTopRight()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        Assert.Equal(new ushort[] { 0, 5, 6, 0, 6, 1 }, core.TriIndices[..6].ToArray());
    }

    /// <summary>
    /// An odd flat index splits top-left to bottom-right:
    /// <c>BuildTriTLtoBR</c>.
    /// </summary>
    [Fact]
    public void AnOddSquareSplitsTopLeftToBottomRight()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        Assert.Equal(new ushort[] { 1, 6, 2, 2, 6, 7 }, core.TriIndices[6..12].ToArray());
    }

    /// <summary>
    /// The parity is of the flat index, and the odd width makes the second row
    /// start on the other diagonal:.
    /// </summary>
    [Fact]
    public void TheSecondRowStartsOnTheOtherDiagonal()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        // Square (0, 1) has flat index 5, odd: TLtoBR.
        int first = 4 * 6;
        Assert.Equal(new ushort[] { 5, 10, 6 }, core.TriIndices.Slice(first, 3).ToArray());
    }

    /// <summary>
    /// Texture coordinates interpolate bilinearly from the corners:
    /// <c>CalcDispSurfCoords</c>.
    /// </summary>
    [Fact]
    public void TextureCoordinatesInterpolateAcrossTheGrid()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);

        // textureVecs s = 0.25 x, t = -0.25 y.
        DispUv uv = core.TexCoords[DispFixtures.Index(core, 2, 1)];

        Assert.Equal(new DispUv(32, -16), uv);
    }

    /// <summary>
    /// Luxel coordinates run from 0.5 to size + 0.5: <c>CalcLuxelCoords</c>,
    /// spread by <c>CalcDispSurfCoords</c>.
    /// </summary>
    [Fact]
    public void LuxelCoordinatesRunFromAHalfToTheSizePlusAHalf()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        int last = core.Size - 1;

        Assert.Equal(new DispUv(0.5f, 0.5f), core.LuxelCoord(0, 0));
        Assert.Equal(
            new DispUv(core.Surface.LuxelU + 0.5f, core.Surface.LuxelV + 0.5f),
            core.LuxelCoord(0, last));
    }

    /// <summary>A bump index past three is refused: <c>NUM_BUMP_VECTS + 1</c> sets.</summary>
    [Fact]
    public void ALuxelBumpIndexPastThreeIsRefused()
    {
        CoreDispInfo core = new(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => core.LuxelCoord(4, 0));
    }

    /// <summary>
    /// A flat displacement's normals all equal the plane normal:
    /// <c>GenerateDispSurfNormals</c>.
    /// </summary>
    [Fact]
    public void AFlatDisplacementsNormalsAreThePlaneNormal()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(3, Floor[0]), Floor);

        Assert.All(core.Normals.ToArray(), n => Assert.Equal(new Vec3(0, 0, 1), n));
    }

    /// <summary>
    /// On a planar slope every vertex's normal is the slope's normal: each fan
    /// triangle is coplanar, <c>CalcNormalFromEdges</c>.
    /// </summary>
    [Fact]
    public void APlanarSlopesNormalIsTheSlopeNormal()
    {
        // 64 units per grid step, rising 16 per step along x: z = x / 4.
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, _) => x * 16), Floor);
        Vec3 expected = new Vec3(-0.25f, 0, 1).Normalise().Normalised;

        Vec3 n = core.Normal(DispFixtures.Index(core, 2, 2));

        Assert.Equal(expected.X, n.X, 1e-6f);
        Assert.Equal(expected.Z, n.Z, 1e-6f);
    }

    /// <summary>
    /// Under stock the averaged normal is NOT renormalised, so a crease vertex
    /// carries a normal shorter than one:.
    /// </summary>
    [Fact]
    public void UnderStockACreaseVertexNormalIsShorterThanOne()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, _) => MathF.Abs(x - 2) * 64), Floor, stockNormalMean: true);

        float length = core.Normal(DispFixtures.Index(core, 2, 2)).Length();

        Assert.True(length < 0.99f, $"crease normal length {length}");
    }

    /// <summary>
    /// Under correct the crease normal is unit length, same direction:
    /// <see cref="SourceSharp.MapTools.Options.StockQuirk.DispVertexNormalMeanUnnormalised"/>.
    /// </summary>
    [Fact]
    public void UnderCorrectACreaseVertexNormalIsUnitLength()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, _) => MathF.Abs(x - 2) * 64), Floor);

        Vec3 n = core.Normal(DispFixtures.Index(core, 2, 2));

        Assert.Equal(1.0f, n.Length(), 1e-6f);
        Assert.Equal(new Vec3(0, 0, 1), n);
    }

    /// <summary>
    /// A corner vertex has one quadrant, two triangles:
    /// <c>DoesEdgeExist</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 0, 1, true)]
    [InlineData(0, 0, 2, true)]
    [InlineData(0, 0, 3, false)]
    [InlineData(4, 4, 1, false)]
    [InlineData(4, 4, 2, false)]
    [InlineData(2, 2, 7, false)]
    public void AnEdgeExistsOnlyTowardsAnotherVertex(int row, int col, int direction, bool expected)
    {
        Assert.Equal(expected, CoreDispInfo.DoesEdgeExist(row, col, direction, 5));
    }

    /// <summary>
    /// With the texture axes unset — as they always are, nothing calls
    /// <c>SetSAxis</c>/<c>SetTAxis</c> — every tangent is zero.
    /// </summary>
    [Fact]
    public void TheTangentsAreZeroWhenTheAxesAreUnset()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (x, y) => x * y), Floor);

        Assert.All(core.TangentS.ToArray(), t => Assert.Equal(Vec3.Zero, t));
        Assert.All(core.TangentT.ToArray(), t => Assert.Equal(Vec3.Zero, t));
    }

    /// <summary>
    /// With the axes set, S is flipped when S x T agrees with the plane normal:
    /// </summary>
    [Fact]
    public void TheSTangentIsFlippedWhenTheAxesAgreeWithThePlane()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0]);
        CoreDispInfo core = new(2);
        core.SetListBase([core]);
        core.Surface.SAxis = new Vec3(1, 0, 0);
        core.Surface.TAxis = new Vec3(0, 1, 0);
        DisplacementLumpBuilder.DispMapToCoreDispInfo(disp, DispFixtures.Face(Floor), core, false);

        // S = N x T = (-1, 0, 0), then negated because (S x T). N > 0.
        Assert.Equal(new Vec3(1, 0, 0), core.TangentS[0]);
        Assert.Equal(new Vec3(0, 1, 0), core.TangentT[0]);
    }

    /// <summary>
    /// The quad-tree root's box is the box of every displaced vertex:
    /// <c>CalcBoundingBoxAtNode</c>.
    /// </summary>
    [Fact]
    public void TheRootBoundsAreTheBoxOfTheDisplacedVertices()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(3, Floor[0], (x, y) => (x == 3 && y == 5) ? 90 : (x == 1 ? -7 : 0)),
            Floor);

        Assert.Equal(new DispBox(new Vec3(0, 0, -7), new Vec3(256, 256, 90)), core.RootBounds);
    }

    /// <summary>
    /// The root box is computed once in <c>Create</c> and does not follow a
    /// later <c>SetVert</c>, which is why <c>Disp_GridIndex</c> sees pre-snap
    /// vertices.
    /// </summary>
    [Fact]
    public void TheRootBoundsDoNotFollowALaterSetVert()
    {
        CoreDispInfo core = DispFixtures.Core(DispFixtures.Heightfield(2, Floor[0]), Floor);
        DispBox before = core.RootBounds;

        core.SetVert(12, new Vec3(128, 128, 5000));

        Assert.Equal(before, core.RootBounds);
    }

    /// <summary>
    /// The top bit of <c>minTess</c> marks the rest as surface flags:
    /// <c>InitDispInfo</c>.
    /// </summary>
    [Fact]
    public void AMinTessWithTheTopBitSetIsSurfaceFlags()
    {
        CoreDispInfo core = new(2);

        core.InitDispInfo(unchecked((int)0x80000006), new float[25], new Vec3[25], new float[25]);

        Assert.Equal(6, core.Surface.Flags);
    }

    /// <summary>A <c>minTess</c> without the top bit is dropped:.</summary>
    [Fact]
    public void AMinTessWithoutTheTopBitLeavesTheFlagsAlone()
    {
        CoreDispInfo core = new(2);

        core.InitDispInfo(6, new float[25], new Vec3[25], new float[25]);

        Assert.Equal(0, core.Surface.Flags);
    }

    /// <summary>A short field is refused rather than read past.</summary>
    [Fact]
    public void AShortFieldIsRefused()
    {
        CoreDispInfo core = new(3);

        Assert.Throws<ArgumentException>(
            () => core.InitDispInfo(0, new float[25], new Vec3[25], new float[25]));
    }

    /// <summary>
    /// Rebuilding from the lump's <c>CDispVert</c>s (vrad's path.
    ///) reproduces vbsp's displaced vertices bit for
    /// bit, because the lump stores exactly the direction and distance the
    /// VMF path used.
    /// </summary>
    [Fact]
    public void RebuildingFromTheLumpReproducesTheVerticesBitForBit()
    {
        MapDisplacement disp = DispFixtures.Heightfield(3, Floor[0], (x, y) => (x * 3.3f) - (y * 1.7f));
        (IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps) =
            DispFixtures.Build([(disp, Floor)]);

        CoreDispInfo rebuilt = new(3);
        rebuilt.SetListBase([rebuilt]);
        DispLightingLoader.BuilderInit(
            rebuilt, results[0].Info, 0, Floor, new Vec3(DispFixtures.LuxelsPerUnit, 0, 0),
            new Vec3(0, -DispFixtures.LuxelsPerUnit, 0), lumps.Verts.ToArray(), lumps.Tris.ToArray());
        rebuilt.Create();

        for (int i = 0; i < rebuilt.Size; i++)
        {
            Assert.True(DispFixtures.BitEqual(results[0].Core.Vert(i), rebuilt.Vert(i)), $"vertex {i}");
        }
    }

    /// <summary>The lump path copies the triangle tags:.</summary>
    [Fact]
    public void RebuildingFromTheLumpCopiesTheTriangleTags()
    {
        CoreDispInfo core = new(2);
        DispTri[] tris = new DispTri[32];
        tris[7] = new DispTri { Tags = 0x2 };

        core.InitDispInfo(0, new DispVert[25], tris);

        Assert.Equal((ushort)0x2, core.TriTags[7]);
    }

    /// <summary>
    /// <c>SetAll</c> sets the spare bits past the vertex count too:
    /// <c>CBitVec::SetAll</c>, as <c>SetupAllowedVerts</c> uses it.
    /// </summary>
    [Fact]
    public void SettingAllAllowedVertsSetsTheSpareBits()
    {
        CoreDispInfo core = new(2);

        core.AllowedVertsSetAll();

        Assert.All(core.AllowedVerts.ToArray(), w => Assert.Equal(0xFFFFFFFFu, w));
    }

    /// <summary>Clearing one allowed vertex clears exactly its bit.</summary>
    [Fact]
    public void ClearingAnAllowedVertClearsOneBit()
    {
        CoreDispInfo core = new(4);
        core.AllowedVertsSetAll();

        core.AllowedVertsClear(33);

        Assert.False(core.AllowedVertsGet(33));
        Assert.Equal(0xFFFFFFFDu, core.AllowedVerts[1]);
    }

    /// <summary>
    /// A neighbour lookup before the list is set is a bug, not a null:
    /// <c>SetDispUtilsHelperInfo</c>.
    /// </summary>
    [Fact]
    public void ANeighbourLookupWithoutAListThrows()
    {
        Assert.Throws<InvalidOperationException>(() => new CoreDispInfo(2).ByIndex(0));
    }

    /// <summary>The no-neighbour index 0xFFFF reads as null.</summary>
    [Fact]
    public void TheNoNeighbourIndexReadsAsNull()
    {
        CoreDispInfo core = new(2);
        core.SetListBase([core]);

        Assert.Null(core.ByIndex(DispSubNeighbor.NoNeighbor));
    }

    /// <summary>
    /// A corner point is the DISPLACED corner, which is what corner-neighbour
    /// matching compares.
    /// </summary>
    [Fact]
    public void ACornerPointIsTheDisplacedCorner()
    {
        CoreDispInfo core = DispFixtures.Core(
            DispFixtures.Heightfield(2, Floor[0], (_, _) => 12), Floor);

        Assert.Equal(new Vec3(0, 0, 12), core.CornerPoint(0));
    }
}
