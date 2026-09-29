//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with static props: two synthetic models, a harness library of two
/// walkable rooms that props are added to, and the room compile, link and
/// flattened compile the static prop facts run.
/// </summary>
/// <remarks>
/// The models are written by <see cref="StudioModelWriter"/> into the
/// harness's in-memory content, which the room compiles and the flattened
/// compile read; the links get a context without them, so every fact that
/// links also shows the link needs no game files (decision D1).
/// </remarks>
internal static class RoomPropHarness
{
    /// <summary>A 16 x 16 x 32 box standing on its origin.</summary>
    public const string BoxModel = "models/props_test/box.mdl";

    /// <summary>An 8 x 8 x 64 post standing on its origin.</summary>
    public const string PostModel = "models/props_test/post.mdl";

    /// <summary>A 48 x 8 x 32 bar along x standing on its origin: a door frame's lintel, reaching 24 each way.</summary>
    public const string BarModel = "models/props_test/bar.mdl";

    /// <summary>An 80 x 8 x 32 beam along x standing on its origin, reaching 40 each way.</summary>
    public const string BeamModel = "models/props_test/beam.mdl";

    /// <summary>The models' files, as a game would ship them.</summary>
    public static IReadOnlyDictionary<string, byte[]> Models()
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (StudioModelSpec spec in new[]
        {
            Spec("props_test/box.mdl", new Vec3(-8, -8, 0), new Vec3(8, 8, 32)),
            Spec("props_test/post.mdl", new Vec3(-4, -4, 0), new Vec3(4, 4, 64)),
            Spec("props_test/bar.mdl", new Vec3(-24, -4, 0), new Vec3(24, 4, 32)),
            Spec("props_test/beam.mdl", new Vec3(-40, -4, 0), new Vec3(40, 4, 32)),
        })
        {
            foreach ((string path, byte[] bytes) in StudioModelWriter.Write(spec))
            {
                files[path] = bytes;
            }
        }

        return files;

        static StudioModelSpec Spec(string name, Vec3 mins, Vec3 maxs) => new()
        {
            Name = name,
            Materials = ["prop"],
            MaterialSearchPaths = ["models/props_test/"],
            Meshes = [Primitives.Box(0, mins, maxs)],
        };
    }

    /// <summary>A compile context whose content holds the harness materials and the models.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1)
    {
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: Models());
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return context;
    }

    /// <summary>The four-socket walkable room most facts place.</summary>
    public static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>A second four-socket walkable room.</summary>
    public static RoomDefinition Other => RoomHarness.WalkableRoom(
        "other", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>A <c>prop_static</c> at a room-local origin.</summary>
    public static VmfChunk Prop(int id, string model, Vec3 origin, string angles = "0 0 0", params (string Key, string Value)[] keys)
    {
        VmfChunk entity = Entity("prop_static", id, origin, keys);
        entity.AddKey("model", model);
        entity.AddKey("angles", angles);
        return entity;
    }

    /// <summary>A point entity at a room-local origin.</summary>
    public static VmfChunk Entity(string classname, int id, Vec3 origin, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        entity.AddKey("origin", VmfPlacement.Format(origin));
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>
    /// The harness library of <see cref="Hub"/> and <see cref="Other"/>
    /// (<see cref="RoomHarness.LibraryVmf"/>), with each extra entity added
    /// to room 0 (hub) or 1 (other) at its room-local origin.
    /// </summary>
    public static VmfDocument Library(params (int Room, VmfChunk Entity)[] extra)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Other);
        foreach ((int room, VmfChunk entity) in extra)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(corner)));
        }

        return library;
    }

    /// <summary>The library's rooms compiled against the models, as <c>ssmap room</c> compiles them.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name)));
        }

        return compiled;
    }

    /// <summary>A library of compiled rooms on the walkable kit.</summary>
    public static RoomLibrary RoomsOf(params RoomObject[] rooms)
    {
        RoomLibrary library = new(RoomHarness.WalkableKit, RoomHarness.Cell);
        foreach (RoomObject room in rooms)
        {
            library.Add(room);
        }

        return library;
    }

    /// <summary>A level of the given rows (north row first), as <c>ssmap link</c> reads it.</summary>
    public static LevelGrid Level(params string[] rows) => LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "props");

    /// <summary>A level linked from compiled rooms, with a context that has no game files.</summary>
    public static async Task<LinkedLevel> LinkAsync(RoomLibrary library, LevelGrid level, int degree = 1)
    {
        LevelLayout layout = level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return await LevelLinker.LinkAsync(layout, library, context);
    }

    /// <summary>The layout a level gives the library.</summary>
    public static LevelLayout Layout(RoomLibrary library, LevelGrid level) =>
        level.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);

    /// <summary>The level flattened and compiled whole, against the models.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("props"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's static prop lump.</summary>
    public static StaticPropLump Props(BspData bsp) => RoomStaticProps.ReadLump(bsp)!;

    /// <summary>
    /// A map's static props in what a game observes of each (the rooms
    /// design, 4.3: model, origin, angles, skin, solidity, flags, fades,
    /// lighting origin), leaf lists left out, sorted, so the link and the
    /// flattened compile are compared whatever order each wrote them in.
    /// </summary>
    public static List<string> Observed(BspData bsp)
    {
        StaticPropLump lump = Props(bsp);
        return [.. lump.Props.Select(p => string.Join(
                " | ",
                lump.ModelNames[p.PropType],
                V(p.Origin),
                V(p.Angles),
                RoomStaticProps.HasLightingOrigin(p) ? V(p.LightingOrigin) : "-",
                p.Skin.ToString(CultureInfo.InvariantCulture),
                p.Solid.ToString(CultureInfo.InvariantCulture),
                ((uint)p.Flags).ToString(CultureInfo.InvariantCulture),
                F(p.FadeMinDist),
                F(p.FadeMaxDist),
                F(p.ForcedFadeScale),
                p.MinDxLevel.ToString(CultureInfo.InvariantCulture),
                p.MaxDxLevel.ToString(CultureInfo.InvariantCulture)))
            .Order(StringComparer.Ordinal)];

        static string V(Vec3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

        static string F(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>A prop's leaves, as a set.</summary>
    public static HashSet<int> LeavesOf(StaticPropLump lump, StaticProp prop) =>
        [.. lump.LeafEntries.Skip(prop.FirstLeaf).Take(prop.LeafCount).Select(l => (int)l)];
}
