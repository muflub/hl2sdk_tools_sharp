using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Light;

internal static class Geometry
{
    internal static LightGeometry Load(LightTestMap map, ComplianceOptions? compliance = null) =>
        LightGeometry.Load(map.Build(), VradLightingRange.Ldr, compliance ?? ComplianceOptions.Correct);

    /// <summary>One cluster that sees itself.</summary>
    internal static byte[] OneClusterVis() => LightTestMap.VisLump([[true]]);

    internal static PatchSet Patches(LightTestMap map, out LightGeometry geometry, float maxChop = 4)
    {
        geometry = Load(map);
        return PatchBuilder.Build(geometry, map.Entities, new TextureLightTable(new(), "box"), maxChop);
    }

    /// <summary>A 256x256 floor at lightmapscale 16: 16 x 16 luxels.</summary>
    internal static LightTestMap Floor(SurfaceFlags flags = 0, float size = 256)
    {
        LightTestMap map = new();
        int tex = map.AddTexture("concrete/floor", flags);
        map.AddFloor(tex, 0, 0, size, size, 0);
        map.Visibility = OneClusterVis();
        return map;
    }
}

public sealed class PatchBuilderTests
{
    [Fact]
    public void EveryFaceStartsAsOnePatch()
    {
        PatchSet patches = Geometry.Patches(LightBox.Map(), out _);
        Assert.Equal(6, patches.Count);
    }

    [Fact]
    public void APatchCarriesItsFacesAreaAndCentre()
    {
        PatchSet patches = Geometry.Patches(Geometry.Floor(), out _);
        ref Patch p = ref patches.At(0);
        Assert.Equal(256f * 256f, p.Area);
        Assert.Equal(new Vec3(128, 128, 0), p.Origin);
        Assert.Equal(256f * 256f, patches.TotalArea);
    }

    [Fact]
    public void TheTextureScaleAndTheChopScaleComeFromDifferentAxes()
    {
        //: scale from textureVecs (0.25), luxscale from the
        // lightmap vecs (1/16).
        PatchSet patches = Geometry.Patches(Geometry.Floor(), out _);
        ref Patch p = ref patches.At(0);
        Assert.Equal((0.25f, 0.25f), (p.ScaleS, p.ScaleT));
        Assert.Equal(1.0f / 16.0f, p.LuxScale);
    }

    [Fact]
    public void AZeroAreaFaceIsCountedDegenerateAndGetsNoPatch()
    {
        LightTestMap map = new();
        int tex = map.AddTexture("x");
        map.AddFace(tex, new Vec3(0, 0, 1), new(0, 0, 0), new(10, 0, 0), new(20, 0, 0));
        map.Visibility = Geometry.OneClusterVis();
        PatchSet patches = Geometry.Patches(map, out _);
        Assert.Equal(0, patches.Count);
        Assert.Equal(1, patches.DegenerateFaces);
    }

    [Fact]
    public void ReflectivityIsClampedBelowOne()
    {
        LightTestMap map = new();
        int tex = map.AddTexture("white", reflectivity: new Vec3(1, 0.5f, 2));
        map.AddFloor(tex, 0, 0, 64, 64, 0);
        LightGeometry g = Geometry.Load(map);
        (_, float baseArea, Vec3 reflectivity) = PatchBuilder.BaseLightForFace(g, new(new(), "box"), 0);
        Assert.Equal(new Vec3(0.99f, 0.5f, 0.99f), reflectivity);
        Assert.Equal(64f * 64f, baseArea);
    }

    [Fact]
    public void ModelFacesBelongToTheEntityNamingTheModel()
    {
        List<SourceSharp.MapFormats.Bsp.Structs.BspEntity> e =
        [
            LightTestMap.Entity(("classname", "worldspawn")),
            LightTestMap.Entity(("classname", "func_door"), ("model", "*1")),
        ];
        Assert.Equal(1, PatchBuilder.EntityForModel(e, 1));
        Assert.Equal(0, PatchBuilder.EntityForModel(e, 2));
    }

    [Fact]
    public void ASkyFaceIsASkyPatch()
    {
        PatchSet patches = Geometry.Patches(Geometry.Floor(SurfaceFlags.Sky), out _);
        Assert.True(patches.At(0).Sky);
    }
}

