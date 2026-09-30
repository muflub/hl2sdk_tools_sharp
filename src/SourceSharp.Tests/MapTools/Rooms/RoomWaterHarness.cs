//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with water: a cheap and an expensive water material (each with its
/// underside), pools built into the harness rooms, and the room compile,
/// link and flattened compile the water facts run.
/// </summary>
/// <remarks>
/// The rooms are <see cref="RoomPropHarness.Hub"/> and
/// <see cref="RoomPropHarness.Other"/> on the walkable kit: a 256-unit
/// cell, a 16-unit shell, a door 96 wide from the floor (z 16) to the
/// ceiling (z 240). A pool is a water brush standing on the floor; it is
/// compiled with the managed cooker, so its fluid is in the room's
/// collision, as <c>ssmap room</c> compiles it.
/// </remarks>
internal static class RoomWaterHarness
{
    /// <summary>A cheap water: no reflection, fog, an underside.</summary>
    public const string CheapWater = "unit/water_cheap";

    /// <summary>An expensive water: reflection and refraction render targets, fog, an underside.</summary>
    public const string ExpensiveWater = "unit/water_expensive";

    /// <summary>The underside both waters name as <c>$bottommaterial</c>.</summary>
    public const string Beneath = "unit/water_beneath";

    /// <summary>A water vrad lights (<c>%compileKeepLight</c>): its faces take lightmaps like any face.</summary>
    public const string LitWater = "unit/water_lit";

    /// <summary>The first id the water brushes take; a brush's sides are its id times 8.</summary>
    public const int WaterBrush = 7000;

    /// <summary>A pool in the hub's south-west quarter, away from every socket: floor to z 56.</summary>
    public static Box Pool { get; } = new(new Vec3(40, 40, 16), new Vec3(104, 104, 56));

