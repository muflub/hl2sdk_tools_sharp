//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// A bounding sphere of a <see cref="SampleGroup"/>'s real lanes, and whether
/// its points and normals are tame enough for <see cref="DeadLightCull"/> to
/// reason about. Built once per group, then tested against every light.
/// </summary>
/// <remarks>
/// Everything is in double, from the group's float values, so the sphere
/// really contains every point: the centre is the mean, the radius the
/// largest distance to it, inflated far beyond double rounding.
/// </remarks>
internal readonly struct SampleBounds
{
    /// <summary>
    /// Coordinates beyond this, in any lane, disable the cull: it keeps every
    /// distance the gather computes well inside float range, which the
    /// falloff-finiteness argument needs.
    /// </summary>
    internal const double MaxCoordinate = 1.0e7;

    /// <summary>A normal longer than this disables the tests that rely on its dot product being finite.</summary>
    internal const double MaxNormalLength = 10.0;

    /// <summary>Creates bounds from their parts; <see cref="Of"/> is the usual way.</summary>
    /// <param name="centre">The sphere's centre.</param>
    /// <param name="radius">Its radius, already inflated.</param>
    /// <param name="valid">Every point finite and in range, and every flat normal finite and short.</param>
    /// <param name="normalsBounded">Every normal, bump ones included, finite and short.</param>
    internal SampleBounds(Vec3d centre, double radius, bool valid, bool normalsBounded)
    {
        Centre = centre;
        Radius = radius;
        Valid = valid;
        NormalsBounded = normalsBounded;
    }

    /// <summary>The sphere's centre.</summary>
    internal Vec3d Centre { get; }

    /// <summary>The sphere's radius: no real lane's point is farther from <see cref="Centre"/>.</summary>
    internal double Radius { get; }

    /// <summary>
    /// Every real lane's point is finite and within <see cref="MaxCoordinate"/>,
    /// and every real lane's flat normal is finite and no longer than
    /// <see cref="MaxNormalLength"/>. When false, nothing is culled.
    /// </summary>
    internal bool Valid { get; }

    /// <summary>
    /// Every normal of every real lane, bump normals included, is finite and
    /// no longer than <see cref="MaxNormalLength"/>: so every dot product the
    /// gather takes with them is finite.
    /// </summary>
    internal bool NormalsBounded { get; }

    /// <summary>The bounds of a group's real lanes (<see cref="SampleGroup.Count"/>).</summary>
    /// <param name="group">The group.</param>
    /// <returns>The bounds.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static SampleBounds Of(SampleGroup group)
    {
        int count = group.Count;
        Vec3[] p = group.Points;
        bool valid = count is >= 1 and <= SampleGroup.Lanes;
        if (!valid)
        {
            return default;
        }

        double sx = 0, sy = 0, sz = 0;
        for (int lane = 0; lane < count; lane++)
        {
            valid &= Tame(p[lane].X) && Tame(p[lane].Y) && Tame(p[lane].Z);
            valid &= ShortNormal(group.Normal(0, lane));
            sx += p[lane].X;
            sy += p[lane].Y;
            sz += p[lane].Z;
        }

        bool normalsBounded = valid;
        for (int n = 1; n < group.NormalCount && normalsBounded; n++)
        {
            for (int lane = 0; lane < count; lane++)
            {
                normalsBounded &= ShortNormal(group.Normal(n, lane));
            }
        }

        if (!valid)
        {
            return default;
        }

        Vec3d centre = new(sx / count, sy / count, sz / count);
        double r2 = 0;
        for (int lane = 0; lane < count; lane++)
        {
            double dx = p[lane].X - centre.X;
            double dy = p[lane].Y - centre.Y;
            double dz = p[lane].Z - centre.Z;
            r2 = Math.Max(r2, (dx * dx) + (dy * dy) + (dz * dz));
        }

        // Double rounding is ~1e-16 relative; the inflation dwarfs it.
        double radius = (Math.Sqrt(r2) * (1.0 + 1.0e-9)) + 1.0e-9;
        return new SampleBounds(centre, radius, valid: true, normalsBounded);
    }

    private static bool Tame(float v) => Math.Abs(v) <= MaxCoordinate;

    private static bool ShortNormal(Vec3 n)
    {
        double l2 = ((double)n.X * n.X) + ((double)n.Y * n.Y) + ((double)n.Z * n.Z);
        return l2 <= MaxNormalLength * MaxNormalLength;
    }
}

/// <summary>A double-precision point, for the cull's geometry.</summary>
/// <param name="X">X.</param>
/// <param name="Y">Y.</param>
/// <param name="Z">Z.</param>
internal readonly record struct Vec3d(double X, double Y, double Z);

