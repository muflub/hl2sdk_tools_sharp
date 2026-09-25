using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>HaltonSequenceGenerator_t</c>: the radical
/// inverse of an integer counter in a prime base.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sequence starts at element 2, not 1.</b> <c>NextValue</c> is
/// <c>return GetElement(seed++);</c> and <c>GetElement</c> ignores its argument
/// and reads the MEMBER <c>seed</c>, which the
/// post-increment has already advanced by the time the body runs. So the
/// first value of the base-2 sequence is 0.25 (element 2), not 0.5 (element 1).
/// </para>
/// <para>
/// A value type with its counter inline, so a sampler is per call exactly as
/// stock's <c>DirectionalSampler_t sampler;</c> locals are -- which is what
/// makes every sample group's sky directions the same sequence.
/// </para>
/// </remarks>
public struct HaltonSequence
{
    private readonly int _base;
    private readonly float _fbase;
    private int _seed;

    /// <summary>Creates a generator.</summary>
    /// <param name="primeBase">The base, a prime of at least 2.</param>
    /// <exception cref="ArgumentOutOfRangeException">The base is below 2.</exception>
    public HaltonSequence(int primeBase)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(primeBase, 2);
        _base = primeBase;
        _fbase = primeBase;
        _seed = 1;
    }

    /// <summary>The next value, in [0, 1).</summary>
    /// <returns>The radical inverse of the counter AFTER it was incremented.</returns>
    public float NextValue()
    {
        _seed++;
        int tmpseed = _seed;
        float ret = 0.0f;

        // `1.0/fbase` is a double divide narrowed to float;
        // the running `base_inv /= fbase` is float.
        float baseInv = (float)(1.0 / _fbase);
        while (tmpseed != 0)
        {
            int dig = tmpseed % _base;
            ret += dig * baseInv;
            baseInv /= _fbase;
            tmpseed /= _base;
        }

        return ret;
    }
}

/// <summary>
/// <c>DirectionalSampler_t</c>: quasi-random unit
/// vectors, the directions of sky ambient rays and of sun jitter.
/// </summary>
/// <remarks>
/// <para>
/// z is uniform in [-1, 1] from the base-2 sequence and the azimuth uniform
/// from the base-3 one, which is an area-uniform sphere sampling. Mixed
/// precision exactly as written: <c>2*z - 1.0</c> and <c>2.0*M_PI*v</c> are
/// double expressions narrowed into float locals, and <c>acos</c>, <c>sin</c>
/// and <c>cos</c> of a float argument resolve to the FLOAT overloads in the reference build.
/// </para>
/// <para>
/// The float transcendental functions are the one place this cannot promise
/// stock's bits: MSVC's <c>acosf</c>/<c>sinf</c>/<c>cosf</c> and .NET's
/// <see cref="MathF"/> are not required to round identically. The directions
/// agree to an ulp or two; they are deterministic on every machine here.
/// </para>
/// </remarks>
public struct DirectionalSampler
{
    private HaltonSequence _zdot;
    private HaltonSequence _vrot;

    /// <summary>Creates a sampler at the start of its sequence.</summary>
    public DirectionalSampler()
    {
        _zdot = new HaltonSequence(2);
        _vrot = new HaltonSequence(3);
    }

    /// <summary>The next direction.</summary>
    /// <returns>A unit vector.</returns>
    public Vec3 NextValue()
    {
        float zvalue = _zdot.NextValue();
        zvalue = (float)((2 * zvalue) - 1.0);
        float phi = MathF.Acos(zvalue);
        float theta = (float)(2.0 * Math.PI * _vrot.NextValue());
        float sinP = MathF.Sin(phi);
        return new Vec3(MathF.Cos(theta) * sinP, MathF.Sin(theta) * sinP, zvalue);
    }
}
