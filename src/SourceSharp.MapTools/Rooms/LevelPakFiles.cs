//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The linked level's pak file: every placed room's packed files, merged
/// into one archive by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a room packs.</b> A room is compiled by vbsp like any map, and
/// vbsp packs what it makes for the map into the map's pak: the default
/// cubemaps built from the sky's textures
/// (<c>materials/maps/&lt;map&gt;/cubemapdefault.vtf</c> and its
/// <c>.hdr.vtf</c>), patched materials (water depth,
/// <c>_wvt_patch</c>), the cubemap patches and copies named after each
/// <c>env_cubemap</c> sample, and static prop <c>.vhv</c> files. A patched material's
/// name holds the compile's map name, which for a room is the room's own
/// (<see cref="Bsp.VbspContext.MapBase"/>, the room name lower-cased). The
/// pack stores each room's pak as vbsp wrote it, byte for byte, inside the
/// room's container, so the link needs nothing but the pack (decision D1 of
/// the rooms design).
/// </para>
/// <para>
/// <b>Merged by name.</b> The link writes one archive holding every file of
/// every room the level places. A name two rooms (or two placements of one
/// room) both pack is written once when the bytes agree, and refused,
/// naming both rooms, when they do not: the engine reads one file per name,
/// so one of the two rooms would silently get the other's.
/// </para>
/// <para>
/// <b>Kept or renamed.</b> A file named after the room stays under the
/// room's name. Its placements share it, which is right for bytes that do
/// not depend on where the room is placed (water depth, WVT patches), and
/// the room's texdata names it under that name, so renaming it would break
/// the reference. What the engine looks up under the <em>map's</em> name is
/// renamed to the level's: today that is the default cubemap pair, which
/// the engine reads as <c>materials/maps/&lt;loaded map&gt;/cubemapdefault</c>
/// for every reflective surface of a map with no cubemap sample. Every room
/// of a library builds it from the library's one sky (decision D3), so the
/// rooms' copies are the same bytes and merge into one; the level's name
/// is the one a vbsp compile of the flattened level writes it under, so the
/// linked map and the flattened one hold the same files after renaming.
/// The cubemap samples' files, whose names hold the map's name and the
/// sample's world position (a patched specular material and the sample's
/// default cubemap copies), are written once per placement of their room,
/// under the level's name and the placement's position, a patched
/// material's text renamed to match (<see cref="PlacementCubemaps"/>); none
/// is kept under the room's name, where nothing would read it.
/// </para>
/// <para>
/// <b>Static prop lighting.</b> vrad names each static prop's vertex
/// lighting by the prop's index in the map's lump (<c>sp_N.vhv</c>, and
/// <c>sp_hdr_N.vhv</c> for HDR), and the engine finds a prop's file by
/// that index. A linked prop has a new index, and a room placed twice has
/// each prop twice, so a room's lighting file is written once per kept
/// placement of its prop, under the prop's linked index; the file of a
/// prop the level drops (<c>room_needs</c>, socket furniture) is left out.
/// The bytes are the room's: vertex lighting is stored in the model's
/// vertex order, which a turn does not move (the rooms design, 1.1).
/// </para>
/// <para>
/// <b>Deterministic.</b> The archive is written by
/// <see cref="ZipArchiveWriter"/>, which stores (never compresses) and
/// writes no timestamp, with its entries in ordinal order of their linked
/// names, so the pak is a function of the set of files the level's rooms
/// pack: not of the thread count, the layout's order or which room is
/// placed first. Each entry's bytes, compression method and checksum are
/// carried as the room's pak held them.
/// </para>
/// <para>
/// <b>Nothing packed, nothing changed.</b> A level none of whose rooms packs
/// a file carries its first room's (empty) pak lump byte for byte, as the
/// link always has, so its bytes do not move.
/// </para>
/// </remarks>
public static class LevelPakFiles
{
    /// <summary>The directory, under <c>materials/</c>, that vbsp names map-specific files in: <c>maps/&lt;map&gt;/</c>.</summary>
    public const string MapMaterialsDirectory = "materials/maps/";

    /// <summary>The default cubemap's file name, under the map's directory.</summary>
    public const string DefaultCubemap = "cubemapdefault.vtf";

    /// <summary>The HDR default cubemap's file name, under the map's directory.</summary>
    public const string DefaultCubemapHdr = "cubemapdefault.hdr.vtf";

