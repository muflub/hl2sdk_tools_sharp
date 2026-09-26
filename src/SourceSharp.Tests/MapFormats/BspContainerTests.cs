//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The BSP container: the header, the 64 lump slots, the game lump's nested
/// directory, and the write order that makes a round trip byte-exact.
/// </summary>
public class BspContainerTests
{
    [Fact]
    public void HeaderIsTenThirtySixBytes()
    {
        // ident + version + 64 * sizeof(lump_t) + mapRevision. The number is
        // quoted in the reference implementation's own comments, so it is
        // worth pinning against the arithmetic rather than trusting both.
        Assert.Equal(1036, BspData.HeaderSize);
    }

    [Fact]
    public void IdentSpellsVbsp()
    {
        Assert.Equal("VBSP", string.Concat(
            (char)(BspData.Ident & 0xFF),
            (char)((BspData.Ident >> 8) & 0xFF),
            (char)((BspData.Ident >> 16) & 0xFF),
            (char)((BspData.Ident >> 24) & 0xFF)));
    }

    [Fact]
    public async Task GameLumpIdMatchesWhatAGoldenMapActuallyHolds()
    {
        // THIS FACT USED TO BE WRONG, and instructively so. It asserted
        // MakeId("sprp") == ('p'<<24)|('r'<<16)|('p'<<8)|'s', which is the
        // packing the implementation happened to use -- so the check compared
        // the code against a restatement of the code and passed while both were
        // wrong. The reference implementation spells the id as the C multi-character
        // constant 'sprp', whose leftmost character lands in the HIGHEST byte.
        //
        // The fix is not a better constant in the test: it is to ask the FILE.
        // dm_lockdown's game-lump directory holds a static-prop entry and a
        // detail-prop entry, and whatever MakeId produces has to equal what is
        // written there.
        using FileStream file = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = await BspFile.LoadAsync(file);

        Assert.Contains(bsp.GameLumps, e => e.Id == GameLumpEntry.MakeId("sprp"));
        Assert.Contains(bsp.GameLumps, e => e.Id == GameLumpEntry.MakeId("dprp"));
    }

    [Fact]
    public void GameLumpIdPutsTheFirstCharacterInTheHighestByte()
    {
        // The arithmetic, pinned separately from the file so a failure says
        // which of the two is wrong. 'sprp' is 0x73707270.
        Assert.Equal(0x73707270, GameLumpEntry.MakeId("sprp"));
    }

    [Fact]
    public void GameLumpIdRoundTripsToItsCode()
    {
        Assert.Equal("dprp", new GameLumpEntry(GameLumpEntry.MakeId("dprp"), 0, 4, default).IdString());
    }

    [Fact]
    public void GameLumpIdRejectsACodeThatIsNotFourCharacters()
    {
        Assert.Throws<ArgumentException>(() => GameLumpEntry.MakeId("sprp2"));
    }

    [Fact]
    public async Task LoadRejectsAFileThatIsNotABsp()
    {
        byte[] bytes = new byte[BspData.HeaderSize];
        "NOPE"u8.CopyTo(bytes);

        using MemoryStream stream = new(bytes);
        InvalidBspException error =
            await Assert.ThrowsAsync<InvalidBspException>(() => BspFile.LoadAsync(stream));

        Assert.Contains("ident", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsAVersionTheEngineWouldNotLoad()
    {
        byte[] bytes = new byte[BspData.HeaderSize];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), BspData.Ident);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 18);

        using MemoryStream stream = new(bytes);
        InvalidBspException error =
            await Assert.ThrowsAsync<InvalidBspException>(() => BspFile.LoadAsync(stream));

