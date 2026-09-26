//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>g_anorms</c>: the 162 directions every ambient sample is taken along
/// </summary>
/// <remarks>
/// <para>
/// A fixed, arbitrary, un-improvable table. It is the vertex-normal set from
/// Quake II's model format, reused here purely as a roughly-even sphere
/// sampling; the angle between neighbours is about 14.55 degrees and
/// <see cref="ConeInnerAngleRadians"/> is half of that. Nothing about it is
/// derived, so there is no "correct" alternative to branch to -- a sampling
/// pattern is arbitrary, not wrong, and this is reproduced unconditionally.
/// </para>
/// <para>
/// The values are transcribed from the reference build at six decimals, which is how they
/// are written there: they are not unit length and were never meant to be.
/// <c>ComputeAmbientFromSphericalSamples</c> normalises nothing and divides the
/// accumulated cube by the summed dot products, so the table's small departures
/// from unit length cancel rather than accumulate.
/// </para>
/// </remarks>
public static class VertexNormals
{
    /// <summary>How many directions there are (<c>NUMVERTEXNORMALS</c>).</summary>
    /// <remarks>
    /// 162, which is not a multiple of four, and that matters to any caller
    /// that wants to trace these in SIMD packets: stock's own sky-visibility
    /// loop steps by four and clamps, so its last
    /// group is (160, 161, 161, 161) and direction 161 is traced three times.
    /// Leaf ambient's own loop is scalar and has no such quirk.
    /// </remarks>
    public const int Count = 162;

    /// <summary>
    /// Half the angle between neighbouring directions, in radians
    /// (<c>VERTEXNORMAL_CONE_INNER_ANGLE</c>).
    /// </summary>
    /// <remarks>
    /// <c>DEG2RAD(7.275)</c>, and <c>DEG2RAD</c> is
    /// <c>x * (M_PI_F / 180.f)</c> in float. Written out in that order because
    /// <c>CalcRayAmbientLighting</c> takes <c>tan</c> of it and the result
    /// scales the cone radius that decides the point-sample/average blend.
    /// </remarks>
    public const float ConeInnerAngleRadians = 7.275f * (MathF.PI / 180.0f);

