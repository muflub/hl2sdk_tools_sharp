//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// A recorded set of rays and the answers STOCK gave for them.
/// </summary>
/// <param name="Rays">The rays, as origin and end point.</param>
/// <param name="StockSurface">Stock's hit face per ray, or -1 for a miss.</param>
/// <param name="StockFraction">Stock's <c>m_HitFrac</c> per ray.</param>
/// <param name="StockHasLuxel">Stock's <c>m_bHasLuxel</c> per ray.</param>
/// <remarks>
/// <para>
/// These files are not a golden output of THIS port. They were produced by
/// stock's own <c>CLightSurface</c> and <c>EnumerateNodesAlongRay_R</c>, cut
/// out of the reference implementation and by line range and
/// compiled -- so the comparison below is against the reference implementation's arithmetic rather
/// than against a previous run of ours. <c>Fixtures/README-bsp-rays.md</c>
/// carries the exact commands and the extraction hashes.
/// </para>
/// <para>
/// THE DRIFT THIS DOES NOT CATCH, said plainly: if stock's source moves and
/// these files are not regenerated, they become a record of what stock USED to
/// say. The README carries the commit they were made at, and the extraction
/// script fails loudly rather than silently cutting different lines.
/// </para>
/// </remarks>
internal sealed record StockRaySet(
    Ray[] Rays,
    int[] StockSurface,
    float[] StockFraction,
    bool[] StockHasLuxel)
{
    private const string RaysName = "rays-lockdown-leafambient.bin";
    private const string AnswersName = "stock-lockdown-leafambient.bin";

    /// <summary>How many rays the set holds.</summary>
    public int Count => Rays.Length;

    /// <summary>
    /// Loads the committed set from THIS worktree.
    /// </summary>
    /// <returns>The rays and stock's answers.</returns>
    /// <exception cref="InvalidOperationException">
    /// The checkout root or either file could not be found. Thrown, not
    /// skipped: both are committed, so absence means the layout moved, and a
    /// parity fact that skips itself is how a real divergence stays hidden.
    /// </exception>
    public static StockRaySet Load() =>
        Load(Path.Combine(FixtureDirectory(), RaysName),
             Path.Combine(FixtureDirectory(), AnswersName));

    /// <summary>
    /// Loads a set from an explicit pair of files.
    /// </summary>
    /// <param name="rayPath">The ray file.</param>
    /// <param name="answerPath">The answers the oracle gave for it.</param>
    /// <returns>The rays and stock's answers.</returns>
    /// <exception cref="InvalidOperationException">
    /// The two files do not describe the same run.
    /// </exception>
    /// <remarks>
    /// The throughput measurement uses this to run a much larger set than is
    /// worth committing: 324,000 rays against the fixture's 8,100. Committing
    /// the big one would put 7 MB in the repository to make one number less
    /// noisy, and the correctness gate does not need it.
    /// </remarks>
    public static StockRaySet Load(string rayPath, string answerPath)
    {
        byte[] rayBytes = File.ReadAllBytes(rayPath);
        byte[] answerBytes = File.ReadAllBytes(answerPath);

        int n = BitConverter.ToInt32(rayBytes, 0);
        if (rayBytes.Length != 4 + (n * 24))
        {
            throw new InvalidOperationException(
                $"{RaysName} says {n} rays, which needs {4 + (n * 24)} bytes, and the file is "
                + $"{rayBytes.Length}");
        }

        int answerCount = BitConverter.ToInt32(answerBytes, 0);
        if (answerCount != n || answerBytes.Length != 4 + (n * 9))
        {
            throw new InvalidOperationException(
                $"{AnswersName} holds {answerCount} answers in {answerBytes.Length} bytes, and "
                + $"{RaysName} holds {n} rays: the pair was not made in one run");
        }

        Ray[] rays = new Ray[n];
        for (int i = 0; i < n; i++)
        {
            int o = 4 + (i * 24);
            float sx = BitConverter.ToSingle(rayBytes, o);
            float sy = BitConverter.ToSingle(rayBytes, o + 4);
            float sz = BitConverter.ToSingle(rayBytes, o + 8);
            float ex = BitConverter.ToSingle(rayBytes, o + 12);
            float ey = BitConverter.ToSingle(rayBytes, o + 16);
            float ez = BitConverter.ToSingle(rayBytes, o + 20);

            // Stock's Ray_t is start plus a delta walked over the fraction
            // 0..1. Ray carries a direction and a MaxDistance in lengths of
            // it, so a unit MaxDistance over the whole delta is the same span
            // -- and it is the only spelling that leaves the arithmetic
            // untouched, since any other would scale the direction and change
            // the last bits of every dot product.
            rays[i] = new Ray(sx, sy, sz, ex - sx, ey - sy, ez - sz, 1.0f);
        }

        int[] surface = new int[n];
        float[] fraction = new float[n];
        bool[] hasLuxel = new bool[n];
        for (int i = 0; i < n; i++)
        {
            surface[i] = BitConverter.ToInt32(answerBytes, 4 + (i * 4));
            fraction[i] = BitConverter.ToSingle(answerBytes, 4 + (n * 4) + (i * 4));
            hasLuxel[i] = answerBytes[4 + (n * 8) + i] != 0;
        }

        return new StockRaySet(rays, surface, fraction, hasLuxel);
    }

    /// <summary>The directory the committed fixtures live in.</summary>
    /// <returns>An absolute path inside THIS worktree.</returns>
    /// <exception cref="InvalidOperationException">It could not be found.</exception>
    public static string FixtureDirectory()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The recorded ray "
                + "set is located relative to the tree this binary was built from, so that a "
                + "worktree compares against ITS OWN fixtures and not an enclosing checkout's.");
        }

        string dir = Path.Combine(
            root, "src", "SourceSharp.Tests",
            "MapTools", "Tracing", "Fixtures");

        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"{dir} is missing. These are COMMITTED files, not build output.");
        }

        return dir;
    }
}
