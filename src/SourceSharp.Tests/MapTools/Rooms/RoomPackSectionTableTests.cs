//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The pack's section table: each section's frame and revision read where
/// there is one, listed without them where there is not, and a pack whose
/// sections run past its end refused.
/// </summary>
public sealed class RoomPackSectionTableTests
{
    /// <summary>
    /// A framed section of each codec shows its codec, decoded length and
    /// revision; <c>ROOM</c>, <c>CMPL</c>, a section too short for a frame
    /// and one with an unknown codec byte show none; <c>LENT</c> shows its
    /// frame and no revision; a payload that does not decode, or whose
    /// recorded length is too small or too large to hold a revision, shows
    /// no revision.
    /// </summary>
    [Fact]
    public void EachSectionShowsWhatItsFrameHolds()
    {
        byte[] payload = [0, 0, 0, 3, 1, 2, 3, 4];
        Assert.True(NavCompression.TryParse("deflate", out NavCompression deflate));
        Assert.True(NavCompression.TryParse("brotli", out NavCompression brotli));

        Check("ROOM", Framed(0, payload, payload.Length), null, null, null);
        Check(RoomCompileIds.PackSection, new byte[16], null, null, null);
        Check("ECNT", [0, 0, 0], null, null, null);
        Check("ECNT", Framed(3, payload, payload.Length), null, null, null);
        Check("ECNT", Framed(0, payload, payload.Length), 0, payload.Length, 3);
        Check("GEO1", Framed(1, deflate.Compress(payload), payload.Length), 1, payload.Length, 3);
        Check("NVR2", Framed(2, brotli.Compress(payload), payload.Length), 2, payload.Length, 3);
        Check(RoomLibraryEntities.SectionTag, Framed(0, payload, payload.Length), 0, payload.Length, null);
        Check("COL0", Framed(1, [0xFF, 0xFF, 0xFF], payload.Length), 1, payload.Length, null);
        Check("ENT0", Framed(0, [1, 2, 3], 3), 0, 3, null);
        Check("NAM0", Framed(0, payload, long.MaxValue), 0, long.MaxValue, null);
    }

    /// <summary>The hash prefix is the stored bytes' SHA-256, so equal sections read alike and others do not.</summary>
    [Fact]
    public void TheHashPrefixFollowsTheBytes()
    {
        RoomPackSectionInfo a = RoomPackSectionTable.Describe("r", new RoomPackSection("ROOM", 0, 3), [1, 2, 3]);
        RoomPackSectionInfo b = RoomPackSectionTable.Describe("s", new RoomPackSection("ROOM", 9, 3), [1, 2, 3]);
        RoomPackSectionInfo c = RoomPackSectionTable.Describe("r", new RoomPackSection("ROOM", 0, 3), [1, 2, 4]);
        Assert.Equal(16, a.Sha256.Length);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
    }

    /// <summary>
    /// A written pack reads back as its library sections then each room's,
    /// with offsets and lengths from its index; the text has one line each,
    /// the library's owner written <c>(library)</c> and a missing field as
    /// a dash.
    /// </summary>
    [Fact]
    public async Task APackReadsBackInOrderAndFormats()
    {
        byte[] pack = await PackAsync();
        using MemoryStream stream = new(pack);
        IReadOnlyList<RoomPackSectionInfo> table = await RoomPackSectionTable.ReadAsync(stream);
        Assert.Equal(["CMPL", "ROOM", "ECNT", "GEO0", "NVR1"], table.Select(s => s.Tag));
        Assert.Null(table[0].Room);
        Assert.Equal("hub", table[1].Room);
        foreach (RoomPackSectionInfo section in table)
        {
            Assert.True(section.Offset + section.Length <= pack.Length);
        }

        string text = RoomPackSectionTable.Format(table);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        Assert.Equal("owner tag offset length codec decoded revision sha256", lines[0]);
        Assert.Matches(@"^\(library\) CMPL \d+ 16 - - - [0-9a-f]{16}$", lines[1]);
        Assert.Matches(@"^hub ROOM \d+ 3 - - - [0-9a-f]{16}$", lines[2]);
        Assert.Matches(@"^hub ECNT \d+ 17 none 8 3 [0-9a-f]{16}$", lines[3]);
        Assert.Matches(@"^hub GEO0 \d+ \d+ deflate 8 3 [0-9a-f]{16}$", lines[4]);
        Assert.Matches(@"^hub NVR1 \d+ \d+ brotli 8 3 [0-9a-f]{16}$", lines[5]);
        Assert.Throws<ArgumentNullException>(() => RoomPackSectionTable.Format(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPackSectionTable.ReadAsync(null!));
    }

    /// <summary>
    /// A pack cut short, read from a stream that cannot tell its length (so
    /// the index alone cannot see it), is refused at the first section that
    /// runs past the end.
    /// </summary>
    [Fact]
    public async Task APackCutShortIsRefused()
    {
        byte[] pack = await PackAsync();
        using ForwardOnlyStream cut = new(pack[..^4]);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPackSectionTable.ReadAsync(cut));
        Assert.Contains("NVR1 section of room \"hub\" runs past the pack's end", refused.Message, StringComparison.Ordinal);

        byte[] library = await PackAsync(rooms: false);
        using ForwardOnlyStream cutLibrary = new(library[..^4]);
        refused = await Assert.ThrowsAsync<LinkException>(() => RoomPackSectionTable.ReadAsync(cutLibrary));
        Assert.Contains("CMPL section runs past", refused.Message, StringComparison.Ordinal);
    }

    private static void Check(string tag, byte[] bytes, int? codec, long? decoded, int? revision)
    {
        RoomPackSectionInfo info = RoomPackSectionTable.Describe("r", new RoomPackSection(tag, 100, bytes.Length), bytes);
        Assert.Equal(("r", tag, 100L, (long)bytes.Length), (info.Room, info.Tag, info.Offset, info.Length));
        Assert.True(codec == info.Codec, $"{tag}: codec {info.Codec}");
        Assert.True(decoded == info.DecodedLength, $"{tag}: decoded {info.DecodedLength}");
        Assert.True(revision == info.Revision, $"{tag}: revision {info.Revision}");
    }

    private static byte[] Framed(byte codec, byte[] stored, long decoded)
    {
        byte[] bytes = new byte[9 + stored.Length];
        bytes[0] = codec;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), decoded);
        stored.CopyTo(bytes, 9);
        return bytes;
    }

    private static async Task<byte[]> PackAsync(bool rooms = true)
    {
        byte[] payload = [0, 0, 0, 3, 1, 2, 3, 4];
        Assert.True(NavCompression.TryParse("deflate", out NavCompression deflate));
        Assert.True(NavCompression.TryParse("brotli", out NavCompression brotli));
        RoomPackItem item = new("hub", new byte[] { 7, 8, 9 })
        {
            Extra =
            [
                new RoomPackSectionData("ECNT", Framed(0, payload, payload.Length)),
                new RoomPackSectionData("GEO0", Framed(1, deflate.Compress(payload), payload.Length)),
                new RoomPackSectionData("NVR1", Framed(2, brotli.Compress(payload), payload.Length)),
            ],
        };
        using MemoryStream stream = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(Guid.Empty)], rooms ? [item] : [], stream, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>A stream that reads forward only and does not know its length.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
