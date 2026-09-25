using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// <c>ProcessModels</c> and <c>EndBSPFile</c> end to end, over maps built in
/// code with in-memory content: no game content, no wine, no disk.
/// </summary>
/// <remarks>
/// Each fact pins one stock behaviour exactly. The catalogue gate against
/// stock's own output covers the same ground in bulk; these are the
/// behaviours behind it, one at a time.
/// </remarks>
public sealed class VbspCompileTests
{
    // A sealed 256-unit room: six 16-thick slabs around (0,0,0)-(256,256,256).
    private static VmfDocument Room(
        bool sealedRoom = true,
        bool brushEntity = false,
        bool namedLight = false,
        bool water = false,
        bool occluder = false)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        void Slab((float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));

        Slab((-16, -16, -16), (272, 272, 0));      // floor
        Slab((-16, -16, 256), (272, 272, 272));    // ceiling
        Slab((-16, -16, 0), (0, 272, 256));        // -x
        Slab((256, -16, 0), (272, 272, 256));      // +x
        Slab((0, -16, 0), (256, 0, 256));          // -y
        if (sealedRoom)
        {
            Slab((0, 256, 0), (256, 272, 256));    // +y
        }

        if (water)
        {
            world.Children.Add(UnitMap.Box(UnitMap.Water, (0, 0, 0), (256, 256, 64), id++));
        }

        document.Chunks.Add(world);

        Entity(document, "info_player_start", "128 128 128");

        if (brushEntity)
        {
            VmfChunk e = Entity(document, "func_brush", null);
            e.Children.Add(UnitMap.Box(UnitMap.Plain, (96, 96, 64), (160, 160, 128), id++));
        }

        if (occluder)
        {
            VmfChunk e = Entity(document, "func_occluder", null);
            e.Children.Add(UnitMap.Box(UnitMap.Plain, (32, 32, 32), (64, 224, 224), id++));
        }

        if (namedLight)
        {
            VmfChunk e = Entity(document, "light", "128 128 200");
            e.AddKey("targetname", "lamp");
            e.AddKey("style", "5");
        }

