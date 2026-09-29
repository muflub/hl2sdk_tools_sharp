//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
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
/// The rooms design's cubemap fixture (15.2, 16): the real-content 3x3
/// sample (a sky whose textures resolve) whose <c>hall</c> has an
/// <c>env_cubemap</c> and a specular block, taken through the CLI as a user
/// runs it: <c>ssmap room</c>, <c>ssmap link</c>, <c>ssmap check</c>, and
/// <c>ssmap link --flatten</c> compiled with <c>ssmap vbsp</c> for the
/// reference.
/// </summary>
/// <remarks>
/// The sample itself stays as it is checked in (its other facts hold its
/// bytes to the generator); the cubemap and the material are added to the
/// library here, as the real-content set adds the sky.
/// </remarks>
public sealed class RoomsCubemapCommandsTests
{
    private const string Sky = "sky_day01_01";
    private const string Shiny = Rooms3x3Kit.MaterialFolder + "/shiny";

    /// <summary>
    /// A level whose hall has a sample links without game files, carries
    /// the sample at the flattened compile's position, packs exactly the
    /// flattened compile's files byte for byte (the hall's patched material
    /// and the sample's default cubemap copies under the level's name and
    /// the sample's world position, and the default cubemaps), and passes
    /// <c>ssmap check</c> with nothing to report, the "no cubemap samples"
    /// warning gone. The sample costs no entity: the linked entity count is
    /// the pack's prediction.
    /// </summary>
    /// <param name="level">The sample level: the canonical arrangement, and the same turned.</param>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    public async Task ALevelWithACubemapLinksChecksCleanAndMatchesItsFlattenedCompile(string level)
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

        BspData linked = await LoadAsync(fs, $"/sample/out/{level}.bsp");
        DCubemapSample sample = Assert.Single(BspStructView.As<DCubemapSample>(linked[BspLump.Cubemaps]).ToArray());
        Dictionary<string, byte[]> linkedFiles = await FilesAsync(linked);
        string at = $"{sample.Origin[0]}_{sample.Origin[1]}_{sample.Origin[2]}";
        Assert.Contains($"materials/maps/{level}/c{at}.vtf", linkedFiles.Keys);
        Assert.Contains($"materials/maps/{level}/c{at}.hdr.vtf", linkedFiles.Keys);
        Assert.Contains($"materials/maps/{level}/{Shiny}_{at}.vmt", linkedFiles.Keys);
        Assert.DoesNotContain(linkedFiles.Keys, n => n.StartsWith("materials/maps/hall/", StringComparison.Ordinal));

        using StringWriter check = new();
        exit = await CheckCommand.RunAsync(fs, [$"/sample/out/{level}.bsp"], check);
        Assert.True(exit == Program.ExitSuccess, check.ToString());
        Assert.EndsWith(": clean", check.ToString().TrimEnd(), StringComparison.Ordinal);
        Assert.True((await BspValidator.CheckAsync(linked, CancellationToken.None)).ForCode(BspRuleCodes.NoCubemaps).IsEmpty);

        // Zero entities for cubemaps: the linked lump holds exactly what the
        // pack's counts predict (1 is the worldspawn).
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

        BspData whole = await LoadAsync(fs, $"/sample/maps/{level}.bsp");
        Assert.True(whole[BspLump.Cubemaps].Data.Span.SequenceEqual(linked[BspLump.Cubemaps].Data.Span), "the samples differ");
        Dictionary<string, byte[]> reference = await FilesAsync(whole);
        Assert.Equal(reference.Keys.Order(StringComparer.Ordinal), linkedFiles.Keys);
        foreach ((string name, byte[] bytes) in reference)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(linkedFiles[name]), $"{name}: the link and the flattened compile pack different bytes");
        }
    }

    /// <summary>
    /// <c>ssmap room</c> with the cubemap writes the same pack at one thread
    /// and at four (15.5), and the hall's entry, and only the hall's, has
    /// its <c>CUBE</c> section.
    /// </summary>
    [Fact]
    public async Task ThePackIsTheSameBytesAtAnyThreadCount()
    {
        InMemoryFileSystem fs = await SampleAsync();
        byte[]? first = null;
        foreach (string threads in new[] { "1", "4" })
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
        Assert.Equal(["hall"], index.Entries.Where(e => e.Find(RoomCubemaps.SectionTag) is not null).Select(e => e.Name));
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
    /// The sample's generated files under <c>/sample</c>: the library naming
    /// the synthetic sky, with an <c>env_cubemap</c> in the hall and the
    /// hall's block made specular, and the game folder holding the sky and
    /// the specular material.
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
                AddCubemap(library);
                content = library.ToBytes();
            }

            fs.AddFile(Rooted("/sample/" + path), content);
        }

        return WithGame(fs);
    }

    /// <summary>
    /// The hall's block, a detail brush, made specular, and a sample above
    /// it, off the whole units, in the hall's cell of the library.
    /// </summary>
    private static void AddCubemap(VmfDocument library)
    {
        VmfChunk marker = library.GetChunks("entity").Single(e =>
            e.GetValue("classname") == RoomLibraryVmf.RoomEntity && e.GetValue(RoomLibraryVmf.NameKey) == "hall");
        Vec3 corner = Parse(marker.GetValue("origin")!);
        Vec3 blockMins = corner + new Vec3(160, 32, 16);
        VmfChunk block = library.GetChunks("entity")
            .SelectMany(e => e.GetChunks("solid"))
            .Single(s => VmfPlacement.Bounds(s).Mins == blockMins);
        foreach (VmfChunk side in block.GetChunks("side"))
        {
            side.Keys.First(k => k.Name == "material").Value = Shiny;
        }

        VmfChunk cubemap = new("entity");
        cubemap.AddKey("id", "990001");
        cubemap.AddKey("classname", "env_cubemap");
        cubemap.AddKey("origin", VmfPlacement.Format(corner + new Vec3(184.5f, 96.25f, 100)));
        library.Chunks.Add(cubemap);

        static Vec3 Parse(string text)
        {
            float[] v = [.. text.Split(' ').Select(t => float.Parse(t, CultureInfo.InvariantCulture))];
            return new Vec3(v[0], v[1], v[2]);
        }
    }

    /// <summary>Adds the synthetic sky's six materials and textures, the sample's materials and the specular one to the game folder.</summary>
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

        fs.AddFile(
            Rooted($"/sample/materials/{Shiny}.vmt"),
            Encoding.ASCII.GetBytes($"\"LightmappedGeneric\"\n{{\n\t\"$basetexture\" \"{Shiny}\"\n\t\"$envmap\" \"env_cubemap\"\n}}\n"));
        return fs;
    }

    private static readonly Lazy<IReadOnlyList<KeyValuePair<string, byte[]>>> SkyFiles = new(() =>
        [.. SyntheticContent.Build().Where(f => f.Key.StartsWith("materials/skybox/" + Sky, StringComparison.Ordinal))]);

    /// <summary>Where the commands look for a rooted path they are given: they resolve against the host.</summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
