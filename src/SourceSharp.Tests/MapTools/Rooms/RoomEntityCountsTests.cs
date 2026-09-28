//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's entity counts (<see cref="RoomEntityCounts"/>): what is counted,
/// how the class table sorts it, the <c>ECNT</c> section's layout, codecs and
/// refusals, the binding to one compile, and the pack carrying the counts
/// for every room, refused ones included, with the fallback to counting the
/// lump for a pack written before them.
/// </summary>
public sealed class RoomEntityCountsTests(RoomLinkDataFixture fixture) : IClassFixture<RoomLinkDataFixture>
{
    /// <summary>
    /// Every entity but the worldspawn is counted, by class, in ordinal
    /// order of the class; an entity with no class counts under the empty
    /// class, and a second worldspawn is merged like the first.
    /// </summary>
    [Fact]
    public void EveryEntityButTheWorldspawnIsCountedByClass()
    {
        RoomEntityCounts counts = RoomEntityCounts.Of(Bsp(
            ["worldspawn"], ["info_player_start"], ["light"], ["info_player_start"], ["func_detail"], [], ["worldspawn"]));

        Assert.Equal(
            [new("", 1), new("func_detail", 1), new("info_player_start", 2), new("light", 1)],
            counts.Classes);
        Assert.Equal(5, counts.Entities);
    }

    /// <summary>A room with only its worldspawn, and one with an empty lump, count nothing.</summary>
    [Fact]
    public void ARoomWithOnlyItsWorldspawnCountsNothing()
    {
        Assert.Empty(RoomEntityCounts.Of(Bsp(["worldspawn"])).Classes);
        Assert.Equal(0, RoomEntityCounts.Of(new BspData()).Entities);
    }

    /// <summary>
    /// The shipped table sorts compile-only classes out of the listed
    /// entities and every other class into the edicts; a table with
    /// server-only and spawn-transient rows sorts the first out of the edicts
    /// and keeps the second in them (the spawn's peak must fit too).
    /// </summary>
    [Fact]
    public void TheTableSortsTheCounts()
    {
        RoomEntityCounts counts = RoomEntityCounts.Of(Bsp(
            ["worldspawn"], ["info_player_start"], ["func_detail"], ["logic_relay"], ["logic_relay"], ["infodecal"]));

        EntityTally shipped = counts.Tally(EntityClassTable.Default);
        Assert.Equal(new EntityTally(4, 0, 1), shipped);
        Assert.Equal(4, shipped.Listed);

        EntityClassTable mod = EntityClassTable.Default.With(
        [
            new EntityClassRow("logic_relay", EntityCost.ServerOnly, EntityClassCertainty.OwnerSupplied, "mod"),
            new EntityClassRow("infodecal", EntityCost.SpawnTransient, EntityClassCertainty.Believed, "mod"),
        ]);
        EntityTally modded = counts.Tally(mod);
        Assert.Equal(new EntityTally(2, 2, 1), modded);
        Assert.Equal(4, modded.Listed);

        Assert.Throws<ArgumentNullException>(() => counts.Tally(null!));
        Assert.Throws<ArgumentNullException>(() => RoomEntityCounts.Of(null!));
    }

    /// <summary>
    /// The section's bytes, pinned: codec 0, the payload's length, then the
    /// revision, the class count, and per class its name and count.
    /// </summary>
    [Fact]
    public void TheSectionsBytesArePinned()
    {
        RoomPackSectionData section = RoomEntityCounts.Of(Bsp(["worldspawn"], ["light"], ["light"])).ToSection();

        Assert.Equal(RoomEntityCounts.SectionTag, section.Tag);
        Assert.Equal(
            Convert.FromHexString("00" + "0000000000000015" + "00000001" + "00000001" + "00000005" + "6C69676874" + "00000002"),
            section.Bytes.ToArray());
    }

    /// <summary>Counts round-trip through their section under every codec.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheSectionRoundTripsUnderEveryCodec(byte codecByte)
    {
        RoomLinkCodec codec = (RoomLinkCodec)codecByte;
        RoomEntityCounts counts = RoomEntityCounts.Of(Bsp(["worldspawn"], ["a"], ["b"], ["b"], ["é"], []));
        byte[] bytes = counts.ToSection(codec).Bytes.ToArray();
        Assert.Equal((byte)codec, bytes[0]);

        RoomEntityCounts read = RoomEntityCounts.Read(bytes, "r")!;
        Assert.Equal(counts.Classes, read.Classes);
        Assert.Equal(counts.Entities, read.Entities);
    }

    /// <summary>An absent section reads as no counts, and so does one of a revision this build does not read.</summary>
    [Fact]
    public void AnAbsentOrUnknownRevisionSectionReadsAsNone()
    {
        Assert.Null(RoomEntityCounts.Read(null, "r"));
        Assert.Null(RoomEntityCounts.Read(Section(w => { w.Int(2); w.Int(0); }), "r"));
    }