        Assert.Contains("18", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsALumpThatRunsPastTheEndOfTheFile()
    {
        byte[] bytes = new byte[BspData.HeaderSize];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), BspData.Ident);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), BspData.Version);
        // Lump 0: offset 1036, length 4096, in a file that is only 1036 bytes.
        BitConverter.TryWriteBytes(bytes.AsSpan(8), BspData.HeaderSize);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 4096);

        using MemoryStream stream = new(bytes);
        InvalidBspException error =
            await Assert.ThrowsAsync<InvalidBspException>(() => BspFile.LoadAsync(stream));

        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadObservesAPreCancelledToken()
    {
        using MemoryStream stream = new(await File.ReadAllBytesAsync(GoldenBsp.Lockdown()));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BspFile.LoadAsync(stream, cancelled.Token));
    }

    [Fact]
    public void WriteOrderNamesNoLumpTwice()
    {
        // A duplicated step would write a lump's bytes twice and leave the
        // header pointing at the second copy. The file would still load.
        HashSet<BspLump> seen = [];
        foreach (BspWriteOrder.Step step in BspWriteOrder.Steps)
        {
            Assert.True(seen.Add(step.Lump), $"{step.Lump} appears twice in the write order");
        }
    }

    [Fact]
    public void WriteOrderExcludesTheTwoLumpsWithFramingOfTheirOwn()
    {
        // The game lump rebuilds an absolute-offset directory and the pak lump
        // is aligned before it is written, so neither can be a plain "write
        // these bytes and pad to four" step.
        Assert.False(BspWriteOrder.IsOrdered(BspLump.GameLump));
        Assert.False(BspWriteOrder.IsOrdered(BspLump.PakFile));
    }

    [Fact]
    public void WriteOrderSkipsOnlyTheThreeLumpsStockSkipsWhenEmpty()
    {
        BspLump[] skippable = [.. BspWriteOrder.Steps.Where(s => s.SkipWhenEmpty).Select(s => s.Lump)];

        // (`if (numfaces_hdr)`) (null pointer
        // checks). Everything else is written even at length zero, which
        // records the writer's position in the header rather than leaving the
        // slot zeroed.
        Assert.Equal([BspLump.FacesHdr, BspLump.PhysCollide, BspLump.PhysDisp], skippable);
    }

    [Fact]
    public async Task GoldenMapRoundTripsByteForByte()
    {
        // THE Phase 1a GATE. Loading a real map and writing it back out must
        // reproduce the file exactly: same lump order, same four-byte padding,
        // same header, same rebuilt game-lump directory.
        //
        // dm_lockdown is a version 19 map from an older bsplib -- it has no
        // leaf-ambient and no HDR lumps, and it DOES carry lump 49, which the
        // bsplib in this tree abandoned. So it is round-tripped in the mode
        // that preserves its own layout. Writing it canonically would produce a
        // valid map, and a different file; that is a fact of its own below.
        await AssertRoundTripsAsync(GoldenBsp.Lockdown());
    }

    [SandboxBspFact]
    public async Task SandboxMapRoundTripsByteForByte()
    {
        string? path = GoldenBsp.Sandbox();
        Assert.NotNull(path);
        await AssertRoundTripsAsync(path);
    }

    [Fact]
    public async Task PreservingModeNeedsAFileToPreserve()
    {
        BspData built = new();
        built.SetLump(BspLump.Entities, "{}"u8.ToArray());

        using MemoryStream output = new();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BspFile.SaveAsync(built, output, BspWriteMode.PreserveSourceLayout));

        Assert.Contains("built rather than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalModeReordersAnOlderMap()
    {
        // The other half of the round-trip fact: the two modes really are
        // different, so the preserving one is not quietly doing nothing. An
        // older map written canonically keeps its contents and changes its
        // layout.
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());

        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.Canonical);

        Assert.False(original.AsSpan().SequenceEqual(output.ToArray()));

        // ... and the contents survive the reordering.
        output.Position = 0;
        BspData reloaded = await BspFile.LoadAsync(output);
        Assert.True(
            bsp[BspLump.Entities].Data.Span.SequenceEqual(reloaded[BspLump.Entities].Data.Span));
        Assert.True(
            bsp[BspLump.Planes].Data.Span.SequenceEqual(reloaded[BspLump.Planes].Data.Span));
        Assert.Equal(bsp.GameLumps.Count, reloaded.GameLumps.Count);
    }

    private static async Task AssertRoundTripsAsync(string path)
    {
        byte[] original = await File.ReadAllBytesAsync(path);

        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.PreserveSourceLayout);

        byte[] written = output.ToArray();

        Assert.Equal(original.Length, written.Length);
        Assert.True(original.AsSpan().SequenceEqual(written), FirstDifference(original, written));
    }

    [Fact]
    public async Task GoldenMapKeepsItsHeaderFieldsAcrossALoad()
    {
        using FileStream file = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = await BspFile.LoadAsync(file);

        Assert.InRange(bsp.FileVersion, BspData.MinVersion, BspData.Version);
        Assert.False(bsp[BspLump.Entities].IsEmpty);
        Assert.False(bsp[BspLump.Planes].IsEmpty);
    }

    [Fact]
    public async Task GoldenMapGameLumpDirectoryParses()
    {
        using FileStream file = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = await BspFile.LoadAsync(file);

        // Every entry's payload must lie inside the outer lump; the parser
        // throws if not, so reaching here with entries at all is the check.
        // The four-character codes are asserted to be printable because a
        // mis-rebased directory yields entries whose ids are payload bytes.
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            Assert.All(entry.IdString(), c => Assert.InRange(c, ' ', '~'));
        }
    }

    private static string FirstDifference(byte[] expected, byte[] actual)
    {
        int limit = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < limit; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"first difference at byte {i} (0x{i:X}): "
                    + $"expected 0x{expected[i]:X2}, wrote 0x{actual[i]:X2}";
            }
        }

        return $"the first {limit} bytes agree; lengths differ ({expected.Length} vs {actual.Length})";
    }
}
