//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3×3 sample's levels link to the same bytes from a room pack that
/// carries the link work done ahead (<see cref="RoomLinkData"/>) as from the
/// compiled rooms without it, which is how the equivalence facts link them.
/// </summary>
public sealed class Rooms3x3LinkDataTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    /// <summary>The sample's default cases (its levels and turned arrangements).</summary>
    public static TheoryData<string> Cases => Rooms3x3Fixture.CaseNames;

    /// <summary>
    /// Each case links byte for byte the same from the pack as from the
    /// rooms: the pack loaded as <c>ssmap link</c> loads it (only the placed
    /// turns), the rooms as compiled, which compute the same data at link.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ALevelLinksTheSameFromAPackWithLinkData(string name)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        byte[] expected = await LinkBytesAsync(layout, fixture.Library, name);

        List<RoomPackItem> items = [];
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        // The library's settings go in the pack and come back out, as
        // ssmap room and ssmap link carry them (the library's mapversion is
        // among them).
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([fixture.Library.Options.ToSection()!.Value], items, pack, CancellationToken.None);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        RoomLibraryOptions options = await RoomPack.ReadLibraryOptionsAsync(pack, index);
        pack.Position = 0;
        index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(
            pack,
            index,
            [.. layout.Rooms.GroupBy(r => r.Placement.Room, r => r.Placement.Rotation).Select(g => new RoomPackRequest(g.Key, [.. g]))]);
        Assert.All(rooms, r => Assert.NotNull(r.Link));

        RoomLibrary packed = new(fixture.Library.Kit, fixture.Library.CellSize) { Options = options };
        foreach (RoomObject room in rooms)
        {
            packed.Add(room);
        }

        Assert.Equal(expected, await LinkBytesAsync(layout, packed, name));
    }

    private async Task<byte[]> LinkBytesAsync(LevelLayout layout, RoomLibrary library, string name)
    {
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, library, fixture.Context(name));
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
