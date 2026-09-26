//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Io;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// The stock reference for Phase 3g's catalogue entries.
/// </summary>
/// <remarks>
/// <para>
/// RECIPE. Emit the 3g entries and <c>SurfaceContentFixture</c>'s files into
/// a directory (<c>&lt;dir&gt;/content</c> for the fixture), write
/// <c>&lt;dir&gt;/game/gameinfo.txt</c> as <c>tools/mapgame</c>'s with
/// <c>game+mod "Z:&lt;dir&gt;/content"</c> as the FIRST game path, then run
/// stock x64 <c>vbsp -v -game Z:&lt;dir&gt;/game Z:&lt;dir&gt;/&lt;name&gt;.vmf</c> on each
/// (memory-capped, 3G), and write <c>&lt;dir&gt;/game/gameinfo.txt</c>'s path into
/// each map's <c>&lt;name&gt;.gameinfo</c> sidecar (<see cref="StockProvenance"/>;
/// without it the facts fail). Mount the reference copy with:
/// </para>
/// <code>P3G_STOCK_DIR=&lt;the reference directory&gt; dotnet test --filter SurfaceContent</code>
/// <para>
/// Set but wrong is not a skip: a gate that passes because a path was
/// mistyped has certified nothing, so a set variable naming a directory with
/// no BSPs fails the facts instead.
/// </para>
/// </remarks>
internal static class P3gStock
{
    public const string DirectoryVariable = "P3G_STOCK_DIR";

    public static string? Directory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } set ? set : null;

    public static string? SkipReason() =>
        Directory is null
            ? $"no {DirectoryVariable}: this tree has no stock-compiled 3g catalogue. See P3gStock for the recipe."
            : InstalledGameContent.SkipReason;

    public static TheoryData<string> Entries
    {
        get
        {
            TheoryData<string> data = [];
            string? directory = Directory;

            if (directory is null || !System.IO.Directory.Exists(directory))
            {
                data.Add("none");
                return data;
            }

            foreach (string bsp in System.IO.Directory.GetFiles(directory, "*.bsp").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileNameWithoutExtension(bsp));
            }

            return data;
        }
    }

    public static async Task<BspData> StockBspAsync(string name)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(Directory!, name + ".bsp"));
        return await BspFile.LoadAsync(stream);
    }

    public static async Task<ZipArchiveReader> StockPakAsync(BspData bsp) =>
        await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data);

    /// <summary>
    /// Loads the entry's VMF with the 3a reader against the same content stock
    /// compiled it with, under stock compliance.
    /// </summary>
    public static async Task<(VbspContext Context, MapFile Map, MaterialPatcher Patcher)> LoadAsync(string name, ComplianceOptions? compliance = null)
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);

        // The content stock compiled THIS map against, from its sidecar
        // (StockProvenance): never a default.
        GameContentMounter.Result mounted = await StockProvenance.MountAsync(
            guarded, host.ToVirtualPath(Directory!), name);

        VbspContext context = new(VbspOptions.Default with { Compliance = compliance ?? ComplianceOptions.Stock }, mounted.Content)
        {
            MapBase = name,
        };

        MapFile map = await new MapFileReader(context, guarded)
            .LoadAsync(host.ToVirtualPath(Path.Combine(Directory!, name + ".vmf")));

        return (context, map, new MaterialPatcher(mounted.Content, new MapPakFile(), context.Options.Compliance));
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class P3gStockTheoryAttribute : TheoryAttribute
{
    public P3gStockTheoryAttribute() => Skip = P3gStock.SkipReason();
}
