using System.Numerics;
using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// IVP vector helpers that are real (non-inlined) functions in the binaries, so their grouping is
/// fixed regardless of caller.
/// </summary>
internal static class IvpVector
{
    /// <summary>
    /// <c>IVP_U_Point::inline_calc_cross_product</c> as emitted at SDK 001ff990 / TF2 002091a0:
    /// <c>a x b</c> with x = a.y*b.z - b.y*a.z, y = a.z*b.x - b.z*a.x, z = b.y*a.x - a.y*b.x.
    /// </summary>
    /// <typeparam name="T">Precision.</typeparam>
    /// <param name="ax">a.x.</param>
    /// <param name="ay">a.y.</param>
    /// <param name="az">a.z.</param>
    /// <param name="bx">b.x.</param>
    /// <param name="by">b.y.</param>
    /// <param name="bz">b.z.</param>
    /// <param name="cx">Result x.</param>
    /// <param name="cy">Result y.</param>
    /// <param name="cz">Result z.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cross<T>(T ax, T ay, T az, T bx, T by, T bz, out T cx, out T cy, out T cz)
        where T : unmanaged, IBinaryFloatingPointIeee754<T>
    {
        cx = (ay * bz) - (by * az);
        cy = (az * bx) - (bz * ax);
        cz = (by * ax) - (ay * bx);
    }

    /// <summary>
    /// <c>cvttss2si</c>/<c>cvttsd2si</c>: truncate toward zero, and the "integer indefinite"
    /// value <c>int.MinValue</c> for NaN or anything outside int's range (C#'s own conversion
    /// saturates instead, which would change the low byte IVP keeps).
    /// </summary>
    /// <param name="v">The value.</param>
    /// <returns>The x86 conversion result.</returns>
    public static int CvttToInt32(double v) =>
        double.IsNaN(v) || v >= 2147483648.0 || v <= -2147483649.0 ? int.MinValue : (int)v;
}
