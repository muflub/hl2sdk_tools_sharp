using System.Globalization;
using System.Text.RegularExpressions;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The eight load counts parsed out of a stock <c>vbsp -v</c> log.
/// </summary>
/// <param name="Brushes">"%5i brushes".</param>
/// <param name="ClipBrushes">"%5i clipbrushes".</param>
/// <param name="TotalSides">"%5i total sides".</param>
/// <param name="BoxBevels">"%5i boxbevels".</param>
/// <param name="EdgeBevels">"%5i edgebevels".</param>
/// <param name="Entities">"%5i entities".</param>
/// <param name="Planes">"%5i planes".</param>
/// <param name="AreaPortals">"%5i areaportals".</param>
internal readonly record struct StockLoadCounts(
    int Brushes,
    int ClipBrushes,
    int TotalSides,
    int BoxBevels,
    int EdgeBevels,
    int Entities,
    int Planes,
    int AreaPortals);

/// <summary>
/// The stock vbsp reference this lane is judged against: a <c>-v</c> log and a
/// compiled <c>.bsp</c> per catalogue map.
/// </summary>
/// <remarks>
/// <para>
/// Stock prints the load statistics under <c>-v</c> and nowhere else, so the
/// only way to hold the plane table — whose contents are otherwise invisible
/// until faces exist — against a reference is to compile with <c>-v</c> and
/// read the log. That is what these facts do, and it is why they need a
/// directory a plain catalogue compile does not produce.
/// </para>
/// <para>
/// The recipe, which is what a reader of a skipped test needs:
/// </para>
/// <code>
/// make-catmaps -game &lt;corpus-game&gt; &lt;worktree&gt; $DIR
/// # or, for VMFs already in $DIR (ss_sandbox, the bevel shapes):
/// make-catmaps -game &lt;corpus-game&gt; -noemit &lt;worktree&gt; $DIR
/// VVIS_STOCK_DIR=$DIR dotnet test --filter StockLoad
/// </code>
/// <para>
/// Every map carries a <c>&lt;n&gt;.gameinfo</c> sidecar naming the
/// <c>gameinfo.txt</c> stock was given (<see cref="StockProvenance"/>); the
/// managed side mounts exactly that, and a map without one FAILS. Before the
/// sidecar, every map was compiled here against <c>tools/mapgame</c> whatever
/// stock had used, and the eleven maps built on p3g's fixture content read
/// as 35 port failures.
/// </para>
/// <remarks>
/// Every <c>.vmf</c> with a <c>.vbspv.log</c> beside it is picked up, so the
/// corpus map and the bevel shapes join the catalogue simply by being in the
/// directory.
/// </remarks>
/// </remarks>
internal static class StockLoad
{
    /// <summary>Why these facts cannot run, or null.</summary>
    public static string? SkipReason()
    {
        string? directory = StockCatalogue.Directory;

        if (directory is null)
        {
            return $"no {StockCatalogue.DirectoryVariable}: this tree has no stock-compiled "
                + "catalogue. See StockLoad for the recipe.";
        }

        if (Directory.GetFiles(directory, "*.vbspv.log").Length == 0)
        {
            return $"{directory} holds no *.vbspv.log: the catalogue was compiled without -v, "
                + "so stock's load counts are not recorded. See StockLoad for the recipe.";
        }

        return InstalledGameContent.SkipReason;
    }

