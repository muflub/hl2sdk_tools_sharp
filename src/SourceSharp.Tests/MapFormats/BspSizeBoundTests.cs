//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// <see cref="BspFile.SizeBound"/>: never below what <see cref="BspFile.SaveAsync(BspData, Stream, BspWriteMode, CancellationToken)"/>
/// writes, and only a few bytes a lump above it, so a buffer of that size is
/// made once and never grows.
/// </summary>
public sealed class BspSizeBoundTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheBoundHoldsTheWrittenFileAndLittleMore(int seed)
    {
        Random random = new(seed);
        BspData bsp = new();
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            if (lump == BspLump.GameLump || random.Next(3) == 0)
            {
                continue;
            }

            byte[] data = new byte[random.Next(0, 5000)];
            random.NextBytes(data);
            bsp.SetLump(lump, data);
        }

        for (int i = 0; i < random.Next(0, 4); i++)
        {
            byte[] payload = new byte[random.Next(1, 3000)];
            bsp.GameLumps.Add(new GameLumpEntry(GameLumpEntry.MakeId("sp" + i + "x"), 0, 4, payload));
        }

        long bound = BspFile.SizeBound(bsp);
        using MemoryStream written = new();
        await BspFile.SaveAsync(bsp, written, BspWriteMode.Canonical);

        Assert.InRange(bound, written.Length, written.Length + (BspData.HeaderLumps * 4) + 64);
    }

    [Fact]
    public async Task AnEmptyBspIsBoundedByItsHeaderAndPadding()
    {
        BspData bsp = new();
        using MemoryStream written = new();
        await BspFile.SaveAsync(bsp, written, BspWriteMode.Canonical);

        Assert.InRange(BspFile.SizeBound(bsp), written.Length, written.Length + (BspData.HeaderLumps * 4) + 64);
        Assert.Throws<ArgumentNullException>(() => BspFile.SizeBound(null!));
    }
}
