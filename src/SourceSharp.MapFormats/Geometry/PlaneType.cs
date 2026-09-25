namespace SourceSharp.MapFormats.Geometry;

/// <summary>
/// How a plane's normal is oriented, in the encoding the BSP's
/// <c>dplane_t.type</c> field stores.
/// </summary>
/// <remarks>
/// <c>public/mathlib/mathlib.h:175-181</c>. Values 0..2 are exactly axial and
/// 3..5 name the axis the normal leans closest to. The numbers are written into
/// the PLANES lump, so they are wire format, not an internal convenience:
/// <c>vbsp</c>'s <c>CreateNewFloatPlane</c> (<c>utils/vbsp/map.cpp:221</c>)
/// stores the result of the classification and the engine reads it back.
/// </remarks>
public enum PlaneType
{
    /// <summary>The normal is exactly +X or -X. <c>PLANE_X</c>, 0.</summary>
    X = 0,

    /// <summary>The normal is exactly +Y or -Y. <c>PLANE_Y</c>, 1.</summary>
    Y = 1,

    /// <summary>The normal is exactly +Z or -Z. <c>PLANE_Z</c>, 2.</summary>
    Z = 2,

    /// <summary>The normal leans mostly along X. <c>PLANE_ANYX</c>, 3.</summary>
    AnyX = 3,

    /// <summary>The normal leans mostly along Y. <c>PLANE_ANYY</c>, 4.</summary>
    AnyY = 4,

    /// <summary>The normal leans mostly along Z. <c>PLANE_ANYZ</c>, 5.</summary>
    AnyZ = 5,
}
