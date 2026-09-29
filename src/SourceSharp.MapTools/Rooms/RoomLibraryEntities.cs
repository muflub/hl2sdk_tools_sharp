//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The entities that belong to a whole room library rather than to one of
/// its rooms, and the room pack's library section that keeps them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entities.</b> A few classes set something for the whole map,
/// not for the room they stand in: the sun (<c>light_environment</c>), fog,
/// tone mapping, dynamic shadow direction and post-processing
/// (<c>env_fog_controller</c>, <c>env_tonemap_controller</c>,
/// <c>shadow_control</c>, <c>postprocess_controller</c>). Every room of a
/// library shares one of each (decision D3 of the rooms design: one sun for
/// the library), so an author puts it outside every cell, in the gaps
/// between rooms. The split used to ignore every point entity in the gaps,
/// which is right for the editor's clutter there (a note, a camera, a light
/// to see by) and silently wrong for these: the library's sun vanished
/// without a word. <see cref="RoomLibraryVmf.SplitLibrary"/> collects them
/// instead, and <c>ssmap room</c> writes them into the pack.
/// </para>
/// <para>
/// <b>Only from the gaps.</b> Every other class in the gaps is still
/// ignored, except <c>sky_camera</c>, which is refused there as in a room
/// (below).
/// </para>
/// <para>
/// <b>A room's own copy</b> (<see cref="KeepInRoom"/>, which the split runs
/// on every room, so <c>ssmap room</c>, <c>ssmap rooms</c>, <c>ssmap
/// layout</c> and the flatten all apply it): a room that carries a
/// <c>light_environment</c> is refused unless its keys equal the library's,
/// and then the copy is dropped from the room (decision D3: all rooms share
/// one sun, and a room that disagrees is refused when the pack is built).
/// The four controllers follow the sun, with one allowance the design makes
/// for them: a controller with a <c>targetname</c> that the library does not
/// hold under that name is a room's own (per-room fog is a trigger and a
/// named controller, as in any map), and stays with the room. An unnamed
/// controller, or one named as a library copy is, is library-wide: dropped
/// when equal, refused when not. <c>sky_camera</c> is refused in a room: a 3D
/// skybox belongs to the library's skybox room, which a later part of the
/// rooms work adds. "Equal" is every key but <c>id</c> (the editor's
/// number) and <c>origin</c> (a map-wide entity has no position that means
/// anything, and a copy in a cell cannot stand where the gap's does), plus
/// the outputs in order; a missing key reads as an empty one, as every
/// reader of an entity reads it.
/// </para>
/// <para>
/// <b>In the gaps, one of each.</b> Two suns, or two controllers of one
/// class with the same name (or both unnamed), are refused: the level has
/// one, and keeping either would be a silent choice.
/// </para>
/// <para>
/// <b>In the level</b> (<see cref="ToLinked"/>, <see cref="ForFlatten"/>):
/// the link and the flatten write each library entity once, right after the
/// worldspawn and before every room's entities, never turned (the library's
/// frame is the level's: the sun stands still while rooms turn), with its
/// <c>origin</c> at the level's origin. The gap it stood in has no
/// counterpart in a level, and the one position that is safe in every level
/// is the origin: vbsp's leak flood skips an entity standing exactly there,
/// so the flattened map's compile can never leak through it, where a
/// position in the void would.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>) is written only when the
/// library has such an entity, so a library without one writes the same
/// pack as before this section existed. Its payload follows the pack's
/// convention for new sections: one codec byte (0, none: the only one this
/// build writes or reads, since a handful of entities gains nothing from
/// compression), the payload's uncompressed length as a big-endian
/// <c>int64</c>, then the entities as VMF chunk text, in library order and
/// in library coordinates, exactly as the library wrote them. Text rather
/// than a binary form because the flatten consumes VMF chunks and the link
/// consumes key-value pairs, and both read this text; library coordinates
/// because a gap has no room to be local to.
/// </para>
/// </remarks>
public static class RoomLibraryEntities
{
    /// <summary>The tag of the pack's library section that holds the library-wide entities.</summary>
    public const string SectionTag = "LENT";

    /// <summary>The codec byte for an uncompressed payload, the only one this build writes and reads.</summary>
    public const byte CodecNone = 0;

