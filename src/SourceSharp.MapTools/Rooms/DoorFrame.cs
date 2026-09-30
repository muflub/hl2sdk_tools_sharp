//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One socket's doorway as the door light sees it: the opening's centre on
/// the room's inner wall plane (the plug's inner face), the unit normal out
/// of the room through it, the horizontal axis across it, and the kit's
/// size.
/// </summary>
/// <param name="Centre">The opening's centre on the plug's inner face, room-local.</param>
/// <param name="Out">The unit normal out of the room through the opening.</param>
/// <param name="Across">The horizontal unit axis along the wall: up cross out.</param>
/// <param name="Width">The opening's width.</param>
/// <param name="Height">The opening's height.</param>
/// <param name="Depth">The plug's depth: half the doorway's length at a joint.</param>
/// <remarks>
/// <para>
/// <b>Door-local coordinates</b> are (along <see cref="Out"/>, along
/// <see cref="Across"/>, up) from <see cref="Centre"/>: a point inside the
/// room has a negative first coordinate. Two jointed rooms' frames face
/// each other: their <see cref="Out"/>s and <see cref="Across"/>es are
/// opposite in the world, their centres <c>2 x Depth</c> apart (the two
/// plugs' length), and their up axes and centres' heights the same (every
/// socket is centred on its cell face). So a point at door-local
/// <c>(x, y, z)</c> of one room is at <c>(2 Depth - x, -y, z)</c> of the
/// other, whatever the two placements' turns: <see cref="ToNeighbour"/>.
/// </para>
/// </remarks>
internal readonly record struct DoorFrame(Vec3 Centre, Vec3 Out, Vec3 Across, float Width, float Height, float Depth)
{
    /// <summary>The world's up, which a turn keeps.</summary>
    public static Vec3 Up => new(0, 0, 1);

    /// <summary>The frame of one socket of a room.</summary>
    public static DoorFrame Of(RoomDefinition definition, RoomSocket socket)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        Vec3 @out = socket.Facing switch
        {
            RoomFacing.PositiveX => new Vec3(1, 0, 0),
            RoomFacing.NegativeX => new Vec3(-1, 0, 0),
            RoomFacing.PositiveY => new Vec3(0, 1, 0),
            _ => new Vec3(0, -1, 0),
        };
        Vec3 centre = ((plug.Mins + plug.Maxs) * 0.5f) - (@out * (definition.Kit.Depth * 0.5f));
        return new DoorFrame(centre, @out, Vec3.Cross(Up, @out), definition.Kit.Width, definition.Kit.Height, definition.Kit.Depth);
    }

    /// <summary>A room-local point in door-local coordinates.</summary>
    public Vec3 ToLocal(Vec3 point)
    {
        Vec3 d = point - Centre;
        return new Vec3(Vec3.Dot(d, Out), Vec3.Dot(d, Across), d.Z);
    }

    /// <summary>A room-local direction in door-local coordinates.</summary>
    public Vec3 DirectionToLocal(Vec3 direction) => new(Vec3.Dot(direction, Out), Vec3.Dot(direction, Across), direction.Z);

    /// <summary>A door-local point in room-local coordinates.</summary>
    public Vec3 ToRoom(Vec3 local) => Centre + (Out * local.X) + (Across * local.Y) + (Up * local.Z);

    /// <summary>A door-local direction in room-local coordinates.</summary>
    public Vec3 DirectionToRoom(Vec3 local) => (Out * local.X) + (Across * local.Y) + (Up * local.Z);

    /// <summary>A door-local point of this room in the door-local coordinates of the room jointed to it.</summary>
    public Vec3 ToNeighbour(Vec3 local) => new((2 * Depth) - local.X, -local.Y, local.Z);

    /// <summary>A door-local direction of this room in the door-local coordinates of the room jointed to it.</summary>
    public static Vec3 DirectionToNeighbour(Vec3 local) => new(-local.X, -local.Y, local.Z);
}
