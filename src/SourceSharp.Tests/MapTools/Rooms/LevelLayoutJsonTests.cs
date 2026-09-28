//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The two JSON shapes of the room pipeline — the layout <c>ssmap link</c>
/// reads and the room definition <c>ssmap room</c> reads — written and read
/// back, and every refusal naming the field it refused.
/// </summary>
public sealed class LevelLayoutJsonTests
{
    private const string Layout = """
        { "name": "hubline", "cellSize": 256, "kit": { "width": 96, "height": 96, "depth": 16 },
          "rooms": [
            { "room": "hub", "cellX": 0, "cellY": 0, "rotation": 1,
              "joints": [ { "socket": "PositiveX", "neighborSocket": "NegativeX" } ],
              "capped": [ "NegativeX", "NegativeY" ] },
            { "room": "hub", "cellX": 1, "cellY": 0 } ] }
        """;

    // ---- the layout ---------------------------------------------------------

    [Fact]
    public void ALayoutReadsEveryField()
    {
        LevelLayout layout = LevelLayoutJson.Parse(Layout);

        Assert.Equal("hubline", layout.Name);
        Assert.Equal(256f, layout.CellSize);
        Assert.Equal(new SocketKit(96, 96, 16), layout.Kit);
        Assert.Equal(2, layout.Rooms.Count);
        Assert.Equal(new RoomPlacement("hub", 0, 0, 1), layout.Rooms[0].Placement);
        Assert.Equal([("PositiveX", "NegativeX")], layout.Rooms[0].Joints);
        Assert.Equal(["NegativeX", "NegativeY"], layout.Rooms[0].Capped);

        // Rotation, joints and caps are optional.
        Assert.Equal(new RoomPlacement("hub", 1, 0, 0), layout.Rooms[1].Placement);
        Assert.Empty(layout.Rooms[1].Joints);
        Assert.Empty(layout.Rooms[1].Capped);
        Assert.True(LevelLayoutJson.HasGrid(layout));
    }

