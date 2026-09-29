//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// What the linker refuses, and that each refusal names its cause: content
/// the relocation cannot carry (area portals without their data, props, a pak that is not a
/// zip, displacement collision), levels past a field of the format, grids that
/// are not the library's, vis that does not number its own compile, joints
/// that join nothing, and placements that are not quarter-turn counts.
/// </summary>
public sealed class LevelLinkerRefusalTests
{
    // ---- L5: area portals --------------------------------------------------

    /// <summary>
    /// A room whose lumps have an area portal (three areas, two listings)
    /// but that carries no area portal data from its compile is refused,
    /// naming the room and what to do; the old refusal of every area portal
    /// ("a linkable room has no area portal") is gone with the area portals
    /// feature (the rooms design, PR 13).
    /// </summary>
    [Fact]
    public async Task ARoomWithAnAreaPortalButNoPortalDataIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject portal = RoomHarness.WithLumps(hub, bsp =>
        {
            bsp.SetLump(BspLump.Areas, new byte[3 * 8]);
            bsp.SetLump(BspLump.AreaPortals, new byte[2 * 12]);
        });

        LinkException refused = await LinkPairAsync(portal);
        Assert.Equal(
            "room hub has 1 area portals but no area portal data from its compile (a pack written before the link carried"
            + " area portals, or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("a linkable room has no area portal", refused.Message, StringComparison.Ordinal);

        // Clip vertices alone are an area portal's too.
        RoomObject clip = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.ClipPortalVerts, new byte[12]));
        Assert.StartsWith("room hub has 0 area portals but no area portal data", (await LinkPairAsync(clip)).Message, StringComparison.Ordinal);
    }

    // ---- L6: game lumps, pak, displacement collision -----------------------

    /// <summary>
    /// A room whose static prop lump has content but that carries no static
    /// prop data from its compile (a pack written before static props
    /// linked, a room a host built itself) is refused, naming the room and
    /// what to do, rather than linked with guessed leaves and its
    /// <c>room_needs</c> lost; the old refusal by lump id ("game lump
    /// 'sprp'") is gone with the static props feature.
    /// </summary>
    [Fact]
    public async Task ARoomWithPropsButNoPropDataIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject props = RoomHarness.WithLumps(hub, bsp =>
        {
            GameLumpEntry sprp = bsp.GameLumps.First(g => g.IdString() == "sprp");
            byte[] data = sprp.Data.ToArray();
            data[0] = 1; // one model name in the dictionary
            bsp.GameLumps[bsp.GameLumps.IndexOf(sprp)] = sprp with { Data = data };
        });

        LinkException refused = await LinkPairAsync(props);
        Assert.Equal(
            "room hub has static props but no static prop data from its compile (a pack written before the link carried"
            + " static props, or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("game lump 'sprp'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room with detail props (a non-zero byte in the detail prop game
    /// lump) is still refused by lump id, with the message it always had,
    /// until the link carries detail props.
    /// </summary>
    [Fact]
    public async Task ARoomWithDetailPropsIsStillRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject props = RoomHarness.WithLumps(hub, bsp =>
        {
            GameLumpEntry dprp = bsp.GameLumps.First(g => g.IdString() == "dprp");
            byte[] data = dprp.Data.ToArray();
            data[0] = 1;
            bsp.GameLumps[bsp.GameLumps.IndexOf(dprp)] = dprp with { Data = data };
        });

        LinkException refused = await LinkPairAsync(props);
        Assert.Equal(
            "room hub has content in game lump 'dprp' (static or detail props); the relocation carries only empty game lumps",
            refused.Message);
    }

    /// <summary>
    /// A room whose pak file holds a file is no longer refused (the refusal
    /// "pak file holds N files; the relocation carries only an empty pak" is
    /// gone with the packed-files feature): the file is in the linked pak.
    /// </summary>
    [Fact]
    public async Task ARoomWithAPackedFileIsNoLongerRefused()
    {
        RoomObject hub = await HubAsync();
        ZipArchiveWriter zip = new();
        zip.Add("materials/x.vmt", [1, 2, 3]);
        RoomObject packed = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.PakFile, zip.ToBytes()));

        RoomLibrary library = RoomHarness.Library(packed);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());
        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(linked.Bsp[BspLump.PakFile].Data);
        Assert.Equal([1, 2, 3], Assert.Single(pak.Entries, e => e.Name == "materials/x.vmt").Data);
    }

    /// <summary>A pak that is not a zip is refused rather than carried.</summary>
    [Fact]
    public async Task APakThatIsNotAZipIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject broken = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.PakFile, new byte[] { 1, 2, 3, 4 }));

        LinkException refused = await LinkPairAsync(broken);
        Assert.Contains("pak file is not a zip", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Displacement collision (a non-zero PhysDisp count) is refused.</summary>
    [Fact]
    public async Task DisplacementCollisionIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject disp = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.PhysDisp, new byte[] { 1, 0, 0xFF, 0xFF }));

        LinkException refused = await LinkPairAsync(disp);
        Assert.Contains("displacement collision", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lump outside the relocation set is refused by name. (The clip
    /// portal vertices this fact used to name joined the set with the area
    /// portals, PR 13; a room with them and no area portal data is refused
    /// by <see cref="ARoomWithAnAreaPortalButNoPortalDataIsRefused"/>.)
    /// </summary>
    [Fact]
    public async Task ALumpOutsideTheRelocationSetIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject lights = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.WorldLights, new byte[88]));

        LinkException refused = await LinkPairAsync(lights);
        Assert.Contains("WorldLights", refused.Message, StringComparison.Ordinal);
    }

    // ---- L10: fields the level would outgrow ------------------------------

    /// <summary>
    /// Two rooms whose planes together pass the <c>ushort</c> a face's plane
    /// number is stored in are refused, naming the room that crossed it.
    /// </summary>
    /// <remarks>
    /// The planes are shared by content, so the padding is distinct planes
    /// that the placement does not bring onto each other: x planes a
    /// 1/1024 apart within the first 17 units, which the second room's
    /// one-cell translation moves clear of the first room's.
    /// </remarks>
    [Fact]
    public async Task PlanesPastAFacesPlaneNumberAreRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject big = RoomHarness.WithLumps(hub, bsp =>
        {
            DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
            DPlane[] padded = new DPlane[34_000];
            for (int i = 0; i < padded.Length; i++)
            {
                float dist = (i >> 1) / 1024f;
                padded[i] = i < planes.Length
                    ? planes[i]
                    : (i & 1) == 0
                        ? new DPlane { Normal = new Vec3(1, 0, 0), Dist = dist, Type = 0 }
                        : new DPlane { Normal = new Vec3(-1, 0, 0), Dist = -dist, Type = 0 };
            }

            bsp.SetLump(BspLump.Planes, BspStructView.ToLump<DPlane>(padded, 0).Data);
        });

        LinkException refused = await LinkPairAsync(big);
        Assert.Matches(
            @"^room hub at cell \(\d, 0\) pushes the link to \d+ planes; the format carries at most 65536\.$",
            refused.Message);
    }

    /// <summary>
    /// Texinfos past <c>MAX_MAP_TEXINFO</c> (which is below the <c>short</c>
    /// a face's texinfo is stored in) are refused as the room that brings
    /// them is shared in, before its faces are written.
    /// </summary>
    [Fact]
    public async Task TexinfosPastAFacesTexinfoAreRefused()
    {
        RoomObject big = WithDistinctTexInfos(await HubAsync(), 17_000);

        LinkException refused = await LinkPairAsync(big);
        Assert.Equal(
            "room hub at cell (0, 0) pushes the link to 17000 texinfos; the engine loads at most 12288 (MAX_MAP_TEXINFO).",
            refused.Message);
    }

    /// <summary>
    /// A room whose texinfo lump is padded to <paramref name="count"/>
    /// entries that are all distinct, and stay distinct from a second copy
    /// of the room one cell along +x.
    /// </summary>
    /// <remarks>
    /// The texinfos are shared by content, so repeating the room's own would
    /// pad nothing. Every entry's s axis is made +x with an offset of
    /// <c>i / 1024</c> (exact, under 17 texels): distinct within the room,
    /// and the neighbour's translation takes a cell off every offset, which
    /// puts its set wholly below this one's.
    /// </remarks>
    private static RoomObject WithDistinctTexInfos(RoomObject room, int count) => RoomHarness.WithLumps(room, bsp =>
    {
        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        TexInfo[] padded = new TexInfo[count];
        for (int i = 0; i < padded.Length; i++)
        {
            padded[i] = infos[i % infos.Length];
            padded[i].TextureVecsTexelsPerWorldUnits[0] = 1;
            padded[i].TextureVecsTexelsPerWorldUnits[1] = 0;
            padded[i].TextureVecsTexelsPerWorldUnits[2] = 0;
            padded[i].TextureVecsTexelsPerWorldUnits[3] = i / 1024f;
        }

        bsp.SetLump(BspLump.TexInfo, BspStructView.ToLump<TexInfo>(padded, 0).Data);
    });

    /// <summary>
    /// The nodraw copies the assembly adds for stripped plug faces count
    /// toward <c>MAX_MAP_TEXINFO</c> too: two rooms whose own texinfos are
    /// exactly the cap between them (distinct, so sharing folds none of
    /// them) pass the check made as each room is shared in, and the first
    /// nodraw copy of a doorway's face is refused, naming the loader's
    /// constant.
    /// </summary>
    [Fact]
    public async Task NodrawCopiesPastTheTexinfoCapAreRefused()
    {
        const int cap = 12288;
        RoomObject full = WithDistinctTexInfos(await HubAsync(), cap / 2);

        RoomLibrary library = RoomHarness.Library(full);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        LevelLinker.CheckCapacity(layout, library); // exactly the cap: loads

        LinkException refused = await LinkPairAsync(full);
        Assert.Matches(
            $@"^room hub at cell \(\d, 0\) pushes the link to {cap + 1} texinfos; the engine loads at most {cap} \(MAX_MAP_TEXINFO\)\.$",
            refused.Message);
    }

    /// <summary>
    /// Nodes past <c>MAX_MAP_NODES</c> are refused before any room is
    /// planned: two rooms of 33,000 nodes each are 66,000 before the top tree
    /// adds any.
    /// </summary>
    [Fact]
    public async Task NodesPastTheLoadersCapAreRefusedUpFront()
    {
        RoomObject big = WithNodes(await HubAsync(), 33_000);
        RoomLibrary library = RoomHarness.Library(big);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));

        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(layout, library));
        Assert.Equal(
            "room hub at cell (1, 0) pushes the link to 66003 nodes; the engine loads at most 65536 (MAX_MAP_NODES).",
            refused.Message);
    }

    /// <summary>
    /// The node check made before planning counts the top tree at its floor
    /// (three nodes for two rooms in a row) and no carve chains, so it can
    /// pass a level the assembly then takes past <c>MAX_MAP_NODES</c>; the
    /// exact total is checked once the carve has run. Two rooms of 32,766
    /// nodes are 65,535 up front, and the doorway's carve chain adds more.
    /// </summary>
    [Fact]
    public async Task NodesTheCarveAddsPastTheLoadersCapAreRefused()
    {
        RoomObject big = WithNodes(await HubAsync(), 32_766);
        RoomLibrary library = RoomHarness.Library(big);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        LevelLinker.CheckCapacity(layout, library);

        LinkException refused = await LinkPairAsync(big);
        Assert.Matches(
            @"^room hub at cell \(1, 0\) pushes the link to 655(3[7-9]|[4-9]\d) nodes; the engine loads at most 65536 \(MAX_MAP_NODES\)\.$",
            refused.Message);
    }

    /// <summary>A room whose node lump is padded to <paramref name="count"/> nodes with copies of its own.</summary>
    private static RoomObject WithNodes(RoomObject room, int count) => RoomHarness.WithLumps(room, bsp =>
    {
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        DNode[] padded = new DNode[count];
        for (int i = 0; i < padded.Length; i++)
        {
            padded[i] = nodes[i % nodes.Length];
        }

        bsp.SetLump(BspLump.Nodes, BspStructView.ToLump<DNode>(padded, 0).Data);
    });

    // ---- L11 (linker side): vis that does not number its compile ----------

    /// <summary>
    /// A room object built in code with a leaf cluster past its vis's count
    /// is refused before its rows are shifted: that cluster would alias into
    /// the next room's range.
    /// </summary>
    [Fact]
    public async Task ALeafClusterPastTheRoomsVisIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject aliased = RoomHarness.WithLumps(hub, bsp =>
        {
            DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            int open = Array.FindIndex(leafs, l => l.Cluster >= 0 && (l.Contents & 1) == 0);
            leafs[open].Cluster = (short)hub.ClusterCount;
            bsp.SetLump(BspLump.Leafs, BspStructView.ToLump<DLeaf>(leafs, bsp[BspLump.Leafs].Version).Data, bsp[BspLump.Leafs].Version);
        });

        LinkException refused = await LinkPairAsync(aliased);
        Assert.Contains($"is in cluster {hub.ClusterCount}, but its vis has {hub.ClusterCount} clusters", refused.Message, StringComparison.Ordinal);
    }

    // ---- L13: grids, joints that join nothing, cancellation ---------------

    /// <summary>A layout on another cell size than the library's is refused by rule 5.</summary>
    [Fact]
    public async Task ALayoutOnAnotherGridIsRefused()
    {
        RoomLibrary library = RoomHarness.Library(await HubAsync());
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0)) with { CellSize = 512 };

        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
        Assert.Equal("rule 5 (PlacementIsQuarterTurnGrid): the layout's cell size 512 is not the library's 256.", refused.Message);
    }

    /// <summary>A layout that believes in another kit is refused by rule 4.</summary>
    [Fact]
    public async Task ALayoutWithAnotherKitIsRefused()
    {
        RoomLibrary library = RoomHarness.Library(await HubAsync());
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0))
            with { Kit = new SocketKit(64, 96, 16) };

        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
        Assert.StartsWith("rule 4 (SocketsFromFixedKit): the layout's kit", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A level of no rooms is refused by the layout's own validation, not a crash.</summary>
    [Fact]
    public async Task ALevelOfNoRoomsIsRefused()
    {
        RoomLibrary library = RoomHarness.Library(await HubAsync());
        LevelLayout empty = new("empty", RoomHarness.Cell, RoomHarness.Kit, []);

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await LevelLinker.LinkAsync(empty, library, await RoomHarness.ContextAsync()));
        Assert.Contains("at least one room", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A jointed socket whose plug no open leaf of the room faces is refused:
    /// its door edge would be empty, and the joint would silently join nothing.
    /// </summary>
    [Fact]
    public async Task AJointNoOpenLeafFacesIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject shrunk = RoomHarness.WithLumps(hub, bsp =>
        {
            // Pull every open leaf's +x face back from the +x plug.
            DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            for (int i = 0; i < leafs.Length; i++)
            {
                if ((leafs[i].Contents & 1) == 0)
                {
                    leafs[i].Maxs[0] = 200;
                }
            }

            bsp.SetLump(BspLump.Leafs, BspStructView.ToLump<DLeaf>(leafs, bsp[BspLump.Leafs].Version).Data, bsp[BspLump.Leafs].Version);
        });

        LinkException refused = await LinkPairAsync(shrunk);
        Assert.Contains("jointed socket \"PositiveX\" faces no open leaf", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The closure observes its token at every pivot.</summary>
    [Fact]
    public void TheClosureObservesCancellation()
    {
        byte[][] rows = [[1], [2]];
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LevelLinker.CloseRows(rows, 2, cancelled.Token));
    }

    /// <summary>The closure of an empty cluster space is empty, not a crash.</summary>
    [Fact]
    public void TheClosureOfNoClustersIsEmpty()
    {
        byte[][] rows = [];
        LevelLinker.CloseRows(rows, 0, CancellationToken.None);
        Assert.Empty(rows);
    }

    /// <summary>A cancelled link stops, leaving nothing half-built behind for the caller.</summary>
    [Fact]
    public async Task ACancelledLinkThrows()
    {
        RoomLibrary library = RoomHarness.Library(await HubAsync());
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(), cancelled.Token));
    }

    // ---- L8 / L14: the placement and grid records -------------------------

    /// <summary>A rotation is a quarter-turn count: 0..3 pass, 4, -1 and 90 are refused.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    public void ARotationIsAQuarterTurnCount(int rotation, bool valid)
    {
        RoomPlacement placement = new("hub", 0, 0, rotation);
        if (valid)
        {
            placement.Validate();
            Assert.Equal(rotation, placement.NormalizedRotation);
        }
        else
        {
            LinkException refused = Assert.Throws<LinkException>(placement.Validate);
            Assert.Contains($"rotation {rotation} is not a quarter-turn count", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// NaN, the infinities, zero and negatives are refused as a cell size, in
    /// a room definition, a layout and a library, and as any kit dimension.
    /// Every comparison with NaN is false, so "not positive" alone let it by.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void AGridThatIsNotAPositiveFiniteNumberIsRefused(float bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomDefinition("r", bad, RoomHarness.Kit, []).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LevelLayout("l", bad, RoomHarness.Kit, [new RoomInstance(new RoomPlacement("r", 0, 0, 0), [], [])]).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomLibrary(RoomHarness.Kit, bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketKit(bad, 96, 16).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketKit(96, bad, 16).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketKit(96, 96, bad).Validate());
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<RoomObject> HubAsync() =>
        (await RoomHarness.LibraryAsync(false, RoomHarness.Hub())).Get("hub");

    private static async Task<LinkException> LinkPairAsync(RoomObject room)
    {
        RoomLibrary library = RoomHarness.Library(room);
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        return await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
    }
}
