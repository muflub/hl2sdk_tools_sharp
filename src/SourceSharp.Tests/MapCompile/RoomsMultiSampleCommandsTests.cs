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
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The multi-library sample (<c>samples/rooms-multi</c>, the rooms design's
/// 17.12) end to end through the CLI, as its README runs it: each library
/// packed on its own by <c>ssmap room</c> and both combined by <c>ssmap
/// roompack</c>, the <c>-large</c> levels rewritten by <c>ssmap layout</c>,
/// and every level linked from either, flattened, compiled whole, checked,
/// and held to the level map and navigation of its flattened compile.
/// </summary>
public sealed class RoomsMultiSampleCommandsTests
{
    /// <summary>The line the link, the flatten and the listing give for caves' copy of the sun (D24).</summary>
    private const string EqualSun = "warning: library caves: 1 singleton(s) equal to library base's dropped (light_environment).";

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    /// <summary>The sample as a game folder at <c>/multi</c>.</summary>
    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in RoomsMultiSample.Build())
        {
            fs.AddFile(Rooted("/multi/" + path), bytes);
        }

        return fs;
    }

    private static async Task<BspData> MapAsync(InMemoryFileSystem fs, string path)
    {
        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted(path)))!);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>The sample's level names: the mixed level and its turns, then the <c>-large</c> levels.</summary>
    private static IEnumerable<string> Levels() =>
        [.. Enumerable.Range(0, 4).Select(RoomsMultiSample.TurnName), .. RoomsMultiSample.LargeSeeds.Select(RoomsMultiSample.LargeName)];

    /// <summary>
    /// Both libraries packed apart (<c>ssmap room</c>) and together
    /// (<c>ssmap roompack</c>, which says once that caves' sun is base's);
    /// <c>ssmap rooms</c> over the mixed level lists both libraries and the
    /// tall rooms' boxes; then every level links from the separate packs
    /// (saying so of the sun) and from the combined pack (silent, since the
    /// pack settled it) to maps that differ only in the pack and level ids
    /// the worldspawn records, the same <c>.map2d</c>; each passes
    /// <c>ssmap check</c>, and so does its flattened compile, whose level map
    /// and navigation grid the link's are, the tall cells to their own
    /// heights (a version 3 <c>.nav3d</c>).
    /// </summary>
    [Fact]
    public async Task EveryLevelLinksFromSeparateAndCombinedPacksAndMatchesItsFlattenedCompile()
    {
        InMemoryFileSystem fs = Sample();
        foreach (string library in new[] { "base", "caves" })
        {
            using StringWriter room = new();
            Assert.True(
                await RoomCommands.RunRoomAsync(fs, [], [$"/multi/{library}.vmf", "-game", "/multi"], room) == Program.ExitSuccess,
                room.ToString());
        }

        using StringWriter combine = new();
        Assert.True(
            await RoomCommands.RunRoomPackAsync(
                fs, [], ["-game", "/multi", "-out", "/multi/multi.roompack", "base=/multi/base.vmf", "caves=/multi/caves.vmf"], combine)
            == Program.ExitSuccess,
            combine.ToString());
        Assert.Contains("ssmap roompack: " + EqualSun, combine.ToString(), StringComparison.Ordinal);

        using StringWriter listing = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["/multi/levels/mixed.yaml"], listing));
        string listed = listing.ToString();
        Assert.Contains("library base: ../base.vmf\n", listed, StringComparison.Ordinal);
        Assert.Contains("library caves: ../caves.vmf\n", listed, StringComparison.Ordinal);
        Assert.Contains(EqualSun, listed, StringComparison.Ordinal);
        Assert.Contains("256 x 256 x 512", listed, StringComparison.Ordinal);
        Assert.Contains("256 x 256 x 768", listed, StringComparison.Ordinal);

        VmfDocument[] libraries =
        [
            await VmfDocument.ParseAsync(RoomsMultiSample.Build()["base.vmf"]),
            await VmfDocument.ParseAsync(RoomsMultiSample.Build()["caves.vmf"]),
        ];
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        foreach (string name in Levels())
        {
            LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted($"/multi/levels/{name}.yaml")))!), name);

            using StringWriter separate = new();
            Assert.True(
                await RoomCommands.RunLinkAsync(fs, [$"/multi/levels/{name}.yaml", "-out", $"/multi/out/{name}.bsp"], separate) == Program.ExitSuccess,
                separate.ToString());
            Assert.StartsWith("ssmap link: " + EqualSun + "\n", separate.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Single(separate.ToString().Split('\n'), l => l.Contains("warning", StringComparison.Ordinal));

            using StringWriter combined = new();
            Assert.True(
                await RoomCommands.RunLinkAsync(
                    fs, [$"/multi/levels/{name}.yaml", "-rooms", "/multi/multi.roompack", "-out", $"/multi/comb/{name}.bsp"], combined)
                == Program.ExitSuccess,
                combined.ToString());
            Assert.DoesNotContain("warning", combined.ToString(), StringComparison.Ordinal);

            BspData linked = await MapAsync(fs, $"/multi/out/{name}.bsp");
            BspData fromCombined = await MapAsync(fs, $"/multi/comb/{name}.bsp");
            for (int lump = 0; lump < BspData.HeaderLumps; lump++)
            {
                if ((BspLump)lump is not (BspLump.Entities or BspLump.GameLump))
                {
                    Assert.True(linked[lump].Data.Span.SequenceEqual(fromCombined[lump].Data.Span), $"{name}: {(BspLump)lump}");
                }
            }

            Assert.Equal(linked.GameLumps.Select(g => (g.Id, g.Data.ToArray())), fromCombined.GameLumps.Select(g => (g.Id, g.Data.ToArray())));
            Assert.Equal(WithoutIds(linked), WithoutIds(fromCombined));
            Assert.NotEqual(RoomCompileIds.LevelIdOf(linked), RoomCompileIds.LevelIdOf(fromCombined));
            Assert.Equal(
                fs.GetBytes(VPath.Create(Rooted($"/multi/out/{name}.map2d"))),
                fs.GetBytes(VPath.Create(Rooted($"/multi/comb/{name}.map2d"))));
            await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, $"/multi/out/{name}.bsp");
            await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, $"/multi/comb/{name}.bsp");

            using StringWriter flatten = new();
            Assert.Equal(
                Program.ExitSuccess,
                await RoomCommands.RunLinkAsync(fs, [$"/multi/levels/{name}.yaml", "--flatten", "-out", $"/multi/maps/{name}.vmf"], flatten));
            Assert.Contains(EqualSun, flatten.ToString(), StringComparison.Ordinal);
            using StringWriter vbsp = new();
            Assert.True(await VbspCommand.RunAsync(fs, [$"/multi/maps/{name}.vmf"], cooker, vbsp) == Program.ExitSuccess, vbsp.ToString());
            await RoomsFeaturesSampleCommandsTests.AssertCheckedAsync(fs, $"/multi/maps/{name}.bsp");
            BspData flat = await MapAsync(fs, $"/multi/maps/{name}.bsp");

            uint checksum = BspMapChecksum.Compute(fs.GetBytes(VPath.Create(Rooted($"/multi/out/{name}.bsp")))!);
            Assert.True(
                Map2dWriter.Write(LevelMapBuilder.FromCompile(flat, checksum, level, libraries))
                    .AsSpan().SequenceEqual(fs.GetBytes(VPath.Create(Rooted($"/multi/out/{name}.map2d")))),
                $"{name}: the linked level map is not the flattened compile's");

            foreach (string from in new[] { "out", "comb" })
            {
                Nav3dReader nav = Nav3dReader.Open(fs.GetBytes(VPath.Create(Rooted($"/multi/{from}/{name}.nav3d")))!);
                Assert.Equal(3, nav.Version);
                RoomNavHeightsTests.AssertSameAsFlattened(flat, level, nav);
            }
        }
    }

    /// <summary>
    /// The <c>-large</c> levels are exactly what <c>ssmap layout</c> writes
    /// for their seeds from the two libraries, with each library's pack
    /// beside it (so the layout budgets the level, and a budget no level
    /// reaches changes nothing), and each places a group of tall rooms.
    /// </summary>
    [Fact]
    public async Task TheLargeLevelsAreWhatSsmapLayoutWrites()
    {
        InMemoryFileSystem fs = Sample();
        foreach (string library in new[] { "base", "caves" })
        {
            using StringWriter room = new();
            Assert.True(
                await RoomCommands.RunRoomAsync(fs, [], ["-cooker", "none", "-nolight", $"/multi/{library}.vmf", "-game", "/multi"], room) == Program.ExitSuccess,
                room.ToString());
        }

        HashSet<string> tall = [.. RoomsMultiSample.CavesRooms.Where(r => r.Height > 256).Select(r => r.Kind.Name)];
        foreach (ulong seed in RoomsMultiSample.LargeSeeds)
        {
            string name = RoomsMultiSample.LargeName(seed);
            using StringWriter output = new();
            Assert.True(
                await RoomCommands.RunLayoutAsync(
                    fs,
                    ["base=/multi/base.vmf", "caves=/multi/caves.vmf", "-rows", "4", "-columns", "4", "-seed", seed.ToString(CultureInfo.InvariantCulture),
                     "-large", RoomsMultiSample.LargeShare.ToString(CultureInfo.InvariantCulture),
                     "-group", RoomsMultiSample.GroupSize.ToString(CultureInfo.InvariantCulture), "-out", $"/multi/generated/{name}.yaml"],
                    output) == Program.ExitSuccess,
                output.ToString());
            Assert.Equal(
                fs.GetBytes(VPath.Create(Rooted($"/multi/levels/{name}.yaml"))),
                fs.GetBytes(VPath.Create(Rooted($"/multi/generated/{name}.yaml"))));

            LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(RoomsMultiSample.Build()[$"levels/{name}.yaml"]), name);
            Assert.True(level.Placed.Count(p => tall.Contains(p.Cell.Room)) >= 2, $"{name} places no group of tall rooms");
        }
    }

    /// <summary>A map's entities as text, the pack and level ids left out.</summary>
    private static List<string> WithoutIds(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Select(e => string.Join(
            " | ", e.Pairs.Where(p => p.Key is not ("ss_pack_id" or "ss_level_id")).Select(p => $"{p.Key}={p.Value}")))];
}
