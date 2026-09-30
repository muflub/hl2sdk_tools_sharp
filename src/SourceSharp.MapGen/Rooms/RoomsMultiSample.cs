//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// The multi-library sample (the rooms design, 17.12): two libraries on the
/// 3x3 kit, one sun under the singleton rules and one 3D skybox, tall rooms
/// of two heights, levels that place rooms of both, and levels
/// <c>ssmap layout</c> draws from both with <c>-large</c>, as files under
/// <c>samples/rooms-multi/</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>base.vmf</c> (key <c>base</c>): the 3x3 kit's five kinds as the
/// 3x3 sample has them, and a <c>light_environment</c> in the gaps. It
/// supplies the level's sun (the first library's, 17.4).</item>
/// <item><c>caves.vmf</c> (key <c>caves</c>): a <c>hall</c> and an
/// <c>end</c> of its own (the same names as two of base's, so a level names
/// them qualified or by an alias, and each links under its <c>MapBase</c>
/// namespace), <c>cavern</c> and <c>gallery</c> 512 tall, <c>shaft</c> 768
/// tall with a sky ceiling, the library's 3D skybox (<c>sky</c>), and the
/// same sun as base's. base has no skybox, so the level takes caves' (D29:
/// the earliest library that has one); caves' sun is base's to the key, so
/// the link and the flatten say once that its copy is dropped as equal
/// (D24), and <c>shaft</c>, the room the sun reaches, is baked under the
/// sun and over the skybox the level uses, packed apart or combined.</item>
/// <item><c>levels/mixed.yaml</c> and its three whole-level turns: a 3 x 3
/// level of both libraries, its cells bare (a name one library has),
/// qualified (<c>base.end</c>, <c>caves.end</c>) and aliased (<c>H</c> for
/// <c>caves.hall</c>), with each tall room at every rotation over the four
/// files.</item>
/// <item><c>levels/large_*.yaml</c>: exactly what <c>ssmap layout
/// base=../base.vmf caves=../caves.vmf -rows 4 -columns 4 -seed N -large
/// 0.4 -group 3 -out levels/large_N.yaml</c> writes: groups of tall rooms
/// among the standard ones.</item>
/// </list>
/// <para>
/// <c>ssmap roompack -out multi.roompack base=base.vmf caves=caves.vmf</c>
/// combines the two into one pack; the README runs it, and a fact holds
/// every level linked from it to the level linked from separate packs.
/// Deterministic: the same bytes on every run and host.
/// </para>
/// </remarks>
public static class RoomsMultiSample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-multi";

    /// <summary>The first library's key and file.</summary>
    public const string BaseKey = "base";

    /// <summary>The second library's key and file.</summary>
    public const string CavesKey = "caves";

    /// <summary>The sky material: <c>%compileSky</c>, the shaft's ceiling and the skybox's shell.</summary>
    public const string SkyMaterial = "roomsmulti/sky";

    /// <summary>The skybox room's name.</summary>
    public const string SkyboxName = "sky";

    /// <summary>The first tall height: <c>cavern</c> and <c>gallery</c>.</summary>
    public const float CavernHeight = 512f;

    /// <summary>The second tall height: <c>shaft</c>.</summary>
    public const float ShaftHeight = 768f;

    /// <summary>The seeds of the <c>-large</c> levels.</summary>
    public static IReadOnlyList<ulong> LargeSeeds { get; } = [1, 2];

    /// <summary>The share of the occupied cells the <c>-large</c> levels' groups cover.</summary>
    public const double LargeShare = 0.4;

    /// <summary>The largest group the <c>-large</c> levels grow.</summary>
    public const int GroupSize = 3;

    /// <summary>The <c>-large</c> levels' grid edge.</summary>
    public const int LargeGrid = 4;

    /// <summary>The name of the mixed level.</summary>
    public const string MixedName = "mixed";

    /// <summary>The game name the sample's <c>gameinfo.txt</c> gives.</summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms multi-library sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    /// <summary>A room of a library: its kind, height and ceiling.</summary>
    /// <param name="Kind">Its sockets, features and point entities.</param>
    /// <param name="Height">Its height; the cell size for a cube.</param>
    /// <param name="Ceiling">Its ceiling's material.</param>
    public sealed record LibraryKind(RoomKind Kind, float Height, string Ceiling = Rooms3x3Kit.CeilingMaterial)
    {
        /// <summary>The room as the room pipeline defines it.</summary>
        public RoomDefinition Definition => Rooms3x3Kit.Definition(Kind) with { Height = Height };
    }

    /// <summary>base's rooms: the 3x3 kit's five kinds.</summary>
    public static IReadOnlyList<LibraryKind> BaseRooms { get; } = [.. Rooms3x3Kit.Kinds.Select(k => new LibraryKind(k, Rooms3x3Kit.CellSize))];

    /// <summary>caves' rooms, in library order (its skybox after them).</summary>
    public static IReadOnlyList<LibraryKind> CavesRooms { get; } =
    [
        new LibraryKind(new RoomKind(
            "hall",
            [KitSide.East, KitSide.West],
            [new KitBrush(new Bounds(new(96, 192, 16), new(160, 224, 96)), Rooms3x3Kit.GrateMaterial)],
            [Light(208)]), Rooms3x3Kit.CellSize),
        new LibraryKind(new RoomKind(
            "end",
            [KitSide.East],
            [new KitBrush(new Bounds(new(32, 32, 16), new(96, 96, 48)), Rooms3x3Kit.BlockMaterial)],
            [Light(208)]), Rooms3x3Kit.CellSize),
        new LibraryKind(new RoomKind(
            "cavern",
            [KitSide.East, KitSide.West, KitSide.North, KitSide.South],
            [new KitBrush(new Bounds(new(32, 192, 16), new(64, 224, CavernHeight - Rooms3x3Kit.Wall)), Rooms3x3Kit.BlockMaterial)],
            [Light(208), Light(448)]), CavernHeight),
        new LibraryKind(new RoomKind(
            "gallery",
            [KitSide.East, KitSide.West],
            [new KitBrush(new Bounds(new(96, 192, 240), new(160, 240, 272)), Rooms3x3Kit.BlockMaterial)],
            [Light(208), Light(448)]), CavernHeight),
        new LibraryKind(new RoomKind(
            "shaft",
            [KitSide.East, KitSide.North],
            [new KitBrush(new Bounds(new(32, 32, 16), new(64, 64, 400)), Rooms3x3Kit.BlockMaterial)],
            [Light(208)]), ShaftHeight, SkyMaterial),
    ];

    /// <summary>Where the sky camera stands in the skybox, room-local.</summary>
    public static Point SkyCamera { get; } = new(128, 128, 128);

    /// <summary>The block in the skybox, room-local: an overhang above the camera, so the skybox shades what the sun reaches.</summary>
    public static Bounds SkyboxBlock { get; } = new(new Point(96, 96, 160), new Point(176, 176, 176));

    /// <summary>The level's aliases: <c>H</c> for caves' hall.</summary>
    public static IReadOnlyList<LevelAlias> Aliases { get; } = [new LevelAlias("H", "caves.hall")];

    /// <summary>The mixed level's libraries, as a level in <c>levels/</c> names them.</summary>
    public static IReadOnlyList<LevelLibrary> Libraries { get; } =
        [new LevelLibrary(BaseKey, $"../{BaseKey}.vmf"), new LevelLibrary(CavesKey, $"../{CavesKey}.vmf")];

    /// <summary>
    /// The mixed level, north row first: base's standard rooms down the west
    /// and the middle, caves' tall rooms up the east and the north.
    /// </summary>
    /// <remarks>
    /// Joints: corner and tee, tee and cross, tee and base's end, corner and
    /// caves' hall, hall and caves' end, cross and the cavern, cross and the
    /// gallery, cavern and shaft. The cavern's south door and the gallery's
    /// north door are capped (a plain wall, the grid's edge), and so is the
    /// shaft's west door, facing the gallery's side; every room is reached.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<LevelCell?>> MixedRows { get; } =
    [
        [new LevelCell("base.end", 3), new LevelCell("gallery", 1), new LevelCell("shaft", 2)],
        [new LevelCell("tee", 3), new LevelCell("cross", 0), new LevelCell("cavern", 1)],
        [new LevelCell("corner", 0), new LevelCell("H", 0), new LevelCell("caves.end", 2)],
    ];

    /// <summary>The mixed level's file name for a whole-level turn.</summary>
    /// <param name="turns">Quarter turns, 0 to 3.</param>
    public static string TurnName(int turns) =>
        turns == 0 ? MixedName : string.Create(CultureInfo.InvariantCulture, $"{MixedName}_turn{turns}");

    /// <summary>A <c>-large</c> level's name.</summary>
    /// <param name="seed">Its seed.</param>
    public static string LargeName(ulong seed) => string.Create(CultureInfo.InvariantCulture, $"large_{seed}");

    /// <summary>The <c>ssmap layout</c> options a <c>-large</c> level is drawn with.</summary>
    /// <param name="seed">Its seed.</param>
    public static LevelGeneratorOptions LargeOptions(ulong seed) =>
        new(LargeGrid, LargeGrid, seed) { LargeShare = LargeShare, GroupSize = GroupSize };

    /// <summary>Every generated file: its path under <see cref="Folder"/> and its bytes, in ordinal path order.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(GameInfo),
            [$"{BaseKey}.vmf"] = Encoding.UTF8.GetBytes(BaseVmf()),
            [$"{CavesKey}.vmf"] = Encoding.UTF8.GetBytes(CavesVmf()),
        };

        foreach ((string path, string vmt) in Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        LevelGrid level = RoomsSampleLevels.FromRows(MixedName, Libraries[0].Path, MixedRows, Libraries, Aliases);
        for (int turns = 0; turns < 4; turns++)
        {
            string name = TurnName(turns);
            files[$"levels/{name}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(RoomsSampleLevels.Renamed(level, name),
            [
                turns == 0
                    ? "a level of two libraries: base's rooms, caves' tall rooms, an alias and qualified names"
                    : string.Create(CultureInfo.InvariantCulture, $"the multi-library sample level turned {turns * 90} degrees"),
                "the grid's first line is the north row; each line runs west to east",
            ]));
            level = RoomsSampleLevels.Turned(level);
        }

        foreach (ulong seed in LargeSeeds)
        {
            LevelGrid large = Large(seed);
            files[$"levels/{large.Name}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(large, LevelGenerator.Header(LargeOptions(seed), large)));
        }

        return files;
    }

    /// <summary>
    /// A <c>-large</c> level, as <c>ssmap layout</c> draws it from the two
    /// libraries: every room of each under its qualified name, in operand
    /// order then library order, then each cell written in its shortest
    /// spelling (bare where one library has the name).
    /// </summary>
    /// <param name="seed">Its seed.</param>
    public static LevelGrid Large(ulong seed)
    {
        RoomDefinition[] definitions =
        [
            .. BaseRooms.Select(r => r.Definition with { Name = LevelLibraries.Qualified(BaseKey, r.Kind.Name) }),
            .. CavesRooms.Select(r => r.Definition with { Name = LevelLibraries.Qualified(CavesKey, r.Kind.Name) }),
        ];
        RoomRole[] roles = [.. definitions.Select(_ => RoomRole.None)];
        LevelGrid made = LevelGenerator.Generate(
            definitions, LargeOptions(seed), LargeName(seed), Libraries[0].Path, null, new LayoutTransitions(roles));
        IReadOnlyList<IReadOnlyList<string>> names =
        [
            [.. BaseRooms.Select(r => r.Kind.Name)],
            [.. CavesRooms.Select(r => r.Kind.Name), SkyboxName],
        ];
        return LevelLibraries.Shorten(
            new LevelGrid(made.Name, made.Library, made.Rows, made.Columns, made.Cells) { Libraries = Libraries }, names);
    }

    /// <summary>base's library VMF: the kit's five kinds in a line, and the sun in the first gap.</summary>
    public static string BaseVmf() => LibraryVmf(BaseRooms, skybox: false);

    /// <summary>caves' library VMF: its rooms in a line, its skybox after them, and the same sun in the first gap.</summary>
    public static string CavesVmf() => LibraryVmf(CavesRooms, skybox: true);

    /// <summary>The kit's materials and the sky.</summary>
    public static IReadOnlyList<(string Path, string Vmt)> Materials() =>
    [
        .. Rooms3x3Kit.Materials(),
        ($"materials/{SkyMaterial}.vmt", "\"UnlitGeneric\"\n{\n" + $"\t\"$basetexture\" \"{SkyMaterial}\"\n" + "\t\"%compileSky\" \"1\"\n}\n"),
    ];

    private static string LibraryVmf(IReadOnlyList<LibraryKind> rooms, bool skybox)
    {
        VmfMap map = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            LibraryKind room = rooms[i];
            Point corner = Rooms3x3Kit.LibraryCorner(i);
            Rooms3x3Kit.Place(map, room.Kind, Rooms3x3Placement.Identity, open: null, offset: corner, height: room.Height, ceilingMaterial: room.Ceiling);
            VmfEntity marker = Marker(RoomLibraryVmf.RoomEntity, room.Kind.Name, corner);
            marker.Set("cell_size", Number(Rooms3x3Kit.CellSize));
            marker.Set("door_width", Number(Rooms3x3Kit.DoorWidth));
            marker.Set("door_height", Number(Rooms3x3Kit.DoorHeight));
            marker.Set("wall_depth", Number(Rooms3x3Kit.Wall));
            if (room.Height != Rooms3x3Kit.CellSize)
            {
                marker.Set(RoomLibraryVmf.RoomHeightKey, Number(room.Height));
            }

            map.Entities.Add(marker);
        }

        if (skybox)
        {
            // The skybox cell: a shell of sky with no door, the overhang and
            // the camera. It takes the grid of the rooms and is placed by
            // the link one cell below every level's south-west cell.
            Point corner = Rooms3x3Kit.LibraryCorner(rooms.Count);
            const float c = Rooms3x3Kit.CellSize;
            const float t = Rooms3x3Kit.Wall;
            Bounds[] shell =
            [
                new(new(0, 0, 0), new(c, c, t)),
                new(new(0, 0, c - t), new(c, c, c)),
                new(new(c - t, 0, t), new(c, c, c - t)),
                new(new(0, 0, t), new(t, c, c - t)),
                new(new(t, c - t, t), new(c - t, c, c - t)),
                new(new(t, 0, t), new(c - t, t, c - t)),
            ];
            foreach (Bounds slab in shell)
            {
                map.WorldSolids.Add(VmfMap.Box(slab.Mins + corner, slab.Maxs + corner, SkyMaterial));
            }

            map.WorldSolids.Add(VmfMap.Box(SkyboxBlock.Mins + corner, SkyboxBlock.Maxs + corner, Rooms3x3Kit.BlockMaterial));
            VmfEntity camera = new() { ClassName = "sky_camera" };
            camera.Set("origin", (SkyCamera + corner).ToString());
            camera.Set("scale", "16");
            camera.Set("angles", "0 0 0");
            map.Entities.Add(camera);
            map.Entities.Add(Marker(RoomLibraryVmf.SkyboxEntity, SkyboxName, corner));
        }

        // The sun, the same in both libraries, in the gap after the first room.
        VmfEntity sun = new() { ClassName = "light_environment" };
        sun.Set("origin", new Point(Rooms3x3Kit.CellSize + (Rooms3x3Kit.LibraryGap / 2), 128, 128).ToString());
        sun.Set("angles", "0 30 0");
        sun.Set("pitch", "-50");
        sun.Set("_light", "255 250 230 400");
        sun.Set("_ambient", "120 140 180 60");
        map.Entities.Add(sun);
        return map.Write();
    }

    private static VmfEntity Marker(string classname, string name, Point corner)
    {
        VmfEntity marker = new() { ClassName = classname };
        marker.Set("origin", corner.ToString());
        marker.Set(RoomLibraryVmf.NameKey, name);
        return marker;
    }

    private static KitEntity Light(float z) =>
        new("light", new Point(128, 128, z), null, [new("_light", "255 240 220 200")]);

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
