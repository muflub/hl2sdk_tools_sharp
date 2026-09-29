//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What a room's packed file is to its cubemaps.</summary>
internal enum CubemapFileKind : byte
{
    /// <summary>A patched material, <c>materials/maps/&lt;map&gt;/&lt;material&gt;_x_y_z.vmt</c>: its <c>$envmap</c> names a sample's texture.</summary>
    Material = 0,

    /// <summary>A sample's default cubemap copy, <c>materials/maps/&lt;map&gt;/cx_y_z.vtf</c>.</summary>
    Texture = 1,

    /// <summary>The HDR copy of a sample's default cubemap, <c>materials/maps/&lt;map&gt;/cx_y_z.hdr.vtf</c>.</summary>
    HdrTexture = 2,
}

/// <summary>A texdata string of a room's compile that names a cubemap patch.</summary>
/// <param name="Index">The string's entry in the room's texdata string table.</param>
/// <param name="Sample">The sample the patch was made for, as the room's cubemap lump numbers them.</param>
/// <param name="Material">The patched material, as vbsp named it before the map and the position were added.</param>
internal readonly record struct CubemapString(int Index, int Sample, string Material);

/// <summary>A file of a room's pak that is named after one of its cubemap samples.</summary>
/// <param name="Name">The file's name in the room's pak.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Sample">The sample it is named after.</param>
/// <param name="Material">For a patched material, the material it patches; empty for a texture.</param>
internal readonly record struct CubemapFile(string Name, CubemapFileKind Kind, int Sample, string Material);

/// <summary>
/// A room's <c>env_cubemap</c> samples and everything its compile named
/// after them: the samples per quarter turn, and the texdata strings and
/// packed files the link renames to the level's name and positions (the
/// rooms design, 4.10).
/// </summary>
/// <remarks>
/// <para>
/// <b>The naming problem.</b> vbsp names a cubemap's texture after the map
/// and the sample's position, <c>maps/&lt;map&gt;/c&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>,
/// and every specular material it patches for that sample after both too,
/// <c>maps/&lt;map&gt;/&lt;material&gt;_&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>, whose
/// <c>$envmap</c> names the texture. The engine loads a sample's texture
/// under the loaded map's name and the sample's world position, which is
/// also what <c>buildcubemaps</c> writes. A room is compiled under its own
/// name at its own origin, so each of those names has to be rewritten for
/// every placement: the level's name, the placement's position. That is all
/// text, and all known when the room is packed except the placement, so
/// the pack stores what the link needs to do it without reading anything
/// but the pack: the samples, and which strings and files are named after
/// which sample.
/// </para>
/// <para>
/// <b>The samples are the loader's floats, turned.</b> vbsp truncates each
/// sample's origin to whole units (a C cast, toward zero) after reading it,
/// and the flattened level's compile truncates the origin the flatten
/// wrote, which is the room's origin turned and moved as floats. Turning the
/// room's truncated integers instead gives a different answer for an origin
/// off the whole units whose turn negates it (truncating -10.5 is -10, not
/// -11), so the pack stores the origins as the room's loader read them,
/// turned four ways at pack time (the turn only permutes and negates, so
/// nothing rounds), and the link adds the placement's offset with the same
/// float additions <see cref="QuarterTurn.Apply"/> makes for the flatten and
/// truncates, which gives the flattened compile's integers for any origin.
/// Four copies, not one turned at link, is the rooms design's default for
/// placement records (1.1): twelve bytes a sample a turn.
/// </para>
/// <para>
/// <b>Which names are a sample's.</b> A texdata string is a patch when it is
/// <c>maps/&lt;map&gt;/&lt;material&gt;_&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>
/// for the compile's map name and one of its samples' positions, and the
/// room packs that material's patch file: vbsp writes the file whenever it
/// makes the texdata, so an authored material that only looks like a patch
/// is left alone. A packed file is a sample's when its name is a patch
/// file (for a texdata string or not: a patch's dependent material, such as
/// a <c>$bottommaterial</c>, is patched for the sample too and has no
/// texdata) or one of the sample's two default cubemap copies. Positions are
/// matched by their text, as vbsp wrote it, so a material whose own name
/// ends in digits is still split right: the position is the last three
/// numbers. Two samples at one truncated position share their names, and
/// the first is taken, as vbsp's names do.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>CUBE</c>) has the link
/// sections' framing (<see cref="RoomLinkSections"/>: a codec byte, the
/// decoded length, an <c>int32</c> revision). After it, big-endian
/// <c>int32</c>s and UTF-8 strings as the writer spells them: the
/// compile's map name; the sample count, the rotation count (4) and per
/// turn every sample's origin as three little-endian floats; the string
/// count and per string its table entry, its sample and its material; the
/// file count and per file its name, its kind byte, its sample and its
/// material. The sizes are the compile's own lump's, which the pack stores
/// byte for byte, so they are not repeated. Only a room with samples has the
/// section, so a library without cubemaps packs to the same bytes. The tag
/// is one an older build skips, and that build refuses a room with samples
/// by its lump; a room packed before this section existed has none, and the
/// link refuses it by name.
/// </para>
/// </remarks>
internal sealed class RoomCubemaps
{
    /// <summary>The tag of a room's cubemap section.</summary>
    public const string SectionTag = "CUBE";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>How many turns the section stores the samples at.</summary>
    public const int Rotations = 4;

