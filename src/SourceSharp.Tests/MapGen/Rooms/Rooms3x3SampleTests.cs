//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;

using Bounds = SourceSharp.MapGen.Catalog.Bounds;
using KitPoint = SourceSharp.MapGen.Point;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The 3x3 rooms sample's generator: the kit, the arrangements and their
/// files, and the checked-in copy of them.
/// </summary>
/// <remarks>
/// The generator does not reference the linker, so the facts that matter
/// most here read what it writes with the linker's own readers and
/// transforms: the sidecars through <see cref="RoomDefinitionJson"/>, the
/// layouts through <see cref="LevelLayoutJson"/>, the placement against
/// <see cref="RoomTransform"/>. Two independent spellings of one format, held
/// together.
/// </remarks>
public sealed class Rooms3x3SampleTests
{
    // ---- the checked-in files -------------------------------------------------

    /// <summary>
    /// The files under <c>samples/rooms-3x3/</c> are exactly what the
    /// generator writes, byte for byte, plus the hand-written README: no file
    /// missing, none stale, none extra.
    /// </summary>
    [RepoSourceFact("samples/rooms-3x3/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites()
    {
        string root = Path.GetDirectoryName(RepoSourceFactAttribute.Find("samples/rooms-3x3/README.md")!)!;
        IReadOnlyDictionary<string, byte[]> expected = Rooms3x3Sample.Build();

        // What the README's commands write in place is ignored by git, and
        // by this comparison: the compiled rooms, the linked map, the
        // reference compile's outputs.
        List<string> present = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "README.md"
                && !f.StartsWith("rooms/", StringComparison.Ordinal)
                && !f.StartsWith("out/", StringComparison.Ordinal)
                && !(f.StartsWith("maps/", StringComparison.Ordinal)
                    && Path.GetExtension(f) is ".bsp" or ".prt" or ".log" or ".lin"))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), present);

