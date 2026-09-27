//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// <c>PreGetBumpNormalsForDisp</c>: the texture axes a
/// displacement's bump basis is built from, rotated into the lightmap frame
/// when the two disagree.
/// </summary>
/// <remarks>
/// <para>
/// A displacement's lightmap axes need not follow its texture axes (vbsp may
/// swap them, <c>SwapLightmapAxes</c>). When either pair of unit axes is more
/// than ~2.6 degrees apart (<c>|dot| &lt; 0.999</c>) stock concatenates the
/// lightmap frame with the texture frame -- both with the normal as the third
/// column -- and reads the result's columns back as the u axis, v axis and
/// NORMAL, so the normal it was handed comes back changed. Otherwise the unit
/// texture axes pass through and the normal is untouched.
/// </para>
/// <para>
/// Read by the displacement patch radial and by
/// Bounce(lane 4d).
/// </para>
/// </remarks>
public static class DispBumpBasis
{
    /// <summary>The axis agreement threshold.</summary>
    public const float AxisDotEpsilon = 0.999f;

    /// <summary><c>PreGetBumpNormalsForDisp</c>.</summary>
    /// <param name="tex">The face's texinfo.</param>
    /// <param name="normal">The normal to carry through (it may be rotated).</param>
    /// <param name="stockNormalise">Whether the axes are normalised with stock's estimate.</param>
    /// <returns>The u axis, the v axis, and the (possibly rotated) normal.</returns>
    public static (Vec3 U, Vec3 V, Vec3 Normal) PreGetBumpNormals(in TexInfo tex, Vec3 normal, bool stockNormalise)
    {
        FloatArray8 t = tex.TextureVecsTexelsPerWorldUnits;
        FloatArray8 l = tex.LightmapVecsLuxelsPerWorldUnits;
        Vec3 texU = VradDispSurface.Normalise(new Vec3(t[0], t[1], t[2]), stockNormalise);
        Vec3 texV = VradDispSurface.Normalise(new Vec3(t[4], t[5], t[6]), stockNormalise);
        Vec3 lightU = VradDispSurface.Normalise(new Vec3(l[0], l[1], l[2]), stockNormalise);
        Vec3 lightV = VradDispSurface.Normalise(new Vec3(l[4], l[5], l[6]), stockNormalise);

        // fabs of a float widens to double; the compare is in double.
        bool convert = Math.Abs(Vec3.Dot(texU, lightU)) < AxisDotEpsilon
            || Math.Abs(Vec3.Dot(texV, lightV)) < AxisDotEpsilon;
        if (!convert)
        {
            return (texU, texV, normal);
        }

        // The lightmap frame and the texture frame each hold the axes in
        // COLUMNS (u, v, normal); the result is light * tex, each output row
        // a0*B.row0 + (a1*B.row1 + a2*B.row2), and its columns are read back.
        // Column c of the product is therefore
        // lightU * B_c.X + (lightV * B_c.Y + normal * B_c.Z), where B_c is the
        // texture frame's column c. It is written out per column rather than
        // through 3x3 arrays because this runs once per displacement sample
        // and the arrays were three heap allocations each time; the
        // arithmetic and its association order are unchanged, so the result
        // is bit-identical.
        return (
            ProductColumn(lightU, lightV, normal, texU),
            ProductColumn(lightU, lightV, normal, texV),
            ProductColumn(lightU, lightV, normal, normal));
    }

    /// <summary>
    /// One column of the frame product: <paramref name="b"/> is the texture
    /// frame's column, <paramref name="a0"/>..<paramref name="a2"/> the
    /// lightmap frame's columns.
    /// </summary>
    private static Vec3 ProductColumn(Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b) => new(
        (a0.X * b.X) + ((a1.X * b.Y) + (a2.X * b.Z)),
        (a0.Y * b.X) + ((a1.Y * b.Y) + (a2.Y * b.Z)),
        (a0.Z * b.X) + ((a1.Z * b.Y) + (a2.Z * b.Z)));
}
