using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// What a call to <see cref="PatchSubdivider.Subdivide"/> did.
/// </summary>
/// <param name="PatchesBefore">
/// How many patches existed when it started: stock's
/// <c>%i patches before subdivision</c>.
/// </param>
/// <param name="PatchesAfter">
/// How many exist afterwards: <c>%i patches after subdivision</c>.
/// </param>
/// <param name="ZeroAreaChildren">
/// How many splits produced a child with no area and were abandoned. Stock
/// prints <c>zero area child patch</c> once per occurrence and carries on with
/// the parent unsplit.
/// </param>
/// <param name="SolidOriginPatches">
/// How many patches had an origin in solid space and had to borrow a cluster
/// from one of their winding's corners.
/// </param>
/// <param name="ClusterlessPatches">
/// How many still had no cluster afterwards. Those are invisible to every
/// light's PVS test and so receive no direct light at all.
/// </param>
public readonly record struct SubdivisionReport(
    int PatchesBefore,
    int PatchesAfter,
    int ZeroAreaChildren,
    int SolidOriginPatches,
    int ClusterlessPatches);

/// <summary>
/// <c>SubdividePatches</c>, <c>SubdividePatch</c>, <c>CreateChildPatch</c> and
/// <c>PreventSubdivision</c>.
/// </summary>
/// <remarks>
/// <para>
/// Splitting each root patch down a binary tree until every leaf is about
/// <c>-chop</c> luxels across. The leaves are what the transfer matrix is built
/// over, so this pass sets the size of the radiosity problem: on this project's
/// catalogue it turns 36 patches into 2,632, and on a real map 1,292 into
/// </para>
/// <para>
/// <b>Three passes run AFTER the recursion and none of them is optional.</b>
/// The face lists are rebuilt from scratch so that children
/// come before parents; cluster numbers are resolved only now
/// Because a child's origin can land in a different leaf
/// from its parent's; and the per-cluster leaf list is built in REVERSE
/// Which makes it come out in forward order after the
/// prepends. Reordering any of the three changes the order transfers are
/// accumulated in, and floating-point addition is not associative.
/// </para>
/// <para>
/// <b>Nothing happens at all when <c>-bounce 0</c> is set</b>
/// The early return leaves every root patch as its own leaf
/// and leaves <c>faceParents</c>, <c>clusterChildren</c> and every
/// <c>parent</c> field untouched -- so a <c>-bounce 0</c> compile has patches
/// whose <c>parent</c> is still the zero the <c>memset</c> left, meaning patch
/// 0, rather than -1. Reproduced; see <see cref="Subdivide"/>.
/// </para>
/// </remarks>
public static class PatchSubdivider
{
    /// <summary>
    /// Runs the whole subdivision pass over a patch set.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="neighbours">The smoothing pass, for child patch normals.</param>
    /// <param name="patches">The patches to subdivide, in place.</param>
    /// <param name="tree">The compiled BSP, for resolving cluster numbers.</param>
    /// <param name="minChop">
    /// <c>minchop</c>: the finest patch width in luxels, stock's 4.
    /// </param>
    /// <param name="bounces">
    /// <c>numbounce</c>. Zero skips the entire pass, as stock does.
    /// </param>
    /// <param name="fast">
    /// <c>do_fast</c>. True skips the recursion but still runs the three
    /// bookkeeping passes.
    /// </param>
    /// <param name="smoothingThreshold">The smoothing cosine.</param>
    /// <param name="subdivideDisplacement">
    /// Splits a displacement root patch (<c>StaticDispMgr()-&gt;SubdividePatch</c>,
    ///); null leaves displacement patches whole.
    /// </param>
    /// <returns>The counts stock prints, plus two it does not.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public static SubdivisionReport Subdivide(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        PatchSet patches,
        CompiledBspTree tree,
        float minChop,
        int bounces,
        bool fast,
        float smoothingThreshold,
        Action<PatchSet, int>? subdivideDisplacement = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(tree);

