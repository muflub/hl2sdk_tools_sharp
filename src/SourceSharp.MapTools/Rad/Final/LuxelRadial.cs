using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>
/// <c>radial_t</c>: irregular light samples accumulated
/// onto one face's regular luxel grid, with a weight per luxel.
/// </summary>
/// <remarks>
/// <para>
/// Stock's name is historical (its own comment calls it a "luxel accumulation
/// bucket"). Samples -- the face's own, its neighbours', or bounced patches --
/// are splatted with <see cref="AddDirect"/> or <see cref="AddBounced"/>, and
/// every luxel is then read back normalised by <see cref="Sample"/>.
/// </para>
/// <para>
/// Stock callocs a whole <c>radial_t</c> -- <c>SINGLEMAP</c> luxels of four
/// light arrays each -- per face per style. This is a reusable buffer instead:
/// a worker keeps one and <see cref="Reset"/>s it per face, so the only memory
/// is <c>w*h</c> of the largest face that worker saw. The <c>SINGLEMAP</c>
/// padding still matters in one place, the edge test in <see cref="Sample"/>,
/// and is modelled there without being allocated.
/// </para>
/// <para>
/// Every float operation is in stock's order and precision; the double
/// promotions (<c>EQUAL_EPSILON</c>, <c>0.1</c>, <c>RADIALDIST</c>,
/// <c>1.0 / weight</c>) are the C++ literal types and are kept.
/// </para>
/// </remarks>
public sealed class LuxelRadial
{
    /// <summary><c>RADIALDIST2</c>, an int.</summary>
    public const int RadialDist2 = 2;

    /// <summary><c>RADIALDIST</c>, a double.</summary>
    public const double RadialDist = 1.42;

    /// <summary><c>WEIGHT_EPS</c>, a float.</summary>
    public const float WeightEpsilon = 0.00001f;

    /// <summary><c>OO_SQRT_3</c>.</summary>
    public const float OneOverSqrt3 = 0.57735025882720947f;

    /// <summary>The value stock writes into a luxel it has no answer for: 2550 red.</summary>
    public static readonly Vec3 ErrorRed = new(2550f, 0f, 0f);

    private float[] _weight = [];
    private LightingValue[][] _light = [[], [], [], []];

    /// <summary>The face whose luxel space this is.</summary>
    public FaceLightInfo Info { get; private set; } = null!;

    /// <summary><c>w</c>: luxel columns.</summary>
    public int Width { get; private set; }

    /// <summary><c>h</c>: luxel rows.</summary>
    public int Height { get; private set; }

    /// <summary>How many luxels: <c>w * h</c>.</summary>
    public int Count => Width * Height;

    /// <summary>
    /// <c>AllocateRadial</c>: an empty grid over one
    /// face, reusing this buffer.
    /// </summary>
    /// <param name="info">The face's frame (<c>InitLightinfo</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
    public void Reset(FaceLightInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        Info = info;
        Width = info.Width;
        Height = info.Height;

        int n = Width * Height;
        if (_weight.Length < n)
        {
            _weight = new float[n];
            for (int b = 0; b < _light.Length; b++)
            {
                _light[b] = new LightingValue[n];
            }
        }
        else
        {
            Array.Clear(_weight, 0, n);
            for (int b = 0; b < _light.Length; b++)
            {
                Array.Clear(_light[b], 0, n);
            }
        }
    }

    /// <summary>The accumulated weight of one luxel.</summary>
    /// <param name="index">The luxel, <c>s + t * w</c>.</param>
    /// <returns>The weight.</returns>
    public float Weight(int index) => _weight[index];

    /// <summary>The accumulated light of one luxel for one bump direction.</summary>
    /// <param name="bump">0 for flat, 1..3 for the bump directions.</param>
    /// <param name="index">The luxel.</param>
    /// <returns>The un-normalised light.</returns>
    public LightingValue Light(int bump, int index) => _light[bump][index];

