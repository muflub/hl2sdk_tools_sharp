//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// The 3x3 rooms sample through the CLI, as its README runs it, on an
/// in-memory disk: <c>ssmap room</c> on each room, <c>ssmap link</c> on the
/// sample level and its turns, <c>ssmap vbsp</c> on the reference.
/// </summary>
public sealed class Rooms3x3CommandsTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    /// <summary>
    /// Every room compiles with <c>ssmap room</c> against the sample's own
    /// game folder; <c>ssmap link</c> links the sample level and its three
    /// turns from the same room files without recompiling them; each map it
    /// writes is byte for byte the map the linker API makes from the rooms the
    /// equivalence facts compile, passes the loader validation, and carries
    /// the door graph's visibility.
    /// </summary>
    [Fact]
    public async Task RoomThenLinkWritesTheMapsTheEquivalenceFactsCheck()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            int exit = await RoomCommands.RunRoomAsync(fs, [], [$"/sample/maps/{kind.Name}.vmf", "-out", "/sample/rooms"], output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());
        }

        for (int turns = 0; turns < 4; turns++)
        {
            string name = Rooms3x3Permutations.TurnName(turns);
            int exit = await RoomCommands.RunLinkAsync(
                fs,
                [$"/sample/layouts/{name}.json", "-rooms", "/sample/rooms", "-out", $"/sample/out/{name}.bsp"],
                output);
            Assert.True(exit == Program.ExitSuccess, output.ToString());

            byte[] written = fs.GetBytes(VPath.Create(Rooted($"/sample/out/{name}.bsp")))!;
            Rooms3x3Pair pair = await fixture.PairAsync(name);
            using MemoryStream api = new();
            await BspFile.SaveAsync(pair.Linked.Bsp, api, BspWriteMode.Canonical, CancellationToken.None);
            Assert.True(api.ToArray().AsSpan().SequenceEqual(written), $"{name}: ssmap link and the linker API wrote different maps");

            using MemoryStream stream = new(written);
            BspData map = await BspFile.LoadAsync(stream);
            ValidationReport report = await BspValidator.CheckAsync(map, CancellationToken.None);
            Assert.True(report.ErrorCount == 0, string.Join("; ", report.Diagnostics));
        }

        DoorGraphFacts.AssertDoorGraph((await fixture.PairAsync(Rooms3x3Permutations.LevelName)).Linked,
            (await fixture.PairAsync(Rooms3x3Permutations.LevelName)).Layout, fixture.Library);
    }

    /// <summary>
    /// <c>ssmap vbsp</c> compiles the reference VMF against the sample's game
    /// folder without leaking, and the map it writes agrees with the linked
    /// map at every point of the equivalence facts' lattice.
    /// </summary>
    [Fact]
    public async Task TheReferenceCompilesWithVbspAndAgreesWithTheLink()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        int exit = await VbspCommand.RunAsync(fs, ["/sample/maps/rooms3x3.vmf"], cooker, output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());

        using MemoryStream stream = new(fs.GetBytes(VPath.Create(Rooted("/sample/maps/rooms3x3.bsp")))!);
        BspData reference = await BspFile.LoadAsync(stream);
        Assert.Equal(0, (await BspValidator.CheckAsync(reference, CancellationToken.None)).ErrorCount);

        LevelProbe whole = new(reference);
        LevelProbe linked = (await fixture.PairAsync(Rooms3x3Permutations.LevelName)).LinkedProbe;
        foreach (Vec3 p in Rooms3x3EquivalenceTests.Lattice())
        {
            Assert.True(whole.Contents(p) == linked.Contents(p), $"({p.X} {p.Y} {p.Z})");
        }
    }

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
