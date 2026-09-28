//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link's entity budget (<see cref="LevelEntityBudget"/>): the totals,
/// the reserve and where it comes from, the headroom line, the warning at
/// the reserve's boundary and the refusal over the cap with their exact
/// messages, and the check sitting in <c>CheckCapacity</c>, before any room
/// is planned; then the linked lump against the prediction at every turn.
/// </summary>
public sealed class LevelEntityBudgetTests(RoomLinkDataFixture fixture) : IClassFixture<RoomLinkDataFixture>
{
    /// <summary>The headroom line reads exactly as the design gives it.</summary>
    [Fact]
    public void TheHeadroomLineReadsAsDesigned()
    {
        Assert.Equal(
            "map entities 612 / budget 1536 (reserve 512, cap 2048); 931 entities in the entity list",
            LevelEntityBudget.HeadroomLine(612, 1536, 512, 2048, 931));
    }

    /// <summary>
    /// A level is its one worldspawn plus every placement's entities: each
    /// room's tally multiplied by its placements, compile-only entities left
    /// out of both totals, server-only ones out of the edicts only.
    /// </summary>
    [Fact]
    public void TheTotalsAreTheWorldspawnAndEveryPlacement()
    {
        EntityClassTable mod = EntityClassTable.Default.With(
            [new EntityClassRow("logic_relay", EntityCost.ServerOnly, EntityClassCertainty.OwnerSupplied, "mod")]);
        RoomEntityCounts a = Counts(("info_player_start", 2), ("func_detail", 3), ("logic_relay", 1));
        RoomEntityCounts b = Counts(("light", 5));

        LevelEntityReport report = LevelEntityBudget.Check([("a", a), ("b", b), ("a", a)], 512, mod);

        Assert.Equal(1 + 2 + 5 + 2, report.Edicts);
        Assert.Equal(1 + 3 + 5 + 3, report.Listed);
        Assert.Equal(512, report.Reserve);
        Assert.Equal(1536, report.Budget);
        Assert.Equal(2048, report.Cap);
        Assert.Empty(report.Warnings);
        Assert.Equal(
            [new RoomEntityShare("b", 1, new EntityTally(5, 0, 0)), new RoomEntityShare("a", 2, new EntityTally(2, 1, 3))],
            report.Rooms);
        Assert.Equal("map entities 10 / budget 1536 (reserve 512, cap 2048); 12 entities in the entity list", report.Headroom);
    }

    /// <summary>A level of no rooms is its worldspawn alone.</summary>
    [Fact]
    public void ALevelOfNoRoomsIsItsWorldspawn()
    {
        LevelEntityReport report = LevelEntityBudget.Check([], 0, EntityClassTable.Default);
        Assert.Equal(1, report.Edicts);
        Assert.Equal(1, report.Listed);
        Assert.Empty(report.Rooms);
    }

    /// <summary>
    /// At the budget exactly the level is quiet; one edict past it the link
    /// warns with the design's message, naming the rooms by what all their
    /// placements bring.
    /// </summary>
    [Fact]
    public void TheReserveBoundaryWarns()
    {
        RoomEntityCounts big = Counts(("prop_dynamic", 500));
        RoomEntityCounts small = Counts(("light", 35));
        (string, RoomEntityCounts)[] atBudget = [("big", big), ("big", big), ("big", big), ("small", small)];
        Assert.Empty(LevelEntityBudget.Check(atBudget, 512, EntityClassTable.Default).Warnings);

        RoomEntityCounts smallPlus = Counts(("light", 36));
        LevelEntityReport over = LevelEntityBudget.Check([("big", big), ("big", big), ("big", big), ("small", smallPlus)], 512, EntityClassTable.Default);
        Assert.Equal(1537, over.Edicts);
        Assert.Equal(
            ["map entities 1537 / budget 1536 (reserve 512, cap 2048): the level uses 1 of the reserve;"
            + " most expensive rooms: big x3 = 1500, small x1 = 36"],
            over.Warnings);
    }

    /// <summary>
    /// At the cap exactly the level still links, with the warning; one edict
    /// over, it is refused with the design's message.
    /// </summary>
    [Fact]
    public void OverTheCapIsRefused()
    {
        RoomEntityCounts room = Counts(("prop_dynamic", 2047));
        LevelEntityReport atCap = LevelEntityBudget.Check([("room", room)], 512, EntityClassTable.Default);
        Assert.Equal(2048, atCap.Edicts);
        Assert.Single(atCap.Warnings);

        LinkException refused = Assert.Throws<LinkException>(
            () => LevelEntityBudget.Check([("room", room), ("one", Counts(("light", 1)))], 512, EntityClassTable.Default));
        Assert.Equal("map entities 2049 exceed the cap of 2048 edicts; most expensive rooms: room x1 = 2047, one x1 = 1", refused.Message);
    }

