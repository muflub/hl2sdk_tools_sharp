//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The features sample (<c>samples/rooms-features</c>) end to end through
/// the CLI, as its README runs it, on an in-memory disk: <c>ssmap room</c>
/// packs its rooms lit, with the managed cooker and navigation, as it does by
/// default; <c>ssmap rooms</c> lists what each carries; and every level (the
/// sample and its three turns) links, flattens, compiles whole with
/// <c>ssmap vbsp</c>, passes <c>ssmap check</c>, and links to the level map
/// and navigation its flattened compile gives.
/// </summary>
public sealed class RoomsFeaturesSampleCommandsTests
{
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    /// <summary>The sample as a game folder at <c>/features</c>.</summary>
    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in RoomsFeaturesSample.Build())
        {
            fs.AddFile(Rooted("/features/" + path), bytes);
        }

        return fs;
    }

    private static async Task<BspData> MapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>
    /// <c>ssmap room</c> then <c>ssmap rooms</c>: the tall room's box, the
    /// garden's detail props, marker and label, the ridge's two
    /// displacements, the pool's one body of water, each on its room's own
    /// lines; then each level linked (no warning), checked (no error, and
    /// only the missing cubemap sample warned, as every sample level is),
    /// flattened and compiled whole, the flattened compile checked alike; the
    /// linked <c>.map2d</c> is the flattened compile's map cut by the level
    /// file, byte for byte; the <c>.nav3d</c> (version 3, for the tall room)
    /// is the flattened compile's grid run for run; every point of a lattice
    /// holds the same contents in both maps (the pools' water through their
    /// joint included); and the linked map carries the garden's detail
    /// props, the ridges' displacements and one water record for the two
    /// jointed pools.
    /// </summary>
    [Fact]
    public async Task EveryLevelLinksFlattensAndMatchesItsFlattenedCompile()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter pack = new();
        Assert.True(
            await RoomCommands.RunRoomAsync(fs, [], ["/features/rooms.vmf", "-game", "/features"], pack) == Program.ExitSuccess,
            pack.ToString());

        using StringWriter listing = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/features/rooms.vmf"], listing));
        string[] lines = listing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "  lighting: 1 turn, no sun or sky reaches it",
                "  detail props: " + await DetailPropCountAsync(fs, "garden"),
                "  map: 3 floor ring(s), 1 marker(s), label \"Garden\"",
            ],
            RoomLines(lines, "garden")[2..5]);
        Assert.Equal("tower: cell at (384, 0, 0), 256 x 256 x 512, 2 door(s)", RoomLines(lines, "tower")[0]);
        Assert.Contains("  map: 2 floor ring(s), 0 marker(s), label \"Tower\"", RoomLines(lines, "tower"));
        Assert.Contains("  displacements: 2", RoomLines(lines, "ridge"));
        Assert.Contains("  water: 1 volume(s)", RoomLines(lines, "pool"));
        Assert.DoesNotContain(lines, l => l.StartsWith("  detail props:", StringComparison.Ordinal) && !RoomLines(lines, "garden").Contains(l));

        VmfDocument library = await VmfDocument.ParseAsync(RoomsFeaturesSample.Build()[Rooms3x3Kit.LibraryFile]);
        int props = int.Parse((await DetailPropCountAsync(fs, "garden")), System.Globalization.CultureInfo.InvariantCulture);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        for (int turns = 0; turns < 4; turns++)
        {
            string name = RoomsFeaturesSample.TurnName(turns);
            LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted($"/features/levels/{name}.yaml")))!), name);

            using StringWriter link = new();
            int linkedExit = await RoomCommands.RunLinkAsync(fs, [$"/features/levels/{name}.yaml", "-out", $"/features/out/{name}.bsp"], link);
            Assert.True(linkedExit == Program.ExitSuccess, link.ToString());
            Assert.DoesNotContain("warning", link.ToString(), StringComparison.Ordinal);
            await AssertCheckedAsync(fs, $"/features/out/{name}.bsp");

            using StringWriter flatten = new();
            Assert.Equal(
                Program.ExitSuccess,
                await RoomCommands.RunLinkAsync(fs, [$"/features/levels/{name}.yaml", "--flatten", "-out", $"/features/maps/{name}.vmf"], flatten));
            using StringWriter vbsp = new();
            Assert.True(await VbspCommand.RunAsync(fs, [$"/features/maps/{name}.vmf"], cooker, vbsp) == Program.ExitSuccess, vbsp.ToString());
            await AssertCheckedAsync(fs, $"/features/maps/{name}.bsp");

            byte[] written = fs.GetBytes(VPath.Create(Rooted($"/features/out/{name}.bsp")))!;
            BspData linked = await MapAsync(fs, $"/features/out/{name}.bsp");
            BspData flat = await MapAsync(fs, $"/features/maps/{name}.bsp");

            // The level map: the flattened compile's, cut by the level file.
            byte[] map2d = fs.GetBytes(VPath.Create(Rooted($"/features/out/{name}.map2d")))!;
            uint checksum = BspMapChecksum.Compute(written);
            Assert.True(
                Map2dWriter.Write(LevelMapBuilder.FromCompile(flat, checksum, level, [library])).AsSpan().SequenceEqual(map2d),
                $"{name}: the linked level map is not the flattened compile's");
            Map2dLevel read = Map2dReader.Read(map2d, checksum);
            Assert.Equal(("fountain", "Fountain"), (read.Markers.Single().Kind, read.Markers.Single().Label));
            Assert.Equal(["Garden", "Tower"], read.Rooms.Select(r => r.Label).Where(l => l.Length > 0).Order(StringComparer.Ordinal));

            // The navigation: the flattened compile's grid, the tower's cell to its own height.
            Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(VPath.Create(Rooted($"/features/out/{name}.nav3d")))!);
            Assert.Equal((3, (int)(RoomsFeaturesSample.TowerHeight / 16)), (nav.Version, nav.TallestCell));
            RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav);

            // What each feature leaves in the map.
            Assert.Equal(
                LevelLinkerWaterTests.Points(flat, level.Columns, level.Rows),
                LevelLinkerWaterTests.Points(linked, level.Columns, level.Rows));
            Assert.Equal([$"{RoomsFeaturesSample.WaterLevel} 16 {RoomsFeaturesSample.WaterMaterial}"], LevelLinkerWaterTests.Records(linked));
            Assert.Equal(LevelLinkerWaterTests.Records(flat), LevelLinkerWaterTests.Records(linked));
            Assert.Equal(props, RoomDetailHarness.Lump(linked).Props.Count);
            Assert.NotEmpty(RoomDetailHarness.Lump(flat).Props);
            Assert.Equal(4, RoomDisplacementHarness.Infos(linked).Length);
            Assert.Equal(RoomDisplacementHarness.Infos(flat).Length, RoomDisplacementHarness.Infos(linked).Length);
        }
    }

    /// <summary>
    /// The sample's level files are what the generator writes for the level
    /// turned whole: each file the one before it turned a quarter, every
    /// room in its turned cell a quarter further on, and the fourth turn the
    /// level again.
    /// </summary>
    [Fact]
    public void TheTurnedLevelsAreTheLevelTurned()
    {
        IReadOnlyDictionary<string, byte[]> files = RoomsFeaturesSample.Build();
        LevelGrid Read(int turns)
        {
            string name = RoomsFeaturesSample.TurnName(turns);
            return LevelYaml.Parse(Encoding.UTF8.GetString(files[$"levels/{name}.yaml"]), name);
        }

        LevelGrid level = Read(0);
        for (int turns = 1; turns <= 4; turns++)
        {
            LevelGrid turned = RoomsSampleLevels.Turned(level);
            LevelGrid expected = Read(turns % 4);
            Assert.Equal((expected.Rows, expected.Columns), (turned.Rows, turned.Columns));
            Assert.Equal(
                expected.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"),
                turned.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"));
            level = turned;
        }
    }

    /// <summary>A room's lines of a listing: its heading and the indented lines under it.</summary>
    private static string[] RoomLines(string[] lines, string room)
    {
        int at = Array.FindIndex(lines, l => l.StartsWith(room + ": ", StringComparison.Ordinal));
        Assert.True(at >= 0, $"no {room} in the listing");
        int end = at + 1;
        while (end < lines.Length && lines[end].StartsWith("  ", StringComparison.Ordinal))
        {
            end++;
        }

        return lines[at..end];
    }

    /// <summary>How many detail props the pack's room holds, read from its compile's lump.</summary>
    private static async Task<string> DetailPropCountAsync(InMemoryFileSystem fs, string room)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted("/features/rooms.roompack")))!);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        RoomObject loaded = Assert.Single(await RoomPack.LoadRoomsAsync(stream, index, [room]));
        int count = RoomDetailProps.CountOf(loaded.Bsp);
        Assert.True(count > 0, $"{room} has no detail props");
        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><c>ssmap check</c> on a map: no error, and the one warning every sample level has (no cubemap sample).</summary>
    internal static async Task AssertCheckedAsync(IFileSystem fs, string map)
    {
        using StringWriter check = new();
        _ = await CheckCommand.RunAsync(fs, [Rooted(map)], check);
        string text = check.ToString();
        Assert.True(text.Contains(": 0 error(s), 1 warning(s)", StringComparison.Ordinal), text);
        Assert.Contains("BSP0029 warning", text, StringComparison.Ordinal);
    }
}
