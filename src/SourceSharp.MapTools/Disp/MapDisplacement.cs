//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// One displacement as the VMF describes it: <c>mapdispinfo_t</c>,
/// </summary>
/// <remarks>
/// <para>
/// This is the INPUT side. It holds what Hammer wrote and nothing derived:
/// the per-vertex field vectors, their distances, the per-vertex offsets, the
/// alphas and the triangle tags. <see cref="CoreDispInfo"/> is what those
/// become.
/// </para>
/// <para>
/// Stock keeps 2048 of these in a file-scope array sized for the largest power
/// whatever the actual power is — <c>float dispDists[MAX_DISPVERTS]</c> and
/// three <c>Vector</c> arrays the same, so 2048 * 289 * 40 bytes of
/// statically-reserved space. Here the arrays are sized to the power, which is
/// the only reason this type is a class and allocated per displacement.
/// </para>
/// </remarks>
public sealed class MapDisplacement : IMapDisplacement
{
    /// <summary>Builds an empty displacement of a given power.</summary>
    /// <param name="power">2, 3 or 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">The power is out of range.</exception>
    public MapDisplacement(int power)
    {
        if (power < PowerInfo.MinMapDispPower || power > PowerInfo.MaxMapDispPower)
        {
            throw new ArgumentOutOfRangeException(
                nameof(power),
                power,
                $"a displacement's power is {PowerInfo.MinMapDispPower} to "
                + $"{PowerInfo.MaxMapDispPower}.");
        }

        Power = power;

        int side = (1 << power) + 1;
        int verts = side * side;

        FieldVectors = new Vec3[verts];
        FieldDistances = new float[verts];
        VectorOffsets = new Vec3[verts];
        AlphaValues = new float[verts];
        TriangleTags = new ushort[(1 << power) * (1 << power) * 2];
    }

    /// <summary>The displacement power: 2, 3 or 4.</summary>
    public int Power { get; }

    /// <summary>The number of vertices, <c>(2^power + 1)^2</c>.</summary>
    public int VertCount => FieldDistances.Length;

    /// <summary>The number of triangles, <c>2 * (2^power)^2</c>.</summary>
    public int TriangleCount => TriangleTags.Length;

    /// <inheritdoc />
    public int EntityNumber { get; set; }

    /// <inheritdoc />
    public int BrushSideId { get; set; }

    /// <summary>
    /// The VMF's <c>startposition</c>: which corner of the base quad the field
    /// is anchored to.
    /// </summary>
    public Vec3 StartPosition { get; set; }

    /// <summary>The VMF's <c>flags</c>, which become the surface flags.</summary>
    public int Flags { get; set; }

    /// <summary>The VMF's <c>mintess</c>.</summary>
    /// <remarks>
    /// Read from the file and then NOT written to the BSP: vbsp overwrites
    /// <c>ddispinfo_t::minTess</c> with <c>flags | 0x80000000</c> and leaves
    /// the line that would have used this commented out
    /// It is kept because it is in the VMF.
    /// </remarks>
    public int MinTess { get; set; }

    /// <summary>The VMF's <c>smooth</c>, in degrees.</summary>
    public float SmoothingAngle { get; set; }

    /// <summary>The contents the base brush gave this displacement.</summary>
    /// <remarks>
    /// Set by <see cref="DisplacementLumpBuilder"/>, not by the VMF: stock
    /// copies it from <c>pMapDisp-&gt;face.contents</c>, which
    /// <c>DispGetFaceInfo</c> took from the brush.
    /// </remarks>
    public int Contents { get; set; }

    /// <summary>The VMF's <c>uaxis</c>, kept for older maps' start-index rule.</summary>
    /// <remarks>
    /// <c>GeneratePointStartIndexFromMappingAxes</c> is the old way of choosing
    /// the start corner and nothing in vbsp calls it — <c>InitSurf</c>, its
    /// only caller, is inside an <c>#if 0</c>. The
    /// axes are parsed because the key is in the file.
    /// </remarks>
    public Vec3 UAxis { get; set; }

    /// <summary>The VMF's <c>vaxis</c>.</summary>
    public Vec3 VAxis { get; set; }

    /// <summary>
    /// One unit direction per vertex, from the <c>normals</c> rows.
    /// </summary>
    public Vec3[] FieldVectors { get; }

    /// <summary>
    /// One distance per vertex along its direction, from the
    /// <c>distances</c> rows.
    /// </summary>
    public float[] FieldDistances { get; }

    /// <summary>
    /// One world-space offset per vertex, from the <c>offsets</c> rows.
    /// </summary>
    /// <remarks>
    /// Added to <c>direction * distance</c> BEFORE the result is renormalised,
    /// so a displacement with offsets has field vectors in the BSP that are not
    /// the ones in the VMF. Modern Hammer writes zeros here.
    /// </remarks>
    public Vec3[] VectorOffsets { get; }

    /// <summary>One blend alpha per vertex, from the <c>alphas</c> rows.</summary>
    public float[] AlphaValues { get; }

    /// <summary>
    /// One tag word per triangle, already collapsed to the BSP's
    /// <c>DISPTRI_TAG_*</c> encoding.
    /// </summary>
    /// <remarks>
    /// The VMF's own encoding is the <c>COREDISPTRI_TAG_*</c> one, which has
    /// separate "forced" bits, and the collapse happens during parsing
    /// Rather than on the way out. See
    /// <see cref="VmfDisplacementReader.CollapseTriangleTags"/>.
    /// </remarks>
    public ushort[] TriangleTags { get; }

    /// <summary>
    /// The combined field vector and distance one vertex contributes:
    /// And,.
    /// </summary>
    /// <param name="index">The vertex's flattened grid index.</param>
    /// <param name="stockNormalise">
    /// Reproduce stock's reciprocal-square-root ESTIMATE rather than dividing
    /// exactly. See <see cref="Options.StockQuirk.DispVertNormalise"/>.
    /// </param>
    /// <returns>The unit direction and the distance along it.</returns>
    /// <remarks>
    /// <para>
    /// The length is taken with <c>VectorLength</c>, which is an exact
    /// <c>sqrt</c>, and the direction with <c>VectorNormalize</c>, which is
    /// not — so even in stock the two are not derived from the same number.
    /// Only the direction moves under the compliance switch.
    /// </para>
    /// <para>
    /// Computed twice in stock, once for the lump
    /// (<c>EmitInitialDispInfos</c>) and once for the
    /// <see cref="CoreDispInfo"/> (<c>DispMapToCoreDispInfo</c>), from the same
    /// inputs by the same three lines. One function here, called from both.
    /// </para>
    /// </remarks>
    public (Vec3 Vector, float Distance) CombinedField(int index, bool stockNormalise)
    {
        Vec3 v = (FieldVectors[index] * FieldDistances[index]) + VectorOffsets[index];

        float dist = v.Length();

        Vec3 unit = stockNormalise
            ? v.NormaliseLikeStock().Normalised
            : v.Normalise().Normalised;

        return (unit, dist);
    }
}
