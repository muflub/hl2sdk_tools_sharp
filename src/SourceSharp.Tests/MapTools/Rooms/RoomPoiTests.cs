//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Points of interest in a library, room-local names, room roles and the
/// compile ids: read, checked, resolved and derived.
/// </summary>
public sealed class RoomPoiTests
{
    internal static VmfChunk PoiEntity(Vec3 at, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", "4242");
        entity.AddKey("classname", RoomPois.Entity);
        entity.AddKey("origin", VmfPlacement.Format(at));
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    private static VmfDocument Room(params VmfChunk[] entities)
    {
        VmfDocument document = RoomHarness.BuildRoomModel(RoomHarness.WalkableRoom("r", RoomFacing.PositiveX));
        foreach (VmfChunk entity in entities)
        {
            document.Chunks.Add(entity);
        }

        return document;
    }

    [Fact]
    public void PointsAreTakenOutOfTheRoomWithTheirKeys()
    {
        VmfDocument room = Room(
            PoiEntity(new Vec3(100, 110, 16), ("poi_type", "vantage"), ("poi_tags", "high, north"), ("angles", "0 -90 0"),
                ("poi_radius", "48"), ("poi_agents", "standing, flyer"), ("targetname", "cxry_watch")),
            PoiEntity(new Vec3(1, 2, 3)));
        (VmfDocument stripped, IReadOnlyList<AuthoredPoi> pois) = RoomPois.Extract(room);
        Assert.DoesNotContain(stripped.GetChunks(MapFileLoader.EntityChunk), RoomPois.IsPoi);
        Assert.Equal(room.Chunks.Count - 2, stripped.Chunks.Count);
        Assert.Equal(new AuthoredPoi("4242", new Vec3(100, 110, 16), 270f, true, 48f, "vantage", "high, north", "cxry_watch", pois[0].Agents),
            pois[0]);
        Assert.Equal(["standing", "flyer"], pois[0].Agents);
        Assert.Equal((RoomPois.DefaultType, "", false, 0f, (string?)null), (pois[1].Type, pois[1].Tags, pois[1].HasFacing, pois[1].Radius, pois[1].Name));

        VmfDocument plain = Room();
        Assert.Same(plain, RoomPois.Extract(plain).Stripped);
    }

    [Theory]
    [InlineData("angles", "0 x 0", "not pitch, yaw and roll")]
    [InlineData("poi_type", "door", "reserved")]
    [InlineData("poi_radius", "-1", "0 or more")]
    [InlineData("targetname", "CXRY_a", "placeholder")]
    [InlineData("targetname", "c1r2_a", "looks like a resolved")]
    [InlineData("poi_type", "arrival", "without angles")]
    public void AMalformedPointIsRefusedNamingItsEntity(string key, string value, string expected)
    {
        RoomLintException refused = Assert.Throws<RoomLintException>(() => RoomPois.Extract(Room(PoiEntity(new Vec3(1, 2, 3), (key, value)))));
        Assert.Contains("info_poi 4242", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APointWithoutAnOriginIsRefused()
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("classname", "info_poi");
        Assert.Throws<RoomLintException>(() => RoomPois.Extract(Room(entity)));
    }

    [Theory]
    [InlineData(null, RoomRole.None)]
    [InlineData("", RoomRole.None)]
    [InlineData("none", RoomRole.None)]
    [InlineData("Up", RoomRole.Up)]
    [InlineData(" down ", RoomRole.Down)]
    public void ARolesSpellingIsRead(string? value, RoomRole role) => Assert.Equal(role, RoomPois.ParseRole(value));

    [Fact]
    public void ARoomsRoleComesFromItsInfoRoomAndABadOneIsRefused()
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("a", RoomFacing.PositiveX), RoomHarness.WalkableRoom("b", RoomFacing.NegativeX));
        library.GetChunks(MapFileLoader.EntityChunk).First(e => e.GetValue("name") == "a").AddKey(RoomPois.RoleKey, "up");
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Assert.Equal((RoomRole.Up, RoomRole.None), (rooms[0].Role, rooms[1].Role));

        library.GetChunks(MapFileLoader.EntityChunk).First(e => e.GetValue("name") == "b").AddKey(RoomPois.RoleKey, "sideways");
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.Split(library));
        Assert.Contains("room \"b\"", refused.Message, StringComparison.Ordinal);
        Assert.Contains("up, down or none", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlattenedLevelLeavesPointsOut()
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("a", RoomFacing.PositiveX));
        library.Chunks.Add(PoiEntity(new Vec3(100, 100, 16)));
        Assert.Single(RoomPois.Extract(RoomLibraryVmf.Split(library)[0].Document).Pois);
        LevelGrid level = new("l", "lib.vmf", 1, 1, [new LevelCell("a", 0)]);
        VmfDocument flat = LevelFlattener.Flatten(level, library);
        Assert.DoesNotContain(flat.GetChunks(MapFileLoader.EntityChunk), RoomPois.IsPoi);
    }

    [Theory]
    [InlineData("guard", 3, 5, 0, "guard")]
    [InlineData("cxry_guard", 3, 5, 0, "c3r5_guard")]
    [InlineData("cx+1ry_guard", 3, 5, 0, "c4r5_guard")]
    [InlineData("cx+1ry_guard", 3, 5, 1, "c3r6_guard")]
    [InlineData("cx+1ry_guard", 3, 5, 2, "c2r5_guard")]
    [InlineData("cx+1ry_guard", 3, 5, 3, "c3r4_guard")]
    [InlineData("cxry-1_Door*", 0, 1, 0, "c0r0_Door*")]
    [InlineData("cx-1ry+1_a", 3, 5, 1, "c2r4_a")]
    [InlineData("door_cxry", 3, 5, 0, "door_cxry")]
    public void ALocalNameResolvesForItsCellAndTurn(string name, int column, int row, int turns, string resolved)
    {
        Assert.Equal(resolved, RoomLocalNames.Resolve(name, column, row, turns));
        Assert.Equal(name.StartsWith("cx", StringComparison.Ordinal), RoomLocalNames.IsLocal(name));
    }

    [Theory]
    [InlineData("cXry_a", "placeholder")]
    [InlineData("cx+2ry_a", "placeholder")]
    [InlineData("cx+ry_a", "placeholder")]
    [InlineData("cxry_", "nothing after")]
    [InlineData("C3R5_a", "resolved")]
    [InlineData("c-1r0_a", "placeholder")]
    [InlineData("c1rx_a", "placeholder")]
    [InlineData("cx+1ry_", "nothing after")]
    public void AMalformedOrReservedNameIsRefused(string name, string expected)
    {
        Assert.Contains(expected, RoomLocalNames.Problem(name), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RoomLocalNames.Resolve(name, 0, 0, 0));
    }

    /// <summary>
    /// Names that begin with <c>c</c> but not like the reserved family: a
    /// <c>c</c> then neither <c>x</c> nor a (signed) digit, or no <c>r</c>
    /// before the first underscore (the grammar's suspect pattern).
    /// </summary>
    [Theory]
    [InlineData("c")]
    [InlineData("cr_x")]
    [InlineData("c1_rx")]
    [InlineData("cx")]
    [InlineData("c-r_a")]
    public void NamesThatOnlyStartLikeOneAreGlobal(string name) => Assert.Null(RoomLocalNames.Problem(name));

    [Fact]
    public void CompileIdsAreDeterministicAndChangeWithTheirInputs()
    {
        byte[] library = "library bytes"u8.ToArray();
        Guid pack = RoomCompileIds.PackId(library, ["-v"], "nav");
        Assert.Equal(pack, RoomCompileIds.PackId(library, ["-v"], "nav"));
        Assert.NotEqual(pack, RoomCompileIds.PackId("library bytez"u8, ["-v"], "nav"));
        Assert.NotEqual(pack, RoomCompileIds.PackId(library, ["-v", "-x"], "nav"));
        Assert.NotEqual(pack, RoomCompileIds.PackId(library, ["-v"], null));
        Assert.Equal(8, pack.Version);
        Assert.Equal(0x80, RoomCompileIds.ToBytes(pack)[8] & 0xC0);

        Guid level = RoomCompileIds.LevelId(pack, "level"u8, ["nav"]);
        Assert.Equal(level, RoomCompileIds.LevelId(pack, "level"u8, ["nav"]));
        Assert.NotEqual(level, RoomCompileIds.LevelId(Guid.Empty, "level"u8, ["nav"]));
        Assert.NotEqual(level, RoomCompileIds.LevelId(pack, "level2"u8, ["nav"]));
        Assert.NotEqual(level, RoomCompileIds.LevelId(pack, "level"u8, ["no-nav"]));

        Assert.Equal(pack, RoomCompileIds.FromBytes(RoomCompileIds.ToBytes(pack)));
        Assert.Equal(RoomCompileIds.ToBytes(pack), RoomCompileIds.Section(pack).Bytes.ToArray());
        Assert.Equal("CMPL", RoomCompileIds.Section(pack).Tag);
        Assert.Throws<LinkException>(() => RoomCompileIds.FromBytes(new byte[15]));
    }

    [Fact]
    public void TheIdsAreStampedIntoTheWorldspawnAndReadBack()
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Entities, EntityLump.Write([new BspEntity { Pairs = { new("classname", "worldspawn"), new(RoomCompileIds.LevelIdKey, "old") } }]).Data, 0);
        Guid pack = Guid.Parse("11111111-1111-8111-8111-111111111111");
        Guid level = Guid.Parse("22222222-2222-8222-8222-222222222222");
        RoomCompileIds.Stamp(bsp, pack, level);
        BspEntity world = EntityLump.Parse(bsp[BspLump.Entities])[0];
        Assert.Equal(level.ToString(), world.Get(RoomCompileIds.LevelIdKey));
        Assert.Equal(pack.ToString(), world.Get(RoomCompileIds.PackIdKey));
        Assert.Single(world.Pairs, p => p.Key == RoomCompileIds.LevelIdKey);
        Assert.Equal(level, RoomCompileIds.LevelIdOf(bsp));

        RoomCompileIds.Stamp(bsp, null, level);
        Assert.Null(EntityLump.Parse(bsp[BspLump.Entities])[0].Get(RoomCompileIds.PackIdKey));

        BspData empty = new();
        Assert.Null(RoomCompileIds.LevelIdOf(empty));
        Assert.Throws<LinkException>(() => RoomCompileIds.Stamp(empty, pack, level));
    }
}
