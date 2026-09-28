//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapGen.Catalog;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// A cell face of a room, room-local.
/// </summary>
/// <remarks>
/// The numbering is the room pipeline's own facing numbers
/// (<c>PositiveX</c> 0, <c>NegativeX</c> 1, <c>PositiveY</c> 2,
/// <c>NegativeY</c> 3), so <see cref="Rooms3x3Kit.Definition"/> converts by
/// value, and a fact splits the generated library with the pipeline's own
/// reader to hold the two together.
/// </remarks>
public enum KitSide
{
    /// <summary>+x: the cell face at x = cell size.</summary>
    East = 0,

    /// <summary>-x: the cell face at x = 0.</summary>
    West = 1,

    /// <summary>+y: the cell face at y = cell size.</summary>
    North = 2,

    /// <summary>-y: the cell face at y = 0.</summary>
    South = 3,
}

/// <summary>What a kit brush is to the compiler.</summary>
public enum KitBrushRole
{
    /// <summary>A structural world brush.</summary>
    World,

    /// <summary>A brush of a <c>func_detail</c>: solid, but no part of the vis tree's portals.</summary>
    Detail,
}

/// <summary>One box brush of a room, room-local.</summary>
/// <param name="Box">The box.</param>
/// <param name="Material">Every face's material.</param>
/// <param name="Role">World or detail.</param>
public sealed record KitBrush(Bounds Box, string Material, KitBrushRole Role = KitBrushRole.World);

/// <summary>One point entity of a room, room-local.</summary>
/// <param name="ClassName">Its classname.</param>
/// <param name="Origin">Where it is, room-local.</param>
/// <param name="Yaw">Its yaw in degrees, or null for an entity with no angles.</param>
/// <param name="Keys">Further keys, in pairs, written as given.</param>
public sealed record KitEntity(string ClassName, Point Origin, int? Yaw, IReadOnlyList<KeyValuePair<string, string>> Keys);

/// <summary>
/// One kind of room in the kit: which faces are doors, and what stands inside.
/// </summary>
/// <param name="Name">The room's library name; also the VMF's base name.</param>
/// <param name="Sockets">The faces with a door socket, room-local, in socket order.</param>
/// <param name="Features">The brushes inside the shell that make this kind different from the others.</param>
/// <param name="Entities">The point entities, room-local.</param>
public sealed record RoomKind(
    string Name,
    IReadOnlyList<KitSide> Sockets,
    IReadOnlyList<KitBrush> Features,
    IReadOnlyList<KitEntity> Entities);

/// <summary>
/// The room kit of the 3x3 rooms sample: five kinds of room on one 256-unit
/// grid, each a sealed box with door sockets, and the materials and game
/// folder they compile against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why five kinds, and why these.</b> A rearrangement is only a test if the
/// rooms are not interchangeable. The kinds differ in their socket sets (four,
/// three, two adjacent, two opposite, one), so which cells can hold which
/// room depends on its neighbours; and each carries an interior feature off
/// the room's centre, so a room turned a quarter is a different map even when
/// its socket set looks the same (the four-socket cross). The features also
/// put every contents kind the room feature carries into the level: solid
/// structure, a <c>func_detail</c> brush, a player clip and a grate.
/// </para>
/// <para>
/// <b>The geometry.</b> A 256 cell, a 16-unit shell, and a door the full
/// height of the interior (224 from the floor at 16 to the ceiling at 240),
/// 96 wide. The kit's opening is always centred on the cell face, so only a
/// full-height door stands on the floor. Every coordinate is a multiple of 16,
/// which the verification's sampling lattice (every 8 units, offset by 4)
/// relies on: no sample ever lies on a brush plane, and no solid is thinner
/// than two samples.
/// </para>
/// <para>
/// The shell's slabs meet without overlapping, as <see cref="RoomKit.Shell"/>'s
/// do: floor and ceiling take the whole footprint, the east and west walls the
/// whole depth between them, the north and south walls what is left. A socket
/// replaces its wall with two jambs, which leave exactly the kit's opening;
/// the plug brush fills the opening in the room's own compile. The plug is
/// what the linker strips at a jointed socket, and what the flattened
/// reference simply leaves out there.
/// </para>
/// </remarks>
public static class Rooms3x3Kit
{
    /// <summary>The grid's cell edge.</summary>
    public const float CellSize = 256f;

    /// <summary>The shell's thickness, which is also the socket kit's depth.</summary>
    public const float Wall = 16f;

    /// <summary>The door opening's width, along the face.</summary>
    public const float DoorWidth = 96f;

    /// <summary>The door opening's height: floor to ceiling, so the door stands on the floor.</summary>
    public const float DoorHeight = CellSize - (2 * Wall);

    /// <summary>The low edge of the door opening, along its face.</summary>
    public const float DoorLow = (CellSize - DoorWidth) / 2f;

