//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The brush entity turn rule of the rooms design (4.1, open point O15) on
/// its own: which keys a quarter turn turns on a brush entity, which angles
/// a room may hold, and the link's turn of a compiled brush entity.
/// </summary>
public sealed class BrushEntityDirectionsTests
{
    /// <summary>
    /// The refusal: a brush entity of a class outside the table whose
    /// <c>angles</c> or <c>angle</c> is anything but zero (including a value
    /// that does not read as numbers); none for zero, for a point entity,
    /// for a class vbsp consumes, or for a class in the table.
    /// </summary>
    [Theory]
    [InlineData("func_brush", "angles", "0 0 0", true, null)]
    [InlineData("func_brush", "angles", "-0 0.0 0", true, null)]
    [InlineData("func_brush", "angles", "0 30 0", true, "angles \"0 30 0\"")]
    [InlineData("func_brush", "Angles", "5 0 0", true, "Angles \"5 0 0\"")]
    [InlineData("func_brush", "angles", "0 x 0", true, "angles \"0 x 0\"")]
    [InlineData("func_brush", "angles", "0 0", true, "angles \"0 0\"")]
    [InlineData("func_door", "angle", "0", true, null)]
    [InlineData("func_door", "angle", "-1", true, "angle \"-1\"")]
    [InlineData("func_door", "angles", "0 30 0", false, null)]
    [InlineData("func_detail", "angles", "0 30 0", true, null)]
    [InlineData("func_occluder", "angles", "0 30 0", true, null)]
    public void OnlyNonZeroAnglesOnAnUnknownBrushClassAreRefused(string classname, string key, string value, bool brush, string? refused)
    {
        VmfChunk entity = new("entity");
        entity.AddKey("id", "7");
        entity.AddKey("classname", classname);
        entity.AddKey(key, value);
        if (brush)
        {
            entity.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(1, 1, 1), new Vec3(8, 8, 8), 70));
        }

        string? problem = BrushEntityDirections.Problem("r", entity);
        Assert.Equal(
            refused is null
                ? null
                : $"room r: brush entity 7 ({classname}) has {refused}; a turned room cannot tell whether {classname} applies them to its model."
                    + $" Use 0 0 0, or add {classname} to the known-direction table.",
            problem);

        // A class in the table reads its angles as a direction: never refused.
        Assert.Null(BrushEntityDirections.Problem("r", entity, new HashSet<string>(StringComparer.Ordinal) { classname }));
    }

    /// <summary>
    /// Which keys a turn turns on a brush entity: the direction keys on any
    /// class, whatever their case; <c>angles</c> and <c>angle</c> only on a
    /// class in the table, which starts empty; nothing else.
    /// </summary>
    [Fact]
    public void ATurnTurnsTheDirectionKeysAndOnlyATableClasssAngles()
    {
        Assert.Empty(BrushEntityDirections.KnownDirectionClasses);
        HashSet<string> table = new(StringComparer.Ordinal) { "func_windy" };
        foreach (string key in new[] { "movedir", "PushDir", "GIBDIR" })
        {
            Assert.True(BrushEntityDirections.IsDirectionKey(key));
            Assert.True(BrushEntityDirections.TurnsKey("func_door", key));
        }

        Assert.False(BrushEntityDirections.TurnsKey("func_door", "angles"));
        Assert.False(BrushEntityDirections.TurnsKey("func_door", "angle"));
        Assert.True(BrushEntityDirections.TurnsKey("func_windy", "angles", table));
        Assert.True(BrushEntityDirections.TurnsKey("func_windy", "ANGLE", table));
        Assert.False(BrushEntityDirections.TurnsKey("func_windy", "origin", table));
        Assert.False(BrushEntityDirections.IsDirectionKey("angles"));
        Assert.False(BrushEntityDirections.ReadsAnglesAsDirection(null, table));
        Assert.True(BrushEntityDirections.IsConsumed("func_ladder"));
        Assert.False(BrushEntityDirections.IsConsumed(null));
        Assert.False(BrushEntityDirections.IsConsumed("func_door"));
    }

    /// <summary>
    /// The link's turn of a compiled entity: a brush entity (one whose
    /// <c>model</c> names a brush model, <c>*N</c>) keeps its angles and
    /// turns its direction keys; a point entity turns its angles and leaves
    /// a <c>movedir</c> alone, as it always did; the flatten's turn of the
    /// same entities agrees.
    /// </summary>
    [Theory]
    [InlineData("*3", "0 0 0", "0 135 0")]
    [InlineData("*x", "0 90 0", "0 45 0")]
    [InlineData("models/a.mdl", "0 90 0", "0 45 0")]
    [InlineData(null, "0 90 0", "0 45 0")]
    public void TheLinkTurnsACompiledBrushEntityByTheRule(string? model, string angles, string movedir)
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", "func_door"));
        if (model is not null)
        {
            entity.Pairs.Add(new BspKeyValue("model", model));
        }

        entity.Pairs.Add(new BspKeyValue("angles", "0 0 0"));
        entity.Pairs.Add(new BspKeyValue("movedir", "0 45 0"));
        BspEntity moved = LevelLinker.MoveEntity(entity, new RoomTransform(new RoomPlacement("r", 0, 0, 1), RoomHarness.Cell), "r");
        Assert.Equal(angles, moved.Get("angles"));
        Assert.Equal(movedir, moved.Get("movedir"));
        Assert.True(LevelLinker.IsBrushModel("*12"));
        Assert.False(LevelLinker.IsBrushModel("*"));
        Assert.False(LevelLinker.IsBrushModel("*-1"));
    }
}
