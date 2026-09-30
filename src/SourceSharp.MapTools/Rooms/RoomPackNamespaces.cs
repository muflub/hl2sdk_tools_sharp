//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One library of a pack that holds rooms under namespaces (the rooms
/// design, 17.10 and 17.11): what <c>ssmap roompack</c> and
/// <c>ssmap room -namespace</c> record of it in the <c>NSPC</c> section.
/// </summary>
/// <param name="Key">
/// The namespace: the library's key, the front of each of its rooms' names
/// (<c>key.room</c>), by the level file's key rule
/// (<see cref="LevelLibraries.KeyProblem"/>).
/// </param>
/// <param name="Source">
/// The library VMF as it was given, relative to the pack's folder when it
/// can be, with <c>/</c> separators: what the link compares a level's path
/// with (by file name) to warn when a library has moved.
/// </param>
/// <param name="VmfSha256">The SHA-256 of the library VMF's bytes, as 64 lower-case hex digits.</param>
/// <param name="SingletonsSha256">
/// The SHA-256 of the singletons the namespace was compiled
/// under (<see cref="RoomPackNamespaces.SingletonDigest"/>), as 64 lower-case
/// hex digits: the same for every namespace of one pack.
/// </param>
/// <param name="FirstRoom">The index, in the pack, of the namespace's first room.</param>
/// <param name="RoomCount">How many rooms the namespace holds; its rooms follow one another from <paramref name="FirstRoom"/>.</param>
public sealed record RoomPackNamespace(string Key, string Source, string VmfSha256, string SingletonsSha256, int FirstRoom, int RoomCount)
{
    /// <summary>
    /// The namespace's own name-valued keys (its library's
    /// <c>rooms_name_keys</c>, as <see cref="RoomLibraryOptions.NameKeys"/>
    /// normalises them), or null when it adds none. Name keys are not
    /// singletons (the rooms design, 17.4): each library's shaped its own
    /// rooms' names, so each namespace keeps its own.
    /// </summary>
    public string? NameKeys { get; init; }

    /// <summary>The namespace's name keys as a set, or null when it adds none (<see cref="RoomLibraryOptions.NameKeySet"/>).</summary>
    public IReadOnlySet<string>? NameKeySet => new RoomLibraryOptions { NameKeys = NameKeys }.NameKeySet;

    /// <summary>The prefix every room of the namespace carries: the key and a dot.</summary>
    public string Prefix => Key + LevelLibraries.Separator;
}

/// <summary>
/// The <c>NSPC</c> library section of a combined pack (the rooms design,
/// 17.11): which rooms belong to which library, where each library came
/// from, and what each was compiled under.
/// </summary>
/// <remarks>
/// <para>
/// <b>An optional known tag, not a version.</b> A pack without the section
/// is a plain pack, as every pack before it: one library, rooms under their
/// own names. A pack with it holds its rooms under qualified names,
/// <c>key.room</c>, grouped by namespace in library order. A build that does
/// not know the tag skips it (a reader looks sections up by tag) and reads
/// the rooms under their qualified names, which are legal room names, with
/// the pack's <c>LENT</c> and <c>LOPT</c>, which are the level's
/// singletons (D29): it cannot read a level file that names several libraries
/// (an unknown key), so nothing links silently wrong, and the pack version
/// does not move.
/// </para>
/// <para>
/// <b>Layout</b>, in the section framing of the rooms design's 1.1 (every
/// integer big-endian): a codec byte (0, none), an <c>int64</c> payload
/// length, then the payload: <c>int32</c> revision (<see cref="Revision"/>),
/// <c>int32</c> namespace count (at least one), and per namespace, in
/// library order: its key and its source (each an <c>int32</c> byte count
/// and UTF-8), the 32 bytes of its VMF's SHA-256, the 32 bytes of the
/// singleton digest it was compiled under, <c>int32</c> first room index,
/// <c>int32</c> room count, and its name keys (an <c>int32</c> byte count and
/// UTF-8, empty for none). A section of another revision reads as no
/// section: the pack is then read as plain, as an older build reads it.
/// </para>
/// <para>
/// <b>Why the two digests.</b> <c>ssmap roompack -only</c> copies the
/// namespaces it does not rebuild byte for byte from the existing pack.
/// That is right only if each copied library is what it was (its VMF's
/// digest) and was compiled under what the level's singletons now are (the
/// singleton digest: every namespace is compiled with the first library's
/// worldspawn and lit under the level's sun, D26 and D29). Either differing makes the copy
/// stale, and <c>-only</c> refuses it rather than write a pack a full build
/// would not.
/// </para>
/// </remarks>
public static class RoomPackNamespaces
{
    /// <summary>The section's tag.</summary>
    public const string SectionTag = "NSPC";

