namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The bare numbers vrad's lighting path is built out of.
/// </summary>
/// <remarks>
/// Gathered rather than scattered because several of them are spelled in more
/// than one place in the reference build and one of those spellings is always the odd one
/// out. Each carries its reference site; none is a value this port chose.
/// </remarks>
public static class LightConstants
{
    /// <summary>
    /// <c>EQUAL_EPSILON</c>: 0.001.
    /// </summary>
    /// <remarks>
    /// Used against DOT PRODUCTS as often as against distances -- the flatness
    /// test and the ambient-sky validity test at
    /// Both do -- so it is not a length tolerance despite reading
    /// like one.
    /// </remarks>
    public const float EqualEpsilon = 0.001f;

    /// <summary>
    /// <c>EQUAL_EPSILON</c> as the macro really is: the DOUBLE literal 0.001
    /// </summary>
    /// <remarks>
    /// Wherever the macro meets a float in a comparison the float is promoted,
    /// so a test like <c>area &lt; worldAreaPerLuxel - EQUAL_EPSILON</c>
    /// Is a double subtraction and comparison. For a
    /// bare <c>x &lt; EQUAL_EPSILON</c> the float spelling above happens to
    /// decide identically; for anything with arithmetic on the other side it
    /// does not, which is why both exist.
    /// </remarks>
    public const double EqualEpsilonDouble = 0.001;

    /// <summary>
    /// <c>ON_EPSILON</c> (0.1), the winding clipper's plane tolerance.
    /// </summary>
    public const float OnEpsilon = 0.1f;

    /// <summary>
    /// The epsilon <c>BuildFacesamples</c> clips the LIGHTMAP-space winding
    /// With: <c>ON_EPSILON / 16</c>.
    /// </summary>
    /// <remarks>
    /// Stock's own comment says why: "need a separate epsilon for lightmap
    /// space since ON_EPSILON is for texture space". A luxel is one unit wide
    /// in lightmap space and can be sixteen or more world units wide, so
    /// clipping at 0.1 lightmap units would swallow a tenth of a luxel.
    /// </remarks>
    public const float LightmapOnEpsilon = OnEpsilon / 16.0f;

    /// <summary>
    /// <c>smoothing_threshold</c>'s default:
    /// <c>cos(45 degrees)</c>, spelled to seven digits.
    /// </summary>
    /// <remarks>
    /// Written as the literal stock writes, not as <c>MathF.Cos</c> of 45
    /// degrees: the two differ in the last bits, and this value is compared
    /// against dot products that decide whether two faces smooth.
    /// </remarks>
    public const float DefaultSmoothingThreshold = 0.7071067f;

    /// <summary>
    /// <c>DIRECT_SCALE</c>: <c>100.0 * 100.0</c>.
    /// </summary>
    /// <remarks>
    /// Applied to a surface light's intensity so that the falloff denominator,
    /// which is a squared distance in world units, lands in a range that
    /// produces visible light. Arbitrary but consistent, so reproduced
    /// unconditionally.
    /// </remarks>
    public const float DirectScale = 100.0f * 100.0f;

    /// <summary>
    /// <c>NORMALFORMFACTOR</c>: 40.156979, the
    /// accumulated dot products over a hemisphere.
    /// </summary>
    public const float NormalFormFactor = 40.156979f;

    /// <summary>
    /// <c>CONSTANT_DOT</c>: <c>0.7 / 2</c>, the dot
    /// product substituted when normals are ignored.
    /// </summary>
    public const float ConstantDot = 0.7f / 2f;

    /// <summary>
    /// <c>NSAMPLES_SUN_AREA_LIGHT</c>: 30 jittered
    /// rays for a sun with angular extent.
    /// </summary>
    public const int SunAreaLightSamples = 30;

    /// <summary>
    /// <c>NUMVERTEXNORMALS</c>: 162, the count of
    /// Quake's icosphere directions, reused as the ambient sky sample count.
    /// </summary>
    public const int VertexNormalCount = 162;

    /// <summary>
    /// <c>MAX_TRACE_LENGTH</c>: the length of a ray cast
    /// "to infinity".
    /// </summary>
    /// <remarks>
    /// <c>1.732050807569 * COORD_EXTENT</c> -- the
    /// diagonal of the world box, with sqrt(3) spelled as a literal. The macro
    /// is a DOUBLE expression and is narrowed once at the use site, so the
    /// product is formed in double here too: doing it in float shifts the last
    /// bit of a number that multiplies a unit vector to produce a ray end.
    /// </remarks>
    public const float MaxTraceLength = (float)(1.732050807569 * (2 * 16384));

    /// <summary>
    /// <c>DIST_EPSILON</c>: 0.03125, how far a surface
    /// light's ray start is pushed off its own face.
    /// </summary>
    public const float DistEpsilon = 0.03125f;

    /// <summary>
    /// <c>MAXLIGHTMAPS</c>: four lightstyles per face.
    /// </summary>
    public const int MaxLightmaps = 4;

    /// <summary>
    /// The gradient above which <c>BuildSupersampleFaceLights</c> supersamples
    /// A luxel: 0.0625.
    /// </summary>
    public const float SupersampleGradient = 0.0625f;

    /// <summary>
    /// <c>SINGLE_BRUSH_MAP</c>:
    /// <c>MAX_BRUSH_LIGHTMAP_DIM_INCLUDING_BORDER</c> squared, so 35 * 35.
    /// </summary>
    /// <remarks>
    /// Stock allocates <c>SINGLE_BRUSH_MAP * 2</c> = 2450 samples and then
    /// writes as many as the clip produces, with NO bound check
    ///Vbsp caps a brush lightmap at 32
    /// luxels without its border, and the sample grid is
    /// one wider in each axis, so at most 33 * 33 = 1089 samples can be
    /// produced. The missing check therefore never fires, which is why the
    /// budget is reproduced as a capacity rather than as a quirk.
    /// </remarks>
    public const int SingleBrushMap = 35 * 35;
}
