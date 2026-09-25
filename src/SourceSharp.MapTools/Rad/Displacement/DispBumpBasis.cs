using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// <c>PreGetBumpNormalsForDisp</c> (<c>vrad.cpp:1494</c>): the texture axes a
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
/// Read by the displacement patch radial (<c>vraddisps.cpp:1132</c>) and by
/// bounce (<c>vrad.cpp:1566</c>, lane 4d).
/// </para>
/// </remarks>
public static class DispBumpBasis
{
    /// <summary>The axis agreement threshold, <c>vrad.cpp:1507</c>.</summary>
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

        // :1507-1515. fabs of a float -> the double fabs; the compare is in double.
        bool convert = Math.Abs(Vec3.Dot(texU, lightU)) < AxisDotEpsilon
            || Math.Abs(Vec3.Dot(texV, lightV)) < AxisDotEpsilon;
        if (!convert)
        {
            return (texU, texV, normal);
        }

        // matrix3x4_t(x, y, z, origin) puts the axes in COLUMNS; ConcatTransforms
        // (mathlib_base.cpp:658) is light * tex, each output row
        // a0*B.row0 + (a1*B.row1 + a2*B.row2).
        float[,] a = Columns(lightU, lightV, normal);
        float[,] b = Columns(texU, texV, normal);
        float[,] m = new float[3, 3];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[r, c] = (a[r, 0] * b[0, c]) + ((a[r, 1] * b[1, c]) + (a[r, 2] * b[2, c]));
            }
        }

        // MatrixGetColumn.
        return (
            new Vec3(m[0, 0], m[1, 0], m[2, 0]),
            new Vec3(m[0, 1], m[1, 1], m[2, 1]),
            new Vec3(m[0, 2], m[1, 2], m[2, 2]));
    }

    private static float[,] Columns(Vec3 x, Vec3 y, Vec3 z) => new float[,]
    {
        { x.X, y.X, z.X },
        { x.Y, y.Y, z.Y },
        { x.Z, y.Z, z.Z },
    };
}
