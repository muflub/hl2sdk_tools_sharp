//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// Where the catalogue's STOCK-compiled maps are, when this machine has any.
/// </summary>
/// <remarks>
/// <para>
/// Instrument I3 says managed vvis is judged on stock vbsp's output and never
/// on managed vbsp's -- there is none yet -- so the probe gate needs real
/// <c>.bsp</c> and <c>.prt</c> files that this suite cannot produce: making
/// them needs Wine, a Steam depot and the HL2 content, none of which
/// <c>make test</c> has.
/// </para>
/// <para>
/// The recipe, which is what a reader of a skipped test needs:
/// </para>
/// <code>
/// CATALOGUE_EMIT_DIR=/tmp/catmaps dotnet test --filter TheWholeCatalogueEmits
/// make toolgame
/// for f in /tmp/catmaps/*.vmf; do
/// wine.../vbsp.exe -game "Z:$PWD/tools/mapgame" "Z:$f"
/// done
/// VVIS_STOCK_DIR=/tmp/catmaps dotnet test --filter VvisCatalogue
/// </code>
/// </remarks>
internal static class StockCatalogue
{
    /// <summary>The environment variable naming the directory.</summary>
    internal const string DirectoryVariable = "VVIS_STOCK_DIR";

    /// <summary>The directory, or null when the variable is not set.</summary>
    internal static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set : null;

    /// <summary>Why a fact needing stock output cannot run, or null.</summary>
    /// <returns>A skip reason, or null when the maps are there.</returns>
    internal static string? SkipReason()
    {
        string? directory = Directory;
        if (directory is null)
        {
            return $"no {DirectoryVariable}: this tree has no stock-compiled catalogue. "
                + "See StockCatalogue for the recipe.";
        }

        // Set but WRONG is not a skip. A gate that quietly passes because
        // someone typo'd a path is the failure mode this whole file exists to
        // avoid, so it goes red and says which directory it looked in.
        return null;
    }

    /// <summary>
    /// The map STOCK vvis produced from the same input, at the pinned
    /// configuration, or null.
    /// </summary>
    /// <param name="name">The catalogue entry's name.</param>
    /// <returns>A path, or null.</returns>
    /// <remarks>
    /// <para>
    /// The configuration is PINNED at <c>-threads 1</c>, sorted, and saying so
    /// is not pedantry: spike 0c measured that stock's own answer moves with
    /// the thread count and moves again, in both directions, under
    /// <c>-nosort</c>. "Equal to a stock reference" is not a well-formed claim
    /// without naming which stock run.
    /// </para>
    /// <code>
    /// cp map.bsp map.stockvis.bsp; cp map.prt map.stockvis.prt
    /// wine.../vvis.exe -threads 1 -game "Z:$PWD/tools/mapgame" "Z:.../map.stockvis.bsp"
    /// </code>
    /// </remarks>
    internal static string? StockVisBspFor(string name)
    {
        string? directory = Directory;
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, name + ".stockvis.bsp");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The <c>.bsp</c> for one entry, or null when it was not compiled.</summary>
    /// <param name="name">The catalogue entry's name.</param>
    /// <returns>A path, or null.</returns>
    internal static string? BspFor(string name)
    {
        string? directory = Directory;
        if (directory is null)
        {
            return null;
        }

        if (name.EndsWith(".stockvis", StringComparison.Ordinal))
        {
            return null;
        }

        string bsp = Path.Combine(directory, name + ".bsp");
        string prt = Path.Combine(directory, name + ".prt");

        // Both, or neither: a map that leaks has a.bsp and no.prt, and vvis
        // cannot run on one. That is the l1_leak entry, by design.
        return File.Exists(bsp) && File.Exists(prt) ? bsp : null;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly, when this tree has no
/// stock-compiled catalogue.
/// </summary>
/// <remarks>
/// The same shape as <see cref="SandboxMapFactAttribute"/> and for the same
/// reason: an early <c>return</c> reports a PASS, and a tree that checked
/// nothing then looks exactly like a tree that checked everything and agreed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockCatalogueFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether the maps are there.</summary>
    public StockCatalogueFactAttribute() => Skip = StockCatalogue.SkipReason();
}

/// <summary>
/// A <see cref="TheoryAttribute"/> that skips, visibly, when this tree has no
/// stock-compiled catalogue.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockCatalogueTheoryAttribute : TheoryAttribute
{
    /// <summary>Decides at discovery whether the maps are there.</summary>
    public StockCatalogueTheoryAttribute() => Skip = StockCatalogue.SkipReason();
}
