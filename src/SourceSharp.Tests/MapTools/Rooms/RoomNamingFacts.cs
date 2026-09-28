//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Builders the naming facts share: entities as key lists, outputs, and a
/// level of placements resolved in memory, with no compile.
/// </summary>
internal static class RoomNamingFacts
{
    /// <summary>A placement for <see cref="Resolve"/>: a room, its cell and turn, its entities made fresh, its joined sides.</summary>
    public sealed record Placed(string Room, int X, int Y, int Turns, Func<int, List<LevelEntity>> Entities, JoinedMask Joined = JoinedMask.None);

    /// <summary>An output value, ESC-separated as Hammer writes it.</summary>
    public static string Out(string target, string input, string parameter = "", string delay = "0", string times = "-1") =>
        string.Join(RoomOutput.Escape, target, input, parameter, delay, times);

    /// <summary>An entity of a placement from alternating keys and values.</summary>
    public static LevelEntity Ent(int placement, int index, params string[] keys)
    {
        List<LevelPair> pairs = [];
        for (int i = 0; i < keys.Length; i += 2)
        {
            pairs.Add(new LevelPair(keys[i], keys[i + 1]));
        }

        return new LevelEntity(placement, index, pairs);
    }

    /// <summary>A room's entity list made of entities given as key lists, indexed in order, for a placement.</summary>
    public static Func<int, List<LevelEntity>> Room(params string[][] entities) =>
        placement => [.. entities.Select((keys, i) => Ent(placement, i, keys))];

    /// <summary>Resolves the placements on a grid, the way the link and the flatten do.</summary>
    public static LevelResolution Resolve(bool mod, IReadOnlyList<Placed> placements, int columns = 3, int rows = 3, bool fold = true)
    {
        List<ResolverRoom> rooms = [];
        for (int i = 0; i < placements.Count; i++)
        {
            Placed placed = placements[i];
            List<LevelEntity> entities = placed.Entities(i);
            rooms.Add(new ResolverRoom
            {
                Room = placed.Room,
                Column = placed.X,
                Row = placed.Y,
                Turns = placed.Turns,
                Names = RoomNameAnalysis.Analyse(placed.Room, entities, null)[placed.Turns],
                Entities = entities,
                Joined = placed.Joined,
                CellCentre = $"{placed.X} {placed.Y} 0",
            });
        }

        return LevelEntityResolver.Resolve(rooms, new LevelNamingOptions(mod, fold, columns, rows));
    }

    /// <summary>The resolved entity named so, or null.</summary>
    public static LevelEntity? Named(LevelResolution resolution, string name) =>
        resolution.Entities.FirstOrDefault(e => e.TargetName == name);

    /// <summary>An entity's keys as (key, value) pairs.</summary>
    public static List<(string Key, string? Value)> Keys(LevelEntity entity) => [.. entity.Pairs.Select(p => (p.Key, p.Value))];

    /// <summary>The outputs of an entity with the given key.</summary>
    public static List<RoomOutput> Outputs(LevelEntity entity, string key) =>
        [.. entity.Pairs.Where(p => p.Key == key && RoomOutput.TryParse(p.Value, out _)).Select(p => { RoomOutput.TryParse(p.Value, out RoomOutput o); return o; })];
}