    /// <summary>
    /// The name a room's packed file takes in the linked level: the level's
    /// default cubemap for the room's, else the file's own name.
    /// </summary>
    /// <param name="file">The file's name in the room's pak (lower-cased, as every pak name is).</param>
    /// <param name="room">The room's name, as the library spells it.</param>
    /// <param name="mapBase">
    /// The linked map's name, without directory or extension and lower-cased
    /// (the name vbsp's <see cref="Bsp.VbspContext.MapBase"/> would be);
    /// only read when the file is renamed.
    /// </param>
    /// <returns>The linked name; a renamed one lower-cased, as every pak name is.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string LinkedName(string file, string room, string mapBase)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(mapBase);
        // Lower-cased as every pak name is (vbsp lower-cases its map name
        // and each name it packs), so the name compares as the engine's
        // lookup, which lower-cases too, reads it.
#pragma warning disable CA1308 // pak names are lower case
        return RenamedLeaf(file, room) is { } leaf ? (MapMaterialsDirectory + mapBase + "/" + leaf).ToLowerInvariant() : file;
#pragma warning restore CA1308
    }

    /// <summary>
    /// Whether the link renames a room's packed file to the level's name
    /// (<see cref="LinkedName"/>): the room's default cubemap pair.
    /// </summary>
    /// <param name="file">The file's name in the room's pak.</param>
    /// <param name="room">The room's name.</param>
    /// <returns>True when the file is renamed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsRenamed(string file, string room)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(room);
        return RenamedLeaf(file, room) is not null;
    }

    /// <summary>
    /// Merges the paks of the rooms a level places.
    /// </summary>
    /// <param name="rooms">
    /// Each distinct room the level places, in the order it first places
    /// them, with its parsed pak; a room placed twice is listed once, since
    /// its placements pack the same bytes.
    /// </param>
    /// <param name="mapBase">
    /// The linked map's name (<see cref="LinkedName"/>), or empty when the
    /// caller does not know it: then a room that packs a file the link would
    /// rename is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>
    /// The merged pak lump's bytes, and how many files it holds; null bytes
    /// when no room packs a file, so the caller keeps the lump it has.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">
    /// Two rooms pack one name with different bytes (the rooms design's
    /// message: <c>rooms {a} and {b} both pack {file} with different bytes.</c>),
    /// one room's pak holds a name twice with different bytes, or a file is
    /// to be renamed to a map name nobody gave.
    /// </exception>
    internal static (byte[]? Pak, int Files) Merge(
        IReadOnlyList<(string Room, ZipArchiveReader Pak)> rooms, string mapBase, CancellationToken cancellationToken) =>
        Merge(rooms, mapBase, propFiles: null, cancellationToken);

    /// <summary>
    /// Merges the paks of the rooms a level places, renaming the rooms'
    /// static prop lighting files to the props' linked indices.
    /// </summary>
    /// <param name="rooms">As for <see cref="Merge(IReadOnlyList{ValueTuple{string, ZipArchiveReader}}, string, CancellationToken)"/>.</param>
    /// <param name="mapBase">As for the overload without props.</param>
    /// <param name="propFiles">
    /// Per room with static props, every (room prop, linked prop) pair of
    /// the level (<see cref="LevelLinker.PlanProps"/>), or null when no
    /// placed room has static props: a room listed here has each of its
    /// <c>sp_N.vhv</c> and <c>sp_hdr_N.vhv</c> written once per pair of its
    /// prop N, under the linked index, and not at all when the level keeps
    /// no placement of that prop. A room not listed keeps its files' names.
    /// </param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>As for the overload without props.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rooms"/> or <paramref name="mapBase"/> is null.</exception>
    /// <exception cref="LinkException">As for the overload without props.</exception>
    internal static (byte[]? Pak, int Files) Merge(
        IReadOnlyList<(string Room, ZipArchiveReader Pak)> rooms,
        string mapBase,
        IReadOnlyDictionary<string, IReadOnlyList<(int RoomProp, int Linked)>>? propFiles,
        CancellationToken cancellationToken) =>
        Merge(rooms, mapBase, propFiles, cubemaps: null, cancellationToken);

    /// <summary>
    /// Merges the paks of the rooms a level places, renaming the rooms'
    /// static prop lighting files to the props' linked indices and their
    /// cubemap files to the level's name and each placement's positions.
    /// </summary>
    /// <param name="rooms">As for the overload without props.</param>
    /// <param name="mapBase">As for the overload without props.</param>
    /// <param name="propFiles">As for the overload without cubemaps.</param>
    /// <param name="cubemaps">
    /// Per room with <c>env_cubemap</c> samples, its placements' cubemaps in
    /// link order (<see cref="LevelCubemaps.ByRoom"/>), or null when no
    /// placed room has one: each of such a room's files named after a
    /// sample (a patched material, a sample's default cubemap copy) is
    /// written once per placement under the name the placement gives it
    /// (<see cref="PlacementCubemaps.LinkedEntry"/>), and never under the
    /// room's.
    /// </param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>As for the overload without props.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rooms"/> or <paramref name="mapBase"/> is null.</exception>
    /// <exception cref="LinkException">
    /// As for the overload without props; and a patched material packed
    /// compressed, whose text the link cannot rewrite.
    /// </exception>
    internal static (byte[]? Pak, int Files) Merge(
        IReadOnlyList<(string Room, ZipArchiveReader Pak)> rooms,
        string mapBase,
        IReadOnlyDictionary<string, IReadOnlyList<(int RoomProp, int Linked)>>? propFiles,
        IReadOnlyDictionary<string, IReadOnlyList<PlacementCubemaps>>? cubemaps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(mapBase);
        Dictionary<string, (string Room, ZipEntry Entry)> merged = new(StringComparer.Ordinal);
        void Add(string room, string name, ZipEntry entry)
        {
            if (!merged.TryGetValue(name, out (string Room, ZipEntry Entry) held))
            {
                merged.Add(name, (room, name == entry.Name ? entry : Renamed(entry, name)));
                return;
            }

            if (!SameBytes(held.Entry, entry))
            {
                throw new LinkException(
                    string.Equals(held.Room, room, StringComparison.Ordinal)
                        ? $"room {room} packs {name} twice with different bytes."
                        : $"rooms {held.Room} and {room} both pack {name} with different bytes.");
            }
        }

        foreach ((string room, ZipArchiveReader pak) in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<(int RoomProp, int Linked)>? props = null;
            _ = propFiles?.TryGetValue(room, out props);
            IReadOnlyList<PlacementCubemaps>? placed = null;
            _ = cubemaps?.TryGetValue(room, out placed);
            foreach (ZipEntry entry in pak.Entries)
            {
                // The writer drops an empty entry (vbsp's does too), so an
                // empty file is not a file of the level either.
                if (entry.Data.Length == 0)
                {
                    continue;
                }

                if (props is not null && PropLightingFile(entry.Name) is { } lighting)
                {
                    foreach ((int roomProp, int linked) in props)
                    {
                        if (roomProp == lighting.Prop)
                        {
                            Add(room, StaticPropLighting.FileName(linked, lighting.Hdr), entry);
                        }
                    }

                    continue;
                }

                if (placed is { Count: > 0 } && placed[0].FileNamed(entry.Name) is { } cubemapFile)
                {
                    RefuseWithoutMapName(room, entry.Name, mapBase);
                    foreach (PlacementCubemaps placement in placed)
                    {
                        ZipEntry linked = placement.LinkedEntry(entry, cubemapFile, room);
                        Add(room, linked.Name, linked);
                    }

                    continue;
                }

                if (IsRenamed(entry.Name, room))
                {
                    RefuseWithoutMapName(room, entry.Name, mapBase);
                }

                Add(room, LinkedName(entry.Name, room, mapBase), entry);
            }
        }

        if (merged.Count == 0)
        {
            return (null, 0);
        }

        ZipArchiveWriter writer = new();
        foreach (string name in merged.Keys.Order(StringComparer.Ordinal))
        {
            writer.Add(merged[name].Entry);
        }

        return (writer.ToBytes(), merged.Count);
    }

    /// <summary>
    /// Refuses a file the link renames to the level's map name when it was
    /// given none, rather than carrying it where nothing reads it.
    /// </summary>
    private static void RefuseWithoutMapName(string room, string file, string mapBase)
    {
        if (mapBase.Length == 0)
        {
            throw new LinkException(
                $"room {room} packs {file}, which is named after its map; the link renames it to the level's map name,"
                + " and was given none (the map name of the link's compile context).");
        }
    }

    /// <summary>The file name under the map's directory when <paramref name="file"/> is the room's default cubemap, else null.</summary>
    private static string? RenamedLeaf(string file, string room)
    {
        // "materials/maps/" + room + "/" + leaf, the room compared ignoring
        // case: vbsp lower-cases its map name and every pak name.
        int roomStart = MapMaterialsDirectory.Length;
        int leafStart = roomStart + room.Length + 1;
        if (file.Length <= leafStart
            || !file.StartsWith(MapMaterialsDirectory, StringComparison.Ordinal)
            || string.Compare(file, roomStart, room, 0, room.Length, StringComparison.OrdinalIgnoreCase) != 0
            || file[leafStart - 1] != '/')
        {
            return null;
        }

        string leaf = file[leafStart..];
        return leaf is DefaultCubemap or DefaultCubemapHdr ? leaf : null;
    }

    /// <summary>
    /// The prop a static prop lighting file lights and whether it is the
    /// HDR one, when <paramref name="file"/> is named as vrad names them
    /// (<see cref="StaticPropLighting.FileName"/>: at the pak's root, the
    /// index in decimal without leading zeros); else null.
    /// </summary>
    /// <param name="file">A pak entry's name.</param>
    /// <returns>The prop index and HDR flag, or null.</returns>
    internal static (int Prop, bool Hdr)? PropLightingFile(string file)
    {
        const string prefix = "sp_";
        const string hdr = "hdr_";
        const string extension = ".vhv";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(extension, StringComparison.Ordinal))
        {
            return null;
        }

        string middle = file[prefix.Length..^extension.Length];
        bool isHdr = middle.StartsWith(hdr, StringComparison.Ordinal);
        string digits = isHdr ? middle[hdr.Length..] : middle;
        return digits.Length > 0
            && digits.All(char.IsAsciiDigit)
            && (digits.Length == 1 || digits[0] != '0')
            && int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prop)
            ? (prop, isHdr)
            : null;
    }

    private static ZipEntry Renamed(ZipEntry entry, string name) =>
        new(name, entry.Data, entry.CompressionMethod, entry.Crc, entry.UncompressedSize);

    private static bool SameBytes(ZipEntry a, ZipEntry b) =>
        a.CompressionMethod == b.CompressionMethod
        && a.Crc == b.Crc
        && a.UncompressedSize == b.UncompressedSize
        && a.Data.AsSpan().SequenceEqual(b.Data);
}
