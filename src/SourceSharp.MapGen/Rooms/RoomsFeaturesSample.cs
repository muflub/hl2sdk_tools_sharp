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
/// The features sample: a room library on the 3x3 kit whose rooms carry the
/// map features the 3x3 kinds do not, and a level of them at its four whole
/// turns, as files under <c>samples/rooms-features/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sibling rather than the 3x3 sample grown</b> (the rooms design,
/// section 16, asked for the 3x3 kinds to grow one feature a PR). The 3x3
/// sample is pinned from several sides: its kinds are placed by the
/// generator's own monolithic map (<see cref="Rooms3x3Arrangement.MonolithicVmf"/>,
/// which draws boxes and nothing else), its equivalence facts sample every
/// point of the level for contents and trace every pair of room centres, its
/// seeded levels are pinned by SHA-256 as <c>ssmap layout</c> wrote them, and
/// every PR since 14 recorded that its levels link to the base's bytes. A
/// pool, a displacement or a tall room in a 3x3 kind would change every one
/// of those at once, and the monolithic map would need a displacement and
/// water writer of its own that the flatten already is. A sibling on the
/// same kit carries every feature through the same commands, and the 3x3
/// levels keep being the regression they are.
/// </para>
/// <list type="bullet">
/// <item><c>garden</c>: four doors; a slab whose top grows grass detail
/// props (<c>%detailtype</c>), an <c>info_poi</c> marked on the level map
/// (<c>map_marker</c>, <c>map_label</c>), the room's own <c>map_label</c>,
/// and an <c>info_player_start</c>.</item>
/// <item><c>tower</c>: a tall room (<c>room_height 512</c>) with doors east
/// and west, a pillar to its ceiling, a second light near the top, and its
/// <c>map_label</c>.</item>
/// <item><c>ridge</c>: doors east and north; a power 3 displacement with a
/// 40-unit ridge down its middle (flanks too steep to stand on) and a power
/// 2 patch sharing its east edge.</item>
/// <item><c>pool</c>: doors east, west and north; a pool from the west plug
/// to the east plug, its surface 48 units above the floor, declared on
/// both walls as water sockets (<c>water_east</c>, <c>water_west</c>), so
/// two pools side by side carry their water through the doorway.</item>
/// </list>
/// <para>
/// The level is written by hand (here), not drawn by <c>ssmap layout</c>,
/// which does not read water sockets and may joint a water socket to a dry
/// one; its four files are the level and its whole-level quarter turns, so
/// every room (the tall room, the water joint, the displacements) is linked
/// at every rotation. Deterministic: the same bytes on every run and host.
/// </para>
/// </remarks>
public static class RoomsFeaturesSample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-features";

    /// <summary>The sample's own materials' folder, under <c>materials/</c>, beside the kit's.</summary>
    public const string MaterialFolder = "roomsfeatures";

    /// <summary>The grass: a lightmapped surface whose <c>%detailtype</c> is <see cref="DetailType"/>.</summary>
    public const string GrassMaterial = MaterialFolder + "/grass";

    /// <summary>The pools' water: cheap (no reflection), fogged, unlit, with an underside.</summary>
    public const string WaterMaterial = MaterialFolder + "/water";

    /// <summary>The water's underside, its <c>$bottommaterial</c>.</summary>
    public const string WaterBeneathMaterial = MaterialFolder + "/water_beneath";

    /// <summary>The detail type the grass names in <c>detail.vbsp</c>.</summary>
    public const string DetailType = "roomsfeatures_grass";

    /// <summary>The tall room's height.</summary>
    public const float TowerHeight = 512f;

    /// <summary>A water socket's surface, room-local z: 48 units above the floor's top (z 16).</summary>
    public const float WaterLevel = 64f;

    /// <summary>The level's base name.</summary>
    public const string LevelName = "features";

    /// <summary>The game name the sample's <c>gameinfo.txt</c> gives.</summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms features sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    /// <summary>
    /// The detail props the grass grows: one group of upright sprites, so
    /// the sample needs no model (a prop model would be game content the
    /// sample does not ship).
    /// </summary>
    public const string DetailVbsp =
        "detail.vbsp\n{\n"
        + "\t\"" + DetailType + "\"\n\t{\n"
        + "\t\t\"density\" \"8000.0\"\n"
        + "\t\t\"low\"\n\t\t{\n"
        + "\t\t\t\"alpha\" \"0\"\n"
        + "\t\t\t\"tuft\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"spritesize\" \"0.5 0 24 24\"\n"
        + "\t\t\t\t\"spriterandomscale\" \"0.2\"\n\t\t\t\t\"amount\" \"1\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t\t\"detailOrientation\" \"2\"\n\t\t\t}\n"
        + "\t\t}\n"
        + "\t}\n}\n";

    /// <summary>The garden's grass slab, room-local: off the centre, clear of every doorway.</summary>
    public static Bounds GrassSlab { get; } = new(new Point(48, 48, 16), new Point(208, 144, 20));

    /// <summary>The garden's marked point of interest, room-local.</summary>
    public static Point Fountain { get; } = new(64, 200, 16);

    /// <summary>The ridge room's power 3 patch, room-local: the brush under it.</summary>
    public static Bounds RidgePatch { get; } = new(new Point(32, 48, 16), new Point(160, 176, 24));

    /// <summary>The ridge room's power 2 patch, sharing the first's east edge.</summary>
    public static Bounds SidePatch { get; } = new(new Point(160, 48, 16), new Point(224, 176, 24));

    /// <summary>The pool's water, room-local: from the west plug to the east plug, across the doors' width, floor to <see cref="WaterLevel"/>.</summary>
    public static Bounds Pool { get; } = new(new Point(Rooms3x3Kit.Wall, 64, 16), new Point(Rooms3x3Kit.CellSize - Rooms3x3Kit.Wall, 192, WaterLevel));

    /// <summary>One room of the sample: its kind, height, labels and water sockets, and the brushes the kit's boxes cannot say.</summary>
    /// <param name="Kind">Its sockets, box features and point entities.</param>
    /// <param name="Height">Its height; the cell size for a cube.</param>
    /// <param name="Label">Its <c>map_label</c>, or null.</param>
    /// <param name="WaterSockets">The walls whose sockets declare water at <see cref="WaterLevel"/>.</param>
    /// <param name="Extra">
    /// World brushes the kit's boxes cannot say (displacements, grass,
    /// water), built with the room's cell corner at the point given: a
    /// displacement's start is a position, so the brush is built where it
    /// stands rather than moved afterwards.
    /// </param>
    public sealed record FeatureRoom(
        RoomKind Kind, float Height, string? Label, IReadOnlyList<KitSide> WaterSockets, Func<Point, IReadOnlyList<VmfSolid>> Extra)
    {
        /// <summary>The room as the room pipeline defines it: the kit's definition and its height.</summary>
        public RoomDefinition Definition => Rooms3x3Kit.Definition(Kind) with { Height = Height };
    }

    /// <summary>The library's rooms, in library order.</summary>
    public static IReadOnlyList<FeatureRoom> Rooms =>
    [
        new FeatureRoom(
            new RoomKind(
                "garden",
                [KitSide.East, KitSide.West, KitSide.North, KitSide.South],
                [],
                [
                    Light(208),
                    new KitEntity("info_player_start", new Point(200, 200, 17), 180, []),
                    new KitEntity("info_poi", Fountain, 0,
                        [new("poi_type", "landmark"), new("map_marker", "fountain"), new("map_label", "Fountain")]),
                ]),
            Rooms3x3Kit.CellSize,
            "Garden",
            [],
            at => [VmfMap.Box(GrassSlab.Mins + at, GrassSlab.Maxs + at, Rooms3x3Kit.BlockMaterial, topMaterial: GrassMaterial)]),
        new FeatureRoom(
            new RoomKind(
                "tower",
                [KitSide.East, KitSide.West],
                [new KitBrush(new Bounds(new(32, 192, 16), new(64, 224, TowerHeight - Rooms3x3Kit.Wall)), Rooms3x3Kit.BlockMaterial)],
                [Light(208), Light(448)]),
            TowerHeight,
            "Tower",
            [],
            _ => []),
        new FeatureRoom(
            new RoomKind("ridge", [KitSide.East, KitSide.North], [], [Light(208)]),
            Rooms3x3Kit.CellSize,
            null,
            [],
            at => [Patch(RidgePatch.Offset(at), 3, RidgeHeight), Patch(SidePatch.Offset(at), 2, SideHeight)]),
        new FeatureRoom(
            new RoomKind("pool", [KitSide.East, KitSide.West, KitSide.North], [], [Light(208)]),
            Rooms3x3Kit.CellSize,
            null,
            [KitSide.East, KitSide.West],
            at => [VmfMap.Box(Pool.Mins + at, Pool.Maxs + at, WaterMaterial)]),
    ];

    /// <summary>
    /// The ridge patch's height over its brush at a vertex: a crest of 40
    /// units down the middle column, 20 either side of it, a few units
    /// elsewhere. A step of 18 to 20 units over the 16 between two columns is
    /// steeper than a player can stand on, so the level map shows ground on
    /// either side of the crest, not the flanks.
    /// </summary>
    public static float RidgeHeight(int row, int column) => Math.Abs(column - 4) switch
    {
        0 => 40,
        1 => 20,
        _ => 2 + ((row + column) % 3),
    };

    /// <summary>The side patch's height at a vertex: a few units, varying, so its surface is not its base face.</summary>
    public static float SideHeight(int row, int column) => 2 + (((row * 3) + column) % 4);

    /// <summary>
    /// The level, north row first as the file writes it: the two pools
    /// side by side (their water joint) under the garden and the tall
    /// tower, and the ridge rooms up the east column.
    /// </summary>
    /// <remarks>
    /// <c>pool</c>'s west water socket faces the grid's edge and its east
    /// one the ridge's plain west wall, so both are capped; the only water
    /// joint is the pools' own, at one level. Every room is reachable:
    /// garden and the west pool through the pool's north door, the pools
    /// through their water, garden, tower and the north ridge along the
    /// north row, the two ridges through their north and south doors.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<LevelCell?>> LevelRows { get; } =
    [
        [new LevelCell("garden", 0), new LevelCell("tower", 0), new LevelCell("ridge", 2)],
        [new LevelCell("pool", 0), new LevelCell("pool", 0), new LevelCell("ridge", 0)],
    ];

    /// <summary>The level's file name for a whole-level turn: <c>features</c>, then <c>features_turn1</c> to <c>3</c>.</summary>
    /// <param name="turns">Quarter turns, 0 to 3.</param>
    public static string TurnName(int turns) =>
        turns == 0 ? LevelName : string.Create(CultureInfo.InvariantCulture, $"{LevelName}_turn{turns}");

    /// <summary>Every generated file: its path under <see cref="Folder"/> and its bytes, in ordinal path order.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(GameInfo),
            ["detail.vbsp"] = Encoding.UTF8.GetBytes(DetailVbsp),
            [Rooms3x3Kit.LibraryFile] = Encoding.UTF8.GetBytes(LibraryVmf()),
        };

        foreach ((string path, string vmt) in Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        LevelGrid level = RoomsSampleLevels.FromRows(LevelName, Rooms3x3Sample.LibraryFromLevels, LevelRows);
        for (int turns = 0; turns < 4; turns++)
        {
            string name = TurnName(turns);
            LevelGrid named = RoomsSampleLevels.Renamed(level, name);
            files[$"levels/{name}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(named,
            [
                turns == 0
                    ? "the rooms features sample level"
                    : string.Create(CultureInfo.InvariantCulture, $"the rooms features sample level turned {turns * 90} degrees"),
                "the grid's first line is the north row; each line runs west to east",
            ]));
            level = RoomsSampleLevels.Turned(level);
        }

        return files;
    }

    /// <summary>
    /// The library VMF: every room in a line along +x, 128 units apart, each
    /// marked by an <c>info_room</c> with the kit, its height when it is not
    /// a cube, its water sockets and its label.
    /// </summary>
    public static string LibraryVmf()
    {
        VmfMap map = new();
        IReadOnlyList<FeatureRoom> rooms = Rooms;
        for (int i = 0; i < rooms.Count; i++)
        {
            FeatureRoom room = rooms[i];
            Point corner = Rooms3x3Kit.LibraryCorner(i);
            Rooms3x3Kit.Place(map, room.Kind, Rooms3x3Placement.Identity, open: null, offset: corner, height: room.Height);
            map.WorldSolids.AddRange(room.Extra(corner));

            VmfEntity marker = new() { ClassName = RoomLibraryVmf.RoomEntity };
            marker.Set("origin", corner.ToString());
            marker.Set(RoomLibraryVmf.NameKey, room.Kind.Name);
            marker.Set("cell_size", Number(Rooms3x3Kit.CellSize));
            marker.Set("door_width", Number(Rooms3x3Kit.DoorWidth));
            marker.Set("door_height", Number(Rooms3x3Kit.DoorHeight));
            marker.Set("wall_depth", Number(Rooms3x3Kit.Wall));
            if (room.Height != Rooms3x3Kit.CellSize)
            {
                marker.Set(RoomLibraryVmf.RoomHeightKey, Number(room.Height));
            }

            foreach (KitSide side in room.WaterSockets)
            {
                marker.Set(RoomLibraryVmf.WaterKeyPrefix + Rooms3x3Kit.SocketName(side), $"{Number(WaterLevel)} {WaterMaterial}");
            }

            if (room.Label is { } label)
            {
                marker.Set("map_label", label);
            }

            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>The kit's materials, then the grass, the water and its underside.</summary>
    public static IReadOnlyList<(string Path, string Vmt)> Materials() =>
    [
        .. Rooms3x3Kit.Materials(),
        ($"materials/{GrassMaterial}.vmt",
            "\"LightmappedGeneric\"\n{\n" + $"\t\"$basetexture\" \"{GrassMaterial}\"\n" + $"\t\"%detailtype\" \"{DetailType}\"\n" + "}\n"),
        ($"materials/{WaterMaterial}.vmt",
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"1\"\n"
            + $"\t\"$bottommaterial\" \"{WaterBeneathMaterial}\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n"),
        ($"materials/{WaterBeneathMaterial}.vmt",
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"0\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n"),
    ];

    /// <summary>
    /// A brush on the floor whose top side is a displacement of the given
    /// power, every vertex raised along +z by <paramref name="height"/> at
    /// its (row, column), the start the top's corner at the box's low x and
    /// y; the other sides the kit's block.
    /// </summary>
    /// <param name="box">The brush, where it stands.</param>
    /// <param name="power">2 or 3: the link carries displacement collision only as the virtual mesh vbsp builds for those.</param>
    /// <param name="height">The distance along +z at each (row, column).</param>
    public static VmfSolid Patch(Bounds box, int power, Func<int, int, float> height)
    {
        ArgumentNullException.ThrowIfNull(height);
        VmfSolid solid = VmfMap.Box(box.Mins, box.Maxs, Rooms3x3Kit.BlockMaterial, topMaterial: Rooms3x3Kit.FloorMaterial);
        int n = (1 << power) + 1;
        VmfChunkNode disp = new() { Name = "dispinfo" };
        disp.KeyValues.Add(new("power", Number(power)));
        disp.KeyValues.Add(new("startposition", $"[{new Point(box.Mins.X, box.Mins.Y, box.Maxs.Z)}]"));
        disp.KeyValues.Add(new("flags", "0"));
        disp.KeyValues.Add(new("elevation", "0"));
        disp.KeyValues.Add(new("subdiv", "0"));
        VmfChunkNode normals = new() { Name = "normals" };
        VmfChunkNode distances = new() { Name = "distances" };
        VmfChunkNode offsets = new() { Name = "offsets" };
        VmfChunkNode offsetNormals = new() { Name = "offset_normals" };
        VmfChunkNode alphas = new() { Name = "alphas" };
        VmfChunkNode tags = new() { Name = "triangle_tags" };
        for (int row = 0; row < n; row++)
        {
            string key = "row" + Number(row);
            int r = row;
            normals.KeyValues.Add(new(key, Repeat("0 0 1", n)));
            distances.KeyValues.Add(new(key, string.Join(' ', Enumerable.Range(0, n).Select(c => Number(height(r, c))))));
            offsets.KeyValues.Add(new(key, Repeat("0 0 0", n)));
            offsetNormals.KeyValues.Add(new(key, Repeat("0 0 1", n)));
            alphas.KeyValues.Add(new(key, Repeat("0", n)));
            if (row < n - 1)
            {
                tags.KeyValues.Add(new(key, Repeat("9", 2 * (n - 1))));
            }
        }

        VmfChunkNode allowed = new() { Name = "allowed_verts" };
        allowed.KeyValues.Add(new("10", Repeat("-1", 10)));
        disp.Children.AddRange([normals, distances, offsets, offsetNormals, alphas, tags, allowed]);
        solid.Sides[0].Displacement = disp;
        return solid;

        static string Repeat(string text, int count) => string.Join(' ', Enumerable.Repeat(text, count));
    }

    private static KitEntity Light(float z) =>
        new("light", new Point(128, 128, z), null, [new("_light", "255 240 220 200")]);

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Level grids for the hand-written sample levels: built from rows, renamed and turned whole.</summary>
public static class RoomsSampleLevels
{
    /// <summary>A level from its rows, north row first as a level file writes them.</summary>
    /// <param name="name">The level's name.</param>
    /// <param name="library">The library, as the level file names it (the first library's path for a level of several).</param>
    /// <param name="rows">The rows, north first, each west to east.</param>
    /// <param name="libraries">The level's libraries, or null for a <c>library:</c> level.</param>
    /// <param name="aliases">The level's aliases.</param>
    public static LevelGrid FromRows(
        string name, string library, IReadOnlyList<IReadOnlyList<LevelCell?>> rows,
        IReadOnlyList<LevelLibrary>? libraries = null, IReadOnlyList<LevelAlias>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int columns = rows[0].Count;
        LevelCell?[] cells = new LevelCell?[rows.Count * columns];
        for (int line = 0; line < rows.Count; line++)
        {
            int y = rows.Count - 1 - line;
            for (int x = 0; x < columns; x++)
            {
                cells[(y * columns) + x] = rows[line][x];
            }
        }

        return new LevelGrid(name, library, rows.Count, columns, cells) { Libraries = libraries, Aliases = aliases ?? [] };
    }

    /// <summary>The same level under another name.</summary>
    public static LevelGrid Renamed(LevelGrid level, string name)
    {
        ArgumentNullException.ThrowIfNull(level);
        return new LevelGrid(name, level.Library, level.Rows, level.Columns, level.Cells)
        {
            Libraries = level.Libraries, Aliases = level.Aliases, Transitions = level.Transitions,
        };
    }

    /// <summary>
    /// The level turned a quarter counter-clockwise seen from above, whole:
    /// cell (x, y) goes to (rows − 1 − y, x) of a grid <c>rows</c> wide, and
    /// every room turns a quarter more, so every joint is kept and every
    /// room stands at its next rotation.
    /// </summary>
    public static LevelGrid Turned(LevelGrid level)
    {
        ArgumentNullException.ThrowIfNull(level);
        int rows = level.Rows, columns = level.Columns;
        LevelCell?[] cells = new LevelCell?[rows * columns];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (level[x, y] is { } cell)
                {
                    cells[(x * rows) + (rows - 1 - y)] = cell with { Rotation = (cell.Rotation + 1) % 4 };
                }
            }
        }

        return new LevelGrid(level.Name, level.Library, columns, rows, cells) { Libraries = level.Libraries, Aliases = level.Aliases };
    }
}
