//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// The tolerances the stock compilers were tuned against, each named after the
/// macro it comes from.
/// </summary>
/// <remarks>
/// <para>
/// These are NOT tunables. A clip epsilon decides which side of a plane a
/// vertex lands on, which decides how a brush splits, which decides the node
/// tree, which decides the portal set, which decides PVS. Every published map
/// in the format was built with these numbers; changing one does not make the
/// output "more accurate", it makes it a different map.
/// </para>
/// <para>
/// The stored type of the literal is part of the value and is preserved here.
/// <c>ON_EPSILON</c> is written <c>0.1</c> — a <c>double</c> — and that matters
/// because <c>0.1</c> and <c>0.1f</c> are different numbers:
/// <c>0.1f</c> is 0.100000001490116119384765625 and <c>0.1</c> is
/// 0.1000000000000000055511151231257827. Where the reference compares a
/// <c>vec_t</c> (a <c>float</c>) against the bare macro, the float is promoted
/// and the comparison happens in <c>double</c>; where the macro is passed
/// through a <c>vec_t epsilon</c> parameter it is narrowed to <c>float</c>
/// first and the comparison happens in <c>float</c>. Both spellings are
/// provided below and the ported code uses whichever one its call site used.
/// </para>
/// <para>
/// The full census, for the phases that come later. Eighteen named
/// <c>*_EPSILON</c> macros are reachable in the reference toolchain:
/// </para>
/// <list type="bullet">
/// <item><description><c>ON_EPSILON</c> 0.1</description></item>
/// <item><description><c>CLIP_EPSILON</c> 0.1</description></item>
/// <item><description><c>PLANESIDE_EPSILON</c> 0.001</description></item>
/// <item><description><c>RENDER_NORMAL_EPSILON</c> 0.00001</description></item>
/// <item><description><c>RENDER_DIST_EPSILON</c> 0.01f</description></item>
/// <item><description><c>BRUSH_CLIP_EPSILON</c> 0.01f</description></item>
/// <item><description><c>INTEGRAL_EPSILON</c> 0.01</description></item>
/// <item><description><c>POINT_EPSILON</c> 0.1</description></item>
/// <item><description><c>OFF_EPSILON</c> 0.25</description></item>
/// <item><description><c>CONTINUOUS_EPSILON</c> 0.001</description></item>
/// <item><description><c>BASE_WINDING_EPSILON</c> 0.001</description></item>
/// <item><description><c>SPLIT_WINDING_EPSILON</c> 0.001</description></item>
/// <item><description><c>TRANSFER_EPSILON</c> 0.0000001</description></item>
/// <item><description><c>TEST_EPSILON</c> 0.1</description></item>
/// <item><description><c>PLANE_TEST_EPSILON</c> 0.01</description></item>
/// <item><description><c>TRIEDGE_EPSILON</c> 0.001f</description></item>
/// <item><description><c>DIST_EPSILON</c> 0.03125</description></item>
/// <item><description><c>EQUAL_EPSILON</c> 0.001</description></item>
/// </list>
/// <para>
/// Only the ones this phase's code actually evaluates are declared as members;
/// the rest are listed so the later lanes port a number that is already written
/// down rather than one recalled from memory. The unnamed ones get names here
/// too: see <see cref="ColinearDotThreshold"/>.
/// </para>
/// </remarks>
public static class GeometryEpsilons
{
    /// <summary>
    /// The point-on-plane tolerance, in the <c>double</c> width the macro has.
    /// </summary>
    /// <remarks>
    /// The reference declares <c>#define ON_EPSILON 0.1</c>. Use this
    /// spelling wherever the reference writes the macro directly in a
    /// comparison against a <c>vec_t</c> — <c>CheckWinding</c> and
    /// <c>WindingOnPlaneSide</c> both do, and both therefore compare in
    /// <c>double</c>.
    /// </remarks>
    public const double OnEpsilon = 0.1;

