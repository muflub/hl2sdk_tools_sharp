using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>The six colours of one ambient cube, stored inline.</summary>
[InlineArray(AmbientCube.Sides)]
public struct AmbientCubeColors
{
    /// <summary>The first side; the attribute supplies the rest.</summary>
    private Vec3 _side0;
}

/// <summary>
/// One candidate ambient sample: where it was taken and what it saw
/// (<c>ambientsample_t</c>, <c>leaf_ambient_lighting.cpp:306</c>).
/// </summary>
/// <remarks>
/// A struct with its cube inline: a leaf keeps at most seventeen of these, and
/// the eviction below reads every pair of them, so they are laid out flat
/// rather than as objects with their own arrays.
/// </remarks>
public struct AmbientSample
{
    /// <summary>Where the sample was taken.</summary>
    public Vec3 Position;

    /// <summary>The six cube colours.</summary>
    public AmbientCubeColors Cube;
}

/// <summary>
/// How a leaf's candidate samples are whittled down to the ones worth storing.
/// </summary>
/// <remarks>
/// <para>
/// Two separate reductions, in this order: <see cref="Add"/> caps the list at
/// sixteen, throwing away the sample whose nearest neighbour is nearest -- so
/// the survivors spread out and keep colour variety -- and
/// <see cref="Compress"/> then drops any sample the others can already
/// reconstruct to within three units of gamma space.
/// </para>
/// <para>
/// Both read the cube VALUES, which is why the number of records in the lump is
/// not a function of the geometry alone: any float non-determinism upstream that
/// reaches about 1e-4 moves the count.
/// </para>
/// </remarks>
public static class AmbientSampleList
{
    /// <summary>The cap (<c>MAX_SAMPLES</c>, <c>leaf_ambient_lighting.cpp:316</c>).</summary>
    public const int MaxSamples = 16;

    /// <summary>
    /// Adds a sample, evicting the least valuable one when the list is over
    /// the cap (<c>AddSampleToList</c>, <c>leaf_ambient_lighting.cpp:314</c>).
    /// </summary>
    /// <param name="list">The list, added to in place.</param>
    /// <param name="position">Where the sample was taken.</param>
    /// <param name="cube">The six colours.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The new sample is appended FIRST and the eviction considers the whole
    /// list including it, so the sample just added can be the one thrown away.
    /// The score is "10 % distance, 90 % colour difference": each sample's
    /// nearest neighbour under a distance scaled by <c>0.1 + 0.9 * maxDC</c>;
    /// the sample whose nearest neighbour is nearest is evicted.
    /// </para>
    /// <para>
    /// <c>maxDC</c> is reset per NEIGHBOUR, not per side, so it is a running
    /// maximum over all six sides by the time it is clamped, and
    /// <c>totalDC</c> sums that running maximum once per side. Both are stock's
    /// heuristic and are reproduced.
    /// </para>
    /// <para>
    /// <b>The tie-break is dead code</b> in stock: <c>nearestNeighborTotal</c>
    /// is never assigned (<c>:330</c>), so on an exact tie the EARLIER index
    /// always wins. See <see cref="StockQuirk.AmbientSampleTieBreakNeverFires"/>.
    /// Eviction is <c>FastRemove</c>: the last element moves into the hole, and
    /// the resulting order reaches the lump.
    /// </para>
    /// </remarks>
    public static void Add(
        List<AmbientSample> list,
        Vec3 position,
        ReadOnlySpan<Vec3> cube,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(compliance);
        ArgumentOutOfRangeException.ThrowIfLessThan(cube.Length, AmbientCube.Sides);

        AmbientSample added = default;
        added.Position = position;
        for (int k = 0; k < AmbientCube.Sides; k++)
        {
            added.Cube[k] = cube[k];
        }

        list.Add(added);

        if (list.Count <= MaxSamples)
        {
            return;
        }

        bool deadTieBreak = compliance.Emulates(StockQuirk.AmbientSampleTieBreakNeverFires);
        Span<AmbientSample> samples = CollectionsMarshal.AsSpan(list);

        int nearestNeighborIndex = 0;
        float nearestNeighborDist = float.MaxValue;
        float nearestNeighborTotal = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            ref readonly AmbientSample a = ref samples[i];
            float closestDist = float.MaxValue;
            float totalDc = 0;

            for (int j = 0; j < samples.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                ref readonly AmbientSample b = ref samples[j];
                float dist = (a.Position - b.Position).Length();

                // leaf_ambient_lighting.cpp:341-351.
                float maxDc = 0;
                for (int k = 0; k < AmbientCube.Sides; k++)
                {
                    Vec3 ca = a.Cube[k];
                    Vec3 cb = b.Cube[k];
                    maxDc = MaxOf(maxDc, MathF.Abs(ca.X - cb.X));
                    maxDc = MaxOf(maxDc, MathF.Abs(ca.Y - cb.Y));
                    maxDc = MaxOf(maxDc, MathF.Abs(ca.Z - cb.Z));
                    totalDc += maxDc;
                }

                if (maxDc < 1e-4f)
                {
                    maxDc = 0;
                }
                else if (maxDc > 1.0f)
                {
                    maxDc = 1.0f;
                }

                float distanceFactor = 0.1f + (maxDc * 0.9f);
                dist *= distanceFactor;

                if (dist < closestDist)
                {
                    closestDist = dist;
                }
            }

            bool better = closestDist < nearestNeighborDist
                || (!deadTieBreak
                    && closestDist == nearestNeighborDist
                    && totalDc < nearestNeighborTotal);

            if (better)
            {
                nearestNeighborDist = closestDist;
                nearestNeighborIndex = i;
                if (!deadTieBreak)
                {
                    nearestNeighborTotal = totalDc;
                }
            }
        }