        foreach ((string path, byte[] bytes) in expected)
        {
            byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(root, path));
            Assert.True(
                bytes.AsSpan().SequenceEqual(onDisk),
                $"samples/rooms-3x3/{path} is not what the generator writes: run tools/RoomsSample --out samples/rooms-3x3");
        }
    }

    /// <summary>Two builds are the same bytes, and every text file ends lines with LF only.</summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = Rooms3x3Sample.Build();
        IReadOnlyDictionary<string, byte[]> second = Rooms3x3Sample.Build();

        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, f => Assert.True(f.Value.AsSpan().SequenceEqual(second[f.Key]), f.Key));
        Assert.All(first, f => Assert.DoesNotContain((byte)'\r', f.Value));
        Assert.Equal(26, first.Count); // gameinfo, 7 materials, 5 rooms and 5 sidecars, 4 layouts and 4 references
    }

    // ---- the kit ---------------------------------------------------------------

    /// <summary>
    /// Each sidecar reads, through the linker's own reader, as the room it
    /// sits beside: the kind's name, the grid, the kit, and its sockets on
    /// the faces the kind claims, facing numbers and all.
    /// </summary>
    [Fact]
    public void EverySidecarReadsAsItsRoom()
    {
        IReadOnlyDictionary<string, byte[]> files = Rooms3x3Sample.Build();
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            RoomDefinition definition = RoomDefinitionJson.Parse(
                Encoding.UTF8.GetString(files[$"maps/{kind.Name}.vmf.roomdef.json"]));
            definition.Validate();

            Assert.Equal(kind.Name, definition.Name);
            Assert.Equal(256f, definition.CellSize);
            Assert.Equal(new SocketKit(96, 224, 16), definition.Kit);
            Assert.Equal(
                kind.Sockets.Select(s => ((RoomFacing)(int)s, Rooms3x3Kit.SocketName(s))),
                definition.Sockets.Select(s => (s.Facing, s.Name)));
        }

        Assert.Equal(RoomFacing.PositiveX, (RoomFacing)(int)KitSide.East);
        Assert.Equal(RoomFacing.NegativeX, (RoomFacing)(int)KitSide.West);
        Assert.Equal(RoomFacing.PositiveY, (RoomFacing)(int)KitSide.North);
        Assert.Equal(RoomFacing.NegativeY, (RoomFacing)(int)KitSide.South);
    }

    /// <summary>
    /// The kit's socket kit fills a doorway from the floor to the ceiling:
    /// the opening the linker's kit rectangle cuts is exactly the jambs' gap
    /// and the plug box the generator writes.
    /// </summary>
    [Fact]
    public void ThePlugBoxIsTheLinkersSealBox()
    {
        SocketKit kit = new(Rooms3x3Kit.DoorWidth, Rooms3x3Kit.DoorHeight, Rooms3x3Kit.Wall);
        RoomDefinition cross = new("cross", Rooms3x3Kit.CellSize, kit,
            [.. Rooms3x3Kit.Kind("cross").Sockets.Select(s => new RoomSocket((RoomFacing)(int)s, Rooms3x3Kit.SocketName(s)))]);
        foreach (RoomSocket socket in cross.Sockets)
        {
            Box seal = RoomLinter.SealBox(cross, socket, cross.CellSize);
            Bounds plug = Rooms3x3Kit.PlugBox((KitSide)(int)socket.Facing);
            Assert.Equal(new Vec3(plug.Mins.X, plug.Mins.Y, plug.Mins.Z), seal.Mins);
            Assert.Equal(new Vec3(plug.Maxs.X, plug.Maxs.Y, plug.Maxs.Z), seal.Maxs);
        }
    }

    /// <summary>
    /// Every room kind's brushes: floor and ceiling, a wall or two jambs per
    /// face, a plug per socket unless opened, then its feature; and every
    /// brush on the 16-unit grid inside the cell, which the sampling lattice
    /// of the equivalence facts relies on.
    /// </summary>
    [Fact]
    public void EveryRoomIsItsShellItsPlugsAndItsFeature()
    {
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            IReadOnlyList<KitBrush> all = Rooms3x3Kit.Brushes(kind);
            Assert.Equal(2 + 4 + kind.Sockets.Count + kind.Sockets.Count + kind.Features.Count, all.Count);
            Assert.Equal(kind.Sockets.Count, all.Count(b => b.Material == Rooms3x3Kit.PlugMaterial));

            HashSet<KitSide> open = [kind.Sockets[0]];
            IReadOnlyList<KitBrush> opened = Rooms3x3Kit.Brushes(kind, open);
            Assert.Equal(all.Count - 1, opened.Count);
            Assert.DoesNotContain(opened, b => b.Material == Rooms3x3Kit.PlugMaterial && b.Box == Rooms3x3Kit.PlugBox(kind.Sockets[0]));

            foreach (KitBrush brush in all)
            {
                foreach (float v in new[] { brush.Box.Mins.X, brush.Box.Mins.Y, brush.Box.Mins.Z, brush.Box.Maxs.X, brush.Box.Maxs.Y, brush.Box.Maxs.Z })
                {
                    Assert.Equal(0f, v % 16f);
                    Assert.InRange(v, 0f, Rooms3x3Kit.CellSize);
                }
            }
        }
    }

    /// <summary>
    /// The shell's slabs meet without overlapping, and with every plug in
    /// place they cover the whole cell boundary: a sealed box.
    /// </summary>
    [Fact]
    public void TheShellWithItsPlugsSealsTheCellWithoutOverlaps()
    {
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            List<Bounds> shell = [.. Rooms3x3Kit.Brushes(kind).Take(Rooms3x3Kit.Brushes(kind).Count - kind.Features.Count).Select(b => b.Box)];
            double volume = shell.Sum(b => (double)(b.Maxs.X - b.Mins.X) * (b.Maxs.Y - b.Mins.Y) * (b.Maxs.Z - b.Mins.Z));
            const double c = Rooms3x3Kit.CellSize, i = c - (2 * Rooms3x3Kit.Wall);
            Assert.Equal((c * c * c) - (i * i * i), volume); // exactly the shell: no overlap, no gap
        }
    }

    /// <summary>
    /// No feature stands in the room's centre column or in front of any door
    /// the kind has: two jointed rooms always meet on open floor, and the
    /// centre at eye height is open in every room.
    /// </summary>
    [Fact]
    public void FeaturesKeepClearOfTheCentreAndTheDoors()
    {
        Bounds centre = new(new KitPoint(112, 112, 16), new KitPoint(144, 144, 240));
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            foreach (KitBrush feature in kind.Features)
            {
                Assert.False(Overlaps(feature.Box, centre), $"{kind.Name}'s feature is in the centre");
                foreach (KitSide side in kind.Sockets)
                {
                    // The plug box pushed a door's width into the room.
                    Bounds plug = Rooms3x3Kit.PlugBox(side);
                    (int dx, int dy) = Rooms3x3Kit.Step(side);
                    Bounds approach = new(
                        new KitPoint(Math.Min(plug.Mins.X, plug.Mins.X - (dx * 96)), Math.Min(plug.Mins.Y, plug.Mins.Y - (dy * 96)), plug.Mins.Z),
                        new KitPoint(Math.Max(plug.Maxs.X, plug.Maxs.X - (dx * 96)), Math.Max(plug.Maxs.Y, plug.Maxs.Y - (dy * 96)), plug.Maxs.Z));
                    Assert.False(Overlaps(feature.Box, approach), $"{kind.Name}'s feature is in front of its {side} face");
                }
            }
        }
    }

    /// <summary>
    /// The materials classify as their names say: the plug is trigger-surfaced
    /// and stays solid (a non-solid plug would leak the room), the clip is
    /// player clip, the grate a grate, the rest plain.
    /// </summary>
    [Fact]
    public async Task TheMaterialsCarryTheirCompileFlags()
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, string vmt) in Rooms3x3Kit.Materials())
        {
            disk.AddText(path, vmt);
        }

        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        async Task<MaterialSurface> Classify(string name) =>
            MaterialSurfaceClassifier.Classify(await MaterialFactsReader.ReadAsync(name, content));

        MaterialSurface plug = await Classify(Rooms3x3Kit.PlugMaterial);
        Assert.NotEqual(0, (int)(plug.Flags & SurfaceFlags.Trigger));
        Assert.Equal(BrushContents.Empty, plug.Contents & ~BrushContents.Solid); // nothing that unsolids it
        Assert.Equal(BrushContents.PlayerClip, (await Classify(Rooms3x3Kit.PlayerClipMaterial)).Contents);
        Assert.NotEqual(0, (int)((await Classify(Rooms3x3Kit.GrateMaterial)).Contents & BrushContents.Grate));
        foreach (string plain in new[] { Rooms3x3Kit.WallMaterial, Rooms3x3Kit.FloorMaterial, Rooms3x3Kit.CeilingMaterial, Rooms3x3Kit.BlockMaterial })
        {
            MaterialSurface surface = await Classify(plain);
            Assert.Equal(BrushContents.Empty, surface.Contents);
            Assert.Equal(0, (int)(surface.Flags & (SurfaceFlags.Trigger | SurfaceFlags.NoDraw)));
        }
    }

    /// <summary>
    /// A room VMF is the kind's brushes: world solids and, for a detail
    /// feature, one <c>func_detail</c>; with its entities, the end room's
    /// player start among them.
    /// </summary>
    [Fact]
    public async Task ARoomVmfCarriesItsBrushesAndEntities()
    {
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            VmfDocument document = await VmfDocument.ParseAsync(Rooms3x3Kit.RoomVmf(kind));
            VmfChunk world = document.Chunks.Single(c => c.Name == "world");
            List<VmfChunk> entities = [.. document.Chunks.Where(c => c.Name == "entity")];
            int detail = kind.Features.Count(f => f.Role == KitBrushRole.Detail);

            Assert.Equal(Rooms3x3Kit.Brushes(kind).Count - detail, world.Chunks.Count(c => c.Name == "solid"));
            Assert.Equal(detail > 0 ? 1 : 0, entities.Count(e => e.GetValue("classname") == "func_detail"));
            Assert.Equal(kind.Entities.Count + (detail > 0 ? 1 : 0), entities.Count);
            Assert.Equal(kind.Name == "end" ? 1 : 0, entities.Count(e => e.GetValue("classname") == "info_player_start"));
        }
    }

    // ---- the placement ---------------------------------------------------------

    /// <summary>
    /// The generator's placement moves every point where the linker's
    /// transform does, for every turn — including turn counts outside 0..3,
    /// which both reduce — and turns every face to the world face the
    /// linker's normal names.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(5)]
    public void ThePlacementIsTheLinkersTransform(int turns)
    {
        Rooms3x3Placement mine = new(2, 1, turns);
        RoomTransform theirs = new(new RoomPlacement("r", 2, 1, turns), Rooms3x3Kit.CellSize);
        foreach (Vec3 p in new[] { new Vec3(0, 0, 0), new Vec3(16, 80, 17), new Vec3(256, 240, 256), new Vec3(40, 200, 5) })
        {
            KitPoint moved = mine.Apply(new KitPoint(p.X, p.Y, p.Z));
            Assert.Equal(theirs.Apply(p), new Vec3(moved.X, moved.Y, moved.Z));
        }

        foreach (KitSide side in Enum.GetValues<KitSide>())
        {
            (int axis, int sign) = theirs.WorldNormal((RoomFacing)(int)side);
            (int dx, int dy) = Rooms3x3Kit.Step(Rooms3x3Kit.Turn(side, turns));
            Assert.Equal(axis == 0 ? (sign, 0) : (0, sign), (dx, dy));
        }

        Bounds box = mine.Apply(new Bounds(new KitPoint(0, 0, 0), new KitPoint(64, 32, 16)));
        Assert.True(box.Mins.X <= box.Maxs.X && box.Mins.Y <= box.Maxs.Y && box.Mins.Z <= box.Maxs.Z);
        Assert.Equal(64 * 32, (box.Maxs.X - box.Mins.X) * (box.Maxs.Y - box.Mins.Y));
    }

    /// <summary>The face helpers: names, opposites and steps, and the refusal of a fifth face.</summary>
    [Fact]
    public void TheFaceHelpersNameAndStep()
    {
        Assert.Equal(["east", "west", "north", "south"], Enum.GetValues<KitSide>().Select(Rooms3x3Kit.SocketName));
        Assert.Equal(KitSide.West, Rooms3x3Kit.Opposite(KitSide.East));
        Assert.Equal(KitSide.South, Rooms3x3Kit.Opposite(KitSide.North));
        Assert.Equal((0, -1), Rooms3x3Kit.Step(KitSide.South));
        Assert.Equal((-1, 0), Rooms3x3Kit.Step(KitSide.West));
        Assert.Equal(KitSide.North, Rooms3x3Kit.Turn(KitSide.East, 1));
        Assert.Equal(KitSide.East, Rooms3x3Kit.Turn(KitSide.South, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rooms3x3Kit.SocketName((KitSide)4));
        Assert.Throws<ArgumentException>(() => Rooms3x3Kit.Kind("attic"));
    }

    // ---- arrangements ----------------------------------------------------------

    /// <summary>An arrangement is nine known rooms with quarter-turn rotations.</summary>
    [Fact]
    public void AnArrangementRefusesWhatIsNotALevel()
    {
        Rooms3x3Cell cross = new("cross", 0);
        Assert.Throws<ArgumentException>(() => new Rooms3x3Arrangement([cross]));
        Assert.Throws<ArgumentNullException>(() => new Rooms3x3Arrangement(null!));
        Assert.Throws<ArgumentException>(() => new Rooms3x3Arrangement([.. Enumerable.Repeat(new Rooms3x3Cell("attic", 0), 9)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rooms3x3Arrangement([.. Enumerable.Repeat(new Rooms3x3Cell("cross", 4), 9)]));
    }

    /// <summary>
    /// A door facing a neighbour's wall, across or along the grid, is not a
    /// level; sockets that line up into separate groups are not one level.
    /// </summary>
    [Fact]
    public void SocketsMustLineUpAndConnectEveryRoom()
    {
        // Every room's only door faces east, into the next room's west wall.
        Rooms3x3Arrangement east = new([.. Enumerable.Repeat(new Rooms3x3Cell("end", 0), 9)]);
        Assert.False(east.SocketsLineUp());
        Assert.False(east.IsValid());

        // Every door faces north, into the next room's south wall.
        Rooms3x3Arrangement north = new([.. Enumerable.Repeat(new Rooms3x3Cell("end", 1), 9)]);
        Assert.False(north.SocketsLineUp());

        // North-south halls line up into three columns that never meet.
        Rooms3x3Arrangement columns = new([.. Enumerable.Repeat(new Rooms3x3Cell("hall", 1), 9)]);
        Assert.True(columns.SocketsLineUp());
        Assert.Equal([0, 1, 2, 0, 1, 2, 0, 1, 2], columns.Components());
        Assert.False(columns.IsValid());

        // All crosses: one level, every socket at the grid's edge capped.
        Rooms3x3Arrangement crosses = new([.. Enumerable.Repeat(new Rooms3x3Cell("cross", 0), 9)]);
        Assert.True(crosses.IsValid());
        Assert.Equal(12, Enumerable.Range(0, 9).Sum(i => crosses.Caps(i % 3, i / 3).Count));
    }

    /// <summary>
    /// The sample level is valid, its joints and caps are the documented
    /// ones, and four quarter turns bring it back.
    /// </summary>
    [Fact]
    public void TheSampleLevelIsTheDocumentedOne()
    {
        Rooms3x3Arrangement level = Rooms3x3Permutations.Canonical;
        Assert.True(level.IsValid());
        Assert.Equal("corner@1 corner@0 corner@1 / tee@3 cross@0 tee@1 / end@3 hall@1 corner@3", level.ToString());

        // The three capped sockets: the south-west corner's west face, the
        // hall's north face, the north-east corner's east face.
        Assert.Equal([KitSide.North], level.Caps(0, 0)); // room-local north, turned to the world's west
        Assert.Equal([KitSide.East], level.Caps(1, 2));  // room-local east, turned to the world's north
        Assert.Equal([KitSide.North], level.Caps(2, 2)); // room-local north, turned to the world's east
        Assert.Equal(3, Enumerable.Range(0, 9).Sum(i => level.Caps(i % 3, i / 3).Count));
        Assert.Equal(9 * 2, Enumerable.Range(0, 9).Sum(i => level.Joints(i % 3, i / 3).Count));

        Rooms3x3Arrangement turned = level.Turned();
        Assert.NotEqual(level, turned);
        Assert.True(turned.IsValid());
        Assert.Equal(level, turned.Turned().Turned().Turned());
        Assert.Equal(level.GetHashCode(), new Rooms3x3Arrangement(level.Cells).GetHashCode());
        Assert.False(level.Equals(null));
        Assert.False(level.Equals((object)"rooms3x3"));
        Assert.Equal(
            Rooms3x3Permutations.Multiset,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["corner"] = 4, ["tee"] = 2, ["cross"] = 1, ["end"] = 1, ["hall"] = 1 });
    }

    /// <summary>
    /// A layout reads, through the linker's reader, as the arrangement: every
    /// room in its cell and turn, and every joint met from the other side by
    /// the neighbour the linker's own transform says the socket faces.
    /// </summary>
    [Fact]
    public void ALayoutIsTheArrangementAsTheLinkerReadsIt()
    {
        foreach (Rooms3x3Arrangement arrangement in Rooms3x3Permutations.DefaultCases(Rooms3x3Permutations.All()).Select(c => c.Arrangement))
        {
            LevelLayout layout = LevelLayoutJson.Parse(arrangement.LayoutJson("l"));
            layout.Validate();
            Assert.True(LevelLayoutJson.HasGrid(layout));
            Assert.Equal(9, layout.Rooms.Count);

            foreach (RoomInstance room in layout.Rooms)
            {
                RoomPlacement placement = room.Placement;
                Assert.Equal(arrangement[placement.CellX, placement.CellY], new Rooms3x3Cell(placement.Room, placement.Rotation));
                RoomKind kind = Rooms3x3Kit.Kind(placement.Room);
                Assert.Equal(
                    kind.Sockets.Select(Rooms3x3Kit.SocketName).Order(),
                    room.Joints.Select(j => j.Socket).Concat(room.Capped).Order());

                foreach ((string socket, string theirs) in room.Joints)
                {
                    KitSide side = kind.Sockets.Single(s => Rooms3x3Kit.SocketName(s) == socket);
                    (int axis, int sign) = new RoomTransform(placement, 256).WorldNormal((RoomFacing)(int)side);
                    RoomInstance neighbour = layout.Rooms.Single(r =>
                        r.Placement.CellX == placement.CellX + (axis == 0 ? sign : 0)
                        && r.Placement.CellY == placement.CellY + (axis == 1 ? sign : 0));
                    Assert.Contains((theirs, socket), neighbour.Joints);
                }
            }
        }
    }

    /// <summary>
    /// The monolithic VMF is every room placed: the plugs of capped sockets
    /// only, one <c>func_detail</c> per hall, a light per room and the one
    /// player start, and every brush inside the 3x3 grid.
    /// </summary>
    [Fact]
    public async Task TheMonolithicVmfIsEveryRoomPlacedWithTheJointedPlugsLeftOut()
    {
        Rooms3x3Arrangement level = Rooms3x3Permutations.Canonical;
        VmfDocument document = await VmfDocument.ParseAsync(level.MonolithicVmf());
        VmfChunk world = document.Chunks.Single(c => c.Name == "world");
        List<VmfChunk> entities = [.. document.Chunks.Where(c => c.Name == "entity")];

        int plugs = world.Chunks.Count(s => s.Name == "solid"
            && s.Chunks.Where(c => c.Name == "side").All(side => side.GetValue("material") == Rooms3x3Kit.PlugMaterial));
        Assert.Equal(3, plugs);
        Assert.Equal(1, entities.Count(e => e.GetValue("classname") == "func_detail"));
        Assert.Equal(9, entities.Count(e => e.GetValue("classname") == "light"));
        VmfChunk start = Assert.Single(entities, e => e.GetValue("classname") == "info_player_start");

        // The end room stands at (0, 2) turned three times: its player start
        // at room-local (64, 96) facing east is at (96, 512 + 192) facing south.
        Assert.Equal("96 704 17", start.GetValue("origin"));
        Assert.Equal("0 270 0", start.GetValue("angles"));

        foreach (VmfChunk side in world.Chunks.SelectMany(s => s.Chunks).Where(c => c.Name == "side"))
        {
            foreach (string number in side.GetValue("plane")!.Split(['(', ')', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.InRange(float.Parse(number, System.Globalization.CultureInfo.InvariantCulture), 0f, 3 * Rooms3x3Kit.CellSize);
            }
        }
    }

    // ---- the permutations ------------------------------------------------------

    /// <summary>
    /// The enumeration: exactly the documented count, every one valid and
    /// distinct, the sample level among them, and the set closed under
    /// turning the whole level — a search that skipped a branch would lose
    /// some turned copy of what it found.
    /// </summary>
    [Fact]
    public void TheEnumerationIsEveryValidArrangement()
    {
        IReadOnlyList<Rooms3x3Arrangement> all = Rooms3x3Permutations.All();
        Assert.Equal(Rooms3x3Permutations.ValidCount, all.Count);

        HashSet<Rooms3x3Arrangement> set = [.. all];
        Assert.Equal(all.Count, set.Count);
        Assert.Contains(Rooms3x3Permutations.Canonical, set);
        foreach (Rooms3x3Arrangement arrangement in all)
        {
            Assert.True(arrangement.IsValid(), arrangement.ToString());
            Assert.Contains(arrangement.Turned(), set);
        }
    }

    /// <summary>
    /// The default subset is the documented one, in order: the four turns,
    /// the first arrangement with each kind in the centre and in the corner
    /// (the cross's corner pick is the corner's centre pick, so it adds a
    /// reason, not a case), and four drawn with the fixed seed.
    /// </summary>
    [Fact]
    public void TheDefaultSubsetIsTheDocumentedSeventeen()
    {
        IReadOnlyList<Rooms3x3Case> cases = Rooms3x3Permutations.DefaultCases(Rooms3x3Permutations.All());
        Assert.Equal(
            [
                "rooms3x3", "rooms3x3_turn1", "rooms3x3_turn2", "rooms3x3_turn3",
                "cross_in_centre", "tee_in_centre", "corner_in_centre", "hall_in_centre", "end_in_centre",
                "tee_in_corner", "corner_in_corner", "hall_in_corner", "end_in_corner",
                "drawn_55536", "drawn_59973", "drawn_51431", "drawn_40074",
            ],
            cases.Select(c => c.Name));
        Assert.Contains("the first arrangement with the cross in the corner", cases.Single(c => c.Name == "corner_in_centre").Reason);
        Assert.Equal("cross", cases.Single(c => c.Name == "corner_in_centre").Arrangement[0, 0].Kind);
        Assert.Throws<ArgumentException>(() => Rooms3x3Permutations.DefaultCases([]));
    }

    private static bool Overlaps(Bounds a, Bounds b) =>
        a.Mins.X < b.Maxs.X && b.Mins.X < a.Maxs.X
        && a.Mins.Y < b.Maxs.Y && b.Mins.Y < a.Maxs.Y
        && a.Mins.Z < b.Maxs.Z && b.Mins.Z < a.Maxs.Z;
}
