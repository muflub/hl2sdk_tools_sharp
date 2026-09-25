namespace SourceSharp.MapTools.Materials;

/// <summary>
/// <c>UTILMATLIB_OPACITY</c>'s three answers
/// </summary>
/// <remarks>
/// The order matters and is the C++'s: translucent is tested FIRST, so a
/// material that is both translucent and alpha-tested reports translucent.
/// </remarks>
public enum MaterialOpacity
{
    /// <summary><c>UTILMATLIB_OPAQUE</c>.</summary>
    Opaque = 0,

    /// <summary><c>UTILMATLIB_ALPHATEST</c>.</summary>
    AlphaTest,

    /// <summary><c>UTILMATLIB_TRANSLUCENT</c>.</summary>
    Translucent,
}
