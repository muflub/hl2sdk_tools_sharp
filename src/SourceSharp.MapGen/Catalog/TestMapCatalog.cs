namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The catalogue: every test map this tree can generate, with what it exercises
/// and what it is expected to produce.
///
/// <para>
/// WHAT IS HERE AND WHAT IS NOT. plan_maptools.md §2a's feature table spans four
/// phases; this is the part Phase 1 and Phase 2 need first — the vvis features in
/// full, and the handful of vbsp geometry and structure features that a vvis
/// entry has to be built out of anyway (a leak, func_detail, hint/skip, the tool
/// textures, an areaportal, water). Everything else in
/// <see cref="MapFeature"/> is a declared, listed gap: the matrix fact names
/// each one and the phase that owns it, and requires that no gap belongs to a
/// phase that has already arrived.
/// </para>
///
/// <para>
/// Entries are STATIC DATA built by a property rather than a cached list,
/// because the catalogue's own rule is no mutable static state and a lazily
/// filled cache is exactly that. Building it is a few hundred objects and no
/// geometry — the generators do not run until an entry is asked to build.
/// </para>
///
/// <para>
/// <b>WHERE THE NUMBERS CAME FROM.</b> Every leak, area, cluster and portal
/// count below is a MEASUREMENT, not an argument: each entry was emitted and
/// compiled with stock vbsp and vvis under wine, and the counts read back off
/// the `.prt` and the BSP's lumps. They were worth taking — the first draft of
/// this file reasoned that one convex sealed room is one cluster and no
/// portals, and stock says four and four, because vbsp cuts the world on a
/// 1024-unit block grid before it cuts anything else and a room centred on the
/// origin straddles two of those boundaries. Nothing in the map asks for that
/// and no amount of reading the map would have said so.
/// </para>
///
/// <para>
/// Toolset: `Source SDK Base 2013 Multiplayer/bin` (the 32-bit set, which is
/// the one with `filesystem_stdio.dll` beside it — see `tools/maptools` for why
/// that decides whether a compiled map has lighting in it), driven through
/// Proton Experimental's wine with `tools/mapgame`'s gameinfo. Stock reported
/// zero "Material not found" for every entry, which is the other thing the run
/// established: the palette these maps are built from resolves.
/// </para>
/// </summary>
public static partial class TestMapCatalog
{
    /// <summary>Every entry, in level order.</summary>
    public static IReadOnlyList<CatalogEntry> All { get; } =
    [
        .. Micro(),
        .. Visibility(),
        .. Geometry(),
        .. SurfaceContent(),
        .. Interactions(),
        .. SurfaceContentInteractions(),
        .. Scale(),
    ];

    /// <summary>The entries at one level.</summary>
    /// <param name="level">Which level.</param>
    public static IReadOnlyList<CatalogEntry> AtLevel(MapLevel level)
        => [.. All.Where(e => e.Level == level)];

    /// <summary>The entries that exercise a feature, at any level.</summary>
    /// <param name="feature">A member of the vocabulary.</param>
    public static IReadOnlyList<CatalogEntry> Exercising(MapFeature feature)
        => [.. All.Where(e => e.Features.Contains(feature))];

    /// <summary>One entry, by name.</summary>
    /// <param name="name">The entry's name.</param>
    public static CatalogEntry Named(string name)
        => All.FirstOrDefault(e => e.Name == name)
           ?? throw new ArgumentException($"no catalogue entry named '{name}'", nameof(name));

    // ------------------------------------------------------------------
    // L0 — micro. A handful of brushes and no shell.
    //
    // NOT COMPILABLE ON THEIR OWN, and their instruments say so: an L0 entry
    // has no sealed volume and no entity inside one, so vbsp would report a
    // leak and every instrument would be measuring that instead of the brush.
    // §2a's answer is to feed them to the compiler as an in-memory map model,
    // which arrives with vbsp in Phase 3; until then they are declarations with
    // a generator attached, and the facts that run on them are facts about the
    // geometry they emit.
    // ------------------------------------------------------------------

