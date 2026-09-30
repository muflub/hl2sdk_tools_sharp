//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The <c>NSPC</c> section of a combined pack (the rooms design, 17.11): its
/// round trip, every way it can be damaged and how each is named, its hold
/// on the pack's index, and the two readers built on it (the namespaces of
/// a pack, and a run of rooms copied whole).
/// </summary>
public sealed class RoomPackNamespacesTests
{
    private static readonly string VmfA = RoomPackNamespaces.Digest("a"u8);

    private static readonly string VmfB = RoomPackNamespaces.Digest("b"u8);

    private static readonly string World = RoomPackNamespaces.SingletonDigest([], []);

    private static IReadOnlyList<RoomPackNamespace> Two =>
    [
        new RoomPackNamespace("base", "../base.vmf", VmfA, World, 0, 2),
        new RoomPackNamespace("caves", "caves/caves.vmf", VmfB, World, 2, 1) { NameKeys = "friend,enemy" },
    ];

    // ---- round trip -------------------------------------------------------------

    /// <summary>What is written is read back, name keys and all; a namespace without keys reads without.</summary>
    [Fact]
    public void TheSectionReadsBackWhatWasWritten()
    {
        RoomPackSectionData section = RoomPackNamespaces.ToSection(Two);
        Assert.Equal(RoomPackNamespaces.SectionTag, section.Tag);
        IReadOnlyList<RoomPackNamespace> read = RoomPackNamespaces.Read(section.Bytes.Span)!;
        Assert.Equal(Two, read);
        Assert.Null(read[0].NameKeys);
        Assert.Null(read[0].NameKeySet);
        Assert.Equal(["enemy", "friend"], read[1].NameKeySet!.Order(StringComparer.Ordinal));
        Assert.Equal("caves.", read[1].Prefix);
        Assert.Same(read[1], RoomPackNamespaces.Find(read, "caves"));
        Assert.Null(RoomPackNamespaces.Find(read, "Caves"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("a"u8)), VmfA);
    }

