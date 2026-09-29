//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
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
/// Brush entities through the CLI as a user runs it (the rooms design,
/// 4.1): the 3x3 sample with a door hung in its <c>cross</c>'s east doorway
/// (socket furniture), a hinged door with an origin brush and a trigger,
/// packed by <c>ssmap room</c>, linked by <c>ssmap link</c>, and checked
/// against <c>ssmap link --flatten</c> compiled by <c>ssmap vbsp</c>, at the
/// sample level and its turned twin.
/// </summary>
public sealed class RoomsBrushEntitiesCommandsTests
{
    private const string OriginMaterial = Rooms3x3Kit.MaterialFolder + "/origin";

    /// <summary>
    /// <c>ssmap room</c> writes the same pack at one thread and at four,
    /// twice each (15.5), the <c>cross</c> with its <c>BMOD</c> section; each
    /// level links from the pack, passes the loader checks, and holds the
    /// brush entities the flattened level's compile holds, model by model.
    /// </summary>
    [Fact]
    public async Task BrushEntitiesArePackedLinkedAndFlattenedThroughTheCli()
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
            Assert.NotNull(index.Find("cross")!.Find(RoomBrushModels.SectionTag));
            Assert.Null(index.Find("tee")!.Find(RoomBrushModels.SectionTag));
        }

        foreach (string level in new[] { "rooms3x3", "rooms3x3_turn1" })
        {
            using StringWriter linkOutput = new();
            int linked = await RoomCommands.RunLinkAsync(
                fs, [$"/sample/levels/{level}.yaml", "-rooms", "/sample/rooms.roompack", "-out", $"/sample/out/{level}.bsp"], linkOutput);
            Assert.True(linked == Program.ExitSuccess, linkOutput.ToString());
            BspData bsp = await LoadAsync(fs, $"/sample/out/{level}.bsp");
            ValidationReport report = await BspValidator.CheckAsync(bsp, CancellationToken.None);
            Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
            Assert.Equal(["cross_door", "cross_hinged", "cross_trigger"], RoomBrushHarness.BrushEntities(bsp).Select(p => p.Entity.Get("targetname")!).Order());

            using StringWriter output = new();
            int exit = await RoomCommands.RunLinkAsync(fs, [$"/sample/levels/{level}.yaml", "--flatten", "-out", $"/sample/maps/{level}.vmf"], output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
            await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
            exit = await VbspCommand.RunAsync(fs, [$"/sample/maps/{level}.vmf", "-game", "/sample"], cooker, output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
            BspData flat = await LoadAsync(fs, $"/sample/maps/{level}.bsp");
            Assert.Equal(RoomBrushHarness.Observed(flat), RoomBrushHarness.Observed(bsp));
            foreach (string name in new[] { "cross_door", "cross_hinged", "cross_trigger" })
            {
                Assert.Equal(
                    RoomBrushHarness.Traces(flat, RoomBrushHarness.Named(flat, "targetname", name)),
                    RoomBrushHarness.Traces(bsp, RoomBrushHarness.Named(bsp, "targetname", name)));
            }
        }
    }

    private static async Task<BspData> LoadAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>
    /// The sample's generated files under <c>/sample</c>, the <c>cross</c>
    /// holding a door in its east doorway (its socket's furniture), a hinged
    /// door with an origin brush and a trigger, and an origin material in the
    /// game folder.
    /// </summary>
    private static async Task<InMemoryFileSystem> SampleAsync()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            byte[] content = bytes;
            if (path == Rooms3x3Kit.LibraryFile)
            {
                VmfDocument library = await VmfDocument.ParseAsync(bytes);
                LibraryRoom cross = RoomLibraryVmf.Split(library).Single(r => r.Definition.Name == "cross");
                RoomSocket east = cross.Definition.Sockets.Single(s => s.Facing == RoomFacing.PositiveX);
                Box plug = RoomLinter.SealBox(cross.Definition, east, cross.Definition.CellSize);
                float middle = cross.Definition.CellSize / 2;
                VmfChunk[] entities =
                [
                    RoomBrushHarness.Brush(
                        "func_door", 990200, plug.Mins, plug.Maxs, Rooms3x3Kit.WallMaterial, null,
                        ("targetname", "cross_door"), ("movedir", "90 0 0"), (RoomStaticProps.SocketKey, east.Name)),
                    Hinged(990210, new Vec3(middle - 40, middle - 40, 16), new Vec3(middle - 32, middle + 8, 120), new Vec3(middle - 36, middle - 36, 20)),
                    RoomBrushHarness.Brush(
                        "trigger_multiple", 990220, new Vec3(middle + 8, middle + 8, 16), new Vec3(middle + 40, middle + 40, 96),
                        Rooms3x3Kit.PlugMaterial, null, ("targetname", "cross_trigger")),
                ];
                foreach (VmfChunk entity in entities)
                {
                    library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(cross.Corner)));
                }

                content = library.ToBytes();
            }

            fs.AddFile(Rooted("/sample/" + path), content);
        }

        fs.AddFile(
            Rooted($"/sample/materials/{OriginMaterial}.vmt"),
            Encoding.ASCII.GetBytes("\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"rooms3x3/wall\"\n\t\"%compileOrigin\" \"1\"\n}\n"));
        return fs;
    }

    /// <summary>A hinged door of the sample's wall material with an origin brush of the origin material.</summary>
    private static VmfChunk Hinged(int id, Vec3 mins, Vec3 maxs, Vec3 hinge)
    {
        VmfChunk entity = RoomBrushHarness.Brush("func_door_rotating", id, mins, maxs, Rooms3x3Kit.WallMaterial, null, ("targetname", "cross_hinged"));
        entity.Children.Add(RoomModel.Slab(OriginMaterial, hinge - new Vec3(4, 4, 4), hinge + new Vec3(4, 4, 4), (id * 10) + 1));
        return entity;
    }

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