    /// <summary>
    /// The point-on-plane tolerance as it arrives through a <c>vec_t epsilon</c>
    /// parameter.
    /// </summary>
    /// <remarks>
    /// <c>ChopWinding</c> passes <c>ON_EPSILON</c> to
    /// <c>ClipWindingEpsilon</c>, whose <c>epsilon</c> is a <c>vec_t</c>. The
    /// narrowing happens at the call, so the comparison inside the clipper is a
    /// float one against 0.100000001490116119384765625.
    /// </remarks>
    public const float OnEpsilonFloat = 0.1f;

    /// <summary>
    /// How close to axial a normal must be before <c>SnapVector</c> snaps it.
    /// </summary>
    /// <remarks>
    /// The reference declares <c>#define RENDER_NORMAL_EPSILON 0.00001</c>.
    /// A <c>double</c> literal, and <c>SnapVector</c> compares
    /// <c>fabs(normal[i] - 1)</c> — already a <c>double</c> — against it.
    /// </remarks>
    public const double RenderNormalEpsilon = 0.00001;

    /// <summary>
    /// The same tolerance as it arrives through <c>PlaneEqual</c>'s
    /// <c>float normalEpsilon</c> parameter.
    /// </summary>
    /// <remarks>
    /// <c>FindFloatPlane</c> passes the bare <c>RENDER_NORMAL_EPSILON</c> macro
    /// to <c>PlaneEqual</c>, whose parameter is declared <c>float</c>, so the
    /// double is narrowed at the call and promoted back inside. The narrowing
    /// is not reversible, so this is a different threshold from
    /// <see cref="RenderNormalEpsilon"/> and the two are kept apart.
    /// </remarks>
    public const float RenderNormalEpsilonFloat = 0.00001f;

    /// <summary>
    /// How close to an integer a plane distance must be before it is rounded.
    /// </summary>
    /// <remarks>
    /// The reference declares <c>#define RENDER_DIST_EPSILON 0.01f</c>.
    /// Written with the <c>f</c> suffix, unlike its neighbour above, so it
    /// is 0.00999999977648258 and not 0.01. <c>SnapPlane</c> compares a
    /// <c>double</c> from <c>fabs</c> against it, so the float is promoted back
    /// up and the 0.01f value is what is compared.
    /// </remarks>
    public const float RenderDistEpsilon = 0.01f;

    /// <summary>
    /// The dot product above which <c>RemoveColinearPoints</c> drops a vertex.
    /// </summary>
    /// <remarks>
    /// The reference's colinear-point test reads
    /// <c>if (DotProduct(v1, v2) &lt; 0.999)</c>. Unnamed in the reference, and
    /// a <c>double</c> literal compared against a <c>float</c> dot product, so
    /// the comparison is done in <c>double</c> against
    /// 0.99899999999999999911182158029987 and not against <c>0.999f</c>.
    /// </remarks>
    public const double ColinearDotThreshold = 0.999;

    /// <summary>
    /// The largest coordinate the world may use, and the half-extent
    /// <c>BaseWindingForPlane</c> is scaled from.
    /// </summary>
    /// <remarks>
    /// The reference layout declares <c>#define MAX_COORD_INTEGER
    /// (16384)</c>. Not an epsilon, but it lives at the same kind of
    /// load-bearing constant: <c>BaseWindingForPlane</c> scales its basis
    /// vectors by <c>MAX_COORD_INTEGER*4</c>, so the starting quad is 65536
    /// units out and every later clip inherits that magnitude's rounding.
    /// </remarks>
    public const float MaxCoordInteger = 16384f;

    /// <summary>The negation of <see cref="MaxCoordInteger"/>.</summary>
    /// <remarks>
    /// The reference layout declares
    /// <c>#define MIN_COORD_INTEGER (-MAX_COORD_INTEGER)</c>.
    /// <c>CheckWinding</c> rejects any vertex outside this range.
    /// </remarks>
    public const float MinCoordInteger = -16384f;

    /// <summary>
    /// How far out <c>BaseWindingForPlane</c> pushes its starting quad.
    /// </summary>
    /// <remarks>
    /// The reference scales both basis vectors of its base winding by
    /// <c>(MAX_COORD_INTEGER*4)</c>. The multiplication is done on
    /// <c>int</c>s and the result converted, so it is exactly 65536.
    /// </remarks>
    public const float BaseWindingExtent = 65536f;
}
