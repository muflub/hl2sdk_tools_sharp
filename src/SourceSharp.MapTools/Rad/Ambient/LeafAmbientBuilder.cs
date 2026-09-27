//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>ComputePerLeafAmbientLighting</c>:
/// the whole leaf-ambient stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>51.6 % OF STOCK VRAD'S WALL CLOCK RUNS INSIDE THIS CLASS.</b>
/// <see cref="ComputeLeaf"/> is a pure function of (scene, lights, leaf,
/// options), so the stage parallelises over leaves with no shared writable
/// state; the per-worker <see cref="AmbientSampler"/> only holds buffers every
/// sample overwrites. Leaves are claimed longest-first by their candidate
/// sample count, because a leaf costs up to 128 x 162 rays and stock's
/// cheapest-first order is what left it at 19 % parallel efficiency.
/// </para>
/// <para>
/// FOUR PHASES, in stock's order, and the order matters:
/// </para>
/// <list type="number">
/// <item><description>
/// Classify the world lights, which WRITES <c>DWL_FLAGS_INAMBIENTCUBE</c> back
/// into the lump. Every later phase reads those flags.
/// </description></item>
/// <item><description>
/// Sample every leaf.
/// </description></item>
/// <item><description>
/// Encode the samples into the two lumps, assigning each leaf a run.
/// </description></item>
/// <item><description>
/// Point every EMPTY leaf at its nearest neighbour that has samples. This reads
/// the index built in phase 3, so it cannot be folded into it -- and it
/// overloads the index's meaning: a count of zero makes
/// <c>firstAmbientSample</c> a LEAF number rather than a sample offset.
/// </description></item>
/// </list>
/// </remarks>
public static class LeafAmbientBuilder
{
    /// <summary>The largest number of candidate samples a leaf may draw.</summary>
    public const int MaxSampleCount = 128;

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    public const int ContentsSolid = 0x1;

    /// <summary>
    /// Runs the whole stage over a map.
    /// </summary>
    /// <param name="scene">The map, already gathered for one lighting mode.</param>
    /// <param name="worldLights">
    /// The mode's world lights, WRITABLE: phase 1 sets and clears
    /// <c>DWL_FLAGS_INAMBIENTCUBE</c> on them, and stock writes that back to
    /// the BSP.
    /// </param>
    /// <param name="options">What to reproduce and what to refuse.</param>
    /// <param name="visibility">
    /// <c>TestLine</c> for the baked surface lights, or null. Required when any
    /// light is classified into the cubes, unless
    /// <see cref="LeafAmbientOptions.RequireSurfaceLightVisibility"/> is off.
    /// </param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The two lumps and stock's counters.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NotSupportedException">
    /// A light is flagged into the cubes, no visibility was given, and
    /// <paramref name="options"/> did not waive the check.
    /// </exception>
    public static async Task<LeafAmbientResult> BuildAsync(
        AmbientScene scene,
        DWorldLight[] worldLights,
        LeafAmbientOptions options,
        IAmbientLightVisibility? visibility,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(worldLights);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        bool estimate = options.Compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);
        (int flagged, int surfaceLights) =
            LeafAmbientSurfaceLights.Classify(worldLights, estimate);

        if (options.RequireSurfaceLightVisibility && flagged > 0 && visibility is null)
        {
            throw new NotSupportedException(
                $"{flagged} world lights are flagged for the ambient cubes and no visibility "
                + "was given, so each would be added as though nothing occluded it. Pass an "
                + "IAmbientLightVisibility, or set RequireSurfaceLightVisibility = false.");
        }

        int leafCount = scene.Leaves.Length;
        List<AmbientSample>[] perLeaf;

        CompileParallelism parallelism = (options.Parallelism > 0
            ? new CompileParallelism { MaxDegree = options.Parallelism }
            : CompileParallelism.Default) with { Pool = options.Pool };

