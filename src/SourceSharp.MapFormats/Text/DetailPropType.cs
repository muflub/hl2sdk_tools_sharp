//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The four kinds of detail prop: the reference <c>DetailPropType_t</c>,
/// read by the reference loader.
/// </summary>
/// <remarks>
/// The reference emit path dispatches on only TWO of these:
/// <see cref="Model"/> goes to <c>AddDetailToLump</c> and everything else --
/// the two shapes included, through a <c>default</c> -- goes to
/// <c>AddDetailSpriteToLump</c>.
/// </remarks>
public enum DetailPropType
{
    /// <summary>
    /// <c>DETAIL_PROP_TYPE_MODEL</c>: a studio model, selected by the mere
    /// PRESENCE of a <c>model</c> key.
    /// </summary>
    Model = 0,

    /// <summary>
    /// <c>DETAIL_PROP_TYPE_SPRITE</c>: a camera-facing card. The default when
    /// <c>sprite_shape</c> is absent, and also what any UNRECOGNISED
    /// <c>sprite_shape</c> value falls back to.
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