public sealed class PatchSubdividerTests
{
    private static (PatchSet Patches, SubdivisionReport Report) Run(
        LightTestMap map, int bounces = 100, bool fast = false, float minChop = 4)
    {
        PatchSet patches = Geometry.Patches(map, out LightGeometry g);
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        SubdivisionReport r = PatchSubdivider.Subdivide(
            g, n, patches, new CompiledBspTree(g), minChop, bounces, fast, LightConstants.DefaultSmoothingThreshold);
        return (patches, r);
    }

    [Fact]
    public void ASixteenLuxelFaceSplitsIntoSixtyFourTwoLuxelLeaves()
    {
        // total 16 luxels on both axes against chop 4. The test is
        // `total >= chop`, so a 4-luxel patch splits AGAIN:
        // 16 -> 8 -> 4 -> 2 on each axis, 64 leaves under 63 parents -- the
        // 127 per face stock prints for l1_sealed_room's 16 faces (2032).
        (PatchSet patches, SubdivisionReport r) = Run(Geometry.Floor());
        Assert.Equal((1, 127), (r.PatchesBefore, r.PatchesAfter));
        Assert.Equal(64, patches.AsSpan().ToArray().Count(p => !p.HasChildren));
    }

    [Fact]
    public void NoBouncesMeansNoSubdivisionAtAll()
    {
        (_, SubdivisionReport r) = Run(Geometry.Floor(), bounces: 0);
        Assert.Equal((1, 1), (r.PatchesBefore, r.PatchesAfter));
    }

    [Fact]
    public void FastSkipsTheSplitting()
    {
        (PatchSet patches, SubdivisionReport r) = Run(Geometry.Floor(), fast: true);
        Assert.Equal(1, r.PatchesAfter);
        Assert.Equal(Patch.Invalid, patches.At(0).Parent);
    }

    [Fact]
    public void NoChopIsNeverSplit()
    {
        (_, SubdivisionReport r) = Run(Geometry.Floor(SurfaceFlags.NoChop));
        Assert.Equal(1, r.PatchesAfter);
    }

    [Fact]
    public void NoLightWithoutLightIsNeverSplit()
    {
        (_, SubdivisionReport r) = Run(Geometry.Floor(SurfaceFlags.NoLight));
        Assert.Equal(1, r.PatchesAfter);
    }

    [Fact]
    public void ASkyPatchIsNeverSplit()
    {
        (_, SubdivisionReport r) = Run(Geometry.Floor(SurfaceFlags.Sky));
        Assert.Equal(1, r.PatchesAfter);
    }

    [Fact]
    public void ChildrenComeFirstInTheFacesList()
    {
        // prepends in index order, so the list head is the
        // LAST patch made and a parent comes after its children.
        (PatchSet patches, _) = Run(Geometry.Floor());
        Assert.Equal(patches.Count - 1, patches.FacePatches[0]);
        Assert.Equal(0, patches.FaceParents[0]);
    }

    [Fact]
    public void OnlyLeafPatchesJoinTheClusterList()
    {
        (PatchSet patches, _) = Run(Geometry.Floor());
        int count = 0;
        for (int p = patches.ClusterChildren[0]; p != Patch.Invalid; p = patches.At(p).NextClusterChild)
        {
            Assert.False(patches.At(p).HasChildren);
            count++;
        }

        Assert.Equal(64, count);
    }

    [Fact]
    public void AnElongatedPatchBelowTheChopIsSquaredUp()
    {
        // 48 x 8 units = 3 x 0.5 luxels: nothing reaches chop 4, but 3 is more
        // than twice both other extents, so with -chop 1 the patch is split
        // anyway and its chop halved. Its children then
        // meet the edge rule once more: 1 + 2 + 4 patches.
        LightTestMap map = new();
        int tex = map.AddTexture("x");
        map.AddFloor(tex, 0, 0, 48, 8, 0);
        map.Visibility = Geometry.OneClusterVis();
        (PatchSet patches, SubdivisionReport r) = Run(map, minChop: 1);
        Assert.Equal(7, r.PatchesAfter);
        Assert.Equal(2f, patches.At(0).Chop);
    }