    /// <summary>
    /// The rooms named are at most five, most expensive first, ties by name,
    /// and a room that costs nothing is not named; a reserve of the whole
    /// cap is passed by the worldspawn alone, and then no room is named.
    /// </summary>
    [Fact]
    public void TheCostliestRoomsAreNamedInOrder()
    {
        (string, RoomEntityCounts)[] placements =
        [
            ("f", Counts(("light", 1))), ("e", Counts(("light", 2))), ("d", Counts(("light", 3))),
            ("c", Counts(("light", 4))), ("b", Counts(("light", 4))), ("a", Counts(("light", 1))),
            ("zero", Counts(("func_detail", 9))),
        ];
        LevelEntityReport report = LevelEntityBudget.Check(placements, 2040, EntityClassTable.Default);
        Assert.Equal(
            ["map entities 16 / budget 8 (reserve 2040, cap 2048): the level uses 8 of the reserve;"
            + " most expensive rooms: b x1 = 4, c x1 = 4, d x1 = 3, e x1 = 2, a x1 = 1"],
            report.Warnings);

        LevelEntityReport alone = LevelEntityBudget.Check([("zero", Counts(("func_detail", 1)))], 2048, EntityClassTable.Default);
        Assert.Equal(
            ["map entities 1 / budget 0 (reserve 2048, cap 2048): the level uses 1 of the reserve; most expensive rooms: (none)"],
            alone.Warnings);
    }

    /// <summary>
    /// The entity list is refused past what a map may hold, and warned of
    /// past the server's believed entity handles; server-only entities take
    /// no edict, so only a table that has them can get there.
    /// </summary>
    [Fact]
    public void TheEntityListIsHeldToItsLimits()
    {
        EntityClassTable mod = EntityClassTable.Default.With(
            [new EntityClassRow("logic_relay", EntityCost.ServerOnly, EntityClassCertainty.OwnerSupplied, "mod")]);

        LevelEntityReport handles = LevelEntityBudget.Check([("logic", Counts(("logic_relay", 4096)))], 512, mod);
        Assert.Equal(
            ["map entities: the entity list of 4097 is past the 4096 entity handles a Source 2013 server is believed to have; check the game's limit."],
            handles.Warnings);
        Assert.Empty(LevelEntityBudget.Check([("logic", Counts(("logic_relay", 4095)))], 512, mod).Warnings);

        LinkException refused = Assert.Throws<LinkException>(
            () => LevelEntityBudget.Check([("logic", Counts(("logic_relay", 8192)))], 512, mod));
        Assert.Equal(
            "map entities: the entity list of 8193 exceeds the 8192 entities a map may hold; most expensive rooms: logic x1 = 8192",
            refused.Message);
        Assert.Equal(8192, MapFile.MaxMapEntities);
    }

