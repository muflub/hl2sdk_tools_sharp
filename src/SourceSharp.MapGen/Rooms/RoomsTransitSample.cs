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
/// The transit sample (the rooms design, 15.7): the 3x3 kit's rooms, each
/// with a spawn point, plus an up room left by a button and a down room
/// left down a hallway, and a three-level run of them, as files under
/// <c>samples/rooms-transit/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the 3x3 sample so that library stays without roles, and
/// its levels and digests unchanged.
/// </para>
/// <list type="bullet">
/// <item><c>lift_up</c> (<c>room_role up</c>): four doors; a
/// <c>func_button</c> on a pedestal whose <c>OnPressed</c> fires
/// <c>Transition</c> at <c>cxry_transition</c>; a small transition volume
/// in the middle of the floor; an arrival point facing east and two spawn
/// points beside it. The stock fallback makes the volume a touch-disabled
/// <c>trigger_changelevel</c> the button fires.</item>
/// <item><c>lift_down</c> (<c>room_role down</c>): four doors; a
/// <c>trigger_once</c> hallway whose only output is the transition, around
/// the transition volume, which the stock fallback folds into the
/// changelevel; an arrival point facing north.</item>
/// <item>The kit's five kinds, each with one spawn point, so a top level
/// (<c>up: none</c>) always has a room to start in; <c>end</c> keeps its
/// <c>info_player_start</c>, which a level with transitions strips.</item>
/// </list>
/// <para>
/// The levels are exactly what <c>ssmap layout rooms.vmf -rows 3 -columns 3
/// -seed 1 -transition-distance 2 -sequence 3 -name transit -out levels</c>
/// writes; a fact runs the verb and compares.
/// </para>
/// </remarks>
public static class RoomsTransitSample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-transit";

    /// <summary>The first level's seed.</summary>
    public const ulong Seed = 1;

    /// <summary>How many levels the run has.</summary>
    public const int Levels = 3;

    /// <summary>The fewest doors between a level's up and down rooms.</summary>
    public const int Distance = 2;

    /// <summary>The levels' base name.</summary>
    public const string BaseName = "transit";

    /// <summary>The up room's name.</summary>
    public const string UpRoom = "lift_up";

    /// <summary>The down room's name.</summary>
    public const string DownRoom = "lift_down";

    /// <summary>The trigger material: <c>%compileTrigger</c>, for the brush entities a player walks into.</summary>
    public const string TriggerMaterial = Rooms3x3Kit.MaterialFolder + "/trigger";

    /// <summary>The game name the sample's <c>gameinfo.txt</c> gives.</summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms transit sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    /// <summary>The transition volume, room-local: a small box on the floor in the middle of the room.</summary>
    public static Bounds Volume { get; } = new(new Point(112, 112, 16), new Point(144, 144, 80));

    /// <summary>The down room's hallway trigger, room-local: around the volume, taller.</summary>
    public static Bounds Hallway { get; } = new(new Point(96, 96, 16), new Point(160, 160, 128));

    /// <summary>The up room's button, room-local: a pedestal in the south-east corner.</summary>
    public static Bounds Button { get; } = new(new Point(200, 40, 16), new Point(216, 56, 64));

    /// <summary>Every generated file: its path under <see cref="Folder"/> and its bytes, in ordinal path order.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(GameInfo),
            [Rooms3x3Kit.LibraryFile] = Encoding.UTF8.GetBytes(LibraryVmf()),
        };

        foreach ((string path, string vmt) in Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        LevelGeneratorOptions options = new(Rooms3x3Arrangement.Size, Rooms3x3Arrangement.Size, Seed);
        IReadOnlyList<LevelGrid> levels = LevelGenerator.GenerateSequence(
            [.. Kinds.Select(k => k.Definition)], options, Levels, BaseName, Rooms3x3Sample.LibraryFromLevels, null,
            [.. Kinds.Select(k => k.Role)], Distance);
        for (int i = 0; i < levels.Count; i++)
        {
            LevelGeneratorOptions own = options with { Seed = Seed + (ulong)i };
            files[$"levels/{levels[i].Name}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(levels[i], LevelGenerator.Header(own, levels[i])));
        }

        return files;
    }

    /// <summary>A brush entity of one box, room-local.</summary>
    /// <param name="ClassName">Its class.</param>
    /// <param name="Keys">Its keys, in order.</param>
    /// <param name="Wires">Its outputs: output name, target, input.</param>
    /// <param name="Box">Its one brush.</param>
    /// <param name="Material">The brush's material.</param>
    public sealed record KitBrushEntity(
        string ClassName,
        IReadOnlyList<KeyValuePair<string, string>> Keys,
        IReadOnlyList<(string Output, string Target, string Input)> Wires,
        Bounds Box,
        string Material);

    /// <summary>One room of the transit library: its kind, role and the brush entities it adds.</summary>
    /// <param name="Kind">Its kind: sockets, world features and point entities.</param>
    /// <param name="Role">Its role.</param>
    /// <param name="BrushEntities">Its brush entities, room-local.</param>
    public sealed record TransitRoom(RoomKind Kind, RoomRole Role, IReadOnlyList<KitBrushEntity> BrushEntities)
    {
        /// <summary>The room as the room pipeline defines it.</summary>
        public RoomDefinition Definition => Rooms3x3Kit.Definition(Kind);
    }

    /// <summary>The transition volume's authored name.</summary>
    public const string VolumeName = "cxry_transition";

    /// <summary>The library's rooms, in library order: the kit's five kinds, then the up and down rooms.</summary>
    public static IReadOnlyList<TransitRoom> Kinds
    {
        get
        {
            List<TransitRoom> rooms = [.. Rooms3x3Kit.Kinds.Select(k => new TransitRoom(
                k with { Entities = [.. k.Entities, Poi("spawn", new Point(176, 176, 16), 180)] }, RoomRole.None, []))];

            KitSide[] all = [KitSide.East, KitSide.West, KitSide.North, KitSide.South];
            KitBrushEntity volume = new(
                "trigger_room_transition", [new("targetname", VolumeName)], [], Volume, TriggerMaterial);
            KitBrushEntity button = new(
                "func_button", [new("spawnflags", "1025"), new("wait", "-1")], [("OnPressed", VolumeName, "Transition")],
                Button, Rooms3x3Kit.BlockMaterial);
            rooms.Add(new TransitRoom(
                new RoomKind(UpRoom, all, [],
                [
                    Light(),
                    Poi("arrival", new Point(64, 192, 16), 0),
                    Poi("spawn", new Point(64, 128, 16), 0),
                    Poi("spawn", new Point(192, 192, 16), 270),
                ]),
                RoomRole.Up,
                [button, volume]));

            KitBrushEntity hallway = new(
                "trigger_once", [new("spawnflags", "1")], [("OnStartTouch", VolumeName, "Transition")], Hallway, TriggerMaterial);
            rooms.Add(new TransitRoom(
                new RoomKind(DownRoom, all, [], [Light(), Poi("arrival", new Point(64, 64, 16), 90)]),
                RoomRole.Down,
                [hallway, volume]));
            return rooms;
        }
    }

    /// <summary>The library VMF: every room in a line, 128 units apart, each marked by an <c>info_room</c>.</summary>
    public static string LibraryVmf()
    {
        VmfMap map = new();
        IReadOnlyList<TransitRoom> rooms = Kinds;
        for (int i = 0; i < rooms.Count; i++)
        {
            TransitRoom room = rooms[i];
            Point corner = Rooms3x3Kit.LibraryCorner(i);
            Rooms3x3Kit.Place(map, room.Kind, Rooms3x3Placement.Identity, open: null, offset: corner);
            foreach (KitBrushEntity entity in room.BrushEntities)
            {
                VmfEntity placed = new() { ClassName = entity.ClassName };
                foreach (KeyValuePair<string, string> key in entity.Keys)
                {
                    placed.Set(key.Key, key.Value);
                }

                foreach ((string output, string target, string input) in entity.Wires)
                {
                    placed.Wire(output, target, input);
                }

                placed.Solids.Add(VmfMap.Box(entity.Box.Mins + corner, entity.Box.Maxs + corner, entity.Material));
                map.Entities.Add(placed);
            }

            VmfEntity marker = new() { ClassName = "info_room" };
            marker.Set("origin", corner.ToString());
            marker.Set("name", room.Kind.Name);
            marker.Set("cell_size", Number(Rooms3x3Kit.CellSize));
            marker.Set("door_width", Number(Rooms3x3Kit.DoorWidth));
            marker.Set("door_height", Number(Rooms3x3Kit.DoorHeight));
            marker.Set("wall_depth", Number(Rooms3x3Kit.Wall));
            if (room.Role != RoomRole.None)
            {
                marker.Set(RoomPois.RoleKey, room.Role == RoomRole.Up ? "up" : "down");
            }

            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>The kit's materials and the trigger material.</summary>
    public static IReadOnlyList<(string Path, string Vmt)> Materials() =>
    [
        .. Rooms3x3Kit.Materials(),
        ($"materials/{TriggerMaterial}.vmt",
            "\"LightmappedGeneric\"\n{\n" + $"\t\"$basetexture\" \"{TriggerMaterial}\"\n" + "\t\"%compileTrigger\" \"1\"\n}\n"),
    ];

    private static KitEntity Poi(string type, Point origin, int yaw) =>
        new("info_poi", origin, yaw, [new("poi_type", type)]);

    private static KitEntity Light() =>
        new("light", new Point(128, 128, 208), null, [new("_light", "255 240 220 200")]);

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