    /// <summary>The catalogue entries that have both a VMF and a verbose log.</summary>
    public static TheoryData<string> Entries
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string name in EntryNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>The same entries, as plain names.</summary>
    public static IReadOnlyList<string> EntryNames
    {
        get
        {
            string? directory = StockCatalogue.Directory;

            if (directory is null || !Directory.Exists(directory))
            {
                return ["none"];
            }

            List<string> names = [];

            foreach (string log in Directory
                .GetFiles(directory, "*.vbspv.log")
                .Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(log)[..^".vbspv.log".Length];

                if (File.Exists(Path.Combine(directory, name + ".vmf")))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>The VMF for one entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The host path.</returns>
    public static string VmfPath(string name) =>
        Path.Combine(StockCatalogue.Directory!, name + ".vmf");

    /// <summary>The compiled BSP for one entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The host path.</returns>
    public static string BspPath(string name) =>
        Path.Combine(StockCatalogue.Directory!, name + ".bsp");

    /// <summary>
    /// Parses the eight counts out of a stock <c>-v</c> log.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The counts.</returns>
    /// <remarks>
    /// The log repeats "%5i brushes" once per brush model later in the compile,
    /// so only the FIRST match of each line counts — the block printed at the
    /// end of <c>LoadMapFile</c>.
    /// </remarks>
    public static StockLoadCounts Counts(string name)
    {
        string text = File.ReadAllText(
            Path.Combine(StockCatalogue.Directory!, name + ".vbspv.log"));

        return new StockLoadCounts(
            First(text, "brushes"),
            First(text, "clipbrushes"),
            First(text, "total sides"),
            First(text, "boxbevels"),
            First(text, "edgebevels"),
            First(text, "entities"),
            First(text, "planes"),
            First(text, "areaportals"));
    }

    /// <summary>
    /// What stock's own log says its end-of-compile compaction removed.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <returns>
    /// The texinfo and texdata counts before and after
    /// <c>CompactTexinfoArray</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// "Reduced 90 texinfos to 71" and "Reduced 20 texdatas to 16 (525 bytes
    /// to 354)". This is the ONLY reliable way to know whether the final lump
    /// is the load-time table: equal counts do not mean nothing was dropped,
    /// because a table this port did not compact can happen to be the same
    /// length as one stock did — which is exactly what
    /// <c>ss_sandbox</c> does, and what an earlier version of these facts
    /// read as agreement.
    /// </para>
    /// </remarks>
    public static (int TexInfoBefore, int TexInfoAfter, int TexDataBefore, int TexDataAfter)
        Compaction(string name)
    {
        string text = File.ReadAllText(
            Path.Combine(StockCatalogue.Directory!, name + ".vbspv.log"));

        Match texinfo = Regex.Match(
            text, @"Reduced (\d+) texinfos to (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        Match texdata = Regex.Match(
            text, @"Reduced (\d+) texdatas to (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(texinfo.Success, "the stock log has no \"Reduced N texinfos\" line");
        Assert.True(texdata.Success, "the stock log has no \"Reduced N texdatas\" line");

        return (
            int.Parse(texinfo.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(texinfo.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(texdata.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(texdata.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Loads a catalogue map with this port, against the toolgame content.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <param name="compliance">
    /// Which compliance the caller is asserting. REQUIRED, with no default:
    /// every fact in this tree that compares against stock output must say so,
    /// because a gate that silently inherits
    /// <see cref="ComplianceOptions.Correct"/> would go red the day a quirk
    /// site starts doing the right thing and read as a regression.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The context and the loaded map.</returns>
    public static async Task<(VbspContext Context, MapFile Map, MapLoadStatistics Stats)> LoadAsync(
        string name,
        ComplianceOptions compliance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);

        // The content stock compiled THIS map against, from its sidecar;
        // never a default (StockProvenance).
        GameContentMounter.Result mounted = await StockProvenance.MountAsync(
            guarded,
            host.ToVirtualPath(StockCatalogue.Directory!),
            name,
            cancellationToken: cancellationToken);

        VbspContext context =
            new(VbspOptions.Default with { Compliance = compliance }, mounted.Content)
            {
                MapBase = name,
            };
        MapFileReader reader = new(context, guarded);

        MapFile map = await reader.LoadAsync(
            host.ToVirtualPath(VmfPath(name)), cancellationToken);

        return (context, map, reader.Statistics);
    }

    /// <summary>The TEXDATA lump of the stock BSP, with names resolved.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries and their names, in lump order.</returns>
    public static async Task<IReadOnlyList<(DTexData Entry, string Name)>> StockTexDataAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadBspAsync(name, cancellationToken);

        ReadOnlySpan<DTexData> texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]);
        ReadOnlySpan<int> table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]);
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span;

        List<(DTexData, string)> result = [];

        foreach (DTexData entry in texData)
        {
            result.Add((entry, StringAt(table, strings, entry.NameStringTableId)));
        }

        return result;
    }

    /// <summary>The PLANES lump of the stock BSP.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in lump order.</returns>
    /// <remarks>
    /// <para>
    /// <b>This lump IS the plane table.</b> <c>EmitPlanes</c>
    /// copies
    /// <c>g_MainMap-&gt;mapplanes[0..nummapplanes)</c> into <c>dplanes</c> index
    /// for index — normal, dist and the stored type — and its own comment says
    /// why: "There is no oportunity to discard planes, because all of the
    /// original brushes will be saved in the map."
    /// </para>
    /// <para>
    /// So there is no compaction to work around and no renumbering to match
    /// through: the comparison against this lump is element by element, in
    /// order, on the exact bytes — which is the strongest form the gate on this
    /// lane's central claim can take.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<DPlane>> StockPlanesAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadBspAsync(name, cancellationToken);
        return [.. BspStructView.As<DPlane>(bsp[BspLump.Planes])];
    }

    /// <summary>The TEXINFO lump of the stock BSP.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, in lump order.</returns>
    public static async Task<IReadOnlyList<TexInfo>> StockTexInfoAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadBspAsync(name, cancellationToken);
        return [.. BspStructView.As<TexInfo>(bsp[BspLump.TexInfo])];
    }

    /// <summary>The TEXDATA_STRING_TABLE and _DATA lumps of the stock BSP.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The table's offsets and the data lump's bytes.</returns>
    public static async Task<(int[] Offsets, byte[] Data)> StockStringsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        BspData bsp = await LoadBspAsync(name, cancellationToken);

        return (
            [.. BspStructView.As<int>(bsp[BspLump.TexDataStringTable])],
            [.. bsp[BspLump.TexDataStringData].Data.Span]);
    }

    private static async Task<BspData> LoadBspAsync(string name, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(BspPath(name));
        return await BspFile.LoadAsync(stream, cancellationToken);
    }

    private static string StringAt(ReadOnlySpan<int> table, ReadOnlySpan<byte> strings, int id)
    {
        if ((uint)id >= (uint)table.Length)
        {
            return string.Empty;
        }

        int offset = table[id];
        int end = offset;

        while (end < strings.Length && strings[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.Latin1.GetString(strings[offset..end]);
    }

    private static int First(string text, string label)
    {
        Match match = Regex.Match(
            text,
            @"^\s*(-?\d+) " + Regex.Escape(label) + @"\s*$",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"the stock log has no \"{label}\" line");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that skips, visibly, when this tree has no
/// stock <c>-v</c> catalogue compile to compare against.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockLoadTheoryAttribute : TheoryAttribute
{
    /// <summary>Decides at discovery whether the reference is there.</summary>
    public StockLoadTheoryAttribute() => Skip = StockLoad.SkipReason();
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly, when this tree has no
/// stock <c>-v</c> catalogue compile to compare against.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockLoadFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether the reference is there.</summary>
    public StockLoadFactAttribute() => Skip = StockLoad.SkipReason();
}
