//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The multi-library sample's generator (the rooms design, 17.12): the
/// checked-in files, the two libraries read by the pipeline's splitter, the
/// singleton rule over them, and the mixed level's cells resolved.
/// </summary>
public sealed class RoomsMultiSampleTests
{
    /// <summary>The files under <c>samples/rooms-multi/</c> are exactly what the generator writes, plus the hand-written README.</summary>
    [RepoSourceFact("samples/rooms-multi/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites() =>
        await SampleFiles.AssertCheckedInAsync("samples/rooms-multi", RoomsMultiSample.Build(), "--multi");

    /// <summary>Two builds are the same bytes, LF only: the game, two libraries, eight materials and six levels.</summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = RoomsMultiSample.Build();
        IReadOnlyDictionary<string, byte[]> second = RoomsMultiSample.Build();
        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, f => Assert.True(f.Value.AsSpan().SequenceEqual(second[f.Key]), f.Key));
        Assert.All(first, f => Assert.DoesNotContain((byte)'\r', f.Value));
        Assert.Equal(17, first.Count);
        Assert.Equal(
            ["levels/large_1.yaml", "levels/large_2.yaml", "levels/mixed.yaml", "levels/mixed_turn1.yaml", "levels/mixed_turn2.yaml", "levels/mixed_turn3.yaml"],
            first.Keys.Where(k => k.StartsWith("levels/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// base splits into the 3x3 kit's five cube rooms, the 3x3 sample's own
    /// rooms (the same room documents); caves into its two cube rooms named
    /// as two of base's, its two heights of tall rooms, and its skybox; each
    /// has one sun in its gaps, the same to the key, and only caves a
    /// skybox, so the level's singletons are base's sun (caves' copy equal)
    /// and caves' skybox (D29).
    /// </summary>
    [Fact]
    public async Task TheLibrariesShareASunAndCavesHasTheSkybox()
    {
        IReadOnlyDictionary<string, byte[]> files = RoomsMultiSample.Build();
        RoomLibrarySplit basis = RoomLibraryVmf.SplitLibrary(await VmfDocument.ParseAsync(files["base.vmf"]));
        RoomLibrarySplit caves = RoomLibraryVmf.SplitLibrary(await VmfDocument.ParseAsync(files["caves.vmf"]));
        RoomLibrarySplit threeByThree = RoomLibraryVmf.SplitLibrary(await VmfDocument.ParseAsync(Rooms3x3Sample.Build()[Rooms3x3Kit.LibraryFile]));

        Assert.Equal(["cross", "tee", "corner", "hall", "end"], basis.Rooms.Select(r => r.Definition.Name));
        Assert.All(basis.Rooms, r => Assert.False(r.Definition.IsShaped));
        Assert.Equal(threeByThree.Rooms.Select(r => r.Document.ToBytes()), basis.Rooms.Select(r => r.Document.ToBytes()));
        Assert.Null(basis.Skybox);

        Assert.Equal(["hall", "end", "cavern", "gallery", "shaft"], caves.Rooms.Select(r => r.Definition.Name));
        Assert.Equal([256f, 256f, 512f, 512f, 768f], caves.Rooms.Select(r => r.Definition.Height));
        Assert.Equal(RoomsMultiSample.SkyboxName, caves.Skybox?.Definition.Name);

        VmfChunk sun = Assert.Single(basis.LibraryEntities);
        Assert.Equal("light_environment", sun.GetValue("classname"));
        Assert.Equal(sun.Keys.Where(k => k.Name != "id").Select(k => (k.Name, k.Value)), Assert.Single(caves.LibraryEntities).Keys.Where(k => k.Name != "id").Select(k => (k.Name, k.Value)));

        LevelLibraries.LevelSingletonChoice level = LevelLibraries.LevelSingletonsOf(
        [
            new LevelLibraries.LibrarySingletons(basis.LibraryEntities, basis.Options, basis.Skybox?.Definition.Name),
            new LevelLibraries.LibrarySingletons(caves.LibraryEntities, caves.Options, caves.Skybox?.Definition.Name),
        ]);
        Assert.Equal((1, 0), (level.SkyboxSource, level.SunSource));
        Assert.Single(level.Entities);
    }

    /// <summary>
    /// The mixed level resolves against the two libraries: bare names to the
    /// one library that has them, <c>base.end</c> and <c>caves.end</c> as
    /// written, the alias <c>H</c> to caves' hall; it places all three tall
    /// rooms, and its turns place each at every rotation.
    /// </summary>
    [Fact]
    public void TheMixedLevelResolvesAndTurnsEveryTallRoom()
    {
        IReadOnlyDictionary<string, byte[]> files = RoomsMultiSample.Build();
        IReadOnlyList<IReadOnlyList<string>> names =
        [
            [.. RoomsMultiSample.BaseRooms.Select(r => r.Kind.Name)],
            [.. RoomsMultiSample.CavesRooms.Select(r => r.Kind.Name), RoomsMultiSample.SkyboxName],
        ];
        Dictionary<string, HashSet<int>> turns = new(StringComparer.Ordinal);
        for (int t = 0; t < 4; t++)
        {
            string name = RoomsMultiSample.TurnName(t);
            LevelGrid level = LevelLibraries.Resolve(LevelYaml.Parse(Encoding.UTF8.GetString(files[$"levels/{name}.yaml"]), name), names);
            List<string> rooms = [.. level.Placed.Select(p => p.Cell.Room).Order(StringComparer.Ordinal)];
            Assert.Equal(
                ["base.corner", "base.cross", "base.end", "base.tee", "caves.cavern", "caves.end", "caves.gallery", "caves.hall", "caves.shaft"],
                rooms);
            foreach ((_, _, LevelCell cell) in level.Placed.Where(p => p.Cell.Room is "caves.cavern" or "caves.gallery" or "caves.shaft"))
            {
                (turns.TryGetValue(cell.Room, out HashSet<int>? seen) ? seen : turns[cell.Room] = []).Add(cell.Rotation);
            }
        }

        Assert.Equal(3, turns.Count);
        Assert.All(turns.Values, seen => Assert.Equal([0, 1, 2, 3], seen.Order()));
    }
}