    // codec byte + int64 uncompressed length
    private const int HeaderBytes = 1 + 8;

    /// <summary>Whether a class is one the whole library shares, and so is collected from the gaps.</summary>
    /// <param name="classname">The entity's class, or null.</param>
    /// <returns>True for the sun, fog, tone map, shadow and post-process controllers, matched exactly as vbsp matches classes.</returns>
    public static bool IsLibraryWide(string? classname) => classname switch
    {
        "light_environment" or "env_fog_controller" or "env_tonemap_controller" or "shadow_control" or "postprocess_controller" => true,
        _ => false,
    };

    /// <summary>The sun's class: one per library, compared by class alone.</summary>
    public const string SunClass = VmfPlacement.SunClass;

    /// <summary>The class of a 3D skybox's camera, which only the library's skybox room may hold.</summary>
    public const string SkyCameraClass = "sky_camera";

    /// <summary>The class vbsp adds to a map with water that has none; one per level.</summary>
    public const string WaterLodClass = "water_lod_control";

    /// <summary>Who a difference is reported against when the first copy is the library's.</summary>
    internal const string LibraryOwner = "the library's";

    /// <summary>
    /// Whether a class is one a level has one of per name: the library-wide
    /// classes, and <c>water_lod_control</c>, which vbsp adds to every room
    /// compile with water.
    /// </summary>
    /// <param name="classname">The entity's class, or null.</param>
    /// <returns>True for those classes, matched exactly.</returns>
    public static bool IsLevelSingleton(string? classname) =>
        IsLibraryWide(classname) || string.Equals(classname, WaterLodClass, StringComparison.Ordinal);

    /// <summary>
    /// What makes two copies of a singleton the same entity: the sun by its
    /// class alone (there is one sun), every other class by class and
    /// <c>targetname</c> (an unnamed copy and a named one are different
    /// entities, as are two different names).
    /// </summary>
    internal static string IdentityOf(string classname, string? targetname) =>
        string.Equals(classname, SunClass, StringComparison.Ordinal)
            ? classname
            : string.Concat(classname, "\0", targetname ?? string.Empty);