    /// <summary>The high edge of the door opening, along its face.</summary>
    public const float DoorHigh = DoorLow + DoorWidth;

    /// <summary>The sample's material folder, under <c>materials/</c>.</summary>
    public const string MaterialFolder = "rooms3x3";

    /// <summary>The shell's walls.</summary>
    public const string WallMaterial = MaterialFolder + "/wall";

    /// <summary>The shell's floor.</summary>
    public const string FloorMaterial = MaterialFolder + "/floor";

    /// <summary>The shell's ceiling.</summary>
    public const string CeilingMaterial = MaterialFolder + "/ceiling";

    /// <summary>Pillars, steps and crates.</summary>
    public const string BlockMaterial = MaterialFolder + "/block";

    /// <summary>
    /// The door plug: <c>%compileTrigger</c> and nothing else, so the brush stays
    /// <c>CONTENTS_SOLID</c> (it seals the room's compile) while its surfaces
    /// carry the trigger flag the linker finds it by.
    /// </summary>
    /// <remarks>
    /// Not the game's <c>tools/toolstrigger</c>: that material is also
    /// <c>%compileNonsolid</c>, and a non-solid plug would leak the room.
    /// </remarks>
    public const string PlugMaterial = MaterialFolder + "/doorplug";

    /// <summary>Player clip: <c>CONTENTS_PLAYERCLIP</c>, drawn as nothing.</summary>
    public const string PlayerClipMaterial = MaterialFolder + "/playerclip";

    /// <summary>A grate: <c>%compilePassBullets</c>, so <c>CONTENTS_GRATE</c> and not solid.</summary>
    public const string GrateMaterial = MaterialFolder + "/grate";

