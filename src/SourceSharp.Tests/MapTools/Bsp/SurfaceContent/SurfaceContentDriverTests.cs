//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapGen;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Validation;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// <see cref="SurfaceContentVbsp"/> on in-memory content: the 3g extension in
/// the real driver, with no stock reference needed.
/// </summary>
public class SurfaceContentDriverTests
{
    private const string Decal = "unit/decal";

    [Fact]
    public async Task EveryOverlayPointsAtItsOwnMaterialAfterTexInfoCompaction()
    {
        // The overlays' texinfos are made at OverlayFaces, before
        // CompactTexinfos renumbers the table.
        BspData bsp = await CompileAsync(OverlayRoom());

        DOverlay[] overlays = BspStructView.As<DOverlay>(bsp[BspLump.Overlays]).ToArray();
        Assert.Equal(2, overlays.Length);
        Assert.All(overlays, o => Assert.Equal(Decal, MaterialOf(bsp, o.TexInfo)));
    }

    [Fact]
    public async Task EveryOverlayListsTheFacesOfItsSides()
    {
        BspData bsp = await CompileAsync(OverlayRoom());

        DOverlay[] overlays = BspStructView.As<DOverlay>(bsp[BspLump.Overlays]).ToArray();
        Assert.All(overlays, o => Assert.True(o.GetFaceCount() > 0));
        Assert.Equal(overlays.Length, BspStructView.As<DOverlayFade>(bsp[BspLump.OverlayFades]).Length);
    }