        if (visibility is TracerLineVisibility traced)
        {
            // The surface-light segments go to the tracer through the seam, a
            // worker's batch of whole leaves at a time (TestLineStage), so a
            // GPU tracer gets them and no worker ever waits on one. Largest
            // leaves first, as the queue orders them; the order changes only
            // who lights what.
            int[] order = [.. Enumerable.Range(0, leafCount)
                .OrderByDescending(leaf => CandidateSampleCount(scene, leaf, options))
                .ThenBy(leaf => leaf)];
            perLeaf = await TestLineStage.RunAsync(
                leafCount,
                order,
                parallelism,
                () => new LeafWorker(scene, worldLights, traced, options),
                options.BatchSegments,
                TestLineStage.DefaultBatchItems,
                "leaf ambient",
                cancellationToken).ConfigureAwait(false);
            return Encode(scene, perLeaf, flagged, surfaceLights);
        }

        WorkQueueOptions queueOptions = new()
        {
            Stage = "leaf ambient",
            ItemCost = leaf => CandidateSampleCount(scene, leaf, options),
            ChunkSize = 1,
        };

        using (WorkQueue queue = new(parallelism))
        {
            perLeaf = await queue.RunAsync(
                    leafCount,
                    (leaf, sampler, context) => ComputeLeaf(scene, sampler, leaf, options, context.CancellationToken),
                    _ => new AmbientSampler(scene, worldLights, visibility, options.Compliance),
                    queueOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Encode(scene, perLeaf, flagged, surfaceLights);
    }

    /// <summary>
    /// <c>ComputeAmbientForLeaf</c>: one
    /// leaf's surviving samples.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="sampler">The worker's cube computer.</param>
    /// <param name="leafIndex">The leaf.</param>
    /// <param name="options">What to reproduce.</param>
    /// <param name="cancellationToken">Polled once per sample.</param>
    /// <returns>The samples, possibly none.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// A PURE FUNCTION of the scene, the lights inside
    /// <paramref name="sampler"/>, the leaf and the options. The leaf's
    /// <see cref="LeafSampler"/> is constructed here, which is what gives every
    /// leaf a stream seeded zero -- stock's <c>CLeafSampler sampler( iThread );</c>
    /// is a local of this same function. Hoisting it would change
    /// every sample position in the map after the first leaf.
    /// </para>
    /// <para>
    /// The boundary planes are gathered BEFORE the solid test, as stock does;
    /// the early return makes that wasted work unobservable.
    /// </para>
    /// <para>
    /// The leaf's surface-light visibility is ONE call for all its samples
    /// (<see cref="AmbientSampler.AddSurfaceLights"/>), made once every
    /// sample's position and ray cube exist. Positions and ray cubes are made
    /// in stock's interleaved order (they share the displacement scratch);
    /// only the visibility waits for the leaf, and the samples join the list
    /// in order once it is answered.
    /// </para>
    /// </remarks>
    public static List<AmbientSample> ComputeLeaf(
        AmbientScene scene,
        AmbientSampler sampler,
        int leafIndex,
        LeafAmbientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(options);

        LeafPlan plan = PlanSamples(scene, sampler, leafIndex, options, cancellationToken);
        if (plan.Count > 0)
        {
            sampler.AddSurfaceLights(plan.Positions.AsSpan(0, plan.Count), plan.Cubes.AsSpan(0, plan.Count * AmbientCube.Sides));
        }

        return Collect(plan, options);
    }

    /// <summary>
    /// A leaf's samples before their surface lights: the positions and ray
    /// cubes, and where the leaf's segments start in its worker's batch.
    /// </summary>
    private sealed record LeafPlan(Vec3[] Positions, Vec3[] Cubes, int Count, int FirstSegment);

    /// <summary>
    /// <see cref="ComputeLeaf"/> up to the visibility: the sample positions,
    /// in stock's order, each with its ray cube.
    /// </summary>
    private static LeafPlan PlanSamples(
        AmbientScene scene,
        AmbientSampler sampler,
        int leafIndex,
        LeafAmbientOptions options,
        CancellationToken cancellationToken)
    {
        List<LeafPlane> leafPlanes = [];
        LeafSampler positions = new(scene, sampler.Displacements);
        LeafBoundaryPlanes.Gather(leafIndex, scene.Nodes, scene.Planes, scene.Parents, leafPlanes);

        int sampleCount = CandidateSampleCount(scene, leafIndex, options);

        if ((scene.Leaves[leafIndex].Contents & ContentsSolid) != 0)
        {
            // No samples in solid leaves; the encode step points them at the
            // nearest non-solid leaf instead.
            return new LeafPlan([], [], 0, 0);
        }

        Vec3[] samplePositions = new Vec3[sampleCount];
        Vec3[] cubes = new Vec3[sampleCount * AmbientCube.Sides];
        for (int i = 0; i < sampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samplePositions[i] = positions.Generate(leafIndex, leafPlanes);
            sampler.ComputeRayCube(samplePositions[i], cubes.AsSpan(i * AmbientCube.Sides, AmbientCube.Sides));
        }

        return new LeafPlan(samplePositions, cubes, sampleCount, 0);
    }

    /// <summary>The leaf's finished cubes into its sample list, in sample order, then compressed.</summary>
    private static List<AmbientSample> Collect(LeafPlan plan, LeafAmbientOptions options)
    {
        List<AmbientSample> list = [];
        for (int i = 0; i < plan.Count; i++)
        {
            AmbientSampleList.Add(
                list, plan.Positions[i], plan.Cubes.AsSpan(i * AmbientCube.Sides, AmbientCube.Sides), options.Compliance);
        }

        AmbientSampleList.Compress(list);
        return list;
    }

    /// <summary>
    /// A worker of the traced path: its cube computer, and its batch over the
    /// visibility's tracer.
    /// </summary>
    private sealed class LeafWorker(
        AmbientScene scene, DWorldLight[] worldLights, TracerLineVisibility visibility, LeafAmbientOptions options)
        : TestLineWorker<LeafPlan, List<AmbientSample>>(new TestLineBatch(visibility.Tracer))
    {
        private readonly AmbientSampler _sampler = new(scene, worldLights, visibility, options.Compliance);

        public override LeafPlan Plan(int item, CancellationToken cancellationToken)
        {
            LeafPlan plan = PlanSamples(scene, _sampler, item, options, cancellationToken);
            int first = _sampler.PlanSurfaceLights(plan.Positions.AsSpan(0, plan.Count), Lines, visibility.StockReciprocal);
            return plan with { FirstSegment = first };
        }

        public override List<AmbientSample> Resolve(int item, LeafPlan state)
        {
            _sampler.ResolveSurfaceLights(
                state.Positions.AsSpan(0, state.Count),
                state.Cubes.AsSpan(0, state.Count * AmbientCube.Sides),
                Lines,
                state.FirstSegment);
            return Collect(state, options);
        }
    }

    /// <summary>
    /// How many candidate samples a leaf draws
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="leafIndex">The leaf.</param>
    /// <param name="options">What to reproduce.</param>
    /// <returns>The count, between 1 and <see cref="MaxSampleCount"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The heuristic wants roughly one sample per player-sized volume: 32 units
    /// across in x and y, 64 tall. The divisions are INTEGER divisions of
    /// integer extents, so a leaf 63 units wide counts as one 32-unit step and
    /// not two -- that truncation is part of the count and is reproduced under
    /// both policies.
    /// </para>
    /// <para>
    /// What is NOT reproduced under
    /// <see cref="CompliancePolicy.Correct"/> is
    /// <see cref="StockQuirk.LeafAmbientSampleCountAxes"/>: stock clamps y and
    /// z to the x value, discarding both.
    /// </para>
    /// </remarks>
    public static int CandidateSampleCount(
        AmbientScene scene, int leafIndex, LeafAmbientOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return CandidateSampleCount(in scene.Leaves[leafIndex], options);
    }

    /// <summary>How many candidate samples a leaf draws, from the leaf alone.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="options">What to reproduce.</param>
    /// <returns>The count, between 1 and <see cref="MaxSampleCount"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static int CandidateSampleCount(ref readonly DLeaf leaf, LeafAmbientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int xSize = (leaf.Maxs[0] - leaf.Mins[0]) / 32;
        int ySize = (leaf.Maxs[1] - leaf.Mins[1]) / 32;
        int zSize = (leaf.Maxs[2] - leaf.Mins[2]) / 64;

        xSize = Math.Max(xSize, 1);
        if (options.Compliance.Emulates(StockQuirk.LeafAmbientSampleCountAxes))
        {
            ySize = Math.Max(xSize, 1);
            zSize = Math.Max(xSize, 1);
        }
        else
        {
            ySize = Math.Max(ySize, 1);
            zSize = Math.Max(zSize, 1);
        }

        int volumeCount = xSize * ySize * zSize;
        if (options.FastAmbient)
        {
            volumeCount = 1;
        }

        return Math.Clamp(volumeCount, 1, MaxSampleCount);
    }

