//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.Tests.MapTools.Bsp.Collision;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// The collision stage inside the vbsp driver: <c>EmitPhysCollision</c> at,
/// fed by the write stage's own lumps, water
/// volumes and side visibility, and writing LUMP_PHYSCOLLIDE / LUMP_PHYSDISP.
/// </summary>
/// <remarks>
/// The cooker here is <see cref="FakeCollisionCooker"/> (axial boxes only):
/// these facts are about what the driver hands the emitter and what it does
/// with the answer. The real library is gated by the native tier
/// (<see cref="VbspPhysStockGateTests"/>).
/// </remarks>
public sealed class VbspCollisionWiringTests
{
    private const string Metal = "unit/metal";

    // A sealed room of axial slabs, optionally with a pool and a func_brush.
    private static VmfDocument Room(bool water = false, bool brushEntity = false, string floor = UnitMap.Plain)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        void Slab(string material, (float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(material, mins, maxs, id++));

        Slab(floor, (-16, -16, -16), (272, 272, 0));
        Slab(UnitMap.Plain, (-16, -16, 256), (272, 272, 272));
        Slab(UnitMap.Plain, (-16, -16, 0), (0, 272, 256));
        Slab(UnitMap.Plain, (256, -16, 0), (272, 272, 256));
        Slab(UnitMap.Plain, (0, -16, 0), (256, 0, 256));
        Slab(UnitMap.Plain, (0, 256, 0), (256, 272, 256));

        if (water)
        {
            Slab(UnitMap.Water, (0, 0, 0), (256, 256, 64));
        }

        document.Chunks.Add(world);
        Entity(document, "info_player_start", "128 128 128");

        if (brushEntity)
        {
            VmfChunk e = Entity(document, "func_brush", null);
            e.Children.Add(UnitMap.Box(UnitMap.Plain, (96, 96, 96), (160, 160, 160), id++));
        }

        return document;
    }

    private static VmfChunk Entity(VmfDocument document, string className, string? origin)
    {
        VmfChunk e = new(MapFileLoader.EntityChunk);
        e.AddKey("id", (document.Chunks.Count + 100).ToString(CultureInfo.InvariantCulture));
        e.AddKey("classname", className);
        if (origin is not null)
        {
            e.AddKey("origin", origin);
        }

        document.Chunks.Add(e);
        return e;
    }

