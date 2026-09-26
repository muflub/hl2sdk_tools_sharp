//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// <see cref="MapCompiler.CompileAsync"/>: the three stages chained in one
/// process, on maps built in code with in-memory content -- no disk, no wine.
/// </summary>
public sealed class MapCompilerTests
{
    private const string MapDirectory = "maps";

    // A sealed room of six 16-thick slabs around (0,0,0)-(256,256,256), a
    // player start and a light inside; without the +y slab it leaks.
    private static VmfDocument Room(bool sealedRoom = true)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        int id = 10;
        void Slab((float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));

        Slab((-16, -16, -16), (272, 272, 0));
        Slab((-16, -16, 256), (272, 272, 272));
        Slab((-16, -16, 0), (0, 272, 256));
        Slab((256, -16, 0), (272, 272, 256));
        Slab((0, -16, 0), (256, 0, 256));
        if (sealedRoom)
        {
            Slab((0, 256, 0), (256, 272, 256));
        }

        // A pillar, so vvis has more than one cluster to see between.
        Slab((96, 96, 0), (160, 160, 256));

        document.Chunks.Add(world);
        Entity(document, "info_player_start", "32 32 64");
        VmfChunk light = Entity(document, "light", "200 200 200");
        light.AddKey("_light", "255 255 255 200");
        return document;
    }

    private static VmfChunk Entity(VmfDocument document, string className, string origin)
    {
        VmfChunk e = new(MapFileLoader.EntityChunk);
        e.AddKey("id", (document.Chunks.Count + 100).ToString(CultureInfo.InvariantCulture));
        e.AddKey("classname", className);
        e.AddKey("origin", origin);
        document.Chunks.Add(e);
        return e;
    }

    // The unit materials plus the map at maps/<name>.vmf, on one in-memory disk.
    private static async Task<(InMemoryFileSystem Files, IContentFileSystem Content)> DiskAsync(
        VmfDocument document, string name = "room")
    {
        InMemoryFileSystem files = new();
        files.AddText(
            $"materials/{UnitMap.Plain}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText($"{MapDirectory}/{name}.vmf", Encoding.UTF8.GetString(document.ToBytes()));
        IContentFileSystem content = new ContentFileSystem([await DirectoryContentMount.MountAsync(files, VPath.Empty)]);
        return (files, content);
    }

    private static CompileRequest Request(
        InMemoryFileSystem files,
        IContentFileSystem content,
        CompileOutput? output = null,
        int degree = 2,
        string name = "room") => new()
        {
            Source = MapSource.FromVmf(files, VPath.Create($"{MapDirectory}/{name}.vmf")),
            Content = content,
            Vrad = VradOptions.Default with { Bounces = 0 },
            Parallel = new CompileParallelism { MaxDegree = degree },
            Output = output ?? CompileOutput.ToDirectory(files, VPath.Create(MapDirectory)),
        };

    private static async Task<byte[]> BytesAsync(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task<byte[]> ReadAsync(InMemoryFileSystem files, string path)
    {
        using System.Buffers.IMemoryOwner<byte> owner = await files.ReadAllAsync(VPath.Create(path));
        return owner.Memory.ToArray();
    }

    private static async Task<List<string>> ListAsync(InMemoryFileSystem files)
    {
        List<string> all = [];
        await foreach (VPath path in files.EnumerateAsync(VPath.Create(MapDirectory), "*"))
        {
            all.Add(path.FileName);
        }

        all.Sort(StringComparer.Ordinal);
        return all;
    }

    [Fact]
    public async Task ASealedMapGoesThroughAllThreeStages()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.NotNull(result.Vis);
        Assert.NotNull(result.Rad);
    }

    [Fact]
    public async Task TheChainLeavesALightingLump()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Equal(result.Rad!.Passes[0].LightDataSize, result.Bsp![BspLump.Lighting].Length);
    }

    [Fact]
    public async Task ToDirectoryWritesTheBspPortalFileAndLog()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        _ = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Equal(["room.bsp", "room.log", "room.prt", "room.vmf"], await ListAsync(files));
    }

    [Fact]
    public async Task TheWrittenBspIsTheResultsBsp()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Equal(await BytesAsync(result.Bsp!), await ReadAsync(files, "maps/room.bsp"));
    }

    [Fact]
    public async Task InMemoryWritesNothing()
    {
        // A compile can run with zero disk writes.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content, CompileOutput.InMemory), null);

        Assert.Equal(["room.vmf"], await ListAsync(files));
        Assert.Empty(result.Written);
    }

    [Fact]
    public async Task InMemoryAndToDirectoryMakeTheSameMap()
    {
        (InMemoryFileSystem a, IContentFileSystem ca) = await DiskAsync(Room());
        (InMemoryFileSystem b, IContentFileSystem cb) = await DiskAsync(Room());
        CompileResult memory = await MapCompiler.CompileAsync(Request(a, ca, CompileOutput.InMemory), null);
        _ = await MapCompiler.CompileAsync(Request(b, cb), null);

        Assert.Equal(await ReadAsync(b, "maps/room.bsp"), await BytesAsync(memory.Bsp!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheChainEqualsTheStagesRunThroughFiles(bool slanted)
    {
        // The chain claims the BSP needs no file round trip between stages
        // and hands vvis the.prt's text. Run the stages the way three
        // processes would -- save, load, parse -- and require the same bytes;
        // the slanted map's portal text is not its floats (next fact).
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(slanted ? SlantedRoom() : Room());
        CompileResult chain = await MapCompiler.CompileAsync(Request(files, content, CompileOutput.InMemory), null);

        VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);

        BspData bsp;
        using (MemoryStream stream = new(await BytesAsync(vbsp.Bsp!)))
        {
            bsp = await BspFile.LoadAsync(stream);
        }

        PortalFile prt = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
        CompileParallelism degree = new() { MaxDegree = 2 };
        _ = await Vvis.ComputeAsync(bsp, PortalSet.FromPortalFile(prt), new VisContext { Parallelism = degree });

        using (MemoryStream stream = new(await BytesAsync(bsp)))
        {
            bsp = await BspFile.LoadAsync(stream);
        }

        _ = await Vrad.LightAsync(bsp, new VradContext
        {
            Options = VradOptions.Default with { Bounces = 0 },
            MapName = "room",
            Content = content,
            Parallelism = degree,
        });

        Assert.Equal(await BytesAsync(bsp), await BytesAsync(chain.Bsp!));
    }

    // The sealed room with a slanted wedge on the floor: its sloped face
    // meets the axial split planes at fractional coordinates, so the.prt's
    // six-decimal text is not the floats vbsp held.
    private static VmfDocument SlantedRoom()
    {
        VmfDocument document = Room();
        VmfChunk world = document.Chunks[0];
        VmfChunk solid = new(MapFileLoader.SolidChunk);
        solid.AddKey("id", "90");
        // A prism under the plane through (16,0,0), (240,0,77), (16,256,0)...
        UnitMap.Add(solid, UnitMap.Plain, (16, 0, 0), (16, 256, 0), (240, 256, 77));   // slope (normal up/-x)
        UnitMap.Add(solid, UnitMap.Plain, (16, 0, 0), (240, 0, 0), (240, 256, 0));     // bottom
        UnitMap.Add(solid, UnitMap.Plain, (240, 256, 0), (240, 0, 0), (240, 0, 77));   // +x
        UnitMap.Add(solid, UnitMap.Plain, (16, 0, 0), (16, 0, 77), (240, 0, 77));      // -y
        UnitMap.Add(solid, UnitMap.Plain, (16, 256, 0), (240, 256, 0), (240, 256, 77)); // +y
        world.Children.Add(solid);
        return document;
    }

    [Fact]
    public async Task TheSlantedFixturesPortalTextIsNotItsFloats()
    {
        // The premise of the slanted case above: this fixture reaches portal
        // coordinates.prt's %f text rounds. NOTE:
        // measured by mutation (p7 m0), handing vvis the floats instead
        // changes no output byte here -- the text round trip is kept because
        // it is stock's contract between the tools, not because it is observed.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(SlantedRoom());
        VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);
        PortalFile text = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));

        bool anyDiffers = false;
        for (int i = 0; i < text.Portals.Count; i++)
        {
            anyDiffers |= !text.Portals[i].Points.SequenceEqual(vbsp.Portals.Portals[i].Points);
        }

        Assert.True(anyDiffers);
    }

    [Fact]
    public async Task OneThreadAndFourMakeTheSameBytes()
    {
        (InMemoryFileSystem a, IContentFileSystem ca) = await DiskAsync(Room());
        (InMemoryFileSystem b, IContentFileSystem cb) = await DiskAsync(Room());
        CompileResult one = await MapCompiler.CompileAsync(Request(a, ca, CompileOutput.InMemory, degree: 1), null);
        CompileResult four = await MapCompiler.CompileAsync(Request(b, cb, CompileOutput.InMemory, degree: 4), null);

        Assert.Equal(await BytesAsync(one.Bsp!), await BytesAsync(four.Bsp!));
    }

    [Fact]
    public async Task ALeakedMapWritesALinAndNoPortalFile()
    {
        //: a.prt only for a sealed world; LeakFile writes.lin.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        _ = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Equal(["room.bsp", "room.lin", "room.log", "room.vmf"], await ListAsync(files));
    }

    [Fact]
    public async Task ALeakedMapSkipsVvisWithAWarning()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Null(result.Vis);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == MapCompilerCodes.VisSkipped && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ALeakedMapIsStillLit()
    {
        // A Hammer chain runs vrad after vvis fails to open the.prt.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.NotNull(result.Rad);
        Assert.NotEqual(0, result.Bsp![BspLump.Lighting].Length);
    }

    [Fact]
    public async Task ALeakedMapCarriesItsLeakReport()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.NotNull(result.Leak);
    }

    [Fact]
    public async Task LeakTestStopsTheChainAtALeak()
    {
        //: -leaktest exits at the leak, writing no.bsp.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        CompileRequest request = Request(files, content) with { Vbsp = VbspOptions.Default with { LeakTest = true } };
        CompileResult result = await MapCompiler.CompileAsync(request, null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Rad);
        Assert.DoesNotContain("room.bsp", await ListAsync(files));
    }

    [Fact]
    public async Task AStalePortalFileIsDeletedWhenTheMapNowLeaks()
    {
        // removes <map>.prt and <map>.lin before compiling.
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room(sealedRoom: false));
        files.AddText("maps/room.prt", "PRT1\n0\n0\n");
        _ = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.DoesNotContain("room.prt", await ListAsync(files));
    }

    [Fact]
    public async Task AStaleLeakFileIsDeletedWhenTheMapIsNowSealed()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        files.AddText("maps/room.lin", "0 0 0\n");
        _ = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.DoesNotContain("room.lin", await ListAsync(files));
    }

    [Fact]
    public async Task TheLogIsAppendedToNotReplaced()
    {
        // opens the log with "a".
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        files.AddText("maps/room.log", "an earlier compile\n");
        _ = await MapCompiler.CompileAsync(Request(files, content), null);

        string log = Encoding.UTF8.GetString(await ReadAsync(files, "maps/room.log"));
        Assert.StartsWith("an earlier compile\nssmap all: room\n", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogHoldsTheResultsCommentary()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        string log = Encoding.UTF8.GetString(await ReadAsync(files, "maps/room.log"));
        Assert.Equal(string.Concat(result.Log.Select(l => l + "\n")), log);
    }

    [Fact]
    public async Task TheBaseNameCanDifferFromTheMapsName()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        _ = await MapCompiler.CompileAsync(
            Request(files, content, CompileOutput.ToDirectory(files, VPath.Create(MapDirectory), "other")), null);

        Assert.Contains("other.bsp", await ListAsync(files));
    }

    [Fact]
    public async Task ADocumentSourceMakesTheSameMapAsItsFile()
    {
        VmfDocument room = Room();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(room);
        CompileResult fromFile = await MapCompiler.CompileAsync(Request(files, content, CompileOutput.InMemory), null);
        CompileResult fromDocument = await MapCompiler.CompileAsync(
            Request(files, content, CompileOutput.InMemory) with { Source = MapSource.FromDocument(room, "room") },
            null);

        Assert.Equal(await BytesAsync(fromFile.Bsp!), await BytesAsync(fromDocument.Bsp!));
    }

    [Fact]
    public void TheSourcesNameIsTheFileNameWithoutItsExtension()
    {
        MapSource source = MapSource.FromVmf(new InMemoryFileSystem(), VPath.Create("maps/Some_Map.vmf"));
        Assert.Equal("Some_Map", source.Name);
    }

    [Fact]
    public async Task OnlyEntsIsRefused()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileRequest request = Request(files, content) with { Vbsp = VbspOptions.OnlyEntsDefault };

        await Assert.ThrowsAsync<ArgumentException>(() => MapCompiler.CompileAsync(request, null));
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapCompiler.CompileAsync(Request(files, content), null, cancelled.Token));
        Assert.Equal(["room.vmf"], await ListAsync(files));
    }

    [Fact]
    public async Task ProgressReportsEachStageBoundary()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        List<long> chain = [];
        Progress progress = new(p =>
        {
            if (p.Stage == MapCompiler.ChainStage)
            {
                lock (chain)
                {
                    chain.Add(p.Done);
                }
            }
        });

        _ = await MapCompiler.CompileAsync(Request(files, content, CompileOutput.InMemory), progress);

        Assert.Equal([0L, 1L, 2L, 3L], chain);
    }

    [Fact]
    public async Task TheTimingsNameEveryStageInOrder()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

        Assert.Equal(["load", "vbsp", "vvis", "vrad", "write"], result.Timings.Select(t => t.Stage));
    }

    [Fact]
    public async Task TheLogSeamSeesTheSameLinesAsTheResult()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        ListLog log = new();
        CompileResult result = await MapCompiler.CompileAsync(
            Request(files, content, CompileOutput.InMemory) with { Log = log }, null);

        Assert.Equal(result.Log, log.Lines);
    }

    // Synchronous, unlike Progress<T>, so the order is the order reported.
    private sealed class Progress(Action<CompileProgress> report) : IProgress<CompileProgress>
    {
        public void Report(CompileProgress value) => report(value);
    }

    private sealed class ListLog : ICompileLog
    {
        public List<string> Lines { get; } = [];

        public void Write(DiagnosticSeverity severity, string message) => Lines.Add(message);

        public void Report(CompileDiagnostic diagnostic) =>
            Lines.Add($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
    }
}
