//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using SourceSharp.MapFormats.Zip;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Zip;

/// <summary>
/// The listing this reader produces, against the listing stock
/// <c>bspzip.exe -dir</c> produces for the same BSP.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 1b brief asks for stock <c>bspzip -dir</c> to list a managed
/// pak identically. Running <c>bspzip.exe</c> needs Wine, a Wine prefix and a
/// resolvable <c>gameinfo.txt</c>, none of which a test suite that must "run
/// anywhere" can require -- so the tool is driven OUT of band and its output
/// is checked in as a fixture, which this fact compares against.
/// </para>
/// <para>
/// The fixture is <c>bspzip-dir-dm_lockdown.txt</c> beside this file: stock's
/// stdout with its banner, its two "Reading unknown lump" lines and its ANSI
/// colour escapes stripped, leaving one entry name per line in the order
/// bspzip walked them. The command that produced it is recorded in the
/// fixture's first line so it can be regenerated.
/// </para>
/// </remarks>
public class BspZipParityTests
{
    private const string MapPath = "game/mod_sharp/maps/dm_lockdown.bsp";
    private const string FixturePath =
        "src/SourceSharp.Tests/MapFormats/Zip/bspzip-dir-dm_lockdown.txt";

    private const int LumpTableOffset = 8;
    private const int LumpEntrySize = 16;

    private static byte[] ReadPakLump()
    {
        byte[] bsp = File.ReadAllBytes(RepoSourceFactAttribute.Find(MapPath)!);

        int entry = LumpTableOffset + (ZipFormat.PakFileLumpIndex * LumpEntrySize);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(entry));
        int length = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(entry + 4));

        return bsp.AsSpan(offset, length).ToArray();
    }

    private static string[] ReadFixture() =>
        [.. File.ReadAllLines(RepoSourceFactAttribute.Find(FixturePath)!)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

    [RepoSourceFact(MapPath, FixturePath)]
    public async Task ManagedListingMatchesBspzipsEntryForEntry()
    {
        // Same entries, same order. Order matters: bspzip walks the central
        // directory in file order, and so does this reader -- unlike
        // CZipFile, which re-sorts into a red-black tree on load
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(ReadPakLump(), CancellationToken.None);

        Assert.Equal(ReadFixture(), read.Entries.Select(e => e.Name).ToArray());
    }

    [RepoSourceFact(MapPath, FixturePath)]
    public async Task ManagedListingOfAPakThisPortWroteMatchesBspzipsToo()
    {
        // The stronger form, and the one the brief asks for: the listing must
        // still match after the pak has been round-tripped through THIS
        // WRITER. It does, because the round trip is byte-exact -- but this
        // fact fails for a writer that is merely "valid zip" rather than
        // byte-identical, which a listing comparison alone would not catch.
        ZipArchiveReader original = await ZipArchiveReader.ParseAsync(ReadPakLump(), CancellationToken.None);
        byte[] rewritten = original.ToWriter().ToBytes();

        ZipArchiveReader reread = await ZipArchiveReader.ParseAsync(rewritten, CancellationToken.None);

        Assert.Equal(ReadFixture(), reread.Entries.Select(e => e.Name).ToArray());
    }
}
