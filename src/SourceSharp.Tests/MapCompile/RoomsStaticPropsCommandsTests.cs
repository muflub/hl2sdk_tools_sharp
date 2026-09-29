//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// Static props through the CLI as a user runs it (the rooms design, 4.3):
/// the 3x3 sample with a <c>prop_static</c> in its <c>tee</c> and the model
/// in the game folder, packed by <c>ssmap room</c>, linked by <c>ssmap
/// link</c> with the game folder gone, and checked against <c>ssmap link
/// --flatten</c> compiled by <c>ssmap vbsp</c>.
/// </summary>
public sealed class RoomsStaticPropsCommandsTests
{
    /// <summary>
    /// <c>ssmap room</c> writes the same pack at one thread and at four,
    /// twice each (15.5), the <c>tee</c> with its <c>PROP</c> section; the
    /// level links from the pack alone (decision D1: the model is not read
    /// at link), passes the loader checks, and holds the props the flattened
    /// level's compile holds.
    /// </summary>
    [Fact]
    public async Task AStaticPropIsPackedLinkedAndFlattenedThroughTheCli()
    {
        InMemoryFileSystem fs = await SampleAsync();
        byte[]? first = null;
        foreach (string threads in new[] { "1", "4", "1", "4" })
        {
            using StringWriter output = new();
            int exit = await RoomCommands.RunRoomAsync(
                fs, [], ["/sample/rooms.vmf", "-game", "/sample", "-threads", threads, "-out", "/sample/rooms.roompack"], output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
            byte[] pack = fs.GetBytes(VPath.Create(Rooted("/sample/rooms.roompack")))!;
            first ??= pack;
            Assert.True(first.AsSpan().SequenceEqual(pack), $"-threads {threads} wrote another pack");
        }

        using (MemoryStream stream = new(first!))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
            Assert.NotNull(index.Find("tee")!.Find(RoomStaticProps.SectionTag));
            Assert.Null(index.Find("cross")!.Find(RoomStaticProps.SectionTag));
        }

        // The link reads the pack alone: the models are gone.
        foreach (VPath path in fs.Paths.Where(p => p.Value.Contains("/sample/models/", StringComparison.Ordinal)).ToList())
        {
            await fs.DeleteAsync(path);
        }

        using StringWriter linkOutput = new();
        int linked = await RoomCommands.RunLinkAsync(
            fs, ["/sample/levels/rooms3x3.yaml", "-rooms", "/sample/rooms.roompack", "-out", "/sample/out/rooms3x3.bsp"], linkOutput);
        Assert.True(linked == Program.ExitSuccess, linkOutput.ToString());
        BspData level = await LoadAsync(fs, "/sample/out/rooms3x3.bsp");
        ValidationReport report = await BspValidator.CheckAsync(level, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        Assert.Equal(2, RoomPropHarness.Props(level).Props.Count);

        fs = WithModels(fs);
        using StringWriter output2 = new();
        int exit2 = await RoomCommands.RunLinkAsync(fs, ["/sample/levels/rooms3x3.yaml", "--flatten", "-out", "/sample/maps/rooms3x3.vmf"], output2);
        Assert.True(exit2 == Program.ExitSuccess, output2.ToString());
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        exit2 = await VbspCommand.RunAsync(fs, ["/sample/maps/rooms3x3.vmf"], cooker, output2);
        Assert.True(exit2 == Program.ExitSuccess, output2.ToString());
        Assert.Equal(RoomPropHarness.Observed(await LoadAsync(fs, "/sample/maps/rooms3x3.bsp")), RoomPropHarness.Observed(level));
    }

    private static async Task<BspData> LoadAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>The sample's generated files under <c>/sample</c>, a prop in the <c>tee</c>, and the models in the game folder.</summary>
    private static async Task<InMemoryFileSystem> SampleAsync()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            byte[] content = bytes;
            if (path == Rooms3x3Kit.LibraryFile)
            {
                VmfDocument library = await VmfDocument.ParseAsync(bytes);
                VmfChunk marker = library.GetChunks(MapFileLoader.EntityChunk)
                    .Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity && e.GetValue(RoomLibraryVmf.NameKey) == "tee");
                Vec3 corner = VmfPlacement.Origin(marker)!.Value;
                VmfChunk prop = RoomPropHarness.Prop(990100, RoomPropHarness.BoxModel, new Vec3(192, 48, 16), "0 20 0");
                library.Chunks.Add(VmfPlacement.MoveEntity(prop, QuarterTurn.Translation(corner)));
                content = library.ToBytes();
            }

            fs.AddFile(Rooted("/sample/" + path), content);
        }

        return WithModels(fs);
    }

    private static InMemoryFileSystem WithModels(InMemoryFileSystem fs)
    {
        foreach ((string path, byte[] bytes) in RoomPropHarness.Models())
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
        }

        return fs;
    }

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
