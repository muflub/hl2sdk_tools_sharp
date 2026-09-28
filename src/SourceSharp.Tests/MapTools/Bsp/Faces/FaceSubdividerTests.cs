//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Cutting a face down until its lightmap fits
///(<c>SubdivideFace</c>).
/// </summary>
/// <remarks>
/// The fixture's texinfo is one luxel per world unit on both lightmap axes, so
/// a face's luxel extent equals its extent in units and the 32-luxel threshold
/// is 32 units.
/// </remarks>
public class FaceSubdividerTests
{
    [Fact]
    public void AFaceInsideTheLightmapLimitIsLeftAlone()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 32f, 32f);
        Face? head = subdivider.SubdivideFace(face, face);

        Assert.Same(face, head);
        Assert.False(face.IsDead);
        Assert.Equal(0, context.Counters.Subdivided);
    }

    [Fact]
    public void AFaceOverTheLimitIsSplitInTwo()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 48f, 16f);
        Face? head = subdivider.SubdivideFace(face, face);

        Assert.True(face.IsDead);
        Assert.NotNull(face.Split[0]);
        Assert.NotNull(face.Split[1]);
        Assert.Equal(1, context.Counters.Subdivided);
        Assert.NotSame(face, head);
    }

    [ReferenceRsqrtFact]
    public void TheBackPieceIsOneLuxelShortOfTheLimit()
    {
        // Stock's estimate sizes the cut, so the expected value is per CPU
        // family (StockQuirk.VbspVectorNormalise); the Correct side is exact
        // everywhere and pinned by TheCorrectCutIsExactlyOneLuxelShort.
        FaceBuildContext context = FaceStageFixture.Create(compliance: ComplianceOptions.Stock);
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 48f, 16f);
        subdivider.SubdivideFace(face, face);

        // dist = (mins + g_maxLightmapDimension - 1) /
        // luxelsPerWorldUnit, which at one luxel per unit and mins 0 is 31.
        // hang the BACK winding on split[1], and the back
        // of a +x plane at 31 is the x <= 31 side.
        context.Windings.Bounds(face.Split[1]!.Winding, out Vec3 mins, out Vec3 maxs);

        Assert.Equal(0f, mins.X);
        Assert.Equal(VendorGolden.Expected("face-subdivider.back-max-x", 31f, maxs.X), maxs.X);
    }

    [ReferenceRsqrtFact]
    public void TheFrontPieceIsTheFarSideOfTheCut()
    {
        // Stock's estimate sizes the cut, so the expected value is per CPU
        // family (StockQuirk.VbspVectorNormalise); the Correct side is exact
        // everywhere and pinned by TheCorrectCutIsExactlyOneLuxelShort.
        FaceBuildContext context = FaceStageFixture.Create(compliance: ComplianceOptions.Stock);
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 48f, 16f);
        subdivider.SubdivideFace(face, face);

        // The subdivider clips against +x (the luxel axis) at 31 and
        // hangs the FRONT winding -- the side the normal points
        // into, x >= 31 -- on split[0].
        context.Windings.Bounds(face.Split[0]!.Winding, out Vec3 mins, out Vec3 maxs);

        Assert.Equal(VendorGolden.Expected("face-subdivider.front-min-x", 31f, mins.X), mins.X);
        Assert.Equal(48f, maxs.X);
    }

    [ReferenceRsqrtFact]
    public void AFaceOverTheLimitOnBothAxesIsSplitOnXFirst()
    {
        // Stock's estimate sizes the cut, so the expected value is per CPU
        // family (StockQuirk.VbspVectorNormalise); the Correct side is exact
        // everywhere and pinned by TheCorrectCutIsExactlyOneLuxelShort.
        FaceBuildContext context = FaceStageFixture.Create(compliance: ComplianceOptions.Stock);
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 96f, 96f);
        subdivider.SubdivideFace(face, face);

        // Axis 0 is x, and returns out of the axis loop as soon
        // as it splits, so the first cut is always the x one: the back piece
        //(split[1]) ends at x = 31 and still spans the whole
        // of y.
        context.Windings.Bounds(face.Split[1]!.Winding, out Vec3 mins, out Vec3 maxs);

        Assert.Equal(VendorGolden.Expected("face-subdivider.both-axes-back-max-x", 31f, maxs.X), maxs.X);
        Assert.Equal(96f, maxs.Y - mins.Y);
    }

    /// <summary>
    /// Under Correct the cut is an exact divide, so the back piece ends at
    /// exactly 31 luxels on every CPU: a plain fact, not a per-vendor golden.
    /// </summary>
    [Fact]
    public void TheCorrectCutIsExactlyOneLuxelShort()
    {
        FaceBuildContext context = FaceStageFixture.Create(compliance: ComplianceOptions.Correct);
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 48f, 16f);
        subdivider.SubdivideFace(face, face);

        context.Windings.Bounds(face.Split[1]!.Winding, out _, out Vec3 backMaxs);
        context.Windings.Bounds(face.Split[0]!.Winding, out Vec3 frontMins, out _);

        Assert.Equal(31f, backMaxs.X);
        Assert.Equal(31f, frontMins.X);
    }

    [Fact]
    public void ANoLightFaceIsNeverSubdivided()
    {
        FaceBuildContext context = FaceStageFixture.Create();

        // Re-register the texinfo with SURF_NOLIGHT.
        SourceSharp.MapFormats.Bsp.Structs.TexInfo noLight = context.TexInfos[0];
        noLight.Flags = (int)SurfaceFlags.NoLight;
        int index = context.TexInfos.Add(noLight);

        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 512f, 512f);
        face.TexInfo = index;

        Assert.Same(face, subdivider.SubdivideFace(face, face));
        Assert.False(face.IsDead);
    }

    [Fact]
    public void ADeadFaceIsNeverSubdivided()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 512f, 512f);
        face.Merged = face;

        Assert.Same(face, subdivider.SubdivideFace(face, face));
        Assert.Equal(0, context.Counters.Subdivided);
    }

    [Fact]
    public void EveryPieceOfASubdividedFaceIsInsideTheLimit()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        FaceSubdivider subdivider = new(context);

        Face face = Rect(context, 200f, 100f);
        Face? head = subdivider.SubdivideFace(face, face);

        int live = 0;

        for (Face? f = head; f is not null; f = f.Next)
        {
            if (f.IsDead)
            {
                continue;
            }

            live++;
            context.Windings.Bounds(f.Winding, out Vec3 mins, out Vec3 maxs);

            Assert.True(
                maxs.X - mins.X <= context.MaxLightmapDimension + 0.001f,
                $"a piece is {maxs.X - mins.X} luxels wide");
            Assert.True(
                maxs.Y - mins.Y <= context.MaxLightmapDimension + 0.001f,
                $"a piece is {maxs.Y - mins.Y} luxels tall");
        }

        Assert.True(live >= 4 * 4, $"only {live} pieces from a 200x100 face");
    }

    [Fact]
    public void SubdivideFaceListWalksTheOriginalChainAndNotThePieces()
    {
        FaceBuildContext context = FaceStageFixture.Create();
        FaceSubdivider subdivider = new(context);

        Face a = Rect(context, 48f, 16f);
        Face b = Rect(context, 16f, 16f);
        a.Next = b;

        Face? head = subdivider.SubdivideFaceList(a);

        // b is untouched and still reachable; a was split.
        Assert.True(a.IsDead);
        Assert.False(b.IsDead);
        Assert.NotSame(a, head);
        Assert.Equal(1, context.Counters.Subdivided);
    }

    private static Face Rect(FaceBuildContext context, float width, float height) =>
        FaceStageFixture.Face(context,
        [
            new Vec3(0f, 0f, 0f),
            new Vec3(width, 0f, 0f),
            new Vec3(width, height, 0f),
            new Vec3(0f, height, 0f),
        ]);
}
