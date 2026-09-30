//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Moving VMF brushes and entities by a quarter turn and an offset: the
/// arithmetic the library split and the flattened reference are made of.
/// </summary>
public sealed class VmfPlacementTests
{
    // ---- the transform -------------------------------------------------------------

    /// <summary>
    /// A placement's quarter turn is the linker's transform exactly, point
    /// for point, for every turn count including ones outside 0..3.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(5)]
    public void AQuarterTurnIsThePlacementsTransform(int turns)
    {
        RoomTransform linker = new(new RoomPlacement("r", 2, -1, turns), 256);
        QuarterTurn turn = QuarterTurn.Of(linker);
        foreach (Vec3 p in new[] { Vec3.Zero, new Vec3(16, 80, 17), new Vec3(256, 240, 256), new Vec3(-3, 7.5f, 1) })
        {
            Assert.Equal(linker.Apply(p), turn.Apply(p));
        }
    }

    [Fact]
    public void ADirectionTurnsAndAYawTurnsWithIt()
    {
        QuarterTurn quarter = new(1, new Vec3(100, 0, 0));
        Assert.Equal(new Vec3(0, 1, 5), quarter.Rotate(new Vec3(1, 0, 5)));
        Assert.Equal(new Vec3(-1, 0, 0), new QuarterTurn(2, Vec3.Zero).Rotate(new Vec3(1, 0, 0)));
        Assert.Equal(new Vec3(0, -1, 0), new QuarterTurn(3, Vec3.Zero).Rotate(new Vec3(1, 0, 0)));
        Assert.Equal(new Vec3(100, 1, 0), quarter.Apply(new Vec3(1, 0, 0)));
        Assert.Equal(90f, quarter.TurnYaw(0));
        Assert.Equal(0f, quarter.TurnYaw(270));
        Assert.Equal(0f, new QuarterTurn(1, Vec3.Zero).TurnYaw(-90));
        Assert.Equal(270f, new QuarterTurn(-2, Vec3.Zero).TurnYaw(-270));
        Assert.Equal(new QuarterTurn(0, new Vec3(1, 2, 3)), QuarterTurn.Translation(new Vec3(1, 2, 3)));
    }

    // ---- brushes ----------------------------------------------------------------------

    /// <summary>A brush's box is the bounds of its sides' plane points.</summary>
    [Fact]
    public void ABrushIsMeasuredFromItsPlanePoints()
    {
        Box box = VmfPlacement.Bounds(Solid());
        Assert.Equal(new Vec3(0, 0, 0), box.Mins);
        Assert.Equal(new Vec3(64, 32, 16), box.Maxs);
    }

    /// <summary>A brush moved has every plane point moved and nothing else about it changed but its texture axes.</summary>
    [Fact]
    public void AMovedBrushHasItsPlanesMoved()
    {
        VmfChunk solid = Solid();
        solid.AddChunk("editor").AddKey("color", "0 255 0");
        QuarterTurn turn = new(1, new Vec3(256, 512, 0));

        VmfChunk moved = VmfPlacement.MoveSolid(solid, turn);

        Box box = VmfPlacement.Bounds(moved);
        Assert.Equal(new Vec3(224, 512, 0), box.Mins);
        Assert.Equal(new Vec3(256, 576, 16), box.Maxs);
        Assert.Equal("(224 512 16) (224 576 16) (256 576 16)", moved.Chunks.First().GetValue("plane"));
        Assert.Equal("7", moved.GetValue("id"));
        Assert.Equal("0 255 0", moved.GetChunk("editor")!.GetValue("color"));
        Assert.Equal("unit/plain", moved.Chunks.First().GetValue("material"));

        // A copy: the source is untouched.
        Assert.Equal("(0 32 16) (64 32 16) (64 0 16)", solid.Chunks.First().GetValue("plane"));
    }

