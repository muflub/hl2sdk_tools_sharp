namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>vstdlib</c>'s <c>CUniformRandomStream</c>: Numerical Recipes' <c>ran1</c>,
/// bit for bit.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>CLeafSampler</c> draws every ambient sample position
/// from one of these and the positions decide every byte of the
/// <c>LUMP_LEAF_AMBIENT_LIGHTING</c> lump. An approximation here is not a
/// slightly-different map; it is a completely different one.
/// </para>
/// <para>
/// A MUTABLE STRUCT, deliberately, and that is not a style slip. Stock's
/// sampler holds its stream BY VALUE and constructs a fresh one per leaf, so the
/// port's lifetime has to be the same: a class would tempt a caller to share one
/// across leaves and silently change every sample in the map. Callers hold it in
/// a local or a field and pass it by <c>ref</c>.
/// </para>
/// <para>
/// NO LOCK. Stock's class carries a <c>CThreadFastMutex</c>, which is dead weight
/// in the only use this port has: one stream per leaf, on one thread, never
/// shared. Contending for it would be the bug, not the protection.
/// </para>
/// <para>
/// <b>vstdlib's source is not in the SDK</b> -- only
/// <c>src/lib/public/linux64/libvstdlib.so</c> is -- so this is a port of the
/// published algorithm rather than of a file on disk, and it is checked against
/// that binary rather than assumed: see
/// <c>StockRandomStreamNativeParityTests</c>, which P/Invokes the shipped
/// library's <c>RandomSeed</c>/<c>RandomFloat</c> (they drive a global
/// <c>CUniformRandomStream</c>) and compares bit patterns.
/// </para>
/// </remarks>
public struct StockRandomStream
{
    /// <summary>How many shuffle slots <c>ran1</c> keeps (<c>NTAB</c>).</summary>
    private const int Ntab = 32;

    /// <summary>The Lehmer multiplier (<c>IA</c>).</summary>
    private const int Ia = 16807;

    /// <summary>The modulus, 2^31 - 1 (<c>IM</c>).</summary>
    private const int Im = 2147483647;

    /// <summary><c>IM / IA</c>, for Schrage's trick (<c>IQ</c>).</summary>
    private const int Iq = 127773;

    /// <summary><c>IM % IA</c>, for Schrage's trick (<c>IR</c>).</summary>
    private const int Ir = 2836;

    /// <summary>How many draws one shuffle slot spans (<c>NDIV</c>).</summary>
    private const int Ndiv = 1 + ((Im - 1) / Ntab);

    /// <summary><c>1 / IM</c>, as a double (<c>AM</c>).</summary>
    private const double Am = 1.0 / Im;

    /// <summary>The largest value a draw may take (<c>RNMX</c>).</summary>
    /// <remarks>
    /// <c>1.0 - 1.2e-7</c>, computed in double exactly as stock's
    /// <c>#define RNMX (1.0-EPS)</c> is, then compared against a float. It keeps
    /// <c>RandomFloat(0, x)</c> strictly below <c>x</c>.
    /// </remarks>
    private const double Rnmx = 1.0 - 1.2e-7;

    /// <summary>The Lehmer state.</summary>
    private int _idum;

    /// <summary>The last value drawn out of the shuffle table.</summary>
    private int _iy;

    /// <summary>The shuffle table.</summary>
    private ShuffleTable _iv;

    /// <summary>
    /// A stream seeded the way a default-constructed
    /// <c>CUniformRandomStream</c> is.
    /// </summary>
    /// <remarks>
    /// <c>CUniformRandomStream::CUniformRandomStream()</c> calls
    /// <c>SetSeed(0)</c>, and <c>SetSeed</c> leaves <c>m_iy</c> at 0, which is
    /// what makes <see cref="GenerateRandomNumber"/> run its warm-up on the
    /// first draw. So <c>default(StockRandomStream)</c> is ALREADY the right
    /// state and this constructor is only here to say so out loud.
    /// </remarks>
    public StockRandomStream() => SetSeed(0);

    /// <summary>A stream seeded with a given value.</summary>
    /// <param name="seed">The seed.</param>
    public StockRandomStream(int seed) => SetSeed(seed);

