using System.Buffers.Binary;
using System.Text;
using SourceSharp.MapFormats.Zip;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Zip;

/// <summary>
/// Facts for the STORE-only pakfile reader and writer, from
/// And the reference implementation.
/// </summary>
public class ZipArchiveTests
{
    private static byte[] BuildOneEntryPak(string name, byte[] data)
    {
        ZipArchiveWriter writer = new();
        writer.Add(name, data);
        return writer.ToBytes();
    }

    [Fact]
    public void LocalFileHeaderStartsWithThePkSignature()
    {
        // PKID(3,4), the local file header signature. On a
        // little-endian machine the bytes read "PK\x03\x04".
        byte[] pak = BuildOneEntryPak("materials/a.vmt", [1, 2, 3]);

        Assert.Equal([0x50, 0x4B, 0x03, 0x04], pak[..4]);
    }

    [Fact]
    public void StoredEntryDeclaresVersionNeededTen()
    {
        byte[] pak = BuildOneEntryPak("a.txt", [1]);

        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(4)));
    }

    [Fact]
    public void FlagsAreZeroSoThereIsNeverADataDescriptor()
    {
        // Hardcode 0. Bit 3 would mean the sizes
        // follow the data in a PK\x07\x08 record, which is what
        // System.IO.Compression writes and Source cannot read.
        byte[] pak = BuildOneEntryPak("a.txt", [1, 2, 3, 4]);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(6)));
        Assert.DoesNotContain("PK\b", Encoding.Latin1.GetString(pak), StringComparison.Ordinal);
    }

    [Fact]
    public void CompressionMethodIsZeroForAStoredEntry()
    {
        byte[] pak = BuildOneEntryPak("a.txt", [1]);

        Assert.Equal(
            (ushort)ZipCompressionMethod.Store,
            BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(8)));
    }

    [Fact]
    public void ModificationTimeAndDateAreZero()
    {
        // The reference implementation. Nothing derives them from the clock, which
        // is why a pak is reproducible at all.
        byte[] pak = BuildOneEntryPak("a.txt", [1]);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(10)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(12)));
    }

    [Fact]
    public void ExtraFieldLengthIsZeroBecausePcAlignmentIsZero()
    {
        // The reference's CalculatePadding returns 0 when its alignment size is 0,
        // and it always is on the PC: the
        // constructor sets it and ForceAlignment has no call site.
        byte[] pak = BuildOneEntryPak("a.txt", [1]);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(28)));
        Assert.Equal(0, ZipFormat.PcAlignment);
    }

    [Fact]
    public void FileNameIsWrittenWithoutATerminator()
    {
        // Put(pFilename, V_strlen(pFilename)).
        byte[] pak = BuildOneEntryPak("ab.txt", [9]);
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(26));

        Assert.Equal(6, nameLength);
        Assert.Equal("ab.txt", Encoding.Latin1.GetString(pak, 30, nameLength));
        Assert.Equal(9, pak[30 + nameLength]);
    }

    [Fact]
    public void NameIsLowerCased()
    {
        // Q_strlower, unconditionally.
        ZipArchiveWriter writer = new();
        ZipEntry entry = writer.Add("Materials/Metal/MetalWall.VMT", [1]);

        Assert.Equal("materials/metal/metalwall.vmt", entry.Name);
    }

    [Fact]
    public void CentralDirectoryDeclaresVersionMadeByTwenty()
    {
        // The reference implementation, with the comment "This is the version that the
        // winzip that I have writes."
        byte[] pak = BuildOneEntryPak("a.txt", [1]);
        int directory = FindCentralDirectory(pak);

        Assert.Equal(
            ZipFormat.VersionMadeBy,
            BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(directory + 4)));
    }

    [Fact]
    public void CentralDirectoryOffsetIsRelativeToTheStartOfThePak()
    {
        // Every offset subtracts
        // zipOffsetInStream, because the pak is embedded in a BSP at a
        // non-zero file offset.
        byte[] pak = BuildOneEntryPak("a.txt", [1]);
        int directory = FindCentralDirectory(pak);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(directory + 42)));
    }

    [Fact]
    public void ExternalAttributesAreZero()
    {
        // "usually something, but zero is OK as if the
        // input came from stdin".
        byte[] pak = BuildOneEntryPak("a.txt", [1]);
        int directory = FindCentralDirectory(pak);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(directory + 38)));
    }

    [Fact]
    public void DefaultCommentIsTheThirtyTwoByteXzipString()
    {
        // The reference's MakeXZipCommentString: "XZP%c %d" over a
        // zeroed 32-byte buffer, with '1' for the compatible format and 0 for
        // the alignment. Every pak the reference compiler writes carries it.
        byte[] comment = ZipArchiveWriter.BuildComment();

        Assert.Equal(ZipFormat.CommentLength, comment.Length);
        Assert.Equal("XZP1 0", Encoding.Latin1.GetString(comment, 0, 6));
        Assert.All(comment[6..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EndRecordDeclaresTheCommentLength()
    {
        byte[] pak = BuildOneEntryPak("a.txt", [1]);
        int record = pak.Length - ZipFormat.CommentLength - ZipFormat.EndOfCentralDirectorySize;

        Assert.Equal(
            ZipFormat.EndOfCentralDirectorySignature,
            BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(record)));
        Assert.Equal(
            ZipFormat.CommentLength,
            BinaryPrimitives.ReadUInt16LittleEndian(pak.AsSpan(record + 20)));
    }

    [Fact]
    public void ZeroLengthEntriesAreSilentlyDropped()
    {
        // Both skip an entry whose compressed
        // size is not positive, and realNumFiles counts only what was
        // written. An empty file put into a pak does not come out of it.
        ZipArchiveWriter writer = new();
        writer.Add("empty.txt", []);
        writer.Add("full.txt", [1, 2, 3]);

        ZipArchiveReader read = ReadBack(writer.ToBytes());

        Assert.Equal(["full.txt"], read.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Crc32MatchesTheWellKnownValueForTheStandardTestVector()
    {
        // And the table: init 0xFFFFFFFF,
        // reflected polynomial 0xEDB88320, final xor 0xFFFFFFFF. The CRC-32 of
        // "123456789" under that definition is the published 0xCBF43926.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Crc32OfNothingIsZero()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void CrcIsComputedOverTheUncompressedBytes()
    {
        // "CRC is before compression".
        byte[] data = Encoding.Latin1.GetBytes("materials go here");
        ZipEntry entry = new("a.vmt", data);

        Assert.Equal(Crc32.Compute(data), entry.Crc);
    }

    [Fact]
    public async Task RoundTripOfASynthesisedPakIsByteIdentical()
    {
        ZipArchiveWriter writer = new();
        writer.Add("materials/a.vmt", Encoding.Latin1.GetBytes("\"LightmappedGeneric\"\n{\n}\n"));
        writer.Add("materials/b.vmt", Encoding.Latin1.GetBytes("\"UnlitGeneric\"\n{\n}\n"));
        writer.Add("stale.txt", Encoding.Latin1.GetBytes("x"));

        byte[] original = writer.ToBytes();
        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(original, CancellationToken.None);

        Assert.Equal(original, read.ToWriter().ToBytes());
    }

    [Fact]
    public async Task RoundTripPreservesEntryOrder()
    {
        // Stock cannot promise this: entries live in a CUtlRBTree keyed on a
        // CUtlSymbol id, which is the order each name
        // was first interned anywhere in the process. This reader keeps a
        // list, which is what makes the byte-exact round trip possible.
        ZipArchiveWriter writer = new();
        writer.Add("zzz.txt", [1]);
        writer.Add("aaa.txt", [2]);
        writer.Add("mmm.txt", [3]);

        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(writer.ToBytes(), CancellationToken.None);

        Assert.Equal(["zzz.txt", "aaa.txt", "mmm.txt"], read.Entries.Select(e => e.Name));
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        // Lower-cases before looking up, against names
        // that were lower-cased on the way in.
        ZipArchiveWriter writer = new();
        writer.Add("materials/metal.vmt", [7]);

        ZipArchiveReader read = ReadBack(writer.ToBytes());

        Assert.NotNull(read.Find("MATERIALS/METAL.VMT"));
    }

    [Fact]
    public void EmptyPakWithNoEntriesIsValid()
    {
        // Zero entries returns quietly. Every BSP
        // without embedded content has one.
        ZipArchiveWriter writer = new();
        ZipArchiveReader read = ReadBack(writer.ToBytes());

        Assert.Empty(read.Entries);
    }

    [Fact]
    public async Task PakShorterThanAnEndRecordIsRejected()
    {
        // The one size sanity check either parser has.
        InvalidZipException error = await Assert.ThrowsAsync<InvalidZipException>(
            async () => await ZipArchiveReader.ParseAsync(new byte[10], CancellationToken.None));

        Assert.Contains("shorter than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PakWithNoEndRecordIsRejected()
    {
        InvalidZipException error = await Assert.ThrowsAsync<InvalidZipException>(
            async () => await ZipArchiveReader.ParseAsync(new byte[64], CancellationToken.None));

        Assert.Contains("no end-of-central-directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeflateIsRejected()
    {
        // Method 8 is accepted NOWHERE in the tree: the gates at
        // Admit only 0 and 14. This is the
        // fact that justifies not using System.IO.Compression, which can only
        // write method 8.
        byte[] pak = BuildOneEntryPak("a.txt", [1, 2, 3]);
        int directory = FindCentralDirectory(pak);
        BinaryPrimitives.WriteUInt16LittleEndian(pak.AsSpan(directory + 10), 8);

        InvalidZipException error = await Assert.ThrowsAsync<InvalidZipException>(
            async () => await ZipArchiveReader.ParseAsync(pak, CancellationToken.None));

        Assert.Contains("unsupported compression method 8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LzmaEntryIsAcceptedByTheDirectoryWalk()
    {
        // Method 14 IS a legal method, so a pak carrying
        // one must parse -- it is only the CONTENTS that cannot be produced.
        ZipArchiveWriter writer = new();
        writer.Add(new ZipEntry(
            "big.vtf",
            [9, 20, 5, 0, 0x5D, 0, 0, 0x80, 0, 0xAA, 0xBB],
            ZipCompressionMethod.Lzma,
            crc: 0x12345678,
            uncompressedSize: 4096));

        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(writer.ToBytes(), CancellationToken.None);

        Assert.False(read.Entries[0].IsStored);
    }

    [Fact]
    public void LzmaFramingIsTheZipSpecFormNotTheReferenceLzmaHeader()
    {
        // Strips the reference's lzma_header_t -- the 'LZMA'
        // magic plus two sizes -- and substitutes the ZIP
        // 5.8.8 preamble: two SDK version bytes, a little-endian uint16
        // properties size, and five properties bytes. There is NO magic to
        // look for.
        ZipEntry entry = new(
            "big.vtf",
            [9, 20, 5, 0, 0x5D, 0, 0, 0x80, 0, 0xAA],
            ZipCompressionMethod.Lzma,
            crc: 0,
            uncompressedSize: 4096);

        LzmaEntryInfo info = LzmaEntryInfo.From(entry);

        Assert.Equal(9, info.SdkVersionMajor);
        Assert.Equal(20, info.SdkVersionMinor);
        Assert.Equal(5, info.PropertiesSize);
        Assert.Equal(4096u, info.UncompressedSize);
        Assert.True(info.IsWellFormed);
    }

    [Fact]
    public void LzmaPropertiesSizeOtherThanFiveIsReportedAsUnloadable()
    {
        // Rejects anything but LZMA_PROPS_SIZE, so
        // such an entry would not load in the game either. Saying so is more
        // useful than saying "unsupported".
        ZipEntry entry = new(
            "big.vtf",
            [9, 20, 7, 0, 0, 0, 0, 0, 0],
            ZipCompressionMethod.Lzma,
            crc: 0,
            uncompressedSize: 16);

        LzmaEntryInfo info = LzmaEntryInfo.From(entry);

        Assert.False(info.IsWellFormed);
        Assert.Contains("would not load in the game", info.Describe("big.vtf"), StringComparison.Ordinal);
    }

    [Fact]
    public void LzmaInfoRefusesAStoredEntry()
    {
        Assert.Throws<InvalidZipException>(() => LzmaEntryInfo.From(new ZipEntry("a.txt", [1])));
    }

    [Fact]
    public void LzmaInfoRefusesATruncatedPayload()
    {
        ZipEntry entry = new(
            "a.vtf", [9, 20], ZipCompressionMethod.Lzma, crc: 0, uncompressedSize: 1);

        Assert.Throws<InvalidZipException>(() => LzmaEntryInfo.From(entry));
    }

    [Fact]
    public async Task EntryPointingPastTheEndOfThePakIsRejected()
    {
        // Stock does no bounds check at all, so a
        // corrupt pak reads whatever memory follows. A library must not.
        byte[] pak = BuildOneEntryPak("a.txt", [1, 2, 3]);
        int directory = FindCentralDirectory(pak);
        BinaryPrimitives.WriteUInt32LittleEndian(pak.AsSpan(directory + 20), 0xFFFF);

        await Assert.ThrowsAsync<InvalidZipException>(
            async () => await ZipArchiveReader.ParseAsync(pak, CancellationToken.None));
    }

    [Fact]
    public async Task LocalHeaderSignatureIsVerifiedEvenThoughStockNeverLooks()
    {
        // A DELIBERATE ADDITION. The reference implementation derives the data offset
        // arithmetically from the CENTRAL header and never reads the local
        // one, so a pak whose two headers disagree silently yields wrong
        // bytes. Checking costs one comparison.
        byte[] pak = BuildOneEntryPak("a.txt", [1, 2, 3]);
        pak[0] = 0x00;

        InvalidZipException error = await Assert.ThrowsAsync<InvalidZipException>(
            async () => await ZipArchiveReader.ParseAsync(pak, CancellationToken.None));

        Assert.Contains("local file header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataSurvivesARoundTrip()
    {
        byte[] data = Encoding.Latin1.GetBytes("patch\n{\n\t\"include\"\t\t\"materials/a.vmt\"\n}\n");
        ZipArchiveWriter writer = new();
        writer.Add("materials/maps/x/a.vmt", data);

        ZipArchiveReader read = await ZipArchiveReader.ParseAsync(writer.ToBytes(), CancellationToken.None);

        Assert.Equal(data, read.Find("materials/maps/x/a.vmt")!.Data);
    }

    [Fact]
    public async Task WriteAsyncProducesTheSameBytesAsToBytes()
    {
        ZipArchiveWriter writer = new();
        writer.Add("a.txt", [1, 2, 3]);

        using MemoryStream stream = new();
        await writer.WriteAsync(stream, CancellationToken.None);

        Assert.Equal(writer.ToBytes(), stream.ToArray());
    }

    private static ZipArchiveReader ReadBack(byte[] pak) =>
        ZipArchiveReader.ParseAsync(pak, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private static int FindCentralDirectory(byte[] pak)
    {
        for (int offset = pak.Length - ZipFormat.EndOfCentralDirectorySize; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(offset)) ==
                ZipFormat.EndOfCentralDirectorySignature)
            {
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(offset + 16));
            }
        }

        throw new InvalidOperationException("no end-of-central-directory record in the test pak");
    }
}