    /// <summary>
    /// Turns the per-leaf sample lists into the two lumps.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="perLeaf">Each leaf's surviving samples.</param>
    /// <param name="flagged">How many lights went into the cubes.</param>
    /// <param name="surfaceLights">How many <c>emit_surface</c> lights there are.</param>
    /// <returns>The result.</returns>
    /// <remarks>
    /// <para>
    /// Positions are stored as EIGHT-BIT FRACTIONS of the leaf's bounding box,
    /// so a sample's recorded position is quantised to 1/255 of the leaf in each
    /// axis. That is the disk format and is not adjustable.
    /// </para>
    /// <para>
    /// Note the second pass. A leaf with no samples has its
    /// <c>firstAmbientSample</c> overwritten with a LEAF INDEX -- the nearest
    /// leaf that does have samples -- and the engine knows to follow it because
    /// the count is zero. Stock's own comment calls this an UNDONE and wants the
    /// engine to do it dynamically; until it does, the field means two different
    /// things depending on its neighbour.
    /// </para>
    /// </remarks>
    private static LeafAmbientResult Encode(
        AmbientScene scene, List<AmbientSample>[] perLeaf, int flagged, int surfaceLights)
    {
        int leafCount = perLeaf.Length;
        DLeafAmbientIndex[] index = new DLeafAmbientIndex[leafCount];
        List<DLeafAmbientLighting> lighting = new(leafCount * 4);

        for (int leafId = 0; leafId < leafCount; leafId++)
        {
            List<AmbientSample> list = perLeaf[leafId];
            index[leafId].AmbientSampleCount = (ushort)list.Count;

            if (list.Count == 0)
            {
                index[leafId].FirstAmbientSample = 0;
                continue;
            }

            index[leafId].FirstAmbientSample = (ushort)lighting.Count;

            ref readonly DLeaf leaf = ref scene.Leaves[leafId];
            foreach (AmbientSample sample in list)
            {
                DLeafAmbientLighting light = default;
                light.X = Fixed8Fraction(sample.Position.X, leaf.Mins[0], leaf.Maxs[0]);
                light.Y = Fixed8Fraction(sample.Position.Y, leaf.Mins[1], leaf.Maxs[1]);
                light.Z = Fixed8Fraction(sample.Position.Z, leaf.Mins[2], leaf.Maxs[2]);
                light.Pad = 0;

                for (int side = 0; side < AmbientCube.Sides; side++)
                {
                    light.Cube.Color[side] = StockLightColor.Encode(sample.Cube[side]);
                }

                lighting.Add(light);
            }
        }

        int badLeaves = 0;
        for (int i = 0; i < leafCount; i++)
        {
            if (index[i].AmbientSampleCount != 0)
            {
                continue;
            }

            if ((scene.Leaves[i].Contents & ContentsSolid) == 0)
            {
                badLeaves++;
            }

            index[i].AmbientSampleCount = 0;
            index[i].FirstAmbientSample = (ushort)NearestNeighborWithLight(scene, index, i);
        }

        return new LeafAmbientResult(
            index, [.. lighting], flagged, surfaceLights, badLeaves);
    }

