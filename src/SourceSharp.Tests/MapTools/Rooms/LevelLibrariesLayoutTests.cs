//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// What a host planning a level of several libraries (<c>ssmap layout</c>)
/// reads before any level exists: the level's singletons by the link's rule,
/// and the compatibility check over every library it may draw from.
/// </summary>
public sealed class LevelLibrariesLayoutTests
{
    private static VmfChunk Entity(string classname, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new("entity");
        entity.AddKey("classname", classname);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>
    /// One library's singletons are its own (the same entity list and
    /// options record); a sun, an option and a skybox only a later library
    /// has are the level's (D29), and the first library's stand where it has
    /// them.
    /// </summary>
    [Fact]
    public void TheLevelsSingletonsAreTheLinksRule()
    {
        VmfChunk fog = Entity("env_fog_controller", ("targetname", "fog"));
        VmfChunk sun = Entity("light_environment", ("angles", "-45 30 0"));
        RoomLibraryOptions first = RoomLibraryOptions.None;
        LevelLibraries.LibrarySingletons alone = new([fog], first, null);
        LevelLibraries.LevelSingletonChoice one = LevelLibraries.LevelSingletonsOf([alone]);
        Assert.Same(alone.Entities, one.Entities);
        Assert.Same(first, one.Options);
        Assert.Null(one.SkyboxSource);

        LevelLibraries.LevelSingletonChoice two = LevelLibraries.LevelSingletonsOf(
            [alone, new([sun, Entity("env_fog_controller", ("targetname", "fog"), ("fogcolor", "1 2 3"))], new RoomLibraryOptions(300), "sky")]);
        Assert.Equal([fog, sun], two.Entities);
        Assert.Equal(300, two.Options.EntityReserve);
        Assert.Equal(1, two.SkyboxSource);
        Assert.Equal(1, two.SunSource);
        Assert.Throws<ArgumentNullException>(() => LevelLibraries.LevelSingletonsOf(null!));
    }

    /// <summary>
    /// Every library a layout may draw from is held to the first's grid and
    /// kit by its first room, with 17.5's texts naming the paths as the level
    /// writes them; compatible libraries pass; the lists must match.
    /// </summary>
    [Fact]
    public void EveryCandidateLibraryIsHeldToOneGridAndKit()
    {
        VmfDocument a = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX));
        VmfDocument b = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("nave", RoomFacing.PositiveX) with { Height = 512 });
        VmfDocument wide = RoomHarness.LibraryVmf(new RoomDefinition("wide", 256, new SocketKit(128, 224, 16), [new RoomSocket(RoomFacing.PositiveX, "east")]));
        LevelLibrary[] keys = [new("a", "a.vmf"), new("b", "../b.vmf")];

        LevelLibraries.CheckCandidates(keys, [a, b]);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLibraries.CheckCandidates(keys, [a, wide]));
        Assert.Equal(
            "libraries a (a.vmf) and b (../b.vmf) have different door kits: door_width 96 against 128; the rooms of a level join through one kit.",
            refused.Message);
        Assert.Throws<ArgumentException>(() => LevelLibraries.CheckCandidates(keys, [a]));
        Assert.Throws<ArgumentNullException>(() => LevelLibraries.CheckCandidates(null!, [a]));
    }
}
