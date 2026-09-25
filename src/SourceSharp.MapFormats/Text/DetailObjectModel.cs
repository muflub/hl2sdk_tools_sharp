namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One model entry inside a detail group.
/// </summary>
/// <param name="Type">
/// Which of the four placement kinds this is. Decided by whether a
/// <c>model</c> key was present and, failing that, by <c>sprite_shape</c>
/// (<c>src/utils/vbsp/detailobjects.cpp:135-164</c>).
/// </param>
/// <param name="ModelName">
/// The <c>model</c> key's value, or null for a sprite. Its PRESENCE is what
/// selects <see cref="DetailPropType.Model"/>, and when it is present none of
/// the sprite keys in the same block are read at all
/// (<c>detailobjects.cpp:135-139</c> against the <c>else</c> at
/// <c>:140-213</c>).
/// </param>
/// <param name="CumulativeAmount">
/// <c>m_Amount</c>: a point on a cumulative distribution, NOT this model's own
/// weight. <c>detailobjects.cpp:215-216</c> adds the running total to each
/// <c>amount</c> as it goes, and <c>:240-247</c> divides the lot by the total
/// only when that total EXCEEDS 1 -- so a group whose amounts sum to less than
/// 1 leaves the remainder as empty space, and <c>SelectDetail</c>
/// (<c>:360-373</c>) returns -1 for it and places nothing.
/// </param>
/// <param name="Upright">
/// <c>upright</c>, defaulting to 0 (<c>detailobjects.cpp:218-222</c>). When
/// set, the prop takes a random yaw only; otherwise it is oriented to the
/// surface normal (<c>:570-602</c>).
/// </param>
/// <param name="MinCosAngle">
/// The COSINE of <c>minAngle</c>, which defaults to 180 degrees and so to -1
/// (<c>detailobjects.cpp:224-228</c>). Stored as a cosine because that is what
/// the placement test compares against.
/// </param>
/// <param name="MaxCosAngle">
/// The cosine of <c>maxAngle</c>. If it ends up larger than
/// <paramref name="MinCosAngle"/> the two are forced equal
/// (<c>detailobjects.cpp:231-235</c>).
/// </param>
/// <param name="Orientation">
/// <c>detailOrientation</c>, defaulting to 0
/// (<c>detailobjects.cpp:229</c>).
/// </param>
/// <param name="SwayAmount">
/// <c>sway</c> clamped to 0..1 and scaled by 255
/// (<c>detailobjects.cpp:200-202</c>), which is a percentage of
/// <c>cl_detail_max_sway</c> rather than a distance.
/// </param>
/// <param name="ShapeAngle">
/// <c>shape_angle</c>, read as an INT with no clamp and stored in a byte
/// (<c>detailobjects.cpp:206</c>), so a value of 360 wraps to 104.
/// </param>
/// <param name="ShapeSize">
/// <c>shape_size</c> clamped to 0..1 and scaled by 255
/// (<c>detailobjects.cpp:208-211</c>).
/// </param>
/// <param name="RandomScaleStdDev">
/// <c>spriterandomscale</c>, defaulting to 0
/// (<c>detailobjects.cpp:198</c>). Used as a Gaussian standard deviation at
/// placement, and skipped entirely when zero (<c>:617-621</c>).
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