    /// <summary>The writer refuses what the reader would: no namespace, a key that is not a key, a digest that is not one, a negative place.</summary>
    [Fact]
    public void TheWriterRefusesWhatCannotBeRead()
    {
        Assert.Throws<ArgumentException>(() => RoomPackNamespaces.ToSection([]));
        Assert.Throws<ArgumentException>(() => RoomPackNamespaces.ToSection([Two[0] with { Key = "3x3" }]));
        Assert.Throws<ArgumentException>(() => RoomPackNamespaces.ToSection([Two[0] with { VmfSha256 = "abc" }]));
        Assert.Throws<ArgumentException>(() => RoomPackNamespaces.ToSection([Two[0] with { SingletonsSha256 = new string('z', 64) }]));
        Assert.Throws<ArgumentException>(() => RoomPackNamespaces.ToSection([Two[0] with { VmfSha256 = VmfA.ToUpperInvariant() + "00" }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomPackNamespaces.ToSection([Two[0] with { FirstRoom = -1 }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomPackNamespaces.ToSection([Two[0] with { RoomCount = -1 }]));
    }

    // ---- damage ---------------------------------------------------------------------

    /// <summary>A section of another revision reads as none: the pack is then plain, as an older build reads it.</summary>
    [Fact]
    public void AnotherRevisionReadsAsNoSection()
    {
        byte[] bytes = RoomPackNamespaces.ToSection(Two).Bytes.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(9), RoomPackNamespaces.Revision + 1);
        Assert.Null(RoomPackNamespaces.Read(bytes));
    }

    /// <summary>Every damage the section can take is refused, each named.</summary>
    [Theory]
    [InlineData("short", "is 4 bytes, shorter than its 9-byte header")]
    [InlineData("codec", "has codec 1; this build reads codec 0 (none)")]
    [InlineData("length", "says 999 bytes but holds")]
    [InlineData("none", "claims 0 namespaces; a pack with namespaces has 1 to 1048576")]
    [InlineData("cut", "is cut short")]
    [InlineData("key", "names namespace \"3x3\", which does not start with a letter")]
    [InlineData("twice", "names namespace \"BASE\" twice, ignoring case")]
    [InlineData("negative", "gives namespace base rooms from -1, 2 of them")]
    [InlineData("trailing", "has 3 bytes after its last namespace")]
    [InlineData("utf8", "holds text that is not UTF-8")]
    public void DamageIsRefusedByName(string damage, string message)
    {
        byte[] bytes = damage switch
        {
            "short" => [0, 0, 0, 0],
            "codec" => With(RoomPackNamespaces.ToSection(Two).Bytes.ToArray(), b => b[0] = 1),
            "length" => With(RoomPackNamespaces.ToSection(Two).Bytes.ToArray(), b => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(1), 999)),
            "none" => Frame(Payload(w => { Int(w, 1); Int(w, 0); })),
            "cut" => Frame(RoomPackNamespaces.ToSection(Two).Bytes.ToArray()[9..^5]),
            "key" => Frame(Payload(w => { Int(w, 1); Int(w, 1); Namespace(w, "3x3", 0, 1); })),
            "twice" => Frame(Payload(w => { Int(w, 1); Int(w, 2); Namespace(w, "base", 0, 1); Namespace(w, "BASE", 1, 1); })),
            "negative" => Frame(Payload(w => { Int(w, 1); Int(w, 1); Namespace(w, "base", -1, 2); })),
            "trailing" => Frame([.. RoomPackNamespaces.ToSection(Two).Bytes.ToArray()[9..], 1, 2, 3]),
            "utf8" => Frame(Payload(w => { Int(w, 1); Int(w, 1); Int(w, 2); w.Write([0xC3, 0x28]); })),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        LinkException refused = Assert.Throws<LinkException>(() => RoomPackNamespaces.Read(bytes));
        Assert.StartsWith("the room pack's NSPC section ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
    }

    // ---- against the index ------------------------------------------------------------

    /// <summary>
    /// The namespaces must cover the pack's rooms in order, each from where
    /// the one before it ends, every room named after its namespace: a gap,
    /// an overlap, a run past the last room, a room of another namespace's
    /// name and rooms left over are each refused by name.
    /// </summary>
    [Fact]
    public async Task TheNamespacesMustCoverThePacksRoomsInOrder()
    {
        RoomPackIndex index = await IndexAsync("base.hub", "base.other", "caves.hub");
        RoomPackNamespaces.Check(Two, index);

        void Refused(string message, params RoomPackNamespace[] spaces)
        {
            LinkException refused = Assert.Throws<LinkException>(() => RoomPackNamespaces.Check(spaces, index));
            Assert.Equal("the room pack's NSPC section " + message + ".", refused.Message);
        }

        Refused("starts namespace base at room 1; its rooms follow the namespace before it, from room 0", Two[0] with { FirstRoom = 1 }, Two[1]);
        Refused("starts namespace caves at room 1; its rooms follow the namespace before it, from room 2", Two[0], Two[1] with { FirstRoom = 1 });
        Refused("gives namespace caves rooms 2 to 3; the pack holds 3", Two[0], Two[1] with { RoomCount = 2 });
        Refused("puts room \"caves.hub\" in namespace base; its rooms are named base.<room>", Two[0] with { RoomCount = 3 });
        Refused("covers 2 of the pack's 3 rooms; every room of a pack with namespaces belongs to one", Two[0]);
    }

    /// <summary>
    /// A plain pack has no namespaces; a pack with them reads them back, held
    /// to its index; a damaged section fails the read.
    /// </summary>
    [Fact]
    public async Task APacksNamespacesAreReadWithItsIndex()
    {
        using (MemoryStream plain = await PackAsync([], "hub"))
        {
            Assert.Null(await RoomPack.ReadNamespacesAsync(plain, await RoomPack.ReadIndexAsync(plain)));
        }

        using (MemoryStream spaced = await PackAsync([RoomPackNamespaces.ToSection(Two)], "base.hub", "base.other", "caves.hub"))
        {
            Assert.Equal(Two, await RoomPack.ReadNamespacesAsync(spaced, await RoomPack.ReadIndexAsync(spaced)));
        }

        using MemoryStream wrong = await PackAsync([RoomPackNamespaces.ToSection(Two)], "base.hub", "base.other");
        RoomPackIndex index = await RoomPack.ReadIndexAsync(wrong);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadNamespacesAsync(wrong, index));
        Assert.Contains("gives namespace caves rooms 2 to 2; the pack holds 2", refused.Message, StringComparison.Ordinal);
    }

    // ---- copying rooms ----------------------------------------------------------------

    /// <summary>
    /// A run of rooms read whole and written again gives the pack's own
    /// bytes, every section under its tag; an empty run is empty; a run
    /// outside the pack, and a stream that cannot seek, are refused.
    /// </summary>
    [Fact]
    public async Task ARunOfRoomsIsCopiedByteForByte()
    {
        RoomObject hub = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(RoomHarness.Hub()), RoomHarness.Hub(), await RoomHarness.ContextAsync());
        List<RoomPackItem> items =
        [
            await RoomPackItem.CreateAsync(hub),
            await RoomPackItem.CreateAsync(hub with { Definition = hub.Definition with { Name = "other" } }),
        ];
        using MemoryStream written = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(Guid.Empty)], items, written, CancellationToken.None);
        byte[] pack = written.ToArray();

        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<RoomPackItem> copied = await RoomPack.ReadItemsAsync(stream, index, 0, 2);
        Assert.Equal(["hub", "other"], copied.Select(i => i.Name));
        Assert.True(index.Entries[1].Sections.Count > 1);
        using MemoryStream again = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(Guid.Empty)], copied, again, CancellationToken.None);
        Assert.Equal(pack, again.ToArray());

        Assert.Equal("other", Assert.Single(await RoomPack.ReadItemsAsync(stream, index, 1, 1)).Name);
        Assert.Empty(await RoomPack.ReadItemsAsync(stream, index, 2, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RoomPack.ReadItemsAsync(stream, index, 1, 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RoomPack.ReadItemsAsync(stream, index, -1, 1));
        using ForwardOnlyStream forward = new(pack);
        RoomPackIndex forwardIndex = await RoomPack.ReadIndexAsync(forward);
        await Assert.ThrowsAsync<NotSupportedException>(() => RoomPack.ReadItemsAsync(forward, forwardIndex, 0, 1));
    }

    // ---- digests ------------------------------------------------------------------------

    /// <summary>
    /// The singleton digest follows the worldspawn's keys (their values and
    /// their order), the library-wide entities and a skybox a later library
    /// supplies (D29), and nothing else.
    /// </summary>
    [Fact]
    public void TheSingletonDigestFollowsTheWorldAndTheEntities()
    {
        KeyValuePair<string, string> a = new("skyname", "sky_day01"), b = new("detailmaterial", "detail/x");
        VmfChunk sun = RoomLightHarness.Sun();
        string digest = RoomPackNamespaces.SingletonDigest([a, b], [sun]);
        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, RoomPackNamespaces.SingletonDigest([a, b], [RoomLightHarness.Sun()]));
        Assert.NotEqual(digest, RoomPackNamespaces.SingletonDigest([b, a], [sun]));
        Assert.NotEqual(digest, RoomPackNamespaces.SingletonDigest([a, new("detailmaterial", "detail/y")], [sun]));
        Assert.NotEqual(digest, RoomPackNamespaces.SingletonDigest([a, b], [RoomLightHarness.Sun("0 60 0")]));
        Assert.NotEqual(digest, RoomPackNamespaces.SingletonDigest([a, b], []));
        Assert.NotEqual(RoomPackNamespaces.SingletonDigest([new("ab", "c")], []), RoomPackNamespaces.SingletonDigest([new("a", "bc")], []));

        // D29: a later library's skybox is folded (its name too); none leaves the digest as it was.
        Assert.Equal(digest, RoomPackNamespaces.SingletonDigest([a, b], [sun], null));
        string later = RoomPackNamespaces.SingletonDigest([a, b], [sun], "caves.sky");
        Assert.NotEqual(digest, later);
        Assert.NotEqual(later, RoomPackNamespaces.SingletonDigest([a, b], [sun], "halls.sky"));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static byte[] With(byte[] bytes, Action<byte[]> change)
    {
        change(bytes);
        return bytes;
    }

    private static byte[] Payload(Action<MemoryStream> write)
    {
        using MemoryStream payload = new();
        write(payload);
        return payload.ToArray();
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] bytes = new byte[9 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), payload.Length);
        payload.CopyTo(bytes, 9);
        return bytes;
    }

    private static void Int(MemoryStream w, int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        w.Write(b);
    }

    private static void Text(MemoryStream w, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Int(w, bytes.Length);
        w.Write(bytes);
    }

    private static void Namespace(MemoryStream w, string key, int first, int count)
    {
        Text(w, key);
        Text(w, "x.vmf");
        w.Write(new byte[64]);
        Int(w, first);
        Int(w, count);
        Text(w, string.Empty);
    }

    /// <summary>A pack of containers that are not rooms, which an index does not look into.</summary>
    private static async Task<MemoryStream> PackAsync(IReadOnlyList<RoomPackSectionData> library, params string[] names)
    {
        MemoryStream stream = new();
        await RoomPack.SaveAsync(library, [.. names.Select(n => new RoomPackItem(n, new byte[] { 1, 2, 3 }))], stream, CancellationToken.None);
        stream.Position = 0;
        return stream;
    }

    private static async Task<RoomPackIndex> IndexAsync(params string[] names)
    {
        using MemoryStream stream = await PackAsync([], names);
        return await RoomPack.ReadIndexAsync(stream);
    }

    /// <summary>A stream that reads forward only, as a pipe would.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