    /// <summary>
    /// <c>AddDirectToRadial</c>: splats one light sample
    /// over the luxels its bounds overlap.
    /// </summary>
    /// <param name="point">The sample's world position.</param>
    /// <param name="minS">Its bounds in THIS grid's luxel space: min s.</param>
    /// <param name="minT">Min t.</param>
    /// <param name="maxS">Max s.</param>
    /// <param name="maxT">Max t.</param>
    /// <param name="light">One value per bump direction, 1 or 4 of them.</param>
    /// <param name="hasBumpmap">Whether this grid's face is bumped.</param>
    /// <param name="neighbourHasBumpmap">Whether the sample's face is.</param>
    /// <remarks>
    /// <para>
    /// The weight is area over distance: the overlap of the sample's box with
    /// a two-luxel window round each luxel centre, divided by the Chebyshev
    /// distance from the sample to that centre, floored at 0.1. The overlap
    /// test is against the DOUBLE <c>EQUAL_EPSILON</c>, and <c>area / 0.1</c>
    /// is a double division narrowed back -- both as the C++ types make them.
    /// </para>
    /// <para>
    /// A flat sample feeding a bumped face lands in all four maps: whole in the
    /// flat one, times <c>1/sqrt(3)</c> in each bump direction.
    /// </para>
    /// </remarks>
    public void AddDirect(
        Vec3 point,
        float minS,
        float minT,
        float maxS,
        float maxT,
        ReadOnlySpan<LightingValue> light,
        bool hasBumpmap,
        bool neighbourHasBumpmap)
    {
        (float coordS, float coordT) = Info.WorldToLuxel(point);

        // 87-95. (int) truncates toward zero; the +0.9999f then +1 is stock's
        // own "????".
        int sMin = (int)minS;
        int tMin = (int)minT;
        int sMax = (int)(maxS + 0.9999f) + 1;
        int tMax = (int)(maxT + 0.9999f) + 1;

        sMin = Math.Max(sMin, 0);
        tMin = Math.Max(tMin, 0);
        sMax = Math.Min(sMax, Width);
        tMax = Math.Min(tMax, Height);

        for (int s = sMin; s < sMax; s++)
        {
            for (int t = tMin; t < tMax; t++)
            {
                // 101-104. max/min against DOUBLE -1.0 and 1.0: the float
                // difference is widened, compared, and narrowed back.
                float s0 = (float)StockMax(minS - s, -1.0);
                float t0 = (float)StockMax(minT - t, -1.0);
                float s1 = (float)StockMin(maxS - s, 1.0);
                float t1 = (float)StockMin(maxT - t, 1.0);

                float area = (s1 - s0) * (t1 - t0);

                if (area > LightConstants.EqualEpsilonDouble)
                {
                    float ds = MathF.Abs(coordS - s);
                    float dt = MathF.Abs(coordT - t);

                    float r = StockMax(ds, dt);

                    r = r < 0.1 ? (float)(area / 0.1) : area / r;

                    int i = s + (t * Width);

                    if (hasBumpmap)
                    {
                        if (neighbourHasBumpmap)
                        {
                            for (int b = 0; b < BumpBasis.LightmapCount; b++)
                            {
                                _light[b][i].AddWeighted(light[b], r);
                            }
                        }
                        else
                        {
                            _light[0][i].AddWeighted(light[0], r);
                            float bumpWeight = r * OneOverSqrt3;
                            for (int b = 1; b < BumpBasis.LightmapCount; b++)
                            {
                                _light[b][i].AddWeighted(light[0], bumpWeight);
                            }
                        }
                    }
                    else
                    {
                        _light[0][i].AddWeighted(light[0], r);
                    }

                    _weight[i] += r;
                }
            }
        }
    }

    /// <summary>
    /// <c>AddBouncedToRadial</c>: splats one patch's
    /// bounced light with a radial falloff sized to the patch.
    /// </summary>
    /// <param name="point">The patch's origin.</param>
    /// <param name="minS">The patch's bounds in this grid's luxel space: min s.</param>
    /// <param name="minT">Min t.</param>
    /// <param name="maxS">Max s.</param>
    /// <param name="maxT">Max t.</param>
    /// <param name="light">The patch's total light, one per bump direction.</param>
    /// <param name="hasBumpmap">Whether this grid's face is bumped.</param>
    /// <param name="neighbourHasBumpmap">Whether the patch's face is.</param>
    /// <remarks>
    /// The patch extent is clamped to at least one luxel (the comment at
 ///), the window is <c>RADIALDIST</c> patch-extents either side
    /// -- computed in double because <c>RADIALDIST</c> is a double literal --
    /// and the weight is <c>2 - (ds² + dt²)</c> in patch units, kept where
    /// positive. Only the colour accumulates: the sun amount is untouched,
    /// because the overload stock calls takes a bare <c>Vector</c>.
    /// </remarks>
    public void AddBounced(
        Vec3 point,
        float minS,
        float minT,
        float maxS,
        float maxT,
        ReadOnlySpan<Vec3> light,
        bool hasBumpmap,
        bool neighbourHasBumpmap)
    {
        (float coordS, float coordT) = Info.WorldToLuxel(point);

        float distS = maxS - minS;
        float distT = maxT - minT;

        distS = (float)StockMax(1.0, distS);
        distT = (float)StockMax(1.0, distT);

        int sMin = (int)(coordS - (distS * RadialDist));
        int tMin = (int)(coordT - (distT * RadialDist));
        int sMax = (int)(coordS + (distS * RadialDist) + 1.0f);
        int tMax = (int)(coordT + (distT * RadialDist) + 1.0f);

        sMin = Math.Max(sMin, 0);
        tMin = Math.Max(tMin, 0);
        sMax = Math.Min(sMax, Width);
        tMax = Math.Min(tMax, Height);

        for (int s = sMin; s < sMax; s++)
        {
            for (int t = tMin; t < tMax; t++)
            {
                float ds = (coordS - s) / distS;
                float dt = (coordT - t) / distT;

                float r = RadialDist2 - ((ds * ds) + (dt * dt));

                int i = s + (t * Width);

                if (r > 0)
                {
                    if (hasBumpmap)
                    {
                        if (neighbourHasBumpmap)
                        {
                            for (int b = 0; b < BumpBasis.LightmapCount; b++)
                            {
                                _light[b][i].Lighting += r * light[b];
                            }
                        }
                        else
                        {
                            _light[0][i].Lighting += r * light[0];
                            float bumpWeight = r * OneOverSqrt3;
                            for (int b = 1; b < BumpBasis.LightmapCount; b++)
                            {
                                _light[b][i].Lighting += bumpWeight * light[0];
                            }
                        }
                    }
                    else
                    {
                        _light[0][i].Lighting += r * light[0];
                    }

                    _weight[i] += r;
                }
            }
        }
    }

