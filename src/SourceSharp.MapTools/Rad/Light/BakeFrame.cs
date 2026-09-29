//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The frame a vrad run lights a map in, when the map is a room that a level
/// will place turned: world-fixed directions (the sun, the sky's sampling
/// directions, the other direction tables vrad sweeps) expressed in the
/// room's own frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A room of a room library is lit once per quarter turn when sun
/// or sky light reaches it (the rooms design, 9.2): the sun is fixed in the
/// world, so a room placed turned sees it from another side. The bake runs on
/// the room as it was compiled, in its own frame, which keeps its lightmap
/// layout (texture axes turn with the room, so a luxel of the turned room is
/// the same luxel). What must turn instead is everything vrad holds fixed in
/// the world: the sun's direction, the directions it samples the sky ambient
/// along, the sun's area-light jitter, and the direction tables it casts
/// rays along for leaf ambient cubes and prop lighting. Each is turned here by
/// the inverse of the placement's turn, so a bake at turn <i>r</i> is the bake
/// of the room physically turned by <i>r</i>, up to float rounding.
/// </para>
/// <para>
/// <b>Exact.</b> A quarter turn about +z swaps and negates components and
/// multiplies nothing, so a turned direction is the same floats in another
/// order, and a dot product with a turned axis is the same product. Turn 0
/// returns its input untouched, so a map lit without a frame (every compile
/// that is not a room bake) runs exactly the arithmetic it always did.
/// </para>
/// </remarks>
internal static class BakeFrame
{
    /// <summary>
    /// A world direction in the frame of a room placed at
    /// <paramref name="turns"/> quarter turns: the inverse of the placement's
    /// turn (<c>RoomTransform</c> maps room <c>(x, y, z)</c> to world
    /// <c>(-y, x, z)</c> at one turn, so world <c>(x, y, z)</c> is room
    /// <c>(y, -x, z)</c>).
    /// </summary>
    /// <param name="world">The direction in the world.</param>
    /// <param name="turns">The placement's quarter turns; any integer, reduced mod 4.</param>
    /// <returns>The same direction in the room's frame.</returns>
    public static Vec3 ToRoom(Vec3 world, int turns) => (turns & 3) switch
    {
        0 => world,
        1 => new Vec3(world.Y, -world.X, world.Z),
        2 => new Vec3(-world.X, -world.Y, world.Z),
        _ => new Vec3(-world.Y, world.X, world.Z),
    };

    /// <summary>A table of world directions in a room's frame (<see cref="ToRoom(Vec3, int)"/>).</summary>
    /// <param name="world">The directions.</param>
    /// <param name="turns">The placement's quarter turns.</param>
    /// <returns>A new array, the directions turned, in the same order.</returns>
    public static Vec3[] ToRoom(ReadOnlySpan<Vec3> world, int turns)
    {
        Vec3[] turned = new Vec3[world.Length];
        for (int i = 0; i < turned.Length; i++)
        {
            turned[i] = ToRoom(world[i], turns);
        }

        return turned;
    }
}