    /// <summary>A codec this build does not read, and a payload that decodes to another length, are refused with the pack's messages.</summary>
    [Fact]
    public void AnUnknownCodecOrALyingLengthIsRefused()
    {
        byte[] bytes = RoomEntityCounts.Of(Bsp(["light"])).ToSection().Bytes.ToArray();

        byte[] codec = (byte[])bytes.Clone();
        codec[0] = 7;
        Assert.Equal(
            "room hub: section ECNT uses codec 7, which this build does not read.",
            Assert.Throws<LinkException>(() => RoomEntityCounts.Read(codec, "hub")).Message);

        byte[] length = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt64BigEndian(length.AsSpan(1), bytes.Length);
        Assert.Equal(
            $"room hub: section ECNT decodes to {bytes.Length - 9} bytes, not the {bytes.Length} it records.",
            Assert.Throws<LinkException>(() => RoomEntityCounts.Read(length, "hub")).Message);
    }

    /// <summary>A payload out of shape is refused, naming the room and the section.</summary>
    [Theory]
    [InlineData("zero", "holds 0 entities of class \"a\".")]
    [InlineData("negative", "holds -3 entities of class \"a\".")]
    [InlineData("order", "holds class \"a\" after \"b\"; the classes are distinct and in ordinal order.")]
    [InlineData("twice", "holds class \"a\" after \"a\"; the classes are distinct and in ordinal order.")]
    [InlineData("trailing", "holds 1 bytes after its end.")]
    [InlineData("overflow", "holds 2147483647 entities of class \"b\".")]
    public void APayloadOutOfShapeIsRefused(string fault, string expected)
    {
        byte[] bytes = fault switch
        {
            "zero" => Section(w => { w.Int(1); w.Int(1); w.String("a"); w.Int(0); }),
            "negative" => Section(w => { w.Int(1); w.Int(1); w.String("a"); w.Int(-3); }),
            "order" => Section(w => { w.Int(1); w.Int(2); w.String("b"); w.Int(1); w.String("a"); w.Int(1); }),
            "twice" => Section(w => { w.Int(1); w.Int(2); w.String("a"); w.Int(1); w.String("a"); w.Int(1); }),
            "trailing" => Section(w => { w.Int(1); w.Int(0); w.Byte(0); }),
            "overflow" => Section(w => { w.Int(1); w.Int(2); w.String("a"); w.Int(1); w.String("b"); w.Int(int.MaxValue); }),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        LinkException refused = Assert.Throws<LinkException>(() => RoomEntityCounts.Read(bytes, "hub"));
        Assert.Equal($"room pack entry \"hub\": its \"ECNT\" section {expected}", refused.Message);
    }

    /// <summary>A payload cut short is refused as truncated.</summary>
    [Fact]
    public void APayloadCutShortIsRefused()
    {
        byte[] bytes = Section(w => { w.Int(1); w.Int(1); w.String("light"); });
        Assert.Equal(
            "room pack entry \"hub\": its \"ECNT\" section is truncated.",
            Assert.Throws<LinkException>(() => RoomEntityCounts.Read(bytes, "hub")).Message);
    }

    /// <summary>
    /// Counts describe one compile: those made from a room's BSP, or bound to
    /// it, are for that room; read ones are for none until bound; and a room
    /// copied with its BSP replaced is counted afresh.
    /// </summary>
    [Fact]
    public void CountsAreBoundToOneCompile()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomEntityCounts own = RoomEntityCounts.Of(hub.Bsp);
        Assert.True(own.IsFor(hub));

        RoomEntityCounts read = RoomEntityCounts.Read(own.ToSection().Bytes.ToArray(), "hub")!;
        Assert.False(read.IsFor(hub));
        Assert.True(read.For(hub.Bsp).IsFor(hub));

        RoomObject copy = RoomHarness.WithLumps(hub, _ => { });
        Assert.False(own.IsFor(copy));
    }

    /// <summary>
    /// A room uses its stored counts while they describe its compile (so the
    /// link reads them rather than the lump), and counts its lump when they
    /// do not or it has none.
    /// </summary>
    [Fact]
    public void ARoomUsesItsStoredCountsWhileTheyFit()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomEntityCounts lie = RoomEntityCounts.Read(Section(w => { w.Int(1); w.Int(1); w.String("npc_lie"); w.Int(9); }), "hub")!;

        RoomObject stored = hub with { EntityCounts = lie.For(hub.Bsp) };
        Assert.Equal(9, stored.CountEntities().Entities);

