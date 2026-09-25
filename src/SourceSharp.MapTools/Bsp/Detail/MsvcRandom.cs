using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Bsp.Detail;

/// <summary>
/// The Microsoft C runtime's <c>rand()</c>, which stock x64 vbsp is linked
/// against: <c>state = state * 214013 + 2531011</c>, returning bits 16-30.
/// </summary>
/// <remarks>
/// <para>
/// Not a stock defect: detail-prop placement is DEFINED by this sequence
/// (<c>srand(hammerfaceid)</c> per face), so it
/// is reproduced unconditionally. One instance per face, seeded as stock seeds
/// it — which is what lets faces be placed in parallel and committed in face
/// order.
/// </para>
/// </remarks>
public struct MsvcRandom
{
    /// <summary>The reference generator's maximum: 32767.</summary>
    public const int RandMax = 0x7fff;

    private uint _state;

    /// <summary><c>srand(seed)</c>.</summary>
    /// <param name="seed">The seed.</param>
    public MsvcRandom(int seed) => _state = unchecked((uint)seed);

    /// <summary><c>rand()</c>: 0 to 32767.</summary>
    /// <returns>The next value.</returns>
    public int Next()
    {
        _state = unchecked((_state * 214013u) + 2531011u);
        return (int)((_state >> 16) & RandMax);
    }

    /// <summary>
    /// <c>rand() / (float)RAND_MAX</c> as the reference build computes it: a
    /// multiply by the constant's reciprocal. 0 to 1 inclusive.
    /// </summary>
    /// <returns>The value.</returns>
    /// <remarks>
    /// The source says divide; the shipped x64 vbsp was compiled with
    /// floating-point contraction allowed and turned the divide by a constant
    /// into a multiply by <c>1.0f / 32767</c>. MEASURED: with a true divide, 15
    /// of 976 detail props in <c>l1_detail_props</c> (and 18 of 896 in
    /// <c>l2_props_overlays_detail_cubemap</c>) land one ulp of <c>u</c> away
    /// from stock; with the reciprocal, every origin matches. The divide in
    /// <c>360.0f * rand() / (float)RAND_MAX</c> (an expression whose
    /// left side is not a lone <c>rand()</c>) was NOT rewritten: making it a
    /// reciprocal multiply too moved three more props off stock.
    /// </remarks>
    public float NextUnit() => Next() * (1.0f / RandMax);
}

/// <summary>
/// <c>CGaussianRandomStream</c> over vstdlib's uniform stream
/// The Marsaglia polar method, with the second
/// value of each pair kept for the next call.
/// </summary>
/// <remarks>
/// <para>
/// vbsp calls the global <c>RandomGaussianFloat</c>, whose stream is a static:
/// its cached second value survives <c>RandomSeed</c>, so it carries from one
/// face to the next in face order. This object is that state for one compile.
/// </para>
/// <para>
/// The <c>fac</c> term is evaluated in DOUBLE and rounded once: measured, the
/// float-overload reading (<c>logf</c>, float divide, <c>sqrtf</c>) put 9 of
/// the 976 props of <c>l1_detail_props</c> one ulp off stock's scale, and the
/// double evaluation matches all of them — vstdlib is a separate DLL and its
/// <c>log</c> is the C double function.
/// </para>
/// </remarks>
public sealed class GaussianRandomStream
{
    private bool _haveValue;
    private float _value;

    /// <summary>
    /// <c>CGaussianRandomStream::RandomFloat</c>.
    /// </summary>
    /// <param name="uniform">The uniform stream it draws from.</param>
    /// <param name="mean">The mean.</param>
    /// <param name="stdDev">The standard deviation.</param>
    /// <returns>A normally distributed value.</returns>
    public float RandomFloat(ref StockRandomStream uniform, float mean, float stdDev)
    {
        if (!_haveValue)
        {
            float v1, v2, rsq;
            do
            {
                v1 = (2.0f * uniform.RandomFloat()) - 1.0f;
                v2 = (2.0f * uniform.RandomFloat()) - 1.0f;
                rsq = (v1 * v1) + (v2 * v2);
            }
            while (rsq > 1.0f || rsq == 0.0f);

            float fac = (float)Math.Sqrt(-2.0 * Math.Log(rsq) / rsq);
            _value = v1 * fac;
            _haveValue = true;
            return (stdDev * (v2 * fac)) + mean;
        }

        _haveValue = false;
        return (stdDev * _value) + mean;
    }
}