    private static IEnumerable<CatalogEntry> Micro()
    {
        yield return new CatalogEntry
        {
            Name = "l0_unit_cube",
            Level = MapLevel.L0,
            Features = [MapFeature.StructuralBrush],
            Summary = "one 64-unit structural brush, and nothing else at all",
            Instruments = CompileInstrument.None,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(1),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(0),
                Materials = [RoomKit.BlockMaterial],
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                RoomKit.Worldspawn(map);
                RoomKit.Block(map, Bounds.Around(new Point(0f, 0f, 0f), new Point(32f, 32f, 32f)),
                              RoomKit.BlockMaterial);

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l0_overlapping_cubes",
            Level = MapLevel.L0,
            Features = [MapFeature.OverlappingBrushCsg],
            Summary = "two 64-unit brushes sharing an eighth of their volume",
            Instruments = CompileInstrument.None,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(2),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(0),
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                RoomKit.Worldspawn(map);

                // Offset by half the extent on every axis, so the overlap is a
                // corner rather than a face: a shared FACE is a different case
                // (no volume to chop) and belongs to its own entry when
                // somebody writes one.
                RoomKit.Block(map, Bounds.Around(new Point(0f, 0f, 0f), new Point(32f, 32f, 32f)),
                              RoomKit.BlockMaterial);
                RoomKit.Block(map, Bounds.Around(new Point(32f, 32f, 32f), new Point(32f, 32f, 32f)),
                              RoomKit.BlockMaterial);

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l0_micro_brush",
            Level = MapLevel.L0,
            Features = [MapFeature.MicroBrush],
            Summary = "a brush of volume 0.125, under vbsp's default -micro threshold of 1.0",
            Instruments = CompileInstrument.None,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(1),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(0),
            },
            Options = new CatalogCompileOptions { Vbsp = ["-micro", "1.0"] },
            Generator = _ =>
            {
                var map = new VmfMap();
                RoomKit.Worldspawn(map);

                // Half a unit on each side. `VbspOptions.MicroVolume` defaults
                // to 1.0 and the report is "brush with volume < 1.0", so 0.125
                // is under it by a factor of eight — near enough to be the case
                // under test, far enough that a change of epsilon does not
                // silently turn the entry into a different one.
                RoomKit.Block(map, new Bounds(new Point(0f, 0f, 0f), new Point(0.5f, 0.5f, 0.5f)),
                              RoomKit.BlockMaterial);

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l0_nonaxial_wedge",
            Level = MapLevel.L0,
            Features = [MapFeature.NonAxialBrush],
            Summary = "a right triangular prism: five faces, one of them on no axis",
            Instruments = CompileInstrument.None,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(1),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(0),
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                RoomKit.Worldspawn(map);
                map.WorldSolids.Add(RoomKit.Wedge(
                    new Bounds(new Point(-64f, -64f, 0f), new Point(64f, 64f, 128f)),
                    RoomKit.BlockMaterial));

                return map;
            },
        };
    }

    // ------------------------------------------------------------------
    // L1 — vvis. Phase 2's whole feature list, one entry each.
    // ------------------------------------------------------------------

    private static IEnumerable<CatalogEntry> Visibility()
    {
        yield return new CatalogEntry
        {
            Name = "l1_sealed_room",
            Level = MapLevel.L1,
            Features = [MapFeature.TrivialPvs],
            Summary = "one sealed room: the trivial PVS, and the baseline for everything else",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(2),
                Classnames = ["info_player_start", "light"],
                Leaks = false,
                Areas = CountRange.Exactly(2),

                // FOUR CLUSTERS AND FOUR PORTALS FOR ONE EMPTY BOX, and the
                // reason is worth knowing before reading any other entry here.
                //
                // This was declared as 1 and 0 on the argument that the
                // interior is convex, which is true and is not what decides it:
                // vbsp cuts the world on a 1024-unit BLOCK GRID before it cuts
                // anything else, and a 544-unit room centred on the origin
                // straddles the x = 0 and y = 0 block boundaries. Four blocks,
                // four leaves, four portals between them. Nothing in the map
                // asked for that and nothing in the map can avoid it.
                //
                // The number stands as stock measured it rather than being
                // engineered away by moving the room off the origin — on the
                // origin is where a sign error shows up, and the block grid is
                // a vbsp behaviour every entry in the catalogue is subject to.
                Clusters = CountRange.Exactly(4),
                Portals = CountRange.Exactly(4),
                Probes = [new VisProbe("centre", new Point(0f, 0f, 64f))],
                Lights = CountRange.Exactly(1),
            },
            Generator = _ => SealedRoom(),
        };

        yield return Chain(
            name: "l1_two_rooms_and_a_door",
            feature: MapFeature.PortalThroughDoorway,
            summary: "two rooms joined by one doorway: the simplest flood there is",
            chain: RoomChain.Straight(2),
            clusters: 12, portals: 14,
            mustSee: [new ProbePair("room0", "room1")]);

        yield return Chain(
            name: "l1_long_corridor",
            feature: MapFeature.DeepPortalChain,
            summary: "sixteen rooms, doorways alternating side to side: portal flow with a long way to walk",
            chain: RoomChain.Zigzag(16),
            clusters: 60, portals: 65,
            mustSee: [new ProbePair("room0", "room1")],

            // Three doorways alternating by 256 put the reachable spread at
            // doorway 2 at +-96 (RoomChain.ReachableSpreadAfter), and doorway
            // 2 spans -288..-224. There is no line, so room0 cannot see room3
            // and certainly cannot see room15.
            mustNotSee: [new ProbePair("room0", "room3"), new ProbePair("room0", "room15")]);

        yield return new CatalogEntry
        {
            Name = "l1_open_arena",
            Level = MapLevel.L1,
            Features = [MapFeature.WideOpenPvs],
            Summary = "one room, thirty-six pillars: a lattice of leaves that nearly all see each other",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(new Arena { PillarsPerSide = 6 }.BrushCount),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(2),
                Leaks = false,
                Areas = CountRange.Exactly(2),

                // Measured. The lattice cuts the floor into far more leaves
                // than there are pillars, and the number depends on the widths
                // this entry's seed drew — which is exactly why it is pinned
                // here rather than reasoned about: byte-stable generation makes
                // it a property of the entry.
                Clusters = CountRange.Exactly(90),
                Portals = CountRange.Exactly(180),
                Probes = new Arena { PillarsPerSide = 6 }.Probes,

                // Both probes sit at the same X, outside the lattice's reach,
                // so the line between them is clear whatever widths the seed
                // drew. See Arena.Probes for why the diagonal pair is not
                // declared.
                MustSee = [new ProbePair("ring_low", "ring_high")],
            },
            Generator = random =>
            {
                var map = new VmfMap();
                new Arena { PillarsPerSide = 6 }.Build(map, random);

                return map;
            },
        };

        yield return Chain(
            name: "l1_three_doors_in_a_line",
            feature: MapFeature.SightlineThroughDoors,
            summary: "four rooms, three doorways dead on the axis: the first room must see the fourth",
            chain: RoomChain.Straight(4),
            clusters: 24, portals: 30,
            mustSee: [new ProbePair("room0", "room3")]);

        yield return Chain(
            name: "l1_three_doors_almost_in_a_line",
            feature: MapFeature.NearMissSightline,
            summary: "the same four rooms with the third doorway stepped aside: it must NOT see",
            chain: new RoomChain { Rooms = 4, DoorOffsets = [0f, 0f, 256f] },
            clusters: 20, portals: 24,
            mustSee: [new ProbePair("room0", "room1"), new ProbePair("room2", "room3")],

            // A line through the first two doorways can be at most +-96 across
            // by the third (RoomChain.ReachableSpreadAfter(2) = 96), and the
            // third doorway spans 224..288. Disjoint, so there is no line at
            // all -- which is the whole content of this entry, and the one
            // expectation in the vvis catalogue that a PVS saying "everything
            // sees everything" fails.
            mustNotSee: [new ProbePair("room0", "room3")]);

        yield return Chain(
            name: "l1_vvis_fast",
            feature: MapFeature.VvisFast,
            summary: "three rooms compiled with -fast: base vis only, no portal flow",
            chain: RoomChain.Zigzag(3),
            clusters: 14, portals: 18,
            mustSee: [new ProbePair("room0", "room1")],

            // NO must-not-see, deliberately. -fast stops after BasePortalVis,
            // so its PVS is a SUPERSET of the exact answer: every must-see
            // expectation still holds and every must-not-see one is entitled to
            // fail. Declaring one here would be declaring a bug.
            mustNotSee: [],
            options: new CatalogCompileOptions { Vvis = ["-fast"] });

        yield return Chain(
            name: "l1_corridor_farz",
            feature: MapFeature.Farz,
            summary: "eight rooms and a worldspawn farz of 1024: visibility clipped by distance",
            chain: RoomChain.Zigzag(8),
            clusters: 32, portals: 37,
            mustSee: [new ProbePair("room0", "room1")],
            mustNotSee: [new ProbePair("room0", "room7")],
            extra: map => map.World.Add(new("farz", "1024")));

        yield return Chain(
            name: "l1_corridor_radius_override",
            feature: MapFeature.VvisRadiusOverride,
            summary: "the same eight rooms, the radius given on the command line instead",
            chain: RoomChain.Zigzag(8),
            clusters: 32, portals: 37,
            mustSee: [new ProbePair("room0", "room1")],
            mustNotSee: [new ProbePair("room0", "room7")],
            options: new CatalogCompileOptions { Vvis = ["-radius_override", "1024"] });

        yield return Chain(
            name: "l1_water_and_fog_leaves",
            feature: MapFeature.WaterAndFogLeaves,
            summary: "two rooms with the second one flooded: leaves that are under water",
            chain: RoomChain.Straight(2),
            clusters: 16, portals: 26,
            mustSee: [new ProbePair("room0", "room1")],
            brushesAdded: 1,
            hasLeafWaterData: true,
            extra: map =>
            {
                var chain = RoomChain.Straight(2);
                Bounds room = chain.Room(1);

                RoomKit.Pool(map, new Bounds(
                    room.Mins,
                    new Point(room.Maxs.X, room.Maxs.Y, room.Mins.Z + 64f)));
            });
    }

    // ------------------------------------------------------------------
    // L1 — the vbsp features a vvis entry is built out of anyway.
    // ------------------------------------------------------------------

    private static IEnumerable<CatalogEntry> Geometry()
    {
        yield return new CatalogEntry
        {
            Name = "l1_leak",
            Level = MapLevel.L1,
            Features = [MapFeature.Leak],
            Summary = "a sealed room with a light outside it: an entity in the void",

            // No stage isolation: a leaked map has no .prt, so there is nothing
            // to hand the next tool and nothing for it to be judged on.
            Instruments = CompileInstrument.LoaderCheck
                          | CompileInstrument.SemanticDiff
                          | CompileInstrument.Determinism,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(3),
                Leaks = true,

                // Three, where an unleaked room is two: the void outside the
                // shell becomes an area of its own once the flood gets out.
                Areas = CountRange.Exactly(3),
                Probes = [new VisProbe("centre", new Point(0f, 0f, 64f))],
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();

                // Above the ceiling, in the void. vbsp floods outwards from
                // every entity origin, so one outside the shell is the whole
                // definition of a leak -- and the entity inside is left in
                // place so that the map still has an inside for the leak to be
                // reported against.
                RoomKit.Light(map, new Point(0f, 0f, StandardRoom.Maxs.Z + 512f));

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_func_detail",
            Level = MapLevel.L1,
            Features = [MapFeature.FuncDetail],
            Summary = "one pillar structural and an identical one tied to func_detail",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                BrushEntities = CountRange.Exactly(1),
                PointEntities = CountRange.Exactly(2),
                Classnames = ["func_detail"],
                Leaks = false,
                Areas = CountRange.Exactly(2),

                // The control is the point of the entry: two pillars of the
                // same size in the same room, one of which cuts the tree and
                // one of which must not. Eight clusters against the empty
                // room's four — and the detail pillar contributed none of the
                // four that were added, which is the claim this entry exists
                // to hold a port to.
                Clusters = CountRange.Exactly(8),
                Portals = CountRange.Exactly(11),
                Probes =
                [
                    new VisProbe("by_structural", new Point(-160f, 0f, 64f)),
                    new VisProbe("by_detail", new Point(160f, 0f, 64f)),
                ],
                MustSee = [new ProbePair("by_structural", "by_detail")],
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();
                var pillar = new Bounds(new Point(-96f, -32f, 0f), new Point(-32f, 32f, 256f));

                RoomKit.Block(map, pillar, RoomKit.BlockMaterial);
                RoomKit.BrushEntity(
                    map, "func_detail",
                    [VmfMap.Box(
                        pillar.Offset(new Point(128f, 0f, 0f)).Mins,
                        pillar.Offset(new Point(128f, 0f, 0f)).Maxs,
                        RoomKit.BlockMaterial)]);

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_hint_skip",
            Level = MapLevel.L1,
            Features = [MapFeature.HintSkip],
            Summary = "a sealed room cut in two by a hint brush, skip on its other five faces",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(2),
                Materials = [RoomKit.Hint, RoomKit.Skip],
                Leaks = false,
                Areas = CountRange.Exactly(2),

                // Six against the same room's four: the hint face at x = 8 cuts
                // the two blocks it crosses, and nothing else in this map could
                // have. The must-see pair below is what tells a hint apart from
                // a solid slab that would split the tree the same way — a hint
                // brush is not solid, so the two sides still see each other.
                Clusters = CountRange.Exactly(6),
                Portals = CountRange.Exactly(7),
                Probes =
                [
                    new VisProbe("low_side", new Point(-160f, 0f, 64f)),
                    new VisProbe("high_side", new Point(160f, 0f, 64f)),
                ],
                MustSee = [new ProbePair("low_side", "high_side")],
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();

                map.WorldSolids.Add(RoomKit.BoxWithFace(
                    new Bounds(new Point(-8f, StandardRoom.Mins.Y, StandardRoom.Mins.Z),
                               new Point(8f, StandardRoom.Maxs.Y, StandardRoom.Maxs.Z)),
                    RoomKit.Skip, "+x", RoomKit.Hint));

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_tool_textures",
            Level = MapLevel.L1,
            Features = [MapFeature.ToolTextures],
            Summary = "one brush of each tool texture: clip, playerclip, npcclip, blocklos, "
                      + "invisible, nodraw, trigger, ladder and origin",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 6),
                BrushEntities = CountRange.Exactly(3),
                PointEntities = CountRange.Exactly(2),
                Materials =
                [
                    RoomKit.Clip, RoomKit.PlayerClip, RoomKit.NpcClip, RoomKit.BlockLos,
                    RoomKit.Invisible, RoomKit.NoDraw, RoomKit.Trigger, RoomKit.Ladder,
                    RoomKit.Origin,
                ],
                Classnames = ["trigger_multiple", "func_ladder", "func_rotating"],
                Leaks = false,
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(20),
                Portals = CountRange.Exactly(52),
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();

                // Six world brushes in a row along -Y, 64 wide with a 16-unit
                // gap: separate brushes rather than a stack sharing faces, and
                // the row is 464 units long so it fits inside a 512-unit room
                // with 24 units to spare at each end.
                string[] worldMaterials =
                [
                    RoomKit.Clip, RoomKit.PlayerClip, RoomKit.NpcClip,
                    RoomKit.BlockLos, RoomKit.Invisible, RoomKit.NoDraw,
                ];

                for (int i = 0; i < worldMaterials.Length; i++)
                {
                    float x = -224f + (i * 80f);

                    RoomKit.Block(
                        map,
                        new Bounds(new Point(x, -224f, 0f), new Point(x + 64f, -160f, 64f)),
                        worldMaterials[i]);
                }

                // The three that are only meaningful on an entity. A trigger
                // texture on a world brush is nothing at all; origin is the
                // one that has to share an entity with real geometry, because
                // it names that entity's pivot.
                RoomKit.BrushEntity(
                    map, "trigger_multiple",
                    [VmfMap.Box(new Point(-192f, 96f, 0f), new Point(-64f, 224f, 128f),
                                RoomKit.Trigger)],
                    "spawnflags", "1", "wait", "1");

                RoomKit.BrushEntity(
                    map, "func_ladder",
                    [VmfMap.Box(new Point(-32f, 96f, 0f), new Point(32f, 128f, 192f),
                                RoomKit.Ladder)]);

                RoomKit.BrushEntity(
                    map, "func_rotating",
                    [
                        VmfMap.Box(new Point(96f, 96f, 32f), new Point(224f, 224f, 96f),
                                   RoomKit.BlockMaterial),
                        VmfMap.Box(new Point(152f, 152f, 56f), new Point(168f, 168f, 72f),
                                   RoomKit.Origin),
                    ],
                    "origin", "160 160 64",
                    "maxspeed", "100",
                    "spawnflags", "1");

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_areaportal",
            Level = MapLevel.L1,
            Features = [MapFeature.Areaportal],
            Summary = "two rooms with a func_areaportal filling the doorway exactly",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomChain.Straight(2).BrushCount),
                BrushEntities = CountRange.Exactly(1),
                PointEntities = CountRange.Exactly(3),
                Classnames = ["func_areaportal"],
                Materials = [RoomKit.AreaPortal],
                Leaks = false,

                // THE WHOLE OBSERVABLE. Three entries in LUMP_AREAS where
                // every other sealed entry here has two: area 0 is vbsp's
                // placeholder and the other two are the rooms either side of
                // the portal. Two would mean the areaportal did not seal, which
                // is the failure this entry is built to catch.
                //
                // And the cluster count is IDENTICAL to l1_two_rooms_and_a_door
                // — 12 and 14 — which is the other half of the claim: an
                // areaportal adds an area boundary and no visibility split.
                Areas = CountRange.Exactly(3),
                Clusters = CountRange.Exactly(12),
                Portals = CountRange.Exactly(14),
                Probes = RoomChain.Straight(2).Probes,
                MustSee = [new ProbePair("room0", "room1")],
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                var chain = RoomChain.Straight(2);
                chain.Build(map);

                Bounds doorway = chain.Doorway(0);

                RoomKit.BrushEntity(
                    map, "func_areaportal",
                    [VmfMap.Box(doorway.Mins, doorway.Maxs, RoomKit.AreaPortal)],
                    "targetname", "catalogue_areaportal",
                    "StartOpen", "1");

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l1_water_volume",
            Level = MapLevel.L1,
            Features = [MapFeature.Water],
            Summary = "a sealed room with 64 units of water in the bottom of it",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomKit.ShellBrushes + 1),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(2),
                Materials = [RoomKit.Water],
                Leaks = false,
                HasLeafWaterData = true,
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(8),
                Portals = CountRange.Exactly(12),
                Probes =
                [
                    new VisProbe("under", new Point(0f, 0f, 32f)),
                    new VisProbe("over", new Point(0f, 0f, 192f)),
                ],
            },
            Generator = _ =>
            {
                VmfMap map = SealedRoom();

                RoomKit.Pool(map, new Bounds(
                    StandardRoom.Mins,
                    new Point(StandardRoom.Maxs.X, StandardRoom.Maxs.Y, StandardRoom.Mins.Z + 64f)));

                return map;
            },
        };
    }

    // ------------------------------------------------------------------
    // L2 — features in deliberate combination.
    //
    // TWO ENTRIES, NOT THE PLAN'S SIX. §2a's L2 table is written against the
    // whole feature list and most of its pairs need displacements, props,
    // instances or HDR, none of which has an L1 entry yet. These two are the
    // pairs that can be built out of what is here -- and the matrix fact
    // reports L2 coverage separately rather than rounding it up.
    // ------------------------------------------------------------------

    private static IEnumerable<CatalogEntry> Interactions()
    {
        yield return new CatalogEntry
        {
            Name = "l2_detail_and_hint_in_a_corridor",
            Level = MapLevel.L2,
            Features = [MapFeature.DeepPortalChain, MapFeature.FuncDetail, MapFeature.HintSkip],
            Summary = "a four-room zigzag with a detail block in each room and a hint plane in one",
            Observables = new MapObservables
            {
                // Shell and dividers, plus one hint slab.
                WorldBrushes = CountRange.Exactly(RoomChain.Zigzag(4).BrushCount + 1),
                BrushEntities = CountRange.Exactly(4),
                PointEntities = CountRange.Exactly(5),
                Classnames = ["func_detail"],
                Materials = [RoomKit.Hint, RoomKit.Skip],
                Leaks = false,
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(14),
                Portals = CountRange.Exactly(14),
                Probes = RoomChain.Zigzag(4).Probes,
                MustSee = [new ProbePair("room0", "room1")],
                MustNotSee = [new ProbePair("room0", "room3")],
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                var chain = RoomChain.Zigzag(4);
                chain.Build(map);

                // The hint plane goes across room 1, where the detail block
                // also is: the interaction under test is whether a detail brush
                // standing on a hint's split plane changes what the hint does.
                Bounds room = chain.Room(1);

                map.WorldSolids.Add(RoomKit.BoxWithFace(
                    new Bounds(new Point(room.Centre.X - 8f, room.Mins.Y, room.Mins.Z),
                               new Point(room.Centre.X + 8f, room.Maxs.Y, room.Maxs.Z)),
                    RoomKit.Skip, "+x", RoomKit.Hint));

                for (int i = 0; i < chain.Rooms; i++)
                {
                    Point centre = chain.RoomCentre(i);

                    RoomKit.BrushEntity(
                        map, "func_detail",
                        [VmfMap.Box(
                            new Point(centre.X - 48f, centre.Y - 48f, 0f),
                            new Point(centre.X + 48f, centre.Y + 48f, 96f),
                            RoomKit.BlockMaterial)]);
                }

                return map;
            },
        };

        yield return new CatalogEntry
        {
            Name = "l2_areaportal_between_pools",
            Level = MapLevel.L2,
            Features = [MapFeature.PortalThroughDoorway, MapFeature.Areaportal, MapFeature.Water],
            Summary = "two flooded rooms with an areaportal sealing the dry doorway between them",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(RoomChain.Straight(2).BrushCount + 2),
                BrushEntities = CountRange.Exactly(1),
                PointEntities = CountRange.Exactly(3),
                Classnames = ["func_areaportal"],
                Materials = [RoomKit.AreaPortal, RoomKit.Water],
                Leaks = false,
                HasLeafWaterData = true,

                // The interaction: water on BOTH sides of an area boundary, so
                // the area split and the water split have to agree about the
                // leaves either side of the same doorway.
                Areas = CountRange.Exactly(3),
                Clusters = CountRange.Exactly(20),
                Portals = CountRange.Exactly(36),
                Probes = RoomChain.Straight(2).Probes,
                MustSee = [new ProbePair("room0", "room1")],
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                var chain = RoomChain.Straight(2);
                chain.Build(map);

                Bounds doorway = chain.Doorway(0);

                // One pool per room, and the doorway left DRY.
                //
                // Running the water through the doorway instead was the first
                // shape of this entry and it is wrong twice over: the water
                // brush would overlap the divider pieces, putting an
                // overlapping-CSG case into an entry that does not declare one,
                // and the areaportal brush would then intersect water, which is
                // a vbsp complaint of its own rather than the interaction under
                // test.
                for (int room = 0; room < chain.Rooms; room++)
                {
                    Bounds here = chain.Room(room);

                    RoomKit.Pool(map, new Bounds(
                        here.Mins,
                        new Point(here.Maxs.X, here.Maxs.Y, here.Mins.Z + 32f)));
                }

                RoomKit.BrushEntity(
                    map, "func_areaportal",
                    [VmfMap.Box(doorway.Mins, doorway.Maxs, RoomKit.AreaPortal)],
                    "targetname", "catalogue_areaportal",
                    "StartOpen", "1");

                return map;
            },
        };
    }

    // ------------------------------------------------------------------
    // L3 — the same generators with the knob turned up.
    // ------------------------------------------------------------------

    private static IEnumerable<CatalogEntry> Scale()
    {
        yield return Chain(
            name: "l3_corridor_64_rooms",
            feature: MapFeature.DeepPortalChain,
            summary: "sixty-four rooms in a zigzag: the same generator, the knob turned up",
            chain: RoomChain.Zigzag(64),
            clusters: 244, portals: 271,
            mustSee: [new ProbePair("room0", "room1")],
            mustNotSee: [new ProbePair("room0", "room63")],
            level: MapLevel.L3);

        yield return new CatalogEntry
        {
            Name = "l3_arena_144_pillars",
            Level = MapLevel.L3,
            Features = [MapFeature.WideOpenPvs],
            Summary = "a twelve-by-twelve pillar grid: vvis's quadratic case, at size",
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(new Arena { PillarsPerSide = 12 }.BrushCount),
                BrushEntities = CountRange.Exactly(0),
                PointEntities = CountRange.Exactly(2),
                Leaks = false,
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(352),
                Portals = CountRange.Exactly(777),
                Probes = new Arena { PillarsPerSide = 12 }.Probes,
                MustSee = [new ProbePair("ring_low", "ring_high")],
            },
            Generator = random =>
            {
                var map = new VmfMap();
                new Arena { PillarsPerSide = 12 }.Build(map, random);

                return map;
            },
        };
    }

    // ------------------------------------------------------------------
    // The shapes the entries above are built from.
    // ------------------------------------------------------------------

    /// <summary>
    /// The room every sealed L1 entry starts from: 512 x 512 x 256 of empty
    /// space, centred on the world origin.
    ///
    /// <para>
    /// On the origin because a map that only works away from it hides a whole
    /// class of sign error, and 512 on a side because that is big enough for a
    /// pillar and two probes and small enough that the whole thing is one
    /// screenful of brushes.
    /// </para>
    /// </summary>
    public static Bounds StandardRoom { get; } =
        new(new Point(-256f, -256f, 0f), new Point(256f, 256f, 256f));

    /// <summary>
    /// A sealed room with a player start and a light in it.
    ///
    /// <para>
    /// The player start is not decoration: vbsp decides what is inside the map
    /// by flooding out from entity origins, so a sealed room with nothing in it
    /// compiles to nothing at all.
    /// </para>
    /// </summary>
    public static VmfMap SealedRoom()
    {
        var map = new VmfMap();

        RoomKit.Worldspawn(map);
        RoomKit.Shell(map, StandardRoom);
        RoomKit.PlayerStart(map, StandardRoom.OnFloor(16f));
        RoomKit.Light(map, StandardRoom.OnFloor(StandardRoom.Size.Z - 32f));

        return map;
    }

    /// <summary>
    /// Declares an entry built on <see cref="RoomChain"/>.
    ///
    /// <para>
    /// Eight of the entries above are the same map with a different room count
    /// and different doorway offsets, so they are declared through one helper
    /// rather than copied: a copied declaration is where a brush count and a
    /// probe list drift apart.
    /// </para>
    /// </summary>
    private static CatalogEntry Chain(
        string name,
        MapFeature feature,
        string summary,
        RoomChain chain,
        int clusters,
        int portals,
        IReadOnlyList<ProbePair> mustSee,
        IReadOnlyList<ProbePair>? mustNotSee = null,
        CatalogCompileOptions? options = null,
        Action<VmfMap>? extra = null,
        int brushesAdded = 0,
        bool? hasLeafWaterData = null,
        MapLevel level = MapLevel.L1)
        => new()
        {
            Name = name,
            Level = level,
            Features = [feature],
            Summary = summary,
            Options = options ?? CatalogCompileOptions.Default,
            Observables = new MapObservables
            {
                WorldBrushes = CountRange.Exactly(chain.BrushCount + brushesAdded),
                BrushEntities = CountRange.Exactly(0),

                // One player start, plus one light per room.
                PointEntities = CountRange.Exactly(chain.Rooms + 1),
                Lights = CountRange.Exactly(chain.Rooms),
                Leaks = false,

                // Two, not one: area 0 is vbsp's placeholder. None of these
                // entries has an areaportal in it, so every one of them has
                // exactly one real area.
                Areas = CountRange.Exactly(2),
                Clusters = CountRange.Exactly(clusters),
                Portals = CountRange.Exactly(portals),
                HasLeafWaterData = hasLeafWaterData,
                Probes = chain.Probes,
                MustSee = mustSee,
                MustNotSee = mustNotSee ?? [],
            },
            Generator = _ =>
            {
                var map = new VmfMap();
                chain.Build(map);
                extra?.Invoke(map);

                return map;
            },
        };
}
