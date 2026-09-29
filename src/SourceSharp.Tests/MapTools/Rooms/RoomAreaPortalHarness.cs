//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with area portals: a room split in two by an inner wall whose
/// doorway a <c>func_areaportal</c> fills, the harness hub beside it, and
/// the room compile, link and flattened compile the area portal facts run,
/// with the two readings the facts compare maps by: the partition of the
/// level's open space into areas, and the portals between them.
/// </summary>
/// <remarks>
/// The split room (<see cref="Split"/>) has sockets east and west only, so
/// its two halves meet nowhere but through the portal: its west half opens
/// onto whatever it joins west, its east half east. Room 0 of the library
/// is the hub (four sockets), room 1 the split room.
/// </remarks>
internal static class RoomAreaPortalHarness
{
    /// <summary>The area portal's material: never drawn.</summary>
    public const string PortalMaterial = "unit/areaportal";

    /// <summary>The split room's area portal entity's Hammer id.</summary>
    public const int PortalId = 700100;

    /// <summary>The inner wall's west face: the wall stands from here to <see cref="WallEast"/> across the room.</summary>
    public const float WallWest = 120;

    /// <summary>The inner wall's east face.</summary>
    public const float WallEast = 136;

    /// <summary>The inner doorway, which the portal fills: across the wall, 48 wide and 96 high.</summary>
    public static Box Doorway { get; } = new(new Vec3(WallWest, 104, 16), new Vec3(WallEast, 152, 112));

    /// <summary>The split room: walkable, with sockets east and west.</summary>
    public static RoomDefinition Split => RoomHarness.WalkableRoom("split", RoomFacing.PositiveX, RoomFacing.NegativeX);