    private static async Task<(VbspResult Result, VbspContext Context)> CompileAsync(
        VmfDocument document, ICollisionCooker? cooker, ComplianceOptions? compliance = null, VbspContext? context = null)
    {
        context ??= await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance ?? ComplianceOptions.Correct });
        context.CollisionCooker = cooker;
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        return (await Vbsp.CompileAsync(map, context), context);
    }

    // A context whose content has a surface-property manifest and a material
    // naming one of its surfaces.
    private static async Task<VbspContext> ContextWithSurfacePropsAsync(VbspOptions? options = null)
    {
        InMemoryFileSystem files = new();
        files.AddText(
            "materials/concrete/concretefloor001a.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$surfaceprop\" \"concrete\"\n}\n");
        files.AddText(
            "materials/nature/blendgrassgravel001a.vmt",
            "\"WorldVertexTransition\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$surfaceprop\" \"gravel\"\n\t\"$surfaceprop2\" \"grass\"\n}\n");
        files.AddText("materials/unit/plain.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{Metal}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$surfaceprop\" \"metal\"\n}\n");
        files.AddText(
            "scripts/surfaceproperties_manifest.txt",
            "\"surfaces_manifest\"\n{\n\t\"file\" \"scripts/surfaceproperties.txt\"\n}\n");
        files.AddText(
            "scripts/surfaceproperties.txt",
            "\"default\"\n{\n\t\"density\" \"2000\"\n}\n\"concrete\"\n{\n\t\"density\" \"2400\"\n}\n\"metal\"\n{\n\t\"density\" \"2700\"\n}\n"
            + "\"gravel\"\n{\n\t\"density\" \"1600\"\n}\n\"grass\"\n{\n\t\"density\" \"1500\"\n}\n");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new VbspContext(options ?? VbspOptions.Default, new ContentFileSystem([mount])) { MapBase = "unit" };
    }

    [Fact]
    public async Task WithoutACookerTheMapHasNoPhysCollideLump()
    {
        // physcollision == NULL: "Can't build collision data!".
        (VbspResult result, _) = await CompileAsync(Room(), cooker: null);

        Assert.Equal(0, result.Bsp![BspLump.PhysCollide].Data.Length);
    }

    [Fact]
    public async Task WithACookerTheWorldModelHasACollisionRecord()
    {
        (VbspResult result, _) = await CompileAsync(Room(), new FakeCollisionCooker());

        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(result.Bsp![BspLump.PhysCollide].Data.Span);
        Assert.Equal(0, models[0].ModelIndex);
    }

    [Fact]
    public async Task EachWorldBrushIsAConvexOfTheWorldSolid()
    {
        // The six slabs: one convex per brush, cooked from the LUMP_BRUSHES
        // the write stage emitted (the reference implementation's BuildWorldPhysModel).
        FakeCollisionCooker cooker = new();
        await CompileAsync(Room(), cooker);

        Assert.Equal(6, cooker.Session.PlaneCalls.Count);
    }

    [Fact]
    public async Task ABrushEntityGetsItsOwnCollisionRecord()
    {
        (VbspResult result, _) = await CompileAsync(Room(brushEntity: true), new FakeCollisionCooker());

        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(result.Bsp![BspLump.PhysCollide].Data.Span);
        Assert.Equal([0, 1], models.Select(m => m.ModelIndex));
    }

    [Fact]
    public async Task ThePoolReachesTheEmitterAsAFluid()
    {
        // EmitWaterVolumesForBSP recorded the volume while
        // the tree existed; ConvertWaterModelToPhysCollide turns it into a
        // "fluid" section of the world's keydata.
        (VbspResult result, _) = await CompileAsync(Room(water: true), new FakeCollisionCooker());

        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(result.Bsp![BspLump.PhysCollide].Data.Span);
        Assert.Contains("fluid {", models[0].KeyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCookersLeafWaterIdsAreTheOnesTheDriverAssigned()
    {
        // The driver assigns the ids whether or not a cooker runs; the
        // emitter's write-back must agree leaf for leaf.
        (VbspResult bare, _) = await CompileAsync(Room(water: true), cooker: null);
        (VbspResult cooked, _) = await CompileAsync(Room(water: true), new FakeCollisionCooker());

        Assert.Equal(bare.Bsp![BspLump.Leafs].Data.ToArray(), cooked.Bsp![BspLump.Leafs].Data.ToArray());
    }

    [Fact]
    public async Task ThePoolsLeavesCarryItsFogVolume()
    {
        (VbspResult result, _) = await CompileAsync(Room(water: true), new FakeCollisionCooker());

        Assert.Contains(BspStructView.As<DLeaf>(result.Bsp![BspLump.Leafs]).ToArray(), l => l.LeafWaterDataId == 0);
    }

    [Fact]
    public async Task UnderCorrectTheWaterSurfaceFaceGetsItsFogVolume()
    {
        // WriteFogVolumeIDs loops to firstface + numfaces, and
        // numfaces is still 0 when it runs: StockQuirk.FogVolumeLoopOverNoFaces.
        // Correct runs the loop over the model's real faces.
        (VbspResult result, _) = await CompileAsync(Room(water: true), new FakeCollisionCooker());

        Assert.Contains(BspStructView.As<DFace>(result.Bsp![BspLump.Faces]).ToArray(), f => f.SurfaceFogVolumeId == 0);
    }

    [Fact]
    public async Task UnderStockNoFaceGetsAFogVolume()
    {
        (VbspResult result, _) = await CompileAsync(Room(water: true), new FakeCollisionCooker(), ComplianceOptions.Stock);

        Assert.DoesNotContain(BspStructView.As<DFace>(result.Bsp![BspLump.Faces]).ToArray(), f => f.SurfaceFogVolumeId >= 0);
    }

    [Fact]
    public async Task AMaterialsSurfacePropResolvesAgainstTheLoadedTable()
    {
        // GetSurfaceProperties against the table
        // LoadSurfaceProperties read before the map.
        VbspContext context = await ContextWithSurfacePropsAsync();
        await CompileAsync(Room(floor: Metal), cooker: null, context: context);

        int texData = Enumerable.Range(0, context.TexDatas.Count).Single(i => context.TexDatas.NameOf(i) == Metal);
        Assert.Equal(context.TexDatas.PropertyTable!.GetSurfaceIndex("metal"), context.TexDatas.SurfaceProperties[texData]);
    }

    [Fact]
    public async Task AMaterialWithoutASurfacePropResolvesToMinusOne()
    {
        VbspContext context = await ContextWithSurfacePropsAsync();
        await CompileAsync(Room(floor: Metal), cooker: null, context: context);

        int texData = Enumerable.Range(0, context.TexDatas.Count).Single(i => context.TexDatas.NameOf(i) == UnitMap.Plain);
        Assert.Equal(-1, context.TexDatas.SurfaceProperties[texData]);
    }

    [Fact]
    public async Task TheSurfacePropReachesTheWorldsMaterialTable()
    {
        // s_WorldPropList: the world's per-triangle materials by name
        //, which only a resolved $surfaceprop can put there.
        VbspContext context = await ContextWithSurfacePropsAsync();
        (VbspResult result, _) = await CompileAsync(Room(floor: Metal), new FakeCollisionCooker(), context: context);

        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(result.Bsp![BspLump.PhysCollide].Data.Span);
        Assert.Contains("\"metal\"", models[0].KeyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrushEntitysHiddenSideIsNotShrunk()
    {
        // "don't shrink brush sides with no visible components":
        // two boxes of one func_brush meet at x = 128, so each one's side
        // there is not visible after MarkVisibleSides and keeps its distance.
        VmfDocument document = Room();
        VmfChunk e = Entity(document, "func_brush", null);
        e.Children.Add(UnitMap.Box(UnitMap.Plain, (96, 96, 96), (128, 160, 160), 900));
        e.Children.Add(UnitMap.Box(UnitMap.Plain, (128, 96, 96), (160, 160, 160), 901));
        FakeCollisionCooker cooker = new();
        await CompileAsync(document, cooker);

        CollisionPlane[] left = cooker.Session.PlaneCalls.Single(p => p.Any(q => q.Normal.X == -1 && q.Dist == -96f + PhysCollisionEmitter.VPhysicsShrink * -1f));
        Assert.Contains(left, q => q.Normal.X == 1 && q.Dist == 128f);
    }

    [Fact]
    public async Task ABrushEntitysVisibleSideIsShrunk()
    {
        VmfDocument document = Room();
        VmfChunk e = Entity(document, "func_brush", null);
        e.Children.Add(UnitMap.Box(UnitMap.Plain, (96, 96, 96), (128, 160, 160), 900));
        e.Children.Add(UnitMap.Box(UnitMap.Plain, (128, 96, 96), (160, 160, 160), 901));
        FakeCollisionCooker cooker = new();
        await CompileAsync(document, cooker);

        // The last cook of the left box is the shrunk one (the first is the
        // unshrunk test collide).
        CollisionPlane[] left = cooker.Session.PlaneCalls.Last(p => p.Any(q => q.Normal.X == 1 && q.Dist == 128f));
        Assert.Contains(left, q => q.Normal.Z == 1 && q.Dist == 160f - PhysCollisionEmitter.VPhysicsShrink);
    }

    private static async Task<VbspResult> BlendAsync(bool noVirtualMesh, bool supportsVirtualMesh = true)
    {
        VbspContext context = await ContextWithSurfacePropsAsync(VbspOptions.Default with { NoVirtualMesh = noVirtualMesh });
        string vmf = SourceSharp.Tests.MapTools.Disp.DispCatalogueMaps.All.Single(e => e.Name == "p3f_p2_blend").Vmf;
        (VbspResult result, _) = await CompileAsync(await VmfDocument.ParseAsync(vmf), new FakeCollisionCooker(supportsVirtualMesh), context: context);
        return result;
    }

    [Fact]
    public async Task ABlendedDisplacementsSecondSurfacePropReachesTheWorld()
    {
        //: on the polysoup road a triangle whose alpha
        // sum passes 382.5 takes the material's $surfaceprop2
        //(GetSurfaceProperties2).
        VbspResult result = await BlendAsync(noVirtualMesh: true);

        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(result.Bsp![BspLump.PhysCollide].Data.Span);
        Assert.Contains("\"grass\"", models[0].KeyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnThePolysoupRoadThereIsNoPhysDispLump()
    {
        // g_pPhysDisp stays NULL.
        VbspResult result = await BlendAsync(noVirtualMesh: true);

        Assert.Equal(0, result.Bsp![BspLump.PhysDisp].Data.Length);
    }

    [Fact]
    public async Task OnTheVirtualMeshRoadEachDisplacementHasAPhysDispEntry()
    {
        VbspResult result = await BlendAsync(noVirtualMesh: false);

        Assert.Single(PhysDispLump.ReadSizes(result.Bsp![BspLump.PhysDisp].Data.Span));
    }
}
