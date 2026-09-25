using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// <c>EmitPhysCollision</c>'s own logic (<c>ivp.cpp</c>, <c>disp_ivp.cpp</c>)
/// against the managed <see cref="FakeCollisionCooker"/>: the unit tier.
/// </summary>
public class PhysCollisionEmitterTests
{
    private static readonly Vec3 Lo = new(-32, -32, -32);
    private static readonly Vec3 Hi = new(32, 32, 32);

    private static async Task<(PhysCollisionResult Result, FakeCollisionCooker Cooker)> EmitAsync(
        CollisionFixture fixture, int models = 1, bool virtualMesh = true)
    {
        FakeCollisionCooker cooker = new(virtualMesh);
        PhysCollisionResult result = await PhysCollisionEmitter.EmitAsync(fixture.Build(models), cooker);
        return (result, cooker);
    }

    private static string Key(PhysCollisionResult result, int record = 0) => result.Models[record].KeyText;

    [Fact]
    public async Task TheWorldsSolidBrushesAreOneStaticSolidOfMaskSolid()
    {
        // ivp.cpp:1309 and :292-295.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.StartsWith("staticsolid {\n\"index\" \"0\"\n\"contents\" \"33570827\"\n}\n", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualTerrainIsDeclaredWhenTheLibrarySupportsIt()
    {
        // ivp.cpp:1560.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Contains("virtualterrain {}\n", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoVirtualMeshDeclaresNoVirtualTerrainAndWritesNoPhysDisp()
    {
        CollisionFixture f = new() { NoVirtualMesh = true };
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.DoesNotContain("virtualterrain", Key(r), StringComparison.Ordinal);
        Assert.Null(r.PhysDisp);
    }

    [Fact]
    public async Task TheVirtualMeshRoadWritesAPhysDispEvenWithNoDisplacements()
    {
        // Disp_BuildVirtualMesh always runs, so stock's catalogue maps carry a
        // two-byte PHYSDISP holding a zero count.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Equal(new byte[] { 0, 0 }, r.PhysDisp);
    }

    [Fact]
    public async Task EachTriangleTakesTheMaterialOfTheSideItFaces()
    {
        // ivp.cpp:1286-1305: TriangleNormal then FindBrushSide, per triangle.
        CollisionFixture f = new();
        string[] props = ["metal", "wood", "wood", "shell", "shell", "water"];
        f.Box(0, Lo, Hi, CollisionContents.Solid, [.. props.Select(f.TexInfoFor)]);

        (PhysCollisionResult r, _) = await EmitAsync(f);
        string blob = Encoding.ASCII.GetString(r.Models[0].Solids[0]);

        // Faces +X -X +Y -Y +Z -Z, two triangles each; table order is first use:
        // metal=1 wood=2 shell=3 water=4.
        Assert.EndsWith("m=1,1,2,2,2,2,3,3,3,3,4,4", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMaterialTableListsPropertiesInFirstUseOrder()
    {
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("wood"));
        f.Box(0, Lo + new Vec3(100, 0, 0), Hi + new Vec3(100, 0, 0), CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.EndsWith("materialtable {\n\"wood\" \"1\"\n\"metal\" \"2\"\n}\n", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASideWithNoSurfacePropIsDefaultInTheMaterialTable()
    {
        // ivp.cpp:1570-1573: propIndex < 0 is written as "default".
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor(null));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Contains("\"default\" \"1\"\n", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public void PropIndexIsOneBasedAndStopsAt126()
    {
        // ivp.cpp:375-389.
        List<int> table = [];
        for (int i = 0; i < 126; i++)
        {
            Assert.Equal(i + 1, PhysCollisionEmitter.PropIndex(table, i));
        }

        Assert.Equal(0, PhysCollisionEmitter.PropIndex(table, 999));
        Assert.Equal(5, PhysCollisionEmitter.PropIndex(table, 4));
    }

    [Fact]
    public async Task ClipBrushesAreTheirOwnStaticSolidsAfterTheSolidOne()
    {
        // BuildWorldPhysModel, ivp.cpp:1316-1318: solid, player clip, monster clip.
        CollisionFixture f = new();
        int t = f.TexInfoFor("metal");
        f.Box(0, Lo, Hi, CollisionContents.Solid, t);
        f.Box(0, Lo + new Vec3(100, 0, 0), Hi + new Vec3(100, 0, 0), CollisionContents.MonsterClip, t);
        f.Box(0, Lo + new Vec3(200, 0, 0), Hi + new Vec3(200, 0, 0), CollisionContents.PlayerClip, t);

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Contains(
            "\"contents\" \"33570827\"\n}\nstaticsolid {\n\"index\" \"1\"\n\"contents\" \"65536\"\n}\n"
            + "staticsolid {\n\"index\" \"2\"\n\"contents\" \"131072\"\n}\n",
            Key(r),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWorldIsNotShrunk()
    {
        // NO_SHRINK for model 0, ivp.cpp:1531.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (_, FakeCollisionCooker c) = await EmitAsync(f);

        Assert.All(c.Session.PlaneCalls[0], p => Assert.Equal(32f, p.Dist));
    }

    [Fact]
    public async Task ABrushEntitysVisibleSidesShrinkByHalfAnInch()
    {
        // VPHYSICS_SHRINK, ivp.cpp:37 and :529. Two calls per brush: the
        // unshrunk test hull, then the shrunk one.
        CollisionFixture f = new();
        f.Box(1, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.All(c.Session.PlaneCalls[^1], p => Assert.Equal(31.5f, p.Dist));
        Assert.All(c.Session.PlaneCalls[^2], p => Assert.Equal(32f, p.Dist));
    }

    [Fact]
    public async Task AnInvisibleSideIsNotShrunk()
    {
        // ivp.cpp:505-513.
        CollisionFixture f = new() { SideVisible = [[], [true, false, true, true, true, true]] };
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Box(1, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.Equal([31.5f, 32f, 31.5f, 31.5f, 31.5f, 31.5f], c.Session.PlaneCalls[^1].Select(p => p.Dist));
    }

    [Fact]
    public async Task AnAxisThinnerThanThreeShrinksIsNotShrunk()
    {
        // ivp.cpp:515-527 with shrinkMinimum = m_shrink * 3 = 1.5 (:547).
        CollisionFixture f = new();
        f.Box(1, new Vec3(-32, -32, 0), new Vec3(32, 32, 1), CollisionContents.Solid, f.TexInfoFor("metal"));

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.Equal([31.5f, 31.5f, 31.5f, 31.5f, 1f, 0f], c.Session.PlaneCalls[^1].Select(p => p.Dist));
    }

    [Fact]
    public async Task ABrushEntityIsASolidBlockWithMassMaterialAndVolume()
    {
        // CPhysCollisionEntrySolid::WriteToTextBuffer, ivp.cpp:248.
        CollisionFixture f = new();
        int wood = f.TexInfoFor("wood");
        f.Box(1, Lo, Hi, CollisionContents.Solid, wood);
        f.Face(1, wood, 4096f);

        (PhysCollisionResult r, _) = await EmitAsync(f, models: 2);

        // 63^3 shrunk volume x 700 x 0.0254^3.
        float volume = 63f * 63f * 63f;
        float mass = volume * 700f * PhysCollisionEmitter.CubicMetersPerCubicInch;
        string F6(float v) => ((double)v).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            "solid {\n\"index\" \"0\"\n\"mass\" \"" + F6(mass) + "\"\n\"surfaceprop\" \"wood\"\n\"volume\" \"" + F6(volume) + "\"\n}\n",
            r.Models.Single(m => m.ModelIndex == 1).KeyText);
    }

    [Fact]
    public void TheMaterialCoveringTheMostAreaWins()
    {
        // ivp.cpp:1423-1437.
        CollisionFixture f = new();
        (string material, _) = PhysCollisionEmitter.MassAndMaterial(
            [f.Props.GetSurfaceIndex("wood"), f.Props.GetSurfaceIndex("metal"), f.Props.GetSurfaceIndex("wood")],
            [10f, 15f, 10f], null, 1000f, f.Props, ComplianceOptions.Correct);

        Assert.Equal("wood", material);
    }

    [Fact]
    public void StockShellMassCountsTheSentinelArea()
    {
        // ShellMassSentinelArea, ivp.cpp:1436: totalArea includes proplist[0]'s implicit 1.
        CollisionFixture f = new();
        (_, float mass) = PhysCollisionEmitter.MassAndMaterial(
            [f.Props.GetSurfaceIndex("shell")], [100f], null, 1e9f, f.Props, ComplianceOptions.Stock);

        Assert.Equal(101f * 0.5f * 1000f * PhysCollisionEmitter.CubicMetersPerCubicInch, mass);
    }

    [Fact]
    public void CorrectShellMassWeighsOnlyTheFaces()
    {
        CollisionFixture f = new();
        (_, float mass) = PhysCollisionEmitter.MassAndMaterial(
            [f.Props.GetSurfaceIndex("shell")], [100f], null, 1e9f, f.Props, ComplianceOptions.Correct);

        Assert.Equal(100f * 0.5f * 1000f * PhysCollisionEmitter.CubicMetersPerCubicInch, mass);
    }

    [Fact]
    public void StockFacelessShellWeighsThreeSquareInches()
    {
        // The sentinel's 1 plus the faceless entry's 2 (ivp.cpp:1384-1396).
        CollisionFixture f = new();
        (_, float mass) = PhysCollisionEmitter.MassAndMaterial(
            [], [], f.Props.GetSurfaceIndex("shell"), 1000f, f.Props, ComplianceOptions.Stock);

        Assert.Equal(3f * 0.5f * 1000f * PhysCollisionEmitter.CubicMetersPerCubicInch, mass);
    }

    [Fact]
    public void CorrectFacelessShellWeighsItsVolume()
    {
        CollisionFixture f = new();
        (_, float mass) = PhysCollisionEmitter.MassAndMaterial(
            [], [], f.Props.GetSurfaceIndex("shell"), 1000f, f.Props, ComplianceOptions.Correct);

        Assert.Equal(1000f * 1000f * PhysCollisionEmitter.CubicMetersPerCubicInch, mass);
    }

    [Fact]
    public void MassIsClampedToFiftyTonnes()
    {
        // VPHYSICS_MAX_MASS, ivp.cpp:1467.
        CollisionFixture f = new();
        (_, float mass) = PhysCollisionEmitter.MassAndMaterial(
            [f.Props.GetSurfaceIndex("metal")], [1f], null, 1e12f, f.Props, ComplianceOptions.Correct);

        Assert.Equal(5e4f, mass);
    }

    [Fact]
    public void AFacelessModelUsesItsFirstSidesPropertyWithAreaTwo()
    {
        // ivp.cpp:1389-1397: area 2 beats the implicit entry's 1.
        CollisionFixture f = new();
        (string material, _) = PhysCollisionEmitter.MassAndMaterial([], [], f.Props.GetSurfaceIndex("metal"), 1f, f.Props, ComplianceOptions.Correct);

        Assert.Equal("metal", material);
    }

    [Fact]
    public void ANegativePropertyFallsBackToIndexZero()
    {
        // ivp.cpp:1446: "use default if this material has no prop".
        CollisionFixture f = new();
        (string material, _) = PhysCollisionEmitter.MassAndMaterial([-1], [50f], null, 1f, f.Props, ComplianceOptions.Correct);

        Assert.Equal("default", material);
    }

    [Fact]
    public async Task AOneConvexBrushEntityBuildsNoOuterHull()
    {
        // ivp.cpp:1353: buildOuterConvexHull = count > 1.
        CollisionFixture f = new();
        f.Box(1, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.False(c.Session.ParamsCalls[0].BuildOuterConvexHull);
        Assert.True(c.Session.ParamsCalls[0].BuildDragAxisAreas);
    }

    [Fact]
    public async Task ATwoConvexBrushEntityBuildsAnOuterHull()
    {
        CollisionFixture f = new();
        int t = f.TexInfoFor("metal");
        f.Box(1, Lo, Hi, CollisionContents.Solid, t);
        f.Box(1, Lo + new Vec3(100, 0, 0), Hi + new Vec3(100, 0, 0), CollisionContents.Solid, t);

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.True(c.Session.ParamsCalls[0].BuildOuterConvexHull);
    }

    [Theory]
    [InlineData(10f, 1f)]      // 10 x 10 x 1% = 1: at the floor
    [InlineData(1000f, 1024f)] // 10,000: clamped to 1024
    [InlineData(200f, 400f)]   // 40,000 x 1%
    public void DragAreaEpsilonIsOnePercentOfTheSmallestFaceClamped(float side, float expected)
    {
        Assert.Equal(expected, PhysCollisionEmitter.DragAreaEpsilon(Vec3.Zero, new Vec3(side, side, side)));
    }

    [Fact]
    public async Task AModelWithNoSolidBrushesHasNoRecord()
    {
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Box(1, Lo, Hi, 0, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f, models: 2);

        Assert.Equal([0], r.Models.Select(m => m.ModelIndex));
    }

    [Fact]
    public async Task EveryLeafLosesTestFogVolumeAndItsWaterData()
    {
        // ClearLeafWaterData, ivp.cpp:1475.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.All(r.LeafWaterDataIds, id => Assert.Equal(-1, id));
        Assert.Equal(0x20, r.LeafContents[1]);
    }

    [Fact]
    public async Task AWaterVolumeIsAFluidBlockAfterTheSolids()
    {
        // CPhysCollisionEntryFluid::WriteToTextBuffer, ivp.cpp:359-371.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Water, f.TexInfoFor("metal"));
        f.Water.Add(new WaterModel(0, CollisionContents.Water, true, new Vec3(0, 0, 1), 32f, 0, [0]));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.StartsWith(
            "fluid {\n\"index\" \"0\"\n\"surfaceprop\" \"water\"\n\"damping\" \"0.010000\"\n\"contents\" \"32\"\n"
            + "\"surfaceplane\" \"0.000000 0.000000 1.000000 32.000000 \"\n"
            + "\"currentvelocity\" \"0.000000 0.000000 0.000000 \"\n}\n",
            Key(r),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaterVolumesLeavesGetItsFogVolume()
    {
        // ivp.cpp:1187.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Water, f.TexInfoFor("metal"));
        f.Water.Add(new WaterModel(0, CollisionContents.Water, true, new Vec3(0, 0, 1), 32f, 5, [0]));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Equal(5, r.LeafWaterDataIds[0]);
    }

    [Fact]
    public async Task AWaterVolumeWithoutASurfaceTakesItsTopFromTheCollide()
    {
        // ivp.cpp:1223-1228: CollideGetExtent along +Z.
        CollisionFixture f = new();
        f.Box(0, Lo, new Vec3(32, 32, 20), CollisionContents.Water, f.TexInfoFor("metal"));
        f.Water.Add(new WaterModel(0, CollisionContents.Water, false, new Vec3(1, 0, 0), 999f, 0, [0]));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Contains("\"surfaceplane\" \"0.000000 0.000000 1.000000 20.000000 \"", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheWorldsWaterVolumesAreEmitted()
    {
        // ConvertWaterModelToPhysCollide is called for model 0 only (ivp.cpp:1335).
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Water.Add(new WaterModel(1, CollisionContents.Water, true, new Vec3(0, 0, 1), 32f, 0, [0]));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.DoesNotContain("fluid", Key(r), StringComparison.Ordinal);
    }

    private static async Task<(string Key, FakeCollisionCooker Cooker)> WaterAsync(ComplianceOptions compliance, float brushTop, int surfaceTexInfo)
    {
        CollisionFixture f = new();
        int metal = f.TexInfoFor("metal");
        f.Box(0, Lo, new Vec3(32, 32, brushTop), CollisionContents.Water, metal);
        f.Water.Add(new WaterModel(0, CollisionContents.Water, true, new Vec3(0, 0, 1), 20f, 0, [0], surfaceTexInfo < 0 ? -1 : metal));
        FakeCollisionCooker cooker = new();
        PhysCollisionResult r = await PhysCollisionEmitter.EmitAsync(f.Build() with { Compliance = compliance }, cooker);
        return (r.Models[0].KeyText, cooker);
    }

    [Fact]
    public async Task StockFluidsAreAlwaysWater()
    {
        // FluidSurfacePropIgnored, ivp.cpp:1211: the override reads a -1.
        (string key, _) = await WaterAsync(ComplianceOptions.Stock, 20f, surfaceTexInfo: 0);

        Assert.Contains("\"surfaceprop\" \"water\"", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectFluidsTakeTheSurfaceMaterialsProperty()
    {
        (string key, _) = await WaterAsync(ComplianceOptions.Correct, 20f, surfaceTexInfo: 0);

        Assert.Contains("\"surfaceprop\" \"metal\"", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectFluidsWithoutASurfaceMaterialAreWater()
    {
        (string key, _) = await WaterAsync(ComplianceOptions.Correct, 20f, surfaceTexInfo: -1);

        Assert.Contains("\"surfaceprop\" \"water\"", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockAddsAWaterBrushCrossingTheSurfaceWhole()
    {
        // WaterBrushNotClippedAtSurface, ivp.cpp:1196: the brush reaches z=32, the surface is z=20.
        (_, FakeCollisionCooker c) = await WaterAsync(ComplianceOptions.Stock, 32f, -1);

        Assert.Equal(6, c.Session.PlaneCalls[^1].Length);
    }

    [Fact]
    public async Task CorrectCutsAWaterBrushCrossingTheSurface()
    {
        (_, FakeCollisionCooker c) = await WaterAsync(ComplianceOptions.Correct, 32f, -1);

        Assert.Equal(new CollisionPlane(new Vec3(0, 0, 1), 20f), c.Session.PlaneCalls[^1][^1]);
        Assert.Equal(7, c.Session.PlaneCalls[^1].Length);
    }

    [Fact]
    public async Task CorrectLeavesAWaterBrushBelowTheSurfaceAlone()
    {
        // Only brushes that reach above the surface are cut: a brush whose top IS
        // the surface keeps its six planes and its stock bytes.
        (_, FakeCollisionCooker c) = await WaterAsync(ComplianceOptions.Correct, 20f, -1);

        Assert.Equal(6, c.Session.PlaneCalls[^1].Length);
    }

    [Fact]
    public async Task EveryCollideIsDestroyed()
    {
        CollisionFixture f = new();
        int t = f.TexInfoFor("metal");
        f.Box(0, Lo, Hi, CollisionContents.Solid, t);
        f.Box(1, Lo, Hi, CollisionContents.Solid, t);

        (_, FakeCollisionCooker c) = await EmitAsync(f, models: 2);

        Assert.Equal(0, c.Session.LiveCollides);
    }

    [Fact]
    public async Task ThePhysCollideLumpReadsBackAsItsRecords()
    {
        CollisionFixture f = new();
        int t = f.TexInfoFor("metal");
        f.Box(0, Lo, Hi, CollisionContents.Solid, t);
        f.Box(1, Lo, Hi, CollisionContents.Solid, t);

        (PhysCollisionResult r, _) = await EmitAsync(f, models: 2);
        IReadOnlyList<PhysCollideModel> back = PhysCollideLump.Read(r.PhysCollide);

        Assert.Equal(r.Models.Select(m => m.KeyText), back.Select(m => m.KeyText));
        Assert.Equal(r.Models.SelectMany(m => m.Solids), back.SelectMany(m => m.Solids));
    }

    [Fact]
    public async Task APreCancelledTokenCooksNothing()
    {
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        FakeCollisionCooker cooker = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PhysCollisionEmitter.EmitAsync(f.Build(), cooker, null, cts.Token));
        Assert.Empty(cooker.Session.PlaneCalls);
    }

    [Fact]
    public async Task ADisplacementIsAVirtualMeshInPhysDisp()
    {
        // Disp_BuildVirtualMesh, disp_ivp.cpp:274-336.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Displacements.Add(new CollisionDisplacement(CollisionFixture.Displacement(), CollisionContents.Solid, 0, -1));

        (PhysCollisionResult r, FakeCollisionCooker c) = await EmitAsync(f);

        Assert.Single(c.Session.VirtualMeshes);
        Assert.Single(PhysDispLump.ReadSizes(r.PhysDisp));
        Assert.True(PhysDispLump.ReadSizes(r.PhysDisp)[0] > 0);
    }

    [Fact]
    public async Task ANonSolidDisplacementHasNoMesh()
    {
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Displacements.Add(new CollisionDisplacement(CollisionFixture.Displacement(), CollisionContents.Water, 0, -1));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Equal([-1], PhysDispLump.ReadSizes(r.PhysDisp));
    }

    [Fact]
    public async Task APowerFourDisplacementForcesThePolysoupRoad()
    {
        // ivp.cpp:1320-1324.
        CollisionFixture f = new();
        f.Box(0, Lo, Hi, CollisionContents.Solid, f.TexInfoFor("metal"));
        f.Displacements.Add(new CollisionDisplacement(CollisionFixture.Displacement(power: 4), CollisionContents.Solid, 0, -1));

        (PhysCollisionResult r, _) = await EmitAsync(f);

        Assert.Null(r.PhysDisp);
        Assert.DoesNotContain("virtualterrain", Key(r), StringComparison.Ordinal);
        Assert.Contains("staticsolid {\n\"index\" \"1\"\n}\n", Key(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APolysoupTriangleUsesSurfaceProp2PastTheAlphaDelta()
    {
        // disp_ivp.cpp:170-179: alpha sum > 382.5 switches to $surfaceprop2.
        CollisionFixture f = new() { NoVirtualMesh = true };
        int metal = f.TexInfoFor("metal");
        f.Box(0, Lo, Hi, CollisionContents.Solid, metal);
        f.Displacements.Add(new CollisionDisplacement(
            CollisionFixture.Displacement(alpha: 200f), CollisionContents.Solid, metal, f.Props.GetSurfaceIndex("wood")));

        (PhysCollisionResult r, _) = await EmitAsync(f);
        string soup = Encoding.ASCII.GetString(r.Models[0].Solids[1]);

        // metal is material 1 (the box), wood becomes 2: every triangle's alpha sum is 600.
        Assert.StartsWith("soup 2 2 2", soup, StringComparison.Ordinal);
        Assert.DoesNotContain(" 1", soup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVirtualMeshServesTheIndicesReversed()
    {
        // CDispMeshEvent's constructor swaps pIndices end for end, disp_ivp.cpp:223-226.
        var core = CollisionFixture.Displacement();
        List<ushort> forward = SourceSharp.MapTools.Disp.DispTesselator.Tesselate(core);

        VirtualMeshSource mesh = PhysCollisionEmitter.DispMeshEvent(core, 0);

        Assert.Equal(Enumerable.Reverse(forward), mesh.Indices);
    }

    [Fact]
    public void TheVirtualMeshServesVerticesUpToTheHighestIndex()
    {
        var core = CollisionFixture.Displacement();
        VirtualMeshSource mesh = PhysCollisionEmitter.DispMeshEvent(core, 0);

        Assert.Equal(mesh.Indices.Max() + 1, mesh.Vertices.Length);
        Assert.Equal(core.Vert(3), mesh.Vertices[3]);
    }

    [Fact]
    public void ADegenerateDisplacementTriangleStopsTheCompile()
    {
        // disp_ivp.cpp:296-307: stock Error()s.
        var core = CollisionFixture.Displacement();
        for (int i = 0; i < core.Size; i++)
        {
            core.SetVert(i, Vec3.Zero);
        }

        Assert.Throws<MapCompileException>(() => PhysCollisionEmitter.DispMeshEvent(core, 0));
    }

    [Fact]
    public void DisplacementsInOneGridCellHashTogether()
    {
        // Disp_GridIndex, disp_ivp.cpp:50-69.
        var a = CollisionFixture.Displacement();
        var b = CollisionFixture.Displacement();

        Assert.Equal(PhysCollisionEmitter.DispGridIndex(a), PhysCollisionEmitter.DispGridIndex(b));
        Assert.Equal((16416 / 4096) | ((16416 / 4096) << 8) | ((16385 / 8192) << 16), PhysCollisionEmitter.DispGridIndex(a));
    }

    [Fact]
    public void TriangleNormalIsTheSecondEdgeCrossTheFirst()
    {
        // ivp.cpp:1237-1244: CrossProduct( e1, e0 ).
        Vec3 n = PhysCollisionEmitter.TriangleNormal(Vec3.Zero, new Vec3(0, 1, 0), new Vec3(1, 0, 0));

        Assert.Equal(new Vec3(0, 0, 1), n);
    }
}
