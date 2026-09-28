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

            // Parsed and turned at room compile time (or now, for a room
            // without stored link data); a key that could not be read is
            // reported here, where the walk reaches its entity.
            foreach (RoomLinkEntity item in EntitiesFor(plan.Placement.Room, plan.Transform.Placement.NormalizedRotation).Items)
            {
                if (!item.IsWorld)
                {
                    merged.Add(item.Error is null ? TranslateEntity(item, plan.Transform) : throw new LinkException(item.Error));
                    continue;
                }

                BspEntity entity = new();
                foreach (RoomLinkPair pair in item.Pairs)
                {
                    entity.Pairs.Add(new BspKeyValue(pair.Key, pair.Value!));
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

                if (item.Error is { } error)
                {
                    throw new LinkException(error);
                }

                if (item.Extent is { } turned)
                {
                    Box moved = plan.Transform.TranslateBox(turned);
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

    /// <summary>One entity with its placement keys moved.</summary>
    /// <remarks>
    /// The turn (<c>TurnPair</c>, which the room compile stores) and then
    /// the cell (<see cref="TranslateEntity"/>, the link's share).
    /// </remarks>
    internal static BspEntity MoveEntity(BspEntity entity, RoomTransform transform, string room)
    {
        int turns = transform.Placement.NormalizedRotation;
        List<RoomLinkPair> pairs = new(entity.Pairs.Count);
        foreach (BspKeyValue pair in entity.Pairs)
        {
            pairs.Add(TurnPair(pair, turns, room));
        }

        return TranslateEntity(new RoomLinkEntity(false, pairs, null, null), transform);
    }

    /// <summary>
    /// A turned entity moved to the placement's cell: its origin through
    /// <see cref="RoomTransform.Translate"/>, every other key as the turn left it.
    /// </summary>
    private static BspEntity TranslateEntity(RoomLinkEntity entity, RoomTransform transform)
    {
        BspEntity moved = new();
        foreach (RoomLinkPair pair in entity.Pairs)
        {
            moved.Pairs.Add(new BspKeyValue(pair.Key, pair.Value ?? FormatVec(transform.Translate(pair.Origin))));
        }

        return moved;
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
