using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Decides, for a leaf, whether a ray from its centre can reach the sky.
/// </summary>
/// <param name="leafCentre">The leaf's bounding box centre.</param>
/// <returns>True when any of 162 probe directions hits sky.</returns>
/// <remarks>
/// The seam for <c>CanLeafTraceToSky</c>, which
/// needs the ray tracer Phase 4b builds. It is a delegate rather than a direct
/// call so that <see cref="SkyLeafVisibility"/> can be exercised, and its two
/// PVS passes gated against stock, without a tracer in hand.
/// </remarks>
public delegate bool LeafSkyProbe(Vec3 leafCentre);

/// <summary>
/// <c>BuildVisForLightEnvironment</c>: which leaves
/// see sky, and the union of their PVS rows that becomes the sun's reach.
/// </summary>
/// <remarks>
/// <para>
/// Three passes, and their separation is load-bearing. Stock's own comment at
/// Says why the third exists: "Must set the bits in a separate
/// pass so as to not flood-fill LEAF_FLAGS_SKY everywhere". Pass two reads the
/// flags pass one set and writes to a side bit array; folding it into pass
/// three would let a leaf that just became sky-visible make its neighbours
/// sky-visible, and so on across the map.
/// </para>
/// <para>
/// <b>Pass two is O(leaves squared).</b> For every non-sky leaf it walks EVERY
/// other leaf looking for a sky one in its PVS, with only
/// an early break once a 3D-sky leaf is found. On a map with 40,000 leaves
/// that is 1.6 billion iterations of a cheap test. It is reproduced as written
/// -- this is a correctness port -- and it is an obvious candidate for the
/// performance phase, which could invert the loop and walk only the sky leaves.
/// </para>
/// <para>
/// <b>This mutates the leaf lump's flags.</b> SKY and SKY2D are cleared on
/// every leaf and recomputed, and they are written back to the BSP, so a
/// second vrad run on the same map starts from the first run's answer only
/// because the clear throws it away. The flags live
/// <see cref="Flags"/> here rather than in
/// <see cref="LightGeometry.Leaves"/>, which stays immutable.
/// </para>
/// </remarks>
public sealed class SkyLeafVisibility
{
    private readonly LeafSkyProbe? _probe;

    /// <summary>Creates the pass.</summary>
    /// <param name="probe">
    /// The sky trace for radial-vis leaves, or null to skip that branch. See
    /// <see cref="RadialLeavesSkipped"/>.
    /// </param>
    public SkyLeafVisibility(LeafSkyProbe? probe = null) => _probe = probe;

    private readonly List<int> _radialCandidates = [];

    /// <summary>
    /// The radial-vis leaves that pass three would have probed with
    /// <c>CanLeafTraceToSky</c> but could not,
    /// because no synchronous probe was given. A batch tracer answers them
    /// afterwards and calls <see cref="MarkSky"/>; the order is leaf order.
    /// </summary>
    public IReadOnlyList<int> RadialCandidates => _radialCandidates;

    /// <summary>Sets <c>LEAF_FLAGS_SKY</c> on a leaf a probe found could see sky.</summary>
    /// <param name="leaf">The leaf.</param>
    public void MarkSky(int leaf)
    {
        if ((Flags[leaf] & LeafFlags.Sky) == 0)
        {
            Flags[leaf] |= LeafFlags.Sky;
            SkyLeaves++;
        }
    }

    /// <summary>
    /// The per-leaf flags as this pass left them, parallel to
    /// <see cref="LightGeometry.Leaves"/>.
    /// </summary>
    public LeafFlags[] Flags { get; private set; } = [];

    /// <summary>How many leaves have a sky face in them.</summary>
    public int LeavesWithSkyFaces { get; private set; }

    /// <summary>How many leaves ended up flagged <see cref="LeafFlags.Sky"/>.</summary>
    public int SkyLeaves { get; private set; }

    /// <summary>How many ended up flagged <see cref="LeafFlags.Sky2D"/>.</summary>
    public int Sky2DLeaves { get; private set; }

    /// <summary>
    /// How many radial-vis leaves were left unresolved because no probe was
    /// supplied.
    /// </summary>
    /// <remarks>
    /// Non-zero means the answer is INCOMPLETE rather than merely approximate:
    /// those leaves keep whatever the PVS passes gave them, and stock would
    /// have traced 162 rays from each to see if any reaches sky. A map whose
    /// vvis run used radial vis is the only one where this can be non-zero.
    /// </remarks>
    public int RadialLeavesSkipped { get; private set; }

