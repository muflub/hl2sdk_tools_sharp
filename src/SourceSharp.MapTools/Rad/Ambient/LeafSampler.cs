using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>CLeafSampler</c>: rejection
/// sampling for a point that is really inside a leaf.
/// </summary>
/// <remarks>
/// <para>
/// A leaf's bounding box is a loose fit -- it is the box of a convex cell
/// carved out by arbitrary planes -- and the cell may also be partly filled by
/// <c>func_detail</c> brushes and displacement surfaces that the tree does not
/// split on. So a position is drawn uniformly in the box and then tested three
/// ways: against the leaf's own boundary planes, against the leaf's brushes
/// along all six axes, and (in stock, not yet here) against the leaf's
/// displacements.
/// </para>
/// <para>
/// <b>THE STREAM IS PER LEAF, SEEDED ZERO, AND THAT IS NOT A BUG TO FIX HERE.</b>
/// <c>ComputeAmbientForLeaf</c> declares <c>CLeafSampler sampler( iThread );</c>
/// as a LOCAL, and <c>CLeafSampler</c> holds its
/// <c>CUniformRandomStream</c> by value, so the stream is default-constructed --
/// <c>SetSeed(0)</c> -- once per leaf and never crosses a thread. Every leaf in
/// every map therefore draws the SAME sequence of numbers, and the sample
/// positions are already a pure function of the leaf's geometry.
/// </para>
/// <para>
/// This was worth establishing because a plan section was written on the
/// opposite premise -- that the stream was per thread, and that leaf ambient was
/// therefore non-deterministic at line 221. It is not: <c>iThread</c> is carried
/// into this class for exactly one purpose, indexing the global
/// <c>s_DispTested[]</c> scratch inside <c>CastRayInLeaf</c>, and that is the
/// ONLY per-thread state in the sampler. Re-seeding per leaf index would be a
/// defensible improvement and it would also move 94 % of the map's ambient
/// cubes, so it is a deliberate output change and not a tidy-up.
/// </para>
/// </remarks>
public sealed class LeafSampler
{
    /// <summary>How many draws before giving up on a leaf.</summary>
    /// <remarks>
    /// A leaf whose interior is a vanishing fraction of its bounding box
    /// -- a long thin diagonal sliver -- can exhaust this, and then the sample
    /// is taken at the box CENTRE, which may not be inside the leaf at all. That
    /// is stock's fallback and it is reproduced; the x-leafambient prototype
    /// counted 201 of 39,008 samples on <c>dm_lockdown</c> reaching it.
    /// </remarks>
    public const int MaxTries = 1000;

    /// <summary>The map.</summary>
    private readonly AmbientScene _scene;

    /// <summary>This leaf's stream. Reset per leaf by the caller.</summary>
    private StockRandomStream _random;