    /// <summary>An entity's keys and outputs as the singleton rules compare them.</summary>
    /// <param name="entity">A VMF entity.</param>
    /// <returns>Its keys in file order, then its <c>connections</c> outputs in file order.</returns>
    internal static List<KeyValuePair<string, string>> PairsOf(VmfChunk entity)
    {
        List<KeyValuePair<string, string>> pairs = [.. entity.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value))];
        foreach (VmfChunk connections in entity.GetChunks(MapFileLoader.ConnectionsChunk))
        {
            pairs.AddRange(connections.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value)));
        }

        return pairs;
    }

    /// <summary>A key's last value among <paramref name="pairs"/> (as vbsp keeps it: a repeated key's last value wins), or null.</summary>
    internal static string? LastValue(IEnumerable<KeyValuePair<string, string>> pairs, string key)
    {
        string? value = null;
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
            }
        }

        return value;
    }

    /// <summary>
    /// The first difference between two copies of an entity, or null when
    /// they are equal: every key but <c>id</c>, <c>hammerid</c> and
    /// <c>origin</c>, a missing key reading as an empty one, and the outputs
    /// in order.
    /// </summary>
    /// <param name="entity">The copy being checked.</param>
    /// <param name="first">The copy it is checked against.</param>
    /// <returns>The key, as <paramref name="entity"/> spells it (else as <paramref name="first"/> does), and the two values.</returns>
    /// <remarks>
    /// <para>
    /// A plain key is compared by name ignoring case, with its last value,
    /// which is what vbsp keeps and the engine reads. The keys are visited in
    /// <paramref name="entity"/>'s order and then <paramref name="first"/>'s,
    /// so the key named is the same on every run.
    /// </para>
    /// <para>
    /// An output (a value that reads as <c>target,input,parameter,delay,times</c>,
    /// <see cref="RoomOutput"/>) may repeat under one name, so outputs are
    /// compared as a list, in order: the entity fires them in that order.
    /// </para>
    /// </remarks>
    internal static (string Key, string Value, string Other)? Difference(
        IReadOnlyList<KeyValuePair<string, string>> entity, IReadOnlyList<KeyValuePair<string, string>> first)
    {
        (List<string> order, Dictionary<string, (string Key, string Value)> plain, List<KeyValuePair<string, string>> outputs) a = Normalise(entity);
        (List<string> order, Dictionary<string, (string Key, string Value)> plain, List<KeyValuePair<string, string>> outputs) b = Normalise(first);
        foreach (string key in a.order.Concat(b.order.Where(k => !a.plain.ContainsKey(k))))
        {
            (string spelt, string value) = a.plain.TryGetValue(key, out (string Key, string Value) found) ? found : (b.plain[key].Key, string.Empty);
            string other = b.plain.TryGetValue(key, out (string Key, string Value) theirs) ? theirs.Value : string.Empty;
            if (!string.Equals(value, other, StringComparison.Ordinal))
            {
                return (spelt, value, other);
            }
        }

        for (int i = 0; i < Math.Max(a.outputs.Count, b.outputs.Count); i++)
        {
            KeyValuePair<string, string>? mine = i < a.outputs.Count ? a.outputs[i] : null;
            KeyValuePair<string, string>? theirs = i < b.outputs.Count ? b.outputs[i] : null;
            if (mine is null || theirs is null
                || !string.Equals(mine.Value.Key, theirs.Value.Key, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(mine.Value.Value, theirs.Value.Value, StringComparison.Ordinal))
            {
                string key = (mine ?? theirs)!.Value.Key;
                return (key, mine?.Value ?? string.Empty, theirs?.Value ?? string.Empty);
            }
        }

        return null;

        static (List<string>, Dictionary<string, (string Key, string Value)>, List<KeyValuePair<string, string>>) Normalise(
            IReadOnlyList<KeyValuePair<string, string>> pairs)
        {
            List<string> order = [];
            Dictionary<string, (string Key, string Value)> plain = new(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> outputs = [];
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                if (IsIgnored(pair.Key))
                {
                    continue;
                }

                if (RoomOutput.TryParse(pair.Value, out _))
                {
                    outputs.Add(pair);
                }
                else if (plain.TryGetValue(pair.Key, out (string Key, string Value) seen))
                {
                    plain[pair.Key] = (seen.Key, pair.Value);
                }
                else
                {
                    plain[pair.Key] = (pair.Key, pair.Value);
                    order.Add(pair.Key);
                }
            }

            // The order list holds each key as first spelt; look-ups ignore case.
            return (order, plain, outputs);
        }

        static bool IsIgnored(string key) =>
            string.Equals(key, "id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "hammerid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "origin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal of a copy that differs from the first: the sun's message is
    /// the design's (<c>room {room}: its light_environment differs from the
    /// library's ({key}: "{a}" against "{b}"); the sun is library-wide.</c>),
    /// and the other classes read the same with their own class.
    /// </summary>
    /// <param name="room">The room whose copy differs.</param>
    /// <param name="classname">The class.</param>
    /// <param name="against">Whose copy it differs from: <see cref="LibraryOwner"/>, or <c>room {name}'s</c>.</param>
    /// <param name="difference">The first difference.</param>
    internal static string DiffersMessage(string room, string classname, string against, (string Key, string Value, string Other) difference)
    {
        string tail = string.Equals(classname, SunClass, StringComparison.Ordinal)
            ? "the sun is library-wide."
            : against == LibraryOwner ? $"{classname} is library-wide." : $"the level has one {classname}.";
        return $"room {room}: its {classname} differs from {against} ({difference.Key}: \"{difference.Value}\" against \"{difference.Other}\"); {tail}";
    }

    /// <summary>
    /// Whether a room keeps one of its entities, by the library-singleton
    /// rules: false for a copy of a library entity that equals it (the room
    /// drops it); true for everything else the rules allow.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="entity">One of the room's entities, as the library wrote it.</param>
    /// <param name="library">The library-wide entities from the gaps.</param>
    /// <param name="skybox">Whether the room is the library's skybox room, the one room a <c>sky_camera</c> belongs in.</param>
    /// <returns>Whether the room keeps it.</returns>
    /// <exception cref="RoomLibraryException">
    /// A <c>sky_camera</c> outside the skybox room; a sun or controller that differs from the
    /// library's; a sun, or an unnamed controller, when the library holds
    /// none. Each message names the room, and a difference names the key.
    /// </exception>
    internal static bool KeepInRoom(string room, VmfChunk entity, IReadOnlyList<VmfChunk> library, bool skybox = false)
    {
        string? classname = entity.GetValue("classname");
        if (string.Equals(classname, SkyCameraClass, StringComparison.Ordinal))
        {
            return skybox ? true : throw new RoomLibraryException($"room {room}: sky_camera is allowed only in the library's skybox room.");
        }

        if (!IsLibraryWide(classname))
        {
            return true;
        }

        List<KeyValuePair<string, string>> pairs = PairsOf(entity);
        string? name = NameOf(pairs);
        string identity = IdentityOf(classname!, name);
        VmfChunk? match = library.FirstOrDefault(l => IdentityOf(l.GetValue("classname")!, NameOf(PairsOf(l))) == identity);
        if (match is null)
        {
            if (!string.Equals(classname, SunClass, StringComparison.Ordinal) && name is not null)
            {
                // A named controller of the room's own: per-room fog and the like.
                return true;
            }

            throw new RoomLibraryException(string.Equals(classname, SunClass, StringComparison.Ordinal)
                ? $"room {room}: it has a light_environment, and the library has none in the gaps between rooms; the sun is library-wide, so it belongs there."
                : $"room {room}: it has an unnamed {classname}, and the library has none in the gaps between rooms; an unnamed {classname} is library-wide, so it belongs there (name it to keep it with the room).");
        }

        if (Difference(pairs, PairsOf(match)) is { } difference)
        {
            throw new RoomLibraryException(DiffersMessage(room, classname!, LibraryOwner, difference));
        }

        return false;
    }

    /// <summary>
    /// Refuses a library whose gaps hold two copies of one singleton: two
    /// suns, or two controllers of one class under one name (both unnamed
    /// counts as one name).
    /// </summary>
    /// <param name="entities">The library-wide entities from the gaps, in library order.</param>
    /// <exception cref="RoomLibraryException">Two copies of one singleton, naming the entity ids.</exception>
    internal static void CheckGaps(IReadOnlyList<VmfChunk> entities)
    {
        foreach (IGrouping<string, VmfChunk> group in entities.GroupBy(e => IdentityOf(e.GetValue("classname")!, NameOf(PairsOf(e))), StringComparer.Ordinal))
        {
            List<VmfChunk> copies = [.. group];
            if (copies.Count < 2)
            {
                continue;
            }

            string classname = copies[0].GetValue("classname")!;
            string ids = string.Join(", ", copies.Select(VmfPlacement.IdOf));
            string? name = NameOf(PairsOf(copies[0]));
            throw new RoomLibraryException(string.Equals(classname, SunClass, StringComparison.Ordinal)
                ? $"the library has {copies.Count} light_environment entities in the gaps between rooms (entities {ids}); the sun is library-wide, so there is one."
                : $"the library has {copies.Count} {(name is null ? "unnamed" : $"\"{name}\"")} {classname} entities in the gaps between rooms (entities {ids}); a level has one of each.");
        }
    }

    /// <summary>A library entity as the link writes it: what vbsp would compile it to, standing at the level's origin.</summary>
    /// <param name="entity">A library-wide entity, as the pack holds it.</param>
    /// <returns>
    /// Its keys in the order vbsp's compile of <see cref="ForFlatten"/> leaves
    /// them (each new key goes in front, a repeated key keeps its place and
    /// takes the later value, <c>id</c> becomes <c>hammerid</c>), then its
    /// outputs in order, so the linked entity is the flattened map's, key
    /// for key, but for the <c>hammerid</c>, which the flatten renumbers as it
    /// does every room entity's.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static BspEntity ToLinked(VmfChunk entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        VmfChunk placed = ForFlatten(entity);
        List<BspKeyValue> pairs = [];
        foreach (VmfKey key in placed.Keys)
        {
            string name = string.Equals(key.Name, "id", StringComparison.OrdinalIgnoreCase) ? "hammerid" : key.Name;
            int at = pairs.FindIndex(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                pairs[at] = new BspKeyValue(pairs[at].Key, key.Value);
            }
            else
            {
                pairs.Insert(0, new BspKeyValue(name, key.Value));
            }
        }

        foreach (VmfChunk connections in placed.GetChunks(MapFileLoader.ConnectionsChunk))
        {
            pairs.AddRange(connections.Keys.Select(k => new BspKeyValue(k.Name, k.Value)));
        }

        BspEntity linked = new();
        linked.Pairs.AddRange(pairs);
        return linked;
    }

    /// <summary>A library entity as the flatten writes it: a copy standing at the level's origin, never turned.</summary>
    /// <param name="entity">A library-wide entity, as the library wrote it.</param>
    /// <returns>A copy with every <c>origin</c> key set to <c>0 0 0</c>; an entity without one is copied as it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static VmfChunk ForFlatten(VmfChunk entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        VmfChunk copy = VmfPlacement.Clone(entity);
        foreach (VmfKey key in copy.Keys)
        {
            if (string.Equals(key.Name, "origin", StringComparison.OrdinalIgnoreCase))
            {
                key.Value = LevelOrigin;
            }
        }

        return copy;
    }

    /// <summary>
    /// The library-wide entities counted by class, as the level carries
    /// them: what the link's entity budget adds once per level, and what
    /// <c>ssmap layout</c> and <c>ssmap rooms</c> count.
    /// </summary>
    /// <param name="entities">The library-wide entities (<see cref="RoomLibrary.LibraryEntities"/>).</param>
    /// <returns>Their counts, bound to no room.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    public static RoomEntityCounts Count(IReadOnlyList<VmfChunk> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return RoomEntityCounts.FromClasses(entities.Select(e => ToLinked(e).ClassName));
    }

    /// <summary>Where every library entity stands in a level.</summary>
    public const string LevelOrigin = "0 0 0";

    /// <summary>An entity's <c>targetname</c> (its last, as vbsp keeps it), or null when it has none or an empty one.</summary>
    internal static string? NameOf(IEnumerable<KeyValuePair<string, string>> pairs) =>
        LastValue(pairs, "targetname") is { Length: > 0 } name ? name : null;

    /// <summary>The pack section that holds a library's library-wide entities.</summary>
    /// <param name="entities">The entities, in library order (<see cref="RoomLibrarySplit.LibraryEntities"/>).</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    public static RoomPackSectionData ToSection(IReadOnlyList<VmfChunk> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        VmfDocument document = new();
        foreach (VmfChunk entity in entities)
        {
            document.Chunks.Add(VmfPlacement.Clone(entity));
        }

        byte[] text = document.ToBytes();
        byte[] bytes = new byte[HeaderBytes + text.Length];
        bytes[0] = CodecNone;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), text.Length);
        text.CopyTo(bytes, HeaderBytes);
        return new RoomPackSectionData(SectionTag, bytes);
    }

    /// <summary>Reads the library-wide entities back from their section's bytes.</summary>
    /// <param name="section">The section's bytes, as the pack holds them.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The entities, in library order.</returns>
    /// <exception cref="LinkException">A codec this build does not read, a length that disagrees with the bytes, or text that is not VMF chunks.</exception>
    public static async ValueTask<IReadOnlyList<VmfChunk>> ReadAsync(ReadOnlyMemory<byte> section, CancellationToken cancellationToken = default)
    {
        if (section.Length < HeaderBytes)
        {
            throw new LinkException($"the room pack's {SectionTag} section is {section.Length} bytes, shorter than its {HeaderBytes}-byte header.");
        }

        byte codec = section.Span[0];
        if (codec != CodecNone)
        {
            throw new LinkException($"the room pack's {SectionTag} section has codec {codec}; this build reads codec {CodecNone} (none).");
        }

        long length = BinaryPrimitives.ReadInt64BigEndian(section.Span[1..]);
        if (length != section.Length - HeaderBytes)
        {
            throw new LinkException(
                $"the room pack's {SectionTag} section says {length} bytes of entities but holds {section.Length - HeaderBytes}.");
        }

        VmfDocument document;
        try
        {
            document = await VmfDocument.ParseAsync(section[HeaderBytes..], cancellationToken).ConfigureAwait(false);
        }
        catch (ChunkFileException exception)
        {
            throw new LinkException($"the room pack's {SectionTag} section is not VMF text: {exception.Message}");
        }

        return [.. document.Chunks];
    }
}