    /// <summary>The payload revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>The bytes of a SHA-256.</summary>
    private const int DigestBytes = 32;

    // codec byte + int64 payload length
    private const int HeaderBytes = 1 + 8;

    /// <summary>The section for a pack's namespaces, in library order.</summary>
    /// <param name="namespaces">The namespaces, their rooms in pack order.</param>
    /// <returns>The section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="namespaces"/> is null.</exception>
    /// <exception cref="ArgumentException">No namespace, a key that is not a key, a digest that is not 64 hex digits, or a negative index or count.</exception>
    public static RoomPackSectionData ToSection(IReadOnlyList<RoomPackNamespace> namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        if (namespaces.Count == 0)
        {
            throw new ArgumentException("a pack with namespaces has at least one.", nameof(namespaces));
        }

        using MemoryStream payload = new();
        Int(payload, Revision);
        Int(payload, namespaces.Count);
        foreach (RoomPackNamespace space in namespaces)
        {
            ArgumentNullException.ThrowIfNull(space, nameof(namespaces));
            if (LevelLibraries.KeyProblem(space.Key) is { } problem)
            {
                throw new ArgumentException($"the namespace \"{space.Key}\" {problem}.", nameof(namespaces));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(space.FirstRoom, nameof(namespaces));
            ArgumentOutOfRangeException.ThrowIfNegative(space.RoomCount, nameof(namespaces));
            Text(payload, space.Key);
            Text(payload, space.Source);
            payload.Write(DigestOf(space.VmfSha256));
            payload.Write(DigestOf(space.SingletonsSha256));
            Int(payload, space.FirstRoom);
            Int(payload, space.RoomCount);
            Text(payload, space.NameKeys ?? string.Empty);
        }

        byte[] body = payload.ToArray();
        byte[] bytes = new byte[HeaderBytes + body.Length];
        bytes[0] = RoomLibraryOptions.CodecNone;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), body.Length);
        body.CopyTo(bytes, HeaderBytes);
        return new RoomPackSectionData(SectionTag, bytes);
    }

    /// <summary>Reads the section's namespaces.</summary>
    /// <param name="section">The section's bytes.</param>
    /// <returns>The namespaces in library order, or null for a revision this build does not read (the pack then reads as plain).</returns>
    /// <exception cref="LinkException">The section is cut short or out of shape; the message says how.</exception>
    /// <remarks>
    /// This checks the section on its own; <see cref="Check"/> holds it to
    /// the pack's index.
    /// </remarks>
    public static IReadOnlyList<RoomPackNamespace>? Read(ReadOnlySpan<byte> section)
    {
        if (section.Length < HeaderBytes)
        {
            throw Bad($"is {section.Length} bytes, shorter than its {HeaderBytes}-byte header");
        }

        if (section[0] != RoomLibraryOptions.CodecNone)
        {
            throw Bad($"has codec {section[0]}; this build reads codec {RoomLibraryOptions.CodecNone} (none)");
        }

        long length = BinaryPrimitives.ReadInt64BigEndian(section[1..]);
        ReadOnlySpan<byte> payload = section[HeaderBytes..];
        if (length != payload.Length)
        {
            throw Bad($"says {length} bytes but holds {payload.Length}");
        }

        int at = 0;
        if (ReadInt(payload, ref at) != Revision)
        {
            return null;
        }

        int count = ReadInt(payload, ref at);
        if (count < 1 || count > RoomPack.MaxRooms)
        {
            throw Bad($"claims {count} namespaces; a pack with namespaces has 1 to {RoomPack.MaxRooms}");
        }

        List<RoomPackNamespace> namespaces = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string key = ReadText(payload, ref at);
            if (LevelLibraries.KeyProblem(key) is { } problem)
            {
                throw Bad($"names namespace \"{key}\", which {problem}");
            }

            if (!seen.Add(key))
            {
                throw Bad($"names namespace \"{key}\" twice, ignoring case");
            }

            string source = ReadText(payload, ref at);
            string vmf = ReadDigest(payload, ref at);
            string singletons = ReadDigest(payload, ref at);
            int first = ReadInt(payload, ref at);
            int rooms = ReadInt(payload, ref at);
            if (first < 0 || rooms < 0)
            {
                throw Bad($"gives namespace {key} rooms from {first}, {rooms} of them");
            }

            string nameKeys = ReadText(payload, ref at);
            namespaces.Add(new RoomPackNamespace(key, source, vmf, singletons, first, rooms) { NameKeys = nameKeys.Length == 0 ? null : nameKeys });
        }

        if (at != payload.Length)
        {
            throw Bad($"has {payload.Length - at} bytes after its last namespace");
        }

        return namespaces;
    }

    /// <summary>
    /// Holds a pack's namespaces to its index: they cover its rooms in order,
    /// each from where the one before it ends, the last to the pack's last
    /// room, and every room's name starts with its namespace's key and a dot.
    /// </summary>
    /// <param name="namespaces">The namespaces, as read.</param>
    /// <param name="index">The pack's index.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">They do not; the message says where.</exception>
    public static void Check(IReadOnlyList<RoomPackNamespace> namespaces, RoomPackIndex index)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(index);
        int expected = 0;
        foreach (RoomPackNamespace space in namespaces)
        {
            if (space.FirstRoom != expected)
            {
                throw Bad($"starts namespace {space.Key} at room {space.FirstRoom}; its rooms follow the namespace before it, from room {expected}");
            }

            if ((long)space.FirstRoom + space.RoomCount > index.Entries.Count)
            {
                throw Bad($"gives namespace {space.Key} rooms {space.FirstRoom} to {(long)space.FirstRoom + space.RoomCount - 1}; the pack holds {index.Entries.Count}");
            }

            for (int i = space.FirstRoom; i < space.FirstRoom + space.RoomCount; i++)
            {
                string name = index.Entries[i].Name;
                if (!name.StartsWith(space.Prefix, StringComparison.Ordinal) || name.Length == space.Prefix.Length)
                {
                    throw Bad($"puts room \"{name}\" in namespace {space.Key}; its rooms are named {space.Prefix}<room>");
                }
            }

            expected = space.FirstRoom + space.RoomCount;
        }

        if (expected != index.Entries.Count)
        {
            throw Bad($"covers {expected} of the pack's {index.Entries.Count} rooms; every room of a pack with namespaces belongs to one");
        }
    }

    /// <summary>The namespace of a key, compared exactly as the level's keys are, or null.</summary>
    /// <param name="namespaces">The pack's namespaces.</param>
    /// <param name="key">The key.</param>
    /// <returns>The namespace, or null.</returns>
    public static RoomPackNamespace? Find(IReadOnlyList<RoomPackNamespace> namespaces, string key)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        return namespaces.FirstOrDefault(n => n.Key == key);
    }

    /// <summary>The SHA-256 of some bytes, as 64 lower-case hex digits: what a namespace records of its VMF.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The digest.</returns>
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The digest of the singletons a combined pack compiles every namespace
    /// under: the first library's worldspawn as each room carries it, the
    /// level's library-wide entities (the sun among them; the first
    /// library's, its gaps filled from the later ones, D29) and, when a
    /// later library supplies it, the level's skybox room.
    /// </summary>
    /// <param name="world">The worldspawn's keys as a room carries them (<see cref="RoomLibraryVmf.RoomWorldKeys"/>), in order.</param>
    /// <param name="libraryEntities">The level's library-wide entities, in the pack's order (<see cref="LevelLibraries.Singletons"/>).</param>
    /// <param name="laterSkybox">
    /// The level's skybox room, qualified, when it is not the first
    /// library's (D29), else null.
    /// </param>
    /// <returns>64 lower-case hex digits.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// These are exactly what another library's rooms take from the first
    /// library when they compile into its pack (D26): the worldspawn keys
    /// are injected into every room's document, and the sun lights every
    /// room. The first library's settings (<c>LOPT</c>) and skybox shape no
    /// other library's room, so they are not folded: changing the first
    /// library's entity reserve leaves the other namespaces current.
    /// </para>
    /// <para>
    /// <b>A skybox a later library supplies</b> is folded, because it is
    /// packed with that library's rooms: <c>-only</c> copying that namespace
    /// whole is right only while the level's skybox is still its (the first
    /// library gaining a skybox of its own takes the room out of the
    /// namespace). The first library's skybox is not folded, as before, so
    /// a pack whose first library has every singleton keeps its digest.
    /// </para>
    /// <para>
    /// Every part is length-prefixed, so no two lists of keys and entities
    /// fold alike. The entities fold as their <c>LENT</c> section's bytes,
    /// which are a function of them.
    /// </para>
    /// </remarks>
    public static string SingletonDigest(IReadOnlyList<KeyValuePair<string, string>> world, IReadOnlyList<VmfChunk> libraryEntities, string? laterSkybox = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(libraryEntities);
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Part(ReadOnlySpan<byte> part)
        {
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, part.Length);
            sha.AppendData(length);
            sha.AppendData(part);
        }

        Part("ssmap roompack singletons"u8);
        Part(Encoding.UTF8.GetBytes(world.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (KeyValuePair<string, string> pair in world)
        {
            Part(Encoding.UTF8.GetBytes(pair.Key));
            Part(Encoding.UTF8.GetBytes(pair.Value));
        }

        Part(libraryEntities.Count == 0 ? [] : RoomLibraryEntities.ToSection(libraryEntities).Bytes.Span);
        if (laterSkybox is not null)
        {
            Part("skybox"u8);
            Part(Encoding.UTF8.GetBytes(laterSkybox));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// The singletons a lit combined pack's rooms are built under: the
    /// <paramref name="singletons"/> digest (<see cref="SingletonDigest"/>)
    /// and, when the level has a skybox, the skybox's content, which every
    /// sky room of every namespace is baked over (the rooms design, 4.12;
    /// <see cref="RoomLightingSettings.Skybox"/>).
    /// </summary>
    /// <param name="singletons">The pack's singleton digest.</param>
    /// <param name="skybox">The level's skybox room as the pack compiles it (qualified, under the first library's worldspawn), or null.</param>
    /// <returns><paramref name="singletons"/> itself without a skybox; else a digest over both.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="singletons"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why only lit.</b> An unlit room reads nothing of the skybox, so an
    /// unlit pack keeps <see cref="SingletonDigest"/> as it was, and a
    /// skybox edit copies its other namespaces as before. A lit room with a
    /// sky face is baked over the skybox's geometry, so <c>-only</c> may copy
    /// a namespace only while the skybox is the one its sky rooms were baked
    /// over: the skybox's cache digest (<see cref="RoomCacheKey.RoomDigest"/>,
    /// its room-local document and claims) is folded in. Its materials and
    /// models are game content, which <c>-only</c> never promised to watch.
    /// </para>
    /// <para>
    /// Folded for every lit pack with a skybox, whichever library supplies it
    /// (D29): a skybox of the first library is not otherwise in the digest,
    /// and a later library's is there by name only. A lit pack written
    /// before this recorded the digest without it, so its namespaces are
    /// refused once by <c>-only</c> and rebuilt, never copied stale.
    /// </para>
    /// </remarks>
    public static string LitSingletonDigest(string singletons, LibraryRoom? skybox)
    {
        ArgumentNullException.ThrowIfNull(singletons);
        if (skybox is null)
        {
            return singletons;
        }

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string part in (string[])["ssmap roompack lit singletons", singletons, RoomCacheKey.RoomDigest(skybox)])
        {
            byte[] bytes = Encoding.UTF8.GetBytes(part);
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
            sha.AppendData(length);
            sha.AppendData(bytes);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static LinkException Bad(string what) => new($"the room pack's {SectionTag} section {what}.");

    private static byte[] DigestOf(string hex)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex ?? string.Empty);
        }
        catch (FormatException)
        {
            bytes = [];
        }

        return bytes.Length == DigestBytes && hex!.Length == DigestBytes * 2
            ? bytes
            : throw new ArgumentException($"\"{hex}\" is not a SHA-256 of {DigestBytes * 2} hex digits.", nameof(hex));
    }

    private static void Int(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void Text(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Int(s, bytes.Length);
        s.Write(bytes);
    }

    private static int ReadInt(ReadOnlySpan<byte> payload, ref int at)
    {
        if (payload.Length - at < 4)
        {
            throw Bad("is cut short");
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(payload[at..]);
        at += 4;
        return value;
    }

    private static string ReadDigest(ReadOnlySpan<byte> payload, ref int at)
    {
        if (payload.Length - at < DigestBytes)
        {
            throw Bad("is cut short");
        }

        string hex = Convert.ToHexStringLower(payload.Slice(at, DigestBytes));
        at += DigestBytes;
        return hex;
    }

    private static string ReadText(ReadOnlySpan<byte> payload, ref int at)
    {
        int length = ReadInt(payload, ref at);
        if (length < 0 || length > payload.Length - at)
        {
            throw Bad("is cut short");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload.Slice(at, length));
        }
        catch (DecoderFallbackException)
        {
            throw Bad("holds text that is not UTF-8");
        }

        at += length;
        return text;
    }
}
