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
/// the relocation cannot carry (area portals, props, packed files,
/// displacement collision), levels past a field of the format, grids that
/// are not the library's, vis that does not number its own compile, joints
/// that join nothing, and placements that are not quarter-turn counts.
/// </summary>
public sealed class LevelLinkerRefusalTests
{
    // ---- L5: area portals --------------------------------------------------

    /// <summary>A room with a real area portal (three areas, two portals) is refused.</summary>
    [Fact]
    public async Task ARoomWithAnAreaPortalIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject portal = RoomHarness.WithLumps(hub, bsp =>
        {
            bsp.SetLump(BspLump.Areas, new byte[3 * 8]);
            bsp.SetLump(BspLump.AreaPortals, new byte[2 * 12]);
        });

        LinkException refused = await LinkPairAsync(portal);
        Assert.Contains("3 areas and 2 area portals", refused.Message, StringComparison.Ordinal);
    }

    // ---- L6: game lumps, pak, displacement collision -----------------------

    /// <summary>
    /// A room with a static prop (a non-zero byte in its game lump) is refused
    /// by lump id, where it used to be dropped for every room but the first.
    /// </summary>
    [Fact]
    public async Task ARoomWithPropsIsRefused()
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
        Assert.Contains("game lump 'sprp'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A room whose pak file holds a file is refused, naming the count.</summary>
    [Fact]
    public async Task ARoomWithAPackedFileIsRefused()
    {
        RoomObject hub = await HubAsync();
        ZipArchiveWriter zip = new();
        zip.Add("materials/x.vmt", [1, 2, 3]);
        RoomObject packed = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.PakFile, zip.ToBytes()));

        LinkException refused = await LinkPairAsync(packed);
        Assert.Contains("pak file holds 1 files", refused.Message, StringComparison.Ordinal);
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

    /// <summary>A lump outside the relocation set is refused by name.</summary>
    [Fact]
    public async Task ALumpOutsideTheRelocationSetIsRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject clip = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.ClipPortalVerts, new byte[12]));

        LinkException refused = await LinkPairAsync(clip);
        Assert.Contains("ClipPortalVerts", refused.Message, StringComparison.Ordinal);
    }

    // ---- L10: fields the level would outgrow ------------------------------

    /// <summary>
    /// Two rooms whose planes together pass the <c>ushort</c> a face's plane
    /// number is stored in are refused, naming the room that crossed it.
    /// </summary>
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
                padded[i] = i < planes.Length ? planes[i] : planes[i & 1];
            }

            bsp.SetLump(BspLump.Planes, BspStructView.ToLump<DPlane>(padded, 0).Data);
        });

        LinkException refused = await LinkPairAsync(big);
        Assert.Contains("planes", refused.Message, StringComparison.Ordinal);
        Assert.Contains("at most 65536", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Texinfos past the <c>short</c> a face's texinfo is stored in are refused.</summary>
    [Fact]
    public async Task TexinfosPastAFacesTexinfoAreRefused()
    {
        RoomObject hub = await HubAsync();
        RoomObject big = RoomHarness.WithLumps(hub, bsp =>
        {
            TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
            TexInfo[] padded = new TexInfo[17_000];
            for (int i = 0; i < padded.Length; i++)
            {
                padded[i] = infos[i % infos.Length];
            }

            bsp.SetLump(BspLump.TexInfo, BspStructView.ToLump<TexInfo>(padded, 0).Data);
        });

        LinkException refused = await LinkPairAsync(big);
        Assert.Contains("texinfos", refused.Message, StringComparison.Ordinal);
    }

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
