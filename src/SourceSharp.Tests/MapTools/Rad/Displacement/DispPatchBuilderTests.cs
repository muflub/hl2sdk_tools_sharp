using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

using static SourceSharp.Tests.MapTools.Rad.Displacement.DispTestSurfaces;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// <c>CreateParentPatches</c>, <c>CreateChildPatches</c> and friends
/// on a flat 256-unit power-2 floor at 16
/// units per luxel (so <c>dispchop</c> 8 makes the minimum edge 128).
/// </summary>
public sealed class DispPatchBuilderTests
{
    private static readonly DirectLightingSettings Settings = new();

    private static (VradDispSurface Surface, PatchSet Patches, int Root) Root(Func<int, int, float>? height = null)
    {
        CoreDispInfo core = Core(height);
        core.Surface.Handle = 0;
        VradDispSurface s = VradDispSurface.Create(core, Tex(), Settings, false);
        PatchSet patches = new(1, 0, new WindingArena());
        DispPatchBuilder.CreateParentPatch(s, patches, Settings, false, Vec3.Zero, 0, new Vec3(0.5f, 0.5f, 0.5f));
        return (s, patches, patches.Count - 1);
    }

    [Fact]
    public void TheRootPatchAreaIsTheCornerQuads()
    {
        (_, PatchSet p, int root) = Root();
        Assert.Equal(65536.0f, p.At(root).Area);
    }

    [Fact]
    public void TheRootPatchIsCentredOnItsCorners()
    {
        (_, PatchSet p, int root) = Root();
        Assert.Equal(new Vec3(128, 128, 0), p.At(root).Origin);
    }

    [Fact]
    public void TheRootPatchNormalIsTheCornerQuadsNormal()
    {
        (_, PatchSet p, int root) = Root();
        Assert.Equal(new Vec3(0, 0, 1), p.At(root).Normal);
    }

    [Fact]
    public void TheRootPatchHeadsItsFacesList()
    {
        (_, PatchSet p, int root) = Root();
        Assert.Equal(root, p.FacePatches[0]);
        Assert.Equal(Patch.Invalid, p.At(root).Next);
    }

    [Fact]
    public void TheRootPatchTakesDispChopAndUnitScales()
    {
        (_, PatchSet p, int root) = Root();
        Assert.Equal(8.0f, p.At(root).Chop);
        Assert.Equal(1.0f, p.At(root).ScaleS);
        Assert.Equal(1.0f, p.At(root).ScaleT);
        Assert.False(p.At(root).Sky);
    }

    [Fact]
    public void ThePatchMaxIsSeededWithFltMin()
    {
        //: a floor at z = 0 gets a max z of FLT_MIN, not 0.
        (_, PatchSet p, int root) = Root();
        Assert.Equal(DispPatchBuilder.FltMin, p.At(root).Maxs.Z);
    }

    [Fact]
    public void AFlatPowerTwoFloorSplitsIntoThirtyOnePatches()
    {
        // Root -> 2 -> 4 -> 8 -> 16 triangles; the 16 have area 4096 < 8192.
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Assert.Equal(31, p.Count);
    }

    [Fact]
    public void TheLeafPatchesTileTheSurface()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        float leafArea = p.AsSpan().ToArray().Where(x => !x.HasChildren).Sum(x => x.Area);
        Assert.Equal(65536.0f, leafArea);
    }

    [Fact]
    public void TheRootSplitsOnTheLastToFirstDiagonal()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Patch c1 = p.At(p.At(root).Child1);
        Assert.Equal((short)24, c1.Index0);
        Assert.Equal((short)0, c1.Index1);
        Assert.Equal((short)20, c1.Index2);
    }

    [Fact]
    public void AGridChildSplitsAtTheMidpointOfIndices0And1()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Patch tri = p.At(p.At(root).Child1);
        Patch child = p.At(tri.Child1);
        Assert.Equal((short)((24 + 0) / 2), child.Index2);
        Assert.Equal(tri.Index2, child.Index0);
        Assert.Equal(tri.Index0, child.Index1);
    }

    [Fact]
    public void ChildrenLinkToTheirParent()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Assert.Equal(root, p.At(p.At(root).Child1).Parent);
        Assert.Equal(root, p.At(p.At(root).Child2).Parent);
    }

    [Fact]
    public void ChildrenInheritReflectivityAndFaceBounds()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Patch child = p.At(p.At(root).Child2);
        Assert.Equal(new Vec3(0.5f, 0.5f, 0.5f), child.Reflectivity);
        Assert.Equal(p.At(root).FaceMaxs, child.FaceMaxs);
    }

    [Fact]
    public void AChildTriangleIsCentredOnItsThreePoints()
    {
        (VradDispSurface s, PatchSet p, int root) = Root();
        DispPatchBuilder.SubdividePatch(s, p, root, Settings);
        Patch c = p.At(p.At(root).Child1);

        // (256,256), (0,0), (0,256).
        Assert.True(Near(c.Origin, new Vec3(256f / 3f, 512f / 3f, 0), 1e-3f), c.Origin.ToString());
    }

    [Fact]
    public void AnEdgeBelowTheChopIsNotSplit()
    {
        // At 64 units per luxel the minimum edge is 512 > 256.
        CoreDispInfo core = Core();
        core.Surface.Handle = 0;
        VradDispSurface s = VradDispSurface.Create(core, Tex(64), Settings, false);
        PatchSet p = new(1, 0, new WindingArena());
        DispPatchBuilder.CreateParentPatch(s, p, Settings, false, Vec3.Zero, 0, Vec3.Zero);
        DispPatchBuilder.SubdividePatch(s, p, 0, Settings);
        Assert.Equal(1, p.Count);
    }

    [Fact]
    public void PastTheGridsLevelsTrianglesSplitInWorldSpace()
    {
        // At 4 units per luxel the minimum edge is 32: a power-2 grid runs out
        // of vertices after four levels and CreateChildPatchesSub takes over,
        // whose children carry no grid indices.
        CoreDispInfo core = Core();
        core.Surface.Handle = 0;
        VradDispSurface s = VradDispSurface.Create(core, Tex(4), Settings, false);
        PatchSet p = new(1, 0, new WindingArena());
        DispPatchBuilder.CreateParentPatch(s, p, Settings, false, Vec3.Zero, 0, Vec3.Zero);
        DispPatchBuilder.SubdividePatch(s, p, 0, Settings);
        Assert.Contains(p.AsSpan().ToArray(), x => x.Index0 == -1 && x.Index1 == -1 && x.Index2 == -1);
    }
}
