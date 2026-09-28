//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// <see cref="StockQuirk.VbspVectorNormalise"/> on the displacement surface:
/// <see cref="CoreDispSurface.GetNormal"/> and
/// <see cref="CoreDispSurface.LongestInU"/>, and the callers that set
/// <see cref="CoreDispSurface.StockNormalise"/> from the compile's compliance.
/// </summary>
/// <remarks>
/// As in <c>VbspVectorNormaliseTests</c>, the inputs that separate the
/// estimate from an exact divide depend on the CPU, so each fact scans a
/// fixed family and requires at least one member to separate them.
/// </remarks>
public sealed class CoreDispSurfaceNormaliseTests
{
    /// <summary>
    /// Under Stock, vrad's displacement loader gives the four corners the base
    /// quad's normal as the estimate normalises it, which for some slope is
    /// not the exact normal.
    /// </summary>
    [Fact]
    public void StockLoadTakesTheEstimatedBaseQuadNormal()
    {
        (Vec3[] quad, Vec3 cross) = FindSeparatingSlope();

        CoreDispInfo[] cores = DispLightingLoader.Load(SlopedBsp(quad), ComplianceOptions.Stock);

        Assert.True(cores[0].Surface.StockNormalise);
        Assert.All(cores[0].Surface.Normals.ToArray(), n => Assert.Equal(cross.NormaliseLikeStock().Normalised, n));
        Assert.NotEqual(cross.Normalise().Normalised, cores[0].Surface.Normals[0]);
    }

    /// <summary>
    /// Under Correct, the corners take the exact normal of the same slope.
    /// </summary>
    [Fact]
    public void CorrectLoadTakesTheExactBaseQuadNormal()
    {
        (Vec3[] quad, Vec3 cross) = FindSeparatingSlope();

        CoreDispInfo[] cores = DispLightingLoader.Load(SlopedBsp(quad), ComplianceOptions.Correct);

        Assert.False(cores[0].Surface.StockNormalise);
        Assert.All(cores[0].Surface.Normals.ToArray(), n => Assert.Equal(cross.Normalise().Normalised, n));
        Assert.NotEqual(cross.NormaliseLikeStock().Normalised, cores[0].Surface.Normals[0]);
    }

    /// <summary>
    /// Under Stock, an exact tie between the quad's extents along the two
    /// lightmap axes is broken by the estimate: for some pair of axis lengths
    /// the estimated u axis comes out shorter than the v axis, and u loses.
    /// </summary>
    [Fact]
    public void StockLongestInUBreaksATieOnTheEstimate()
    {
        (Vec3 u, Vec3 v) = FindTieBreakingAxes();

        Assert.False(Surface(stock: true).LongestInU(u, v));
    }

    /// <summary>
    /// Under Correct, the same square measures the same along both axes, and
    /// the tie goes to u as the comparison is written.
    /// </summary>
    [Fact]
    public void CorrectLongestInUKeepsTheExactTie()
    {
        (Vec3 u, Vec3 v) = FindTieBreakingAxes();

        Assert.True(Surface(stock: false).LongestInU(u, v));
    }

    /// <summary>
    /// <see cref="CoreDispSurface.GetNormal"/> follows the flag on its own,
    /// whoever set it: the estimate when set, the exact divide when not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetNormalFollowsTheSurfacesFlag(bool stock)
    {
        (Vec3[] quad, Vec3 cross) = FindSeparatingSlope();
        CoreDispSurface surface = new() { StockNormalise = stock };
        for (int i = 0; i < 4; i++)
        {
            surface.SetPoint(i, quad[i]);
        }

        Vec3 expected = stock ? cross.NormaliseLikeStock().Normalised : cross.Normalise().Normalised;
        Assert.Equal(expected, surface.GetNormal());
    }

    /// <summary>A surface nobody configured divides exactly.</summary>
    [Fact]
    public void ANewSurfaceDividesExactly()
    {
        Assert.False(new CoreDispSurface().StockNormalise);
    }

    /// <summary>
    /// vbsp's lump builder hands each surface the compile's side of the quirk,
    /// and only this quirk decides it.
    /// </summary>
    [Fact]
    public void TheLumpBuilderSetsTheSurfaceFromTheCompliance()
    {
        Vec3[] floor = DispFixtures.UnitFloor();
        MapDisplacement disp = DispFixtures.Heightfield(2, floor[0]);

        Assert.True(Built(ComplianceOptions.Stock).Surface.StockNormalise);
        Assert.False(Built(ComplianceOptions.Correct).Surface.StockNormalise);
        Assert.True(Built(ComplianceOptions.Correct.Flipping(StockQuirk.VbspVectorNormalise)).Surface.StockNormalise);
        Assert.False(Built(ComplianceOptions.Stock.Flipping(StockQuirk.VbspVectorNormalise)).Surface.StockNormalise);

        CoreDispInfo Built(ComplianceOptions c) => DispFixtures.Build([(disp, floor)], c).Results[0].Core;
    }