    /// <summary>Where vbsp puts a map's own materials: <c>materials/</c> then the patch name.</summary>
    private const string MaterialsPrefix = "materials/";

    private readonly Vec3[][] _turned;
    private readonly BspData? _bsp;

    private RoomCubemaps(
        string mapBase,
        Vec3[][] turned,
        IReadOnlyList<CubemapString> strings,
        IReadOnlyList<CubemapFile> files,
        BspData? bsp)
    {
        MapBase = mapBase;
        _turned = turned;
        Strings = strings;
        Files = files;
        _bsp = bsp;
    }

    /// <summary>The map name the room was compiled under, which its patch names hold (vbsp's <see cref="VbspContext.MapBase"/>).</summary>
    public string MapBase { get; }

    /// <summary>How many samples the room has.</summary>
    public int SampleCount => _turned[0].Length;

    /// <summary>The texdata strings named after a sample, in table order.</summary>
    public IReadOnlyList<CubemapString> Strings { get; }

    /// <summary>The packed files named after a sample, in the pak's order.</summary>
    public IReadOnlyList<CubemapFile> Files { get; }

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same data bound to a compile.</summary>
    internal RoomCubemaps For(BspData bsp) => new(MapBase, _turned, Strings, Files, bsp);

    /// <summary>The samples' origins turned, not moved: as the room's loader read them, at <paramref name="rotation"/> quarter turns.</summary>
    /// <param name="rotation">The quarter turn, 0 to 3.</param>
    /// <returns>One origin per sample, in lump order.</returns>
    public ReadOnlySpan<Vec3> Turned(int rotation) => _turned[rotation];

    /// <summary>
    /// The samples' linked positions for a placement: each origin turned,
    /// then offset by the placement with the float additions the flatten's
    /// <see cref="QuarterTurn.Apply"/> makes, then truncated as vbsp
    /// truncates a sample's origin (<see cref="CubemapFixups.SampleOrigin"/>).
    /// </summary>
    /// <param name="transform">The placement.</param>
    /// <returns>One position per sample, in lump order.</returns>
    public (int X, int Y, int Z)[] WorldOrigins(RoomTransform transform)
    {
        Vec3 offset = QuarterTurn.Of(transform).Offset;
        ReadOnlySpan<Vec3> turned = _turned[transform.Placement.NormalizedRotation];
        (int X, int Y, int Z)[] world = new (int X, int Y, int Z)[turned.Length];
        for (int i = 0; i < turned.Length; i++)
        {
            world[i] = CubemapFixups.SampleOrigin(turned[i] + offset);
        }

        return world;
    }

    /// <summary>
    /// A room's cubemap data from its compile, or null for a room with no
    /// sample.
    /// </summary>
    /// <param name="bsp">The room's compile.</param>
    /// <param name="samples">
    /// The compile's samples as its loader read them (the context's
    /// <see cref="VbspContext.CubemapSamples"/>), which vbsp wrote into the
    /// lump; the origins must truncate to the lump's.
    /// </param>
    /// <param name="mapBase">The map name the room was compiled under.</param>
    /// <param name="cancellationToken">Cancels the read of the room's pak.</param>
    /// <returns>The data, bound to the compile.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The samples are not the ones the compile's lump holds.</exception>
    /// <exception cref="InvalidZipException">The room's pak is not a zip.</exception>
    public static async Task<RoomCubemaps?> BuildAsync(
        BspData bsp, IReadOnlyList<CubemapSample> samples, string mapBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(mapBase);
        ReadOnlySpan<DCubemapSample> lump = BspStructView.As<DCubemapSample>(bsp[BspLump.Cubemaps]);
        if (lump.Length == 0)
        {
            return null;
        }

        if (samples.Count != lump.Length)
        {
            throw new ArgumentException($"the compile has {samples.Count} cubemap samples, and its lump {lump.Length}.", nameof(samples));
        }

        Vec3[] origins = new Vec3[samples.Count];
        for (int i = 0; i < origins.Length; i++)
        {
            origins[i] = samples[i].Origin;
            if (CubemapFixups.SampleOrigin(origins[i]) != Position(lump[i]))
            {
                throw new ArgumentException($"cubemap sample {i} at {origins[i]} is not its lump's.", nameof(samples));
            }
        }

        Vec3[][] turned = new Vec3[Rotations][];
        for (int r = 0; r < Rotations; r++)
        {
            turned[r] = [.. origins.Select(o => new QuarterTurn(r, Vec3.Zero).Rotate(o))];
        }

        (int X, int Y, int Z)[] positions = new (int X, int Y, int Z)[lump.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = Position(lump[i]);
        }

        ZipArchiveReader? pak = bsp[BspLump.PakFile].Length == 0
            ? null
            : await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data, cancellationToken).ConfigureAwait(false);
        HashSet<string> packed = new(pak?.Entries.Select(e => e.Name) ?? [], StringComparer.Ordinal);