    [Fact]
    public async Task TheGameLumpsCarryTheirStockVersions()
    {
        BspData bsp = await CompileAsync(OverlayRoom());

        Assert.Equal(10, bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.StaticProps)).Version);
        Assert.Equal(4, bsp.GameLumps.Single(g => g.Id == GameLumpId.MakeId(GameLumpId.DetailProps)).Version);
    }

    [Fact]
    public async Task ASpecularRoomPaksTheCubemapPatchItsSidesNowUse()
    {
        // Cubemap_AttachDefaultCubemapToSpecularSides:
        // every specular side is patched to the nearest env_cubemap, and the
        // patch is in the pak.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        BspData bsp = await CompileAsync(vmf);

        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data);
        List<string> patches = [.. pak.Entries.Select(e => e.Name).Where(n => n.StartsWith("materials/maps/unit/", StringComparison.Ordinal) && n.EndsWith(".vmt", StringComparison.Ordinal))];
        Assert.NotEmpty(patches);

        TexInfo[] texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        Assert.Contains(Enumerable.Range(0, texInfos.Length), t => $"materials/{MaterialOf(bsp, t)}.vmt" == patches[0]);
    }

    [Fact]
    public async Task TheFileValidates()
    {
        BspData bsp = await CompileAsync(OverlayRoom());

        ValidationReport report = await BspValidator.CheckAsync(bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
    }

    [Fact]
    public async Task OnlyEntsKeepsThePakAndRewritesTheStaticProps()
    {
        // -onlyents: LoadBSPFile + WriteBSPFile keep the pak.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        BspData full = await CompileAsync(vmf);
        int before = (await ZipArchiveReader.ParseAsync(full[BspLump.PakFile].Data)).Entries.Count;

        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, extra: AddDecal);
        VbspContext context = new(loaded.Context.Options with { OnlyEnts = true }, loaded.Context.Content) { MapBase = "unit" };
        MapFile map = await new MapFileReader(context, loaded.Files).LoadAsync(SourceSharp.MapTools.Io.VPath.Create("maps/unit.vmf"));
        BspData updated = await SurfaceContentVbsp.UpdateAsync(full, map, context);

        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(updated[BspLump.PakFile].Data);
        Assert.Equal(before + 1, pak.Entries.Count); // + stale.txt
        Assert.Contains(updated.GameLumps, g => g.Id == GameLumpId.MakeId(GameLumpId.StaticProps) && g.Version == 10);
    }

    [Fact]
    public async Task ThePublicDriverWritesTheSurfaceContentLumps()
    {
        // p3g integration item 1: Vbsp.CompileAsync itself attaches the 3g
        // stage, so ssmap vbsp writes a real pak, not an empty zip.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, ComplianceOptions.Correct, AddDecal);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(result.Bsp![BspLump.PakFile].Data);
        Assert.Contains(pak.Entries, e => e.Name.StartsWith("materials/maps/unit/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePublicDriverWritesTheCubemapLump()
    {
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, ComplianceOptions.Correct, AddDecal);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.Equal(1, BspStructView.As<DCubemapSample>(result.Bsp![BspLump.Cubemaps]).Length);
    }

    [Fact]
    public async Task ThePublicDriverWritesTheOverlays()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(OverlayRoom(), ComplianceOptions.Correct, AddDecal);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.Equal(2, BspStructView.As<DOverlay>(result.Bsp![BspLump.Overlays]).Length);
    }

    [Fact]
    public async Task TheLoaderKeepsTheWaterOverlaysSoNoDocumentIsNeeded()
    {
        // p3g integration item 3: the overlaytransition chunks stay on the
        // map, in file order.
        VmfMap vmf = WaterOverlayRoom();
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, ComplianceOptions.Correct, AddDecal);

        Assert.Single(loaded.Map.WaterOverlayData);
    }

    [Fact]
    public async Task AWaterOverlayCompilesWithoutTheDocument()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(WaterOverlayRoom(), ComplianceOptions.Correct, AddDecal);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.Equal(1, BspStructView.As<DWaterOverlay>(result.Bsp![BspLump.WaterOverlays]).Length);
    }

    [Fact]
    public async Task ACubemapPatchedWatersBottomFaceUsesThePatchedBottomMaterial()
    {
        //: AssignBottomWaterMaterialToFace reads
        // $bottommaterial from the CUBEMAP-PATCHED water, after the fixup; the
        // patch names a patched bottom that exists only
        // in the pak. p3g integration item 2: the driver read the facts before
        // the fixup and from disk, found none for the patched name, and dropped
        // the bottom face and its texinfo.
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(PoolRoom(cubemap: true), ComplianceOptions.Correct, AddNoDraw);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.Contains("maps/unit/unit/beneath_0_0_128", FaceMaterials(result.Bsp!));
    }

    [Fact]
    public async Task APatchedWatersBottomDoesNotWarnThatItHasNoBottomMaterial()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(PoolRoom(cubemap: true), ComplianceOptions.Correct, AddNoDraw);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("$bottommaterial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACubemapPatchedWatersDepthMaterialIsDescribedByTheOriginal()
    {
        // FindAliasedTexData describes the per-depth water
        // texdata with FindOriginalMaterial of the water's own texdata, which
        // follows the patch chain back to the VMT on disk. The water here is
        // cubemap-patched, so its texdata names a patch that exists only in
        // the pak: read by that name, the depth texdata was "not found" and
        // its texinfo pointed at texdata -1 (sdk_ctf_2fort, twice).
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(PoolRoom(cubemap: true), ComplianceOptions.Correct, AddNoDraw);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("_depth_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnpatchedWatersBottomFaceUsesItsOwnBottomMaterial()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(PoolRoom(cubemap: false), ComplianceOptions.Correct, AddNoDraw);

        VbspResult result = await Vbsp.CompileAsync(loaded.Map, loaded.Context);

        Assert.Contains(SurfaceUnit.Beneath, FaceMaterials(result.Bsp!));
    }

    private const string NoDraw = "tools/toolsnodraw";

    private static void AddNoDraw(SourceSharp.MapTools.Io.InMemoryFileSystem files) =>
        files.AddText($"materials/{NoDraw}.vmt", "\"LightmappedGeneric\"\n{\n\t\"%compilenodraw\" \"1\"\n}\n");

    // A sealed room with a pool in its lower half: a water top over nodraw.
    private static VmfMap PoolRoom(bool cubemap)
    {
        VmfMap map = SurfaceUnit.Room(SurfaceUnit.Plain);
        map.WorldSolids.Add(VmfMap.Box(
            TestMapCatalog.StandardRoom.Mins,
            new Point(TestMapCatalog.StandardRoom.Maxs.X, TestMapCatalog.StandardRoom.Maxs.Y, TestMapCatalog.StandardRoom.Mins.Z + 64f),
            NoDraw,
            SurfaceUnit.Water));

        if (cubemap)
        {
            RoomKit.PointEntity(map, "env_cubemap", new Point(0f, 0f, 128f));
        }

        return map;
    }

    // l1_water_overlay's shape: an info_overlay_transition whose
    // overlaytransition block puts one overlaydata on the pool's surface.
    private static VmfMap WaterOverlayRoom()
    {
        VmfMap map = PoolRoom(cubemap: false);
        int surface = TestMapCatalog.SideId(map, RoomKit.ShellBrushes, 0);

        VmfEntity transition = RoomKit.PointEntity(map, "info_overlay_transition", new Point(0f, 0f, 64f));
        VmfChunkNode block = new() { Name = "overlaytransition" };
        VmfChunkNode data = new() { Name = "overlaydata" };
        foreach ((string key, string value) in new[]
        {
            ("material", Decal), ("StartU", "0"), ("EndU", "1"), ("StartV", "0"), ("EndV", "1"),
            ("BasisOrigin", "[0 0 64]"), ("BasisU", "[1 0 0]"), ("BasisV", "[0 1 0]"), ("BasisNormal", "[0 0 1]"),
            ("uv0", "[-24 -24 0]"), ("uv1", "[-24 24 0]"), ("uv2", "[24 24 0]"), ("uv3", "[24 -24 0]"),
            ("sides", $"{surface}"),
        })
        {
            data.KeyValues.Add(new(key, value));
        }

        block.Children.Add(data);
        transition.Chunks.Add(block);
        return map;
    }

    private static IReadOnlyList<string> FaceMaterials(BspData bsp)
    {
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        return [.. faces.Select(f => MaterialOf(bsp, f.TexInfo)).Distinct()];
    }

    private static VmfMap OverlayRoom()
    {
        VmfMap map = SurfaceUnit.Room(SurfaceUnit.Plain);
        int floor = TestMapCatalog.SideId(map, 0, 0);
        int wall = TestMapCatalog.SideId(map, 2, 2);
        Point at = new(0, 0, 0);
        foreach (string sides in new[] { $"{floor}", $"{floor} {wall}" })
        {
            RoomKit.PointEntity(map, "info_overlay", at,
                "material", Decal, "sides", sides, "RenderOrder", "0",
                "StartU", "0", "EndU", "1", "StartV", "0", "EndV", "1",
                "BasisOrigin", "0 0 0", "BasisU", "1 0 0", "BasisV", "0 1 0", "BasisNormal", "0 0 1",
                "uv0", "-32 -32 0", "uv1", "-32 32 0", "uv2", "32 32 0", "uv3", "32 -32 0",
                "fademindist", "-1", "fademaxdist", "0", "angles", "0 0 0");
        }

        return map;
    }

    private static void AddDecal(SourceSharp.MapTools.Io.InMemoryFileSystem files) =>
        files.AddText($"materials/{Decal}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");

    private static async Task<BspData> CompileAsync(VmfMap vmf)
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, ComplianceOptions.Correct, AddDecal);
        VmfDocument document = await VmfDocument.ParseAsync(vmf.Write());
        VbspResult result = await SurfaceContentVbsp.CompileAsync(loaded.Map, loaded.Context, document);
        return result.Bsp!;
    }

    private static string MaterialOf(BspData bsp, int texInfo)
    {
        TexInfo info = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])[texInfo];
        DTexData data = BspStructView.As<DTexData>(bsp[BspLump.TexData])[info.TexData];
        int start = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[data.NameStringTableId];
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span[start..];
        return System.Text.Encoding.Latin1.GetString(strings[..strings.IndexOf((byte)0)]);
    }
}