/// <summary>
/// What <see cref="DeadLightCull"/> needs to know about one light, worked out
/// once when the gatherer is built.
/// </summary>
internal readonly struct LightCullShape
{
    /// <summary>
    /// The cosine margin every angular test keeps. The gather's own dot
    /// products differ from the exact ones by float rounding (a few 1e-7) and,
    /// under <see cref="Options.StockQuirk.GatherReciprocalEstimate"/>, by the
    /// reciprocal-square-root estimate's error, which is bounded well below
    /// 4e-4 relative even before its Newton step. 1e-3 covers both with room.
    /// </summary>
    internal const double CosineMargin = 1.0e-3;

    /// <summary>
    /// The closest a sample may be to the light for the behind-the-normal and
    /// emitter-plane tests: it keeps the squared distance a normal float, so
    /// the gather's direction and falloff stay finite.
    /// </summary>
    internal const double MinDistance = 1.0e-2;

    /// <summary>Attenuation coefficients above this disable the falloff-finiteness argument.</summary>
    internal const double MaxAttenuation = 1.0e6;

    /// <summary>The largest attenuation coefficient must be at least this for the falloff to stay finite.</summary>
    internal const double MinAttenuation = 1.0e-20;

    /// <summary>A spot exponent above this disables the falloff-finiteness argument.</summary>
    internal const double MaxExponent = 1.0e6;

    /// <summary>The shape of a light that is never culled.</summary>
    internal static LightCullShape Never => default;

    /// <summary>False for a light the cull never touches.</summary>
    internal bool Cullable { get; init; }

    /// <summary>The light's type.</summary>
    internal EmitType Type { get; init; }

    /// <summary>Where the gather's rays end: the origin, or the world origin when the light has a face.</summary>
    internal Vec3d Source { get; init; }

    /// <summary>
    /// The distance at or beyond which a sample is certainly past the hard fade
    /// distance as the gather computes it; +infinity when there is no hard
    /// fade (or it cannot be relied on).
    /// </summary>
    internal double FadeCull { get; init; }

    /// <summary>The unit axis of a spot's cone or a surface's emitting side.</summary>
    internal Vec3d Axis { get; init; }

    /// <summary>
    /// The cosine of the widest angle from <see cref="Axis"/> still certainly
    /// cut by the gather's spot test, and its sine. <see cref="HasCone"/> is
    /// false when the cone cannot be tested.
    /// </summary>
    internal double ConeCos { get; init; }

    /// <summary>The sine that goes with <see cref="ConeCos"/>.</summary>
    internal double ConeSin { get; init; }

    /// <summary>The spot's cone is testable.</summary>
    internal bool HasCone { get; init; }

    /// <summary>A surface light's emitting side is testable.</summary>
    internal bool HasPlane { get; init; }

    /// <summary>
    /// The gather's falloff is finite for every sample
    /// <see cref="SampleBounds.Valid"/> allows: what the behind-the-normal test
    /// needs for a point or spot light, whose zero dot is multiplied by it.
    /// </summary>
    internal bool FalloffFinite { get; init; }

    /// <summary>Works out a light's shape.</summary>
    /// <param name="light">The light.</param>
    /// <returns>Its shape; <see cref="Never"/> for a light that must not be culled.</returns>
    internal static LightCullShape Of(DirectLight light)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.Type is not (EmitType.Point or EmitType.Spotlight or EmitType.Surface))
        {
            return Never;
        }

        // A dead record adds zero times the intensity; with a non-finite
        // intensity that is NaN, not nothing. Keep such a light.
        if (!float.IsFinite(light.Intensity.X) || !float.IsFinite(light.Intensity.Y) || !float.IsFinite(light.Intensity.Z))
        {
            return Never;
        }

        Vec3 src = light.FaceNum == -1 ? light.Origin : Vec3.Zero;
        if (!(Math.Abs(src.X) <= SampleBounds.MaxCoordinate)
            || !(Math.Abs(src.Y) <= SampleBounds.MaxCoordinate)
            || !(Math.Abs(src.Z) <= SampleBounds.MaxCoordinate))
        {
            return Never;
        }

        // The gather cuts a lane when !(dist <= end). Its dist is within a few
        // 1e-7 of the exact one; demand 1e-5 relative plus 1e-3 absolute.
        double fadeCull = double.PositiveInfinity;
        float end = light.EndFadeDistance;
        if (end > light.StartFadeDistance && float.IsFinite(end))
        {
            fadeCull = end + (Math.Abs((double)end) * 1.0e-5) + 1.0e-3;
        }

        Vec3d axis = default;
        bool hasCone = false;
        bool hasPlane = false;
        double coneCos = 0;
        double coneSin = 0;
        Vec3 n = light.Normal;
        double nLen = Math.Sqrt(((double)n.X * n.X) + ((double)n.Y * n.Y) + ((double)n.Z * n.Z));
        bool axisUsable = nLen is >= 0.5 and <= 2.0;
        if (axisUsable)
        {
            axis = new Vec3d(n.X / nLen, n.Y / nLen, n.Z / nLen);
        }

        if (light.Type == EmitType.Spotlight && axisUsable && float.IsFinite(light.StopDot2))
        {
            // The gather's dot2 is |N| cos(angle) up to the margin's error, and
            // is cut when not above StopDot2: every angle whose cosine is at
            // most StopDot2 / |N| - margin is cut for certain.
            double c = (light.StopDot2 / nLen) - CosineMargin;
            if (c is > -1.0 and < 1.0)
            {
                hasCone = true;
                coneCos = c;
                coneSin = Math.Sqrt(1.0 - (c * c));
            }
        }

        if (light.Type == EmitType.Surface && axisUsable)
        {
            hasPlane = true;
        }

        return new LightCullShape
        {
            Cullable = true,
            Type = light.Type,
            Source = new Vec3d(src.X, src.Y, src.Z),
            FadeCull = fadeCull,
            Axis = axis,
            ConeCos = coneCos,
            ConeSin = coneSin,
            HasCone = hasCone,
            HasPlane = hasPlane,
            FalloffFinite = FalloffStaysFinite(light),
        };
    }

    // For a sample at 0.01..1e8 units: the attenuation denominator lies in
    // [max coefficient * (1 - 1e-6), 3e22], so its reciprocal -- exact or
    // estimated -- is finite; the spot's cone multiplier is clamped to [0, 1]
    // before a power with a non-negative exponent keeps it there; the fade
    // multiplier is clamped to [0, 1] too.
    private static bool FalloffStaysFinite(DirectLight light)
    {
        double q = light.QuadraticAttn;
        double l = light.LinearAttn;
        double c = light.ConstantAttn;
        bool attn = q is >= 0 and <= MaxAttenuation && l is >= 0 and <= MaxAttenuation
            && c is >= 0 and <= MaxAttenuation && Math.Max(q, Math.Max(l, c)) >= MinAttenuation;

        // CapDist below 1 (or NaN) would let the evaluation distance under 1.
        bool cap = light.CapDist >= 1.0f;
        bool exponent = light.Type != EmitType.Spotlight || light.Exponent is >= 0 and <= (float)MaxExponent;
        return attn && cap && exponent;
    }
}

