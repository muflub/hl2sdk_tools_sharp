//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.Write;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One placement's cubemaps as the level links them: its room's samples at
/// their linked positions, and every name the room's compile made after a
/// sample renamed to the level's map name and the sample's linked position.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is renamed.</b> The room's patched texdata strings
/// (<c>maps/&lt;room&gt;/&lt;material&gt;_x_y_z</c> becomes
/// <c>maps/&lt;level&gt;/&lt;material&gt;_X_Y_Z</c>), its patched material
/// files with them, and its samples' default cubemap copies
/// (<c>maps/&lt;room&gt;/cx_y_z</c> becomes <c>maps/&lt;level&gt;/cX_Y_Z</c>):
/// exactly the names a vbsp compile of the flattened level gives the same
/// sample and material, since vbsp spells both from the map's name and the
/// sample's truncated position (<see cref="CubemapFixups.PatchedName"/>).
/// Inside a patched material, a quoted value that is one of the sample's
/// old names (the texture its <c>$envmap</c> names, or a dependent
/// material's patch, as a <c>$bottommaterial</c>) takes the new name; the
/// rest of the file, its <c>include</c> of the original material and its
/// layout, is the room's, which is what vbsp writes for the same material
/// in any map, so the file is the flattened compile's byte for byte. The
/// values are replaced in one pass over the file's quoted strings, so a
/// new name that happens to spell another sample's old one (a level named
/// as its room, a placement at the room's own origin) is never renamed
/// twice.
/// </para>
/// <para>
/// <b>Which sample a face uses</b> is the room's own compile's: every
/// specular face of a room names a sample of that room, whichever is nearest
/// in the room (open point O9's default). The flattened compile picks the
/// nearest sample in the whole level, which near a door can be the
/// neighbour's; that is the one difference the rooms design accepts.
/// </para>
/// <para>
/// <b>Texdata.</b> A placement's renamed strings are its own, so a room
/// with patches placed twice brings its patched materials twice, once per
/// position: every (specular material, sample, placement) is its own
/// texdata and texinfo family, which the shared tables count against
/// <c>MAX_MAP_TEXDATA</c> and <c>MAX_MAP_TEXINFO</c> as they count any.
/// </para>
/// </remarks>
internal sealed class PlacementCubemaps
{
    private readonly (int X, int Y, int Z)[] _world;
    private readonly Dictionary<string, string>[] _renames;
    private readonly Dictionary<string, CubemapFile> _files;

    private PlacementCubemaps(
        string roomName,
        RoomCubemaps room,
        BspData bsp,
        string mapBase,
        (int X, int Y, int Z)[] world,
        Dictionary<int, string> strings,
        Dictionary<string, string>[] renames)
    {
        RoomName = roomName;
        Room = room;
        Bsp = bsp;
        MapBase = mapBase;
        _world = world;
        Strings = strings;
        _renames = renames;
        _files = room.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>The placed room's name, as the library spells it.</summary>
    public string RoomName { get; }

    /// <summary>The room's cubemap data.</summary>
    public RoomCubemaps Room { get; }

    /// <summary>The room's compile, whose cubemap lump holds the samples' sizes.</summary>
    public BspData Bsp { get; }

    /// <summary>The level's map name the names take.</summary>
    public string MapBase { get; }

    /// <summary>The samples' linked positions, in the room's lump order.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> World => _world;

    /// <summary>The room's texdata strings this placement renames: per string-table entry, its linked name.</summary>
    public IReadOnlyDictionary<int, string> Strings { get; }

    /// <summary>
    /// One placement's cubemaps, or null when its room has no sample.
    /// </summary>
    /// <param name="room">The placed room.</param>
    /// <param name="transform">The placement.</param>
    /// <param name="mapBase">The level's map name.</param>
    /// <returns>The placement's cubemaps, or null.</returns>
    /// <exception cref="LinkException">
    /// The room has samples and no cubemap data from its compile, or a
    /// renamed patch is longer than vbsp names one.
    /// </exception>
    public static PlacementCubemaps? Make(RoomObject room, RoomTransform transform, string mapBase)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(mapBase);
        int lump = BspStructView.Count<DCubemapSample>(room.Bsp[BspLump.Cubemaps]);
        if (lump == 0)
        {
            return null;
        }

        string name = room.Definition.Name;
        if (room.CubemapsOfCompile is not { } cubemaps)
        {
            throw new LinkException(
                $"room {name} has {lump} cubemap samples but no cubemap data from its compile"
                + " (a pack written before the link carried cubemaps, or a room built without ssmap room);"
                + " recompile the library with ssmap room.");
        }

        (int X, int Y, int Z)[] world = cubemaps.WorldOrigins(transform);
        ReadOnlySpan<DCubemapSample> samples = BspStructView.As<DCubemapSample>(room.Bsp[BspLump.Cubemaps]);
        string Patch(string material, int sample)
        {
            string patch = RoomCubemaps.PatchName(material, mapBase, world[sample]);
            if (patch.Length >= CubemapFixups.TextureNameLength - 1)
            {
                throw new LinkException(
                    $"room {name} at cell ({transform.Placement.CellX}, {transform.Placement.CellY}): the cubemap patch {patch} is"
                    + $" {patch.Length} characters long; vbsp names a patch in fewer than {CubemapFixups.TextureNameLength - 1}.");
            }

            return patch;
        }

        Dictionary<int, string> strings = [];
        foreach (CubemapString s in cubemaps.Strings)
        {
            strings[s.Index] = Patch(s.Material, s.Sample);
        }

        // Per sample, what its patched materials' text renames: its texture
        // and every patch the room packs for it (a dependent's among them).
        Dictionary<string, string>[] renames = new Dictionary<string, string>[world.Length];
        for (int i = 0; i < world.Length; i++)
        {
            renames[i] = new(StringComparer.Ordinal)
            {
                [RoomCubemaps.TextureName(cubemaps.MapBase, (samples[i].Origin[0], samples[i].Origin[1], samples[i].Origin[2]))] =
                    RoomCubemaps.TextureName(mapBase, world[i]),
            };
        }

        foreach (CubemapFile file in cubemaps.Files)
        {
            if (file.Kind == CubemapFileKind.Material)
            {
                renames[file.Sample][PatchOf(file)] = Patch(file.Material, file.Sample);
            }
        }

        return new PlacementCubemaps(name, cubemaps, room.Bsp, mapBase, world, strings, renames);
    }