    /// <summary>
    /// vrad's loader does the same, and a policy with only this quirk flipped
    /// moves the corner normals by itself.
    /// </summary>
    [Fact]
    public void TheLightingLoaderSetsTheSurfaceFromTheCompliance()
    {
        (Vec3[] quad, Vec3 cross) = FindSeparatingSlope();
        BspData bsp = SlopedBsp(quad);

        CoreDispInfo flipped = DispLightingLoader.Load(bsp, ComplianceOptions.Correct.Flipping(StockQuirk.VbspVectorNormalise))[0];

        Assert.True(flipped.Surface.StockNormalise);
        Assert.Equal(cross.NormaliseLikeStock().Normalised, flipped.Surface.Normals[0]);
    }

    /// <summary>
    /// A family of base quads tilted about x by <c>h</c> units over 256, whose
    /// normal <c>(0, -256h, 65536)</c> is non-axial; returns the first whose
    /// normal the estimate rounds differently from the exact divide, with the
    /// cross product <see cref="CoreDispSurface.GetNormal"/> normalises.
    /// </summary>
    private static (Vec3[] Quad, Vec3 Cross) FindSeparatingSlope()
    {
        for (int h = 1; h <= 255; h += 2)
        {
            Vec3[] quad =
            [
                new Vec3(0, 0, 0),
                new Vec3(0, 256, h),
                new Vec3(256, 256, h),
                new Vec3(256, 0, 0),
            ];

            Vec3 cross = Vec3.Cross(quad[3] - quad[0], quad[1] - quad[0]);
            if (cross.NormaliseLikeStock().Normalised != cross.Normalise().Normalised)
            {
                return (quad, cross);
            }
        }

        Assert.Fail("no slope separates the estimate from the exact divide on this CPU");
        return default;
    }

    /// <summary>
    /// A pair of axis-aligned lightmap axes, u along x and v along y, of
    /// different lengths: exactly normalised both are unit vectors, so a
    /// square measures the same along each; returns the first pair whose
    /// estimated u component comes out below the estimated v one.
    /// </summary>
    private static (Vec3 U, Vec3 V) FindTieBreakingAxes()
    {
        List<float> lengths = [];
        for (int i = 1; i <= 64; i++)
        {
            lengths.Add((i * 0.0371f) + 0.003f);
        }

        foreach (float ku in lengths)
        {
            foreach (float kv in lengths)
            {
                Vec3 u = new(ku, 0, 0);
                Vec3 v = new(0, kv, 0);

                if (u.NormaliseLikeStock().Normalised.X < v.NormaliseLikeStock().Normalised.Y)
                {
                    return (u, v);
                }
            }
        }

        Assert.Fail("every estimated axis normalises to the same component on this CPU");
        return default;
    }

    /// <summary>A 256-unit square quad on the floor, flagged for one side.</summary>
    private static CoreDispSurface Surface(bool stock)
    {
        CoreDispSurface surface = new() { StockNormalise = stock };
        Vec3[] floor = DispFixtures.UnitFloor();
        for (int i = 0; i < 4; i++)
        {
            surface.SetPoint(i, floor[i]);
        }

        return surface;
    }

    /// <summary>
    /// A BSP holding just what <see cref="DispLightingLoader.Load"/> reads: one
    /// power-2 displacement on a four-edge face over <paramref name="quad"/>.
    /// </summary>
    private static BspData SlopedBsp(Vec3[] quad)
    {
        (IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps) =
            DispFixtures.Build([(DispFixtures.Heightfield(2, quad[0]), quad)]);

        List<DEdge> edges = [default];
        List<int> surfEdges = [];
        for (int k = 0; k < 4; k++)
        {
            DEdge e = default;
            e.V[0] = (ushort)k;
            e.V[1] = (ushort)((k + 1) % 4);
            edges.Add(e);
            surfEdges.Add(edges.Count - 1);
        }

        DFace face = default;
        face.FirstEdge = 0;
        face.NumEdges = 4;
        face.TexInfo = 0;
        face.DispInfo = 0;

        TexInfo tex = default;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = DispFixtures.LuxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[5] = -DispFixtures.LuxelsPerUnit;

        BspData bsp = new();
        bsp[BspLump.DispInfo] = BspStructView.ToLump<DispInfo>([results[0].Info], 0);
        bsp[BspLump.DispVerts] = BspStructView.ToLump<DispVert>(lumps.Verts.ToArray(), 0);
        bsp[BspLump.DispTris] = BspStructView.ToLump<DispTri>(lumps.Tris.ToArray(), 0);
        bsp[BspLump.Faces] = BspStructView.ToLump<DFace>([face], 0);
        bsp[BspLump.Vertexes] = BspStructView.ToLump<Vec3>(quad, 0);
        bsp[BspLump.Edges] = BspStructView.ToLump<DEdge>(edges.ToArray(), 0);
        bsp[BspLump.SurfEdges] = BspStructView.ToLump<int>(surfEdges.ToArray(), 0);
        bsp[BspLump.TexInfo] = BspStructView.ToLump<TexInfo>([tex], 0);
        return bsp;
    }
}
