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
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The 3x3 rooms sample through the CLI, as its README runs it, on an
/// in-memory disk: <c>ssmap room</c> on the library, <c>ssmap link</c> on
/// the sample's level files, <c>ssmap link --flatten</c> and
/// <c>ssmap vbsp</c> on the reference, <c>ssmap layout</c> for the seeded
/// levels.
/// </summary>
public sealed class Rooms3x3CommandsTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    /// <summary>
    /// <c>ssmap room</c> compiles every room of the library against the
    /// sample's own game folder into one pack; <c>ssmap link</c> links every
    /// level file of the sample from that pack without recompiling; each
    /// map it writes is byte for byte the map the linker API makes from the
    /// rooms the equivalence facts compile, passes the loader validation,
    /// and carries the door graph's visibility.
    /// </summary>
    [Fact]
    public async Task RoomThenLinkWritesTheMapsTheEquivalenceFactsCheck()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], ["/sample/rooms.vmf", "-game", "/sample", "-out", "/sample/rooms.roompack"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        IReadOnlyDictionary<string, RoomEntityCounts> counts;
        using (MemoryStream pack = new(fs.GetBytes(VPath.Create(Rooted("/sample/rooms.roompack")))!))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
            Assert.Equal(Rooms3x3Kit.Kinds.Select(k => k.Name), index.Entries.Select(e => e.Name));

            // The counts the pack stores are the counts of the rooms it holds.
            IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(pack, index, [.. index.Entries.Select(e => e.Name)]);
            Assert.All(rooms, room => Assert.Equal(RoomEntityCounts.Of(room.Bsp).Classes, room.EntityCounts!.Classes));
            pack.Position = 0;
            counts = await RoomPack.ReadEntityCountsAsync(pack, await RoomPack.ReadIndexAsync(pack));
        }

        foreach (string name in Levels())
        {
            using StringWriter linkOutput = new();
            exit = await RoomCommands.RunLinkAsync(
                fs,
                [$"/sample/levels/{name}.yaml", "-rooms", "/sample/rooms.roompack", "-out", $"/sample/out/{name}.bsp"],
                linkOutput);
            Assert.True(exit == Program.ExitSuccess, linkOutput.ToString());

            // The entity budget: the linked lump holds exactly what the pack's
            // counts predict for the level, and the headroom line says so.
            byte[] written = fs.GetBytes(VPath.Create(Rooted($"/sample/out/{name}.bsp")))!;
            LevelGrid level = LevelYaml.Parse(Encoding.UTF8.GetString(fs.GetBytes(VPath.Create(Rooted($"/sample/levels/{name}.yaml")))!), name);
            long predicted = 1 + level.Placed.Sum(p => counts[p.Cell.Room].Tally(EntityClassTable.Default).Listed);
            using (MemoryStream linkedMap = new(written))
            {
                Assert.Equal(predicted, EntityLump.Parse((await BspFile.LoadAsync(linkedMap))[BspLump.Entities]).Count);
            }

            Assert.StartsWith(
                $"ssmap link: map entities {predicted} / budget 1536 (reserve 512, cap 2048); {predicted} entities in the entity list",
                linkOutput.ToString(),
                StringComparison.Ordinal);
            Rooms3x3Pair pair = await fixture.PairAsync(name);
            using MemoryStream api = new();
            await BspFile.SaveAsync(pair.Linked.Bsp, api, BspWriteMode.Canonical, CancellationToken.None);
            Assert.True(api.ToArray().AsSpan().SequenceEqual(written), $"{name}: ssmap link and the linker API wrote different maps");

            using MemoryStream stream = new(written);
            BspData map = await BspFile.LoadAsync(stream);
            ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
            Assert.True(report.ErrorCount == 0, string.Join("; ", report.Diagnostics));
        }

        Rooms3x3Pair sample = await fixture.PairAsync(Rooms3x3Permutations.LevelName);
        DoorGraphFacts.AssertDoorGraph(sample.Linked, sample.Layout, fixture.Library);

        // With the pack beside the library, ssmap layout budgets every level
        // (cap - reserve by default), and the sample's seeded levels, far
        // under it, come out as they always did.
        await AssertSeededLevelsAsync(fs);
    }

    /// <summary>
    /// <c>ssmap link --flatten</c> writes the reference VMF the equivalence
    /// facts compile, byte for byte; <c>ssmap vbsp</c> compiles it against
    /// the sample's game folder without leaking; and the map it writes
    /// agrees with the linked map at every point of the equivalence facts'
    /// lattice.
    /// </summary>
    [Fact]
    public async Task TheFlattenedReferenceCompilesWithVbspAndAgreesWithTheLink()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        int exit = await RoomCommands.RunLinkAsync(
            fs, ["/sample/levels/rooms3x3.yaml", "--flatten", "-out", "/sample/maps/rooms3x3.vmf"], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        Rooms3x3Pair pair = await fixture.PairAsync(Rooms3x3Permutations.LevelName);
        Assert.Equal(pair.Flattened.ToBytes(), fs.GetBytes(VPath.Create(Rooted("/sample/maps/rooms3x3.vmf"))));

        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        exit = await VbspCommand.RunAsync(fs, ["/sample/maps/rooms3x3.vmf"], cooker, output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted("/sample/maps/rooms3x3.bsp")))!);
        BspData reference = await BspFile.LoadAsync(stream);
        Assert.Equal(0, (await BspValidator.CheckAsync(reference, CancellationToken.None)).ErrorCount);

        LevelProbe whole = new(reference);
        LevelProbe linked = pair.LinkedProbe;
        foreach (Vec3 p in Rooms3x3EquivalenceTests.Lattice())
        {
            Assert.True(whole.Contents(p) == linked.Contents(p), $"({p.X} {p.Y} {p.Z})");
        }
    }

    /// <summary>
    /// The sample's seeded level files are exactly what
    /// <c>ssmap layout rooms.vmf -rows 3 -columns 3 -seed N</c> writes for
    /// their seeds, run on the sample's own library.
    /// </summary>
    [Fact]
    public async Task TheSampleSeededLevelsAreWhatSsmapLayoutWrites() => await AssertSeededLevelsAsync(Sample());

    /// <summary>
    /// Runs <c>ssmap layout</c> for each of the sample's seeds and holds the
    /// level it writes to the checked-in one.
    /// </summary>
    private static async Task AssertSeededLevelsAsync(InMemoryFileSystem fs)
    {
        foreach (string name in Rooms3x3Permutations.SampleSeeds)
        {
            (_, ulong seed, double empty) = Rooms3x3Permutations.Seeds.Single(s => s.Name == name);
            List<string> args =
            [
                "/sample/rooms.vmf", "-rows", "3", "-columns", "3", "-seed", seed.ToString(CultureInfo.InvariantCulture),
                "-out", $"/sample/generated/{name}.yaml",
            ];
            if (empty > 0)
            {
                args.AddRange(["-empty", empty.ToString(CultureInfo.InvariantCulture)]);
            }

            using StringWriter output = new();
            Assert.Equal(Program.ExitSuccess, await RoomCommands.RunLayoutAsync(fs, args, output));
            Assert.Equal(
                fs.GetBytes(VPath.Create(Rooted($"/sample/levels/{name}.yaml"))),
                fs.GetBytes(VPath.Create(Rooted($"/sample/generated/{name}.yaml"))));
        }
    }

    /// <summary>The sample's level files, by base name.</summary>
    private static IEnumerable<string> Levels() =>
        Rooms3x3Sample.Build().Keys
            .Where(k => k.StartsWith("levels/", StringComparison.Ordinal))
            .Select(k => Path.GetFileNameWithoutExtension(k));

    /// <summary>The sample's generated files, under <c>/sample</c> as the host resolves it.</summary>
    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
        }

        return fs;
    }

    /// <summary>
    /// Where the commands look for a rooted path they are given: they resolve
    /// against the host (<c>/sample</c> is <c>D:\sample</c> on Windows).
    /// </summary>
    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;
}
