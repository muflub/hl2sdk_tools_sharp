//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// A map's checksum, which a level map binds itself to: the CRC-32 of every
/// lump but the entity lump, in lump order; an entity edit keeps it, any
/// other change does not, and a file that is not a map is refused.
/// </summary>
public sealed class BspMapChecksumTests
{
    private static async Task<byte[]> MapAsync(string entities, byte[] planes)
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Entities, EntityLump.Write(EntityLump.ParseText(entities)).Data, 0);
        bsp.SetLump(BspLump.Planes, planes);
        bsp.SetLump(BspLump.Vertexes, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>
    /// The checksum is the CRC-32 of the lumps' bytes in lump order with the
    /// entity lump left out: an edit to the entities keeps it, an edit to a
    /// plane changes it.
    /// </summary>
    [Fact]
    public async Task TheChecksumIsEveryLumpButTheEntities()
    {
        byte[] planes = new byte[20];
        planes[0] = 7;
        byte[] map = await MapAsync("{\n\"classname\" \"worldspawn\"\n}\n", planes);

        List<byte> lumps = [];
        for (int lump = 1; lump < BspData.HeaderLumps; lump++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(8 + (16 * lump)));
            int length = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(8 + (16 * lump) + 4));
            lumps.AddRange(map.AsSpan(offset, length).ToArray());
        }

        uint checksum = BspMapChecksum.Compute(map);
        Assert.Equal(Crc32.Compute([.. lumps]), checksum);
        Assert.Equal(checksum, BspMapChecksum.Compute(await MapAsync("{\n\"classname\" \"worldspawn\"\n\"message\" \"edited\"\n}\n", planes)));
        planes[1] = 1;
        Assert.NotEqual(checksum, BspMapChecksum.Compute(await MapAsync("{\n\"classname\" \"worldspawn\"\n}\n", planes)));
    }

    /// <summary>Bytes that are not a map, and a map whose directory points outside it, are refused with a message.</summary>
    [Fact]
    public async Task AFileThatIsNotAMapIsRefused()
    {
        Assert.Equal(
            "not a BSP: the header is missing or cut short",
            Assert.Throws<InvalidBspException>(() => BspMapChecksum.Compute(new byte[2000])).Message);
        Assert.Equal(
            "not a BSP: the header is missing or cut short",
            Assert.Throws<InvalidBspException>(() => BspMapChecksum.Compute("VBSP"u8)).Message);

        byte[] map = await MapAsync("{\n\"classname\" \"worldspawn\"\n}\n", new byte[20]);
        BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(8 + 16 + 4), map.Length);
        Assert.Equal(
            $"lump 1 runs outside the file (offset {BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(8 + 16))}, length {map.Length})",
            Assert.Throws<InvalidBspException>(() => BspMapChecksum.Compute(map)).Message);
    }
}
