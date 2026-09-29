//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The transit sample's generator (the rooms design, 15.7): the checked-in
/// files, the library's rooms and roles read by the pipeline's own readers,
/// and its run of levels held to the level rule.
/// </summary>
public sealed class RoomsTransitSampleTests
{
    /// <summary>The files under <c>samples/rooms-transit/</c> are exactly what the generator writes, plus the hand-written README.</summary>
    [RepoSourceFact("samples/rooms-transit/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites()
    {
        string root = Path.GetDirectoryName(RepoSourceFactAttribute.Find("samples/rooms-transit/README.md")!)!;
        IReadOnlyDictionary<string, byte[]> expected = RoomsTransitSample.Build();
        List<string> present = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "README.md"
                && !f.StartsWith("out/", StringComparison.Ordinal)
                && !f.StartsWith("maps/", StringComparison.Ordinal)
                && !(!f.Contains('/', StringComparison.Ordinal) && Path.GetExtension(f) is ".roompack" or ".roomnav")
                && !(f.StartsWith("levels/", StringComparison.Ordinal) && Path.GetExtension(f) is ".bsp" or ".vmf" or ".nav3d" or ".prt"))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), present);
        foreach ((string path, byte[] bytes) in expected)
        {
            byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(root, path));
            Assert.True(
                bytes.AsSpan().SequenceEqual(onDisk),
                $"samples/rooms-transit/{path} is not what the generator writes: run tools/RoomsSample --transit samples/rooms-transit");
        }
    }

    /// <summary>Two builds are the same bytes, LF only: the game, nine materials, the library and three levels.</summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = RoomsTransitSample.Build();
        IReadOnlyDictionary<string, byte[]> second = RoomsTransitSample.Build();
        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, f => Assert.True(f.Value.AsSpan().SequenceEqual(second[f.Key]), f.Key));
        Assert.All(first, f => Assert.DoesNotContain((byte)'\r', f.Value));
        Assert.Equal(13, first.Count);
        Assert.Equal(
            ["levels/transit_01.yaml", "levels/transit_02.yaml", "levels/transit_03.yaml"],
            first.Keys.Where(k => k.StartsWith("levels/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The library splits into the kit's five kinds and the two role rooms,
    /// with their roles; every room's transition data reads (the role rooms'
    /// with their volume and arrival, the up room with two spawn points and
    /// no fold, the down room's hallway folding; every ordinary room one
    /// spawn point).
    /// </summary>
    [Fact]
    public async Task TheLibraryHasItsRolesAndTransitionData()
    {
        VmfDocument library = await VmfDocument.ParseAsync(RoomsTransitSample.Build()["rooms.vmf"]);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(["cross", "tee", "corner", "hall", "end", "lift_up", "lift_down"], rooms.Select(r => r.Definition.Name));
        Assert.Equal(
            [RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.None, RoomRole.Up, RoomRole.Down],
            rooms.Select(r => r.Role));
        foreach (LibraryRoom room in rooms)
        {
            RoomTransit transit = RoomTransit.FromVmf(room.Definition, room.Role, room.Document)!;
            Assert.Equal(room.Role, transit.Role);
            switch (room.Role)
            {
                case RoomRole.Up:
                    Assert.Equal((-1, 2), (transit.FoldId, transit.Spawns.Count));
                    Assert.NotNull(transit.Arrival);
                    break;
                case RoomRole.Down:
                    Assert.True(transit.FoldId >= 0);
                    Assert.NotNull(transit.Arrival);
                    break;
                default:
                    Assert.Single(transit.Spawns);
                    break;
            }
        }
    }

    /// <summary>
    /// Every level of the run passes the level rule and flattens in both
    /// modes: the top level spawns in a room with a spawn point and leads
    /// only down, the middle both ways, the bottom only up.
    /// </summary>
    [Fact]
    public async Task TheRunPassesTheLevelRuleInBothModes()
    {
        IReadOnlyDictionary<string, byte[]> files = RoomsTransitSample.Build();
        VmfDocument library = await VmfDocument.ParseAsync(files["rooms.vmf"]);
        for (int i = 1; i <= RoomsTransitSample.Levels; i++)
        {
            string name = $"transit_0{i}";
            LevelGrid level = LevelYaml.Parse(System.Text.Encoding.UTF8.GetString(files[$"levels/{name}.yaml"]), name);
            foreach (bool mod in new[] { false, true })
            {
                VmfDocument flat = LevelFlattener.FlattenLevel(level, library, new LevelFlattenOptions { ModEntities = mod }).Vmf;
                List<string> classes = [.. flat.GetChunks(MapFileLoader.EntityChunk).Select(e => e.GetValue("classname") ?? string.Empty)];
                int transitions = classes.Count(c => c is "trigger_changelevel" or "logic_level_transition");
                Assert.Equal(i is 1 or RoomsTransitSample.Levels ? 1 : 2, transitions);
                Assert.Equal(mod ? 0 : i == 1 ? 1 : 3, classes.Count(c => c == "info_player_start"));
                Assert.DoesNotContain(RoomTransit.VolumeClass, classes.Where(_ => mod));
            }
        }
    }
}