        int before = patches.Count;

        // Not "skip the splitting" -- skip EVERYTHING,
        // including the face-list rebuild and the cluster resolution.
        if (bounces == 0)
        {
            return new SubdivisionReport(before, before, 0, 0, 0);
        }

        // Cache each root patch's plane distance and thread the
        // faceParents list. This runs over the ORIGINAL count, so the children
        // created below are not in faceParents -- which is the definition of
        // that list.
        for (int i = 0; i < before; i++)
        {
            ref Patch patch = ref patches.At(i);
            patch.CachedPlaneDist = patch.PlaneDist;
            patch.NextParent = patches.FaceParents[patch.FaceNumber];
            patches.FaceParents[patch.FaceNumber] = i;
        }

        int zeroArea = 0;

        // Also over the original count, and note that `parent` is set
        // to -1 for every root patch HERE rather than in MakePatchForFace.
        for (int i = 0; i < before; i++)
        {
            patches.At(i).Parent = Patch.Invalid;

            if (PreventSubdivision(geometry, patches, i))
            {
                continue;
            }

            if (fast)
            {
                continue;
            }

            // A displacement patch goes to the displacement
            // manager's own splitter, which this lane does not own.
            if (geometry.Faces[patches.At(i).FaceNumber].DispInfo == -1)
            {
                SubdividePatch(
                    geometry, neighbours, patches, i, minChop, smoothingThreshold, ref zeroArea);
            }
            else
            {
                // StaticDispMgr->SubdividePatch, lane 4e's.
                subdivideDisplacement?.Invoke(patches, i);
            }
        }

        RebuildFaceLists(patches);
        (int solid, int clusterless) = ResolveClusters(patches, tree);