    [Fact]
    public void ChildPatchesTakeTheParentsPlaneAndAPhongNormal()
    {
        (PatchSet patches, _) = Run(Geometry.Floor());
        ref Patch child = ref patches.At(1);
        Assert.Equal(0, child.Parent);
        Assert.Equal(new Vec3(0, 0, 1), child.Normal);
        Assert.Equal(patches.At(0).PlaneDist, child.CachedPlaneDist);
    }
}

public sealed class FaceNeighbourTests
{
    private static LightTestMap Bent(float degrees, uint groupsA = 0, uint groupsB = 0)
    {
        // Two 64-wide faces meeting along the y axis at x = 0, the second
        // tilted up by `degrees`.
        LightTestMap map = new();
        int tex = map.AddTexture("x");
        map.AddSmoothedFace(tex, new Vec3(0, 0, 1), groupsA, new(-64, 64, 0), new(0, 64, 0), new(0, 0, 0), new(-64, 0, 0));
        float r = degrees * MathF.PI / 180;
        Vec3 n = new(-MathF.Sin(r), 0, MathF.Cos(r));
        Vec3 far = new(64 * MathF.Cos(r), 0, 64 * MathF.Sin(r));
        map.AddSmoothedFace(
            tex, n, groupsB, new(0, 64, 0), new(far.X, 64, far.Z), new(far.X, 0, far.Z), new(0, 0, 0));
        return map;
    }

    [Fact]
    public void FacesAtRightAnglesDoNotSmooth()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(LightBox.Map()), LightConstants.DefaultSmoothingThreshold);
        for (int f = 0; f < n.Count; f++)
        {
            Assert.Empty(n.Neighbours(f).ToArray());
        }
    }

    [Fact]
    public void AShallowBendSmoothsBothWays()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(20)), LightConstants.DefaultSmoothingThreshold);
        Assert.Equal([1], n.Neighbours(0).ToArray());
        Assert.Equal([0], n.Neighbours(1).ToArray());
    }

    [Fact]
    public void ASharedCornerNormalIsTheNormalisedSumOfBothFaces()
    {
        // The corner normal adds the neighbour once and the
        // face's own normal once, then normalises.
        LightGeometry g = Geometry.Load(Bent(20));
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        Vec3 expected = (n.FaceNormal(1) + n.FaceNormal(0)).Normalise().Normalised;
        Assert.Equal(expected, n.CornerNormals(0)[1]);
    }

    [Fact]
    public void ACornerWithNoNeighbourKeepsTheFaceNormal()
    {
        LightGeometry g = Geometry.Load(Bent(20));
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        Assert.Equal(new Vec3(0, 0, 1), n.CornerNormals(0)[0]);
    }

    [Fact]
    public void ASteepBendIsASeam()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(60)), LightConstants.DefaultSmoothingThreshold);
        Assert.Empty(n.Neighbours(0).ToArray());
    }

    [Fact]
    public void ASharedSmoothingGroupSmoothsEvenASteepBend()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(60, 1, 1)), LightConstants.DefaultSmoothingThreshold);
        Assert.Equal([1], n.Neighbours(0).ToArray());
    }

    [Fact]
    public void TheHardEdgeGroupsNeverSmooth()
    {
        uint hard = 0x01000000;
        FaceNeighbours n = FaceNeighbours.Build(
            Geometry.Load(Bent(10, hard | 1, hard | 1)), LightConstants.DefaultSmoothingThreshold);
        Assert.Empty(n.Neighbours(0).ToArray());
    }

    [Fact]
    public void DisjointSmoothingGroupsDoNotSmooth()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(10, 1, 2)), LightConstants.DefaultSmoothingThreshold);
        Assert.Empty(n.Neighbours(0).ToArray());
    }

    [Fact]
    public void ABentFaceIsNotFlat()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(20)), LightConstants.DefaultSmoothingThreshold);
        Assert.False(n.IsFlat(0, LightConstants.DefaultSmoothingThreshold));
    }

    [Fact]
    public void AThresholdOfOneMakesEveryFaceFlat()
    {
        FaceNeighbours n = FaceNeighbours.Build(Geometry.Load(Bent(20)), LightConstants.DefaultSmoothingThreshold);
        Assert.True(n.IsFlat(0, 1.0f));
    }

    [Fact]
    public void ThePhongNormalAtTheSharedEdgeLeansTowardTheNeighbour()
    {
        LightGeometry g = Geometry.Load(Bent(20));
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        FaceCentroids c = new(2);
        PatchSet patches = PatchBuilder.Build(g, [LightTestMap.Entity(("classname", "worldspawn"))], new(new(), "x"), 4);
        Vec3 phong = PhongNormals.Compute(g, n, patches.Centroids, 0, new Vec3(-1, 32, 0), LightConstants.DefaultSmoothingThreshold);
        Assert.True(phong.X < 0, $"phong {phong}");
        Assert.True(phong.Z < 1);
        _ = c;
    }

    [Fact]
    public void ThePhongNormalOfAFlatFaceIsTheFaceNormal()
    {
        LightGeometry g = Geometry.Load(LightBox.Map());
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        PatchSet patches = PatchBuilder.Build(g, [LightTestMap.Entity(("classname", "worldspawn"))], new(new(), "x"), 4);
        Assert.Equal(
            new Vec3(0, 0, 1),
            PhongNormals.Compute(g, n, patches.Centroids, 0, new Vec3(100, 100, 0), LightConstants.DefaultSmoothingThreshold));
    }

    [Fact]
    public void TheFourWidePhongNormalAgreesWithTheScalarOneAwayFromEstimates()
    {
        LightGeometry g = Geometry.Load(Bent(20));
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        PatchSet patches = PatchBuilder.Build(g, [LightTestMap.Entity(("classname", "worldspawn"))], new(new(), "x"), 4);
        Vec3[] spots = [new(-1, 32, 0), new(-32, 32, 0), new(-63, 1, 0), new(-10, 60, 0)];
        Vec3[] four = new Vec3[4];
        PhongNormals.ComputeFour(g, n, patches.Centroids, 0, spots, four, LightConstants.DefaultSmoothingThreshold);
        for (int i = 0; i < 4; i++)
        {
            Vec3 one = PhongNormals.Compute(g, n, patches.Centroids, 0, spots[i], LightConstants.DefaultSmoothingThreshold);
            Assert.True((one - four[i]).Length() < 1e-5f, $"{i}: {one} vs {four[i]}");
        }
    }
}

