namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The four kinds of detail prop: a port of <c>DetailPropType_t</c>
/// (<c>src/public/bspfile.h</c>, read in
/// <c>src/utils/vbsp/detailobjects.cpp:145-164</c>).
/// </summary>
/// <remarks>
/// The emit path dispatches on only TWO of these:
/// <see cref="Model"/> goes to <c>AddDetailToLump</c> and everything else --
/// the two shapes included, through a <c>default</c> -- goes to
/// <c>AddDetailSpriteToLump</c> (<c>detailobjects.cpp:607-626</c>).
/// </remarks>
public enum DetailPropType
{
    /// <summary>
    /// <c>DETAIL_PROP_TYPE_MODEL</c>: a studio model, selected by the mere
    /// PRESENCE of a <c>model</c> key (<c>detailobjects.cpp:135-139</c>).
    /// </summary>
    Model = 0,

    /// <summary>
    /// <c>DETAIL_PROP_TYPE_SPRITE</c>: a camera-facing card. The default when
    /// <c>sprite_shape</c> is absent, and also what any UNRECOGNISED
    /// <c>sprite_shape</c> value falls back to
    /// (<c>detailobjects.cpp:157-164</c>).
    /// </summary>
    Sprite,

    /// <summary>
    /// <c>DETAIL_PROP_TYPE_SHAPE_CROSS</c>: <c>sprite_shape</c> is
    /// <c>cross</c>.
    /// </summary>
    ShapeCross,

    /// <summary>
    /// <c>DETAIL_PROP_TYPE_SHAPE_TRI</c>: <c>sprite_shape</c> is <c>tri</c>.
    /// </summary>
    ShapeTri,
}
