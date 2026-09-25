namespace SourceSharp.MapTools.Rad;

/// <summary>
/// The tag bits vrad packs into a caster triangle's identity.
/// </summary>
/// <remarks>
/// <para>
/// <c>vrad.h:300-302</c>. The identity is not an index into anything: it is a
/// set of flags with, for a static prop, the prop's own index in the low bits.
/// That packing is what lets a shadow ray skip the prop it started on --
/// <c>Trace4Rays</c> is called with <c>TRACE_ID_STATICPROP | propIndex</c>
/// (<c>trace.cpp:164</c>) and the traversal drops any triangle whose id matches.
/// </para>
/// <para>
/// So the low 24 bits are meaningful ONLY when
/// <see cref="StaticProp"/> is set. Brush, sky and displacement triangles all
/// carry the bare flag and are indistinguishable from each other inside their
/// class -- stock never needs to know which brush a triangle came from.
/// </para>
/// </remarks>
public static class TraceId
{
    /// <summary>A sky face: blocks nothing, but reports the sky. <c>vrad.h:300</c>.</summary>
    public const int Sky = 0x01000000;

    /// <summary>An ordinary light blocker: world brush or displacement. <c>vrad.h:301</c>.</summary>
    public const int Opaque = 0x02000000;

    /// <summary>A static prop; the prop index is in the low bits. <c>vrad.h:302</c>.</summary>
    public const int StaticProp = 0x04000000;

    /// <summary>The bits a static prop's index occupies.</summary>
    /// <remarks>
    /// Not a stock constant. Stock ORs the index in and masks it out by
    /// implication; naming the mask is what lets a fact assert that a prop
    /// index never collides with a tag bit.
    /// </remarks>
    public const int PropIndexMask = 0x00FFFFFF;

    /// <summary>Whether an identity names a static prop.</summary>
    /// <param name="id">The triangle identity.</param>
    /// <returns>True when <see cref="StaticProp"/> is set.</returns>
    public static bool IsStaticProp(int id) => (id & StaticProp) != 0;

    /// <summary>Whether an identity names a sky face.</summary>
    /// <param name="id">The triangle identity.</param>
    /// <returns>True when <see cref="Sky"/> is set.</returns>
    public static bool IsSky(int id) => (id & Sky) != 0;

    /// <summary>Whether an identity names an opaque blocker.</summary>
    /// <param name="id">The triangle identity.</param>
    /// <returns>True when <see cref="Opaque"/> is set.</returns>
    public static bool IsOpaque(int id) => (id & Opaque) != 0;

    /// <summary>The prop index carried by a static prop's identity.</summary>
    /// <param name="id">The triangle identity.</param>
    /// <returns>The index in the low 24 bits.</returns>
    /// <remarks>
    /// Read this only after <see cref="IsStaticProp"/>. It returns a number for
    /// any input, and on a brush triangle that number is zero -- a valid prop
    /// index, which is exactly the shape of mistake this project has made
    /// before.
    /// </remarks>
    public static int PropIndex(int id) => id & PropIndexMask;
}