public sealed class FaceLightInfoTests
{
    private static (LightGeometry G, FaceLightInfo Info) Floor(float offset = 0)
    {
        LightTestMap map = new();
        int tex = map.AddTexture("x");
        map.AddFloor(tex, offset, offset, 256 + offset, 256 + offset, 0);
        LightGeometry g = Geometry.Load(map);
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        return (g, FaceLightInfo.Build(g, n, 0, Vec3.Zero, LightConstants.DefaultSmoothingThreshold));
    }

    [Fact]
    public void TheGridIsOneLargerThanTheLightmapSize()
    {
        (_, FaceLightInfo info) = Floor();
        Assert.Equal((17, 17), (info.Width, info.Height));
    }

    [Fact]
    public void LuxelToWorldAndBackRoundTrips()
    {
        (_, FaceLightInfo info) = Floor();
        Vec3 w = info.LuxelToWorld(3.5f, 7.25f);
        (float s, float t) = info.WorldToLuxel(w);
        Assert.Equal((3.5f, 7.25f), (s, t));
    }

    [Fact]
    public void LuxelZeroSitsAtTheFacesLightmapMinimum()
    {
        // s = x/16, t = -y/16: the minimum t is at the face's largest y.
        (_, FaceLightInfo info) = Floor();
        Assert.Equal(new Vec3(0, 256, 0), info.LuxelToWorld(0, 0));
    }

    [Fact]
    public void AnAxisParallelToTheNormalIsDegenerate()
    {
        //: |det| < 1e-20 leaves the origin at zero.
        LightTestMap map = new();
        int tex = map.AddTexture("x", sAxis: new Vec3(0, 0, 1), tAxis: new Vec3(0, 1, 0));
        map.AddFloor(tex, 0, 0, 64, 64, 0);
        LightGeometry g = Geometry.Load(map);
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        FaceLightInfo info = FaceLightInfo.Build(g, n, 0, new Vec3(1, 2, 3), LightConstants.DefaultSmoothingThreshold);
        Assert.True(info.IsDegenerate);
        Assert.Equal(new Vec3(1, 2, 3), info.LuxelOrigin);
    }