        List<CubemapFile> files = [];
        foreach (ZipEntry entry in pak?.Entries ?? [])
        {
            if (FileOf(entry.Name, mapBase, positions) is { } file)
            {
                files.Add(file);
            }
        }

        List<CubemapString> strings = [];
        ReadOnlySpan<int> table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]);
        ReadOnlySpan<byte> data = bsp[BspLump.TexDataStringData].Data.Span;
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] < 0 || table[i] > data.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> rest = data[table[i]..];
            int end = rest.IndexOf((byte)0);
            string text = Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]);
            if (MaterialOf(text, mapBase, positions) is { } patch && packed.Contains(MaterialFile(text)))
            {
                strings.Add(new CubemapString(i, patch.Sample, patch.Material));
            }
        }

        return new RoomCubemaps(mapBase, turned, strings, files, bsp);
    }

    /// <summary>The pack section holding this data.</summary>
    internal RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.String(MapBase);
        w.Int(SampleCount);
        w.Int(Rotations);
        foreach (Vec3[] turn in _turned)
        {
            w.Structs<Vec3>(turn, counted: false);
        }

        w.Int(Strings.Count);
        foreach (CubemapString s in Strings)
        {
            w.Int(s.Index);
            w.Int(s.Sample);
            w.String(s.Material);
        }

        w.Int(Files.Count);
        foreach (CubemapFile f in Files)
        {
            w.String(f.Name);
            w.Byte((byte)f.Kind);
            w.Int(f.Sample);
            w.String(f.Material);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>A room's cubemap data from its section, bound to its compile; null when absent or of another revision.</summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compile.</param>
    /// <returns>The data, or null.</returns>
    /// <exception cref="LinkException">
    /// The section is damaged or is not this compile's: a sample count or
    /// position other than the compile's lump, a rotation count other than
    /// 4, a turn that is not the first turned, a string entry or sample out of
    /// range, a kind byte past the last, bytes after its end.
    /// </exception>
    internal static RoomCubemaps? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        ReadOnlySpan<DCubemapSample> lump = BspStructView.As<DCubemapSample>(bsp[BspLump.Cubemaps]);
        string mapBase = r.String();
        int samples = r.Count("cubemap samples");
        if (samples != lump.Length)
        {
            throw r.Mismatch($"{samples} cubemap samples; the room has {lump.Length}");
        }

        int rotations = r.Int();
        if (rotations != Rotations)
        {
            throw r.Mismatch($"{rotations} turns of its samples; it stores {Rotations}");
        }

        Vec3[][] turned = new Vec3[Rotations][];
        for (int t = 0; t < Rotations; t++)
        {
            turned[t] = r.Structs<Vec3>("cubemap samples", samples, counted: false);
        }

        for (int i = 0; i < samples; i++)
        {
            if (CubemapFixups.SampleOrigin(turned[0][i]) != Position(lump[i]))
            {
                throw r.Mismatch($"cubemap sample {i} at {turned[0][i]}, not where the room's lump has it");
            }

            for (int t = 1; t < Rotations; t++)
            {
                if (turned[t][i] != new QuarterTurn(t, Vec3.Zero).Rotate(turned[0][i]))
                {
                    throw r.Mismatch($"cubemap sample {i} at turn {t} not turned from turn 0");
                }
            }
        }

        int tableEntries = BspStructView.Count<int>(bsp[BspLump.TexDataStringTable]);
        int stringCount = r.Count("cubemap strings");
        List<CubemapString> strings = new(stringCount);
        for (int i = 0; i < stringCount; i++)
        {
            int index = r.Int();
            if ((uint)index >= (uint)tableEntries)
            {
                throw r.Mismatch($"a cubemap string naming table entry {index}; the room has {tableEntries}");
            }

            strings.Add(new CubemapString(index, Sample(r, samples), r.String()));
        }

        int fileCount = r.Count("cubemap files");
        List<CubemapFile> files = new(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            string name = r.String();
            CubemapFileKind kind = (CubemapFileKind)r.Small((int)CubemapFileKind.HdrTexture, "a cubemap file kind of");
            files.Add(new CubemapFile(name, kind, Sample(r, samples), r.String()));
        }

        r.End();
        return new RoomCubemaps(mapBase, turned, strings, files, bsp);

        static int Sample(RoomLinkSections.Reader r, int samples)
        {
            int sample = r.Int();
            return (uint)sample < (uint)samples ? sample : throw r.Mismatch($"a cubemap name of sample {sample}; the room has {samples}");
        }
    }

    /// <summary>
    /// A sample's texture name, <c>maps/&lt;map&gt;/c&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>,
    /// as vbsp names it (<see cref="CubemapFixups.PatchedName"/> with <c>c</c>).
    /// </summary>
    /// <param name="mapBase">The map name.</param>
    /// <param name="at">The sample's truncated position.</param>
    /// <returns>The name.</returns>
    internal static string TextureName(string mapBase, (int X, int Y, int Z) at) =>
        CubemapFixups.PatchedName("c", mapBase, at, isMaterialName: false);

    /// <summary>
    /// A patch's material name,
    /// <c>maps/&lt;map&gt;/&lt;material&gt;_&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>,
    /// spelled as vbsp spells it but without its length check, which the
    /// link makes with its own message.
    /// </summary>
    /// <param name="material">The patched material.</param>
    /// <param name="mapBase">The map name.</param>
    /// <param name="at">The sample's truncated position.</param>
    /// <returns>The name.</returns>
    internal static string PatchName(string material, string mapBase, (int X, int Y, int Z) at) =>