    /// <summary>Reseeds the stream (<c>CUniformRandomStream::SetSeed</c>).</summary>
    /// <param name="seed">The seed.</param>
    /// <remarks>
    /// Note the sign fold: a positive seed is negated, a negative one is kept.
    /// So seeds <c>n</c> and <c>-n</c> produce the SAME stream, which is stock's
    /// behaviour and not a transcription error.
    /// </remarks>
    public void SetSeed(int seed)
    {
        _idum = seed < 0 ? seed : -seed;
        _iy = 0;
    }

    /// <summary>
    /// One raw draw on <c>[1, IM-1]</c>
    /// (<c>CUniformRandomStream::GenerateRandomNumber</c>).
    /// </summary>
    /// <returns>The value.</returns>
    /// <remarks>
    /// The warm-up loop runs when <c>m_idum &lt;= 0</c> OR <c>m_iy</c> is zero,
    /// so a stream seeded 0 warms up on its first draw and then never again.
    /// It discards the first 8 iterations (<c>j</c> from <c>NTAB+7</c> down to
    /// <c>NTAB</c>) before it starts filling the table.
    /// </remarks>
    public int GenerateRandomNumber()
    {
        int k;

        if (_idum <= 0 || _iy == 0)
        {
            if (-_idum < 1)
            {
                _idum = 1;
            }
            else
            {
                _idum = -_idum;
            }

            for (int j = Ntab + 7; j >= 0; j--)
            {
                k = _idum / Iq;
                _idum = (Ia * (_idum - (k * Iq))) - (Ir * k);
                if (_idum < 0)
                {
                    _idum += Im;
                }

                if (j < Ntab)
                {
                    _iv[j] = _idum;
                }
            }

            _iy = _iv[0];
        }

        k = _idum / Iq;
        _idum = (Ia * (_idum - (k * Iq))) - (Ir * k);
        if (_idum < 0)
        {
            _idum += Im;
        }

        int slot = _iy / Ndiv;
        _iy = _iv[slot];
        _iv[slot] = _idum;
        return _iy;
    }

    /// <summary>
    /// A float uniform on <c>[low, high)</c>
    /// (<c>CUniformRandomStream::RandomFloat</c>).
    /// </summary>
    /// <param name="low">The lower bound, inclusive.</param>
    /// <param name="high">The upper bound, exclusive.</param>
    /// <returns>The draw.</returns>
    /// <remarks>
    /// <c>AM * GenerateRandomNumber()</c> is a DOUBLE multiply narrowed to
    /// float, then clamped at <see cref="Rnmx"/>, then scaled. Doing the
    /// multiply in float instead moves the low bits of roughly one draw in a
    /// thousand, which is enough to relocate an ambient sample.
    /// </remarks>
    public float RandomFloat(float low = 0.0f, float high = 1.0f)
    {
        float fl = (float)(Am * GenerateRandomNumber());
        if (fl > Rnmx)
        {
            fl = (float)Rnmx;
        }

        return (fl * (high - low)) + low;
    }

    /// <summary>An int uniform on <c>[low, high]</c>, both inclusive.</summary>
    /// <param name="low">The lower bound.</param>
    /// <param name="high">The upper bound.</param>
    /// <returns>The draw, or <paramref name="low"/> when the range is empty.</returns>
    /// <remarks>
    /// <c>CUniformRandomStream::RandomInt</c>. The span is computed in
    /// <see cref="uint"/> so that a range wider than <see cref="int.MaxValue"/>
    /// does not overflow, exactly as stock's <c>unsigned int x</c> does.
    /// </remarks>
    public int RandomInt(int low, int high)
    {
        if (high < low)
        {
            return low;
        }

        uint x = (uint)(high - low) + 1;
        if (x <= 1 || Im / x == 0)
        {
            return low;
        }

        uint maxAcceptable = (uint)(Im - ((uint)Im + 1) % x);
        uint n;
        do
        {
            n = (uint)GenerateRandomNumber();
        }
        while (n > maxAcceptable);

        return low + (int)(n % x);
    }

    /// <summary>The 32 shuffle slots, inline so the stream copies by value.</summary>
    [System.Runtime.CompilerServices.InlineArray(Ntab)]
    private struct ShuffleTable
    {
        /// <summary>The first slot; the attribute supplies the rest.</summary>
        private int _element0;
    }
}