    [Fact]
    public void TheLightmapWindingIsInLuxelSpace()
    {
        (LightGeometry g, FaceLightInfo info) = Floor();
        WindingArena arena = new();
        Winding w = info.LightmapCoordWinding(arena, g);
        foreach (Vec3 p in arena.Points(w))
        {
            Assert.InRange(p.X, 0, 16);
            Assert.InRange(p.Y, 0, 16);
            Assert.Equal(0f, p.Z);
        }
    }
}

public sealed class FaceSampleBuilderTests
{
    private static FaceLight Samples(float offset = 0, bool fast = false, bool center = false, bool keep = true, float size = 256)
    {
        LightTestMap map = new();
        int tex = map.AddTexture("x");
        map.AddFloor(tex, offset, offset, size + offset, size + offset, 0);
        LightGeometry g = Geometry.Load(map);
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        FaceLightInfo info = FaceLightInfo.Build(g, n, 0, Vec3.Zero, LightConstants.DefaultSmoothingThreshold);
        FaceLight fl = new(0, 1);
        FaceSampleBuilder.CalcPoints(g, info, fl, new WindingArena(), fast, center, keep);
        return fl;
    }

    [Fact]
    public void WorldAreaPerLuxelIsTheInverseOfTheLuxelDensities()
    {
        FaceLight fl = Samples();
        Assert.Equal(256f, fl.WorldAreaPerLuxel);
    }

    [Fact]
    public void AnAlignedFaceHasOneFullSamplePerCell()
    {
        // The last row and column of the 17x17 grid are the far edge and get
        // no cell of their own.
        FaceLight fl = Samples();
        Assert.Equal(256, fl.Samples.Length);
        Assert.All(fl.Samples, s => Assert.Equal(256f, s.Area));
    }

    [Fact]
    public void ASampleSitsAtItsCellsCentroid()
    {
        FaceLight fl = Samples();
        LightSample first = fl.Samples[0];
        Assert.Equal((0, 0), (first.S, first.T));
        Assert.Equal((0.5f, 0.5f), (first.CoordS, first.CoordT));
        Assert.Equal(new Vec3(8, 248, 0), first.Position);
    }

    [Fact]
    public void LuxelsAreTheWholeGrid()
    {
        FaceLight fl = Samples();
        Assert.Equal(289, fl.Luxels.Length);
        Assert.Equal(new Vec3(16, 256, 0), fl.Luxels[1]);
    }

    [Fact]
    public void EverySampleStartsWithTheFaceNormal()
    {
        Assert.All(Samples().Samples, s => Assert.Equal(new Vec3(0, 0, 1), s.Normal));
    }

    [Fact]
    public void AnOffsetFaceHasPartialSamplesWithWindings()
    {
        // Half a luxel off the grid: the first row and column are half cells,
        // kept with world windings for supersampling.
        FaceLight fl = Samples(offset: 8);
        Assert.Contains(fl.Samples, s => s.Area < 256f && s.WindingCount > 0);
        Assert.DoesNotContain(fl.Samples, s => s.Area == 256f && s.WindingCount > 0);
    }

    [Fact]
    public void PartialWindingsAreDroppedWithoutSupersampling()
    {
        FaceLight fl = Samples(offset: 8, keep: false);
        Assert.All(fl.Samples, s => Assert.Equal(0, s.WindingCount));
    }

    [Fact]
    public void PartialAreasSumToTheFaceArea()
    {
        FaceLight fl = Samples(offset: 8);
        Assert.Equal(256f * 256f, fl.Samples.Sum(s => s.Area), 0);
    }

    [Fact]
    public void FastPutsOneFullSampleOnEveryLuxel()
    {
        FaceLight fl = Samples(fast: true);
        Assert.Equal(289, fl.Samples.Length);
        Assert.Equal(fl.Luxels[5], fl.Samples[5].Position);
        Assert.Equal((-0.5f, 0.5f), (fl.Samples[0].MinS, fl.Samples[0].MaxS));
    }

    [Fact]
    public void CenterSamplesCutsAtTheHalfLuxel()
    {
        // do_centersamples: offset 0.5, so row 0 and column 0 are [0, 0.5]:
        // the first cell is a quarter luxel.
        FaceLight fl = Samples(center: true);
        Assert.Equal(64f, fl.Samples[0].Area);
    }
}