        RoomObject stale = hub with { EntityCounts = lie };
        Assert.Equal(RoomEntityCounts.Of(hub.Bsp).Classes, stale.CountEntities().Classes);
        Assert.Equal(RoomEntityCounts.Of(hub.Bsp).Classes, hub.CountEntities().Classes);
    }

    /// <summary>
    /// Every room's pack item carries its counts straight after the container,
    /// a room the link refuses included (it has no link sections, but its
    /// entities still count for <c>ssmap rooms</c> and the layout budget),
    /// and the same room always gives the same bytes.
    /// </summary>
    [Fact]
    public async Task EveryPackItemCarriesItsCounts()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomPackItem item = await RoomPackItem.CreateAsync(hub);
        Assert.Equal(RoomEntityCounts.SectionTag, item.Extra[0].Tag);
        Assert.Equal(RoomEntityCounts.Of(hub.Bsp).ToSection().Bytes.ToArray(), item.Extra[0].Bytes.ToArray());
        Assert.Equal(item.Extra[0].Bytes.ToArray(), (await RoomPackItem.CreateAsync(hub)).Extra[0].Bytes.ToArray());

        RoomObject bad = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.DispInfo, new byte[176]));
        RoomPackItem refused = await RoomPackItem.CreateAsync(bad);
        Assert.Equal([RoomEntityCounts.SectionTag], refused.Extra.Select(s => s.Tag));
    }

    /// <summary>
    /// The counts a pack stores are the counts of the room it loads, bound to
    /// that room; a pack written before the counts loads its rooms without
    /// them, and they count to the same numbers from the lump.
    /// </summary>
    [Fact]
    public async Task StoredCountsEqualComputedCountsAndOldPacksFallBack()
    {
        string[] names = ["hub", "end", "hall"];
        List<RoomPackItem> items = [];
        List<RoomPackItem> old = [];
        foreach (string name in names)
        {
            RoomPackItem item = await RoomPackItem.CreateAsync(fixture.Library.Get(name));
            items.Add(item);
            old.Add(item with { Extra = [.. item.Extra.Where(s => s.Tag != RoomEntityCounts.SectionTag)] });
        }

        foreach ((List<RoomPackItem> pack, bool stored) in new[] { (items, true), (old, false) })
        {
            using MemoryStream stream = new(await SaveAsync(pack));
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(stream, index, names);
            foreach (RoomObject room in rooms)
            {
                Assert.Equal(stored, room.EntityCounts is not null);
                Assert.Equal(stored, room.EntityCounts?.IsFor(room) ?? false);
                Assert.Equal(RoomEntityCounts.Of(room.Bsp).Classes, room.CountEntities().Classes);
                Assert.Equal(RoomEntityCounts.Of(fixture.Library.Get(room.Definition.Name).Bsp).Classes, room.CountEntities().Classes);
            }

            stream.Position = 0;
            index = await RoomPack.ReadIndexAsync(stream);
            IReadOnlyDictionary<string, RoomEntityCounts> counts = await RoomPack.ReadEntityCountsAsync(stream, index);
            string[] expected = stored ? [.. names.Order(StringComparer.Ordinal)] : [];
            Assert.Equal(expected, counts.Keys.Order(StringComparer.Ordinal));
            foreach ((string name, RoomEntityCounts room) in counts)
            {
                Assert.Equal(RoomEntityCounts.Of(fixture.Library.Get(name).Bsp).Classes, room.Classes);
            }
        }
    }

    /// <summary>The pack readers refuse null arguments.</summary>
    [Fact]
    public async Task ThePackReadersRefuseNulls()
    {
        using MemoryStream stream = new(await SaveAsync([]));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPack.ReadEntityCountsAsync(null!, index));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPack.ReadEntityCountsAsync(stream, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPack.ReadLibraryOptionsAsync(null!, index));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomPack.ReadLibraryOptionsAsync(stream, null!));
    }

    /// <summary>A BSP whose entity lump holds one entity per class list: its classname, or no key at all when empty.</summary>
    internal static BspData Bsp(params string[][] classes)
    {
        List<BspEntity> entities = [];
        foreach (string[] entity in classes)
        {
            BspEntity e = new();
            if (entity.Length == 0)
            {
                e.Pairs.Add(new BspKeyValue("origin", "0 0 0"));
            }

            foreach (string name in entity)
            {
                e.Pairs.Add(new BspKeyValue("classname", name));
            }

            entities.Add(e);
        }

        BspData bsp = new();
        bsp[BspLump.Entities] = EntityLump.Write(entities);
        return bsp;
    }

    /// <summary>An uncompressed <c>ECNT</c> section with the given payload.</summary>
    private static byte[] Section(Action<RoomLinkSections.Writer> payload)
    {
        RoomLinkSections.Writer w = new();
        payload(w);
        return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
    }

    private static async Task<byte[]> SaveAsync(IReadOnlyList<RoomPackItem> items)
    {
        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        return stream.ToArray();
    }
}
