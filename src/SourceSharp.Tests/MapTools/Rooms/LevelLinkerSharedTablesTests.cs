//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The linked map's planes, strings, texdatas and texinfos are shared by the
/// rooms rather than appended room by room: each table holds every distinct
/// entry once, every reference that named a room's copy names the shared
/// entry with the same content, plane pairs stay pairs, and the result is the
/// same bytes whatever the thread count.
/// </summary>
/// <remarks>
/// The unit facts drive <see cref="LevelLinker.LinkPlanes"/> and
/// <see cref="LevelLinker.LinkTextures"/> directly. The level facts link the
/// walkable hub with a ramp (a non-axial plane) and an occluder, at every
/// quarter turn, and check each reference against the room's own lumps put
/// through the placement, which is what the appending link wrote; one fact
/// pins that resolved view to the digest the appending link's output had.
/// </remarks>
public sealed class LevelLinkerSharedTablesTests
{
    // ---- the plane table ---------------------------------------------------

    /// <summary>
    /// A plane is stored once, as a pair with its flip; asked for again it is
    /// the same pair, and asked for by its flip it is the same pair, flipped.
    /// The null pair keeps indices 0 and 1.
    /// </summary>
    [Fact]
    public void APlaneIsStoredOnceAsAPairAndFoundByEitherHalf()
    {
        LevelLinker.LinkPlanes planes = new();
        DPlane ramp = Plane(new Vec3(0, 0.6f, 0.8f), 12);

        Assert.Equal((2, false), planes.Intern(ramp));
        Assert.Equal((2, false), planes.Intern(ramp));
        Assert.Equal((2, true), planes.Intern(Plane(new Vec3(0, -0.6f, -0.8f), -12)));
        Assert.Equal(4, planes.Count);
        Assert.Equal(default, planes.Planes[0]);
        Assert.Equal(default, planes.Planes[1]);
        Assert.Equal(ramp, planes.Planes[2]);
        Assert.Equal(new Vec3(0, -0.6f, -0.8f), planes.Planes[3].Normal);
        Assert.Equal(-12, planes.Planes[3].Dist);
        Assert.Equal(ramp.Type, planes.Planes[3].Type);
    }

    /// <summary>
    /// Planes that differ in distance or normal are different pairs, numbered
    /// in the order they are first asked for; a plane given by normal and
    /// distance is typed from its normal.
    /// </summary>
    [Fact]
    public void DifferentPlanesAreDifferentPairsInFirstAskedOrder()
    {
        LevelLinker.LinkPlanes planes = new();
        Assert.Equal((2, false), planes.Intern(new Vec3(1, 0, 0), 256));
        Assert.Equal((4, false), planes.Intern(new Vec3(1, 0, 0), 512));
        Assert.Equal((6, false), planes.Intern(new Vec3(0, 1, 0), 256));
        Assert.Equal((2, false), planes.Intern(new Vec3(1, 0, 0), 256));
        Assert.Equal((int)PlaneType.Y, planes.Planes[6].Type);
        Assert.Equal(8, planes.Count);
    }

    /// <summary>
    /// A normal whose zero components are negative zero (what a quarter turn
    /// writes) is the same plane as the positive-zero one: equal as numbers.
    /// The stored plane is the first one's, bits and all.
    /// </summary>
    [Fact]
    public void NegativeZeroIsTheSamePlane()
    {
        LevelLinker.LinkPlanes planes = new();
        DPlane turned = new() { Normal = new Vec3(-0f, 1, -0f), Dist = 64, Type = (int)PlaneType.Y };
        Assert.Equal((2, false), planes.Intern(turned));
        Assert.Equal((2, false), planes.Intern(new Vec3(0, 1, 0), 64));
        Assert.True(float.IsNegative(planes.Planes[2].Normal.X), "the stored plane is not the first one's");
        Assert.Equal(LevelLinker.PlaneKey.Of(new Vec3(0, 1, 0), 64, 1), LevelLinker.PlaneKey.Of(new Vec3(-0f, 1, -0f), 64, 1));
    }

    /// <summary>A node's children swap exactly when its shared pair is flipped.</summary>
    [Fact]
    public void ANodesChildrenSwapOnlyOnAFlippedPair()
    {
        IntArray2 children = default;
        children[0] = 7;
        children[1] = -3;
        Assert.Equal((7, -3), Pair(LevelLinker.Orient(children, flipped: false)));
        Assert.Equal((-3, 7), Pair(LevelLinker.Orient(children, flipped: true)));

        static (int, int) Pair(IntArray2 c) => (c[0], c[1]);
    }

    // ---- the material tables -----------------------------------------------

    /// <summary>
    /// Two rooms' strings and texdatas share by content: a name and a
    /// material the first room brought are named, not added, whatever index
    /// the second room gave them, and the string data holds each name once,
    /// NUL-terminated.
    /// </summary>
    [Fact]
    public void StringsAndTexdatasAreSharedByContent()
    {
        LevelLinker.LinkTextures textures = new();
        BspData a = Materials(["brick", "floor"], (0, 64), (1, 128));
        BspData b = Materials(["tile", "floor", "brick"], (2, 64), (1, 128), (0, 32));

        int[] aStrings = textures.InternStrings(a, "a");
        Assert.Equal([0, 1], aStrings);
        Assert.Equal([0, 1], textures.InternTexDatas(a, aStrings));

        int[] bStrings = textures.InternStrings(b, "b");
        Assert.Equal([2, 1, 0], bStrings);

        // brick at 64 is a's first; floor at 128 its second; tile is new.
        Assert.Equal([0, 1, 2], textures.InternTexDatas(b, bStrings));
        Assert.Equal(3, textures.TexDatas.Count);
        Assert.Equal("brick\0floor\0tile\0", Encoding.ASCII.GetString([.. textures.StringData]));
        Assert.Equal([0, 6, 12], textures.StringTable);
        Assert.Equal(1, textures.TexDatas[1].NameStringTableId);
    }

