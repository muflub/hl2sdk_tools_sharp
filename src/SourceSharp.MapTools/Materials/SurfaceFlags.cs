namespace SourceSharp.MapTools.Materials;

/// <summary>
/// <c>SURF_*</c> from the reference implementation: the flags a compiler
/// writes into a <c>texinfo_t</c>.
/// </summary>
/// <remarks>
/// Stored in a 32-bit field in the BSP but, as the header's own comment says,
/// read back into a short by the engine — so nothing above bit 15 is usable.
/// </remarks>
[Flags]
public enum SurfaceFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>
    /// <c>SURF_LIGHT</c>: the value holds the light strength. vrad's, never
    /// set by <c>FindMiptex</c>.
    /// </summary>
    Light = 0x0001,

    /// <summary>
    /// <c>SURF_SKY2D</c>: skylight and draw the 2D sky, but not the 3D skybox.
    /// </summary>
    Sky2D = 0x0002,

    /// <summary><c>SURF_SKY</c>: do not draw, but add to the skybox.</summary>
    Sky = 0x0004,

    /// <summary><c>SURF_WARP</c>: turbulent water warp.</summary>
    Warp = 0x0008,

    /// <summary>
    /// <c>SURF_TRANS</c>: sort as a translucent primitive.
    /// </summary>
    Trans = 0x0010,

    /// <summary>
    /// <c>SURF_NOPORTAL</c>: no portal may be placed on this surface.
    /// </summary>
    NoPortal = 0x0020,

    /// <summary><c>SURF_TRIGGER</c>: the surface is a trigger.</summary>
    Trigger = 0x0040,

    /// <summary><c>SURF_NODRAW</c>: do not reference the texture.</summary>
    NoDraw = 0x0080,

    /// <summary><c>SURF_HINT</c>: a primary BSP splitter.</summary>
    Hint = 0x0100,

    /// <summary>
    /// <c>SURF_SKIP</c>: ignore completely, allowing non-closed brushes.
    /// </summary>
    Skip = 0x0200,

    /// <summary><c>SURF_NOLIGHT</c>: do not calculate light.</summary>
    NoLight = 0x0400,

    /// <summary>
    /// <c>SURF_BUMPLIGHT</c>: three lightmaps, for bump mapping.
    /// </summary>
    BumpLight = 0x0800,

    /// <summary><c>SURF_NOSHADOWS</c>: do not receive shadows.</summary>
    NoShadows = 0x1000,

    /// <summary><c>SURF_NODECALS</c>: do not receive decals.</summary>
    NoDecals = 0x2000,

    /// <summary>
    /// <c>SURF_NOCHOP</c>: do not subdivide patches on this surface.
    /// </summary>
    NoChop = 0x4000,

    /// <summary><c>SURF_HITBOX</c>: the surface is part of a hitbox.</summary>
    Hitbox = 0x8000,
}
