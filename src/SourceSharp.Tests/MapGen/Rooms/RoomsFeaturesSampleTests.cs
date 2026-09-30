//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The features sample's generator: the checked-in files, the library read
/// by the pipeline's own splitter (heights, water sockets, labels,
/// displacements), the kit's tall shell, and the level helpers.
/// </summary>
public sealed class RoomsFeaturesSampleTests
{
    /// <summary>The files under <c>samples/rooms-features/</c> are exactly what the generator writes, plus the hand-written README.</summary>
    [RepoSourceFact("samples/rooms-features/README.md")]
    public async Task TheCheckedInSampleIsWhatTheGeneratorWrites() =>
        await SampleFiles.AssertCheckedInAsync("samples/rooms-features", RoomsFeaturesSample.Build(), "--features");

    /// <summary>Two builds are the same bytes, LF only: the game, the detail file, ten materials, the library and four levels.</summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = RoomsFeaturesSample.Build();
        IReadOnlyDictionary<string, byte[]> second = RoomsFeaturesSample.Build();
        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, f => Assert.True(f.Value.AsSpan().SequenceEqual(second[f.Key]), f.Key));
        Assert.All(first, f => Assert.DoesNotContain((byte)'\r', f.Value));
        Assert.Equal(17, first.Count);
        Assert.Equal(
            ["levels/features.yaml", "levels/features_turn1.yaml", "levels/features_turn2.yaml", "levels/features_turn3.yaml"],
            first.Keys.Where(k => k.StartsWith("levels/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The pipeline's splitter reads the library as the generator describes
    /// it: four rooms in order, each the definition the generator gives
    /// (the tower 512 tall), the pool's water sockets east and west at the
    /// sample's level and material, the garden's and the tower's labels.
    /// </summary>
    [Fact]
    public async Task TheLibrarySplitsIntoTheFeatureRooms()
    {
        VmfDocument library = await VmfDocument.ParseAsync(RoomsFeaturesSample.Build()[Rooms3x3Kit.LibraryFile]);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal(["garden", "tower", "ridge", "pool"], rooms.Select(r => r.Definition.Name));
        foreach ((LibraryRoom room, RoomsFeaturesSample.FeatureRoom expected) in rooms.Zip(RoomsFeaturesSample.Rooms))
        {
            Assert.Equal(expected.Definition.Height, room.Definition.Height);
            Assert.Equal(expected.Definition.Sockets, room.Definition.Sockets);
            Assert.Equal(expected.Label ?? string.Empty, room.MapLabel);
        }

        Assert.Equal(RoomsFeaturesSample.TowerHeight, rooms[1].Definition.Height);
        Assert.True(rooms[1].Definition.IsShaped);
        RoomWaterSocket water = new(RoomsFeaturesSample.WaterLevel, RoomsFeaturesSample.WaterMaterial);
        Assert.Equal(
            [new KeyValuePair<string, RoomWaterSocket>("east", water), new KeyValuePair<string, RoomWaterSocket>("west", water)],
            rooms[3].WaterSockets.OrderBy(w => w.Key, StringComparer.Ordinal));
        Assert.All(rooms.Where(r => r.Definition.Name != "pool"), r => Assert.Empty(r.WaterSockets));
    }

    /// <summary>
    /// The ridge room's two patches are displacements vbsp reads: the top
    /// side of each carries a <c>dispinfo</c> of power 3 and 2, started at
    /// its brush's top corner in the library (the room's cell corner added),
    /// with a row per vertex row; the ridge crest 40 above its flanks' feet.
    /// </summary>
    [Fact]
    public async Task TheRidgeRoomsPatchesAreDisplacements()
    {
        VmfDocument library = await VmfDocument.ParseAsync(RoomsFeaturesSample.Build()[Rooms3x3Kit.LibraryFile]);
        LibraryRoom ridge = RoomLibraryVmf.Split(library).Single(r => r.Definition.Name == "ridge");
        List<VmfChunk> infos = [.. ridge.Document.GetChunk(MapFileLoader.WorldChunk)!
            .GetChunks(MapFileLoader.SolidChunk)
            .SelectMany(s => s.GetChunks(MapFileLoader.SideChunk))
            .SelectMany(s => s.GetChunks("dispinfo"))];
        Assert.Equal(["3", "2"], infos.Select(i => i.GetValue("power")));
        // Room-local after the split: the brushes' top corners.
        Assert.Equal(["[32 48 24]", "[160 48 24]"], infos.Select(i => i.GetValue("startposition")));
        Assert.Equal(9, infos[0].GetChunk("distances")!.Keys.Count());
        Assert.Equal("4 20 40 20 2 3", string.Join(' ', infos[0].GetChunk("distances")!.GetValue("row0")!.Split(' ').Skip(2).Take(6)));
        Assert.Equal(8, infos[0].GetChunk("triangle_tags")!.Keys.Count());

        // The writer puts a side's displacement inside the side, after its keys; a side without one is written as before.
        VmfSolid patch = RoomsFeaturesSample.Patch(RoomsFeaturesSample.SidePatch, 2, RoomsFeaturesSample.SideHeight);
        VmfMap map = new();
        map.WorldSolids.Add(patch);
        string text = map.Write();
        Assert.Equal(1, text.Split("dispinfo").Length - 1);
        Assert.Contains("\t\t\t\"smoothing_groups\" \"0\"\n\t\t\tdispinfo\n\t\t\t{\n\t\t\t\t\"power\" \"2\"\n", text, StringComparison.Ordinal);
        patch.Sides[0].Displacement = null;
        VmfMap plain = new();
        plain.WorldSolids.Add(VmfMap.Box(RoomsFeaturesSample.SidePatch.Mins, RoomsFeaturesSample.SidePatch.Maxs, Rooms3x3Kit.BlockMaterial, Rooms3x3Kit.FloorMaterial));
        VmfMap without = new();
        without.WorldSolids.Add(patch);
        Assert.Equal(plain.Write(), without.Write());
    }

    /// <summary>
    /// The kit's shell at a room's own height: a cube's brushes are the ones
    /// the kit always gave, whatever spelled the height; a tall room's walls
    /// and jambs run to its ceiling, its ceiling slab stands at its height,
    /// and each door gets one lintel over the opening from the cube's
    /// ceiling up, after its jambs; the plugs are the cube's.
    /// </summary>
    [Fact]
    public void ATallRoomsShellRunsToItsCeilingWithALintelOverEachDoor()
    {
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            Assert.Equal(Rooms3x3Kit.Brushes(kind), Rooms3x3Kit.Brushes(kind, height: Rooms3x3Kit.CellSize));
        }

        RoomKind hall = Rooms3x3Kit.Kind("hall");
        IReadOnlyList<KitBrush> cube = Rooms3x3Kit.Brushes(hall);
        IReadOnlyList<KitBrush> tall = Rooms3x3Kit.Brushes(hall, height: 512, ceilingMaterial: "x/sky");
        Assert.Equal(cube.Count + hall.Sockets.Count, tall.Count);
        Assert.Equal(new Bounds(new(0, 0, 496), new(256, 256, 512)), tall[1].Box);
        Assert.Equal("x/sky", tall[1].Material);
        List<KitBrush> lintels = [.. tall.Where(b => b.Box.Mins.Z == 240 && b.Box.Maxs.Z == 496)];
        Assert.Equal(
            [new Bounds(new(240, 80, 240), new(256, 176, 496)), new Bounds(new(0, 80, 240), new(16, 176, 496))],
            lintels.Select(b => b.Box));
        Assert.All(tall.Where(b => b.Material == Rooms3x3Kit.WallMaterial && !lintels.Contains(b)), b => Assert.Equal(496, b.Box.Maxs.Z));
        Assert.Equal(
            cube.Where(b => b.Material == Rooms3x3Kit.PlugMaterial).Select(b => b.Box),
            tall.Where(b => b.Material == Rooms3x3Kit.PlugMaterial).Select(b => b.Box));
    }

    /// <summary>
    /// A level from its rows puts the first row north; turned four times it
    /// is the level again; each turn keeps its libraries and aliases.
    /// </summary>
    [Fact]
    public void TheLevelHelpersBuildAndTurnALevel()
    {
        LevelGrid level = RoomsSampleLevels.FromRows("l", "../a.vmf", RoomsFeaturesSample.LevelRows);
        Assert.Equal((2, 3), (level.Rows, level.Columns));
        Assert.Equal("garden", level[0, 1]!.Room);
        Assert.Equal(new LevelCell("ridge", 2), level[2, 1]);
        Assert.Equal("pool", level[0, 0]!.Room);

        LevelGrid turned = RoomsSampleLevels.Turned(level);
        Assert.Equal((3, 2), (turned.Rows, turned.Columns));
        // (x, y) goes to (rows - 1 - y, x): the south-west pool to the south-east, a quarter further on.
        Assert.Equal(new LevelCell("pool", 1), turned[1, 0]);

        LevelGrid back = level;
        for (int i = 0; i < 4; i++)
        {
            back = RoomsSampleLevels.Turned(back);
        }

        Assert.Equal(level.Cells, back.Cells);

        LevelGrid keyed = RoomsSampleLevels.FromRows("m", "../a.vmf", [[new LevelCell("H", 0)]], [new LevelLibrary("a", "../a.vmf")], [new LevelAlias("H", "a.hall")]);
        LevelGrid renamed = RoomsSampleLevels.Renamed(RoomsSampleLevels.Turned(keyed), "n");
        Assert.Equal(("n", "a"), (renamed.Name, renamed.Libraries!.Single().Key));
        Assert.Equal("H", renamed.Aliases.Single().Name);
    }
}

/// <summary>What the sample facts share: holding a checked-in sample folder to its generator.</summary>
internal static class SampleFiles
{
    /// <summary>
    /// The files under a sample folder are exactly the generator's, plus its
    /// hand-written README, compile output (which git ignores) left out.
    /// </summary>
    /// <param name="folder">The sample's folder, repository-relative.</param>
    /// <param name="expected">What the generator writes.</param>
    /// <param name="flag">The <c>tools/RoomsSample</c> option that rewrites it, for the message.</param>
    public static async Task AssertCheckedInAsync(string folder, IReadOnlyDictionary<string, byte[]> expected, string flag)
    {
        string root = Path.GetDirectoryName(RepoSourceFactAttribute.Find(folder + "/README.md")!)!;
        List<string> present = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "README.md"
                && !f.StartsWith("out/", StringComparison.Ordinal)
                && !f.StartsWith("comb/", StringComparison.Ordinal)
                && !f.StartsWith("maps/", StringComparison.Ordinal)
                && !(!f.Contains('/', StringComparison.Ordinal) && Path.GetExtension(f) is ".roompack" or ".roomnav" or ".db")
                && !(f.StartsWith("levels/", StringComparison.Ordinal) && Path.GetExtension(f) is not ".yaml"))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), present);
        foreach ((string path, byte[] bytes) in expected)
        {
            byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(root, path));
            Assert.True(
                bytes.AsSpan().SequenceEqual(onDisk),
                $"{folder}/{path} is not what the generator writes: run tools/RoomsSample {flag} {folder}");
        }
    }
}