/// <summary>
/// Decides, before a (group, light) record is emitted, that the light
/// provably contributes nothing to any lane of the group, so the record can be
/// left out without changing a bit of the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why leaving a dead record out is exact.</b> A dead record emits no rays,
/// and a call with no rays adds no packet padding (every call before it ended
/// on a packet boundary), so the rays of every other record are laid out and
/// answered exactly as before. The direct gather skips a light whose every
/// <c>dot * falloff</c> is zero, so a dead record there changes nothing, not
/// even the style allocation. The supersampling resample adds
/// <c>(±0) * intensity</c> to an accumulator that starts at +0; an IEEE sum
/// that starts at +0 is never -0 (x + y is -0 only when both are -0), and
/// adding a zero of either sign to anything that is not -0 returns it
/// unchanged. So the one bit a zero could flip, the sign of a zero sum,
/// cannot flip. That needs the intensity finite -- zero times infinity is
/// NaN -- so a light with a non-finite intensity is never culled.
/// </para>
/// <para>
/// <b>Dead means</b> every real lane either leaves the gather before writing
/// anything (past the hard fade distance, outside a spot's cone, a surface
/// light behind the sample's normal: all zeros), or ends with a zero dot and a
/// finite falloff (a point or spot light behind the normal: the resolve step
/// clamps the dot to +0 and zeroes the bump dots with it) or a zero falloff
/// and finite dots (a sample behind a surface light's emitting side).
/// </para>
/// <para>
/// <b>Margins.</b> Each test proves its condition for the exact geometry with
/// a margin, then relies on the gather's float arithmetic staying within that
/// margin of exact: <see cref="LightCullShape.CosineMargin"/> for the angular
/// tests and 1e-5 relative plus 1e-3 absolute for the fade distance. The
/// margins cover both policies, so one cull decision holds whichever
/// arithmetic the gather uses. When a condition cannot be proved -- a
/// non-finite or out-of-range input anywhere -- the light is kept.
/// </para>
/// </remarks>
internal static class DeadLightCull
{
    /// <summary>
    /// True when <paramref name="shape"/>'s light provably lights no lane of
    /// <paramref name="group"/>: gathering it would emit no ray and leave every
    /// <c>dot * falloff</c> of every normal zero after the resolve step.
    /// </summary>
    /// <param name="shape">The light's shape.</param>
    /// <param name="group">The group's points and normals.</param>
    /// <param name="bounds">The group's bounds (<see cref="SampleBounds.Of"/>).</param>
    /// <param name="flags">The gather flags the record would be gathered with.</param>
    /// <returns>True when the record can be left out.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool IsDead(in LightCullShape shape, SampleGroup group, in SampleBounds bounds, GatherFlags flags)
    {
        if (!shape.Cullable || !bounds.Valid)
        {
            return false;
        }

        Vec3d src = shape.Source;
        Vec3d c = bounds.Centre;
        double r = bounds.Radius;
        double wx = c.X - src.X;
        double wy = c.Y - src.Y;
        double wz = c.Z - src.Z;
        double d = Math.Sqrt((wx * wx) + (wy * wy) + (wz * wz));

        // Every point past the hard fade distance: each lane returns at once.
        if (d - r >= shape.FadeCull)
        {
            return true;
        }

        if (shape.HasCone && OutsideCone(shape, wx, wy, wz, d, r))
        {
            return true;
        }

        if (shape.HasPlane && bounds.NormalsBounded && BehindEmitter(shape, wx, wy, wz, d, r))
        {
            return true;
        }

        // Behind every lane's own normal. A surface light returns from such a
        // lane at once; a point or spot light carries on with a zero dot, so
        // its falloff must be finite for zero times it to be zero.
        bool ignoreNormals = (flags & GatherFlags.IgnoreNormals) != 0;
        if (!ignoreNormals && (shape.Type == EmitType.Surface || shape.FalloffFinite))
        {
            return BehindEveryNormal(src, group);
        }

        return false;
    }