        FastRemove(list, nearestNeighborIndex);
    }

    /// <summary>
    /// Drops every sample the rest can already reconstruct
    /// (<c>CompressAmbientSampleList</c>, <c>leaf_ambient_lighting.cpp:432</c>).
    /// </summary>
    /// <param name="list">The list, shortened in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    /// <remarks>
    /// ONE PASS, forwards, with the index stepped back after a removal -- and
    /// because the removal is a <c>FastRemove</c>, the element that lands in the
    /// hole is tested in the removed sample's place. The comparison is in GAMMA
    /// space ("a perceptual basis"), so the threshold of 3 is three display
    /// units out of 255.
    /// </remarks>
    public static void Compress(List<AmbientSample> list)
    {
        ArgumentNullException.ThrowIfNull(list);

        Span<Vec3> testCube = stackalloc Vec3[AmbientCube.Sides];

        for (int i = 0; i < list.Count; i++)
        {
            if (list.Count > 1)
            {
                ReadOnlySpan<AmbientSample> samples = CollectionsMarshal.AsSpan(list);
                ColorAtPosition(testCube, samples[i].Position, samples, skipIndex: i);
                if (CubeDeltaGammaSpace(testCube, samples[i].Cube) < 3)
                {
                    FastRemove(list, i);
                    i--;
                }
            }
        }
    }

    /// <summary>
    /// Reconstructs the ambient colour at a point from the samples
    /// (<c>Mod_LeafAmbientColorAtPos</c>, <c>leaf_ambient_lighting.cpp:404</c>).
    /// </summary>
    /// <param name="into">Receives the six colours.</param>
    /// <param name="position">Where to reconstruct.</param>
    /// <param name="samples">The samples.</param>
    /// <param name="skipIndex">One sample to leave out, or -1.</param>
    /// <remarks>
    /// An inverse-square-distance weighted mean with the distance offset by one,
    /// so a sample AT the point does not produce an infinite weight. This is
    /// the engine's own reconstruction, copied into vrad so the compressor can
    /// ask what the engine will do; changing one side alone makes the
    /// compression wrong.
    /// </remarks>
    public static void ColorAtPosition(
        Span<Vec3> into, Vec3 position, ReadOnlySpan<AmbientSample> samples, int skipIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, AmbientCube.Sides);

        for (int i = 0; i < AmbientCube.Sides; i++)
        {
            into[i] = Vec3.Zero;
        }

        float totalFactor = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (i == skipIndex)
            {
                continue;
            }

            float dist = (samples[i].Position - position).LengthSquared();
            float factor = 1.0f / (dist + 1.0f);
            totalFactor += factor;

            for (int j = 0; j < AmbientCube.Sides; j++)
            {
                into[j] += samples[i].Cube[j] * factor;
            }
        }

        for (int i = 0; i < AmbientCube.Sides; i++)
        {
            into[i] *= 1.0f / totalFactor;
        }
    }

    /// <summary>
    /// The largest per-channel, per-side difference between two cubes, in
    /// gamma space (<c>CubeDeltaGammaSpace</c>, <c>leaf_ambient_lighting.cpp:385</c>).
    /// </summary>
    /// <param name="a">One cube.</param>
    /// <param name="b">The other.</param>
    /// <returns>The largest difference, 0..255.</returns>
    public static int CubeDeltaGammaSpace(ReadOnlySpan<Vec3> a, ReadOnlySpan<Vec3> b)
    {
        int maxDelta = 0;
        for (int i = 0; i < AmbientCube.Sides; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                int v0 = StockLightColor.LinearToScreenGamma(a[i][j]);
                int v1 = StockLightColor.LinearToScreenGamma(b[i][j]);
                int delta = Math.Abs(v0 - v1);
                if (delta > maxDelta)
                {
                    maxDelta = delta;
                }
            }
        }

        return maxDelta;
    }

    /// <summary>The stock <c>max</c> macro: <c>a &gt; b ? a : b</c>.</summary>
    private static float MaxOf(float a, float b) => a > b ? a : b;

    /// <summary>
    /// <c>CUtlVector::FastRemove</c>: move the last element into the hole.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="index">Which element to drop.</param>
    private static void FastRemove(List<AmbientSample> list, int index)
    {
        int last = list.Count - 1;
        if (index != last)
        {
            list[index] = list[last];
        }

        list.RemoveAt(last);
    }
}