    /// <summary>
    /// The nearest leaf that has ambient samples
    /// (<c>NearestNeighborWithLight</c>).
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="index">The index built so far.</param>
    /// <param name="leafId">The empty leaf.</param>
    /// <returns>The neighbour's index, or <paramref name="leafId"/> itself.</returns>
    /// <remarks>
    /// <para>
    /// The search box is the leaf's own box GROWN BY ITS OWN SIZE in each
    /// direction -- so a large leaf searches a large neighbourhood and a small
    /// one a small neighbourhood. A leaf with no lit leaf in that box gets
    /// itself, which leaves the engine following a self-reference to a leaf with
    /// no samples; stock does the same.
    /// </para>
    /// <para>
    /// Ties go to the FIRST leaf the box query enumerates, because the
    /// comparison is strictly less-than. The enumeration order is therefore part
    /// of the answer, which is why <see cref="ToolBspTree.EnumerateLeavesInBox"/> reproduces
    /// stock's front-child-first descent rather than using any convenient tree
    /// walk.
    /// </para>
    /// </remarks>
    private static int NearestNeighborWithLight(
        AmbientScene scene, DLeafAmbientIndex[] index, int leafId)
    {
        (Vec3 mins, Vec3 maxs) = LeafBounds(scene, leafId);
        Vec3 size = maxs - mins;

        List<int> leaves = [];
        ToolBspTree.EnumerateLeavesInBox(scene.Nodes, scene.Planes, mins - size, maxs + size, leaves);

        float bestDist = float.MaxValue;
        int bestIndex = leafId;

        foreach (int testIndex in leaves)
        {
            if (index[testIndex].AmbientSampleCount == 0)
            {
                continue;
            }

            (Vec3 testMins, Vec3 testMaxs) = LeafBounds(scene, testIndex);
            float dist = AabbDistance(mins, maxs, testMins, testMaxs);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = testIndex;
            }
        }