    /// <summary>The link's reserve wins over the library's, the library's over the default; each must be 0 to the cap.</summary>
    [Fact]
    public void TheReserveComesFromTheLinkThenTheLibraryThenTheDefault()
    {
        Assert.Equal(512, LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, RoomLibraryOptions.None));
        Assert.Equal(700, LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, new RoomLibraryOptions(700)));
        Assert.Equal(64, LevelEntityBudget.ReserveFor(new LevelLinkOptions { EntityReserve = 64 }, new RoomLibraryOptions(700)));
        Assert.Equal(0, LevelEntityBudget.ReserveFor(new LevelLinkOptions { EntityReserve = 0 }, RoomLibraryOptions.None));

        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEntityBudget.ReserveFor(new LevelLinkOptions { EntityReserve = -1 }, RoomLibraryOptions.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEntityBudget.ReserveFor(new LevelLinkOptions { EntityReserve = 2049 }, RoomLibraryOptions.None));
        Assert.Throws<ArgumentNullException>(() => LevelEntityBudget.ReserveFor(null!, RoomLibraryOptions.None));
        Assert.Throws<ArgumentNullException>(() => LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, null!));
        Assert.Throws<ArgumentNullException>(() => LevelEntityBudget.Check(null!, 0, EntityClassTable.Default));
        Assert.Throws<ArgumentNullException>(() => LevelEntityBudget.Check([], 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEntityBudget.Check([], -1, EntityClassTable.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEntityBudget.Check([], 2049, EntityClassTable.Default));
    }

    /// <summary>
    /// <c>CheckCapacity</c> budgets from the rooms: stored counts when they
    /// describe the room (a stored count that differs from the lump proves
    /// the lump is not read), the lump otherwise, with the library's reserve
    /// unless the link gives one.
    /// </summary>
    [Fact]
    public void CheckCapacityBudgetsFromStoredCountsAndFallsBackToTheLump()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomObject end = fixture.Library.Get("end");
        RoomLibrary library = RoomHarness.Library(hub, end);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("end", 1, 0, 2));

        LevelEntityReport computed = LevelLinker.CheckCapacity(layout, library);
        Assert.Equal(1 + Listed(hub) + Listed(end), computed.Listed);
        Assert.Equal(512, computed.Reserve);

        RoomEntityCounts lie = RoomEntityCounts.Of(RoomEntityCountsTests.Bsp(["npc_lie"], ["npc_lie"], ["npc_lie"])).For(hub.Bsp);
        RoomLibrary stored = RoomHarness.Library(hub with { EntityCounts = lie }, end);
        stored.Options = new RoomLibraryOptions(1000);
        LevelEntityReport read = LevelLinker.CheckCapacity(layout, stored);
        Assert.Equal(1 + 3 + Listed(end), read.Edicts);
        Assert.Equal(1000, read.Reserve);
        Assert.Equal(10, LevelLinker.CheckCapacity(layout, stored, new LevelLinkOptions { EntityReserve = 10 }).Reserve);
    }

    /// <summary>
    /// A level over the cap is refused by the link before any room is
    /// planned: the crowded room also carries a lump the planning refuses,
    /// and the budget's refusal comes first.
    /// </summary>
    [Fact]
    public async Task TheLinkRefusesOverTheCapBeforePlanning()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomObject crowded = RoomHarness.WithLumps(hub, bsp =>
        {
            bsp[BspLump.Entities] = Lump(2100);
            bsp.SetLump(BspLump.DispInfo, new byte[176]);
        });
        RoomLibrary library = RoomHarness.Library(crowded, fixture.Library.Get("end"));
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("end", 1, 0, 2));

        VbspContext context = await RoomHarness.ContextAsync();
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LevelLinker.LinkAsync(layout, library, context));
        Assert.Equal(
            $"map entities {1 + 2100 + Listed(fixture.Library.Get("end"))} exceed the cap of 2048 edicts;"
            + $" most expensive rooms: hub x1 = 2100, end x1 = {Listed(fixture.Library.Get("end"))}",
            refused.Message);
    }

    /// <summary>
    /// The link returns the budget's report, with the warning when the level
    /// eats into the reserve; the link's reserve and class table are the
    /// ones it is given.
    /// </summary>
    [Fact]
    public async Task TheLinkReturnsTheReport()
    {
        RoomLibrary library = fixture.Library;
        LinkedLevel linked = await LevelLinker.LinkAsync(fixture.Layout, library, await RoomHarness.ContextAsync());
        LevelEntityReport report = linked.EntityBudget!;
        Assert.Equal(512, report.Reserve);
        Assert.Empty(report.Warnings);
        Assert.Equal(EntityLump.Parse(linked.Bsp[BspLump.Entities]).Count, report.Listed);

        LinkedLevel tight = await LevelLinker.LinkAsync(
            fixture.Layout, library, await RoomHarness.ContextAsync(), new LevelLinkOptions { EntityReserve = 2048 - 2 });
        Assert.Equal(2046, tight.EntityBudget!.Reserve);
        Assert.StartsWith($"map entities {report.Edicts} / budget 2 (reserve 2046, cap 2048): the level uses {report.Edicts - 2} of the reserve;", tight.EntityBudget.Warnings.Single(), StringComparison.Ordinal);
        Assert.Equal(linked.Bsp[BspLump.Entities].Data.ToArray(), tight.Bsp[BspLump.Entities].Data.ToArray());

        VbspContext context = await RoomHarness.ContextAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(() => LevelLinker.LinkAsync(fixture.Layout, library, context, null!));
    }

    /// <summary>
    /// At every quarter turn, the linked entity lump holds exactly the
    /// entities the budget predicts from the rooms' counts, stored or
    /// computed; a compile-only entity a room still carries is stripped from
    /// the lump and counted nowhere, and every other entity is kept.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheLinkedLumpHoldsWhatTheBudgetPredicts(int rotation)
    {
        RoomObject hub = fixture.Library.Get("hub");
        List<BspEntity> entities = EntityLump.Parse(hub.Bsp[BspLump.Entities]);
        entities.Add(Entity("func_detail", "16 16 16"));
        entities.Add(Entity("prop_static", "32 32 32"));
        entities.Add(Entity("info_target", "48 48 48"));
        RoomObject carrying = RoomHarness.WithLumps(hub, bsp => bsp[BspLump.Entities] = EntityLump.Write(entities));
        RoomObject counted = carrying with { EntityCounts = RoomEntityCounts.Of(carrying.Bsp) };

        foreach (RoomObject room in new[] { carrying, counted })
        {
            RoomLibrary library = RoomHarness.Library(room, fixture.Library.Get("end"));
            LevelLayout layout = RoomHarness.AutoLayout("one", library, ("hub", 0, 0, rotation), ("hub", 1, 0, rotation));
            LinkedLevel linked = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());

            List<BspEntity> lump = EntityLump.Parse(linked.Bsp[BspLump.Entities]);
            Assert.Equal(linked.EntityBudget!.Listed, lump.Count);
            Assert.Equal(linked.EntityBudget.Edicts, lump.Count);
            Assert.DoesNotContain(lump, e => e.ClassName is "func_detail" or "prop_static");
            Assert.Equal(2, lump.Count(e => e.ClassName == "info_target"));
            Assert.Equal(2, lump.Count(e => e.ClassName == "info_player_start"));
            Assert.Equal(new EntityTally(2, 0, 2), linked.EntityBudget.Rooms.Single().Each);
        }
    }

    /// <summary>
    /// The link strips and counts by the class table it is given: a table
    /// that makes <c>func_detail</c> an edict keeps it in the lump and
    /// counts it, and the shipped table strips it.
    /// </summary>
    [Fact]
    public async Task TheLinkStripsByTheTableItIsGiven()
    {
        RoomObject hub = fixture.Library.Get("hub");
        List<BspEntity> entities = EntityLump.Parse(hub.Bsp[BspLump.Entities]);
        entities.Add(Entity("func_detail", "16 16 16"));
        RoomObject carrying = RoomHarness.WithLumps(hub, bsp => bsp[BspLump.Entities] = EntityLump.Write(entities));
        RoomLibrary library = RoomHarness.Library(carrying);
        LevelLayout layout = RoomHarness.AutoLayout("one", library, ("hub", 0, 0, 0));
        EntityClassTable keeps = EntityClassTable.Default.With(
            [new EntityClassRow("func_detail", EntityCost.Edict, EntityClassCertainty.OwnerSupplied, "a mod that keeps it")]);

        LinkedLevel kept = await LevelLinker.LinkAsync(
            layout, library, await RoomHarness.ContextAsync(), new LevelLinkOptions { EntityClasses = keeps });
        List<BspEntity> lump = EntityLump.Parse(kept.Bsp[BspLump.Entities]);
        Assert.Contains(lump, e => e.ClassName == "func_detail");
        Assert.Equal(kept.EntityBudget!.Listed, lump.Count);

        LinkedLevel stripped = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());
        Assert.DoesNotContain(EntityLump.Parse(stripped.Bsp[BspLump.Entities]), e => e.ClassName == "func_detail");
        Assert.Equal(kept.EntityBudget.Listed - 1, stripped.EntityBudget!.Listed);
    }

    /// <summary>The same level gives the same report every time, and the report does not depend on the thread count.</summary>
    [Fact]
    public async Task TheReportIsDeterministic()
    {
        LinkedLevel one = await LevelLinker.LinkAsync(fixture.Layout, fixture.Library, await RoomHarness.ContextAsync(degree: 1));
        LinkedLevel four = await LevelLinker.LinkAsync(fixture.Layout, fixture.Library, await RoomHarness.ContextAsync(degree: 4));
        Assert.Equal(one.EntityBudget!.Headroom, four.EntityBudget!.Headroom);
        Assert.Equal(one.EntityBudget.Rooms, four.EntityBudget.Rooms);
    }

    private static int Listed(RoomObject room) => RoomEntityCounts.Of(room.Bsp).Tally(EntityClassTable.Default).Listed;

    private static RoomEntityCounts Counts(params (string Class, int Count)[] classes) =>
        RoomEntityCounts.Of(RoomEntityCountsTests.Bsp([["worldspawn"], .. classes.SelectMany(c => Enumerable.Repeat(new[] { c.Class }, c.Count))]));

    private static BspEntity Entity(string className, string origin)
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", className));
        entity.Pairs.Add(new BspKeyValue("origin", origin));
        return entity;
    }

    /// <summary>A worldspawn and <paramref name="count"/> point entities.</summary>
    private static BspLumpData Lump(int count)
    {
        List<BspEntity> entities = [];
        BspEntity world = new();
        world.Pairs.Add(new BspKeyValue("classname", "worldspawn"));
        entities.Add(world);
        for (int i = 0; i < count; i++)
        {
            entities.Add(Entity("info_target", "8 8 8"));
        }

        return EntityLump.Write(entities);
    }
}
