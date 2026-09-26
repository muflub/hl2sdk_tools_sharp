//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One model entry inside a detail group.
/// </summary>
/// <param name="Type">
/// Which of the four placement kinds this is. Decided by whether a
/// <c>model</c> key was present and, failing that, by <c>sprite_shape</c>
/// in the reference loader.
/// </param>
/// <param name="ModelName">
/// The <c>model</c> key's value, or null for a sprite. Its PRESENCE is what
/// selects <see cref="DetailPropType.Model"/>, and when it is present none of
/// the sprite keys in the same block are read at all — the reference reader
/// commits to the model branch before touching them.
/// </param>
/// <param name="CumulativeAmount">
/// <c>m_Amount</c>: a point on a cumulative distribution, NOT this model's own
/// weight. The reference loader adds the running total to each <c>amount</c>
/// as it goes, and divides the lot by the total only when that total EXCEEDS
/// 1 -- so a group whose amounts sum to less than 1 leaves the remainder as
/// empty space, and <c>SelectDetail</c> returns -1 for it and places nothing.
/// </param>
/// <param name="Upright">
/// <c>upright</c>, defaulting to 0. When set, the prop takes a random yaw
/// only; otherwise the reference placement code orients it to the surface
/// normal.
/// </param>
/// <param name="MinCosAngle">
/// The COSINE of <c>minAngle</c>, which defaults to 180 degrees and so to -1.
/// Stored as a cosine because that is what the placement test compares
/// against.
/// </param>
/// <param name="MaxCosAngle">
/// The cosine of <c>maxAngle</c>. If it ends up larger than
/// <paramref name="MinCosAngle"/> the reference loader forces the two equal.
/// </param>
/// <param name="Orientation">
/// <c>detailOrientation</c>, defaulting to 0.
/// </param>
/// <param name="SwayAmount">
/// <c>sway</c> clamped to 0..1 and scaled by 255 by the reference loader,
/// which is a percentage of <c>cl_detail_max_sway</c> rather than a distance.
/// </param>
/// <param name="ShapeAngle">
/// <c>shape_angle</c>, read as an INT with no clamp and stored in a byte, so
/// a value of 360 wraps to 104.
/// </param>
/// <param name="ShapeSize">
/// <c>shape_size</c> clamped to 0..1 and scaled by 255.
/// </param>
/// <param name="RandomScaleStdDev">
/// <c>spriterandomscale</c>, defaulting to 0. Used as a Gaussian standard
/// deviation at placement, and skipped entirely when zero.
/// </param>
public sealed record DetailObjectModel(
    DetailPropType Type,
    string? ModelName,
    float CumulativeAmount,
    bool Upright,
    float MinCosAngle,
    float MaxCosAngle,
    int Orientation,
    byte SwayAmount,
    byte ShapeAngle,
    byte ShapeSize,
    float RandomScaleStdDev);