    /// <summary>The name of a room-local face as a socket.</summary>
    /// <param name="side">The face.</param>
    public static string SocketName(KitSide side) => side switch
    {
        KitSide.East => "east",
        KitSide.West => "west",
        KitSide.North => "north",
        KitSide.South => "south",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "a room has four faces"),
    };

    /// <summary>
    /// A face turned by quarter turns counter-clockwise seen from above, the
    /// level format's rotation: east goes to north, north to west.
    /// </summary>
    /// <param name="side">The room-local face.</param>
    /// <param name="turns">Quarter turns, 0 to 3.</param>
    public static KitSide Turn(KitSide side, int turns)
    {
        KitSide turned = side;
        for (int i = 0; i < ((turns % 4) + 4) % 4; i++)
        {
            turned = turned switch
            {
                KitSide.East => KitSide.North,
                KitSide.North => KitSide.West,
                KitSide.West => KitSide.South,
                _ => KitSide.East,
            };
        }

        return turned;
    }

    /// <summary>The face on the other side of a shared wall.</summary>
    /// <param name="side">One side.</param>
    public static KitSide Opposite(KitSide side) => Turn(side, 2);

    /// <summary>Which neighbouring cell a face looks into.</summary>
    /// <param name="side">A world-space face.</param>
    public static (int Dx, int Dy) Step(KitSide side) => side switch
    {
        KitSide.East => (1, 0),
        KitSide.West => (-1, 0),
        KitSide.North => (0, 1),
        _ => (0, -1),
    };

    /// <summary>
    /// The five kinds, in the order the enumeration tries them.
    /// </summary>
    /// <remarks>
    /// Every feature keeps clear of the room's centre column and of the strip
    /// in front of every door, so any two rooms whose doors are jointed are
    /// connected through open floor, and the centre of every room at eye height
    /// is open space the verification can trace from.
    /// </remarks>
    public static IReadOnlyList<RoomKind> Kinds { get; } =
    [
        new RoomKind(
            "cross",
            [KitSide.East, KitSide.West, KitSide.North, KitSide.South],
            [new KitBrush(new Bounds(new(32, 192, 16), new(64, 224, 240)), BlockMaterial)],
            [Light()]),
        new RoomKind(
            "tee",
            [KitSide.East, KitSide.West, KitSide.North],
            [new KitBrush(new Bounds(new(32, 32, 16), new(96, 64, 128)), PlayerClipMaterial)],
            [Light()]),
        new RoomKind(
            "corner",
            [KitSide.East, KitSide.North],
            [new KitBrush(new Bounds(new(32, 32, 16), new(64, 64, 240)), GrateMaterial)],
            [Light()]),
        new RoomKind(
            "hall",
            [KitSide.East, KitSide.West],
            [new KitBrush(new Bounds(new(160, 32, 16), new(208, 64, 64)), BlockMaterial, KitBrushRole.Detail)],
            [Light()]),
        new RoomKind(
            "end",
            [KitSide.East],
            [new KitBrush(new Bounds(new(32, 160, 16), new(96, 224, 32)), BlockMaterial)],
            [Light(), new KitEntity("info_player_start", new Point(64, 96, 17), 0, [])]),
    ];

    /// <summary>A kind by name.</summary>
    /// <param name="name">The kind's name.</param>
    public static RoomKind Kind(string name) =>
        Kinds.FirstOrDefault(k => k.Name == name)
        ?? throw new ArgumentException($"the kit has no room kind '{name}'", nameof(name));

    private static KitEntity Light() =>
        new("light", new Point(128, 128, 208), null, [new("_light", "255 240 220 200")]);

    /// <summary>
    /// The room's brushes, room-local: floor, ceiling, the four walls (jambs
    /// where a socket is), the plugs of every socket not in
    /// <paramref name="open"/>, then the kind's features.
    /// </summary>
    /// <param name="kind">The room.</param>
    /// <param name="open">Room-local faces whose plug is left out: the jointed ones, in a merged level.</param>
    public static IReadOnlyList<KitBrush> Brushes(RoomKind kind, IReadOnlySet<KitSide>? open = null)
    {
        ArgumentNullException.ThrowIfNull(kind);

        const float c = CellSize;
        const float t = Wall;
        List<KitBrush> brushes =
        [
            new(new Bounds(new(0, 0, 0), new(c, c, t)), FloorMaterial),
            new(new Bounds(new(0, 0, c - t), new(c, c, c)), CeilingMaterial),
        ];

        foreach (KitSide side in new[] { KitSide.East, KitSide.West, KitSide.North, KitSide.South })
        {
            // East and west span the whole depth; north and south fit between them.
            (float x0, float x1, float y0, float y1) = side switch
            {
                KitSide.East => (c - t, c, 0f, c),
                KitSide.West => (0f, t, 0f, c),
                KitSide.North => (t, c - t, c - t, c),
                _ => (t, c - t, 0f, t),
            };
            bool alongY = side is KitSide.East or KitSide.West;

            if (!kind.Sockets.Contains(side))
            {
                brushes.Add(new(new Bounds(new(x0, y0, t), new(x1, y1, c - t)), WallMaterial));
                continue;
            }

            // Two jambs either side of the opening; the door is the full
            // interior height, so there is no lintel or sill to add.
            brushes.Add(alongY
                ? new(new Bounds(new(x0, y0, t), new(x1, DoorLow, c - t)), WallMaterial)
                : new(new Bounds(new(x0, y0, t), new(DoorLow, y1, c - t)), WallMaterial));
            brushes.Add(alongY
                ? new(new Bounds(new(x0, DoorHigh, t), new(x1, y1, c - t)), WallMaterial)
                : new(new Bounds(new(DoorHigh, y0, t), new(x1, y1, c - t)), WallMaterial));
        }

        foreach (KitSide side in kind.Sockets)
        {
            if (open is null || !open.Contains(side))
            {
                brushes.Add(new(PlugBox(side), PlugMaterial));
            }
        }

        brushes.AddRange(kind.Features);
        return brushes;
    }

    /// <summary>
    /// A socket's plug box, room-local: the kit opening, reaching the wall's
    /// thickness inward from the cell face, so two jointed rooms' plugs meet
    /// face to face on the shared cell face.
    /// </summary>
    /// <param name="side">The socket's face.</param>
    public static Bounds PlugBox(KitSide side)
    {
        const float c = CellSize;
        const float t = Wall;
        return side switch
        {
            KitSide.East => new Bounds(new(c - t, DoorLow, t), new(c, DoorHigh, c - t)),
            KitSide.West => new Bounds(new(0, DoorLow, t), new(t, DoorHigh, c - t)),
            KitSide.North => new Bounds(new(DoorLow, c - t, t), new(DoorHigh, c, c - t)),
            _ => new Bounds(new(DoorLow, 0, t), new(DoorHigh, t, c - t)),
        };
    }

    /// <summary>The library's file name in the sample folder.</summary>
    public const string LibraryFile = "rooms.vmf";

    /// <summary>The gap between two rooms' cells in the library: half a cell of nothing.</summary>
    public const float LibraryGap = 128f;

    /// <summary>Where a kind's cell starts in the library: the kinds stand in a line along +x.</summary>
    /// <param name="index">The kind's position in <see cref="Kinds"/>.</param>
    public static Point LibraryCorner(int index) => new(index * (CellSize + LibraryGap), 0, 0);

    /// <summary>
    /// The room library: every kind in its own cell, every socket plugged,
    /// the cells in a line along +x with <see cref="LibraryGap"/> between
    /// them, and an <c>info_room</c> at each cell's low corner naming the
    /// room and stating the grid and the kit.
    /// </summary>
    public static string LibraryVmf()
    {
        VmfMap map = new();
        for (int i = 0; i < Kinds.Count; i++)
        {
            RoomKind kind = Kinds[i];
            Point corner = LibraryCorner(i);
            Place(map, kind, Rooms3x3Placement.Identity, open: null, offset: corner);
            VmfEntity marker = new() { ClassName = "info_room" };
            marker.Set("origin", corner.ToString());
            marker.Set("name", kind.Name);
            marker.Set("cell_size", Number(CellSize));
            marker.Set("door_width", Number(DoorWidth));
            marker.Set("door_height", Number(DoorHeight));
            marker.Set("wall_depth", Number(Wall));
            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>
    /// What the room pipeline reads a kind as: its name, the grid, the kit,
    /// and a socket per door named for its wall, in wall order.
    /// </summary>
    /// <param name="kind">The room.</param>
    public static SourceSharp.MapTools.Rooms.RoomDefinition Definition(RoomKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return new SourceSharp.MapTools.Rooms.RoomDefinition(
            kind.Name,
            CellSize,
            new SourceSharp.MapTools.Rooms.SocketKit(DoorWidth, DoorHeight, Wall),
            [.. kind.Sockets.Order().Select(side => new SourceSharp.MapTools.Rooms.RoomSocket(
                (SourceSharp.MapTools.Rooms.RoomFacing)(int)side, SocketName(side)))]);
    }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds one room to a map at a placement: its world brushes, its detail
    /// brushes as one <c>func_detail</c>, and its point entities.
    /// </summary>
    /// <param name="map">Where it goes.</param>
    /// <param name="kind">The room.</param>
    /// <param name="placement">Where it stands and how it is turned.</param>
    /// <param name="open">Room-local faces whose plug is left out.</param>
    /// <param name="offset">Added after the placement: where the library puts the room's cell.</param>
    internal static void Place(VmfMap map, RoomKind kind, Rooms3x3Placement placement, IReadOnlySet<KitSide>? open, Point offset = default)
    {
        List<VmfSolid> detail = [];
        foreach (KitBrush brush in Brushes(kind, open))
        {
            Bounds placed = placement.Apply(brush.Box);
            Bounds world = new(placed.Mins + offset, placed.Maxs + offset);
            VmfSolid solid = VmfMap.Box(world.Mins, world.Maxs, brush.Material);
            if (brush.Role == KitBrushRole.Detail)
            {
                detail.Add(solid);
            }
            else
            {
                map.WorldSolids.Add(solid);
            }
        }

        if (detail.Count > 0)
        {
            VmfEntity func = new() { ClassName = "func_detail" };
            func.Solids.AddRange(detail);
            map.Entities.Add(func);
        }

        foreach (KitEntity entity in kind.Entities)
        {
            VmfEntity placed = new() { ClassName = entity.ClassName };
            placed.Set("origin", (placement.Apply(entity.Origin) + offset).ToString());
            if (entity.Yaw is int yaw)
            {
                placed.Set("angles", string.Create(
                    CultureInfo.InvariantCulture, $"0 {(yaw + (90 * placement.Rotation)) % 360} 0"));
            }

            foreach (KeyValuePair<string, string> key in entity.Keys)
            {
                placed.Set(key.Key, key.Value);
            }

            map.Entities.Add(placed);
        }
    }

    /// <summary>
    /// The sample's materials: path under the game folder, and the VMT text.
    /// </summary>
    /// <remarks>
    /// No textures ship with the sample: the compile reads a material's
    /// <c>%compile*</c> keys and its shader, and a missing base texture is a
    /// warning. What matters is the flags, which are the point of each one.
    /// </remarks>
    public static IReadOnlyList<(string Path, string Vmt)> Materials()
    {
        static string Lit(string name, string extra = "") =>
            "\"LightmappedGeneric\"\n{\n"
            + $"\t\"$basetexture\" \"{name}\"\n"
            + extra
            + "}\n";

        return
        [
            ($"materials/{BlockMaterial}.vmt", Lit(BlockMaterial)),
            ($"materials/{CeilingMaterial}.vmt", Lit(CeilingMaterial)),
            ($"materials/{PlugMaterial}.vmt", Lit(PlugMaterial, "\t\"%compileTrigger\" \"1\"\n")),
            ($"materials/{FloorMaterial}.vmt", Lit(FloorMaterial)),
            ($"materials/{GrateMaterial}.vmt", Lit(GrateMaterial, "\t\"%compilePassBullets\" \"1\"\n")),
            ($"materials/{PlayerClipMaterial}.vmt", Lit(PlayerClipMaterial, "\t\"%playerClip\" \"1\"\n")),
            ($"materials/{WallMaterial}.vmt", Lit(WallMaterial)),
        ];
    }

    /// <summary>
    /// The sample folder's <c>gameinfo.txt</c>: a game that mounts only
    /// itself, so <c>ssmap room rooms.vmf -game .</c> finds the materials
    /// beside it with no Steam install.
    /// </summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms 3x3 sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";
}
