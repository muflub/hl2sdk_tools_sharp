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

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>The worldspawn keys that describe one room's extent rather than the map.</summary>
    private const string WorldMinsKey = "world_mins";

    /// <summary>The worldspawn's upper extent key.</summary>
    private const string WorldMaxsKey = "world_maxs";

    /// <summary>
    /// The linked entity lump: one worldspawn, then every room's other
    /// entities in layout order, each moved to its room's placement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every room's lump is a whole entity list ending in NUL, with its own
    /// worldspawn first; the engine reads entity text up to the first NUL, so
    /// lumps appended together are read as room 0's alone. Parsing each one
    /// and writing the list once is the fix.
    /// </para>
    /// <para>
    /// The first room's worldspawn is the map's. Its <c>world_mins</c> and
    /// <c>world_maxs</c> are replaced by the union of every room's extents as
    /// placed; every other key must agree across rooms (sky, detail
    /// settings, and so on are per map, and quietly keeping one room's would
    /// be a silent choice), and <c>hammerid</c> is per room and not compared.
    /// </para>
    /// <para>
    /// A moved entity has its <c>origin</c> put through the placement and its
    /// yaw (<c>angles</c>' second value, or <c>angle</c> unless it is the -1
    /// "up" or -2 "down" code) turned by the placement's quarter turns. Those
    /// are the keys every point entity reads its placement from; a key that
    /// holds a world position under another name is carried as written.
    /// </para>
    /// </remarks>
    internal static BspLumpData MergeEntities(RoomPlan[] plans)
    {
        List<BspEntity> merged = [];
        BspEntity? world = null;
        string? worldOwner = null;
        Box? extent = null;
        foreach (RoomPlan plan in plans)
        {
            string name = plan.Placement.Room.Definition.Name;
            foreach (BspEntity entity in EntityLump.Parse(plan.Bsp[BspLump.Entities]))
            {
                if (!string.Equals(entity.ClassName, "worldspawn", StringComparison.Ordinal))
                {
                    merged.Add(MoveEntity(entity, plan.Transform, name));
                    continue;
                }

                if (world is null)
                {
                    world = entity;
                    worldOwner = name;
                }
                else
                {
                    RequireSameWorld(world, worldOwner!, entity, name);
                }

                if (entity.Get(WorldMinsKey) is { } mins && entity.Get(WorldMaxsKey) is { } maxs)
                {
                    Box moved = MoveBox(plan.Transform, ParseVec(mins, WorldMinsKey, name), ParseVec(maxs, WorldMaxsKey, name));
                    extent = extent is { } sofar ? Union(sofar, moved) : moved;
                }
            }
        }

        List<BspEntity> lump = [];
        if (world is not null)
        {
            BspEntity linkedWorld = new();
            foreach (BspKeyValue pair in world.Pairs)
            {
                string value = pair.Value;
                if (extent is { } box && IsKey(pair.Key, WorldMinsKey))
                {
                    value = FormatVec(box.Mins);
                }
                else if (extent is { } box2 && IsKey(pair.Key, WorldMaxsKey))
                {
                    value = FormatVec(box2.Maxs);
                }

                linkedWorld.Pairs.Add(new BspKeyValue(pair.Key, value));
            }

            lump.Add(linkedWorld);
        }

        lump.AddRange(merged);
        return EntityLump.Write(lump);
    }

    /// <summary>
    /// The six keys vbsp writes on an <c>info_ladder</c> (what a
    /// <c>func_ladder</c> becomes once its brushes join the world): the
    /// ladder's bounds, one component per key, in mins-then-maxs order.
    /// </summary>
    /// <remarks>A property, not a static array: an array's elements are writable, and the libraries hold no mutable statics.</remarks>
    private static string[] LadderKeys => ["mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z"];

    /// <summary>One entity with its placement keys moved.</summary>
    /// <remarks>
    /// Besides <c>origin</c> and the yaw, an <c>info_ladder</c>'s bounds are
    /// a world-space box written as six separate keys (<see cref="LadderKeys"/>).
    /// The room compile wrote them room-local; a whole-map compile of the
    /// same level measures them from the moved brushes. So when all six are
    /// present they are read as one box, put through the placement the way
    /// an occluder's box is (<see cref="MoveBox"/>: every corner moved, then
    /// the least and greatest taken again, because a quarter turn swaps
    /// which corner is the least), and written back with two decimals, the
    /// format vbsp writes them in. An entity with only some of the six keys
    /// is not a ladder vbsp made, and its keys are carried as written.
    /// </remarks>
    internal static BspEntity MoveEntity(BspEntity entity, RoomTransform transform, string room)
    {
        int turns = transform.Placement.NormalizedRotation;
        string[] ladderKeys = LadderKeys;
        string[]? ladder = MoveLadderBounds(entity, ladderKeys, transform, room);
        BspEntity moved = new();
        foreach (BspKeyValue pair in entity.Pairs)
        {
            string value = pair.Value;
            int ladderKey = ladder is null ? -1 : Array.FindIndex(ladderKeys, k => IsKey(pair.Key, k));
            if (ladderKey >= 0)
            {
                value = ladder![ladderKey];
            }
            else if (IsKey(pair.Key, "origin"))
            {
                value = FormatVec(transform.Apply(ParseVec(value, "origin", room)));
            }
            else if (turns != 0 && IsKey(pair.Key, "angles"))
            {
                Vec3 angles = ParseVec(value, "angles", room);
                value = FormatVec(new Vec3(angles.X, TurnYaw(angles.Y, turns), angles.Z));
            }
            else if (turns != 0 && IsKey(pair.Key, "angle"))
            {
                float yaw = ParseFloat(value, "angle", room);
                value = yaw is -1f or -2f ? value : Format(TurnYaw(yaw, turns));
            }

            moved.Pairs.Add(new BspKeyValue(pair.Key, value));
        }

        return moved;
    }

    /// <summary>
    /// An <c>info_ladder</c>'s six bound keys moved by the placement, in
    /// <paramref name="keys"/> order, or null when the entity does not carry
    /// all six.
    /// </summary>
    /// <remarks>
    /// The values are read back from the two-decimal text the room compile
    /// wrote, so a bound off the 0.01 grid is moved from its rounded value;
    /// the whole-map compile rounds after moving. A quarter turn and a
    /// whole-cell translation are exact, so on the grid (every bound a room
    /// kit produces) the two agree to the digit.
    /// </remarks>
    private static string[]? MoveLadderBounds(BspEntity entity, string[] keys, RoomTransform transform, string room)
    {
        float[] bounds = new float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            if (entity.Get(keys[i]) is not { } text)
            {
                return null;
            }

            bounds[i] = ParseFloat(text, keys[i], room);
        }

        Box box = MoveBox(transform, new Vec3(bounds[0], bounds[1], bounds[2]), new Vec3(bounds[3], bounds[4], bounds[5]));
        return [F2(box.Mins.X), F2(box.Mins.Y), F2(box.Mins.Z), F2(box.Maxs.X), F2(box.Maxs.Y), F2(box.Maxs.Z)];

        static string F2(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static void RequireSameWorld(BspEntity world, string owner, BspEntity other, string name)
    {
        static IEnumerable<BspKeyValue> Compared(BspEntity e) =>
            e.Pairs.Where(p => !IsKey(p.Key, WorldMinsKey) && !IsKey(p.Key, WorldMaxsKey) && !IsKey(p.Key, "hammerid"));

        List<BspKeyValue> a = [.. Compared(world)];
        List<BspKeyValue> b = [.. Compared(other)];
        if (!a.SequenceEqual(b))
        {
            throw new LinkException(
                $"room {name}'s worldspawn keys disagree with room {owner}'s; the linked map has one worldspawn,"
                + " so every room must be compiled with the same map-wide settings");
        }
    }

    /// <summary>A yaw turned by quarter turns, kept in [0, 360).</summary>
    private static float TurnYaw(float yaw, int turns)
    {
        float turned = (yaw + (90f * turns)) % 360f;
        return turned < 0 ? turned + 360f : turned;
    }

    private static bool IsKey(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static Vec3 ParseVec(string text, string key, string room)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new LinkException($"room {room} has an entity whose \"{key}\" is \"{text}\", not three numbers");
        }

        return new Vec3(ParseFloat(parts[0], key, room), ParseFloat(parts[1], key, room), ParseFloat(parts[2], key, room));
    }

    private static float ParseFloat(string text, string key, string room) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new LinkException($"room {room} has an entity whose \"{key}\" holds \"{text}\", not a number");

    private static string FormatVec(Vec3 v) => $"{Format(v.X)} {Format(v.Y)} {Format(v.Z)}";

    private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
}