    /// <summary>
    /// <c>SampleRadial</c>: the normalised light at the
    /// luxel nearest a world point.
    /// </summary>
    /// <param name="point">The point, normally a luxel's own world position.</param>
    /// <param name="light">Receives one value per bump direction.</param>
    /// <param name="redErrors">
    /// <c>!bRed2Black</c> (<c>-rederrors</c>): a luxel nothing reached is red
    /// rather than black.
    /// </param>
    /// <param name="compliance">Selects <see cref="StockQuirk.SampleRadialEdgeOffByOne"/>.</param>
    /// <returns>
    /// False when the point fell off the grid or the flat luxel had no weight
    /// -- stock's <c>baseSampleOk</c>, which keeps the luxel out of the
    /// face's average colour.
    /// </returns>
    /// <remarks>
    /// The point is rounded to the nearest luxel (<c>+0.5f</c>, truncate).
    /// Off the grid the answer is 2550 red whatever <paramref name="redErrors"/>
    /// says; stock also prints "SampleRadial: Punting" once per process, which
    /// is <see cref="OffGrid"/> here.
    /// </remarks>
    public bool Sample(Vec3 point, Span<LightingValue> light, bool redErrors, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        (float coordS, float coordT) = Info.WorldToLuxel(point);
        int u = (int)(coordS + 0.5f);
        int v = (int)(coordT + 0.5f);
        int i = u + (v * Width);

        if (IsOffGrid(u, v, compliance))
        {
            OffGrid++;
            for (int b = 0; b < light.Length; b++)
            {
                light[b] = default;
                light[b].Lighting = ErrorRed;
            }

            return false;
        }

        bool baseSampleOk = true;
        for (int b = 0; b < light.Length; b++)
        {
            light[b] = default;

            // The stock edge test lets u == w and v == h through, and indexes
            // SINGLEMAP-sized arrays whose tail nothing ever wrote: a weight of
            // zero. Reading past w*h here is that zero.
            float weight = i < Count ? _weight[i] : 0f;
            if (weight > WeightEpsilon)
            {
                light[b] = _light[b][i];
                light[b].Scale((float)(1.0 / weight));
            }
            else
            {
                light[b].Lighting = redErrors ? ErrorRed : Vec3.Zero;

                if (b == 0)
                {
                    baseSampleOk = false;
                }
            }
        }

        return baseSampleOk;
    }

    /// <summary>
    /// How many <see cref="Sample"/> calls fell off the grid since the last
    /// <see cref="Reset"/>-independent read; stock's once-only "Punting"
    /// warning, counted.
    /// </summary>
    public int OffGrid { get; set; }

    /// <summary>
    /// The edge test of <c>SampleRadial</c>.
    /// </summary>
    /// <param name="u">The luxel column.</param>
    /// <param name="v">The luxel row.</param>
    /// <param name="compliance">The policy.</param>
    /// <returns>True when the point is off the grid.</returns>
    /// <remarks>
    /// Stock writes <c>u &gt; rad-&gt;w</c> and <c>v &gt; rad-&gt;h</c>, so a
    /// point exactly one past the last column or row passes and reads the next
    /// row's first luxel, or the unwritten tail of the array.
    /// <see cref="StockQuirk.SampleRadialEdgeOffByOne"/> keeps that; correct
    /// treats it as off the grid.
    /// </remarks>
    public bool IsOffGrid(int u, int v, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        return compliance.Emulates(StockQuirk.SampleRadialEdgeOffByOne)
            ? u < 0 || u > Width || v < 0 || v > Height
            : u < 0 || u >= Width || v < 0 || v >= Height;
    }

    /// <summary><c>max(a, b)</c> as the macro: <c>a &gt; b ? a : b</c>, in double.</summary>
    private static double StockMax(double a, double b) => a > b ? a : b;

    /// <summary><c>min(a, b)</c> as the macro: <c>a &lt; b ? a : b</c>, in double.</summary>
    private static double StockMin(double a, double b) => a < b ? a : b;

    /// <summary><c>max(a, b)</c> on two floats.</summary>
    private static float StockMax(float a, float b) => a > b ? a : b;
}
