//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The linked BSP read lump by lump, not only its visibility: every face on
/// its plane, every room's leaves in its cell, planes typed and ordered the
/// way the engine walks them, texture axes that still land on the same
/// texels, doorways open at joints and shut at caps, the world collision at
/// the rooms' placements, one entity list, one open area — and the whole map
/// through the loader validation <c>ssmap check</c> runs.
/// </summary>
/// <remarks>
/// Most facts are theories over the second room's quarter turn: a pair of
/// four-socket rooms, the second turned 0..3 times, joined at the one wall
/// they share. The layouts are derived from the placements
/// (<see cref="RoomHarness.AutoLayout"/>) so the joint names follow the turn.
/// </remarks>
public sealed class LevelLinkerRelocationTests
{
    private const float OnPlane = 0.01f;

    // ---- L1: faces reach their own surfedges -------------------------------

    /// <summary>
    /// Every drawn and original face's vertices, reached through its surfedges
    /// and edges, lie on its plane. A face run shifted by the wrong base (the
    /// edge count instead of the surfedge count) reads another room's
    /// surfedges from the second room on, and its vertices leave the plane.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryFaceVertexLiesOnItsPlane(int turns)
    {
        LinkedLevel link = await PairAsync(turns, cook: false);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(link.Bsp[BspLump.Planes]);
        foreach (BspLump lump in (BspLump[])[BspLump.Faces, BspLump.OriginalFaces])
        {
            DFace[] faces = BspStructView.As<DFace>(link.Bsp[lump]).ToArray();
            Assert.NotEmpty(faces);
            for (int f = 0; f < faces.Length; f++)
            {
                DPlane plane = planes[faces[f].PlaneNum];
                foreach (Vec3 v in RoomHarness.FaceVertices(link.Bsp, faces[f]))
                {
                    float off = Vec3.Dot(v, plane.Normal) - plane.Dist;
                    Assert.True(
                        MathF.Abs(off) < OnPlane,
                        $"{lump} {f}: vertex ({v.X} {v.Y} {v.Z}) is {off} off its plane ({plane.Normal.X} {plane.Normal.Y} {plane.Normal.Z}) {plane.Dist}");
                }
            }
        }
    }

    // ---- L2: node and leaf bounds move with the room ------------------------

