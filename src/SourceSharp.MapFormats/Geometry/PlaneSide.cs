namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// Which side of a plane something is on.
/// </summary>
/// <remarks>
/// <c>public/mathlib/mathlib.h:306-309</c>. The numbers are kept because stock
/// uses them as array indices, not only as labels: <c>ClipWindingEpsilon</c>
/// counts vertices with <c>counts[sides[i]]++</c>
/// (<c>utils/common/polylib.cpp:392</c>), so <see cref="Front"/> must be 0,
/// <see cref="Back"/> 1 and <see cref="On"/> 2 for the ported code to read like
/// its original. <see cref="Cross"/> is negative and is deliberately not an
/// index; mathlib.h:309 even says it exists "necessary for polylib.c".
/// </remarks>
public enum PlaneSide
{
    /// <summary>In front of the plane. <c>SIDE_FRONT</c>, 0.</summary>
    Front = 0,

    /// <summary>Behind the plane. <c>SIDE_BACK</c>, 1.</summary>
    Back = 1,

    /// <summary>Within the epsilon slab of the plane. <c>SIDE_ON</c>, 2.</summary>
    On = 2,

    /// <summary>
    /// Spanning the plane, with vertices on both sides. <c>SIDE_CROSS</c>, -2.
    /// </summary>
    Cross = -2,
}
