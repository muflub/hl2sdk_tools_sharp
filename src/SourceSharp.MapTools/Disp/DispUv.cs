//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// A two-dimensional float pair: stock's <c>Vector2D</c>, as displacements use
/// it for texture coordinates, luxel coordinates and lightmap-space points.
/// </summary>
/// <remarks>
/// <b>This wants to be <c>MapFormats.Geometry.Vec2</c> and is not yet.</b>
/// Nothing outside the displacement code needs a 2D vector today, and
/// <c>MapFormats</c> is another lane's file set; when a second consumer
/// appears, this type should move there under that name and this one should
/// go. It is deliberately minimal so that the move is a rename.
/// </remarks>
/// <param name="X">The first component: u, or a lightmap column.</param>
/// <param name="Y">The second: v, or a lightmap row.</param>
public readonly record struct DispUv(float X, float Y)
{
    /// <summary>The component at <paramref name="i"/>: 0 is x, 1 is y.</summary>
    /// <param name="i">0 or 1.</param>
    /// <returns>That component.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Not 0 or 1.</exception>
    public float this[int i] => i switch
    {
        0 => X,
        1 => Y,
        _ => throw new ArgumentOutOfRangeException(
            nameof(i), i, "a 2D vector has two components."),
    };

    /// <summary>Componentwise addition.</summary>
    /// <param name="a">The left pair.</param>
    /// <param name="b">The right pair.</param>
    /// <returns>The sum.</returns>
    public static DispUv operator +(DispUv a, DispUv b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Componentwise subtraction.</summary>
    /// <param name="a">The left pair.</param>
    /// <param name="b">The right pair.</param>
    /// <returns>The difference.</returns>
    public static DispUv operator -(DispUv a, DispUv b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Scaling.</summary>
    /// <param name="a">The pair.</param>
    /// <param name="scale">The scale.</param>
    /// <returns>The scaled pair.</returns>
    public static DispUv operator *(DispUv a, float scale) => new(a.X * scale, a.Y * scale);
}
