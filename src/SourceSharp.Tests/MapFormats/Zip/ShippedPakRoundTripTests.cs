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
/// The Phase 1b round-trip gate, run against a pak that ships inside real
/// game content rather than one this test wrote.
/// </summary>
/// <remarks>
/// <para>
/// Carries 595 files in lump 40. A
/// synthesised pak only proves this writer agrees with itself; a shipped one
/// proves it agrees with the compiler that made the map -- which is what
/// "byte-exact" has to mean for a pak the game will load.
/// </para>
/// <para>
/// Skipped, not passed, when the map is not beside the binary: see
/// <see cref="RepoSourceFactAttribute"/> for why that distinction is enforced
/// here.
/// </para>
/// </remarks>
public class ShippedPakRoundTripTests
{
    private const string MapPath = "game/mod_sharp/maps/dm_lockdown.bsp";

    /// <summary>
    /// The BSP header's fixed size: an ident, a version, 64 lump entries of
    /// four <c>int</c>s each, and a map revision
    /// </summary>
    private const int LumpTableOffset = 8;

    private const int LumpEntrySize = 16;

    private static byte[] ReadPakLump()
    {
        string path = RepoSourceFactAttribute.Find(MapPath)!;
        byte[] bsp = File.ReadAllBytes(path);

        int entry = LumpTableOffset + (ZipFormat.PakFileLumpIndex * LumpEntrySize);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(entry));
        int length = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(entry + 4));

        return bsp.AsSpan(offset, length).ToArray();
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakReadsBackEveryEntry()
    {
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        Assert.NotEmpty(read.Entries);
        Assert.All(read.Entries, e => Assert.True(e.IsStored));
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakRoundTripsByteForByte()
    {
        // THE GATE. Read it, write it, compare the bytes.
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        Assert.Equal(pak, read.ToWriter().ToBytes());
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakStoredCrcsAgreeWithTheDataRecomputed()
    {
        // Nothing in Source ever verifies these -- the CRC is stored at
        // And re-emitted and never compared. So this is the
        // check that the CRC implementation here is the one the compiler used,
        // measured against 595 files of real content rather than one vector.
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        Assert.All(read.Entries, e => Assert.Equal(e.Crc, Crc32.Compute(e.Data)));
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakNamesAreAllLowerCase()
    {
        // Lower-cased on the way in and on the
        // way out, so a shipped pak has no mixed-case name in it.
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        Assert.All(read.Entries, e => Assert.Equal(e.Name.ToLowerInvariant(), e.Name));
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakHasNoZipCommentAtAll()
    {
        // NOT WHAT THE REFERENCE PREDICTS, and the reason the writer's comment is
        // settable. The reference's MakeXZipCommentString is unconditional,
        // and its size calculation
        // calculation notes that "All processed zip files will have a comment
        // string". This shipped HL2DM map has a comment length of zero: it was
        // built by a vbsp that predates the XZIP work. A writer that always
        // emitted the 32-byte comment could not round-trip shipped content.
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        Assert.Empty(read.Comment);
    }

    [RepoSourceFact(MapPath)]
    public async Task ShippedPakUsesNoExtraFieldsBecauseThePcDoesNotAlign()
    {
        // The observable consequence of ZipFormat.PcAlignment being 0: every
        // entry's data begins immediately after its name, so the payloads are
        // contiguous and the first local header sits at offset 0.
        byte[] pak = ReadPakLump();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(pak, CancellationToken.None);

        long expected = 0;
        foreach (ZipEntry entry in read.Entries)
        {
            expected += ZipFormat.LocalFileHeaderSize + entry.Name.Length + entry.Data.Length;
        }

        // The central directory starts exactly where the payloads end.
        int record = pak.Length - ZipFormat.EndOfCentralDirectorySize;
        uint directoryStart = BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(record + 16));

        Assert.Equal(expected, directoryStart);
    }
}
