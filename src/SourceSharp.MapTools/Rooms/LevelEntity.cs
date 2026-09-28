//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One key of an entity the naming resolver works on.</summary>
/// <remarks>
/// A position key the link has turned but not yet moved to its cell has no
/// text yet: it carries <see cref="Position"/> instead of <see cref="Value"/>,
/// and the resolver never touches it (names are never positions). Every other
/// key has its value. <see cref="IsConnection"/> says a key came from a VMF
/// entity's <c>connections</c> chunk, so the flatten writes it back there;
/// the resolver itself reads outputs by their shape (<see cref="RoomOutput"/>)
/// on both paths, which is what keeps the link and the flatten in agreement.
/// </remarks>
internal sealed class LevelPair
{
    /// <summary>A key with its text.</summary>
    public LevelPair(string key, string value, bool isConnection = false)
    {
        Key = key;
        Value = value;
        IsConnection = isConnection;
    }

    /// <summary>A position key of a turned link entity, moved to its cell later.</summary>
    public LevelPair(RoomLinkPair position)
    {
        Key = position.Key;
        Position = position;
    }

    /// <summary>The key as written.</summary>
    public string Key { get; set; }

    /// <summary>The value, or null for a position key.</summary>
    public string? Value { get; set; }

    /// <summary>Whether the flatten writes the key into the entity's <c>connections</c> chunk.</summary>
    public bool IsConnection { get; init; }

    /// <summary>The turned position of a position key, or null.</summary>
    public RoomLinkPair? Position { get; }

    /// <summary>
    /// Whether the resolver removed the key (a <c>room_needs</c> taken off,
    /// an output to an empty cell). Removed keys stay in the list until the
    /// resolver ends, so the stored tables' key indices keep pointing at the
    /// keys they were made for.
    /// </summary>
    public bool Deleted { get; set; }
}

/// <summary>
/// One entity of a level, as the naming resolver sees it: a list of keys it
/// may rename, remove or add to, whatever path the entity came from (a
/// compiled room's entity lump for the link, the room's VMF for the flatten).
/// </summary>
internal sealed class LevelEntity
{
    /// <summary>An entity of a placement.</summary>
    /// <param name="placement">The placement's index in link order, or -1 for a room analysed on its own.</param>
    /// <param name="source">The entity's index in its room's entity list, or -1 for one the linker writes.</param>
    /// <param name="pairs">Its keys, in order.</param>
    public LevelEntity(int placement, int source, List<LevelPair> pairs)
    {
        Placement = placement;
        Source = source;
        Pairs = pairs;
    }

    /// <summary>The placement the entity belongs to, or -1.</summary>
    public int Placement { get; }

    /// <summary>The entity's index in its room's entity list, or -1 for one the linker writes.</summary>
    public int Source { get; }

    /// <summary>The keys, in order; the resolver edits the list in place.</summary>
    public List<LevelPair> Pairs { get; }

    /// <summary>Whether the resolver removed the entity (a failed <c>room_needs</c>, a fold, a merge).</summary>
    public bool Removed { get; set; }

    /// <summary>Whatever the path that made the entity needs back: the VMF chunk for the flatten.</summary>
    public object? Payload { get; init; }

    /// <summary>
    /// The class: the first <c>classname</c> key, spelt exactly, or an empty
    /// string; how the link and the entity counts read it.
    /// </summary>
    public string ClassName
    {
        get
        {
            foreach (LevelPair pair in Pairs)
            {
                if (string.Equals(pair.Key, "classname", StringComparison.Ordinal))
                {
                    return pair.Value ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>The entity's <c>targetname</c>, or null.</summary>
    public string? TargetName => Get("targetname");

    /// <summary>The first value of a key, compared ignoring case as entity keys are; null when absent.</summary>
    public string? Get(string key) => Find(key) is int at ? Pairs[at].Value : null;

    /// <summary>The index of the first pair of a key, ignoring case, or null.</summary>
    public int? Find(string key)
    {
        for (int i = 0; i < Pairs.Count; i++)
        {
            if (string.Equals(Pairs[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Sets a key: the first pair of it gets the value, or a pair is appended.</summary>
    public void Set(string key, string value)
    {
        if (Find(key) is int at)
        {
            Pairs[at].Value = value;
        }
        else
        {
            Pairs.Add(new LevelPair(key, value));
        }
    }

    /// <summary>The hammer id messages name the entity by: <c>hammerid</c> in a compiled lump, <c>id</c> in a VMF.</summary>
    public string Id => Get("hammerid") ?? Get("id") ?? "?";

    /// <summary>A compiled entity's keys, in lump order.</summary>
    public static LevelEntity FromBsp(BspEntity entity, int placement, int source)
    {
        List<LevelPair> pairs = new(entity.Pairs.Count);
        foreach (BspKeyValue pair in entity.Pairs)
        {
            pairs.Add(new LevelPair(pair.Key, pair.Value));
        }

        return new LevelEntity(placement, source, pairs);
    }

    /// <summary>A turned link entity's keys, its positions kept for the link to move.</summary>
    public static LevelEntity FromLink(RoomLinkEntity entity, int placement, int source)
    {
        List<LevelPair> pairs = new(entity.Pairs.Count);
        foreach (RoomLinkPair pair in entity.Pairs)
        {
            pairs.Add(pair.Value is { } value ? new LevelPair(pair.Key, value) : new LevelPair(pair));
        }

        return new LevelEntity(placement, source, pairs);
    }

    /// <summary>
    /// A VMF entity's keys, then its <c>connections</c> keys, in the order
    /// vbsp adds them to the compiled entity (its keys as they come, the
    /// connections after); the chunk rides along as the payload.
    /// </summary>
    public static LevelEntity FromVmf(VmfChunk entity, int placement, int source)
    {
        List<LevelPair> pairs = [];
        foreach (VmfKey key in entity.Keys)
        {
            pairs.Add(new LevelPair(key.Name, key.Value));
        }

        foreach (VmfChunk connections in entity.GetChunks(MapFileLoader.ConnectionsChunk))
        {
            foreach (VmfKey key in connections.Keys)
            {
                pairs.Add(new LevelPair(key.Name, key.Value, isConnection: true));
            }
        }

        return new LevelEntity(placement, source, pairs) { Payload = entity };
    }
}