    /// <summary>The room's file of that name when it is one of its cubemaps', else null.</summary>
    /// <param name="name">A file name in the room's pak.</param>
    /// <returns>What the file is to the samples, or null.</returns>
    public CubemapFile? FileNamed(string name) => _files.TryGetValue(name, out CubemapFile file) ? file : null;

    /// <summary>The name a room's cubemap file takes in the level at this placement.</summary>
    /// <param name="file">The file.</param>
    /// <returns>Its linked name.</returns>
    public string LinkedName(CubemapFile file) => file.Kind switch
    {
        CubemapFileKind.Material => RoomCubemaps.MaterialFile(_renames[file.Sample][PatchOf(file)]),
        _ => RoomCubemaps.TextureFile(RoomCubemaps.TextureName(MapBase, _world[file.Sample]), file.Kind == CubemapFileKind.HdrTexture),
    };

    /// <summary>
    /// A room's cubemap file as the level packs it at this placement: a
    /// texture's bytes as they are, a patched material's with every quoted
    /// value that is one of its sample's names renamed.
    /// </summary>
    /// <param name="entry">The room's entry.</param>
    /// <param name="file">What the entry is to the samples.</param>
    /// <param name="room">The room's name, for the message.</param>
    /// <returns>The linked entry, under its linked name.</returns>
    /// <exception cref="LinkException">A patched material is packed compressed, so its text cannot be rewritten.</exception>
    public ZipEntry LinkedEntry(ZipEntry entry, CubemapFile file, string room)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = LinkedName(file);
        if (file.Kind != CubemapFileKind.Material)
        {
            return new ZipEntry(name, entry.Data, entry.CompressionMethod, entry.Crc, entry.UncompressedSize);
        }

        if (!entry.IsStored)
        {
            throw new LinkException(
                $"room {room} packs {entry.Name} compressed; the link renames the cubemap names inside a patched material and reads only stored files.");
        }

        return new ZipEntry(name, Rename(entry.Data, _renames[file.Sample]));
    }

    /// <summary>
    /// The level's cubemap samples, this placement's: the room's records
    /// (their sizes and zero padding) at the linked positions.
    /// </summary>
    /// <returns>One record per sample, in the room's order.</returns>
    public DCubemapSample[] Samples()
    {
        DCubemapSample[] records = BspStructView.As<DCubemapSample>(Bsp[BspLump.Cubemaps]).ToArray();
        for (int i = 0; i < records.Length; i++)
        {
            records[i].Origin[0] = _world[i].X;
            records[i].Origin[1] = _world[i].Y;
            records[i].Origin[2] = _world[i].Z;
        }

        return records;
    }

    /// <summary>
    /// A text with every double-quoted value found in <paramref name="renames"/>
    /// replaced, in one pass: a quote opens a value and the next quote not
    /// escaped by a backslash closes it, as the KeyValues writer escapes
    /// one; anything else is copied as it is.
    /// </summary>
    internal static byte[] Rename(byte[] text, IReadOnlyDictionary<string, string> renames)
    {
        string source = Encoding.Latin1.GetString(text);
        StringBuilder result = new(source.Length);
        int at = 0;
        while (at < source.Length)
        {
            int open = source.IndexOf('"', at);
            if (open < 0)
            {
                break;
            }

            int close = open + 1;
            while (close < source.Length && (source[close] != '"' || source[close - 1] == '\\'))
            {
                close++;
            }

            if (close >= source.Length)
            {
                break;
            }

            string value = source[(open + 1)..close];
            result.Append(source, at, open + 1 - at).Append(renames.TryGetValue(value, out string? renamed) ? renamed : value).Append('"');
            at = close + 1;
        }

        result.Append(source, at, source.Length - at);
        return Encoding.Latin1.GetBytes(result.ToString());
    }

    /// <summary>The patch name a patched material's file is named after: its name without <c>materials/</c> and <c>.vmt</c>.</summary>
    private static string PatchOf(CubemapFile file) => file.Name["materials/".Length..^".vmt".Length];
}