    /// <summary>
    /// A texdata that differs from another in its reflectivity or any size is
    /// its own entry, even with the same name.
    /// </summary>
    [Theory]
    [InlineData("reflectivity")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("view width")]
    [InlineData("view height")]
    public void ATexdataThatDiffersInAnyFieldIsItsOwn(string field)
    {
        LevelLinker.LinkTextures textures = new();
        BspData room = Materials(["brick"], (0, 64), (0, 64));
        DTexData[] datas = BspStructView.As<DTexData>(room[BspLump.TexData]).ToArray();
        switch (field)
        {
            case "reflectivity": datas[1].Reflectivity = new Vec3(0.5f, 0.5f, 0.25f); break;
            case "width": datas[1].Width = 32; break;
            case "height": datas[1].Height = 32; break;
            case "view width": datas[1].ViewWidth = 32; break;
            default: datas[1].ViewHeight = 32; break;
        }

        room.SetLump(BspLump.TexData, BspStructView.ToLump<DTexData>(datas, 0).Data);
        Assert.Equal([0, 1], textures.InternTexDatas(room, textures.InternStrings(room, "r")));

        // Unchanged, the two are one.
        BspData same = Materials(["brick"], (0, 64), (0, 64));
        Assert.Equal([0, 0], new LevelLinker.LinkTextures().InternTexDatas(same, [0]));
    }

    /// <summary>
    /// Texinfos share by content once their texdata is linked: two rooms'
    /// texinfos naming the same material by different room indices are one,
    /// negative zero is zero, and any other difference keeps them apart.
    /// </summary>
    [Fact]
    public void TexinfosShareByContentOnceTheirTexdataIsLinked()
    {
        LevelLinker.LinkTextures textures = new();
        TexInfo floor = TexInfoOf(texData: 0, (1, 0, 0, 16), (0, -1, 0, 0));
        TexInfo wall = TexInfoOf(texData: 1, (1, 0, 0, 16), (0, 0, -1, 0));
        Assert.Equal([0, 1], textures.InternTexInfos([floor, wall], [5, 6]));

        // The second room numbers the materials the other way round, and its
        // floor's zero components are negative zero.
        TexInfo turned = TexInfoOf(texData: 1, (1, -0f, -0f, 16), (-0f, -1, 0, 0));
        TexInfo shifted = TexInfoOf(texData: 1, (1, 0, 0, 48), (0, -1, 0, 0));
        TexInfo flagged = turned;
        flagged.Flags = 4;
        Assert.Equal([0, 2, 3], textures.InternTexInfos([turned, shifted, flagged], [6, 5]));
        Assert.Equal(5, textures.TexInfos[0].TexData);
        Assert.Equal(4, textures.TexInfos.Count);
        Assert.Equal(2, textures.InternTexInfo(textures.TexInfos[2]));
    }

    /// <summary>
    /// A string table entry past the room's string data is refused, naming
    /// the room and the entry; the last string may end at the data's end
    /// without a terminator, and is stored with one.
    /// </summary>
    [Fact]
    public void AStringTableEntryOutsideItsDataIsRefused()
    {
        BspData room = new();
        room.SetLump(BspLump.TexDataStringData, Encoding.ASCII.GetBytes("a\0tail"));
        room.SetLump(BspLump.TexDataStringTable, BspStructView.ToLump<int>([0, 2], 0).Data);
        LevelLinker.LinkTextures textures = new();
        Assert.Equal([0, 1], textures.InternStrings(room, "r"));
        Assert.Equal("a\0tail\0", Encoding.ASCII.GetString([.. textures.StringData]));

        room.SetLump(BspLump.TexDataStringTable, BspStructView.ToLump<int>([0, 7], 0).Data);
        LinkException refused = Assert.Throws<LinkException>(() => new LevelLinker.LinkTextures().InternStrings(room, "attic"));
        Assert.Equal(
            "room attic's texdata string table entry 1 points at byte 7, outside its 6 bytes of string data",
            refused.Message);

        room.SetLump(BspLump.TexDataStringTable, BspStructView.ToLump<int>([-1], 0).Data);
        Assert.Throws<LinkException>(() => new LevelLinker.LinkTextures().InternStrings(room, "attic"));
    }

    /// <summary>
    /// A room index maps through the room's map; one outside the room's
    /// table (negative, or past its end) names nothing and is -1.
    /// </summary>
    [Fact]
    public void AnIndexOutsideTheRoomsTableNamesNothing()
    {
        int[] map = [4, 9];
        Assert.Equal(9, LevelLinker.Remap(map, 1));
        Assert.Equal(-1, LevelLinker.Remap(map, 2));
        Assert.Equal(-1, LevelLinker.Remap(map, -1));
    }

    /// <summary>
    /// The shared planes may reach the <c>ushort</c> a face's plane number is
    /// and the texinfos <c>MAX_MAP_TEXINFO</c>, and are refused one past,
    /// naming the room whose entries crossed and the limit.
    /// </summary>
    [Fact]
    public void TheSharedTablesAreHeldToTheirLimits()
    {
        LevelLinker.CheckSharedTables("r", 2, 3, 65536, 12288);
        LinkException planes = Assert.Throws<LinkException>(() => LevelLinker.CheckSharedTables("r", 2, 3, 65537, 0));
        Assert.Equal("room r at cell (2, 3) pushes the link to 65537 planes; the format carries at most 65536.", planes.Message);
        LinkException texInfos = Assert.Throws<LinkException>(() => LevelLinker.CheckSharedTables("r", 2, 3, 2, 12289));
        Assert.Equal(
            "room r at cell (2, 3) pushes the link to 12289 texinfos; the engine loads at most 12288 (MAX_MAP_TEXINFO).",
            texInfos.Message);
    }

