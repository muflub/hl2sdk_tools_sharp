//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>SetupLightNormalFromProps</c>: the
/// direction a light entity points, from its <c>angles</c>, <c>angle</c> and
/// <c>pitch</c> keys.
/// </summary>
/// <remarks>
/// <para>
/// Three keys for one direction, and they do not compose the way the names
/// suggest. <c>angle</c> overrides the yaw from <c>angles</c>; <c>pitch</c>
/// overrides the pitch from <c>angles</c>; and each override is skipped when
/// the key reads as ZERO, not when it is absent -- so <c>"pitch" "0"</c> and
/// no pitch key at all are the same input, and a light explicitly aimed at the
/// horizon silently takes its pitch from <c>angles</c> instead.
/// </para>
/// <para>
/// Two sentinel values short-circuit the yaw entirely: <c>ANGLE_UP</c> (-1)
/// and <c>ANGLE_DOWN</c> (-2) from the reference implementation. They set the vector
/// straight up or down -- and are then OVERWRITTEN by the pitch block below,
/// which unconditionally assigns <c>output[2] = sin(pitch)</c>. So
/// <c>"angle" "-1"</c> does not aim a light upward unless the pitch happens to
/// be 90. That is stock's behaviour and it is reproduced; the sentinel's only
/// surviving effect is that x and y start at zero instead of at
/// <c>cos/sin(yaw)</c>, which the same block then multiplies by
/// <c>cos(pitch)</c> -- leaving them zero either way. In other words the two
/// sentinels are, after this function, indistinguishable from each other.
/// </para>
/// </remarks>
public static class LightNormals
{
    /// <summary><c>ANGLE_UP</c>.</summary>
    public const float AngleUp = -1f;

    /// <summary><c>ANGLE_DOWN</c>.</summary>
    public const float AngleDown = -2f;

    /// <summary>
    /// The light's direction.
    /// </summary>
    /// <param name="angles">
    /// The <c>angles</c> key as <c>GetVectorForKey</c> read it: pitch, yaw,
    /// roll.
    /// </param>
    /// <param name="angle">The <c>angle</c> key, or zero.</param>
    /// <param name="pitch">The <c>pitch</c> key, or zero.</param>
    /// <param name="crtCosine">
    /// Take cosines as the reference build's C runtime does at a right angle
    /// (<see cref="Options.StockQuirk.CrtCosineAtRightAngle"/>).
    /// </param>
    /// <param name="reciprocalDegrees">
    /// Convert degrees as the reference build does, multiplying by the float
    /// <c>1/180</c> (<see cref="Options.StockQuirk.DegreesToRadiansByReciprocal"/>).
    /// </param>
    /// <returns>A unit vector.</returns>
    public static Vec3 FromProps(
        Vec3 angles, float angle, float pitch, bool crtCosine = false, bool reciprocalDegrees = false)
    {
        float x;
        float y;

        if (angle == AngleUp)
        {
            x = 0f;
            y = 0f;
        }
        else if (angle == AngleDown)
        {
            x = 0f;
            y = 0f;
        }
        else
        {
            // A zero `angle` falls back to the yaw, which
            // QAngle indexes as component 1 -- the SECOND number of the angles
            // key.
            if (angle == 0f)
            {
                angle = angles.Y;
            }

            x = (float)Cosine(Radians(angle, reciprocalDegrees), crtCosine);
            y = (float)Math.Sin(Radians(angle, reciprocalDegrees));
        }

        // Likewise, a zero pitch falls back to component 0.
        if (pitch == 0f)
        {
            pitch = angles.X;
        }

        // 44-46, and note z is ASSIGNED here rather than multiplied, which is
        // what erases the ANGLE_UP and ANGLE_DOWN sentinels above.
        float z = (float)Math.Sin(Radians(pitch, reciprocalDegrees));
        float cosPitch = (float)Cosine(Radians(pitch, reciprocalDegrees), crtCosine);

        return new Vec3(x * cosPitch, y * cosPitch, z);
    }

    /// <summary>
    /// <c>degrees/180*M_PI</c>: a FLOAT division by the int 180, then a double
    /// multiply by pi.
    /// </summary>
    /// <param name="degrees">The angle.</param>
    /// <param name="reciprocal">
    /// Evaluate it as stock's binary did: <c>degrees * (1.0f/180)</c> in float,
    /// then times pi (<see cref="Options.StockQuirk.DegreesToRadiansByReciprocal"/>).
    /// </param>
    /// <returns>The angle in radians, in double.</returns>
    public static double Radians(float degrees, bool reciprocal) =>
        reciprocal ? (degrees * (1.0f / 180)) * Math.PI : (degrees / 180) * Math.PI;

    /// <summary>
    /// <c>cos</c> as stock's worldlights show it was evaluated.
    /// </summary>
    /// <param name="radians">The argument.</param>
    /// <param name="crtCosine">Reproduce the C runtime's right-angle answer.</param>
    /// <returns>The cosine.</returns>
    /// <remarks>
    /// At exactly <c>+/-(double)M_PI / 2</c> the correctly rounded cosine is
    /// 6.123233995736766e-17 (the distance from the double to the true pi/2),
    /// and that is what <see cref="Math.Cos"/> returns. Stock's x64 binary
    /// wrote 1.2246469e-16 -- the float of <c>sin((double)M_PI)</c>, twice as
    /// large -- for BOTH a yaw of 90 and a pitch of -90 on p4c's texlight
    /// fixture. That is the answer of a cosine taken as <c>sin(|x| + pi/2)</c>
    /// in double, which is what the observed pair fits. Only the observed
    /// arguments are reproduced; every other angle is <see cref="Math.Cos"/>.
    /// </remarks>
    public static double Cosine(double radians, bool crtCosine)
    {
        const double halfPi = Math.PI / 2;
        if (crtCosine && (radians == halfPi || radians == -halfPi))
        {
            return Math.Sin(Math.PI);
        }

        return Math.Cos(radians);
    }
}
