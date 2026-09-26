//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// One light sample on a face: <c>sample_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// A sample is one cell of the face's lightmap grid CLIPPED to the face, so an
/// interior sample is a whole luxel and an edge sample is a sliver of one --
/// which is why it carries its own area, centroid and bounds rather than being
/// implied by <see cref="S"/> and <see cref="T"/>.
/// </para>
/// <para>
/// The clipped winding stock keeps in <c>sample_t::w</c> survives only for the
/// partial samples supersampling will need, in WORLD
/// space. Here it is a range of <see cref="FaceLight.SampleWindingPoints"/>
/// rather than a per-sample allocation: <see cref="WindingCount"/> is zero when
/// stock's pointer would be null.
/// </para>
/// </remarks>
public struct LightSample
{
    /// <summary>The luxel column this sample belongs to.</summary>
    public int S;

    /// <summary>The luxel row this sample belongs to.</summary>
    public int T;

    /// <summary>The centroid, lightmap-space s (<c>coord[0]</c>).</summary>
    public float CoordS;

    /// <summary>The centroid, lightmap-space t (<c>coord[1]</c>).</summary>
    public float CoordT;

    /// <summary>The clipped cell's lightmap-space bounds: min s.</summary>
    public float MinS;

    /// <summary>Min t.</summary>
    public float MinT;

    /// <summary>Max s.</summary>
    public float MaxS;

    /// <summary>Max t.</summary>
    public float MaxT;

    /// <summary>The centroid in world space (<c>pos</c>), model offset included.</summary>
    public Vec3 Position;

    /// <summary>
    /// The normal lighting was gathered with: the face normal on a flat face,
    /// The phong normal otherwise.
    /// </summary>
    public Vec3 Normal;

    /// <summary>The world-space area of the clipped cell.</summary>
    public float Area;

    /// <summary>
    /// Where this sample's world-space winding starts in
    /// <see cref="FaceLight.SampleWindingPoints"/>.
    /// </summary>
    public int WindingOffset;

    /// <summary>How many points it has; zero for "no winding".</summary>
    public int WindingCount;
}

/// <summary>
/// Everything direct lighting produced for one face: <c>facelight_t</c>
/// Plus the face's light styles.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hand-off to 4d, 4e and 4f.</b> Bounce (4d) reads the patches, which
/// <see cref="PatchLighting"/> has already fed from <see cref="Light"/>; the
/// radial filter and encoder (4f) read <see cref="Samples"/>, <see cref="Luxels"/>
/// and <see cref="Light"/> directly, exactly as <c>FinalLightFace</c> reads
/// <c>facelight[]</c>.
/// </para>
/// <para>
/// <see cref="Light"/> is indexed <c>[styleIndex * LightmapCount + normal]</c>
/// where <c>styleIndex</c> is a SLOT in <see cref="Styles"/>, not a light
/// style number, and <c>normal</c> is 0 for the flat lightmap and 1..3 for the
/// bump directions. A slot that was never allocated is null, which is stock's
/// null <c>light[k][n]</c>.
/// </para>
/// </remarks>
public sealed class FaceLight
{
    /// <summary>Creates an empty facelight.</summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="normalCount">1, or 4 on a bumped face.</param>
    public FaceLight(int faceNum, int normalCount)
    {
        FaceNum = faceNum;
        NormalCount = normalCount;
        Light = new LightingValue[]?[LightConstants.MaxLightmaps * BumpBasis.LightmapCount];
        Styles = [255, 255, 255, 255];
    }

    /// <summary>Creates a facelight around samples that already exist.</summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="normalCount">1, or 4 on a bumped face.</param>
    /// <param name="samples">The samples, taken over.</param>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <remarks>
    /// For a producer other than <see cref="FaceSampleBuilder"/> -- the
    /// displacement sampler 4e owns -- and for tests.
    /// </remarks>
    public FaceLight(int faceNum, int normalCount, LightSample[] samples)
        : this(faceNum, normalCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Samples = samples;
    }

    /// <summary>The face.</summary>
    public int FaceNum { get; }

    /// <summary>How many lightmaps per style: 1, or 4 on a <c>SURF_BUMPLIGHT</c> face.</summary>
    public int NormalCount { get; }

    /// <summary>The clipped samples, in t-major then s order.</summary>
    public LightSample[] Samples { get; internal set; } = [];

    /// <summary>
    /// The world-space position of every luxel, <c>width * height</c> of them,
    /// Row-major.
    /// </summary>
    public Vec3[] Luxels { get; internal set; } = [];

    /// <summary>
    /// <c>luxelNormals</c>: a DISPLACEMENT face's per-luxel surface normal,
    /// parallel to <see cref="Luxels"/>; empty
    /// for a brush face, which stock never gives one.
    /// </summary>
    public Vec3[] LuxelNormals { get; internal set; } = [];

    /// <summary>
    /// Backing store for the partial samples' world-space windings.
    /// </summary>
    public Vec3[] SampleWindingPoints { get; internal set; } = [];

    /// <summary><c>worldAreaPerLuxel</c>.</summary>
    public float WorldAreaPerLuxel { get; internal set; }

    /// <summary>
    /// The four light-style slots, 255 for empty, as <c>dface_t::styles</c> ends
    /// up after <c>BuildFacelights</c>.
    /// </summary>
    public byte[] Styles { get; }

    /// <summary>The light, per style slot and normal; see the type remarks.</summary>
    public LightingValue[]?[] Light { get; }

    /// <summary>
    /// True when this face is a displacement and was left for 4e: its samples,
    /// luxels and light come from <c>StaticDispMgr</c>, which this lane does
    /// not own. TODO(4e): <c>BuildDispSamples</c> / <c>BuildDispLuxels</c>.
    /// </summary>
    public bool IsDisplacementDeferred { get; internal set; }

    /// <summary>The light for a slot and normal, or null when the slot is empty.</summary>
    /// <param name="styleIndex">The slot, 0..3.</param>
    /// <param name="normal">The normal, 0..<see cref="NormalCount"/>-1.</param>
    /// <returns>The per-sample values.</returns>
    public LightingValue[]? LightFor(int styleIndex, int normal) =>
        Light[(styleIndex * BumpBasis.LightmapCount) + normal];

    /// <summary>
    /// <c>AllocateLightstyleSamples</c>: zeroed
    /// storage for every normal of one slot.
    /// </summary>
    /// <param name="styleIndex">The slot.</param>
    public void AllocateStyle(int styleIndex)
    {
        for (int n = 0; n < NormalCount; n++)
        {
            Light[(styleIndex * BumpBasis.LightmapCount) + n] = new LightingValue[Samples.Length];
        }
    }

    /// <summary>
    /// <c>FindOrAllocateLightstyleSamples</c>.
    /// </summary>
    /// <param name="lightStyle">The style number.</param>
    /// <returns>The slot, or -1 when all four are taken by other styles.</returns>
    /// <remarks>
    /// A linear scan that stops at the first match OR the first empty slot, so
    /// styles fill slots in the order lights first touch the face. The light
    /// list is walked in a fixed order, which is what makes the slot order a
    /// function of the map.
    /// </remarks>
    public int FindOrAllocateStyle(int lightStyle)
    {
        int k;
        for (k = 0; k < LightConstants.MaxLightmaps; k++)
        {
            if (Styles[k] == lightStyle)
            {
                break;
            }

            if (Styles[k] == 255)
            {
                AllocateStyle(k);
                Styles[k] = (byte)lightStyle;
                break;
            }
        }

        return k >= LightConstants.MaxLightmaps ? -1 : k;
    }
}