    /// <summary>The library's corner of room <paramref name="room"/> (0 hub, 1 split).</summary>
    public static Vec3 Corner(int room) => new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);

    /// <summary>The portal material's file.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [$"materials/{PortalMaterial}.vmt"] = Encoding.ASCII.GetBytes("\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compilenodraw\" \"1\"\n}\n"),
        [$"materials/{LevelDoorPortals.Material}.vmt"] = Encoding.ASCII.GetBytes("\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compilenodraw\" \"1\"\n}\n"),
    };

    /// <summary>A library that asks for door portals: its worldspawn's <c>rooms_door_portals</c> set to 1.</summary>
    public static VmfDocument WithDoorPortals(VmfDocument library)
    {
        library.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.DoorPortalsKey, "1");
        return library;
    }

    /// <summary>A map's area portal entities, each as its pairs but <c>hammerid</c>, in lump order.</summary>
    public static List<string> PortalEntities(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => MapFileLoader.IsAreaPortal(e.ClassName ?? string.Empty))
            .Select(e => string.Join(" | ", e.Pairs.Where(p => p.Key != "hammerid").Select(p => $"{p.Key}={p.Value}")))];

    /// <summary>A compile context whose content holds the harness materials and the portal's.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1)
    {
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: Files());
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        return context;
    }

    /// <summary>
    /// An area portal entity (<paramref name="classname"/>) filling a
    /// room-local box, with its keys.
    /// </summary>
    public static VmfChunk Portal(int id, Box box, string classname = "func_areaportal", params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        entity.AddKey("StartOpen", "1");
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        entity.Children.Add(RoomModel.Slab(PortalMaterial, box.Mins, box.Maxs, id));
        return entity;
    }

    /// <summary>
    /// The harness library of the hub and the split room: the split room's
    /// inner wall around its doorway, and, when <paramref name="portal"/>,
    /// the <c>func_areaportal</c> filling it; each extra entity added to room
    /// 0 (hub) or 1 (split) at its room-local position (its solids moved too).
    /// </summary>
    public static VmfDocument Library(bool portal = true, params (int Room, VmfChunk Entity)[] extra)
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomPropHarness.Hub, Split);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        QuarterTurn move = QuarterTurn.Translation(Corner(1));
        Box d = Doorway;
        Box[] wall =
        [
            new(new Vec3(WallWest, 16, 16), new Vec3(WallEast, d.Mins.Y, 240)),
            new(new Vec3(WallWest, d.Maxs.Y, 16), new Vec3(WallEast, 240, 240)),
            new(new Vec3(WallWest, d.Mins.Y, d.Maxs.Z), new Vec3(WallEast, d.Maxs.Y, 240)),
        ];
        for (int i = 0; i < wall.Length; i++)
        {
            world.Children.Add(VmfPlacement.MoveSolid(RoomModel.Slab(RoomHarness.Plain, wall[i].Mins, wall[i].Maxs, 7001 + i), move));
        }

        if (portal)
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(Portal(PortalId, Doorway), move));
        }

        foreach ((int room, VmfChunk entity) in extra)
        {
            library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(Corner(room))));
        }

        return library;
    }

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them, each on <paramref name="degree"/> threads.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name, degree)));
        }

        return compiled;
    }

    /// <summary>A level linked from compiled rooms, with a context that has no game files.</summary>
    public static Task<LinkedLevel> LinkAsync(RoomLibrary library, LevelGrid level, int degree = 1) =>
        RoomPropHarness.LinkAsync(library, level, degree);

    /// <summary>The level flattened and compiled whole.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("flat"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's area lump.</summary>
    public static DArea[] Areas(BspData bsp) => BspStructView.As<DArea>(bsp[BspLump.Areas]).ToArray();

    /// <summary>A map's area portal listings.</summary>
    public static DAreaPortal[] Listings(BspData bsp) => BspStructView.As<DAreaPortal>(bsp[BspLump.AreaPortals]).ToArray();

    /// <summary>
    /// Sample points of a level's cells: a lattice 8 units apart, 4 off
    /// every multiple of 8 (so no point lies on a brush face of the
    /// harness's whole-unit geometry), at two heights, one under the inner
    /// doorway's lintel and one above it.
    /// </summary>
    public static IEnumerable<Vec3> Samples(LevelGrid level)
    {
        for (int row = 0; row < level.Rows; row++)
        {
            for (int column = 0; column < level.Columns; column++)
            {
                for (int i = 0; i < 32; i++)
                {
                    for (int j = 0; j < 32; j++)
                    {
                        foreach (float z in new[] { 60f, 180f })
                        {
                            yield return new Vec3((column * RoomHarness.Cell) + 4 + (8 * i), (row * RoomHarness.Cell) + 4 + (8 * j), z);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Whether a leaf is open space an area holds: neither solid nor an area portal's own brush.</summary>
    public static bool IsOpen(DLeaf leaf) =>
        (leaf.Contents & (int)(BrushContents.Solid | BrushContents.AreaPortal)) == 0;

    /// <summary>
    /// The partition two maps make of the level's open space: every sample
    /// point open in one is open in the other, and the linked map's area of
    /// each open point names the flattened map's area of it one to one.
    /// </summary>
    /// <returns>The linked map's areas to the flattened map's.</returns>
    public static Dictionary<int, int> SamePartition(BspData linked, BspData flat, LevelGrid level)
    {
        Dictionary<int, int> linkedToFlat = [];
        Dictionary<int, int> flatToLinked = [];
        foreach (Vec3 point in Samples(level))
        {
            DLeaf a = RoomHarness.LeafAt(linked, point);
            DLeaf b = RoomHarness.LeafAt(flat, point);
            bool solidA = (a.Contents & (int)BrushContents.Solid) != 0;
            bool solidB = (b.Contents & (int)BrushContents.Solid) != 0;
            Assert.True(solidA == solidB, $"at {point} the linked leaf is {(solidA ? "solid" : "open")}, the flattened one {(solidB ? "solid" : "open")}");
            if (!IsOpen(a) || !IsOpen(b))
            {
                continue;
            }

            int la = a.GetArea();
            int fb = b.GetArea();
            Assert.True(la > 0, $"at {point} the linked leaf is in area 0");
            if (linkedToFlat.TryGetValue(la, out int seen))
            {
                Assert.True(seen == fb, $"at {point} linked area {la} is flattened area {fb}, elsewhere {seen}");
            }

            if (flatToLinked.TryGetValue(fb, out int back))
            {
                Assert.True(back == la, $"at {point} flattened area {fb} is linked area {la}, elsewhere {back}");
            }

            linkedToFlat[la] = fb;
            flatToLinked[fb] = la;
        }

        return linkedToFlat;
    }

    /// <summary>
    /// A map's portals as the engine reads them: per listing, the area it
    /// is listed from and the other (through <paramref name="areaNames"/>,
    /// which renames a map's areas; identity when null), the key, the
    /// plane's normal and the outline's vertices, sorted, each to a
    /// hundredth; with <paramref name="depth"/>, the plane's distance and
    /// every vertex's coordinate along the normal too.
    /// </summary>
    /// <remarks>
    /// Without the depth, a portal is read as its outline seen along its
    /// normal: which face of the portal's brush vbsp puts it on follows the
    /// order its area flood met the portal's two sides in, which a room's
    /// compile and the flattened level's need not share (the rooms design,
    /// 4.11, the PR 13 note); <see cref="OnPortalFace"/> holds the depth to
    /// one of the two faces.
    /// </remarks>
    public static List<string> Portals(BspData bsp, IReadOnlyDictionary<int, int>? areaNames = null, bool depth = false)
    {
        DArea[] areas = Areas(bsp);
        DAreaPortal[] listings = Listings(bsp);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        Vec3[] verts = BspStructView.As<Vec3>(bsp[BspLump.ClipPortalVerts]).ToArray();
        int Name(int area) => areaNames is null ? area : areaNames[area];
        List<string> portals = [];
        for (int a = 1; a < areas.Length; a++)
        {
            for (int l = areas[a].FirstAreaPortal; l < areas[a].FirstAreaPortal + areas[a].NumAreaPortals; l++)
            {
                DAreaPortal p = listings[l];
                DPlane plane = planes[p.PlaneNum];
                int axis = Math.Abs(plane.Normal.X) > 0.5f ? 0 : Math.Abs(plane.Normal.Y) > 0.5f ? 1 : 2;
                string C(Vec3 v, int i) => !depth && i == axis ? "*" : F(i == 0 ? v.X : i == 1 ? v.Y : v.Z);
                IEnumerable<string> outline = verts.Skip(p.FirstClipPortalVert).Take(p.ClipPortalVerts)
                    .Select(v => $"({C(v, 0)} {C(v, 1)} {C(v, 2)})").Distinct().Order(StringComparer.Ordinal);
                portals.Add($"{Name(a)} -> {Name(p.OtherArea)} key {p.PortalKey} plane ({F(plane.Normal.X)} {F(plane.Normal.Y)} {F(plane.Normal.Z)})"
                    + (depth ? $" {F(plane.Dist)}" : string.Empty) + $" {string.Join(' ', outline)}");
            }
        }

        return [.. portals.Order(StringComparer.Ordinal)];

        // To a hundredth, a negative zero written as zero: a turned normal's
        // zero components carry the sign the turn gave them.
        static string F(float f) => ((Math.Round(f * 100) / 100) + 0.0).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Holds every listing of a map to lying on a face of a portal's brush:
    /// its plane's distance one of the box's two bounds along the normal,
    /// and its outline in that plane.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="brushes">The world boxes of the level's area portal brushes.</param>
    public static void OnPortalFace(BspData bsp, IReadOnlyList<Box> brushes)
    {
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        Vec3[] verts = BspStructView.As<Vec3>(bsp[BspLump.ClipPortalVerts]).ToArray();
        foreach (DAreaPortal p in Listings(bsp).Skip(1))
        {
            DPlane plane = planes[p.PlaneNum];
            int axis = Math.Abs(plane.Normal.X) > 0.5f ? 0 : Math.Abs(plane.Normal.Y) > 0.5f ? 1 : 2;
            static float At(Vec3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
            bool found = brushes.Any(box =>
                (Math.Abs(Math.Abs(plane.Dist) - At(box.Mins, axis)) < 0.01f || Math.Abs(Math.Abs(plane.Dist) - At(box.Maxs, axis)) < 0.01f)
                && verts.Skip(p.FirstClipPortalVert).Take(p.ClipPortalVerts).All(v => Math.Abs(At(v, axis) - Math.Abs(plane.Dist)) < 0.01f));
            Assert.True(found, $"portal {p.PortalKey}'s plane at {plane.Dist} along axis {axis} is on no face of a portal brush");
        }
    }

    /// <summary>A room-local box through a placement: the world box it covers.</summary>
    public static Box Placed(Box local, RoomPlacement placement)
    {
        RoomTransform transform = new(placement, RoomHarness.Cell);
        Vec3 a = transform.Apply(local.Mins);
        Vec3 b = transform.Apply(local.Maxs);
        return new Box(
            new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    /// <summary>A map's area portal entities' numbers, in lump order.</summary>
    public static List<int> PortalNumbers(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => MapFileLoader.IsAreaPortal(e.ClassName ?? string.Empty))
            .Select(e => int.Parse(e.Get(RoomAreaPortals.PortalNumberKey)!, CultureInfo.InvariantCulture))];

    /// <summary>
    /// Whether every node that names one area holds only leaves of that
    /// area (a node's area is the one its children agree on, or -1).
    /// </summary>
    public static void NodeAreasHold(BspData bsp)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        for (int n = 0; n < nodes.Length; n++)
        {
            if (nodes[n].Area <= 0)
            {
                continue;
            }

            Stack<int> pending = new([n]);
            while (pending.TryPop(out int at))
            {
                if (at < 0)
                {
                    DLeaf leaf = leafs[-(at + 1)];
                    Assert.True(
                        (leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.GetArea() == nodes[n].Area,
                        $"node {n} is in area {nodes[n].Area} but holds a leaf of area {leaf.GetArea()}");
                    continue;
                }

                pending.Push(nodes[at].Children[0]);
                pending.Push(nodes[at].Children[1]);
            }
        }
    }

    /// <summary>The bytes <c>ssmap link</c> writes for a level.</summary>
    public static Task<byte[]> BytesAsync(LinkedLevel linked) => RoomOverlayHarness.BytesAsync(linked);
}
