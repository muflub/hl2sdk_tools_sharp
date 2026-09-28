//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The ids that tie a level's outputs to what they were built from: the
/// pack id a room pack carries, and the level id a linked map and its
/// navigation file both carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>What they are for.</b> The game loads a <c>.bsp</c> and, beside it, a
/// <c>.nav3d</c>; nothing stops the two coming from different links. So the
/// link writes one level id into both (the map's worldspawn key
/// <see cref="LevelIdKey"/>, the navigation file's header), and the mod
/// checks they are equal before it trusts the navigation. The pack id is
/// recorded too (<see cref="PackIdKey"/>), so a level can say which room
/// compile it came from.
/// </para>
/// <para>
/// <b>Deterministic, by design.</b> A room pack is a function of its inputs,
/// byte for byte, run after run; a random id would break that. So both ids
/// are name-based: RFC 9562 version 8 UUIDs whose 122 free bits are the
/// first bits of a SHA-256 over their inputs. Version 8 with SHA-256 rather
/// than version 5 with SHA-1 because the rest of the room pipeline already
/// hashes its inputs with SHA-256 (the room container's input keys), and
/// version 5 is defined over SHA-1 alone. The pack id hashes the library
/// VMF's bytes, the options that shape the rooms and their navigation, and
/// the tool's identity (the one the room container records); the level id
/// hashes the pack id, the level file's bytes and the link options. Every
/// id is made in one place, <see cref="Derive"/>: switching to random ids is
/// that one function returning <see cref="Guid.NewGuid"/>, at the price of
/// reproducible packs.
/// </para>
/// </remarks>
public static class RoomCompileIds
{
    /// <summary>The room pack's library section holding the pack id: its sixteen bytes in RFC 9562 order.</summary>
    public const string PackSection = "CMPL";

    /// <summary>The linked map's worldspawn key holding the level id.</summary>
    public const string LevelIdKey = "ss_level_id";

    /// <summary>The linked map's worldspawn key holding the id of the pack its rooms came from.</summary>
    public const string PackIdKey = "ss_pack_id";

    /// <summary>A room pack's id.</summary>
    /// <param name="library">The library VMF's bytes, as read.</param>
    /// <param name="options">The options that shape the rooms, in the order given (paths to outputs and thread counts left out: they change no byte).</param>
    /// <param name="navigation">The navigation settings' description, or null when the library builds none.</param>
    /// <returns>The id.</returns>
    public static Guid PackId(ReadOnlySpan<byte> library, IEnumerable<string> options, string? navigation)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Derive(
            "ssmap room pack",
            [library.ToArray(), Encoding.UTF8.GetBytes(string.Join('\n', options)), Encoding.UTF8.GetBytes(navigation ?? "-"),
                Encoding.UTF8.GetBytes(RoomObjectStore.ToolIdentityOf())]);
    }

    /// <summary>A linked level's id.</summary>
    /// <param name="packId">The pack the rooms came from.</param>
    /// <param name="level">The level file's bytes, as read.</param>
    /// <param name="options">The link options that shape the outputs, in the order given.</param>
    /// <returns>The id.</returns>
    public static Guid LevelId(Guid packId, ReadOnlySpan<byte> level, IEnumerable<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Derive("ssmap link level", [ToBytes(packId), level.ToArray(), Encoding.UTF8.GetBytes(string.Join('\n', options))]);
    }

    /// <summary>An id's sixteen bytes in RFC 9562 (big-endian) order, as the pack and the navigation file store it.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ToBytes(Guid id)
    {
        byte[] bytes = new byte[16];
        _ = id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    /// <summary>An id from its sixteen RFC 9562 bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The id.</returns>
    /// <exception cref="LinkException">Not sixteen bytes.</exception>
    public static Guid FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 16 ? new Guid(bytes, bigEndian: true) : throw new LinkException($"a compile id is 16 bytes, not {bytes.Length}.");

    /// <summary>The pack id's library section.</summary>
    /// <param name="packId">The id.</param>
    /// <returns>The section.</returns>
    public static RoomPackSectionData Section(Guid packId) => new(PackSection, ToBytes(packId));

    /// <summary>Writes the level and pack ids into a map's worldspawn.</summary>
    /// <param name="bsp">The linked map; its entity lump is replaced.</param>
    /// <param name="packId">The pack id, or null when the pack has none.</param>
    /// <param name="levelId">The level id.</param>
    /// <exception cref="LinkException">The map has no worldspawn.</exception>
    public static void Stamp(BspData bsp, Guid? packId, Guid levelId)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        BspEntity world = entities.FirstOrDefault(e => e.ClassName == "worldspawn")
            ?? throw new LinkException("the linked map has no worldspawn to carry its level id.");
        world.Pairs.RemoveAll(p => p.Key == LevelIdKey || p.Key == PackIdKey);
        world.Pairs.Add(new BspKeyValue(LevelIdKey, levelId.ToString("D")));
        if (packId is Guid pack)
        {
            world.Pairs.Add(new BspKeyValue(PackIdKey, pack.ToString("D")));
        }

        bsp.SetLump(BspLump.Entities, EntityLump.Write(entities).Data, bsp[BspLump.Entities].Version);
    }

    /// <summary>A map's level id, from its worldspawn, or null.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The id, or null when the map has none.</returns>
    public static Guid? LevelIdOf(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        BspEntity? world = EntityLump.Parse(bsp[BspLump.Entities]).FirstOrDefault(e => e.ClassName == "worldspawn");
        return world?.Get(LevelIdKey) is { } text && Guid.TryParse(text, out Guid id) ? id : null;
    }

    /// <summary>
    /// The one place an id is made: an RFC 9562 version 8 UUID over the
    /// SHA-256 of a purpose and its inputs, each length-prefixed so no two
    /// input lists hash alike. Return <see cref="Guid.NewGuid"/> here to make
    /// every id random instead.
    /// </summary>
    private static Guid Derive(string purpose, IReadOnlyList<byte[]> parts)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        byte[] name = Encoding.UTF8.GetBytes(purpose);
        BinaryPrimitives.WriteInt64BigEndian(length, name.Length);
        sha.AppendData(length);
        sha.AppendData(name);
        foreach (byte[] part in parts)
        {
            BinaryPrimitives.WriteInt64BigEndian(length, part.Length);
            sha.AppendData(length);
            sha.AppendData(part);
        }

        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset(hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