    /// <summary>The 162 directions.</summary>
    private static readonly ImmutableArray<Vec3> Directions =
    [
        new(-0.525731f, 0.000000f, 0.850651f),
        new(-0.442863f, 0.238856f, 0.864188f),
        new(-0.295242f, 0.000000f, 0.955423f),
        new(-0.309017f, 0.500000f, 0.809017f),
        new(-0.162460f, 0.262866f, 0.951056f),
        new(0.000000f, 0.000000f, 1.000000f),
        new(0.000000f, 0.850651f, 0.525731f),
        new(-0.147621f, 0.716567f, 0.681718f),
        new(0.147621f, 0.716567f, 0.681718f),
        new(0.000000f, 0.525731f, 0.850651f),
        new(0.309017f, 0.500000f, 0.809017f),
        new(0.525731f, 0.000000f, 0.850651f),
        new(0.295242f, 0.000000f, 0.955423f),
        new(0.442863f, 0.238856f, 0.864188f),
        new(0.162460f, 0.262866f, 0.951056f),
        new(-0.681718f, 0.147621f, 0.716567f),
        new(-0.809017f, 0.309017f, 0.500000f),
        new(-0.587785f, 0.425325f, 0.688191f),
        new(-0.850651f, 0.525731f, 0.000000f),
        new(-0.864188f, 0.442863f, 0.238856f),
        new(-0.716567f, 0.681718f, 0.147621f),
        new(-0.688191f, 0.587785f, 0.425325f),
        new(-0.500000f, 0.809017f, 0.309017f),
        new(-0.238856f, 0.864188f, 0.442863f),
        new(-0.425325f, 0.688191f, 0.587785f),
        new(-0.716567f, 0.681718f, -0.147621f),
        new(-0.500000f, 0.809017f, -0.309017f),
        new(-0.525731f, 0.850651f, 0.000000f),
        new(0.000000f, 0.850651f, -0.525731f),
        new(-0.238856f, 0.864188f, -0.442863f),
        new(0.000000f, 0.955423f, -0.295242f),
        new(-0.262866f, 0.951056f, -0.162460f),
        new(0.000000f, 1.000000f, 0.000000f),
        new(0.000000f, 0.955423f, 0.295242f),
        new(-0.262866f, 0.951056f, 0.162460f),
        new(0.238856f, 0.864188f, 0.442863f),
        new(0.262866f, 0.951056f, 0.162460f),
        new(0.500000f, 0.809017f, 0.309017f),
        new(0.238856f, 0.864188f, -0.442863f),
        new(0.262866f, 0.951056f, -0.162460f),
        new(0.500000f, 0.809017f, -0.309017f),
        new(0.850651f, 0.525731f, 0.000000f),
        new(0.716567f, 0.681718f, 0.147621f),
        new(0.716567f, 0.681718f, -0.147621f),
        new(0.525731f, 0.850651f, 0.000000f),
        new(0.425325f, 0.688191f, 0.587785f),
        new(0.864188f, 0.442863f, 0.238856f),
        new(0.688191f, 0.587785f, 0.425325f),
        new(0.809017f, 0.309017f, 0.500000f),
        new(0.681718f, 0.147621f, 0.716567f),
        new(0.587785f, 0.425325f, 0.688191f),
        new(0.955423f, 0.295242f, 0.000000f),
        new(1.000000f, 0.000000f, 0.000000f),
        new(0.951056f, 0.162460f, 0.262866f),
        new(0.850651f, -0.525731f, 0.000000f),
        new(0.955423f, -0.295242f, 0.000000f),
        new(0.864188f, -0.442863f, 0.238856f),
        new(0.951056f, -0.162460f, 0.262866f),
        new(0.809017f, -0.309017f, 0.500000f),
        new(0.681718f, -0.147621f, 0.716567f),
        new(0.850651f, 0.000000f, 0.525731f),
        new(0.864188f, 0.442863f, -0.238856f),
        new(0.809017f, 0.309017f, -0.500000f),
        new(0.951056f, 0.162460f, -0.262866f),
        new(0.525731f, 0.000000f, -0.850651f),
        new(0.681718f, 0.147621f, -0.716567f),
        new(0.681718f, -0.147621f, -0.716567f),
        new(0.850651f, 0.000000f, -0.525731f),
        new(0.809017f, -0.309017f, -0.500000f),
        new(0.864188f, -0.442863f, -0.238856f),
        new(0.951056f, -0.162460f, -0.262866f),
        new(0.147621f, 0.716567f, -0.681718f),
        new(0.309017f, 0.500000f, -0.809017f),
        new(0.425325f, 0.688191f, -0.587785f),
        new(0.442863f, 0.238856f, -0.864188f),
        new(0.587785f, 0.425325f, -0.688191f),
        new(0.688191f, 0.587785f, -0.425325f),
        new(-0.147621f, 0.716567f, -0.681718f),
        new(-0.309017f, 0.500000f, -0.809017f),
        new(0.000000f, 0.525731f, -0.850651f),
        new(-0.525731f, 0.000000f, -0.850651f),
        new(-0.442863f, 0.238856f, -0.864188f),
        new(-0.295242f, 0.000000f, -0.955423f),
        new(-0.162460f, 0.262866f, -0.951056f),
        new(0.000000f, 0.000000f, -1.000000f),
        new(0.295242f, 0.000000f, -0.955423f),
        new(0.162460f, 0.262866f, -0.951056f),
        new(-0.442863f, -0.238856f, -0.864188f),
        new(-0.309017f, -0.500000f, -0.809017f),
        new(-0.162460f, -0.262866f, -0.951056f),
        new(0.000000f, -0.850651f, -0.525731f),
        new(-0.147621f, -0.716567f, -0.681718f),
        new(0.147621f, -0.716567f, -0.681718f),
        new(0.000000f, -0.525731f, -0.850651f),
        new(0.309017f, -0.500000f, -0.809017f),
        new(0.442863f, -0.238856f, -0.864188f),
        new(0.162460f, -0.262866f, -0.951056f),
        new(0.238856f, -0.864188f, -0.442863f),
        new(0.500000f, -0.809017f, -0.309017f),
        new(0.425325f, -0.688191f, -0.587785f),
        new(0.716567f, -0.681718f, -0.147621f),
        new(0.688191f, -0.587785f, -0.425325f),
        new(0.587785f, -0.425325f, -0.688191f),
        new(0.000000f, -0.955423f, -0.295242f),
        new(0.000000f, -1.000000f, 0.000000f),
        new(0.262866f, -0.951056f, -0.162460f),
        new(0.000000f, -0.850651f, 0.525731f),
        new(0.000000f, -0.955423f, 0.295242f),
        new(0.238856f, -0.864188f, 0.442863f),
        new(0.262866f, -0.951056f, 0.162460f),
        new(0.500000f, -0.809017f, 0.309017f),
        new(0.716567f, -0.681718f, 0.147621f),
        new(0.525731f, -0.850651f, 0.000000f),
        new(-0.238856f, -0.864188f, -0.442863f),
        new(-0.500000f, -0.809017f, -0.309017f),
        new(-0.262866f, -0.951056f, -0.162460f),
        new(-0.850651f, -0.525731f, 0.000000f),
        new(-0.716567f, -0.681718f, -0.147621f),
        new(-0.716567f, -0.681718f, 0.147621f),
        new(-0.525731f, -0.850651f, 0.000000f),
        new(-0.500000f, -0.809017f, 0.309017f),
        new(-0.238856f, -0.864188f, 0.442863f),
        new(-0.262866f, -0.951056f, 0.162460f),
        new(-0.864188f, -0.442863f, 0.238856f),
        new(-0.809017f, -0.309017f, 0.500000f),
        new(-0.688191f, -0.587785f, 0.425325f),
        new(-0.681718f, -0.147621f, 0.716567f),
        new(-0.442863f, -0.238856f, 0.864188f),
        new(-0.587785f, -0.425325f, 0.688191f),
        new(-0.309017f, -0.500000f, 0.809017f),
        new(-0.147621f, -0.716567f, 0.681718f),
        new(-0.425325f, -0.688191f, 0.587785f),
        new(-0.162460f, -0.262866f, 0.951056f),
        new(0.442863f, -0.238856f, 0.864188f),
        new(0.162460f, -0.262866f, 0.951056f),
        new(0.309017f, -0.500000f, 0.809017f),
        new(0.147621f, -0.716567f, 0.681718f),
        new(0.000000f, -0.525731f, 0.850651f),
        new(0.425325f, -0.688191f, 0.587785f),
        new(0.587785f, -0.425325f, 0.688191f),
        new(0.688191f, -0.587785f, 0.425325f),
        new(-0.955423f, 0.295242f, 0.000000f),
        new(-0.951056f, 0.162460f, 0.262866f),
        new(-1.000000f, 0.000000f, 0.000000f),
        new(-0.850651f, 0.000000f, 0.525731f),
        new(-0.955423f, -0.295242f, 0.000000f),
        new(-0.951056f, -0.162460f, 0.262866f),
        new(-0.864188f, 0.442863f, -0.238856f),
        new(-0.951056f, 0.162460f, -0.262866f),
        new(-0.809017f, 0.309017f, -0.500000f),
        new(-0.864188f, -0.442863f, -0.238856f),
        new(-0.951056f, -0.162460f, -0.262866f),
        new(-0.809017f, -0.309017f, -0.500000f),
        new(-0.681718f, 0.147621f, -0.716567f),
        new(-0.681718f, -0.147621f, -0.716567f),
        new(-0.850651f, 0.000000f, -0.525731f),
        new(-0.688191f, 0.587785f, -0.425325f),
        new(-0.587785f, 0.425325f, -0.688191f),
        new(-0.425325f, 0.688191f, -0.587785f),
        new(-0.425325f, -0.688191f, -0.587785f),
        new(-0.587785f, -0.425325f, -0.688191f),
        new(-0.688191f, -0.587785f, -0.425325f),
    ];

    /// <summary>The directions, in the table's own order.</summary>
    /// <remarks>
    /// Order is load-bearing: <c>ComputeAmbientFromSphericalSamples</c>
    /// accumulates into the six box directions in this sequence and float
    /// addition is not associative, so a reordering changes the encoded cube.
    /// </remarks>
    public static ReadOnlySpan<Vec3> All => Directions.AsSpan();
}