    /// <summary>
    /// Runs the three passes and merges the result into the sun's PVS.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="visibility">The PVS accessor.</param>
    /// <param name="skyLight">The sun, whose PVS is merged into.</param>
    /// <param name="ambient">The sun's ambient partner, likewise.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public void Build(
        LightGeometry geometry,
        LightVisibility visibility,
        DirectLight skyLight,
        DirectLight ambient)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(skyLight);
        ArgumentNullException.ThrowIfNull(ambient);

        LeafInfo[] leaves = geometry.Leaves;
        Flags = new LeafFlags[leaves.Length];
        LeavesWithSkyFaces = 0;
        SkyLeaves = 0;
        Sky2DLeaves = 0;
        RadialLeavesSkipped = 0;
        _radialCandidates.Clear();

        // Pass one: a leaf containing a sky face is a
        // sky leaf, and its PVS joins the sun's.
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            // The SKY and SKY2D bits are cleared; every OTHER flag --
            // RADIAL in particular, which pass three reads -- survives.
            Flags[leaf] = leaves[leaf].Flags & ~(LeafFlags.Sky | LeafFlags.Sky2D);

            int first = leaves[leaf].FirstLeafFace;
            for (int i = 0; i < leaves[leaf].NumLeafFaces; i++)
            {
                int faceNum = geometry.LeafFaces[first + i];
                int flags = geometry.TexInfos[geometry.Faces[faceNum].TexInfo].Flags;

                if ((flags & (int)SurfaceFlags.Sky) == 0)
                {
                    continue;
                }

                // SKY2D and SKY are exclusive here: a face with
                // both bits set counts only as 2D.
                Flags[leaf] |= (flags & (int)SurfaceFlags.Sky2D) != 0
                    ? LeafFlags.Sky2D
                    : LeafFlags.Sky;

                visibility.MergeLightVis(skyLight, leaves[leaf].Cluster);
                visibility.MergeLightVis(ambient, leaves[leaf].Cluster);
                LeavesWithSkyFaces++;
                break;
            }
        }

        // Pass two: which non-sky leaves can SEE a sky leaf.
        bool[] seesSky = new bool[leaves.Length];
        bool[] seesSky2D = new bool[leaves.Length];
        byte[] pvs = new byte[Math.Max(visibility.RowBytes, 1)];

        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            if ((Flags[leaf] & LeafFlags.Sky) != 0)
            {
                continue;
            }

            if ((leaves[leaf].Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            visibility.GetVisCache(leaves[leaf].Cluster, pvs);

            for (int other = 0; other < leaves.Length; other++)
            {
                if (other == leaf)
                {
                    continue;
                }

                if ((Flags[other] & (LeafFlags.Sky | LeafFlags.Sky2D)) == 0)
                {
                    continue;
                }

                if (!LightVisibility.PvsCheck(pvs, leaves[other].Cluster))
                {
                    continue;
                }

                if ((Flags[other] & LeafFlags.Sky2D) != 0)
                {
                    seesSky2D[leaf] = true;
                }

                if ((Flags[other] & LeafFlags.Sky) != 0)
                {
                    seesSky[leaf] = true;
                    break;
                }
            }
        }

        // Pass three: apply, and rescue radial-vis leaves.
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            if ((Flags[leaf] & LeafFlags.Sky) != 0)
            {
                continue;
            }

            if ((leaves[leaf].Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            if (seesSky2D[leaf])
            {
                Flags[leaf] |= LeafFlags.Sky2D;
            }

            if (seesSky[leaf])
            {
                // 1446-1447. 3D sky supersedes 2D, and clears it.
                Flags[leaf] |= LeafFlags.Sky;
                Flags[leaf] &= ~LeafFlags.Sky2D;
                continue;
            }

            // Radial vis culls portals, so the PVS can be missing
            // a path that really exists. Tracing is the fallback, and stock's
            // FIXME notes it cannot tell 2D sky from 3D when it does.
            if ((Flags[leaf] & LeafFlags.Radial) == 0)
            {
                continue;
            }

            if (_probe is null)
            {
                // Left for a batch probe: see RadialCandidates.
                RadialLeavesSkipped++;
                _radialCandidates.Add(leaf);
                continue;
            }

            Vec3 centre = (leaves[leaf].Mins + leaves[leaf].Maxs) * 0.5f;
            if (_probe(centre))
            {
                Flags[leaf] |= LeafFlags.Sky;
            }
        }

        foreach (LeafFlags flags in Flags)
        {
            if ((flags & LeafFlags.Sky) != 0)
            {
                SkyLeaves++;
            }

            if ((flags & LeafFlags.Sky2D) != 0)
            {
                Sky2DLeaves++;
            }
        }
    }
}