        return new SubdivisionReport(before, patches.Count, zeroArea, solid, clusterless);
    }

    /// <summary>
    /// <c>PreventSubdivision</c>: whether a surface takes
    /// or emits light at all.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="patches">The patch set.</param>
    /// <param name="patchIndex">The patch.</param>
    /// <returns>True when the patch must not be split.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="geometry"/> or <paramref name="patches"/> is null.
    /// </exception>
    /// <remarks>
    /// The second test reads <c>SURF_LIGHT</c>, which
    /// <see cref="PatchBuilder"/> may have OR-ed in during the patch pass -- so
    /// a <c>SURF_NOLIGHT</c> material that is ALSO a texlight is still chopped.
    /// That ordering is why the flag mutation cannot be tidied away.
    /// </remarks>
    public static bool PreventSubdivision(LightGeometry geometry, PatchSet patches, int patchIndex)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(patches);

        int faceNum = patches.At(patchIndex).FaceNumber;
        int flags = geometry.TexInfos[geometry.Faces[faceNum].TexInfo].Flags;

        if ((flags & (int)SurfaceFlags.NoChop) != 0)
        {
            return true;
        }

        return (flags & (int)SurfaceFlags.NoLight) != 0
            && (flags & (int)SurfaceFlags.Light) == 0;
    }

    private static void SubdividePatch(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        PatchSet patches,
        int patchIndex,
        float minChop,
        float smoothingThreshold,
        ref int zeroArea)
    {
        // Sky patches exist so the form factors work out and
        // are never split.
        if (patches.At(patchIndex).Sky)
        {
            return;
        }

        Vec3 total;
        float chop;
        {
            ref readonly Patch patch = ref patches.At(patchIndex);
            total = (patch.Maxs - patch.Mins) * patch.LuxScale;
            chop = patch.Chop;
        }

        // Widest axis and the subdivide decision, in one pass. The
        // ">= chop AND >= minchop" pair matters: chop can have been halved
        // below minchop by an earlier pass, and the second test is what stops
        // the recursion from running away when it has.
        int widestAxis = -1;
        float widest = -1f;
        bool subdivide = false;
        for (int i = 0; i < 3; i++)
        {
            float t = Component(total, i);
            if (t > widest)
            {
                widestAxis = i;
                widest = t;
            }

            if (t >= chop && t >= minChop)
            {
                subdivide = true;
            }
        }

        if (!subdivide && widestAxis != -1)
        {
            // 874-885. "Make more square": a patch more than twice as long as
            // it is wide in BOTH other axes is split anyway, and its chop is
            // halved at the same time so the halves do not immediately qualify
            // again.
            float w = Component(total, widestAxis);
            if (w > Component(total, (widestAxis + 1) % 3) * 2f
                && w > Component(total, (widestAxis + 2) % 3) * 2f
                && chop > minChop)
            {
                subdivide = true;
                chop = Math.Max(minChop, chop / 2f);
                patches.At(patchIndex).Chop = chop;
            }
        }

        if (!subdivide)
        {
            return;
        }

        WindingArena arena = patches.Arena;
        Winding winding;
        float dist;
        Vec3 split = Vec3.Zero;
        {
            ref readonly Patch patch = ref patches.At(patchIndex);
            winding = patch.Winding;
            split = widestAxis switch
            {
                0 => new Vec3(1f, 0f, 0f),
                1 => new Vec3(0f, 1f, 0f),
                _ => new Vec3(0f, 0f, 1f),
            };
            dist = (Component(patch.Mins, widestAxis) + Component(patch.Maxs, widestAxis)) * 0.5f;
        }

        // ON_EPSILON here, not the lightmap epsilon: this is a world-space
        // winding.
        arena.ClipEpsilon(
            winding, split, dist, LightConstants.OnEpsilon, out Winding o1, out Winding o2);

        float area1 = arena.AreaAndBalancePoint(o1, out Vec3 center1);
        float area2 = arena.AreaAndBalancePoint(o2, out Vec3 center2);

        // Stock prints and returns, leaving BOTH child windings
        // allocated and the parent unsplit. The leak is not reproduced; the
        // control flow is.
        if (area1 == 0f || area2 == 0f)
        {
            zeroArea++;
            arena.Free(o2);
            arena.Free(o1);
            return;
        }

        int child1 = CreateChildPatch(
            geometry, neighbours, patches, patchIndex, o1, area1, center1, minChop, smoothingThreshold);
        int child2 = CreateChildPatch(
            geometry, neighbours, patches, patchIndex, o2, area2, center2, minChop, smoothingThreshold);

        {
            // 911-914, with stock's own comment about the refetch: both
            // AddToTail calls above may have reallocated the array.
            ref Patch patch = ref patches.At(patchIndex);
            patch.Child1 = child1;
            patch.Child2 = child2;
        }

        SubdividePatch(geometry, neighbours, patches, child1, minChop, smoothingThreshold, ref zeroArea);
        SubdividePatch(geometry, neighbours, patches, child2, minChop, smoothingThreshold, ref zeroArea);
    }

    private static int CreateChildPatch(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        PatchSet patches,
        int parentIndex,
        Winding winding,
        float area,
        Vec3 center,
        float minChop,
        float smoothingThreshold)
    {
        // The whole parent, copied by value -- including its
        // winding handle, its lighting accumulators and its displacement
        // indices -- and then the fields below overwritten.
        Patch child = patches.At(parentIndex);

        child.Next = Patch.Invalid;
        child.NextParent = Patch.Invalid;
        child.NextClusterChild = Patch.Invalid;
        child.Child1 = Patch.Invalid;
        child.Child2 = Patch.Invalid;
        child.Parent = parentIndex;
        child.IterationKey = 0;

        child.Winding = winding;
        child.Area = area;
        child.Origin = center;

        // A displacement patch would take the displacement surface's
        // own normal; stock says "shouldn't get here anymore" and prints,
        // because SubdividePatches routes displacements elsewhere. The brush
        // branch is the only reachable one here.
        child.Normal = PhongNormals.Compute(
            geometry,
            neighbours,
            patches.Centroids,
            child.FaceNumber,
            child.Origin,
            smoothingThreshold);

        child.CachedPlaneDist = child.PlaneDist;
        patches.Arena.Bounds(winding, out Vec3 mins, out Vec3 maxs);
        child.Mins = mins;
        child.Maxs = maxs;

        // A surface light's patches are never refined by the edge
        // rule -- stock's comment is "don't check edges on surf lights".
        if (child.BaseLight != Vec3.Zero)
        {
            return patches.Add(child);
        }

        // A child that reaches the FACE's bounding box on some axis
        // sits on the face's silhouette, where a coarse patch shows as a
        // blocky shadow edge; so its chop is halved once. The guard is that the
        // child is ALREADY below chop in every axis, which is what makes this
        // the last halving rather than a loop.
        Vec3 total = (maxs - mins) * child.LuxScale;
        if (child.Chop > minChop
            && total.X < child.Chop
            && total.Y < child.Chop
            && total.Z < child.Chop)
        {
            for (int i = 0; i < 3; i++)
            {
                bool touchesFaceEdge = Component(child.FaceMaxs, i) == Component(maxs, i)
                    || Component(child.FaceMins, i) == Component(mins, i);
                if (touchesFaceEdge && Component(total, i) > minChop)
                {
                    child.Chop = Math.Max(minChop, child.Chop / 2f);
                    break;
                }
            }
        }

        return patches.Add(child);
    }

    private static void RebuildFaceLists(PatchSet patches)
    {
        // Clear every head, then prepend every patch in
        // index order. Children have higher indices than their parents, so
        // prepending in index order puts CHILDREN FIRST in the resulting list
        // -- which BuildPatchLights depends on to push sample light up to
        // parents in a single forward walk.
        Array.Fill(patches.FacePatches, Patch.Invalid);

        for (int i = 0; i < patches.Count; i++)
        {
            ref Patch patch = ref patches.At(i);
            patch.Next = patches.FacePatches[patch.FaceNumber];
            patches.FacePatches[patch.FaceNumber] = i;
        }
    }

    private static (int Solid, int Clusterless) ResolveClusters(PatchSet patches, CompiledBspTree tree)
    {
        int solid = 0;
        int clusterless = 0;

        // And stock's comment explains the timing: a child
        // patch's origin can land in a different leaf from its parent's,
        // because only model 0's faces are split by the BSP that governs the
        // PVS.
        for (int i = 0; i < patches.Count; i++)
        {
            ref Patch patch = ref patches.At(i);
            patch.ClusterNumber = tree.ClusterFromPoint(patch.Origin);

            if (patch.ClusterNumber != -1)
            {
                continue;
            }

            solid++;

            // An origin in solid space -- which detail and
            // displacement surfaces produce -- borrows the cluster of the
            // FIRST winding corner that has one.
            foreach (Vec3 point in patches.Arena.Points(patch.Winding))
            {
                int cluster = tree.ClusterFromPoint(point);
                if (cluster != -1)
                {
                    patch.ClusterNumber = cluster;
                    break;
                }
            }

            if (patch.ClusterNumber == -1)
            {
                clusterless++;
            }
        }

        // In REVERSE index order, so that after the prepends each
        // cluster's list comes out in forward index order. Only leaf patches
        // are listed. The heads are NOT cleared first: stock initialises them
        // once in VRAD_LoadBSP and AddDispsToClusterTable appends
        // to the same lists afterwards, so clearing here would be a change.
        for (int num = 0; num < patches.Count; num++)
        {
            int i = patches.Count - num - 1;
            ref Patch patch = ref patches.At(i);

            if (patch.HasChildren || patch.ClusterNumber == -1)
            {
                continue;
            }

            patch.NextClusterChild = patches.ClusterChildren[patch.ClusterNumber];
            patches.ClusterChildren[patch.ClusterNumber] = i;
        }

        return (solid, clusterless);
    }

    private static float Component(Vec3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