    /// <summary>
    /// A texture stays where it was on the brush: a texel's coordinate at a
    /// point of the brush is the same as at the moved point on the moved
    /// brush, the axis turned with the brush and its shift corrected.
    /// </summary>
    [Fact]
    public void ATextureStaysWhereItWasOnTheBrush()
    {
        QuarterTurn turn = new(3, new Vec3(96, 1024, 32));
        VmfChunk side = VmfPlacement.MoveSolid(Solid(), turn).Chunks.First();

        (Vec3 axis, float shift, float scale) before = Axis("[1 0 0 8] 0.25");
        (Vec3 axis, float shift, float scale) after = Axis(side.GetValue("uaxis")!);
        Assert.Equal(new Vec3(0, -1, 0), after.axis);
        foreach (Vec3 p in new[] { new Vec3(0, 0, 16), new Vec3(32, 64, 16), new Vec3(12, 4, 16) })
        {
            float u = (Vec3.Dot(p, before.axis) / before.scale) + before.shift;
            float moved = (Vec3.Dot(turn.Apply(p), after.axis) / after.scale) + after.shift;
            Assert.Equal(u, moved, 3);
        }

        // A zero scale divides nothing: the shift is left as it was.
        VmfChunk zero = Solid();
        zero.Chunks.First().Keys.Single(k => k.Name == "uaxis").Value = "[1 0 0 8] 0";
        Assert.Equal("[0 -1 0 8] 0", VmfPlacement.MoveSolid(zero, turn).Chunks.First().GetValue("uaxis"));
    }

    /// <summary>What a brush cannot be moved or measured with is refused naming the brush.</summary>
    [Theory]
    [InlineData("no sides", "brush 7 has no sides")]
    [InlineData("no plane", "brush 7 has a side with no plane")]
    [InlineData("two points", "brush 7 has a plane \"(0 0 0) (1 0 0)\", not three points")]
    [InlineData("two numbers", "brush 7: plane \"0 0\" is not three numbers")]
    [InlineData("junk number", "brush 7: plane \"x\" is not a number")]
    public void AMalformedBrushIsRefused(string fault, string expected)
    {
        VmfChunk solid = Solid();
        VmfChunk side = solid.Chunks.First();
        VmfKey plane = side.Keys.Single(k => k.Name == "plane");
        switch (fault)
        {
            case "no sides":
                solid.Children.Clear();
                solid.AddKey("id", "7");
                break;
            case "no plane":
                side.Children.Remove(plane);
                break;
            case "two points":
                plane.Value = "(0 0 0) (1 0 0)";
                break;
            case "two numbers":
                plane.Value = "(0 0) (1 0 0) (1 1 0)";
                break;
            case "junk number":
                plane.Value = "(x 0 0) (1 0 0) (1 1 0)";
                break;
        }

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => VmfPlacement.Bounds(solid));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A texture axis that is not <c>[x y z shift] scale</c> is refused when moving.</summary>
    [Theory]
    [InlineData("1 0 0 0 0.25", "has a texture axis \"1 0 0 0 0.25\", not \"[x y z shift] scale\"")]
    [InlineData("[1 0 0] 0.25", "not \"[x y z shift] scale\"")]
    [InlineData("[1 0 0 0]", "not \"[x y z shift] scale\"")]
    [InlineData("[1 0 z 0] 0.25", "brush 7: texture axis \"z\" is not a number")]
    public void AnUnmovableSideIsRefused(string axis, string expected)
    {
        VmfChunk solid = Solid();
        VmfChunk side = solid.Chunks.First();
        side.Keys.Single(k => k.Name == "uaxis").Value = axis;

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => VmfPlacement.MoveSolid(solid, new QuarterTurn(1, Vec3.Zero)));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A displacement moves with its side (the rooms design, 4.5): its start
    /// position through the whole move, its normals, offsets and offset
    /// normals turned as directions, a negative zero written as zero, and
    /// every other key and row (distances, alphas, triangle tags, allowed
    /// vertices, power, flags) copied as written; the source is untouched.
    /// </summary>
    [Theory]
    [InlineData(0, "[272 520 16]", "0 0 1 1 0 0", "0.5 -0.25 3 0 0 0")]
    [InlineData(1, "[248 528 16]", "0 0 1 0 1 0", "0.25 0.5 3 0 0 0")]
    [InlineData(2, "[240 504 16]", "0 0 1 -1 0 0", "-0.5 0.25 3 0 0 0")]
    [InlineData(3, "[264 496 16]", "0 0 1 0 -1 0", "-0.25 -0.5 3 0 0 0")]
    public void ADisplacementMovesWithItsSide(int rotation, string start, string normals, string offsets)
    {
        VmfChunk solid = Solid();
        VmfChunk disp = DispInfo(solid.Chunks.First());
        QuarterTurn turn = new(rotation, new Vec3(256, 512, 0));

        VmfChunk moved = VmfPlacement.MoveSolid(solid, turn).Chunks.First().GetChunk("dispinfo")!;

        Assert.Equal(start, moved.GetValue("startposition"));
        Assert.Equal(normals, moved.GetChunk("normals")!.GetValue("row0"));
        Assert.Equal(offsets, moved.GetChunk("offsets")!.GetValue("row0"));
        Assert.Equal(normals, moved.GetChunk("offset_normals")!.GetValue("row0"));
        Assert.Equal("2", moved.GetValue("power"));
        Assert.Equal("0", moved.GetValue("flags"));
        Assert.Equal("[1 0 0]", moved.GetValue("uaxis"));
        Assert.Equal("4 5", moved.GetChunk("distances")!.GetValue("row0"));
        Assert.Equal("0 255", moved.GetChunk("alphas")!.GetValue("row0"));
        Assert.Equal("9 9", moved.GetChunk("triangle_tags")!.GetValue("row0"));
        Assert.Equal("-1 -1", moved.GetChunk("allowed_verts")!.GetValue("10"));
        Assert.Equal("0 0 1 1 0 0", disp.GetChunk("normals")!.GetValue("row0"));
        Assert.Equal("[16 8 16]", disp.GetValue("startposition"));
    }