    /// <summary>
    /// Every leaf with a cluster lies inside the cell of the room that owns
    /// the cluster, and every node of a room's subtree inside that room's
    /// cell. Bounds left room-local would put every room's boxes in cell
    /// (0, 0), where the engine's box culling throws the other rooms away.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryRoomsLeavesAndNodesLieInItsCell(int turns)
    {
        (LinkedLevel link, RoomLibrary library) = await PairWithLibraryAsync(turns, cook: false);
        int per = library.Get("hub").ClusterCount;
        DLeaf[] leafs = BspStructView.As<DLeaf>(link.Bsp[BspLump.Leafs]).ToArray();
        int checkedLeaves = 0;
        foreach (DLeaf leaf in leafs)
        {
            if (leaf.Cluster < 0 || (leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            RoomPlacement owner = link.Plan.Layout.Rooms[leaf.Cluster / per].Placement;
            Assert.True(
                LevelLinker.BoxOf(leaf).ContainsWithin(RoomHarness.CellBox(owner), 0f),
                $"leaf of cluster {leaf.Cluster} spans ({leaf.Mins[0]} {leaf.Mins[1]} {leaf.Mins[2]})-"
                + $"({leaf.Maxs[0]} {leaf.Maxs[1]} {leaf.Maxs[2]}), outside cell ({owner.CellX}, {owner.CellY})");
            checkedLeaves++;
        }

        Assert.True(checkedLeaves >= 2, "no open leaf of either room was checked");

        // The rooms' own nodes follow the top tree's nodes in layout order;
        // each room's are its compile's node count long.
        // A room's node boxes are its compile's, which reach past the cell
        // into the void it was built in, so they are checked against the
        // room's own boxes put through the placement, corner by corner.
        DNode[] nodes = BspStructView.As<DNode>(link.Bsp[BspLump.Nodes]).ToArray();
        DNode[] roomNodes = BspStructView.As<DNode>(library.Get("hub").Bsp[BspLump.Nodes]).ToArray();
        int topNodes = LevelLinker.TopPlanes(link.Plan.Layout, RoomHarness.Cell).Count;
        for (int r = 0; r < 2; r++)
        {
            RoomTransform transform = new(link.Plan.Layout.Rooms[r].Placement, RoomHarness.Cell);
            for (int n = 0; n < roomNodes.Length; n++)
            {
                Vec3 a = transform.Apply(new Vec3(roomNodes[n].Mins[0], roomNodes[n].Mins[1], roomNodes[n].Mins[2]));
                Vec3 b = transform.Apply(new Vec3(roomNodes[n].Maxs[0], roomNodes[n].Maxs[1], roomNodes[n].Maxs[2]));
                DNode linked = nodes[topNodes + (r * roomNodes.Length) + n];
                Assert.Equal(
                    (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y)),
                    ((float)linked.Mins[0], (float)linked.Mins[1], (float)linked.Maxs[0], (float)linked.Maxs[1]));
            }
        }
    }

    // ---- L8: planes typed and ordered as the engine walks them --------------

    /// <summary>
    /// Every plane's stored type is the type of its own normal, every pair is
    /// a flip, and every node splits on a plane whose axial normal is the
    /// positive axis. The engine reads an axial plane by one coordinate: a
    /// copied type (an x plane turned into a y plane still typed x) or a node
    /// left on a -x plane routes points to the wrong side.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PlanesAreTypedByTheirNormalAndNodesSplitOnPositiveAxes(int turns)
    {
        LinkedLevel link = await PairAsync(turns, cook: false);
        DPlane[] planes = BspStructView.As<DPlane>(link.Bsp[BspLump.Planes]).ToArray();
        for (int p = 2; p < planes.Length; p += 2)
        {
            Assert.Equal((int)new Plane(planes[p].Normal, planes[p].Dist).Type, planes[p].Type);
            Assert.Equal(planes[p].Type, planes[p + 1].Type);
            Assert.Equal(-planes[p].Normal, planes[p + 1].Normal);
            Assert.Equal(-planes[p].Dist, planes[p + 1].Dist);
        }

        foreach (DNode node in BspStructView.As<DNode>(link.Bsp[BspLump.Nodes]))
        {
            DPlane plane = planes[node.PlaneNum];
            float axis = plane.Type switch { 0 => plane.Normal.X, 1 => plane.Normal.Y, 2 => plane.Normal.Z, _ => 1f };
            Assert.True(axis > 0, $"a node splits on axial plane {node.PlaneNum} whose normal is the negative axis");
        }
    }

    /// <summary>
    /// The engine's walk, axial shortcut included, puts every room's cell
    /// centre in an open leaf of that room: the tree routes a turned room as
    /// it routes an unturned one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheEngineWalkFindsEveryRoomsInteriorAfterATurn(int turns)
    {
        (LinkedLevel link, RoomLibrary library) = await PairWithLibraryAsync(turns, cook: false);
        int per = library.Get("hub").ClusterCount;
        for (int r = 0; r < link.Plan.Layout.Rooms.Count; r++)
        {
            RoomPlacement placement = link.Plan.Layout.Rooms[r].Placement;
            Vec3 centre = new((placement.CellX + 0.5f) * RoomHarness.Cell, (placement.CellY + 0.5f) * RoomHarness.Cell, RoomHarness.Cell / 2);
            DLeaf leaf = RoomHarness.LeafAt(link.Bsp, centre);
            Assert.Equal(0, leaf.Contents & (int)BrushContents.Solid);
            Assert.Equal(r, leaf.Cluster / per);
        }
    }

    /// <summary>
    /// A turned room's texture and lightmap coordinates are the room's own:
    /// at every vertex of every face, the linked texinfo applied to the moved
    /// vertex gives the s and t the room's texinfo gave the room-local vertex.
    /// Axes left unturned, or offsets that ignore the translation, move every
    /// texel and invalidate the faces' lightmap extents.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TextureAndLightmapCoordinatesSurviveTheMove(int turns)
    {
        (LinkedLevel link, RoomLibrary library) = await PairWithLibraryAsync(turns, cook: false);
        BspData room = library.Get("hub").Bsp;
        DFace[] roomFaces = BspStructView.As<DFace>(room[BspLump.Faces]).ToArray();
        TexInfo[] roomInfos = BspStructView.As<TexInfo>(room[BspLump.TexInfo]).ToArray();
        DFace[] linkedFaces = BspStructView.As<DFace>(link.Bsp[BspLump.Faces]).ToArray();
        TexInfo[] linkedInfos = BspStructView.As<TexInfo>(link.Bsp[BspLump.TexInfo]).ToArray();

        // The second room's faces follow the first's.
        for (int f = 0; f < roomFaces.Length; f++)
        {
            DFace linked = linkedFaces[roomFaces.Length + f];
            List<Vec3> local = RoomHarness.FaceVertices(room, roomFaces[f]);
            List<Vec3> moved = RoomHarness.FaceVertices(link.Bsp, linked);
            Assert.Equal(local.Count, moved.Count);
            for (int v = 0; v < local.Count; v++)
            {
                for (int row = 0; row < 2; row++)
                {
                    Assert.Equal(
                        Coordinate(roomInfos[roomFaces[f].TexInfo].TextureVecsTexelsPerWorldUnits, row, local[v]),
                        Coordinate(linkedInfos[linked.TexInfo].TextureVecsTexelsPerWorldUnits, row, moved[v]),
                        3);
                    Assert.Equal(
                        Coordinate(roomInfos[roomFaces[f].TexInfo].LightmapVecsLuxelsPerWorldUnits, row, local[v]),
                        Coordinate(linkedInfos[linked.TexInfo].LightmapVecsLuxelsPerWorldUnits, row, moved[v]),
                        3);
                }
            }
        }
    }

    // ---- L3: doorways open at joints, shut at caps -------------------------

    /// <summary>
    /// The centre of every jointed doorway, on both sides of the wall, is an
    /// empty leaf of an open cluster with no brush in it, and the centre of
    /// every capped socket is solid: a joint is a door, a cap is a wall.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task JointedDoorwaysAreOpenAndCappedSocketsStaySolid(int turns)
    {
        (LinkedLevel link, RoomLibrary library) = await PairWithLibraryAsync(turns, cook: false);
        RoomDefinition hub = library.Get("hub").Definition;
        int joints = 0, caps = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            foreach ((string socket, _) in instance.Joints)
            {
                DLeaf leaf = RoomHarness.LeafAt(link.Bsp, RoomHarness.PlugCentre(hub, instance.Placement, socket));
                Assert.Equal(0, leaf.Contents & (int)BrushContents.Solid);
                Assert.True(leaf.Cluster >= 0, $"the doorway at {socket} is in no cluster");
                Assert.Equal(0, leaf.NumLeafBrushes);
                joints++;
            }

            foreach (string socket in instance.Capped)
            {
                DLeaf leaf = RoomHarness.LeafAt(link.Bsp, RoomHarness.PlugCentre(hub, instance.Placement, socket));
                Assert.NotEqual(0, leaf.Contents & (int)BrushContents.Solid);
                Assert.True(leaf.NumLeafBrushes > 0, $"the capped socket {socket} is solid with no plug brush to hit");
                caps++;
            }
        }

        Assert.Equal(2, joints);
        Assert.Equal(6, caps);
    }

