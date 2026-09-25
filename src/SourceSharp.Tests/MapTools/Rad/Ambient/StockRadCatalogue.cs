using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// Where this machine's STOCK-VRAD-COMPILED maps are, if it has any.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A FINISHED MAP IS THE WHOLE ORACLE.</b> Leaf ambient is the LAST
/// lighting stage: <c>VRAD_ComputeOtherLighting</c> (<c>vrad.cpp:2309</c>) runs
/// after <c>RadWorld_Go</c> has finished <c>FinalLightFace</c>, so the lightmap
/// the stage reads is the lightmap that ends up in the file. Everything leaf
/// ambient consumes -- <c>LUMP_LIGHTING</c>, <c>LUMP_WORLDLIGHTS</c>, the tree,
/// the brushes -- and everything it produces -- lumps 51, 52, 55 and 56 -- are
/// both present in one finished <c>.bsp</c>. So the gate is self-contained: read
/// the map, recompute the stage, and the answer must be the same bytes the file
/// already holds.
/// </para>
/// <para>
/// That also means the gate does NOT depend on the vbsp or vvis lanes, and does
/// not need the lighting lane either: it compares against a lightmap stock
/// produced rather than one this port did.
/// </para>
/// <para>
/// The recipe, which is what a reader of a skipped test needs:
/// </para>
/// <code>
/// # a vbsp + vvis output, frozen so no vrad time contains vbsp time
/// cp ss_sandbox_base.bsp $DIR/ss_sandbox.bsp
/// wine .../vrad.exe -both -threads 1 -game "Z:$GAMEINFO" "Z:$DIR/ss_sandbox.bsp"
/// VRAD_STOCK_DIR=$DIR dotnet test --filter LeafAmbientStockParity
/// </code>
/// <para>
/// <c>-threads 1</c> is pinned for reproducibility of the REFERENCE, not
/// because this gate needs it: the stage's inputs and outputs travel together
/// in the file, so whatever lightmap that run produced is the one the
/// comparison uses.
/// </para>
/// </remarks>
internal static class StockRadCatalogue
{
    /// <summary>The environment variable naming the directory.</summary>
    internal const string DirectoryVariable = "VRAD_STOCK_DIR";

    /// <summary>
    /// The shared corpus variable (<c>catmaps-all</c>): read when
    /// <see cref="DirectoryVariable"/> is not set.
    /// </summary>
    internal const string CorpusVariable = "VVIS_STOCK_DIR";

    /// <summary>The suffix the shared corpus gives a map stock vrad lit.</summary>
    internal const string StockRadSuffix = ".stockrad.bsp";

    /// <summary>The directory, or null when neither variable is set.</summary>
    internal static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set
        : Environment.GetEnvironmentVariable(CorpusVariable) is { Length: > 0 } corpus ? corpus
        : null;

    /// <summary>Why a fact needing a stock-vrad'd map cannot run, or null.</summary>
    /// <returns>A skip reason, or null when the directory is named.</returns>
    /// <remarks>
    /// Set but WRONG is deliberately NOT a skip. A gate that quietly passes
    /// because someone mistyped a path is the failure mode this file exists to
    /// avoid, so a named directory with no maps in it goes red and says which
    /// directory it looked in.
    /// </remarks>
    internal static string? SkipReason() =>
        Directory is null
            ? $"no {DirectoryVariable} or {CorpusVariable}: this tree has no stock-vrad-compiled map. "
                + "See StockRadCatalogue for the recipe."
            : null;

    /// <summary>
    /// Whether any map in the directory carries <c>LUMP_LIGHTING_HDR</c>,
    /// read from the header alone.
    /// </summary>
    /// <returns>True when at least one does.</returns>
    internal static bool HasHdrMap()
    {
        const int LightingHdr = 53;
        Span<byte> header = stackalloc byte[8 + (64 * 16)];
        foreach (string path in Maps())
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Read(header) == header.Length
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[(8 + (LightingHdr * 16) + 4)..]) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every stock-vrad'd map in the directory, sorted.</summary>
    /// <returns>Absolute paths.</returns>
    /// <remarks>
    /// A directory holding any <c>*.stockrad.bsp</c> is the shared corpus
    /// layout, where the plain <c>.bsp</c> is vbsp's output and
    /// <c>.stockvis.bsp</c> vvis's: only the lit maps are taken. Otherwise
    /// every <c>.bsp</c> is one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The variable is not set.</exception>
    internal static IReadOnlyList<string> Maps()
    {
        string directory = Directory
            ?? throw new InvalidOperationException(
                $"{DirectoryVariable} is not set; the attribute should have skipped");

        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        string[] maps = System.IO.Directory.GetFiles(directory, "*" + StockRadSuffix);
        if (maps.Length == 0)
        {
            maps = System.IO.Directory.GetFiles(directory, "*.bsp");
        }

        Array.Sort(maps, StringComparer.Ordinal);
        return maps;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the HDR pass: skips, visibly, when there
/// is no stock-vrad'd map or when none of them has an HDR lighting lump (the
/// shared corpus is compiled LDR only).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockRadHdrFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether an HDR map is there.</summary>
    public StockRadHdrFactAttribute() =>
        Skip = StockRadCatalogue.SkipReason()
            ?? (StockRadCatalogue.HasHdrMap()
                ? null
                : $"no map in '{StockRadCatalogue.Directory}' has an HDR pass (LUMP_LIGHTING_HDR); "
                    + "compile references with vrad -both to cover it.");
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly, when this tree has no
/// stock-vrad-compiled map.
/// </summary>
/// <remarks>
/// Skipped rather than early-returned, for the reason
/// <c>StockCatalogueFactAttribute</c> gives: an early return reports PASSED,
/// and a tree that checked nothing then looks exactly like one that checked
/// everything and agreed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockRadFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether the maps are there.</summary>
    public StockRadFactAttribute() => Skip = StockRadCatalogue.SkipReason();
}