    [Fact]
    public void ALayoutWritesWhatItReads()
    {
        LevelLayout layout = LevelLayoutJson.Parse(Layout);
        LevelLayout again = LevelLayoutJson.Parse(LevelLayoutJson.Write(layout));

        Assert.Equal(layout.Name, again.Name);
        Assert.Equal(layout.CellSize, again.CellSize);
        Assert.Equal(layout.Kit, again.Kit);
        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            Assert.Equal(layout.Rooms[i].Placement, again.Rooms[i].Placement);
            Assert.Equal(layout.Rooms[i].Joints, again.Rooms[i].Joints);
            Assert.Equal(layout.Rooms[i].Capped, again.Rooms[i].Capped);
        }
    }

    /// <summary>
    /// A layout without a grid is the library's grid: the parse leaves NaN
    /// sentinels, <see cref="LevelLayoutJson.HasGrid"/> says so, and a
    /// sentinel that reaches validation fails there.
    /// </summary>
    [Fact]
    public void ALayoutWithoutAGridCarriesSentinelsThatFailValidation()
    {
        LevelLayout layout = LevelLayoutJson.Parse("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0 } ] }""");

        Assert.False(LevelLayoutJson.HasGrid(layout));
        Assert.True(float.IsNaN(layout.CellSize));
        Assert.Throws<ArgumentOutOfRangeException>(layout.Validate);
    }

    [Theory]
    [InlineData("not json", "is not JSON")]
    [InlineData("[]", "is Array, expected an object")]
    [InlineData("""{ "name": "n" }""", "is missing rooms")]
    [InlineData("""{ "rooms": [] }""", "is missing name")]
    [InlineData("""{ "name": "n", "rooms": [], "extra": 1 }""", "unknown field \"extra\"")]
    [InlineData("""{ "name": 5, "rooms": [] }""", "field \"name\" is not a string")]
    [InlineData("""{ "name": "n", "cellSize": "big", "rooms": [] }""", "field \"cellSize\" is not a number")]
    [InlineData("""{ "name": "n", "rooms": {} }""", "rooms is not an array")]
    [InlineData("""{ "name": "n", "rooms": [ 1 ] }""", "room 0 is not an object")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0 } ] }""", "room 0 needs room, cellX and cellY")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0.5, "cellY": 0 } ] }""", "field \"rooms[0].cellX\" is not an integer")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": "0", "cellY": 0 } ] }""", "field \"rooms[0].cellX\" is not an integer")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "size": 1 } ] }""", "room 0 has an unknown field \"size\"")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "joints": 1 } ] }""", "room 0 joints is not an array")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "joints": [ "a" ] } ] }""", "joint 0 is not an object")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "joints": [ { "socket": "a" } ] } ] }""", "joint 0 names both of its sockets")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "joints": [ { "socket": "a", "x": "b" } ] } ] }""", "joint 0 has an unknown field \"x\"")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "capped": "a" } ] }""", "field \"rooms[0].capped\" is not an array")]
    [InlineData("""{ "name": "n", "rooms": [ { "room": "r", "cellX": 0, "cellY": 0, "capped": [ 1 ] } ] }""", "has a non-string element 1")]
    [InlineData("""{ "name": "n", "kit": 1, "rooms": [] }""", "kit is not an object")]
    [InlineData("""{ "name": "n", "kit": { "width": 1, "height": 1 }, "rooms": [] }""", "kit needs width, height and depth")]
    [InlineData("""{ "name": "n", "kit": { "width": 1, "height": 1, "depth": 1, "x": 1 }, "rooms": [] }""", "kit has an unknown field \"x\"")]
    public void ABadLayoutIsRefusedNamingTheField(string json, string expected)
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelLayoutJson.Parse(json));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    // ---- the room definition ------------------------------------------------

    [Fact]
    public void ARoomDefinitionWritesWhatItReads()
    {
        RoomDefinition definition = new("salle-é", 256, new SocketKit(96, 96, 16),
            [new RoomSocket(RoomFacing.PositiveX, "east"), new RoomSocket(RoomFacing.NegativeY, "south")]);

        RoomDefinition again = RoomDefinitionJson.Parse(RoomDefinitionJson.Write(definition));

        Assert.Equal(definition.Name, again.Name);
        Assert.Equal(definition.CellSize, again.CellSize);
        Assert.Equal(definition.Kit, again.Kit);
        Assert.Equal(definition.Sockets, again.Sockets);
    }

    [Theory]
    [InlineData("nope", "roomdef is not JSON")]
    [InlineData("1", "roomdef is Number, expected an object")]
    [InlineData("""{ "name": "r" }""", "roomdef needs name, cellSize, kit")]
    [InlineData("""{ "name": "r", "other": 1 }""", "roomdef has an unknown field \"other\"")]
    [InlineData("""{ "name": 1 }""", "roomdef field \"name\" is not a string")]
    [InlineData("""{ "cellSize": "x" }""", "roomdef field \"cellSize\" is not a number")]
    [InlineData("""{ "kit": [] }""", "roomdef kit is not an object")]
    [InlineData("""{ "kit": { "width": 1 } }""", "roomdef kit needs width, height and depth")]
    [InlineData("""{ "kit": { "width": 1, "y": 2 } }""", "roomdef kit has an unknown field \"y\"")]
    [InlineData("""{ "sockets": {} }""", "roomdef sockets is not an array")]
    [InlineData("""{ "sockets": [ 1 ] }""", "a roomdef socket is not an object")]
    [InlineData("""{ "sockets": [ { "facing": 7, "name": "a" } ] }""", "roomdef socket facing 7 is not a facing")]
    [InlineData("""{ "sockets": [ { "facing": "east", "name": "a" } ] }""", "roomdef socket facing east is not a facing")]
    [InlineData("""{ "sockets": [ { "name": "a" } ] }""", "a roomdef socket needs both facing and name")]
    [InlineData("""{ "sockets": [ { "facing": 0, "name": "a", "z": 0 } ] }""", "a roomdef socket has an unknown field \"z\"")]
    public void ABadRoomDefinitionIsRefusedNamingTheField(string json, string expected)
    {
        LinkException refused = Assert.Throws<LinkException>(() => RoomDefinitionJson.Parse(json));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }
}