/// <summary>
/// A level's cubemaps: per placement in link order, its room's samples at
/// their linked positions and the names renamed for them
/// (<see cref="PlacementCubemaps"/>); the level's cubemap lump is every
/// placement's samples in that order, which is the order the flattened
/// level lists its <c>env_cubemap</c>s in (rooms in layout order, each
/// room's entities in its own order), so the two maps' lumps are the same.
/// </summary>
internal sealed class LevelCubemaps
{
    private readonly PlacementCubemaps?[] _placements;

    private LevelCubemaps(PlacementCubemaps?[] placements)
    {
        _placements = placements;
    }

    /// <summary>How many samples the level carries.</summary>
    public int SampleCount => _placements.Sum(p => p?.World.Count ?? 0);

    /// <summary>A placement's cubemaps, or null when its room has none.</summary>
    /// <param name="placement">The placement's index in link order.</param>
    /// <returns>Its cubemaps, or null.</returns>
    public PlacementCubemaps? At(int placement) => _placements[placement];

    /// <summary>
    /// Per room with samples, its placements' cubemaps in link order: how
    /// the pak merge writes a room's cubemap files once per placement
    /// (<see cref="LevelPakFiles"/>).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PlacementCubemaps>> ByRoom()
    {
        Dictionary<string, List<PlacementCubemaps>> rooms = new(StringComparer.Ordinal);
        foreach (PlacementCubemaps? placement in _placements)
        {
            if (placement is null)
            {
                continue;
            }

            if (!rooms.TryGetValue(placement.RoomName, out List<PlacementCubemaps>? list))
            {
                rooms[placement.RoomName] = list = [];
            }

            list.Add(placement);
        }

        return rooms.ToDictionary(r => r.Key, r => (IReadOnlyList<PlacementCubemaps>)r.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// A level's cubemaps, or null when no placed room has a sample, so a
    /// level without cubemaps links exactly as before.
    /// </summary>
    /// <param name="placements">The placements in link order: each placed room and its transform.</param>
    /// <param name="mapBase">The level's map name.</param>
    /// <returns>The level's cubemaps, or null.</returns>
    /// <exception cref="LinkException">As <see cref="PlacementCubemaps.Make"/>; and a level past <see cref="WriteLimits.MaxMapCubemapSamples"/>, naming the placement that crossed it.</exception>
    public static LevelCubemaps? Plan(IReadOnlyList<(RoomObject Room, RoomTransform Transform)> placements, string mapBase)
    {
        ArgumentNullException.ThrowIfNull(placements);
        PlacementCubemaps?[] planned = new PlacementCubemaps?[placements.Count];
        bool any = false;
        int total = 0;
        for (int i = 0; i < planned.Length; i++)
        {
            (RoomObject room, RoomTransform transform) = placements[i];
            planned[i] = PlacementCubemaps.Make(room, transform, mapBase);
            if (planned[i] is { } placement)
            {
                any = true;
                total += placement.World.Count;
                if (total > WriteLimits.MaxMapCubemapSamples)
                {
                    throw new LinkException(
                        $"room {room.Definition.Name} at cell ({transform.Placement.CellX}, {transform.Placement.CellY}) pushes the link to"
                        + $" {total} cubemap samples; vbsp writes at most {WriteLimits.MaxMapCubemapSamples} (MAX_MAP_CUBEMAPSAMPLES).");
                }
            }
        }

        return any ? new LevelCubemaps(planned) : null;
    }

    /// <summary>The level's cubemap lump: every placement's samples, in link order.</summary>
    public byte[] Lump()
    {
        List<DCubemapSample> records = [];
        foreach (PlacementCubemaps? placement in _placements)
        {
            if (placement is not null)
            {
                records.AddRange(placement.Samples());
            }
        }

        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(records)).ToArray();
    }
}