    // The sphere (c, r) lies wholly outside the cone of half-angle phi about
    // the axis: its distance from the cone is at least r. In the plane of the
    // axis and w = c - src, with x = w.axis and y = |w x axis| (so theta is
    // w's angle from the axis), the nearest point of the cone is on its edge
    // at D sin(theta - phi) when theta - phi <= 90 degrees, else the apex at
    // D. theta <= phi means the centre is inside.
    private static bool OutsideCone(in LightCullShape shape, double wx, double wy, double wz, double d, double r)
    {
        Vec3d a = shape.Axis;
        double x = (wx * a.X) + (wy * a.Y) + (wz * a.Z);
        double cx = (wy * a.Z) - (wz * a.Y);
        double cy = (wz * a.X) - (wx * a.Z);
        double cz = (wx * a.Y) - (wy * a.X);
        double y = Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz));

        double sinGap = (y * shape.ConeCos) - (x * shape.ConeSin);
        if (!(sinGap > 0))
        {
            return false;
        }

        double cosGap = (x * shape.ConeCos) + (y * shape.ConeSin);
        double toCone = cosGap >= 0 ? sinGap : d;
        return toCone >= r + (1.0e-9 * (d + 1.0));
    }

    // Every point of the sphere is behind the emitting side by more than the
    // cosine margin: (p - src).axis <= -margin |p - src|, bounded through the
    // centre's height and |p - src| <= D + r. The dot2 the gather takes is
    // then negative, clamped to zero, and so is the falloff made from it.
    private static bool BehindEmitter(in LightCullShape shape, double wx, double wy, double wz, double d, double r)
    {
        Vec3d a = shape.Axis;
        double h = (wx * a.X) + (wy * a.Y) + (wz * a.Z);
        return d - r >= LightCullShape.MinDistance
            && h + r <= (-LightCullShape.CosineMargin * (d + r)) - (1.0e-9 * (d + 1.0));
    }

    // Every real lane's flat normal faces away from the light by more than
    // the cosine margin, so the gather's dot is negative or a zero.
    private static bool BehindEveryNormal(Vec3d src, SampleGroup group)
    {
        const double m2 = LightCullShape.CosineMargin * LightCullShape.CosineMargin;
        const double minD2 = LightCullShape.MinDistance * LightCullShape.MinDistance;
        for (int lane = 0; lane < group.Count; lane++)
        {
            Vec3 p = group.Points[lane];
            Vec3 n = group.Normal(0, lane);
            double vx = src.X - p.X;
            double vy = src.Y - p.Y;
            double vz = src.Z - p.Z;
            double vn = (vx * n.X) + (vy * n.Y) + (vz * n.Z);
            double v2 = (vx * vx) + (vy * vy) + (vz * vz);
            double n2 = ((double)n.X * n.X) + ((double)n.Y * n.Y) + ((double)n.Z * n.Z);
            if (!(vn < 0 && v2 >= minD2 && vn * vn >= m2 * v2 * n2))
            {
                return false;
            }
        }

        return true;
    }
}