    // ---- the up-front texdata count ------------------------------------------

    /// <summary>
    /// The texdata check made before any room is planned counts the level's
    /// distinct materials: three placements of a room of 1,500 materials are
    /// 4,500 room texdatas and pass, because the second and third bring
    /// nothing new. Distinct materials past <c>MAX_MAP_TEXDATA</c> are still
    /// refused before planning, naming the room that brings the 2049th.
    /// </summary>
    [Fact]
    public async Task DistinctTexdatasPastTheLoadersCapAreRefusedUpFront()
    {
        RoomObject hub = (await RoomHarness.LibraryAsync(false, RoomHarness.Hub())).Get("hub");
        RoomObject a = WithTexDatas(hub, "a", 1500, 0);
        RoomObject b = WithTexDatas(hub, "b", 1500, 1000);
        RoomLibrary library = RoomHarness.Library(a, b);

        LevelLinker.CheckCapacity(Line(library, "a", 3), library);

        LevelLayout layout = RoomHarness.AutoLayout("ab", library, ("a", 0, 0, 0), ("a", 1, 0, 0), ("b", 2, 0, 0));
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(layout, library));
        Assert.Equal(
            "room b at cell (2, 0) pushes the link to 2500 texdatas; the engine loads at most 2048 (MAX_MAP_TEXDATA).",
            refused.Message);
    }

    /// <summary>
    /// A level past the old texdata limit now links: three placements of a
    /// room with 1,500 materials were 4,500 texdatas appended, refused at
    /// <c>MAX_MAP_TEXDATA</c>; shared, the linked map holds the 1,500 once,
    /// and the room's string table once, and passes the loader validation.
    /// </summary>
    [Fact]
    public async Task ALevelPastTheAppendingTexdataLimitLinks()
    {
        RoomObject hub = (await RoomHarness.LibraryAsync(false, RoomHarness.Hub())).Get("hub");
        RoomLibrary library = RoomHarness.Library(WithTexDatas(hub, "a", 1500, 0));
        LinkedLevel link = await LevelLinker.LinkAsync(Line(library, "a", 3), library, await RoomHarness.ContextAsync());

        Assert.Equal(1500, BspStructView.Count<DTexData>(link.Bsp[BspLump.TexData]));
        Assert.Equal(
            BspStructView.Count<int>(hub.Bsp[BspLump.TexDataStringTable]),
            BspStructView.Count<int>(link.Bsp[BspLump.TexDataStringTable]));
        ValidationReport report = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.Equal(0, report.ErrorCount);
    }

    // ---- a linked level --------------------------------------------------------

    /// <summary>
    /// Every reference of every room resolves in the linked map to what the
    /// room's own lumps, put through its placement, resolve to: each drawn
    /// and original face's and brush side's plane and texinfo, each node's
    /// oriented plane and child subtrees, each occluder polygon's plane, and
    /// every texinfo's material name. This is what the appending link wrote
    /// entry by entry; sharing may only change which index holds it.
    /// </summary>
    [Fact]
    public async Task EveryReferenceResolvesToItsRoomsMovedEntry()
    {
        (LinkedLevel link, RoomLibrary library) = await TurnedLevelAsync();
        AssertEveryReferenceIsTheRooms(link, library);
    }

    /// <summary>
    /// A face's macro texture names its room's string, which the shared
    /// table holds once: each placement's entries resolve to the room's
    /// names, "none" (0xFFFF) stays none, and an id outside the room's table
    /// names nothing and becomes none.
    /// </summary>
    [Fact]
    public async Task MacroTextureNamesResolveThroughTheSharedStrings()
    {
        RoomObject hub = (await RoomHarness.LibraryAsync(false, RoomHarness.Hub())).Get("hub");
        int strings = BspStructView.Count<int>(hub.Bsp[BspLump.TexDataStringTable]);
        ushort[] ids = [0xFFFF, 0, (ushort)(strings - 1), (ushort)(strings + 5)];
        RoomObject macro = RoomHarness.WithLumps(hub, bsp =>
        {
            int faces = BspStructView.Count<DFace>(bsp[BspLump.Faces]);
            FaceMacroTextureInfo[] infos = new FaceMacroTextureInfo[faces];
            for (int f = 0; f < faces; f++)
            {
                infos[f].MacroTextureNameId = ids[f % ids.Length];
            }

            bsp.SetLump(BspLump.FaceMacroTextureInfo, BspStructView.ToLump<FaceMacroTextureInfo>(infos, 0).Data);
        });
        RoomLibrary library = RoomHarness.Library(macro);
        LinkedLevel link = await LevelLinker.LinkAsync(Line(library, "hub", 2), library, await RoomHarness.ContextAsync());

        FaceMacroTextureInfo[] linked = BspStructView.As<FaceMacroTextureInfo>(link.Bsp[BspLump.FaceMacroTextureInfo]).ToArray();
        FaceMacroTextureInfo[] room = BspStructView.As<FaceMacroTextureInfo>(macro.Bsp[BspLump.FaceMacroTextureInfo]).ToArray();
        Assert.Equal(2 * room.Length, linked.Length);
        for (int m = 0; m < linked.Length; m++)
        {
            ushort id = room[m % room.Length].MacroTextureNameId;
            if (id == 0xFFFF || id >= strings)
            {
                Assert.Equal(0xFFFF, linked[m].MacroTextureNameId);
            }
            else
            {
                Assert.Equal(TableString(macro.Bsp, id), TableString(link.Bsp, linked[m].MacroTextureNameId));
            }
        }
    }

    /// <summary>
    /// The shared tables hold every entry once: no two plane pairs are the
    /// same plane or each other's flip, and no two strings, texdatas or
    /// texinfos are equal; and the level shares a great deal (the turned
    /// hubs repeat each other's planes and all their materials).
    /// </summary>
    [Fact]
    public async Task EachSharedTableHoldsEveryEntryOnce()
    {
        (LinkedLevel link, RoomLibrary library) = await TurnedLevelAsync();
        BspData bsp = link.Bsp;
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        HashSet<LevelLinker.PlaneKey> seen = [];
        for (int p = 2; p < planes.Length; p += 2)
        {
            Assert.True(seen.Add(Key(planes[p])), $"pair {p} repeats an earlier pair");
            Assert.DoesNotContain(Key(planes[p + 1]), seen);
        }

        string[] names = [.. Enumerable.Range(0, BspStructView.Count<int>(bsp[BspLump.TexDataStringTable])).Select(i => TableString(bsp, i))];
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        DTexData[] datas = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
        Assert.Equal(datas.Length, datas.Distinct().Count());
        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        Assert.Equal(infos.Length, infos.Select(i => Convert.ToHexString(MemoryMarshal.AsBytes(new ReadOnlySpan<TexInfo>(in i)))).Distinct().Count());

        BspData room = library.Get("hub").Bsp;
        int rooms = link.Plan.Layout.Rooms.Count;
        Assert.Equal(BspStructView.Count<DTexData>(room[BspLump.TexData]), datas.Length);
        Assert.True(
            planes.Length < rooms * BspStructView.Count<DPlane>(room[BspLump.Planes]) / 2,
            $"{planes.Length} planes for {rooms} rooms of {BspStructView.Count<DPlane>(room[BspLump.Planes])}");
    }

    /// <summary>
    /// Plane pairs stay pairs: every odd plane is the exact negation of the
    /// even one before it, both carry the type of the normal, an axial
    /// pair's even half is the positive axis, and every node splits on an
    /// even half.
    /// </summary>
    [Fact]
    public async Task PlanePairsStayPairs()
    {
        (LinkedLevel link, _) = await TurnedLevelAsync();
        DPlane[] planes = BspStructView.As<DPlane>(link.Bsp[BspLump.Planes]).ToArray();
        Assert.Equal(0, planes.Length % 2);
        for (int p = 2; p < planes.Length; p += 2)
        {
            Assert.Equal(-planes[p].Normal, planes[p + 1].Normal);
            Assert.Equal(-planes[p].Dist, planes[p + 1].Dist);
            Assert.Equal((int)new Plane(planes[p].Normal, 0).Type, planes[p].Type);
            Assert.Equal(planes[p].Type, planes[p + 1].Type);
            if (planes[p].Type < 3)
            {
                Assert.Equal(1f, planes[p].Type switch { 0 => planes[p].Normal.X, 1 => planes[p].Normal.Y, _ => planes[p].Normal.Z });
            }
        }

        Assert.All(BspStructView.As<DNode>(link.Bsp[BspLump.Nodes]).ToArray(), n => Assert.Equal(0, n.PlaneNum & 1));
    }

    /// <summary>
    /// The shared link passes the loader validation <c>ssmap check</c> runs,
    /// as the appending one did.
    /// </summary>
    [Fact]
    public async Task TheSharedLinkPassesTheLoaderValidation()
    {
        (LinkedLevel link, _) = await TurnedLevelAsync();
        ValidationReport report = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.Equal(0, report.ErrorCount);
    }

    /// <summary>
    /// The resolved view of the turned level — every lump the sharing does
    /// not touch, byte for byte, and every plane, texinfo, material and node
    /// reference as the values it resolves to, orientation-normalised — has
    /// the digest the appending link's output had: the shared map is the
    /// same geometry, collision, lighting layout and materials as before,
    /// only numbered differently. Original faces' texinfos are left out:
    /// most of them are stale in the room compiles (see
    /// <c>AStaleOriginalFaceTexinfoDoesNotDecideWhichFacesAreThePlug</c>),
    /// the appending link pointed them into other rooms' entries, and they
    /// are now -1. The link no longer writes the stripped plug brushes, so
    /// the digest is taken with them put back as the empty brushes they were
    /// (<see cref="LinkedBrushProbe.WithPlugsKept"/>): that the digest still
    /// holds shows the drop moved nothing else.
    /// </summary>
    [Fact]
    public async Task TheResolvedLevelIsTheAppendingLinksLevel()
    {
        (LinkedLevel link, RoomLibrary library) = await TurnedLevelAsync();
        Assert.Equal(AppendingLinkResolvedDigest, ResolvedDigest(LinkedBrushProbe.WithPlugsKept(link, library)));
    }

    /// <summary>
    /// A room whose non-axial plane pairs are stored the other way round
    /// (vbsp fixes the order only of axial pairs) shares them flipped with
    /// the room that brought them first: faces and brush sides take the
    /// other half, nodes swap their children, and the linked map is byte for
    /// byte the one two copies of the unflipped room make.
    /// </summary>
    [Fact]
    public async Task AReversedNonAxialPairIsSharedFlipped()
    {
        RoomLibrary library = await CompileAsync();
        RoomObject hub = library.Get("hub");
        (RoomObject reversed, int pairs) = ReverseNonAxialPairs(hub, "hubr");
        Assert.True(pairs > 0, "the hub has no non-axial pair whose world plane the placements share");

        RoomLibrary both = new(hub.Definition.Kit, hub.Definition.CellSize);
        both.Add(hub);
        both.Add(reversed);
        LinkedLevel same = await LinkRowsAsync(library, "hub, hub");
        LinkedLevel flipped = await LinkRowsAsync(both, "hub, hubr");

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if ((BspLump)i != BspLump.Entities)
            {
                Assert.True(same.Bsp[i].Data.Span.SequenceEqual(flipped.Bsp[i].Data.Span), $"{(BspLump)i} differs");
            }
        }

        AssertEveryReferenceIsTheRooms(flipped, both);
    }

    /// <summary>
    /// The tables are built after the parallel planning, in layout order, so
    /// the linked map is the same bytes on one thread or eight, with rooms
    /// planned on the fly (no stored link data) so the planning really runs
    /// on the pool, and the same on a second link.
    /// </summary>
    [Fact]
    public async Task TheSharedTablesAreTheSameOnAnyThreadCount()
    {
        RoomLibrary compiled = await CompileAsync();
        RoomLibrary library = new(compiled.Kit, compiled.CellSize);
        library.Add(compiled.Get("hub") with { Link = null });
        LevelLayout layout = Layout(library, TurnedRows);

        LevelLinkOptions noFold = new() { FoldBrushes = false }; // as TurnedLevelAsync links
        LinkedLevel one = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(degree: 1), noFold);
        LinkedLevel eight = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(degree: 8), noFold);
        LinkedLevel again = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(degree: 8), noFold);
        LinkedLevel stored = (await TurnedLevelAsync()).Link;
        foreach (LinkedLevel other in (LinkedLevel[])[eight, again, stored])
        {
            for (int i = 0; i < BspData.HeaderLumps; i++)
            {
                Assert.True(one.Bsp[i].Data.Span.SequenceEqual(other.Bsp[i].Data.Span), $"{(BspLump)i} differs");
            }
        }
    }

    // ---- the reference ---------------------------------------------------------

    /// <summary>
    /// The digest of <see cref="ResolvedDigest"/> over the turned level as
    /// the appending link wrote it: captured by running this file's
    /// <see cref="ResolvedDigest"/> against the link as it was before the
    /// tables were shared, where every room appended its own copies.
    /// </summary>
    /// <remarks>
    /// Like the vbsp digests (<c>VbspCpuIndependenceTests</c>) it is one
    /// string for every CPU: the rooms compile under the Correct policy and
    /// the relocation is integer exact. If an intended change to the room
    /// compile moves it, recapture it from the failure message, and check
    /// the new value by reverting the link to appending tables.
    /// </remarks>
    private const string AppendingLinkResolvedDigest = "6B9D0924D3EBE505F7AE05B8E871A69E628E7071C43B53D320BD692A0D422DD9";

    /// <summary>The turned level: the ramp-and-occluder hub at every quarter turn, two rows of three.</summary>
    private static readonly string[] TurnedRows = ["hub, hub@90, hub@180", "hub@270, hub@180, hub@90"];

    private static async Task<(LinkedLevel Link, RoomLibrary Library)> TurnedLevelAsync()
    {
        RoomLibrary library = await CompileAsync();
        return (await LinkRowsAsync(library, TurnedRows), library);
    }

    /// <summary>
    /// Links rows of rooms without the brush fold: these facts compare every
    /// brush side with its room's, one for one, which a merged box is not.
    /// </summary>
    private static async Task<LinkedLevel> LinkRowsAsync(RoomLibrary library, params string[] rows) =>
        await LevelLinker.LinkAsync(Layout(library, rows), library, await RoomHarness.ContextAsync(), new LevelLinkOptions { FoldBrushes = false });

    private static LevelLayout Layout(RoomLibrary library, string[] rows) =>
        LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "shared")
            .ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);

    /// <summary>
    /// The walkable hub with a ramp in one corner (a brush whose top slopes
    /// along y, so its plane is non-axial and the same plane in every cell of
    /// a row) and an occluder in another, compiled as <c>ssmap room</c> does.
    /// </summary>
    private static async Task<RoomLibrary> CompileAsync()
    {
        RoomDefinition hub = RoomHarness.WalkableRoom(
            "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VmfDocument vmf = RoomHarness.LibraryVmf(hub);
        VmfChunk ramp = RoomModel.Slab(RoomHarness.Plain, new Vec3(184, 184, 16), new Vec3(216, 216, 48), 70001);
        VmfKey top = ramp.GetChunks("side").First().Keys.First(k => k.Name == "plane");
        top.Value = "(184 216 48) (216 216 48) (216 184 24)";
        vmf.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(ramp);

        VmfChunk occluder = new(MapFileLoader.EntityChunk);
        occluder.AddKey("id", "700002");
        occluder.AddKey("classname", "func_occluder");
        occluder.AddKey("StartActive", "1");
        occluder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(40, 60, 16), new Vec3(56, 120, 120), 70002));
        vmf.Chunks.Add(occluder);

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(vmf);
        RoomLibrary library = new(rooms[0].Definition.Kit, rooms[0].Definition.CellSize);
        foreach (LibraryRoom room in rooms)
        {
            VbspContext context = await RoomHarness.ContextAsync();
            context.MapBase = room.Definition.Name;
            library.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        return library;
    }

    /// <summary>
    /// A copy of a room, renamed, whose non-axial pairs with no x in their
    /// normal (the ones a placement along x leaves where they are) are
    /// stored the other way round, every reference kept on its oriented
    /// plane: faces, brush sides and occluder polygons take the other half,
    /// nodes keep the even index and swap their children.
    /// </summary>
    private static (RoomObject Room, int Pairs) ReverseNonAxialPairs(RoomObject room, string name)
    {
        int reversed = 0;
        RoomObject copy = RoomHarness.WithLumps(room with { Definition = room.Definition with { Name = name } }, bsp =>
        {
            DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
            bool[] swap = new bool[planes.Length / 2];
            for (int k = 0; k < swap.Length; k++)
            {
                if (planes[2 * k].Type >= 3 && planes[2 * k].Normal.X == 0)
                {
                    swap[k] = true;
                    (planes[2 * k], planes[(2 * k) + 1]) = (planes[(2 * k) + 1], planes[2 * k]);
                    reversed++;
                }
            }

            bsp.SetLump(BspLump.Planes, BspStructView.ToLump<DPlane>(planes, 0).Data);
            foreach (BspLump lump in (BspLump[])[BspLump.Faces, BspLump.OriginalFaces])
            {
                DFace[] faces = BspStructView.As<DFace>(bsp[lump]).ToArray();
                for (int f = 0; f < faces.Length; f++)
                {
                    faces[f].PlaneNum = (ushort)Other(faces[f].PlaneNum);
                }

                bsp[lump] = BspStructView.ToLump<DFace>(faces, bsp[lump].Version);
            }

            DBrushSide[] sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
            for (int s = 0; s < sides.Length; s++)
            {
                sides[s].PlaneNum = (ushort)Other(sides[s].PlaneNum);
            }

            bsp.SetLump(BspLump.BrushSides, BspStructView.ToLump<DBrushSide>(sides, 0).Data);
            DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
            for (int n = 0; n < nodes.Length; n++)
            {
                nodes[n].Children = LevelLinker.Orient(nodes[n].Children, swap[nodes[n].PlaneNum >> 1]);
            }

            bsp.SetLump(BspLump.Nodes, BspStructView.ToLump<DNode>(nodes, 0).Data);
            OcclusionLump occlusion = OcclusionLump.Read(bsp[BspLump.Occlusion]);
            for (int p = 0; p < occlusion.Polys.Count; p++)
            {
                DOccluderPolyData poly = occlusion.Polys[p];
                poly.PlaneNum = Other(poly.PlaneNum);
                occlusion.Polys[p] = poly;
            }

            bsp[BspLump.Occlusion] = occlusion.Write();

            int Other(int plane) => swap[plane >> 1] ? plane ^ 1 : plane;
        });

        return (copy, reversed);
    }

    /// <summary>
    /// Checks every room's references against its own lumps moved by its
    /// placement (see <see cref="EveryReferenceResolvesToItsRoomsMovedEntry"/>).
    /// </summary>
    private static void AssertEveryReferenceIsTheRooms(LinkedLevel link, RoomLibrary library)
    {
        BspData bsp = link.Bsp;
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        DFace[] originals = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]).ToArray();
        DBrushSide[] sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        OcclusionLump occlusion = OcclusionLump.Read(bsp[BspLump.Occlusion]);
        int faceBase = 0, originalBase = 0, sideBase = 0, polyBase = 0;
        int nodeBase = LevelLinker.TopPlanes(link.Plan.Layout, link.Plan.Layout.CellSize).Count;
        int checkedNodes = 0, checkedPolys = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            BspData room = library.Get(instance.Placement.Room).Bsp;
            RoomTransform transform = new(instance.Placement, link.Plan.Layout.CellSize);
            (DPlane[] moved, bool[] swapped) = LevelLinker.TransformPlanes(BspStructView.As<DPlane>(room[BspLump.Planes]).ToArray(), transform);
            DPlane Oriented(int p) => moved[swapped[p >> 1] ? p ^ 1 : p];

            DFace[] roomFaces = BspStructView.As<DFace>(room[BspLump.Faces]).ToArray();
            for (int f = 0; f < roomFaces.Length; f++)
            {
                AssertSamePlane(Oriented(roomFaces[f].PlaneNum), planes[faces[faceBase + f].PlaneNum]);
                AssertSameTexInfo(room, roomFaces[f].TexInfo, transform, bsp, faces[faceBase + f].TexInfo);
            }

            DFace[] roomOriginals = BspStructView.As<DFace>(room[BspLump.OriginalFaces]).ToArray();
            for (int f = 0; f < roomOriginals.Length; f++)
            {
                AssertSamePlane(Oriented(roomOriginals[f].PlaneNum), planes[originals[originalBase + f].PlaneNum]);
            }

            // The brush sides are the kept brushes' own, in order: a
            // jointed plug's brush and its sides are not in the link.
            DBrushSide[] roomSides = BspStructView.As<DBrushSide>(room[BspLump.BrushSides]).ToArray();
            DBrush[] roomBrushes = BspStructView.As<DBrush>(room[BspLump.Brushes]).ToArray();
            HashSet<int> stripped = LinkedBrushProbe.Stripped(library.Get(instance.Placement.Room), instance);
            for (int b = 0; b < roomBrushes.Length; b++)
            {
                if (stripped.Contains(b))
                {
                    continue;
                }

                for (int s = roomBrushes[b].FirstSide; s < roomBrushes[b].FirstSide + roomBrushes[b].NumSides; s++)
                {
                    AssertSamePlane(Oriented(roomSides[s].PlaneNum), planes[sides[sideBase].PlaneNum]);
                    AssertSameTexInfo(room, roomSides[s].TexInfo, transform, bsp, sides[sideBase].TexInfo);
                    sideBase++;
                }
            }

            // A node is on its room plane or that plane's flip with its
            // children swapped; a child that is a node says which.
            DNode[] roomNodes = BspStructView.As<DNode>(room[BspLump.Nodes]).ToArray();
            for (int n = 0; n < roomNodes.Length; n++)
            {
                DNode linked = nodes[nodeBase + n];
                DPlane expected = Oriented(roomNodes[n].PlaneNum);
                bool flip = !Same(expected, planes[linked.PlaneNum]);
                AssertSamePlane(flip ? Flip(expected) : expected, planes[linked.PlaneNum]);
                for (int side = 0; side < 2; side++)
                {
                    if (roomNodes[n].Children[side] >= 0)
                    {
                        Assert.Equal(nodeBase + roomNodes[n].Children[side], linked.Children[side ^ (flip ? 1 : 0)]);
                        checkedNodes++;
                    }
                }
            }

            if (room[BspLump.Occlusion].Length > 0)
            {
                OcclusionLump roomOcclusion = OcclusionLump.Read(room[BspLump.Occlusion]);
                for (int p = 0; p < roomOcclusion.Polys.Count; p++)
                {
                    AssertSamePlane(Oriented(roomOcclusion.Polys[p].PlaneNum), planes[occlusion.Polys[polyBase + p].PlaneNum]);
                    checkedPolys++;
                }

                polyBase += roomOcclusion.Polys.Count;
            }

            faceBase += roomFaces.Length;
            originalBase += roomOriginals.Length;
            nodeBase += roomNodes.Length;
        }

        Assert.True(checkedNodes > 0 && checkedPolys > 0, $"{checkedNodes} node children and {checkedPolys} occluder polygons checked");
    }

    private static void AssertSamePlane(DPlane expected, DPlane actual) =>
        Assert.True(Same(expected, actual), $"({actual.Normal.X} {actual.Normal.Y} {actual.Normal.Z}) {actual.Dist} is not ({expected.Normal.X} {expected.Normal.Y} {expected.Normal.Z}) {expected.Dist}");

    private static bool Same(DPlane a, DPlane b) => Key(a) == Key(b);

    private static DPlane Flip(DPlane p) => new() { Normal = -p.Normal, Dist = -p.Dist, Type = p.Type };

    private static LevelLinker.PlaneKey Key(DPlane p) => LevelLinker.PlaneKey.Of(p.Normal, p.Dist, p.Type);

    /// <summary>
    /// A linked texinfo is the room's texinfo moved by the placement (axes,
    /// offsets and flags, compared as numbers) naming the same material; a
    /// negative room index stays as it was.
    /// </summary>
    private static void AssertSameTexInfo(BspData room, int roomIndex, RoomTransform transform, BspData linked, int linkedIndex)
    {
        if (roomIndex < 0)
        {
            Assert.Equal(roomIndex, linkedIndex);
            return;
        }

        TexInfo expected = LevelLinker.TransformTexInfos([BspStructView.As<TexInfo>(room[BspLump.TexInfo])[roomIndex]], transform)[0];
        TexInfo actual = BspStructView.As<TexInfo>(linked[BspLump.TexInfo])[linkedIndex];
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(Bits(expected.TextureVecsTexelsPerWorldUnits[i]), Bits(actual.TextureVecsTexelsPerWorldUnits[i]));
            Assert.Equal(Bits(expected.LightmapVecsLuxelsPerWorldUnits[i]), Bits(actual.LightmapVecsLuxelsPerWorldUnits[i]));
        }

        // A stripped plug face's texinfo is a nodraw copy of its own.
        Assert.Equal(expected.Flags, actual.Flags & ~(int)SourceSharp.MapTools.Materials.SurfaceFlags.NoDraw);
        Assert.Equal(TexDataName(room, expected.TexData), TexDataName(linked, actual.TexData));
    }

    /// <summary>
    /// The SHA-256 of a map as its references resolve: every lump the table
    /// sharing does not renumber, as its bytes; each face, original face,
    /// brush side, node and occluder polygon as its other fields plus the
    /// values its plane (and texinfo) resolve to; each macro texture as its
    /// name. A node is written on whichever of its plane and that plane's
    /// flip sorts first, with its children in that order.
    /// </summary>
    private static string ResolvedDigest(BspData bsp)
    {
        BspLump[] renumbered =
        [
            BspLump.Planes, BspLump.TexData, BspLump.TexInfo, BspLump.TexDataStringData, BspLump.TexDataStringTable,
            BspLump.Faces, BspLump.OriginalFaces, BspLump.BrushSides, BspLump.Nodes, BspLump.Occlusion,
            BspLump.FaceMacroTextureInfo,
        ];
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (!renumbered.Contains((BspLump)i))
            {
                w.Write(i);
                w.Write(bsp[i].Data.Span);
            }
        }

        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            w.Write(entry.IdString());
            w.Write(entry.Data.Span);
        }

        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        DTexData[] datas = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
        void Plane(DPlane p)
        {
            w.Write(Bits(p.Normal.X));
            w.Write(Bits(p.Normal.Y));
            w.Write(Bits(p.Normal.Z));
            w.Write(Bits(p.Dist));
            w.Write(p.Type);
        }

        void Info(int index)
        {
            if (index < 0 || index >= infos.Length)
            {
                w.Write(index);
                return;
            }

            TexInfo t = infos[index];
            for (int i = 0; i < 8; i++)
            {
                w.Write(Bits(t.TextureVecsTexelsPerWorldUnits[i]));
                w.Write(Bits(t.LightmapVecsLuxelsPerWorldUnits[i]));
            }

            w.Write(t.Flags);
            DTexData d = datas[t.TexData];
            w.Write(Bits(d.Reflectivity.X));
            w.Write(Bits(d.Reflectivity.Y));
            w.Write(Bits(d.Reflectivity.Z));
            w.Write(TableString(bsp, d.NameStringTableId));
            w.Write(d.Width);
            w.Write(d.Height);
            w.Write(d.ViewWidth);
            w.Write(d.ViewHeight);
        }

        foreach (BspLump lump in (BspLump[])[BspLump.Faces, BspLump.OriginalFaces])
        {
            foreach (DFace face in BspStructView.As<DFace>(bsp[lump]))
            {
                DFace rest = face;
                rest.PlaneNum = 0;
                rest.TexInfo = 0;
                w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<DFace>(in rest)));
                Plane(planes[face.PlaneNum]);
                if (lump == BspLump.Faces)
                {
                    Info(face.TexInfo);
                }
            }
        }

        foreach (DBrushSide side in BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]))
        {
            DBrushSide rest = side;
            rest.PlaneNum = 0;
            rest.TexInfo = 0;
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<DBrushSide>(in rest)));
            Plane(planes[side.PlaneNum]);
            Info(side.TexInfo);
        }

        foreach (DNode node in BspStructView.As<DNode>(bsp[BspLump.Nodes]))
        {
            DNode rest = node;
            rest.PlaneNum = 0;
            rest.Children = default;
            w.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<DNode>(in rest)));
            DPlane plane = planes[node.PlaneNum];
            bool flip = string.CompareOrdinal(PlaneText(Flip(plane)), PlaneText(plane)) < 0;
            Plane(flip ? Flip(plane) : plane);
            w.Write(node.Children[flip ? 1 : 0]);
            w.Write(node.Children[flip ? 0 : 1]);
        }

        if (bsp[BspLump.Occlusion].Length > 0)
        {
            OcclusionLump occlusion = OcclusionLump.Read(bsp[BspLump.Occlusion]);
            w.Write(Bytes(occlusion.Occluders));
            w.Write(Bytes(occlusion.VertexIndices));
            foreach (DOccluderPolyData poly in occlusion.Polys)
            {
                w.Write(poly.FirstVertexIndex);
                w.Write(poly.VertexCount);
                Plane(planes[poly.PlaneNum]);
            }
        }

        foreach (FaceMacroTextureInfo macro in BspStructView.As<FaceMacroTextureInfo>(bsp[BspLump.FaceMacroTextureInfo]))
        {
            w.Write(macro.MacroTextureNameId == 0xFFFF ? "￿" : TableString(bsp, macro.MacroTextureNameId));
        }

        w.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));

        static string PlaneText(DPlane p) =>
            $"{Bits(p.Normal.X):X8}{Bits(p.Normal.Y):X8}{Bits(p.Normal.Z):X8}{Bits(p.Dist):X8}";

        static byte[] Bytes<T>(List<T> items)
            where T : unmanaged => MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(items)).ToArray();
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>A float's bits with <c>-0</c> folded to <c>+0</c>: numbers compared as numbers.</summary>
    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value == 0f ? 0f : value);

    private static DPlane Plane(Vec3 normal, float dist) =>
        new() { Normal = normal, Dist = dist, Type = (int)new Plane(normal, dist).Type };

    private static TexInfo TexInfoOf(int texData, (float, float, float, float) s, (float, float, float, float) t)
    {
        TexInfo info = new() { TexData = texData };
        (info.TextureVecsTexelsPerWorldUnits[0], info.TextureVecsTexelsPerWorldUnits[1], info.TextureVecsTexelsPerWorldUnits[2], info.TextureVecsTexelsPerWorldUnits[3]) = s;
        (info.TextureVecsTexelsPerWorldUnits[4], info.TextureVecsTexelsPerWorldUnits[5], info.TextureVecsTexelsPerWorldUnits[6], info.TextureVecsTexelsPerWorldUnits[7]) = t;
        return info;
    }

    /// <summary>A map holding only a string table over <paramref name="names"/> and texdatas (name index, width).</summary>
    private static BspData Materials(string[] names, params (int Name, int Width)[] datas)
    {
        List<byte> data = [];
        List<int> table = [];
        foreach (string name in names)
        {
            table.Add(data.Count);
            data.AddRange(Encoding.ASCII.GetBytes(name));
            data.Add(0);
        }

        BspData bsp = new();
        bsp.SetLump(BspLump.TexDataStringData, data.ToArray());
        bsp.SetLump(BspLump.TexDataStringTable, BspStructView.ToLump<int>([.. table], 0).Data);
        DTexData[] texDatas = [.. datas.Select(d => new DTexData
        {
            Reflectivity = new Vec3(0.5f, 0.5f, 0.5f),
            NameStringTableId = d.Name,
            Width = d.Width,
            Height = 64,
            ViewWidth = d.Width,
            ViewHeight = 64,
        })];
        bsp.SetLump(BspLump.TexData, BspStructView.ToLump<DTexData>(texDatas, 0).Data);
        return bsp;
    }

    /// <summary>
    /// A copy of a room, renamed, whose texdata lump is <paramref name="count"/>
    /// entries of its first material at widths <paramref name="from"/> and up:
    /// as many distinct materials.
    /// </summary>
    private static RoomObject WithTexDatas(RoomObject room, string name, int count, int from) =>
        RoomHarness.WithLumps(room with { Definition = room.Definition with { Name = name } }, bsp =>
        {
            DTexData first = BspStructView.As<DTexData>(bsp[BspLump.TexData])[0];
            DTexData[] padded = new DTexData[count];
            for (int i = 0; i < count; i++)
            {
                padded[i] = first;
                padded[i].Width = from + i;
            }

            bsp.SetLump(BspLump.TexData, BspStructView.ToLump<DTexData>(padded, 0).Data);
        });

    /// <summary>A line of one room along +x.</summary>
    private static LevelLayout Line(RoomLibrary library, string room, int length) =>
        RoomHarness.AutoLayout("line", library, [.. Enumerable.Range(0, length).Select(x => (room, x, 0, 0))]);

    private static string TexDataName(BspData bsp, int texData) =>
        TableString(bsp, BspStructView.As<DTexData>(bsp[BspLump.TexData])[texData].NameStringTableId);

    private static string TableString(BspData bsp, int id)
    {
        int offset = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[id];
        ReadOnlySpan<byte> data = bsp[BspLump.TexDataStringData].Data.Span[offset..];
        int end = data.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? data : data[..end]);
    }
}