public sealed class FaceLightStyleTests
{
    [Fact]
    public void AStyleTakesTheFirstFreeSlot()
    {
        FaceLight fl = new(0, 1, new LightSample[3]);
        Assert.Equal(0, fl.FindOrAllocateStyle(0));
        Assert.Equal(1, fl.FindOrAllocateStyle(5));
        Assert.Equal(5, fl.Styles[1]);
        Assert.Equal(3, fl.LightFor(1, 0)!.Length);
    }

    [Fact]
    public void AKnownStyleReturnsItsSlot()
    {
        FaceLight fl = new(0, 1, new LightSample[1]);
        fl.FindOrAllocateStyle(7);
        Assert.Equal(0, fl.FindOrAllocateStyle(7));
    }

    [Fact]
    public void AFifthStyleOverflows()
    {
        FaceLight fl = new(0, 1, new LightSample[1]);
        for (int s = 1; s <= 4; s++)
        {
            fl.FindOrAllocateStyle(s);
        }

        Assert.Equal(-1, fl.FindOrAllocateStyle(9));
    }

    [Fact]
    public void ABumpedFaceAllocatesFourNormals()
    {
        FaceLight fl = new(0, 4, new LightSample[2]);
        fl.AllocateStyle(0);
        for (int n = 0; n < 4; n++)
        {
            Assert.NotNull(fl.LightFor(0, n));
        }
    }
}

public sealed class LightmapOffsetTests
{
    private static (LightGeometry G, FaceLight?[] Lights) Two(SurfaceFlags secondFlags = 0)
    {
        LightTestMap map = new();
        int a = map.AddTexture("a");
        int b = map.AddTexture("b", secondFlags);
        map.AddFloor(a, 0, 0, 64, 64, 0);
        map.AddFloor(b, 0, 0, 64, 64, 64);
        LightGeometry g = Geometry.Load(map);
        FaceLight f0 = new(0, 1);
        f0.Styles[0] = 0;
        FaceLight f1 = new(1, (secondFlags & SurfaceFlags.BumpLight) != 0 ? 4 : 1);
        f1.Styles[0] = 0;
        f1.Styles[1] = 3;
        return (g, [f0, f1]);
    }

    [Fact]
    public void EachFaceReservesItsAveragesFirst()
    {
        //: lightofs points past styles*4 bytes of
        // average colour. 64x64 at 1/16 is 4x4 -> 5x5 = 25 luxels.
        (LightGeometry g, FaceLight?[] lights) = Two();
        LightmapLayout layout = LightmapOffsets.Compute(g, lights, false);
        Assert.Equal(4, layout.LightOffsets[0]);
        Assert.Equal(4 + (25 * 4) + (2 * 4), layout.LightOffsets[1]);
        Assert.Equal(layout.LightOffsets[1] + (25 * 4 * 2), layout.LightDataSize);
    }

    [Fact]
    public void ABumpedFaceHasFourLightmapsPerStyle()
    {
        (LightGeometry g, FaceLight?[] lights) = Two(SurfaceFlags.BumpLight);
        LightmapLayout layout = LightmapOffsets.Compute(g, lights, false);
        Assert.Equal(layout.LightOffsets[1] + (25 * 4 * 2 * 4), layout.LightDataSize);
    }

    [Fact]
    public void ASpecialFaceIsSkipped()
    {
        (LightGeometry g, FaceLight?[] lights) = Two(SurfaceFlags.Sky);
        LightmapLayout layout = LightmapOffsets.Compute(g, lights, false);
        Assert.Equal(-1, layout.LightOffsets[1]);
    }

    [Fact]
    public void AnUnlitFaceIsSkipped()
    {
        (LightGeometry g, _) = Two();
        LightmapLayout layout = LightmapOffsets.Compute(g, [null, null], false);
        Assert.Equal((-1, -1, 0), (layout.LightOffsets[0], layout.LightOffsets[1], layout.LightDataSize));
    }

    [Fact]
    public void DlightMapForcesStyleZeroIntoSlotOne()
    {
        (LightGeometry g, FaceLight?[] lights) = Two();
        LightmapLayout layout = LightmapOffsets.Compute(g, lights, true);
        Assert.Equal(0, layout.Styles[1]);
        Assert.Equal(4 + (2 * 4), layout.LightOffsets[0] + 4);
    }

    [Fact]
    public void TheArrayMustBeOnePerFace()
    {
        (LightGeometry g, _) = Two();
        Assert.Throws<ArgumentException>(() => LightmapOffsets.Compute(g, [null], false));
    }
}

