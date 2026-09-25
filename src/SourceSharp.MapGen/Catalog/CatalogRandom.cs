namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The only source of variation a generator is allowed, seeded from the entry's
/// own name.
///
/// <para>
/// TWO RULES, and both are gates. Generation is byte-stable, so nothing may
/// depend on the clock, on a process-wide sequence, or on anything another entry
/// consumed first; and there are no mutable static fields, so the state lives on
/// an instance handed to one generator.
/// </para>
///
/// <para>
/// NOT `System.Random`. Its algorithm is explicitly not part of .NET's contract
/// and has already changed once between framework versions — which would turn an
/// SDK upgrade into a silent change of every generated map, and a byte-stability
/// fact that compares a map against itself would never notice. xorshift32 is
/// four lines, and those four lines are the contract.
/// </para>
/// </summary>
public sealed class CatalogRandom
{
    private uint _state;

    /// <summary>Seeds the sequence from an entry name.</summary>
    /// <param name="seed">Usually the entry's name.</param>
    public CatalogRandom(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _state = Hash(seed);
    }

    /// <summary>
    /// FNV-1a over the name's bytes.
    ///
    /// <para>
    /// Spelled out rather than `string.GetHashCode()`, which is RANDOMISED PER
    /// PROCESS in .NET by design. Seeding from it would make a generator produce
    /// different bytes on every run, which is exactly the failure the
    /// byte-stability fact exists to catch — except that the fact runs in one
    /// process and would pass.
    /// </para>
    /// </summary>
    /// <param name="text">The name to hash.</param>
    public static uint Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        uint hash = 2166136261u;

        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        // xorshift32 is a fixed point at zero, so a name that happened to hash
        // to it would produce an endless run of zeroes.
        return hash == 0 ? 1u : hash;
    }

    /// <summary>The next value in the sequence.</summary>
    public uint Next()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>The next value, in [min, max).</summary>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Exclusive upper bound.</param>
    public int NextInt(int min, int max)
    {
        if (max <= min)
            throw new ArgumentOutOfRangeException(nameof(max), "max must be above min");

        return min + (int)(Next() % (uint)(max - min));
    }

    /// <summary>
    /// The next value snapped to a grid, in [min, max].
    ///
    /// <para>
    /// On the grid because map geometry is: an off-grid brush face produces
    /// coordinates that do not survive `%g`'s six significant digits the same
    /// way on every machine, and the byte-stability fact would then be measuring
    /// float formatting rather than the generator.
    /// </para>
    /// </summary>
    /// <param name="min">Inclusive lower bound, a multiple of <paramref name="step"/>.</param>
    /// <param name="max">Inclusive upper bound.</param>
    /// <param name="step">The grid size.</param>
    public float NextOnGrid(float min, float max, float step)
    {
        int steps = (int)MathF.Floor((max - min) / step);

        return min + (step * NextInt(0, steps + 1));
    }
}