    /// <summary>
    /// A stripped plug leaves nothing behind: its brush is in no leaf's brush
    /// list and is empty in the brush lump, and the plug's faces are drawn
    /// nodraw, while a capped plug's faces still draw.
    /// </summary>
    [Fact]
    public async Task AStrippedPlugLeavesNoBrushAndNoDrawnFace()
    {
        (LinkedLevel link, RoomLibrary library) = await PairWithLibraryAsync(0, cook: false);
        DBrush[] brushes = BspStructView.As<DBrush>(link.Bsp[BspLump.Brushes]).ToArray();
        ReadOnlySpan<ushort> leafBrushes = BspStructView.As<ushort>(link.Bsp[BspLump.LeafBrushes]);
        HashSet<int> listed = [];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(link.Bsp[BspLump.Leafs]))
        {
            for (int b = 0; b < leaf.NumLeafBrushes; b++)
            {
                listed.Add(leafBrushes[leaf.FirstLeafBrush + b]);
            }
        }

        int stripped = 0;
        for (int b = 0; b < brushes.Length; b++)
        {
            if (brushes[b].Contents == 0)
            {
                stripped++;
                Assert.DoesNotContain(b, listed);
            }
        }

        Assert.Equal(2, stripped); // one plug on each side of the one joint

        TexInfo[] infos = BspStructView.As<TexInfo>(link.Bsp[BspLump.TexInfo]).ToArray();
        int hidden = 0, drawn = 0;
        foreach (DFace face in BspStructView.As<DFace>(link.Bsp[BspLump.Faces]))
        {
            int flags = infos[face.TexInfo].Flags;
            if ((flags & (int)SurfaceFlags.Trigger) == 0)
            {
                continue;
            }

            if ((flags & (int)SurfaceFlags.NoDraw) != 0)
            {
                hidden++;
            }
            else
            {
                drawn++;
            }
        }