        return bestIndex;
    }

    /// <summary>A leaf's bounding box as floats (<c>LeafBounds</c>).</summary>
    /// <param name="scene">The map.</param>
    /// <param name="leafIndex">The leaf.</param>
    /// <returns>The box.</returns>
    private static (Vec3 Mins, Vec3 Maxs) LeafBounds(AmbientScene scene, int leafIndex)
    {
        ref readonly DLeaf leaf = ref scene.Leaves[leafIndex];
        return (new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
                new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));
    }

    /// <summary>
    /// How far apart two boxes are (<c>AABBDistance</c>,
    ///).
    /// </summary>
    /// <param name="mins0">The first box's low corner.</param>
    /// <param name="maxs0">Its high corner.</param>
    /// <param name="mins1">The second box's low corner.</param>
    /// <param name="maxs1">Its high corner.</param>
    /// <returns>The distance, 0 when they overlap on an axis.</returns>
    /// <remarks>
    /// Per axis, the gap is zero when the ranges overlap and NEGATIVE when they
    /// do not -- <c>leastMax - greatestMin</c> of disjoint ranges is negative --
    /// and the result is then the length of that vector, so the sign is
    /// discarded. The intermediate negatives are stock's and are kept rather
    /// than replaced by absolute values, because the length is the same either
    /// way and the comparison against the reference should not need an argument.
    /// </remarks>
    public static float AabbDistance(Vec3 mins0, Vec3 maxs0, Vec3 mins1, Vec3 maxs1)
    {
        Span<float> delta = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            float greatestMin = MathF.Max(mins0[i], mins1[i]);
            float leastMax = MathF.Min(maxs0[i], maxs1[i]);
            delta[i] = greatestMin < leastMax ? 0 : leastMax - greatestMin;
        }

        return new Vec3(delta[0], delta[1], delta[2]).Length();
    }

    /// <summary>
    /// A position as an eight-bit fraction of a range (<c>Fixed8Fraction</c>,
    ///).
    /// </summary>
    /// <param name="t">The value.</param>
    /// <param name="min">The range's low end.</param>
    /// <param name="max">Its high end.</param>
    /// <returns>0..255, or 0 for a degenerate range.</returns>
    /// <remarks>
    /// Rounds by adding a half and truncating, which is a round-half-UP rather
    /// than a round-half-even -- so 127.5 becomes 128. The clamp happens before
    /// the rounding, inside <c>RemapValClamped</c>, so a sample outside the
    /// leaf's own box (the bbox-centre fallback can produce one) saturates
    /// rather than wrapping.
    /// </remarks>
    public static byte Fixed8Fraction(float t, float min, float max)
    {
        if (max <= min)
        {
            return 0;
        }

        float frac = RayAmbientLighting.RemapValClamped(t, min, max, 0.0f, 255.0f);
        return (byte)(frac + 0.5f);
    }
}
