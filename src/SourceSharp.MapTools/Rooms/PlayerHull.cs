//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The box a standing player occupies, and whether a room's doors let it
/// through.
/// </summary>
/// <remarks>
/// <para>
/// A room level is only valid if a player can reach every room, so "a joint"
/// has to mean a doorway a player walks through, not merely a gap. The
/// player is a box as far as movement is concerned: in Half-Life 2 and Team
/// Fortress 2 the standing hull is 32 units square and 72 tall, from the
/// feet up, and the movement code sweeps exactly that box through the world.
/// A doorway admits the player when the box fits through it standing on the
/// room's floor.
/// </para>
/// <para>
/// The kit's opening is centred on its cell face (see
/// <see cref="SocketKit.OpeningUnit"/>), so its sill sits at
/// <c>(cell − height) / 2</c>. The room's floor is its shell, as thick as the
/// kit's wall depth, so the floor's top is at <c>depth</c>. A door stands on
/// the floor only when those agree, which is when the door is exactly the
/// room's interior height, <c>cell − 2 × depth</c>: a shorter centred door
/// has a sill a player would have to climb, and none of the room rules lets
/// a room put steps in its doorway.
/// </para>
/// </remarks>
public static class PlayerHull
{
    /// <summary>The standing player's width and depth: 32 units, both ways.</summary>
    public const float Width = 32f;

    /// <summary>The standing player's height, feet to head: 72 units.</summary>
    public const float Height = 72f;

    /// <summary>How far a door's sill may be from the floor and still count as on it.</summary>
    public const float SillTolerance = 0.01f;

    /// <summary>Why a player could not walk through this kit's doors, or null when one can.</summary>
    /// <param name="kit">The door kit.</param>
    /// <param name="cellSize">The cell the kit's doors are cut in.</param>
    /// <returns>The problem, worded to follow "the door kit ...", or null.</returns>
    public static string? DoorProblem(SocketKit kit, float cellSize)
    {
        if (kit.Width < Width)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"is {kit.Width:0.###} wide; a standing player needs a door at least {Width:0} wide");
        }

        if (kit.Height < Height)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"is {kit.Height:0.###} tall; a standing player needs a door at least {Height:0} tall");
        }

        float sill = (cellSize - kit.Height) / 2f;
        if (Math.Abs(sill - kit.Depth) > SillTolerance)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"puts the door's sill at z = {sill:0.###} but the floor's top at z = {kit.Depth:0.###} (the wall depth),"
                + $" so a player cannot step through; a door stands on the floor when its height is"
                + $" cell size - 2 x wall depth = {cellSize - (2 * kit.Depth):0.###}");
        }

        return null;
    }
}