    /// <summary>A displacement whose start position or rows cannot be read is refused naming its brush.</summary>
    [Theory]
    [InlineData("start", "16 8 16", "brush 7 has a displacement startposition \"16 8 16\", not \"[x y z]\"")]
    [InlineData("start", "[16 8]", "brush 7: startposition \"16 8\" is not three numbers")]
    [InlineData("row", "0 0 1 1 0", "brush 7 has a displacement normals row0 of 5 numbers, not three per vertex")]
    [InlineData("row", "0 0 1 1 0 x", "brush 7: normals \"x\" is not a number")]
    public void AnUnreadableDisplacementIsRefused(string fault, string value, string expected)
    {
        VmfChunk solid = Solid();
        VmfChunk disp = DispInfo(solid.Chunks.First());
        if (fault == "start")
        {
            disp.Keys.Single(k => k.Name == "startposition").Value = value;
        }
        else
        {
            disp.GetChunk("normals")!.Keys.Single().Value = value;
        }

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => VmfPlacement.MoveSolid(solid, new QuarterTurn(1, Vec3.Zero)));
        Assert.Equal(expected + ".", refused.Message);
    }

    /// <summary>A <c>dispinfo</c> on a side, two vertices a row as far as these facts read it.</summary>
    private static VmfChunk DispInfo(VmfChunk side)
    {
        VmfChunk disp = side.AddChunk("dispinfo");
        disp.AddKey("power", "2");
        disp.AddKey("startposition", "[16 8 16]");
        disp.AddKey("flags", "0");
        disp.AddKey("uaxis", "[1 0 0]");
        disp.AddChunk("normals").AddKey("row0", "0 0 1 1 0 0");
        disp.AddChunk("distances").AddKey("row0", "4 5");
        disp.AddChunk("offsets").AddKey("row0", "0.5 -0.25 3 0 0 0");
        disp.AddChunk("offset_normals").AddKey("row0", "0 0 1 1 0 0");
        disp.AddChunk("alphas").AddKey("row0", "0 255");
        disp.AddChunk("triangle_tags").AddKey("row0", "9 9");
        disp.AddChunk("allowed_verts").AddKey("10", "-1 -1");
        return disp;
    }

    // ---- entities --------------------------------------------------------------------

    /// <summary>
    /// A point entity moved: its origin through the transform, its yaw
    /// turned in <c>angles</c> and <c>angle</c> — except the -1 and -2 up and
    /// down codes — its connections and everything else copied.
    /// </summary>
    [Fact]
    public void AMovedEntityHasItsOriginAndYawMoved()
    {
        VmfChunk entity = new("entity");
        entity.AddKey("classname", "info_target");
        entity.AddKey("origin", "16 32 8");
        entity.AddKey("angles", "10 45 5");
        entity.AddKey("angle", "300");
        entity.AddKey("movedir", "0 10 0");
        entity.AddKey("targetname", "door");
        entity.AddChunk("connections").AddKey("OnOpen", "a,b,,0,-1");

        VmfChunk moved = VmfPlacement.MoveEntity(entity, new QuarterTurn(1, new Vec3(256, 0, 0)));

        Assert.Equal("224 16 8", moved.GetValue("origin"));
        Assert.Equal("10 135 5", moved.GetValue("angles"));
        Assert.Equal("30", moved.GetValue("angle"));
        Assert.Equal("0 10 0", moved.GetValue("movedir"));
        Assert.Equal("door", moved.GetValue("targetname"));
        Assert.Equal("a,b,,0,-1", moved.GetChunk("connections")!.GetValue("OnOpen"));

        entity.Keys.Single(k => k.Name == "angle").Value = "-1";
        Assert.Equal("-1", VmfPlacement.MoveEntity(entity, new QuarterTurn(1, Vec3.Zero)).GetValue("angle"));
        entity.Keys.Single(k => k.Name == "angle").Value = "-2";
        Assert.Equal("-2", VmfPlacement.MoveEntity(entity, new QuarterTurn(3, Vec3.Zero)).GetValue("angle"));

        // No turn: the angles are left as written, whatever they hold.
        entity.Keys.Single(k => k.Name == "angles").Value = "0 not-a-number 0";
        Assert.Equal("0 not-a-number 0", VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(Vec3.Zero)).GetValue("angles"));
    }

    /// <summary>
    /// A brush entity moved (open point O15 of the rooms design): its brushes
    /// turn and move, so its <c>angles</c> and <c>angle</c> are carried as
    /// written for a class outside the known-direction table, and its
    /// direction keys (<c>movedir</c>, <c>pushdir</c>, <c>gibdir</c>) turn
    /// as a yaw; a class vbsp consumes keeps the point entity's rule.
    /// </summary>
    [Fact]
    public void AMovedBrushEntityTurnsItsBrushesAndDirectionKeysNotItsAngles()
    {
        VmfChunk entity = new("entity");
        entity.AddKey("classname", "func_door");
        entity.AddKey("origin", "16 32 8");
        entity.AddKey("angles", "0 0 0");
        entity.AddKey("angle", "0");
        entity.AddKey("movedir", "0 45 0");
        entity.AddKey("PushDir", "-90 300 0");
        entity.AddKey("gibdir", "10 0 5");
        entity.Children.Add(Solid());

        VmfChunk moved = VmfPlacement.MoveEntity(entity, new QuarterTurn(1, new Vec3(256, 0, 0)));

        Assert.Equal("224 16 8", moved.GetValue("origin"));
        Assert.Equal("0 0 0", moved.GetValue("angles"));
        Assert.Equal("0", moved.GetValue("angle"));
        Assert.Equal("0 135 0", moved.GetValue("movedir"));
        Assert.Equal("-90 30 0", moved.GetValue("PushDir"));
        Assert.Equal("10 90 5", moved.GetValue("gibdir"));
        Assert.Equal(new Vec3(224, 0, 0), VmfPlacement.Bounds(moved.GetChunk("solid")!).Mins);

        // Unturned, the direction keys stay as written.
        Assert.Equal("0 45 0", VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(new Vec3(1, 1, 1))).GetValue("movedir"));

        // A class vbsp consumes turns its angles as a point entity does.
        entity.Keys.Single(k => k.Name == "classname").Value = "func_detail";
        entity.Keys.Single(k => k.Name == "angles").Value = "0 45 0";
        Assert.Equal("0 135 0", VmfPlacement.MoveEntity(entity, new QuarterTurn(1, Vec3.Zero)).GetValue("angles"));
        Assert.Equal("0 45 0", VmfPlacement.MoveEntity(entity, new QuarterTurn(1, Vec3.Zero)).GetValue("movedir"));
    }

    [Theory]
    [InlineData("origin", "1 2", "entity 9 (light): origin \"1 2\" is not three numbers")]
    [InlineData("angles", "0 a 0", "entity 9 (light): angles \"a\" is not a number")]
    [InlineData("angle", "west", "entity 9 (light): angle \"west\" is not a number")]
    public void AnEntityWithABadPlacementKeyIsRefused(string key, string value, string expected)
    {
        VmfChunk entity = new("entity");
        entity.AddKey("id", "9");
        entity.AddKey("classname", "light");
        entity.AddKey(key, value);

        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => VmfPlacement.MoveEntity(entity, new QuarterTurn(1, Vec3.Zero)));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityOriginIsReadWhenItHasOne()
    {
        VmfChunk entity = new("entity");
        Assert.Null(VmfPlacement.Origin(entity));
        entity.AddKey("origin", "1 2 3");
        Assert.Equal(new Vec3(1, 2, 3), VmfPlacement.Origin(entity));
        Assert.Equal("without an id", VmfPlacement.IdOf(entity));
        entity.Children.Clear();
        entity.AddKey("origin", "1 2 3");
        entity.AddKey("id", "3");
        Assert.Contains("entity 3 (no classname)", Assert.Throws<RoomLibraryException>(
            () => VmfPlacement.MoveEntity(Replace(entity, "origin", "1"), QuarterTurn.Translation(Vec3.Zero))).Message, StringComparison.Ordinal);
    }

    /// <summary>Numbers are written shortest-round-trip, invariant, and never as negative zero.</summary>
    [Fact]
    public void NumbersAreWrittenAsTheyRoundTrip()
    {
        Assert.Equal("0", VmfPlacement.Format(-0f));
        Assert.Equal("0.25", VmfPlacement.Format(0.25f));
        Assert.Equal("-16", VmfPlacement.Format(-16f));
        Assert.Equal("1 0 -2.5", VmfPlacement.Format(new Vec3(1, -0f, -2.5f)));
        VmfChunk chunk = Solid();
        VmfChunk copy = VmfPlacement.Clone(chunk);
        copy.Chunks.First().Keys.First().Value = "changed";
        Assert.NotEqual("changed", chunk.Chunks.First().Keys.First().Value);
        Assert.Equal(
            chunk.Chunks.Count(),
            copy.Chunks.Count());
    }

    /// <summary>A 64 × 32 × 16 box brush at the origin, written as Hammer writes one, id 7.</summary>
    private static VmfChunk Solid()
    {
        VmfChunk solid = new("solid");
        solid.AddKey("id", "7");
        foreach (string plane in new[]
        {
            "(0 32 16) (64 32 16) (64 0 16)",
            "(0 0 0) (64 0 0) (64 32 0)",
            "(0 32 0) (0 0 0) (0 0 16)",
            "(64 0 16) (64 0 0) (64 32 0)",
            "(64 32 16) (64 32 0) (0 32 0)",
            "(0 0 16) (0 0 0) (64 0 0)",
        })
        {
            VmfChunk side = solid.AddChunk("side");
            side.AddKey("plane", plane);
            side.AddKey("material", "unit/plain");
            side.AddKey("uaxis", "[1 0 0 8] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
        }

        return solid;
    }

    private static VmfChunk Replace(VmfChunk entity, string key, string value)
    {
        entity.Keys.Single(k => k.Name == key).Value = value;
        return entity;
    }

    private static (Vec3 Axis, float Shift, float Scale) Axis(string text)
    {
        string[] parts = text.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
        return (new Vec3(F(0), F(1), F(2)), F(3), F(4));
    }
}
