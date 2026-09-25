using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// The lumps with no floating-point freedom, compared as
/// <see cref="DiffKind.Exact"/>.
/// </summary>
/// <remarks>
/// plan_maptools.md 2 names them: the entity text, TEXDATA and its two string
/// lumps, TEXINFO, BRUSHES and BRUSHSIDES, the MODELS count, the game-lump
/// dictionaries and the pak file list. Nothing in any of these is produced by a
/// float reduction whose order a thread count can change, so a difference here
/// is a difference in the map.
/// </remarks>
internal static class ExactLumpDiff
{
    /// <summary>The entity lump, compared entity by entity and pair by pair.</summary>
    internal static void Entities(BspData a, BspData b, LumpDiffBuilder into)
    {
        List<BspEntity> ea;
        List<BspEntity> eb;
        try
        {
            ea = EntityLump.Parse(a[BspLump.Entities]);
            eb = EntityLump.Parse(b[BspLump.Entities]);
        }
        catch (InvalidBspException error)
        {
            // A lump that does not parse is compared as bytes rather than not at
            // all: an instrument that returns "no differences" because it could
            // not read the input is the check that cannot fail.
            into.Note = $"the entity text did not parse ({error.Message}); compared as raw bytes";
            if (!into.BytesIdentical)
            {
                into.Add("ENTITIES", $"{into.LengthA} bytes", $"{into.LengthB} bytes");
            }

            return;
        }

        if (ea.Count != eb.Count)
        {
            into.Add(
                "entity count",
                ea.Count.ToString(CultureInfo.InvariantCulture),
                eb.Count.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(ea.Count, eb.Count);
        for (int i = 0; i < shared && !into.Saturated; i++)
        {
            ComparePairs(i, ea[i], eb[i], into);
        }
    }

    private static void ComparePairs(int index, BspEntity a, BspEntity b, LumpDiffBuilder into)
    {
        string who = $"entity {index.ToString(CultureInfo.InvariantCulture)} \"{a.ClassName}\"";

        if (a.Pairs.Count != b.Pairs.Count)
        {
            into.Add(
                $"{who} key count",
                a.Pairs.Count.ToString(CultureInfo.InvariantCulture),
                b.Pairs.Count.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(a.Pairs.Count, b.Pairs.Count);
        for (int p = 0; p < shared; p++)
        {
            BspKeyValue pa = a.Pairs[p];
            BspKeyValue pb = b.Pairs[p];
            if (string.Equals(pa.Key, pb.Key, StringComparison.Ordinal)
                && string.Equals(pa.Value, pb.Value, StringComparison.Ordinal))
            {
                continue;
            }

            into.Add(
                $"{who} pair {p.ToString(CultureInfo.InvariantCulture)}",
                $"\"{pa.Key}\" \"{pa.Value}\"",
                $"\"{pb.Key}\" \"{pb.Value}\"");
        }
    }

    /// <summary>
    /// An array lump, element by element, reporting the index and the bytes.
    /// </summary>
    /// <typeparam name="T">The lump's element struct.</typeparam>
    /// <param name="a">Map A's lump.</param>
    /// <param name="b">Map B's lump.</param>
    /// <param name="name">The lump's name, for the report.</param>
    /// <param name="into">The verdict being built.</param>
    /// <remarks>
    /// The element is reported as hex rather than as named fields. That is a
    /// deliberate limit of this comparer and not laziness: a struct-by-struct
    /// field renderer would need a reflection walk over every lump struct, and
    /// the question this kind answers is "is it the same", to which the index
    /// and the bytes are a complete answer.
    /// </remarks>
    internal static void Elements<T>(BspLumpData a, BspLumpData b, string name, LumpDiffBuilder into)
        where T : unmanaged
    {
        if (!BspStructView.Fits<T>(a) || !BspStructView.Fits<T>(b))
        {
            into.Note =
                $"a length that is not a whole number of {typeof(T).Name} "
                + $"({Unsafe.SizeOf<T>()} bytes); compared as raw bytes";
            Bytes(a, b, name, into);
            return;
        }

        ReadOnlySpan<T> sa = BspStructView.As<T>(a);
        ReadOnlySpan<T> sb = BspStructView.As<T>(b);

        if (sa.Length != sb.Length)
        {
            into.Add(
                $"{name} count",
                sa.Length.ToString(CultureInfo.InvariantCulture),
                sb.Length.ToString(CultureInfo.InvariantCulture));
        }

        int size = Unsafe.SizeOf<T>();
        int shared = Math.Min(sa.Length, sb.Length);
        ReadOnlySpan<byte> bytesA = a.Data.Span;
        ReadOnlySpan<byte> bytesB = b.Data.Span;

        for (int i = 0; i < shared && !into.Saturated; i++)
        {
            ReadOnlySpan<byte> ea = bytesA.Slice(i * size, size);
            ReadOnlySpan<byte> eb = bytesB.Slice(i * size, size);
            if (ea.SequenceEqual(eb))
            {
                continue;
            }

            into.Add(
                $"{name}[{i.ToString(CultureInfo.InvariantCulture)}]",
                Convert.ToHexString(ea),
                Convert.ToHexString(eb));
        }
    }

    /// <summary>Raw bytes, reporting the first offset that differs.</summary>
    internal static void Bytes(BspLumpData a, BspLumpData b, string name, LumpDiffBuilder into)
    {
        ReadOnlySpan<byte> sa = a.Data.Span;
        ReadOnlySpan<byte> sb = b.Data.Span;

        if (sa.Length != sb.Length)
        {
            into.Add(
                $"{name} length",
                sa.Length.ToString(CultureInfo.InvariantCulture),
                sb.Length.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(sa.Length, sb.Length);
        int at = sa[..shared].CommonPrefixLength(sb[..shared]);
        if (at < shared)
        {
            into.Add(
                $"{name} byte {at.ToString(CultureInfo.InvariantCulture)}",
                sa[at].ToString("X2", CultureInfo.InvariantCulture),
                sb[at].ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// LUMP_MODELS, compared on its COUNT.
    /// </summary>
    /// <param name="a">Map A.</param>
    /// <param name="b">Map B.</param>
    /// <param name="into">The verdict being built.</param>
    /// <remarks>
    /// plan_maptools.md 2 asks for "MODELS counts", not the models themselves,
    /// and the reason is in the struct: a <c>dmodel_t</c> is mostly INDICES --
    /// headnode, firstface -- which the canonical kinds exist because they move.
    /// The count is the part that is a property of the map. The bytes are still
    /// compared, so nothing is hidden; only the verdict is narrow, and the note
    /// says so.
    /// </remarks>
    internal static void Models(BspData a, BspData b, LumpDiffBuilder into)
    {
        int ca = BspStructView.Count<DModel>(a[BspLump.Models]);
        int cb = BspStructView.Count<DModel>(b[BspLump.Models]);
        into.Note =
            "compared on the model COUNT only, per plan_maptools.md 2: a dmodel_t's "
            + "headnode and firstface are indices the canonical kinds exist to absorb";

        if (ca != cb)
        {
            into.Add(
                "model count",
                ca.ToString(CultureInfo.InvariantCulture),
                cb.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The material-name table: LUMP_TEXDATA_STRING_DATA through its string
    /// table, compared name for name.
    /// </summary>
    internal static void MaterialNames(BspData a, BspData b, LumpDiffBuilder into)
    {
        List<string> na = BspNames.MaterialNames(a);
        List<string> nb = BspNames.MaterialNames(b);

        if (na.Count != nb.Count)
        {
            into.Add(
                "material name count",
                na.Count.ToString(CultureInfo.InvariantCulture),
                nb.Count.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(na.Count, nb.Count);
        for (int i = 0; i < shared && !into.Saturated; i++)
        {
            if (!string.Equals(na[i], nb[i], StringComparison.Ordinal))
            {
                into.Add($"material {i.ToString(CultureInfo.InvariantCulture)}", na[i], nb[i]);
            }
        }
    }

    /// <summary>
    /// The game lump's directory, and the static-prop model dictionary inside
    /// it.
    /// </summary>
    internal static void GameLumps(BspData a, BspData b, LumpDiffBuilder into)
    {
        if (a.GameLumps.Count != b.GameLumps.Count)
        {
            into.Add(
                "game lump count",
                a.GameLumps.Count.ToString(CultureInfo.InvariantCulture),
                b.GameLumps.Count.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(a.GameLumps.Count, b.GameLumps.Count);
        for (int i = 0; i < shared && !into.Saturated; i++)
        {
            GameLumpEntry ga = a.GameLumps[i];
            GameLumpEntry gb = b.GameLumps[i];
            if (ga.Id != gb.Id || ga.Version != gb.Version || ga.Flags != gb.Flags
                || ga.Data.Length != gb.Data.Length)
            {
                into.Add(
                    $"game lump {i.ToString(CultureInfo.InvariantCulture)}",
                    Describe(ga),
                    Describe(gb));
            }
        }

        ComparePropDictionary(a, b, into);
    }

    private static string Describe(GameLumpEntry entry) => string.Create(
        CultureInfo.InvariantCulture,
        $"{entry.IdString()} v{entry.Version} flags {entry.Flags} {entry.Data.Length} bytes");

    private static void ComparePropDictionary(BspData a, BspData b, LumpDiffBuilder into)
    {
        List<string>? da = PropDictionary(a);
        List<string>? db = PropDictionary(b);

        if (da is null || db is null)
        {
            if (da is not null || db is not null)
            {
                into.Add("sprp dictionary", da is null ? "absent" : "present", db is null ? "absent" : "present");
            }

            return;
        }

        if (da.Count != db.Count)
        {
            into.Add(
                "sprp dictionary size",
                da.Count.ToString(CultureInfo.InvariantCulture),
                db.Count.ToString(CultureInfo.InvariantCulture));
        }

        int shared = Math.Min(da.Count, db.Count);
        for (int i = 0; i < shared && !into.Saturated; i++)
        {
            if (!string.Equals(da[i], db[i], StringComparison.Ordinal))
            {
                into.Add($"sprp model {i.ToString(CultureInfo.InvariantCulture)}", da[i], db[i]);
            }
        }
    }

    private static List<string>? PropDictionary(BspData bsp)
    {
        int id = GameLumpId.MakeId(GameLumpId.StaticProps);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id != id)
            {
                continue;
            }

            try
            {
                return StaticPropLump.Read(entry).ModelNames;
            }
            catch (InvalidBspException)
            {
                // An sprp version this reader does not know is not this
                // instrument's business to diagnose; the directory comparison
                // above already recorded the version.
                return null;
            }
        }

        return null;
    }

    /// <summary>The pak lump's file list: names, sizes and CRCs.</summary>
    internal static async Task PakFileAsync(
        BspData a,
        BspData b,
        LumpDiffBuilder into,
        CancellationToken cancellationToken)
    {
        SortedDictionary<string, string>? pa = await PakEntriesAsync(a, cancellationToken).ConfigureAwait(false);
        SortedDictionary<string, string>? pb = await PakEntriesAsync(b, cancellationToken).ConfigureAwait(false);

        if (pa is null || pb is null)
        {
            into.Note = "a pak lump could not be read as a zip; compared as raw bytes";
            Bytes(a[BspLump.PakFile], b[BspLump.PakFile], "PAKFILE", into);
            return;
        }

        foreach ((string name, string described) in pa)
        {
            if (into.Saturated)
            {
                break;
            }

            if (!pb.TryGetValue(name, out string? other))
            {
                into.Add($"pak \"{name}\"", described, "absent");
            }
            else if (!string.Equals(described, other, StringComparison.Ordinal))
            {
                into.Add($"pak \"{name}\"", described, other);
            }
        }

        foreach ((string name, string described) in pb)
        {
            if (into.Saturated)
            {
                break;
            }

            if (!pa.ContainsKey(name))
            {
                into.Add($"pak \"{name}\"", "absent", described);
            }
        }
    }

    private static async Task<SortedDictionary<string, string>?> PakEntriesAsync(
        BspData bsp,
        CancellationToken cancellationToken)
    {
        BspLumpData lump = bsp[BspLump.PakFile];
        SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
        if (lump.IsEmpty)
        {
            return entries;
        }

        ZipArchiveReader reader;
        try
        {
            reader = await ZipArchiveReader.ParseAsync(lump.Data, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidZipException)
        {
            return null;
        }

        foreach (ZipEntry entry in reader.Entries)
        {
            entries[entry.Name] = string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.UncompressedSize} bytes, crc {entry.Crc:X8}, {entry.CompressionMethod}");
        }

        return entries;
    }
}

/// <summary>
/// The strings a BSP stores indirectly, resolved.
/// </summary>
internal static class BspNames
{
    /// <summary>
    /// Every material name, in string-table order.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The names; an entry whose offset is out of range reads as <c>&lt;bad&gt;</c>.</returns>
    internal static List<string> MaterialNames(BspData bsp)
    {
        ReadOnlySpan<byte> data = bsp[BspLump.TexDataStringData].Data.Span;
        BspLumpData tableLump = bsp[BspLump.TexDataStringTable];
        List<string> names = [];

        if (!BspStructView.Fits<int>(tableLump))
        {
            return names;
        }

        ReadOnlySpan<int> table = MemoryMarshal.Cast<byte, int>(tableLump.Data.Span);
        foreach (int offset in table)
        {
            if (offset < 0 || offset >= data.Length)
            {
                names.Add("<bad>");
                continue;
            }

            ReadOnlySpan<byte> rest = data[offset..];
            int end = rest.IndexOf((byte)0);
            names.Add(Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]));
        }

        return names;
    }

    /// <summary>
    /// The material name each <c>dtexdata_t</c> points at, in texdata order.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>One name per texdata record.</returns>
    internal static List<string> TexDataNames(BspData bsp)
    {
        List<string> strings = MaterialNames(bsp);
        List<string> names = [];
        BspLumpData lump = bsp[BspLump.TexData];
        if (!BspStructView.Fits<DTexData>(lump))
        {
            return names;
        }

        foreach (DTexData texData in BspStructView.As<DTexData>(lump))
        {
            names.Add(
                texData.NameStringTableId >= 0 && texData.NameStringTableId < strings.Count
                    ? strings[texData.NameStringTableId]
                    : "<bad>");
        }

        return names;
    }
}