        Assert.Equal(2, hidden);
        Assert.Equal(6, drawn);
    }

    // ---- L7: the world collision is every room's, at its placement ---------

    /// <summary>
    /// Rooms cooked by the default cooker link, and the linked world
    /// collision holds, for every brush still solid in the linked map, one
    /// convex carrying that brush's linked index whose bounds are the brush's
    /// world bounds; the stripped plugs have none. A collision lump carried
    /// from one room, or not moved, puts the other room's walls in the wrong
    /// place or nowhere.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheWorldCollisionCoversEveryRoomsBrushesWherePlaced(int turns)
    {
        LinkedLevel link = await PairAsync(turns, cook: true);
        IReadOnlyList<PhysCollideModel> records = PhysCollideLump.Read(link.Bsp[BspLump.PhysCollide].Data.Span);
        PhysCollideModel world = Assert.Single(records);
        Assert.Equal(0, world.ModelIndex);
        Assert.Contains("staticsolid", world.KeyText, StringComparison.Ordinal);

        Dictionary<int, IvpCompactLedge> byBrush = [];
        foreach (byte[] solid in world.Solids)
        {
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(solid)))
            {
                Assert.True(byBrush.TryAdd(ledge.ClientData, ledge), $"brush {ledge.ClientData} has two convexes");
            }
        }

        DBrush[] brushes = BspStructView.As<DBrush>(link.Bsp[BspLump.Brushes]).ToArray();
        DBrushSide[] sides = BspStructView.As<DBrushSide>(link.Bsp[BspLump.BrushSides]).ToArray();
        DPlane[] planes = BspStructView.As<DPlane>(link.Bsp[BspLump.Planes]).ToArray();
        for (int b = 0; b < brushes.Length; b++)
        {
            if (brushes[b].Contents == 0)
            {
                Assert.False(byBrush.ContainsKey(b), $"stripped plug brush {b} still collides");
                continue;
            }

            Assert.True(byBrush.TryGetValue(b, out IvpCompactLedge? ledge), $"brush {b} has no convex in the world collision");
            Box expected = BrushBox(brushes[b], sides, planes);
            Box actual = LedgeBox(ledge!);
            Assert.True(
                Near(expected.Mins, actual.Mins) && Near(expected.Maxs, actual.Maxs),
                $"brush {b}: bounds ({Fmt(expected.Mins)})-({Fmt(expected.Maxs)}) but its convex spans ({Fmt(actual.Mins)})-({Fmt(actual.Maxs)})");
        }

        Assert.Equal(brushes.Count(b => b.Contents != 0), byBrush.Count);
    }

    /// <summary>
    /// Rooms compiled with <c>-cooker none</c> still link, into a map with no
    /// world collision lump at all.
    /// </summary>
    [Fact]
    public async Task UncookedRoomsLinkWithoutWorldCollision()
    {
        LinkedLevel link = await PairAsync(0, cook: false);
        Assert.Equal(0, link.Bsp[BspLump.PhysCollide].Length);
    }

    /// <summary>
    /// A level mixing a cooked room with an uncooked one is refused by name:
    /// the uncooked room would have no physics walls.
    /// </summary>
    [Fact]
    public async Task MixingCookedAndUncookedRoomsIsRefused()
    {
        RoomLibrary cooked = await RoomHarness.LibraryAsync(true, RoomHarness.Hub());
        RoomLibrary plain = await RoomHarness.LibraryAsync(false, RoomHarness.Room("bare",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY));
        RoomLibrary library = RoomHarness.Library(cooked.Get("hub"), plain.Get("bare"));
        LevelLayout layout = RoomHarness.AutoLayout("mixed", library, ("hub", 0, 0, 0), ("bare", 1, 0, 0));

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
        Assert.Contains("room hub has world collision and room bare has none", refused.Message, StringComparison.Ordinal);
    }

    // ---- L15: the loader validation over a linked map ----------------------

    /// <summary>
    /// <c>ssmap check</c>'s loader validation over a linked map of two room
    /// kinds, turned and cooked, both as the parsed map and as the file the
    /// link writes: no error, and no warning the rooms themselves do not
    /// already raise (the rooms have no cubemaps, and neither does the level).
    /// </summary>
    [Fact]
    public async Task TheLinkedMapPassesTheLoaderValidation()
    {
        RoomDefinition corner = RoomHarness.Room("corner", RoomFacing.PositiveX, RoomFacing.PositiveY);
        RoomLibrary library = await RoomHarness.LibraryAsync(true, RoomHarness.Hub(), corner);
        LevelLayout layout = RoomHarness.AutoLayout(
            "check", library, ("corner", 0, 0, 0), ("hub", 1, 0, 1), ("hub", 1, 1, 2), ("hub", 0, 1, 3));
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());

        HashSet<string> roomWarnings = [];
        foreach (RoomObject room in library.Rooms)
        {
            ValidationReport own = await BspValidator.CheckAsync(room.Bsp, CancellationToken.None);
            Assert.Equal(0, own.ErrorCount);
            roomWarnings.UnionWith(own.Diagnostics.Select(d => d.Code));
        }

        ValidationReport parsed = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.True(parsed.ErrorCount == 0, string.Join("\n", parsed.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
        Assert.All(parsed.Diagnostics, d => Assert.Contains(d.Code, roomWarnings));

        using MemoryStream file = new();
        await BspFile.SaveAsync(link.Bsp, file, BspWriteMode.Canonical);
        file.Position = 0;
        ValidationReport written = await BspValidator.CheckFileAsync(file, CancellationToken.None);
        Assert.True(written.ErrorCount == 0, string.Join("\n", written.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
        Assert.All(written.Diagnostics, d => Assert.Contains(d.Code, roomWarnings));
    }

    // ---- L5: one open area -----------------------------------------------

    /// <summary>
    /// Every open leaf of every room is in area 1, and the map has areas 0
    /// and 1 only: the rooms are one world to the server. Areas shifted per
    /// room leave each room an island no entity is networked across.
    /// </summary>
    [Fact]
    public async Task EveryOpenLeafIsInTheOneLevelArea()
    {
        LinkedLevel link = await PairAsync(1, cook: false);
        Assert.Equal(2, BspStructView.Count<DArea>(link.Bsp[BspLump.Areas]));
        int open = 0;
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(link.Bsp[BspLump.Leafs]))
        {
            if ((leaf.Contents & (int)BrushContents.Solid) == 0)
            {
                Assert.Equal(1, leaf.GetArea());
                open++;
            }
        }

        Assert.True(open >= 4, "the two rooms and their two doorways are open leaves");
    }

    // ---- L4: one entity list ---------------------------------------------

    /// <summary>
    /// The linked entity lump is one list: one worldspawn, first, whose
    /// extents cover every room as placed, then every room's own entities
    /// with their origins moved. Lumps appended as bytes end at the first
    /// room's NUL, and the engine never sees the second room's entities.
    /// </summary>
    [Fact]
    public async Task TheEntityLumpIsOneListWithEveryRoomsEntitiesMoved()
    {
        LinkedLevel link = await PairAsync(1, cook: false);
        ReadOnlySpan<byte> bytes = link.Bsp[BspLump.Entities].Data.Span;
        Assert.Equal(bytes.Length - 1, bytes.IndexOf((byte)0));

        List<BspEntity> entities = EntityLump.Parse(link.Bsp[BspLump.Entities]);
        Assert.Equal("worldspawn", entities[0].ClassName);
        Assert.Single(entities, e => e.ClassName == "worldspawn");
        Assert.Equal(["128 128 129", "384 128 129"], entities.Where(e => e.ClassName == "info_player_start").Select(e => e.Get("origin")));

        // The rooms' own extents are their interiors, 16..240 room-local.
        Assert.Equal("16 16 16", entities[0].Get("world_mins"));
        Assert.Equal("496 240 240", entities[0].Get("world_maxs"));
    }

    // ---- L9: lightmap offsets ---------------------------------------------

    /// <summary>
    /// A lit face's lightmap offset shifts by the lighting bytes before its
    /// room, including the offset 0 of a room's first luxel, and an unlit
    /// face's -1 stays -1.
    /// </summary>
    [Fact]
    public async Task LightmapOffsetsShiftExceptUnlit()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject lit = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
            for (int f = 0; f < faces.Length; f++)
            {
                faces[f].LightOfs = f == 1 ? -1 : 4 * f;
            }

            bsp.SetLump(BspLump.Faces, BspStructView.ToLump<DFace>(faces, bsp[BspLump.Faces].Version).Data, bsp[BspLump.Faces].Version);
            bsp.SetLump(BspLump.Lighting, new byte[64]);
        });
        RoomLibrary library = RoomHarness.Library(lit);
        LinkedLevel link = await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("lit", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0)), library, await RoomHarness.ContextAsync());

        DFace[] linked = BspStructView.As<DFace>(link.Bsp[BspLump.Faces]).ToArray();
        int per = linked.Length / 2;
        Assert.Equal(0, linked[0].LightOfs);
        Assert.Equal(-1, linked[1].LightOfs);
        Assert.Equal(8, linked[2].LightOfs);
        Assert.Equal(64, linked[per].LightOfs);
        Assert.Equal(-1, linked[per + 1].LightOfs);
        Assert.Equal(64 + 8, linked[per + 2].LightOfs);
        Assert.Equal(128, link.Bsp[BspLump.Lighting].Length);
    }

    /// <summary>
    /// A texinfo of -1 ("none") on a face or a brush side stays -1: it is not
    /// an index, so no room's base applies to it.
    /// </summary>
    [Fact]
    public async Task AnUnsetTexinfoStaysUnset()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject unset = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            DBrushSide[] sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
            sides[0].TexInfo = -1;
            bsp.SetLump(BspLump.BrushSides, BspStructView.ToLump<DBrushSide>(sides, 0).Data);
            DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]).ToArray();
            faces[0].TexInfo = -1;
            bsp.SetLump(BspLump.OriginalFaces, BspStructView.ToLump<DFace>(faces, 0).Data);
        });
        RoomLibrary library = RoomHarness.Library(unset);
        LinkedLevel link = await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0)), library, await RoomHarness.ContextAsync());

        DBrushSide[] linkedSides = BspStructView.As<DBrushSide>(link.Bsp[BspLump.BrushSides]).ToArray();
        DFace[] linkedFaces = BspStructView.As<DFace>(link.Bsp[BspLump.OriginalFaces]).ToArray();
        Assert.Equal(-1, linkedSides[0].TexInfo);
        Assert.Equal(-1, linkedSides[linkedSides.Length / 2].TexInfo);
        Assert.Equal(-1, linkedFaces[0].TexInfo);
        Assert.Equal(-1, linkedFaces[linkedFaces.Length / 2].TexInfo);
    }

    /// <summary>
    /// An original face's texinfo is not trusted. vbsp compacts the texinfo
    /// table after the faces are written and renumbers the drawn faces, the
    /// brush sides and the water data, but not the original faces: a room
    /// with a player clip or a grate (whose side texinfos compaction folds
    /// away) carries original faces whose texinfo is past the end of its
    /// table, which this fact stands in for by moving every original face's
    /// texinfo past it. The plug's original faces are
    /// found through the stripped drawn faces that name them, and take their
    /// nodraw texinfo; reading the stale index used to throw
    /// <see cref="IndexOutOfRangeException"/> out of the link. Every other
    /// stale index is written as -1: the texinfos are shared, so there is no
    /// room base to shift it by, and shifted it only ever pointed into some
    /// other room's entries.
    /// </summary>
    [Fact]
    public async Task AStaleOriginalFaceTexinfoDoesNotDecideWhichFacesAreThePlug()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject stale = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            int past = BspStructView.Count<TexInfo>(bsp[BspLump.TexInfo]) + 7;
            DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]).ToArray();
            for (int f = 0; f < faces.Length; f++)
            {
                faces[f].TexInfo = (short)past;
            }

            bsp.SetLump(BspLump.OriginalFaces, BspStructView.ToLump<DFace>(faces, 0).Data);
        });
        RoomLibrary library = RoomHarness.Library(stale);
        LinkedLevel link = await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0)), library, await RoomHarness.ContextAsync());

        // The original faces of the stripped plug faces are drawn nodraw
        // through a texinfo that exists; every other original face's index
        // named nothing in its room, and names nothing linked: -1.
        TexInfo[] infos = BspStructView.As<TexInfo>(link.Bsp[BspLump.TexInfo]).ToArray();
        DFace[] drawn = BspStructView.As<DFace>(link.Bsp[BspLump.Faces]).ToArray();
        DFace[] originals = BspStructView.As<DFace>(link.Bsp[BspLump.OriginalFaces]).ToArray();
        HashSet<int> plugOriginals = [.. drawn
            .Where(f => (infos[f.TexInfo].Flags & (int)(SurfaceFlags.Trigger | SurfaceFlags.NoDraw))
                == (int)(SurfaceFlags.Trigger | SurfaceFlags.NoDraw))
            .Select(f => f.OrigFace)];
        Assert.Equal(2, plugOriginals.Count); // one plug face on each side of the one joint
        for (int o = 0; o < originals.Length; o++)
        {
            if (plugOriginals.Contains(o))
            {
                Assert.InRange(originals[o].TexInfo, 0, infos.Length - 1);
                Assert.Equal(
                    (int)(SurfaceFlags.Trigger | SurfaceFlags.NoDraw),
                    infos[originals[o].TexInfo].Flags & (int)(SurfaceFlags.Trigger | SurfaceFlags.NoDraw));
            }
            else
            {
                Assert.Equal(-1, originals[o].TexInfo);
            }
        }
    }

    /// <summary>
    /// A stripped drawn face that names no original face (<c>OrigFace</c> -1)
    /// strips none, and every original face keeps its relocated texinfo: the
    /// shared entry with its room texinfo's moved content.
    /// </summary>
    [Fact]
    public async Task APlugFaceWithNoOriginalFaceStripsNone()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject orphan = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
            for (int f = 0; f < faces.Length; f++)
            {
                faces[f].OrigFace = -1;
            }

            bsp.SetLump(BspLump.Faces, BspStructView.ToLump<DFace>(faces, bsp[BspLump.Faces].Version).Data);
        });
        RoomLibrary library = RoomHarness.Library(orphan);
        LinkedLevel link = await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0)), library, await RoomHarness.ContextAsync());

        DFace[] room = BspStructView.As<DFace>(orphan.Bsp[BspLump.OriginalFaces]).ToArray();
        DFace[] originals = BspStructView.As<DFace>(link.Bsp[BspLump.OriginalFaces]).ToArray();
        Assert.Equal(2 * room.Length, originals.Length);
        for (int o = 0; o < originals.Length; o++)
        {
            RoomTransform transform = new(link.Plan.Layout.Rooms[o / room.Length].Placement, RoomHarness.Cell);
            AssertMovedTexInfo(orphan.Bsp, room[o % room.Length].TexInfo, transform, link.Bsp, originals[o].TexInfo);
        }
    }

    /// <summary>
    /// A linked texinfo is the room's texinfo moved by the placement: the
    /// same axes, offsets and flags as the room's put through the transform,
    /// and a texdata naming the same material; a room index that names no
    /// texinfo of the room is -1.
    /// </summary>
    private static void AssertMovedTexInfo(BspData room, int roomIndex, RoomTransform transform, BspData linked, int linkedIndex)
    {
        TexInfo[] roomInfos = BspStructView.As<TexInfo>(room[BspLump.TexInfo]).ToArray();
        if (roomIndex < 0 || roomIndex >= roomInfos.Length)
        {
            Assert.Equal(-1, linkedIndex);
            return;
        }

        TexInfo expected = LevelLinker.TransformTexInfos([roomInfos[roomIndex]], transform)[0];
        TexInfo actual = BspStructView.As<TexInfo>(linked[BspLump.TexInfo])[linkedIndex];
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected.TextureVecsTexelsPerWorldUnits[i], actual.TextureVecsTexelsPerWorldUnits[i]);
            Assert.Equal(expected.LightmapVecsLuxelsPerWorldUnits[i], actual.LightmapVecsLuxelsPerWorldUnits[i]);
        }

        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(TexDataName(room, expected.TexData), TexDataName(linked, actual.TexData));
    }

    /// <summary>
    /// Every linked face's material is its room face's material: the face's
    /// texinfo, its texdata, the string table entry and the string data
    /// resolve in the linked map to the name they resolved to in the room.
    /// The string table is int offsets into the string data; read as ushort
    /// pairs it both miscounts the table and lands offsets far past the data.
    /// The tables are shared, so each material is there once: as many
    /// texdatas and string entries as the rooms have distinct names.
    /// </summary>
    [Fact]
    public async Task EveryTexdataNameIsItsRoomsMaterial()
    {
        RoomDefinition corner = RoomHarness.Room("corner", RoomFacing.PositiveX, RoomFacing.PositiveY);
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub(), corner);
        LevelLayout layout = RoomHarness.AutoLayout("two", library, ("hub", 0, 0, 0), ("corner", 1, 0, 2));
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());

        TexInfo[] linkedInfos = BspStructView.As<TexInfo>(link.Bsp[BspLump.TexInfo]).ToArray();
        DFace[] linkedFaces = BspStructView.As<DFace>(link.Bsp[BspLump.Faces]).ToArray();
        HashSet<string> names = new(StringComparer.Ordinal);
        int faceBase = 0;
        foreach (RoomInstance instance in layout.Rooms)
        {
            BspData room = library.Get(instance.Placement.Room).Bsp;
            TexInfo[] roomInfos = BspStructView.As<TexInfo>(room[BspLump.TexInfo]).ToArray();
            DFace[] roomFaces = BspStructView.As<DFace>(room[BspLump.Faces]).ToArray();
            for (int f = 0; f < roomFaces.Length; f++)
            {
                Assert.Equal(
                    TexDataName(room, roomInfos[roomFaces[f].TexInfo].TexData),
                    TexDataName(link.Bsp, linkedInfos[linkedFaces[faceBase + f].TexInfo].TexData));
            }

            for (int t = 0; t < BspStructView.Count<DTexData>(room[BspLump.TexData]); t++)
            {
                names.Add(TexDataName(room, t));
            }

            faceBase += roomFaces.Length;
        }

        int count = BspStructView.Count<DTexData>(link.Bsp[BspLump.TexData]);
        Assert.Equal(names.Count, count);
        Assert.Equal(names.Count, BspStructView.Count<int>(link.Bsp[BspLump.TexDataStringTable]));
        Assert.Equal(names.Order(StringComparer.Ordinal), Enumerable.Range(0, count).Select(t => TexDataName(link.Bsp, t)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// One solid leaf two plugs reach into is carved for both: the first
    /// plug's chain splits the leaf, the solid fragment the second plug
    /// reaches is carved again, and every point of either doorway lands in an
    /// empty leaf of the doorway's cluster while every point outside them
    /// stays in a solid leaf with the original brush run.
    /// </summary>
    /// <param name="flipped">
    /// Whether the plane table already holds every carve plane's flip as a
    /// pair's even half, so each carve node lands on a pair shared flipped
    /// and must swap its children (and continue its chain on the other
    /// side) to keep the same doorway.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASolidLeafTwoPlugsReachIntoIsCarvedForBoth(bool flipped)
    {
        DLeaf solid = new()
        {
            Contents = (int)BrushContents.Solid,
            Cluster = -1,
            Mins = LevelLinker.Short3(Vec3.Zero),
            Maxs = LevelLinker.Short3(new Vec3(100, 100, 100)),
            FirstLeafBrush = 3,
            NumLeafBrushes = 2,
            LeafWaterDataId = -1,
        };
        DLeaf open = new() { Contents = 0, Cluster = 0, LeafWaterDataId = -1 };
        open.SetAreaFlags(1, LeafFlags.None);
        List<DLeaf> leafs = [solid];
        List<DNode> nodes = [];
        LevelLinker.LinkPlanes planes = new();
        if (flipped)
        {
            foreach (Vec3 axis in (Vec3[])[new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)])
            {
                foreach (float at in (float[])[0, 10, 20, 60, 70, 100])
                {
                    _ = planes.Intern(new DPlane { Normal = -axis, Dist = -at, Type = (int)new Plane(axis, at).Type });
                }
            }
        }

        int stored = planes.Count;
        List<ushort> leafMinDist = [7];
        List<(Box, int)> plugs =
        [
            (new Box(new Vec3(10, 10, 10), new Vec3(20, 20, 20)), 0),
            (new Box(new Vec3(60, 0, 60), new Vec3(70, 100, 70)), 0),
        ];

        int head = LevelLinker.CarveLeaf(5, [open], 0, plugs, nodes, leafs, planes, leafMinDist);

        Assert.True(head >= 0, "the leaf was not split");
        Assert.Equal(leafs.Count, leafMinDist.Count);
        Assert.All(leafMinDist, d => Assert.Equal(7, d));
        for (int p = 2; p < planes.Count; p += 2)
        {
            DPlane even = planes.Planes[p];
            Assert.True(even.Type < 3 && Vec3.Dot(even.Normal, new Vec3(1, 1, 1)) == (flipped ? -1 : 1), "a carve plane is not the expected axis");
        }

        // Shared flipped, the carve adds no plane: it names the stored pairs.
        Assert.Equal(flipped, planes.Count == stored);

        foreach (Vec3 inside in (Vec3[])[new(15, 15, 15), new(11, 19, 11), new(65, 1, 65), new(65, 99, 69)])
        {
            DLeaf leaf = leafs[Walk(inside)];
            Assert.Equal(0, leaf.Contents);
            Assert.Equal(5, leaf.Cluster);
            Assert.Equal(1, leaf.GetArea());
            Assert.Equal(0, leaf.NumLeafBrushes);
        }

        foreach (Vec3 outside in (Vec3[])[new(5, 5, 5), new(40, 40, 40), new(95, 95, 95), new(65, 50, 80), new(15, 25, 15)])
        {
            DLeaf leaf = leafs[Walk(outside)];
            Assert.Equal((int)BrushContents.Solid, leaf.Contents);
            Assert.Equal(-1, leaf.Cluster);
            Assert.Equal((3, 2), (leaf.FirstLeafBrush, leaf.NumLeafBrushes));
        }

        int Walk(Vec3 point)
        {
            int index = head;
            while (index >= 0)
            {
                DPlane plane = planes.Planes[nodes[index].PlaneNum];
                index = Vec3.Dot(point, plane.Normal) - plane.Dist < 0 ? nodes[index].Children[1] : nodes[index].Children[0];
            }

            return ~index;
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static string TexDataName(BspData bsp, int texData)
    {
        int id = BspStructView.As<DTexData>(bsp[BspLump.TexData])[texData].NameStringTableId;
        int offset = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[id];
        ReadOnlySpan<byte> data = bsp[BspLump.TexDataStringData].Data.Span[offset..];
        return System.Text.Encoding.ASCII.GetString(data[..data.IndexOf((byte)0)]);
    }

    private static async Task<LinkedLevel> PairAsync(int turns, bool cook) =>
        (await PairWithLibraryAsync(turns, cook)).Link;

    private static async Task<(LinkedLevel Link, RoomLibrary Library)> PairWithLibraryAsync(int turns, bool cook)
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(cook, RoomHarness.Hub());
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, turns));
        return (await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()), library);
    }

    private static float Coordinate(FloatArray8 vecs, int row, Vec3 p) =>
        (vecs[row * 4] * p.X) + (vecs[(row * 4) + 1] * p.Y) + (vecs[(row * 4) + 2] * p.Z) + vecs[(row * 4) + 3];

    private static Box BrushBox(DBrush brush, DBrushSide[] sides, DPlane[] planes)
    {
        float[] lo = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
        float[] hi = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        Vec3[] axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        for (int s = 0; s < brush.NumSides; s++)
        {
            DPlane p = planes[sides[brush.FirstSide + s].PlaneNum];
            for (int a = 0; a < 3; a++)
            {
                if (p.Normal == axes[a])
                {
                    hi[a] = MathF.Min(hi[a], p.Dist);
                }
                else if (p.Normal == -axes[a])
                {
                    lo[a] = MathF.Max(lo[a], -p.Dist);
                }
            }
        }

        return new Box(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
    }

    private static Box LedgeBox(IvpCompactLedge ledge)
    {
        Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 hi = new(float.MinValue, float.MinValue, float.MinValue);
        for (int p = 0; p < ledge.PointCount; p++)
        {
            (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, p);
            lo = new Vec3(MathF.Min(lo.X, x), MathF.Min(lo.Y, y), MathF.Min(lo.Z, z));
            hi = new Vec3(MathF.Max(hi.X, x), MathF.Max(hi.Y, y), MathF.Max(hi.Z, z));
        }

        return new Box(lo, hi);
    }

    private static bool Near(Vec3 a, Vec3 b) =>
        MathF.Abs(a.X - b.X) < 0.05f && MathF.Abs(a.Y - b.Y) < 0.05f && MathF.Abs(a.Z - b.Z) < 0.05f;

    private static string Fmt(Vec3 v) => $"{v.X} {v.Y} {v.Z}";
}