public sealed class PatchLightingTests
{
    private static (PatchSet Patches, FaceLightContext Context) Setup(int bounces = 100)
    {
        LightTestMap map = Geometry.Floor();
        PatchSet patches = Geometry.Patches(map, out LightGeometry g);
        FaceNeighbours n = FaceNeighbours.Build(g, LightConstants.DefaultSmoothingThreshold);
        PatchSubdivider.Subdivide(g, n, patches, new CompiledBspTree(g), 4, bounces, false, LightConstants.DefaultSmoothingThreshold);
        DirectLightingSettings settings = new() { Bounces = bounces };
        FaceLightContext context = new(
            g, n, patches, new CompiledBspTree(g), settings,
            new DirectLightGatherer([], 0, settings, new CompiledBspTree(g), g.Leaves, SkyCameras.None));
        return (patches, context);
    }

    private static FaceLight UniformLight(float value)
    {
        FaceLight fl = new(0, 1, new LightSample[256]);
        for (int t = 0; t < 16; t++)
        {
            for (int s = 0; s < 16; s++)
            {
                fl.Samples[s + (t * 16)] = new LightSample
                {
                    S = s,
                    T = t,
                    Area = 256,
                    Position = new Vec3((s * 16) + 8, 256 - ((t * 16) + 8), 0),
                };
            }
        }

        fl.Styles[0] = 0;
        fl.AllocateStyle(0);
        LightingValue[] v = fl.LightFor(0, 0)!;
        for (int i = 0; i < v.Length; i++)
        {
            v[i].Lighting = new Vec3(value, value, value);
        }

        return fl;
    }

    [Fact]
    public void AUniformlyLitFacesPatchesAllGetThatLight()
    {
        (PatchSet patches, FaceLightContext context) = Setup();
        PatchLighting.BuildPatchLights(context, 0, UniformLight(10));
        foreach (Patch p in patches.AsSpan())
        {
            Assert.Equal(10f, p.DirectLight.X, 4);
            Assert.Equal(p.DirectLight, p.TotalLight.Flat);
        }
    }

    [Fact]
    public void ALightAveragingBelowOneIsNotSentToPatches()
    {
        (PatchSet patches, FaceLightContext context) = Setup();
        PatchLighting.BuildPatchLights(context, 0, UniformLight(0.5f));
        Assert.All(patches.AsSpan().ToArray(), p => Assert.Equal(0f, p.SampleArea));
    }

    [Fact]
    public void NoBouncesSendsNothing()
    {
        (PatchSet patches, FaceLightContext context) = Setup(bounces: 0);
        PatchLighting.BuildPatchLights(context, 0, UniformLight(10));
        Assert.All(patches.AsSpan().ToArray(), p => Assert.Equal(Vec3.Zero, p.DirectLight));
    }

    [Fact]
    public void ParentsCollectTheirChildrensSampleArea()
    {
        (PatchSet patches, FaceLightContext context) = Setup();
        PatchLighting.BuildPatchLights(context, 0, UniformLight(10));
        ref Patch root = ref patches.At(0);
        ref Patch c1 = ref patches.At(root.Child1);
        ref Patch c2 = ref patches.At(root.Child2);
        Assert.Equal(c1.SampleArea + c2.SampleArea, root.SampleArea);
    }

    [Fact]
    public void AmbientIsAddedToStyleZero()
    {
        (PatchSet patches, FaceLightContext context) = Setup();
        FaceLight fl = UniformLight(0);
        context = context with { Settings = context.Settings with { Ambient = new Vec3(1, 2, 3) } };
        PatchLighting.BuildPatchLights(context, 0, fl);
        Assert.Equal(new Vec3(1, 2, 3), fl.LightFor(0, 0)![0].Lighting);
        _ = patches;
    }

    [Fact]
    public void ASampleOutsideEveryPatchIsDropped()
    {
        (PatchSet patches, _) = Setup();
        LightSample far = new() { Position = new Vec3(5000, 5000, 0), Area = 1 };
        PatchLighting.AddSampleToPatch(patches, 100, far, new Vec3(10, 10, 10), 0);
        Assert.All(patches.AsSpan().ToArray(), p => Assert.Equal(0f, p.SampleArea));
    }
}