        return document;
    }

    private static VmfChunk Entity(VmfDocument document, string className, string? origin)
    {
        VmfChunk e = new(MapFileLoader.EntityChunk);
        e.AddKey("id", (document.Chunks.Count + 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        e.AddKey("classname", className);
        if (origin is not null)
        {
            e.AddKey("origin", origin);
        }

        document.Chunks.Add(e);
        return e;
    }

    private static async Task<VbspResult> CompileAsync(VmfDocument document, VbspOptions? options = null)
    {
        VbspContext context = await UnitMap.ContextAsync(options);
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context);
    }

    private static ReadOnlySpan<T> Lump<T>(VbspResult result, BspLump lump)
        where T : unmanaged => BspStructView.As<T>(result.Bsp![lump]);

    private static List<BspEntity> Entities(VbspResult result) =>
        EntityLump.Parse(result.Bsp![BspLump.Entities]);

    private static async Task<byte[]> BytesAsync(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    // ---- the shape of a compile --------------------------------------------

    [Fact]
    public async Task ASealedRoomYieldsABspAndAPortalFileAndNoLeak()
    {
        VbspResult result = await CompileAsync(Room());

        Assert.NotNull(result.Bsp);
        Assert.NotNull(result.Portals);
        Assert.Null(result.Leak);
    }

    [Fact]
    public async Task ASealedRoomPassesTheEngineLoaderRules()
    {
        VbspResult result = await CompileAsync(Room());

        ValidationReport report = await BspValidator.CheckAsync(result.Bsp!, CancellationToken.None);

        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public async Task ALeakedRoomStillYieldsABspButNoPortalFile()
    {
        //: WritePortalFile only if !leaked.
        VbspResult result = await CompileAsync(Room(sealedRoom: false));

        Assert.NotNull(result.Bsp);
        Assert.Null(result.Portals);
    }

    [Fact]
    public async Task ALeakedRoomReportsTheLeakFromThePlayerStart()
    {
        VbspResult result = await CompileAsync(Room(sealedRoom: false));

        Assert.Equal("info_player_start", result.Leak!.ClassName);
    }

    [Fact]
    public async Task LeakTestStopsAtALeakWithNothingWritten()
    {
        //: "--- MAP LEAKED ---", exit(0).
        VbspResult result = await CompileAsync(
            Room(sealedRoom: false), VbspOptions.Default with { LeakTest = true });

        Assert.Null(result.Bsp);
    }

    [Fact]
    public async Task LeakTestStillReportsTheLeakItStoppedAt()
    {
        // T4 contract: a preset that forces -leaktest must keep the leak
        // CHECK itself running and reported — the stop is the reference implementation's
        // exit(0) after the finding prints, not a silent abort. Pre-T4 the
        // stop (nothing written) and the un-flagged report were each pinned,
        // but no fact held flag + check + report together, which is the
        // behavior the AutoDetect-style forcing relies on.
        VbspResult result = await CompileAsync(
            Room(sealedRoom: false), VbspOptions.Default with { LeakTest = true });

        Assert.Null(result.Bsp);
        Assert.NotNull(result.Leak);
        Assert.Equal("info_player_start", result.Leak.ClassName);
        Assert.Contains(result.Diagnostics, d => d.Code == LeakTrace.MapLeaked);
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, Room());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Vbsp.CompileAsync(map, context, cancelled.Token));
    }

    [Fact]
    public async Task OnlyEntsIsRefusedByTheFullCompile()
    {
        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { OnlyEnts = true });
        MapFile map = await MapFileLoader.LoadAsync(context, Room());

        await Assert.ThrowsAsync<ArgumentException>(() => Vbsp.CompileAsync(map, context));
    }

    [Fact]
    public async Task TwoCompilesOfOneMapAreByteIdentical()
    {
        byte[] first = await BytesAsync((await CompileAsync(Room(brushEntity: true))).Bsp!);
        byte[] second = await BytesAsync((await CompileAsync(Room(brushEntity: true))).Bsp!);

        Assert.Equal(first, second);
    }

    // ---- BeginBSPFile's reserved entries -----------

    [Fact]
    public async Task LeafZeroIsTheSolidErrorLeaf()
    {
        VbspResult result = await CompileAsync(Room());

        DLeaf leaf = Lump<DLeaf>(result, BspLump.Leafs)[0];

        Assert.Equal((1, 0), (leaf.Contents, (int)leaf.Cluster));
    }

    [Fact]
    public async Task VertexZeroIsTheOriginPlaceholder()
    {
        VbspResult result = await CompileAsync(Room());

        Assert.Equal(Vec3.Zero, Lump<Vec3>(result, BspLump.Vertexes)[0]);
    }

    [Fact]
    public async Task EdgeZeroIsNeverWalkedByAFace()
    {
        // "edge 0 is not used, because 0 can't be negated"
        VbspResult result = await CompileAsync(Room());

        Assert.DoesNotContain(0, Lump<int>(result, BspLump.SurfEdges).ToArray());
    }

    [Fact]
    public async Task NoFaceUsesVertexZero()
    {
        VbspResult result = await CompileAsync(Room());
        ReadOnlySpan<DEdge> edges = Lump<DEdge>(result, BspLump.Edges);

        bool used = false;
        foreach (int e in Lump<int>(result, BspLump.SurfEdges))
        {
            DEdge edge = edges[Math.Abs(e)];
            used |= edge.V[0] == 0 || edge.V[1] == 0;
        }

        Assert.False(used);
    }

    [Fact]
    public async Task AreaPortalZeroIsTheErrorEntry()
    {
        // EmitAreaPortals: numareaportals = 1, "leave 0 as an error"
        VbspResult result = await CompileAsync(Room());

        Assert.Equal(1, Lump<DAreaPortal>(result, BspLump.AreaPortals).Length);
    }

    [Fact]
    public async Task ASealedRoomHasOneAreaPlusTheErrorArea()
    {
        VbspResult result = await CompileAsync(Room());

        Assert.Equal(2, Lump<DArea>(result, BspLump.Areas).Length);
    }

    // ---- models -----------------

    [Fact]
    public async Task ABrushEntityIsASecondModel()
    {
        VbspResult result = await CompileAsync(Room(brushEntity: true));

        Assert.Equal(2, Lump<DModel>(result, BspLump.Models).Length);
    }

    [Fact]
    public async Task ABrushEntityIsNumberedStarOne()
    {
        VbspResult result = await CompileAsync(Room(brushEntity: true));

        Assert.Equal("*1", Entities(result).Single(e => e.ClassName == "func_brush").Get("model"));
    }

    [Fact]
    public async Task ModelFaceRangesTileTheFaceLump()
    {
        VbspResult result = await CompileAsync(Room(brushEntity: true));
        ReadOnlySpan<DModel> models = Lump<DModel>(result, BspLump.Models);

        Assert.Equal(
            (models[0].NumFaces, models[1].FirstFace + models[1].NumFaces),
            (models[1].FirstFace, Lump<DFace>(result, BspLump.Faces).Length));
    }

    [Fact]
    public async Task ASubmodelsLeavesHaveNoCluster()
    {
        //
        VbspResult result = await CompileAsync(Room(brushEntity: true));
        ReadOnlySpan<DLeaf> leafs = Lump<DLeaf>(result, BspLump.Leafs);
        int worldLeaves = Lump<DModel>(result, BspLump.Models)[1].HeadNode;

        // Everything after the world's leaves belongs to model 1.
        int firstSubmodelLeaf = CountWorldLeaves(result);
        bool allMinusOne = true;
        for (int i = firstSubmodelLeaf; i < leafs.Length; i++)
        {
            allMinusOne &= leafs[i].Cluster == -1;
        }

        Assert.True(allMinusOne && worldLeaves >= 0);
    }

    [Fact]
    public async Task ASubmodelsVerticesAreNumberedAfterTheWorlds()
    {
        // FixTjuncs clears the weld hash, not the vertex table.
        VbspResult result = await CompileAsync(Room(brushEntity: true));
        ReadOnlySpan<DModel> models = Lump<DModel>(result, BspLump.Models);

        int worldMax = MaxVertexOfFaces(result, models[0].FirstFace, models[0].NumFaces);
        int subMin = MinVertexOfFaces(result, models[1].FirstFace, models[1].NumFaces);

        Assert.True(subMin > worldMax, $"submodel vertex {subMin} <= world vertex {worldMax}");
    }

    // ---- entities(1535;) ---------------.

    [Fact]
    public async Task WorldspawnCarriesItsDrawnBounds()
    {
        // ComputeBoundsNoSkybox: the inner faces of the room, 0..256.
        VbspResult result = await CompileAsync(Room());
        BspEntity world = Entities(result)[0];

        Assert.Equal(("0 0 0", "256 256 256"), (world.Get("world_mins"), world.Get("world_maxs")));
    }

    [Fact]
    public async Task ANamedLightGetsSwitchableStyleThirtyTwo()
    {
        VbspResult result = await CompileAsync(Room(namedLight: true));

        Assert.Equal("32", Entities(result).Single(e => e.ClassName == "light").Get("style"));
    }

    [Fact]
    public async Task ANamedLightKeepsItsOldStyleAsTheDefault()
    {
        VbspResult result = await CompileAsync(Room(namedLight: true));

        Assert.Equal("5", Entities(result).Single(e => e.ClassName == "light").Get("defaultstyle"));
    }

    [Fact]
    public async Task WaterWithNoLodControlGetsADefaultOne()
    {
        VbspResult result = await CompileAsync(Room(water: true));

        Assert.Equal(
            "2000",
            Entities(result).Single(e => e.ClassName == "water_lod_control").Get("cheapwaterenddistance"));
    }

    [Fact]
    public async Task TheEntityLumpEndsInOneNul()
    {
        VbspResult result = await CompileAsync(Room());
        ReadOnlySpan<byte> text = result.Bsp![BspLump.Entities].Data.Span;

        Assert.Equal((0, (byte)'\n'), (text[^1], text[^2]));
    }

    // ---- brushes and planes(1048) -----------------------.

    [Fact]
    public async Task EveryMapBrushIsEmitted()
    {
        VbspResult result = await CompileAsync(Room(brushEntity: true));

        Assert.Equal(7, Lump<DBrush>(result, BspLump.Brushes).Length);
    }

    [Fact]
    public async Task AnAxialBoxNeedsNoExtraBevelSides()
    {
        // every axial plane of a box is already one of its six sides
        VbspResult result = await CompileAsync(Room());

        Assert.All(Lump<DBrush>(result, BspLump.Brushes).ToArray(), b => Assert.Equal(6, b.NumSides));
    }

    [Fact]
    public async Task VertNormalIndicesAreOneShortPerDrawFaceEdge()
    {
        // g_vertnormalindices is unsigned short, one per edge
        // of every LUMP_FACES face -- not per surfedge, which
        // also counts the original faces'.
        VbspResult result = await CompileAsync(Room());
        int edges = 0;
        foreach (DFace f in Lump<DFace>(result, BspLump.Faces))
        {
            edges += f.NumEdges;
        }

        Assert.Equal(edges * sizeof(ushort), result.Bsp![BspLump.VertNormalIndices].Length);
    }

    [Fact]
    public async Task LeafMinDistToWaterIsOneZeroPerLeaf()
    {
        VbspResult result = await CompileAsync(Room());

        Assert.Equal(
            Lump<DLeaf>(result, BspLump.Leafs).Length * 2,
            result.Bsp![BspLump.LeafMinDistToWater].Length);
    }

    // ---- occluders -------------------------------------------

    [Fact]
    public async Task AnOccluderIsOneOccluderAndNoModel()
    {
        VbspResult result = await CompileAsync(Room(occluder: true));
        OcclusionLump occlusion = OcclusionLump.Read(result.Bsp![BspLump.Occlusion]);

        Assert.Equal(
            (1, 1),
            (occlusion.Occluders.Count, Lump<DModel>(result, BspLump.Models).Length));
    }

    [Fact]
    public async Task AnOccluderIsNumberedAndHasNoModelKey()
    {
        VbspResult result = await CompileAsync(Room(occluder: true));
        BspEntity occluder = Entities(result).Single(e => e.ClassName == "func_occluder");

        Assert.Equal(("0", string.Empty), (occluder.Get("occludernumber"), occluder.Get("model")));
    }

    [Fact]
    public async Task AnOccluderLiesInTheRoomsArea()
    {
        VbspResult result = await CompileAsync(Room(occluder: true));

        Assert.Equal(1, OcclusionLump.Read(result.Bsp![BspLump.Occlusion]).Occluders[0].Area);
    }

    // ---- compliance -------------------------------------------------------

    [Fact]
    public async Task UnderStockEveryNodeAreaIsZero()
    {
        VbspResult result = await CompileAsync(
            Room(), VbspOptions.Default with { Compliance = ComplianceOptions.Stock });

        Assert.All(Lump<DNode>(result, BspLump.Nodes).ToArray(), n => Assert.Equal(0, n.Area));
    }

    [Fact]
    public async Task UnderCorrectTheRootNodeCarriesTheOneArea()
    {
        VbspResult result = await CompileAsync(Room());
        DNode root = Lump<DNode>(result, BspLump.Nodes)[Lump<DModel>(result, BspLump.Models)[0].HeadNode];

        // A single-area map: every node's children agree, so the root is area 1
        // -- except that the outside solid leaves are area 0, so it is -1 or 1.
        Assert.NotEqual(0, root.Area);
    }

    // ---- -onlyents ------------------------------------------

    [Fact]
    public async Task OnlyEntsReplacesTheEntityLump()
    {
        VbspResult full = await CompileAsync(Room());

        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { OnlyEnts = true });
        MapFile map = await MapFileLoader.LoadAsync(context, Room(namedLight: true));
        BspData updated = await Vbsp.UpdateAsync(full.Bsp!, map, context);

        Assert.Contains(EntityLump.Parse(updated[BspLump.Entities]), e => e.ClassName == "light");
    }

    [Fact]
    public async Task OnlyEntsMarksTheLightingStale()
    {
        VbspResult full = await CompileAsync(Room());

        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { OnlyEnts = true });
        MapFile map = await MapFileLoader.LoadAsync(context, Room());
        BspData updated = await Vbsp.UpdateAsync(full.Bsp!, map, context);

        byte[] pak = updated[BspLump.PakFile].Data.ToArray();
        Assert.Contains("stale.txt", Encoding.Latin1.GetString(pak), StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------

    private static int CountWorldLeaves(VbspResult result)
    {
        ReadOnlySpan<DNode> nodes = Lump<DNode>(result, BspLump.Nodes);
        int max = 0;
        Stack<int> pending = new();
        pending.Push(Lump<DModel>(result, BspLump.Models)[0].HeadNode);
        while (pending.Count > 0)
        {
            int n = pending.Pop();
            if (n < 0)
            {
                max = Math.Max(max, -1 - n);
                continue;
            }

            pending.Push(nodes[n].Children[0]);
            pending.Push(nodes[n].Children[1]);
        }

        return max + 1;
    }

    private static int MaxVertexOfFaces(VbspResult result, int first, int count) =>
        VerticesOfFaces(result, first, count).Max();

    private static int MinVertexOfFaces(VbspResult result, int first, int count) =>
        VerticesOfFaces(result, first, count).Min();

    private static List<int> VerticesOfFaces(VbspResult result, int first, int count)
    {
        ReadOnlySpan<DFace> faces = Lump<DFace>(result, BspLump.Faces);
        ReadOnlySpan<int> surfEdges = Lump<int>(result, BspLump.SurfEdges);
        ReadOnlySpan<DEdge> edges = Lump<DEdge>(result, BspLump.Edges);

        List<int> vertices = [];
        for (int f = first; f < first + count; f++)
        {
            for (int i = 0; i < faces[f].NumEdges; i++)
            {
                int e = surfEdges[faces[f].FirstEdge + i];
                DEdge edge = edges[Math.Abs(e)];
                vertices.Add(e >= 0 ? edge.V[0] : edge.V[1]);
            }
        }

        return vertices;
    }
}
