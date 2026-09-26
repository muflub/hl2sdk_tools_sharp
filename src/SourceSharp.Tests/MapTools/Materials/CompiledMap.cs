//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// One material as STOCK vbsp wrote it into a BSP's TEXDATA lump.
/// </summary>
/// <param name="Name">The material name, out of TEXDATA_STRING_DATA.</param>
/// <param name="Reflectivity">
/// What <c>GetMaterialReflectivity</c> gave the real compiler.
/// </param>
/// <param name="Width">What <c>GetMaterialDimensions</c> gave it.</param>
/// <param name="Height">The same, for the height.</param>
internal readonly record struct StockTexData(
    string Name,
    Vec3 Reflectivity,
    int Width,
    int Height);

/// <summary>
/// The committed, stock-compiled map this tree can run a differential against.
/// </summary>
/// <remarks>
/// <para>
/// <c>game/mod_sharp/maps/dm_lockdown.bsp</c> was compiled by stock
/// vbsp. Its TEXDATA lump holds, per material, the reflectivity and the
/// dimensions THAT COMPILER computed out of the material system — so reading
/// it back turns the whole of <c>utilmatlib</c>'s numeric surface into a
/// differential over hundreds of real materials, which no fixture can be.
/// </para>
/// <para>
/// A committed file rather than build output, so a fact that cannot find it
/// throws instead of skipping: its absence means a tracked file moved.
/// </para>
/// </remarks>
internal static class CompiledMap
{
    private static readonly string[] BspParts = ["game", "mod_sharp", "maps", "dm_lockdown.bsp"];

    /// <summary>The committed map of the checkout this binary belongs to.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">
    /// The checkout root or the file could not be found.
    /// </exception>
    public static string DmLockdownPath()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The map is located "
                + "relative to the tree this binary was built from, so the differential reads THIS "
                + "worktree's map and not an enclosing checkout's.");
        }

        string path = Path.Combine(root, Path.Combine(BspParts));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{path} is missing. It is a COMMITTED file, not build output.");
        }

        return path;
    }

    /// <summary>Reads the map.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed BSP.</returns>
    public static async Task<BspData> LoadDmLockdownAsync(
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(DmLockdownPath());
        return await BspFile.LoadAsync(stream, cancellationToken);
    }

    /// <summary>
    /// The TEXDATA lump, with each entry's name resolved.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>One entry per texdata, in lump order.</returns>
    public static ImmutableArray<StockTexData> TexData(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DTexData> texdata = BspStructView.As<DTexData>(bsp[BspLump.TexData]);
        ReadOnlySpan<int> table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]);
        ReadOnlySpan<byte> strings = bsp[BspLump.TexDataStringData].Data.Span;

        ImmutableArray<StockTexData>.Builder builder =
            ImmutableArray.CreateBuilder<StockTexData>(texdata.Length);

        foreach (DTexData entry in texdata)
        {
            builder.Add(new StockTexData(
                StringAt(table, strings, entry.NameStringTableId),
                entry.Reflectivity,
                entry.Width,
                entry.Height));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>The TEXINFO lump.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>Its entries, copied out of the lump.</returns>
    public static ImmutableArray<TexInfo> TexInfo(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<TexInfo> texinfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ImmutableArray<TexInfo>.Builder builder =
            ImmutableArray.CreateBuilder<TexInfo>(texinfo.Length);

        foreach (TexInfo entry in texinfo)
        {
            builder.Add(entry);
        }

        return builder.MoveToImmutable();
    }

    private static string StringAt(ReadOnlySpan<int> table, ReadOnlySpan<byte> strings, int id)
    {
        if ((uint)id >= (uint)table.Length)
        {
            return string.Empty;
        }

        int offset = table[id];
        if ((uint)offset >= (uint)strings.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> rest = strings[offset..];
        int end = rest.IndexOf((byte)0);

        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]);
    }
}