#pragma warning disable CA1308 // vbsp lower-cases its patch names
        string.Create(CultureInfo.InvariantCulture, $"maps/{mapBase}/{material}_{at.X}_{at.Y}_{at.Z}").Replace('\\', '/').ToLowerInvariant();
#pragma warning restore CA1308

    /// <summary>A patch material's file in the pak: <c>materials/</c>, the name, <c>.vmt</c>.</summary>
    internal static string MaterialFile(string patch) => MaterialsPrefix + patch + ".vmt";

    /// <summary>A sample texture's file in the pak, LDR or HDR (<see cref="DefaultCubemapBuilder.HdrName"/>).</summary>
    internal static string TextureFile(string texture, bool hdr) =>
        DefaultCubemapBuilder.HdrName(MaterialsPrefix + texture + ".vtf", hdr);

    private static (int X, int Y, int Z) Position(DCubemapSample sample) => (sample.Origin[0], sample.Origin[1], sample.Origin[2]);

    /// <summary>The patch a name is (its sample and material) when it is one of <paramref name="mapBase"/>'s at one of the positions; else null.</summary>
    private static (int Sample, string Material)? MaterialOf(string name, string mapBase, (int X, int Y, int Z)[] positions)
    {
#pragma warning disable CA1308 // vbsp lower-cases its patch names
        string prefix = $"maps/{mapBase}/".ToLowerInvariant();
#pragma warning restore CA1308
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        for (int i = 0; i < positions.Length; i++)
        {
            string suffix = string.Create(CultureInfo.InvariantCulture, $"_{positions[i].X}_{positions[i].Y}_{positions[i].Z}");
            if (name.Length > prefix.Length + suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (i, name[prefix.Length..^suffix.Length]);
            }
        }

        return null;
    }

    /// <summary>What a packed file is to the samples, or null when it is none of theirs.</summary>
    private static CubemapFile? FileOf(string file, string mapBase, (int X, int Y, int Z)[] positions)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            string texture = TextureName(mapBase, positions[i]);
            if (file == TextureFile(texture, hdr: false))
            {
                return new CubemapFile(file, CubemapFileKind.Texture, i, string.Empty);
            }

            if (file == TextureFile(texture, hdr: true))
            {
                return new CubemapFile(file, CubemapFileKind.HdrTexture, i, string.Empty);
            }
        }

        const string extension = ".vmt";
        if (file.StartsWith(MaterialsPrefix, StringComparison.Ordinal) && file.EndsWith(extension, StringComparison.Ordinal)
            && MaterialOf(file[MaterialsPrefix.Length..^extension.Length], mapBase, positions) is { } patch)
        {
            return new CubemapFile(file, CubemapFileKind.Material, patch.Sample, patch.Material);
        }

        return null;
    }
}
