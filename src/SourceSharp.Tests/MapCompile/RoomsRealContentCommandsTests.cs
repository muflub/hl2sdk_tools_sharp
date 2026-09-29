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
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapGen.Content;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The rooms design's real-content set (15.7): the 3x3 sample library with
/// a sky whose textures resolve (the synthetic content's six skybox faces,
/// <see cref="SyntheticContent"/>), so every room's pak holds the default
/// cubemaps vbsp builds from them, taken through the CLI as a user runs it:
/// <c>ssmap room</c>, <c>ssmap link</c>, and <c>ssmap link --flatten</c>
/// compiled with <c>ssmap vbsp</c> for the reference.
/// </summary>
public sealed class RoomsRealContentCommandsTests
{
    private const string Sky = "sky_day01_01";

    /// <summary>
    /// A level of real-content rooms links without game files (decision D1:
    /// the pack is all the link reads), its pak holds the default cubemaps
    /// once under the level's name, and those are exactly the files, byte for
    /// byte, that vbsp packs for the flattened level (link and flatten agree,
    /// 5.9). The files cost no entity: the linked entity count is the pack's
    /// prediction, as for the sample without a sky.
    /// </summary>
    /// <param name="level">The sample level: the canonical arrangement, and the same turned.</param>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    public async Task ALevelLinksAndFlattensToTheSameFiles(string level)
    {
        InMemoryFileSystem fs = await SampleAsync();
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["/sample/rooms.vmf", "-game", "/sample", "-out", "/sample/rooms.roompack"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        // The link reads the pack alone: the game folder is gone.
        foreach (VPath path in fs.Paths.Where(p => p.Value.Contains("/sample/materials/", StringComparison.Ordinal)).ToList())
        {
            await fs.DeleteAsync(path);
        }

        using StringWriter linkOutput = new();
        exit = await RoomCommands.RunLinkAsync(
            fs, [$"/sample/levels/{level}.yaml", "-rooms", "/sample/rooms.roompack", "-out", $"/sample/out/{level}.bsp"], linkOutput);
        Assert.True(exit == Program.ExitSuccess, linkOutput.ToString());
        Assert.Contains(", 2 packed files", linkOutput.ToString(), StringComparison.Ordinal);

        BspData linked = await LoadAsync(fs, $"/sample/out/{level}.bsp");
        Dictionary<string, byte[]> linkedFiles = await FilesAsync(linked);
        Assert.Equal(
            [$"materials/maps/{level}/cubemapdefault.hdr.vtf", $"materials/maps/{level}/cubemapdefault.vtf"],
            linkedFiles.Keys);
        Assert.Equal(0, (await BspValidator.CheckAsync(linked, CancellationToken.None)).ErrorCount);

        // Zero entities for packed files: the linked lump holds exactly what
        // the pack's counts predict (1 is the worldspawn).
        IReadOnlyDictionary<string, RoomEntityCounts> counts;
        using (MemoryStream pack = new(fs.GetBytes(VPath.Create(Rooted("/sample/rooms.roompack")))!))
        {
            counts = await RoomPack.ReadEntityCountsAsync(pack, await RoomPack.ReadIndexAsync(pack));
        }

        LevelGrid grid = LevelYaml.Parse(Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted($"/sample/levels/{level}.yaml")))!), level);
        Assert.Equal(
            1 + grid.Placed.Sum(p => counts[p.Cell.Room].Tally(EntityClassTable.Default).Listed),
            EntityLump.Parse(linked[BspLump.Entities]).Count);

        // The reference: the flattened level compiled whole, with the game.
        fs = WithGame(fs);
        exit = await RoomCommands.RunLinkAsync(fs, [$"/sample/levels/{level}.yaml", "--flatten", "-out", $"/sample/maps/{level}.vmf"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        exit = await VbspCommand.RunAsync(fs, [$"/sample/maps/{level}.vmf"], cooker, output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        Dictionary<string, byte[]> reference = await FilesAsync(await LoadAsync(fs, $"/sample/maps/{level}.bsp"));
        Assert.Equal(reference.Keys.Order(StringComparer.Ordinal), linkedFiles.Keys);
        foreach ((string name, byte[] bytes) in reference)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(linkedFiles[name]), $"{name}: the link and the flattened compile pack different bytes");
        }
    }

    /// <summary>
    /// <c>ssmap room</c> on real content writes the same pack at one thread
    /// and at four, twice each (15.5), and every room in it packs its own
    /// default cubemaps under its own name, the same bytes in every room
    /// (the library has one sky).
    /// </summary>
    [Fact]
    public async Task ThePackIsTheSameBytesAtAnyThreadCount()
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

        using MemoryStream stream = new(first!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(stream, index, [.. index.Entries.Select(e => e.Name)]);
        byte[]? cube = null;
        foreach (RoomObject room in rooms)
        {
            Dictionary<string, byte[]> files = await FilesAsync(room.Bsp);
            string name = room.Definition.Name;
            Assert.Equal([$"materials/maps/{name}/cubemapdefault.hdr.vtf", $"materials/maps/{name}/cubemapdefault.vtf"], files.Keys.Order(StringComparer.Ordinal));
            cube ??= files[$"materials/maps/{name}/cubemapdefault.vtf"];
            Assert.Equal(cube, files[$"materials/maps/{name}/cubemapdefault.vtf"]);
            Assert.NotNull(room.Link);
        }
    }

    /// <summary>The linked map's name is the output file's, without its extension, lower-cased as vbsp's is.</summary>
    [Fact]
    public void TheMapNameIsTheOutputFilesName()
    {
        Assert.Equal("level_one", RoomCommands.MapBaseOf(VPath.Create("out/Level_One.bsp")));
        Assert.Equal("level", RoomCommands.MapBaseOf(VPath.Create("level")));
        Assert.Equal("a.b", RoomCommands.MapBaseOf(VPath.Create("x/A.B.bsp")));
    }

    private static async Task<BspData> LoadAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>A map's packed files by name, in the order its pak lists them.</summary>
    private static async Task<Dictionary<string, byte[]>> FilesAsync(BspData bsp)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (ZipEntry entry in (await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data)).Entries)
        {
            files.Add(entry.Name, entry.Data);
        }

        return files;
    }

    /// <summary>
    /// The sample's generated files under <c>/sample</c>, the library naming
    /// the synthetic sky and the game folder holding its six faces.
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
                library.GetChunk("world")!.AddKey("skyname", Sky);
                content = library.ToBytes();
            }

            fs.AddFile(Rooted("/sample/" + path), content);
        }

        return WithGame(fs);
    }

    /// <summary>Adds the synthetic sky's six materials and textures to the sample's game folder.</summary>
    private static InMemoryFileSystem WithGame(InMemoryFileSystem fs)
    {
        foreach ((string path, byte[] bytes) in SkyFiles.Value)
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
        }

        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build().Where(f => f.Key.StartsWith("materials/", StringComparison.Ordinal)))
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
        }

        return fs;
    }

    private static readonly Lazy<IReadOnlyList<KeyValuePair<string, byte[]>>> SkyFiles = new(() =>
        [.. SyntheticContent.Build().Where(f => f.Key.StartsWith("materials/skybox/" + Sky, StringComparison.Ordinal))]);

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