    /// <summary>Makes a sampler for one leaf.</summary>
    /// <param name="scene">The map.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    /// <remarks>
    /// ONE PER LEAF. Reusing an instance across leaves would carry the stream
    /// forward and change every sample position after the first leaf; the
    /// builder constructs a fresh one inside its per-leaf function for that
    /// reason, exactly as stock does.
    /// </remarks>
    /// <param name="displacements">The work item's displacement scratch.</param>
    public LeafSampler(AmbientScene scene, DispTestedScratch displacements)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(displacements);
        _scene = scene;
        _disps = displacements;
        _random = new StockRandomStream();
    }

    /// <summary>The work item's displacement scratch.</summary>
    private readonly DispTestedScratch _disps;

    /// <summary>
    /// Draws one sample position inside a leaf
    /// (<c>GenerateLeafSamplePosition</c>).
    /// </summary>
    /// <param name="leafIndex">The leaf.</param>
    /// <param name="leafPlanes">Its inward boundary planes.</param>
    /// <returns>A position inside the leaf, or its bounding-box centre.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="leafPlanes"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// THREE DRAWS PER ATTEMPT, x then y then z, and all three happen before
    /// any test -- so a rejected attempt still consumes exactly three numbers.
    /// Short-circuiting the draws on a leaf with zero extent in one axis would
    /// desynchronise the stream for the whole map.
    /// </para>
    /// <para>
    /// The plane test walks BACKWARDS and rejects on the first plane the point
    /// is not <see cref="DistEpsilon"/> inside. The epsilon is why a sample is
    /// never flush against the leaf boundary, and that in turn is why tracing
    /// from it with a ray epsilon of zero mostly works.
    /// </para>
    /// </remarks>
    public Vec3 Generate(int leafIndex, List<LeafPlane> leafPlanes)
    {
        ArgumentNullException.ThrowIfNull(leafPlanes);

        ref readonly DLeaf leaf = ref _scene.Leaves[leafIndex];

        float dx = leaf.Maxs[0] - leaf.Mins[0];
        float dy = leaf.Maxs[1] - leaf.Mins[1];
        float dz = leaf.Maxs[2] - leaf.Mins[2];

        Vec3 samplePosition = Vec3.Zero;
        bool valid = false;

        for (int i = 0; i < MaxTries && !valid; i++)
        {
            samplePosition = new Vec3(
                leaf.Mins[0] + _random.RandomFloat(0, dx),
                leaf.Mins[1] + _random.RandomFloat(0, dy),
                leaf.Mins[2] + _random.RandomFloat(0, dz));

            valid = true;

            for (int j = leafPlanes.Count; --j >= 0 && valid;)
            {
                float d = Vec3.Dot(leafPlanes[j].Normal, samplePosition) - leafPlanes[j].Dist;
                if (d < DistEpsilon)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                continue;
            }

            for (int j = 0; j < 6; j++)
            {
                int axis = j % 3;
                float bound = j < 3 ? leaf.Mins[axis] : leaf.Maxs[axis];
                Vec3 start = axis switch
                {
                    0 => new Vec3(bound, samplePosition.Y, samplePosition.Z),
                    1 => new Vec3(samplePosition.X, bound, samplePosition.Z),
                    _ => new Vec3(samplePosition.X, samplePosition.Y, bound),
                };

                (float t, Vec3 normal) = CastRayInLeaf(samplePosition, start, leafIndex);

                if (t == 0.0f)
                {
                    // Inside a func_detail brush.
                    valid = false;
                    break;
                }

                if (t != 1.0f)
                {
                    Vec3 delta = start - samplePosition;
                    if (Vec3.Dot(delta, normal) > 0)
                    {
                        // The back side of a displacement.
                        valid = false;
                        break;
                    }
                }
            }
        }

        if (!valid)
        {
            samplePosition = (new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2])
                + new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2])) * 0.5f;
        }

        return samplePosition;
    }

    /// <summary>
    /// The 1/32 unit the sample must be inside every boundary plane by
    /// (<c>DIST_EPSILON</c>).
    /// </summary>
    public const float DistEpsilon = LeafBrushTrace.DistEpsilonSingle;

    /// <summary>
    /// <c>CastRayInLeaf</c>: how far a ray gets
    /// before the leaf's own brushes or displacements stop it.
    /// </summary>
    /// <param name="start">Where the ray starts. Note stock's argument order.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="leafIndex">The leaf to test within.</param>
    /// <returns>The fraction and, when it is not 1, the surface normal.</returns>
    /// <remarks>
    /// The brush trace, then <c>StartRayTest</c> and
 /// <c>ClipRayToDispInLeaf</c>: the nearer of the two wins,
    /// strictly. The displacement scratch is this sampler's own -- stock's
    /// <c>s_DispTested[iThread]</c> as per-work-item state.
    /// </remarks>
    private (float Fraction, Vec3 Normal) CastRayInLeaf(Vec3 start, Vec3 end, int leafIndex)
    {
        LeafBrushHit hit = LeafBrushTrace.Trace(leafIndex, start, end, _scene);
        (float fraction, Vec3 normal) = hit.Fraction != 1.0f || hit.StartSolid
            ? (hit.Fraction, hit.Normal)
            : (1.0f, Vec3.Zero);

        DispCollisionSet disps = _scene.Tracer.Displacements;
        if (disps.Count > 0)
        {
            _disps.StartRayTest();
            disps.ClipRayInLeaf(_disps, start, end - start, leafIndex, out DispRayHit disp);
            if (disp.Distance < fraction)
            {
                fraction = disp.Distance;
                normal = disp.Normal;
            }
        }

        return (fraction, normal);
    }
}