    /// <summary>The materials' files.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [$"materials/{CheapWater}.vmt"] = Encoding.ASCII.GetBytes(
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"1\"\n"
            + $"\t\"$bottommaterial\" \"{Beneath}\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n"),
        [$"materials/{ExpensiveWater}.vmt"] = Encoding.ASCII.GetBytes(
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"1\"\n"
            + $"\t\"$bottommaterial\" \"{Beneath}\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$reflecttexture\" \"_rt_WaterReflection\"\n\t\"$refracttexture\" \"_rt_WaterRefraction\"\n"
            + "\t\"$forceexpensive\" \"1\"\n\t\"$reflectentities\" \"1\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{30 40 60}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"600\"\n}\n"),
        [$"materials/{LitWater}.vmt"] = Encoding.ASCII.GetBytes(
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"%compileKeepLight\" \"1\"\n\t\"$abovewater\" \"1\"\n"
            + $"\t\"$bottommaterial\" \"{Beneath}\"\n\t\"$surfaceprop\" \"water\"\n" + "}\n"),
        [$"materials/{Beneath}.vmt"] = Encoding.ASCII.GetBytes(
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"0\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n"),
    };

    /// <summary>A compile context whose content holds the harness materials, the models and the waters.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1, ManagedCollisionCooker? cooker = null)
    {
        Dictionary<string, byte[]> files = new(RoomPropHarness.Models(), StringComparer.Ordinal);
        foreach ((string path, byte[] bytes) in Files())
        {
            files[path] = bytes;
        }

        foreach ((string path, byte[] bytes) in RoomOverlayHarness.Files())
        {
            files[path] = bytes;
        }

        VbspContext context = await RoomHarness.ContextAsync(extraFiles: files);
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        context.CollisionCooker = cooker;
        return context;
    }

    /// <summary>A water brush, every side the one material, as a room-local box.</summary>
    public static VmfChunk Water(Box box, int id = WaterBrush, string material = CheapWater) =>
        RoomModel.Slab(material, box.Mins, box.Maxs, id);

    /// <summary>
    /// A water brush whose six sides have six ids, the top (the first) the
    /// brush id times 8 and the rest the next five, so a water overlay can
    /// name its surface alone.
    /// </summary>
    public static VmfChunk NumberedWater(Box box, int id = WaterBrush, string material = CheapWater)
    {
        VmfChunk solid = Water(box, id, material);
        int k = 0;
        foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
        {
            side.Keys.First(key => key.Name == "id").Value = (id * 8 + k++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return solid;
    }

    /// <summary>
    /// A water overlay (an <c>overlaydata</c> chunk, its vectors in the
    /// brackets vbsp reads): a 32 x 32 square flat on the surface at a
    /// room-local point, on the sides named.
    /// </summary>
    public static VmfChunk WaterOverlay(Vec3 at, string sides, string u = "1 0 0", string v = "0 1 0")
    {
        VmfChunk data = new(MapFileLoader.OverlayDataChunk);
        data.AddKey("material", RoomOverlayHarness.OverlayMaterial);
        data.AddKey("StartU", "0");
        data.AddKey("EndU", "1");
        data.AddKey("StartV", "0");
        data.AddKey("EndV", "1");
        data.AddKey("BasisOrigin", $"[{VmfPlacement.Format(at)}]");
        data.AddKey("BasisU", $"[{u}]");
        data.AddKey("BasisV", $"[{v}]");
        data.AddKey("BasisNormal", "[0 0 1]");
        data.AddKey("uv0", "[-16 -16 0]");
        data.AddKey("uv1", "[-16 16 0]");
        data.AddKey("uv2", "[16 16 0]");
        data.AddKey("uv3", "[16 -16 0]");
        data.AddKey("sides", sides);
        return data;
    }

    /// <summary>An <c>info_overlay_transition</c> at a room-local point holding water overlays, as the editor writes one.</summary>
    public static VmfChunk Transition(int id, Vec3 at, params VmfChunk[] overlays)
    {
        VmfChunk entity = RoomPropHarness.Entity("info_overlay_transition", id, at);
        VmfChunk block = new(MapFileLoader.OverlayTransitionChunk);
        foreach (VmfChunk overlay in overlays)
        {
            block.Children.Add(overlay);
        }

        entity.Children.Add(block);
        return entity;
    }

    /// <summary>A map's water overlay records.</summary>
    public static DWaterOverlay[] WaterOverlays(BspData bsp) => BspStructView.As<DWaterOverlay>(bsp[BspLump.WaterOverlays]).ToArray();

    /// <summary>
    /// A map's water overlays in what the engine draws of each, but the face
    /// list: id, material, origin, basis normal, the four UV points and the
    /// extents, bit for bit; and the area of the overlay's square its faces
    /// cover, to the hundredth (<see cref="RoomOverlayHarness.Covered"/>).
    /// </summary>
    public static List<string> ObservedWaterOverlays(BspData bsp)
    {
        List<string> observed = [];
        foreach (DWaterOverlay o in WaterOverlays(bsp))
        {
            DOverlay asOverlay = new()
            {
                Id = o.Id,
                TexInfo = o.TexInfo,
                FaceCountAndRenderOrder = o.FaceCountAndRenderOrder,
                U = o.U,
                V = o.V,
                UvPoints = o.UvPoints,
                Origin = o.Origin,
                BasisNormal = o.BasisNormal,
            };
            for (int f = 0; f < o.GetFaceCount(); f++)
            {
                asOverlay.Faces[f] = o.Faces[f];
            }

            System.Text.StringBuilder text = new();
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{o.Id} {RoomBrushHarness.Material(bsp, o.TexInfo)} at {V(o.Origin)} n {V(o.BasisNormal)} uv");
            for (int p = 0; p < 4; p++)
            {
                text.Append(' ').Append(V(o.UvPoints[p]));
            }

            text.Append(System.Globalization.CultureInfo.InvariantCulture, $" u {F(o.U[0])} {F(o.U[1])} v {F(o.V[0])} {F(o.V[1])}");
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $" covers {RoomOverlayHarness.Covered(bsp, asOverlay):0.00}");
            observed.Add(text.ToString());
        }

        return observed;

        static string V(Vec3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

        static string F(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The harness library of <see cref="RoomPropHarness.Hub"/> and
    /// <see cref="RoomPropHarness.Other"/> with world brushes added to room
    /// 0 (hub) or 1 (other) at room-local coordinates, and any extra
    /// entities likewise.
    /// </summary>
    public static VmfDocument Library((int Room, VmfChunk Solid)[] solids, params (int Room, VmfChunk Entity)[] entities)
    {
        VmfDocument library = RoomPropHarness.Library(entities);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        foreach ((int room, VmfChunk solid) in solids)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            world.Children.Add(VmfPlacement.MoveSolid(solid, QuarterTurn.Translation(corner)));
        }

        return library;
    }

    /// <summary>A second pool, in the other room's north-east quarter, deeper and higher: floor to z 88.</summary>
    public static Box OtherPool { get; } = new(new Vec3(152, 152, 16), new Vec3(216, 216, 88));

    /// <summary>
    /// The hub's cheap pool with two water overlays on its surface (one an
    /// <c>info_overlay_transition</c>'s, one the library world's, off the
    /// grid with a turned basis), and the other room's expensive pool.
    /// </summary>
    public static VmfDocument ShowcaseLibrary()
    {
        string surface = (WaterBrush * 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        VmfDocument library = Library(
            [(0, NumberedWater(Pool)), (1, Water(OtherPool, WaterBrush + 1, ExpensiveWater))],
            (0, Transition(900, new Vec3(72, 72, 56), WaterOverlay(new Vec3(64, 64, 56), surface))));
        VmfChunk transition = new(MapFileLoader.OverlayTransitionChunk);
        transition.Children.Add(WaterOverlay(new Vec3(80.3f, 71.1f, 56), surface, "0 1 0", "-1 0 0"));
        library.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(transition);
        return library;
    }

    /// <summary>The library with the hub's pool of cheap water.</summary>
    public static VmfDocument PoolLibrary(string material = CheapWater) => Library([(0, Water(Pool, material: material))]);

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them, with the managed cooker, each on <paramref name="degree"/> threads.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name, degree, cooker)));
        }

        return compiled;
    }

    /// <summary>
    /// The library's rooms compiled and lit as <c>ssmap room</c> does, with
    /// the waters in the content (<see cref="RoomLightHarness.CompileAsync"/>'s
    /// bake and switches).
    /// </summary>
    public static async Task<RoomLibrary> CompileLitAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        RoomLightingSettings settings = RoomLightHarness.Settings(split);
        foreach (LibraryRoom room in split.Rooms)
        {
            VbspContext context = await ContextAsync(room.Definition.Name);
            RoomObject compiledRoom = await RoomCompiler.CompileAsync(room.Document, room.Definition, context);
            compiled.Add(compiledRoom with
            {
                Lighting = await RoomLighting.BakeAsync(compiledRoom, settings, context.Content!, context.Parallelism, CancellationToken.None),
            });
        }

        return compiled;
    }

    /// <summary>A linked level lit afresh by vrad, with the waters in the content and the bake's switches.</summary>
    public static async Task<BspData> RelightAsync(BspData linked)
    {
        VbspContext context = await ContextAsync("relit");
        BspData copy = RoomLighting.Copy(linked);
        _ = await SourceSharp.MapTools.Rad.Vrad.LightAsync(
            copy, new SourceSharp.MapTools.Rad.VradContext { Options = RoomLightHarness.Options, MapName = "relit", Content = context.Content });
        return copy;
    }

    /// <summary>The level flattened and compiled whole, with the managed cooker.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("flat", cooker: cooker));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's leaf water data.</summary>
    public static DLeafWaterData[] WaterData(BspData bsp) => BspStructView.As<DLeafWaterData>(bsp[BspLump.LeafWaterData]).ToArray();

    /// <summary>
    /// What a map holds at a world point: the leaf's contents (water or not,
    /// solid or not) and, in water, the surface height and the material of
    /// the leaf's water data.
    /// </summary>
    public static string At(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        int node = 0;
        while (node >= 0)
        {
            DNode n = nodes[node];
            DPlane plane = planes[n.PlaneNum];
            node = Vec3.Dot(plane.Normal, point) - plane.Dist >= 0 ? n.Children[0] : n.Children[1];
        }

        DLeaf leaf = BspStructView.As<DLeaf>(bsp[BspLump.Leafs])[-1 - node];
        if ((leaf.Contents & (int)BrushContents.Solid) != 0)
        {
            return "solid";
        }

        if ((leaf.Contents & (int)BrushContents.Water) == 0)
        {
            return leaf.LeafWaterDataId == -1 ? "air" : $"air with water data {leaf.LeafWaterDataId}";
        }

        if (leaf.LeafWaterDataId < 0)
        {
            return "water without data";
        }

        DLeafWaterData data = WaterData(bsp)[leaf.LeafWaterDataId];
        return $"water at {data.SurfaceZ} of {RoomBrushHarness.Material(bsp, data.SurfaceTexInfoId)}";
    }
}
