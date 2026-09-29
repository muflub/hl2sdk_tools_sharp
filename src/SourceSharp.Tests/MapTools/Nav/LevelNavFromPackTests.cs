//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Compile;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The map-first split a library host uses: a plan that gives the ids at
/// once, a build it awaits when it likes (and can cancel and run again), and
/// a write that replaces the file whole or not at all.
/// </summary>
public sealed class LevelNavFromPackTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private static readonly Guid Pack = Guid.Parse("11111111-2222-8333-8444-555555555555");

    private static byte[] LevelFile => Encoding.UTF8.GetBytes("level: two rooms\n");

    private RoomObject Room(string name, bool nav = true) =>
        nav ? fixture.Room(name) with { Nav = RoomNavTurns.Of(fixture.Nav(name)) } : fixture.Room(name);

    private LevelLayout Layout => fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));

    private LevelNavPlan Plan(bool nav = true, bool include = true) => LevelNavFromPack.Plan(
        Layout, 2, 1, name => Room(name, nav), Pack, LevelFile, LevelNavFromPack.IdOptions(include, LevelNavFromPack.DefaultCompression), include);

    [Fact]
    public async Task ThePlanGivesTheIdsAtOnceAndTheBuildStitchesTheLevelWithThem()
    {
        LevelNavPlan plan = Plan();
        Assert.True(plan.WritesNavigation);
        Assert.Null(plan.Warning);
        Assert.Equal(Pack, plan.PackId);
        Assert.Equal(RoomCompileIds.LevelId(Pack, LevelFile, LevelNavFromPack.IdOptions(true, LevelNavFromPack.DefaultCompression)), plan.LevelId);

        Nav3dLevel level = await plan.BuildAsync();
        Assert.Equal((Pack, plan.LevelId), (level.PackId, level.LevelId));
        Nav3dLevel again = await plan.BuildAsync();
        Assert.Equal(Nav3dWriter.Write(level), Nav3dWriter.Write(again));
    }

    [Fact]
    public async Task AskingForNoNavigationOrAPackWithoutItPlansNoBuild()
    {
        LevelNavPlan none = Plan(include: false);
        Assert.False(none.WritesNavigation);
        Assert.Null(none.Warning);
        Assert.NotEqual(Plan().LevelId, none.LevelId);
        Assert.Contains("the link asked for none", (await Assert.ThrowsAsync<InvalidOperationException>(() => none.BuildAsync())).Message, StringComparison.Ordinal);

        LevelNavPlan missing = Plan(nav: false);
        Assert.False(missing.WritesNavigation);
        Assert.StartsWith("the room pack holds no navigation for \"east\", \"west\";", missing.Warning, StringComparison.Ordinal);
        Assert.Contains(missing.Warning!, (await Assert.ThrowsAsync<InvalidOperationException>(() => missing.BuildAsync())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledBuildThrowsAndThePlanBuildsTheSameLevelAfterwards()
    {
        LevelNavPlan plan = Plan();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.BuildAsync(cancelled.Token));
        Assert.Equal(Nav3dWriter.Write(await Plan().BuildAsync()), Nav3dWriter.Write(await plan.BuildAsync()));
    }

    [Fact]
    public void APointInACappedDoorwayIsRefusedWhenPlanningBeforeAnyMapIsWritten()
    {
        AuthoredPoi sentry = new("42", new Vec3(248, 128, 16), 0f, true, 0f, "vantage", "", "cxry_sentry", ["standing"]);
        RoomNav east = RoomNavBuilder.Build(fixture.Definition("east"), fixture.Room("east").Bsp, [sentry], RoomRole.None, NavRoomsFixture.Settings);
        RoomObject room = fixture.Room("east") with { Nav = RoomNavTurns.Of(east) };
        LinkException refused = Assert.Throws<LinkException>(() => LevelNavFromPack.Plan(
            fixture.Layout(("east", 0, 0, 0)), 1, 1, _ => room, Pack, LevelFile, LevelNavFromPack.IdOptions(true, LevelNavFromPack.DefaultCompression)));
        Assert.Contains("info_poi 42 \"cxry_sentry\" (vantage) stands in the doorway of socket \"east\"", refused.Message, StringComparison.Ordinal);

        // Ids only: no navigation, so no check.
        Assert.False(LevelNavFromPack.Plan(fixture.Layout(("east", 0, 0, 0)), 1, 1, _ => room, Pack, LevelFile, [], includeNavigation: false).WritesNavigation);
    }

    [Fact]
    public async Task TheWriteReplacesTheFileWholeAndACancelledOrFailedOneLeavesThePreviousFile()
    {
        Nav3dLevel level = await Plan().BuildAsync();
        InMemoryFileSystem disk = new();
        VPath path = VPath.Create("out/level.nav3d");
        long length = await LevelNavPlan.WriteAsync(disk, path, level, LevelNavFromPack.DefaultCompression);
        byte[] first = disk.GetBytes(path)!;
        Assert.Equal(first.Length, length);
        Assert.Equal(NavCodec.Brotli, Nav3dReader.Open(first).Codec);

        // Cancelled before the write: the old file stands.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LevelNavPlan.WriteAsync(disk, path, level, NavCompression.None, cancelled.Token));
        Assert.Equal(first, disk.GetBytes(path));

        // Failed in the file system: likewise.
        ProbeFileSystem failing = new(disk) { BeforeReplace = _ => throw new IOException("no room") };
        await Assert.ThrowsAsync<IOException>(() => LevelNavPlan.WriteAsync(failing, path, level, NavCompression.None));
        Assert.Equal(first, disk.GetBytes(path));

        // Stored raw, the same level.
        Assert.Equal(Nav3dWriter.Write(level), Nav3dWriter.Write(Nav3dReader.Open(disk.GetBytes(path)!).ToLevel()));
        Assert.Equal((long)Nav3dWriter.Write(level).Length, await LevelNavPlan.WriteAsync(disk, path, level, NavCompression.None));
    }

    [Fact]
    public void TheDefaultCodecIsBrotliAndTheIdOptionsSpellIt()
    {
        Assert.Equal(new NavCompression(NavCodec.Brotli, 5), LevelNavFromPack.DefaultCompression);
        Assert.Equal(["nav", "nav-codec Brotli:5"], LevelNavFromPack.IdOptions(true, LevelNavFromPack.DefaultCompression));
        Assert.Equal(["no-nav", "nav-codec None:0"], LevelNavFromPack.IdOptions(false, NavCompression.None));
    }
}
