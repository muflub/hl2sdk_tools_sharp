//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>GATHERLFLAGS_*</c>.
/// </summary>
[Flags]
public enum GatherFlags
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>GATHERLFLAGS_FORCE_FAST</c>: quarter the sky samples, as <c>-fast</c> does.</summary>
    ForceFast = 1,

    /// <summary><c>GATHERLFLAGS_IGNORE_NORMALS</c>: use <c>CONSTANT_DOT</c> for every dot product.</summary>
    IgnoreNormals = 2,
}

/// <summary>
/// Up to four sample points and their normals: the <c>FourVectors</c> stock
/// gathers light for at once.
/// </summary>
/// <remarks>
/// <para>
/// Four lanes, kept as a unit, because two things in stock's gather are
/// decided across the lanes rather than per lane: a light is skipped for the
/// whole group only when EVERY lane's contribution is zero
/// And the 3D-skybox recursion runs only when
/// not every lane is occluded and is started from LANE ZERO's leaf
/// A group of fewer than four samples pads with
/// copies of its last one; the padding lanes are
/// never traced here -- their answers are the last real lane's, copied.
/// </para>
/// </remarks>
public sealed class SampleGroup
{
    /// <summary>Four lanes, always.</summary>
    public const int Lanes = 4;

    /// <summary>The illumination points (<c>m_Points</c>).</summary>
    public Vec3[] Points { get; } = new Vec3[Lanes];

    /// <summary>
    /// The normals, <c>[normal * 4 + lane]</c> (<c>m_PointNormals</c>): the
    /// flat or phong normal first, then the bump basis.
    /// </summary>
    public Vec3[] Normals { get; } = new Vec3[Lanes * BumpBasis.LightmapCount];

    /// <summary>Each lane's cluster (<c>m_Clusters</c>).</summary>
    public int[] Clusters { get; } = new int[Lanes];

    /// <summary>How many lanes are real samples, 1..4.</summary>
    public int Count { get; set; } = Lanes;

    /// <summary>How many normals each lane has: 1, or 4 when bumped.</summary>
    public int NormalCount { get; set; } = 1;

    /// <summary>A normal.</summary>
    /// <param name="normal">Which normal.</param>
    /// <param name="lane">Which lane.</param>
    /// <returns>A reference to it.</returns>
    public ref Vec3 Normal(int normal, int lane) => ref Normals[(normal * Lanes) + lane];
}

/// <summary>
/// What one light contributes to a <see cref="SampleGroup"/>:
/// <c>SSE_sampleLightOutput_t</c>.
/// </summary>
public sealed class GatherOutput
{
    /// <summary>The dot term per normal and lane, <c>[normal * 4 + lane]</c> (<c>m_flDot</c>).</summary>
    public float[] Dot { get; } = new float[SampleGroup.Lanes * BumpBasis.LightmapCount];

    /// <summary>The falloff per lane (<c>m_flFalloff</c>).</summary>
    public float[] Falloff { get; } = new float[SampleGroup.Lanes];

    /// <summary>The sun amount per lane (<c>m_flSunAmount</c>).</summary>
    public float[] SunAmount { get; } = new float[SampleGroup.Lanes];

    /// <summary>Zeroes everything, as <c>GatherSampleLightSSE</c> does first.</summary>
    public void Clear()
    {
        Array.Clear(Dot);
        Array.Clear(Falloff);
        Array.Clear(SunAmount);
    }
}

/// <summary>
/// <c>GatherSampleLightSSE</c> and its three helpers:
/// how much of one direct light reaches a group of sample points.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numerics.</b> Stock's SSE build computes every reciprocal and reciprocal
/// square root here with an ESTIMATE instruction plus one Newton step
/// And the spot exponent with a fixed-point
/// <c>PowSIMD</c> that rounds the exponent down to a quarter. Under
/// <see cref="StockQuirk.GatherReciprocalEstimate"/> and
/// <see cref="StockQuirk.SpotExponentQuarterSteps"/> both are reproduced
/// (the estimates machine-dependently, as the instructions are); by default
/// this divides and takes square roots exactly and uses <see cref="MathF.Pow(float, float)"/>.
/// </para>
/// <para>
/// <c>MaxSIMD</c>/<c>MinSIMD</c> are <c>maxps</c>/<c>minps</c>, which return
/// their SECOND operand when either is NaN. That matters in the sky-ambient
/// normalisation, where a sample with no valid sky direction divides zero by
/// zero: stock's NaN is clamped back to 0 by <c>MaxSIMD(dot, Four_Zeros)</c>
/// And <see cref="Math.Max(float, float)"/> would
/// have kept it. <see cref="MaxPs"/> and <see cref="MinPs"/> reproduce the
/// instruction.
/// </para>
/// </remarks>
public sealed class DirectLightGatherer
{
    private readonly Vec3[] _ambientDirections;
    private readonly Vec3[] _ambientDirectionsFast;
    private readonly Vec3[] _sunJitter;
    private readonly bool _estimates;
    private readonly bool _quarterPow;
    private readonly bool _laneZeroRecursion;
    private readonly int[][] _lightsByCluster;
    private readonly int[] _allLights;
    private readonly LightCullShape[] _cullShapes;

    /// <summary>Creates a gatherer.</summary>
    /// <param name="lights">The active lights, in list order.</param>
    /// <param name="sunAngularExtent"><c>g_SunAngularExtent</c> after the entities were parsed.</param>
    /// <param name="settings">The switches.</param>
    /// <param name="tree">The BSP, for the skybox recursion's leaf lookup.</param>
    /// <param name="leaves">The leaves, for their areas.</param>
    /// <param name="skyCameras">The 3D skyboxes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DirectLightGatherer(
        IReadOnlyList<DirectLight> lights,
        float sunAngularExtent,
        DirectLightingSettings settings,
        CompiledBspTree tree,
        LeafInfo[] leaves,
        SkyCameras skyCameras)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(skyCameras);

        Lights = lights;
        SunAngularExtent = sunAngularExtent;
        Settings = settings;
        Tree = tree;
        Leaves = leaves;
        SkyCameras = skyCameras;

        // The sample count is an int of a float
        // product, and -fast (or FORCE_FAST) quarters the UNSCALED count.
        int skySamples = (int)(LightConstants.VertexNormalCount * settings.SkySampleScale);
        _ambientDirections = SamplerPrefix(skySamples);
        _ambientDirectionsFast = SamplerPrefix(LightConstants.VertexNormalCount / 4);

        // Jitter is drawn only for d >= 1, so ray d uses value d-1.
        _sunJitter = SamplerPrefix(LightConstants.SunAreaLightSamples);

        _estimates = settings.Compliance.Emulates(StockQuirk.GatherReciprocalEstimate);
        _quarterPow = settings.Compliance.Emulates(StockQuirk.SpotExponentQuarterSteps);
        _laneZeroRecursion = settings.Compliance.Emulates(StockQuirk.SkyboxRecursionFromLaneZero);

        // What the dead-record cull needs of each light, snapshotted here as
        // the cluster lists below snapshot the PVS: the lights are final by
        // the time a gatherer is built.
        _cullShapes = new LightCullShape[lights.Count];
        for (int l = 0; l < lights.Count && !settings.KeepDeadLights; l++)
        {
            _cullShapes[l] = LightCullShape.Of(lights[l]);
        }

        // Per cluster, the lights whose PVS reaches it, in list order: a
        // group whose four lanes share a cluster need not test the others.
        _allLights = [.. Enumerable.Range(0, lights.Count)];
        int clusters = 0;
        foreach (LeafInfo leaf in leaves)
        {
            clusters = Math.Max(clusters, leaf.Cluster + 1);
        }

        _lightsByCluster = new int[clusters][];
        List<int> seen = [];
        for (int c = 0; c < clusters; c++)
        {
            seen.Clear();
            for (int l = 0; l < lights.Count; l++)
            {
                if (LightVisibility.PvsCheck(lights[l].Pvs, c))
                {
                    seen.Add(l);
                }
            }

            _lightsByCluster[c] = [.. seen];
        }
    }

    /// <summary>
    /// The indices, in list order, of every light that can reach at least one
    /// of four clusters -- every light that a group in those clusters does not
    /// skip on its PVS test. A superset is
    /// allowed; the caller still tests each lane.
    /// </summary>
    /// <param name="clusters">The four lanes' clusters.</param>
    /// <param name="scratch">Where a merged list is built when the lanes differ.</param>
    /// <returns>The candidate lights.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal ReadOnlySpan<int> LightsReaching(ReadOnlySpan<int> clusters, List<int> scratch)
    {
        int c0 = clusters[0];
        bool same = true;
        bool unknown = c0 < 0 || c0 >= _lightsByCluster.Length;
        for (int lane = 1; lane < clusters.Length; lane++)
        {
            same &= clusters[lane] == c0;
            unknown |= clusters[lane] < 0 || clusters[lane] >= _lightsByCluster.Length;
        }

        // PvsCheck passes a negative cluster, and one past the table is
        // tested against the row itself: both mean "test every light".
        if (unknown)
        {
            return _allLights;
        }

        if (same)
        {
            return _lightsByCluster[c0];
        }

        // Merge the distinct clusters' lists, keeping list order.
        scratch.Clear();
        Span<int> cursor = stackalloc int[SampleGroup.Lanes];
        cursor.Clear();
        while (true)
        {
            int next = int.MaxValue;
            for (int lane = 0; lane < clusters.Length; lane++)
            {
                int[] list = _lightsByCluster[clusters[lane]];
                if (cursor[lane] < list.Length)
                {
                    next = Math.Min(next, list[cursor[lane]]);
                }
            }

            if (next == int.MaxValue)
            {
                return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(scratch);
            }

            scratch.Add(next);
            for (int lane = 0; lane < clusters.Length; lane++)
            {
                int[] list = _lightsByCluster[clusters[lane]];
                if (cursor[lane] < list.Length && list[cursor[lane]] == next)
                {
                    cursor[lane]++;
                }
            }
        }
    }

    /// <summary>
    /// True when light <paramref name="lightIndex"/> provably lights no lane
    /// of <paramref name="group"/> (<see cref="DeadLightCull"/>): its record
    /// would emit no ray and add nothing, so the caller may leave it out.
    /// Always false with <see cref="DirectLightingSettings.KeepDeadLights"/>.
    /// </summary>
    /// <param name="lightIndex">The light's index in <see cref="Lights"/>.</param>
    /// <param name="group">The group, as it would be gathered.</param>
    /// <param name="bounds">The group's bounds, <see cref="SampleBounds.Of"/>.</param>
    /// <param name="flags">The gather flags it would be gathered with.</param>
    /// <returns>True when the record is dead.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CannotLight(int lightIndex, SampleGroup group, in SampleBounds bounds, GatherFlags flags) =>
        DeadLightCull.IsDead(in _cullShapes[lightIndex], group, in bounds, flags);

    /// <summary>
    /// Light every lane of a point light on its own, never four at once --
    /// for the facts that hold the two paths to the same bits.
    /// </summary>
    internal bool ScalarStandardLights { get; init; }

    /// <summary>The lights, in the active list's order.</summary>
    public IReadOnlyList<DirectLight> Lights { get; }

    /// <summary><c>g_SunAngularExtent</c>.</summary>
    public float SunAngularExtent { get; }

    /// <summary>The switches.</summary>
    public DirectLightingSettings Settings { get; }

    /// <summary>The BSP.</summary>
    public CompiledBspTree Tree { get; }

    /// <summary>The leaves.</summary>
    public LeafInfo[] Leaves { get; }

    /// <summary>The 3D skyboxes.</summary>
    public SkyCameras SkyCameras { get; }

    /// <summary><c>maxps</c>: <paramref name="b"/> unless <paramref name="a"/> is strictly greater.</summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand, returned on NaN.</param>
    /// <returns>The larger, SSE-style.</returns>
    public static float MaxPs(float a, float b) => a > b ? a : b;

    /// <summary><c>minps</c>: <paramref name="b"/> unless <paramref name="a"/> is strictly less.</summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand, returned on NaN.</param>
    /// <returns>The smaller, SSE-style.</returns>
    public static float MinPs(float a, float b) => a < b ? a : b;

    // Tape record kinds (GatherTape): what Resolve finishes.
    private const int TapeStandard = 1;
    private const int TapeSky = 2;
    private const int TapeSkyZero = 3;
    private const int TapeAmbient = 4;

    /// <summary>
    /// <c>GatherSampleLightSSE</c>: one light, one group.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="group">The points and normals.</param>
    /// <param name="laneNeeded">
    /// Which lanes' answers can matter -- the PVS mask. A lane that is not
    /// needed may be answered without tracing, EXCEPT where stock couples
    /// lanes (the sun's amount and the skybox recursion), which is traced for
    /// every lane regardless.
    /// </param>
    /// <param name="rays">
    /// Where rays are recorded (while <see cref="LightRayLog.Collecting"/>) or
    /// answered. The collecting call writes the gather's intermediate values to
    /// the log's tape and the replaying call finishes from them, so the two
    /// calls must come in the same order.
    /// </param>
    /// <param name="output">Receives the result (only meaningful when replaying).</param>
    /// <param name="flags">The gather flags.</param>
    /// <param name="epsilon">How far a sky-ambient ray starts off the surface.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The light has an unknown type.</exception>
    public void Gather(
        DirectLight light,
        SampleGroup group,
        ReadOnlySpan<bool> laneNeeded,
        LightRayLog rays,
        GatherOutput output,
        GatherFlags flags = GatherFlags.None,
        float epsilon = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(rays);
        ArgumentNullException.ThrowIfNull(output);

        if (rays.Collecting)
        {
            Emit(light, group, laneNeeded, rays, output, flags, epsilon);
        }
        else
        {
            Resolve(rays, output);
        }
    }

    /// <summary>
    /// The emit half of <see cref="Gather"/>: everything before the rays.
    /// Records the rays and writes one tape record for <see cref="Resolve"/>.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="group">The points and normals.</param>
    /// <param name="laneNeeded">The PVS mask, or empty for every lane.</param>
    /// <param name="rays">Where the rays and the tape go.</param>
    /// <param name="scratch">Scratch output; its contents are undefined afterwards.</param>
    /// <param name="flags">The gather flags.</param>
    /// <param name="epsilon">How far a sky-ambient ray starts off the surface.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Emit(
        DirectLight light,
        SampleGroup group,
        ReadOnlySpan<bool> laneNeeded,
        LightRayLog rays,
        GatherOutput scratch,
        GatherFlags flags,
        float epsilon)
    {
        switch (light.Type)
        {
            case EmitType.SkyLight:
                EmitSkyLight(light, group, rays, flags);
                break;
            case EmitType.SkyAmbient:
                AmbientSky(emit: true, group.Normals, group.Points, group.Count, group.NormalCount,
                    LaneMask(laneNeeded), flags, epsilon, rays, scratch);
                break;
            case EmitType.Point:
            case EmitType.Surface:
            case EmitType.Spotlight:
                EmitStandardLight(light, group, laneNeeded, rays, scratch, flags);
                break;
            default:
                throw new InvalidOperationException($"Bad dl->light.type {light.Type}");
        }
    }

    /// <summary>
    /// The resolve half of <see cref="Gather"/>: reads the next tape record and
    /// the answers to its rays, and finishes the light's contribution.
    /// </summary>
    /// <param name="rays">The tape and the answers, positioned at the record.</param>
    /// <param name="output">Receives the result.</param>
    /// <exception cref="InvalidOperationException">The tape does not hold a gather record here.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Resolve(LightRayLog rays, GatherOutput output)
    {
        GatherTape tape = rays.Tape;
        output.Clear();

        int kind = tape.ReadInt();
        int normals = tape.ReadInt();
        switch (kind)
        {
            case TapeStandard:
                ResolveStandardLight(normals, rays, output);
                break;
            case TapeSky:
                ResolveSkyLight(normals, rays, output);
                break;
            case TapeSkyZero:
                break;
            case TapeAmbient:
                AmbientSky(emit: false, [], [], 0, normals, 0, GatherFlags.None, 0.0f, rays, output);
                break;
            default:
                throw new InvalidOperationException($"the gather tape holds {kind} where a gather record belongs");
        }

        // A light behind the face must not light ANY bump
        // direction: every other normal is zeroed where the flat one is.
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            float d0 = MaxPs(output.Dot[lane], 0.0f);
            output.Dot[lane] = d0;
            bool notZero = d0 > 0.0f;
            for (int n = 1; n < normals; n++)
            {
                int k = (n * SampleGroup.Lanes) + lane;
                float dn = MaxPs(output.Dot[k], 0.0f);
                output.Dot[k] = notZero ? dn : 0.0f;
            }
        }
    }

    private static int LaneMask(ReadOnlySpan<bool> laneNeeded)
    {
        if (laneNeeded.IsEmpty)
        {
            return 0xF;
        }

        int mask = 0;
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            mask |= laneNeeded[lane] ? 1 << lane : 0;
        }

        return mask;
    }

    /// <summary>
    /// <c>TestLine_DoesHitSky</c> for a group: the
    /// fraction of each lane's ray that sees sky, with the 3D-skybox recursion.
    /// </summary>
    /// <param name="start">Four starts.</param>
    /// <param name="stop">Four far ends.</param>
    /// <param name="count">How many lanes are real; the rest copy the last.</param>
    /// <param name="rays">
    /// Where rays are recorded or replayed; like <see cref="Gather"/>, the
    /// collecting and replaying calls must come in the same order.
    /// </param>
    /// <param name="fractionVisible">Receives four fractions; 1 while collecting.</param>
    /// <param name="canRecurse">False inside the recursion itself.</param>
    public void TestLineDoesHitSky(
        ReadOnlySpan<Vec3> start,
        ReadOnlySpan<Vec3> stop,
        int count,
        LightRayLog rays,
        Span<float> fractionVisible,
        bool canRecurse = true)
    {
        ArgumentNullException.ThrowIfNull(rays);

        if (rays.Collecting)
        {
            EmitSkyTest(start, stop, count, rays, canRecurse);
            fractionVisible[..SampleGroup.Lanes].Fill(1.0f);
        }
        else
        {
            ResolveSkyTest(count, rays, fractionVisible, canRecurse);
        }
    }

    private bool Recursion(bool canRecurse) =>
        canRecurse && !Settings.NoSkyboxRecurse && SkyCameras.Cameras.Length > 0;

    /// <summary>
    /// The emit half of <c>TestLine_DoesHitSky</c>: the first rays, and which
    /// lanes may recurse into the skyboxes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitSkyTest(
        ReadOnlySpan<Vec3> start,
        ReadOnlySpan<Vec3> stop,
        int count,
        LightRayLog rays,
        bool canRecurse)
    {
        int primaryBase = rays.SkyCount;
        for (int lane = 0; lane < count; lane++)
        {
            rays.EmitSky(start[lane], stop[lane]);
        }

        rays.EndCall();

        if (!Recursion(canRecurse))
        {
            return;
        }

        // Stock asks lane ZERO's leaf for all four lanes
        // (StockQuirk.SkyboxRecursionFromLaneZero); correct asks each lane's own.
        int mask = 0;
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            mask |= LaneRecurses(_laneZeroRecursion ? start[0] : start[lane], rays.Tape) ? 1 << lane : 0;
        }

        rays.Tape.Int(mask);
        if (mask == 0)
        {
            return;
        }

        if (rays.DeferRecursion)
        {
            // Stock recurses only when the first rays were not ALL occluded
            // That needs their answers, so it is the second
            // stage's decision (EmitDeferredRecursion).
            rays.Defer(primaryBase, count, mask, start, stop);
            return;
        }

        // Recorded whether or not they will be used: the resolve reads them
        // and uses them only when the first rays were not all occluded.
        EmitRecursion(start, stop, count, rays);
    }

    // Per camera, the same four rays carried into the
    // skybox. One camera's rays are one packet of their own.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitRecursion(ReadOnlySpan<Vec3> start, ReadOnlySpan<Vec3> stop, int count, LightRayLog rays)
    {
        Span<Vec3> dir = stackalloc Vec3[SampleGroup.Lanes];
        for (int lane = 0; lane < count; lane++)
        {
            dir[lane] = StockSimd.NormaliseFour(stop[lane] - start[lane], _estimates);
        }

        foreach (SkyCamera camera in SkyCameras.Cameras)
        {
            for (int lane = 0; lane < count; lane++)
            {
                Vec3 skyStart = camera.Origin + (start[lane] * camera.WorldToSky);
                Vec3 skyStop = skyStart + (dir[lane] * LightConstants.MaxTraceLength);
                rays.EmitSky2(skyStart, skyStop);
            }

            rays.EndSky2Block();
        }
    }

    /// <summary>
    /// The second stage of <see cref="LightRayLog.DeferRecursion"/>: for every
    /// deferred sky test whose first rays were not all occluded, the skybox
    /// rays stock traces, camera by camera.
    /// </summary>
    /// <param name="rays">A log whose first stage has been traced into its own answers.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void EmitDeferredRecursion(LightRayLog rays)
    {
        ReadOnlySpan<DeferredSkyTest> tests = rays.Deferred;
        ReadOnlySpan<Vec3> points = rays.DeferredPoints;
        ReadOnlySpan<HitId> hits = rays.FirstStageSkyHits;
        Span<Vec3> start = stackalloc Vec3[SampleGroup.Lanes];
        Span<Vec3> stop = stackalloc Vec3[SampleGroup.Lanes];

        foreach (DeferredSkyTest test in tests)
        {
            // Padding lanes copy the last real one, so "all four" is
            // "every real lane".
            bool fullyOccluded = true;
            for (int lane = 0; lane < test.Count; lane++)
            {
                fullyOccluded &= LightRayLog.Occlusion(hits[test.PrimaryBase + lane]) >= 1.0f;
            }

            if (fullyOccluded)
            {
                continue;
            }

            for (int lane = 0; lane < test.Count; lane++)
            {
                start[lane] = points[test.PointBase + (2 * lane)];
                stop[lane] = points[test.PointBase + (2 * lane) + 1];
            }

            EmitRecursion(start, stop, test.Count, rays);
        }
    }

    /// <summary>The resolve half of <c>TestLine_DoesHitSky</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveSkyTest(int count, LightRayLog rays, Span<float> fractionVisible, bool canRecurse)
    {
        Span<float> occlusion = stackalloc float[SampleGroup.Lanes];
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            occlusion[lane] = lane < count ? rays.ReadSkyOcclusion() : occlusion[count - 1];
        }

        rays.SkipCallPadding();

        // Fully occluded means every lane, padding included -- and the
        // padding copies the last real lane, so that is every real lane.
        bool fullyOccluded = true;
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            fullyOccluded &= occlusion[lane] >= 1.0f;
        }

        if (Recursion(canRecurse))
        {
            int mask = rays.Tape.ReadInt();
            bool deferred = rays.DeferRecursion;

            // Deferred: the second stage traced these only when not fully
            // occluded. Otherwise they were recorded unconditionally.
            if (mask != 0 && (!deferred || !fullyOccluded))
            {
                Span<float> inner = stackalloc float[SampleGroup.Lanes];
                for (int camera = 0; camera < SkyCameras.Cameras.Length; camera++)
                {
                    for (int lane = 0; lane < SampleGroup.Lanes; lane++)
                    {
                        inner[lane] = lane < count ? rays.ReadSky2Occlusion() : inner[count - 1];
                    }

                    rays.SkipSky2BlockPadding();

                    if (fullyOccluded)
                    {
                        continue;
                    }

                    for (int lane = 0; lane < SampleGroup.Lanes; lane++)
                    {
                        // The inner test's fractionVisible.
                        float fraction = 1.0f - MinPs(MaxPs(inner[lane], 0.0f), 1.0f);
                        if ((mask & (1 << lane)) != 0)
                        {
                            occlusion[lane] = occlusion[lane] + 1.0f - fraction;
                        }
                    }
                }
            }
        }

        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            float o = MinPs(MaxPs(occlusion[lane], 0.0f), 1.0f);
            fractionVisible[lane] = 1.0f - o;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitSkyLight(DirectLight light, SampleGroup group, LightRayLog rays, GatherFlags flags)
    {
        GatherTape tape = rays.Tape;
        bool ignoreNormals = (flags & GatherFlags.IgnoreNormals) != 0;
        bool forceFast = (flags & GatherFlags.ForceFast) != 0;
        int normals = group.NormalCount;

        Span<float> dot = stackalloc float[SampleGroup.Lanes];
        bool allZero = true;
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            float d = ignoreNormals
                ? LightConstants.ConstantDot
                : -Vec3.Dot(group.Normal(0, lane), light.Normal);
            d = MaxPs(d, 0.0f);
            dot[lane] = d;
            allZero &= d == 0.0f;
        }

        if (allZero)
        {
            tape.Int(TapeSkyZero);
            tape.Int(normals);
            return;
        }

        int nsamples = 1;
        if (SunAngularExtent > 0.0f)
        {
            nsamples = LightConstants.SunAreaLightSamples;
            if (Settings.Fast || forceFast)
            {
                nsamples /= 4;
            }
        }

        tape.Int(TapeSky);
        tape.Int(normals);
        tape.Int(group.Count);
        tape.Int(nsamples);
        tape.Int(ignoreNormals ? 1 : 0);
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            tape.Float(dot[lane]);
            for (int n = 1; n < normals; n++)
            {
                tape.Float(ignoreNormals ? LightConstants.ConstantDot : -Vec3.Dot(group.Normal(n, lane), light.Normal));
            }
        }

        Span<Vec3> stop = stackalloc Vec3[SampleGroup.Lanes];

        // MAX_TRACE_LENGTH is a double macro, narrowed as VectorScale's
        // float argument; with jitter it multiplies a float extent in double.
        float jitterScale = (float)(1.732050807569 * (2 * 16384) * SunAngularExtent);

        for (int d = 0; d < nsamples; d++)
        {
            Vec3 delta = light.Normal * -LightConstants.MaxTraceLength;
            if (d != 0)
            {
                Vec3 ofs = _sunJitter[d - 1] * jitterScale;
                delta += ofs;
            }

            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                stop[lane] = delta + group.Points[lane];
            }

            // The sun traces every real lane: its amount is added to every
            // lane of a lit group, PVS or not.
            EmitSkyTest(group.Points, stop, group.Count, rays, canRecurse: true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveSkyLight(int normals, LightRayLog rays, GatherOutput output)
    {
        GatherTape tape = rays.Tape;
        int count = tape.ReadInt();
        int nsamples = tape.ReadInt();
        bool ignoreNormals = tape.ReadInt() != 0;

        Span<float> dot = stackalloc float[SampleGroup.Lanes];
        Span<float> bump = stackalloc float[SampleGroup.Lanes * BumpBasis.LightmapCount];
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            dot[lane] = tape.ReadFloat();
            for (int n = 1; n < normals; n++)
            {
                bump[(n * SampleGroup.Lanes) + lane] = tape.ReadFloat();
            }
        }

        Span<float> total = stackalloc float[SampleGroup.Lanes];
        Span<float> fraction = stackalloc float[SampleGroup.Lanes];
        total.Clear();
        for (int d = 0; d < nsamples; d++)
        {
            ResolveSkyTest(count, rays, fraction, canRecurse: true);
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                total[lane] += fraction[lane];
            }
        }

        // 1726-1741. `1.0f / nsamples` is a float divide.
        float inv = 1.0f / nsamples;
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            float see = total[lane] * inv;
            output.Dot[lane] = dot[lane] * see;
            output.Falloff[lane] = 1.0f;
            output.SunAmount[lane] = see * 10000.0f;

            for (int n = 1; n < normals; n++)
            {
                int k = (n * SampleGroup.Lanes) + lane;
                output.Dot[k] = ignoreNormals ? LightConstants.ConstantDot : bump[k] * see;
            }
        }
    }

    /// <summary>
    /// <c>GatherSampleSkyAmbientLightSSE</c>, both
    /// halves in one body so the per-direction arithmetic has one spelling:
    /// the emit walks the directions for their rays, the resolve walks them
    /// again for the sums, reading the normals and switches back off the tape.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void AmbientSky(
        bool emit,
        ReadOnlySpan<Vec3> groupNormals,
        ReadOnlySpan<Vec3> points,
        int count,
        int normals,
        int laneMask,
        GatherFlags flags,
        float epsilon,
        LightRayLog rays,
        GatherOutput output)
    {
        const int L = SampleGroup.Lanes;
        GatherTape tape = rays.Tape;

        // With a 3D skybox the lanes are coupled through the recursion, so
        // every lane is traced; otherwise only lanes whose answer can count.
        bool traceAll;
        bool ignoreNormals;
        bool fastSet;
        Span<Vec3> normalStore = stackalloc Vec3[L * BumpBasis.LightmapCount];
        scoped ReadOnlySpan<Vec3> groupNormalsRead;
        if (emit)
        {
            ignoreNormals = (flags & GatherFlags.IgnoreNormals) != 0;
            fastSet = Settings.Fast || (flags & GatherFlags.ForceFast) != 0;
            traceAll = SkyCameras.Cameras.Length > 0 && !Settings.NoSkyboxRecurse;
            tape.Int(TapeAmbient);
            tape.Int(normals);
            tape.Int(count);
            tape.Int(laneMask);
            tape.Int((ignoreNormals ? 1 : 0) | (fastSet ? 2 : 0) | (traceAll ? 4 : 0));
            for (int i = 0; i < L * normals; i++)
            {
                tape.Vector(groupNormals[i]);
            }

            groupNormalsRead = groupNormals;
        }
        else
        {
            count = tape.ReadInt();
            laneMask = tape.ReadInt();
            int bits = tape.ReadInt();
            ignoreNormals = (bits & 1) != 0;
            fastSet = (bits & 2) != 0;
            traceAll = (bits & 4) != 0;
            for (int i = 0; i < L * normals; i++)
            {
                normalStore[i] = tape.ReadVector();
            }

            groupNormalsRead = normalStore;
        }

        Span<float> sumdot = stackalloc float[L];
        Span<float> ambient = stackalloc float[L * BumpBasis.LightmapCount];
        Span<float> possibleHits = stackalloc float[L * BumpBasis.LightmapCount];
        Span<float> dots = stackalloc float[L * BumpBasis.LightmapCount];
        Span<bool> valid = stackalloc bool[L];
        Span<bool> traceLane = stackalloc bool[L];
        Span<Vec3> surfacePos = stackalloc Vec3[L];
        Span<Vec3> stop = stackalloc Vec3[L];
        Span<float> fraction = stackalloc float[L];
        sumdot.Clear();
        ambient.Clear();
        possibleHits.Clear();

        ReadOnlySpan<Vec3> directions = fastSet ? _ambientDirectionsFast : _ambientDirections;

        foreach (Vec3 anorm in directions)
        {
            bool anyValid = false;
            for (int lane = 0; lane < L; lane++)
            {
                float d = ignoreNormals
                    ? LightConstants.ConstantDot
                    : -Vec3.Dot(groupNormalsRead[lane], anorm);
                valid[lane] = d > LightConstants.EqualEpsilon;
                anyValid |= valid[lane];
                dots[lane] = valid[lane] ? d : 0.0f;
            }

            if (!anyValid)
            {
                continue;
            }

            for (int lane = 0; lane < L; lane++)
            {
                sumdot[lane] = dots[lane] + sumdot[lane];
                possibleHits[lane] = (valid[lane] ? 1.0f : 0.0f) + possibleHits[lane];
                traceLane[lane] = valid[lane];

                for (int n = 1; n < normals; n++)
                {
                    int k = (n * L) + lane;
                    float d = ignoreNormals
                        ? LightConstants.ConstantDot
                        : -Vec3.Dot(groupNormalsRead[k], anorm);
                    bool valid2 = d > LightConstants.EqualEpsilon;
                    dots[k] = valid2 ? d : 0.0f;
                    possibleHits[k] = (valid[lane] && valid2 ? 1.0f : 0.0f) + possibleHits[k];

                    // The gather masks a bump dot by ITS OWN validity only, and
                    // accumulates it with the lane's real visibility --
                    // so a direction behind the flat normal but in front of a
                    // bump normal still needs its ray.
                    traceLane[lane] |= valid2;
                }

                if (emit)
                {
                    // The ray ENDS at pos - anorm * MAX_TRACE_LENGTH
                    // but STARTS epsilon off the surface: surfacePos -= (anorm * -eps).
                    stop[lane] = (anorm * -LightConstants.MaxTraceLength) + points[lane];
                    surfacePos[lane] = points[lane] - (anorm * -epsilon);
                }
            }

            if (emit)
            {
                EmitAmbientRays(surfacePos, stop, count, laneMask, traceLane, traceAll, rays);
                continue;
            }

            ResolveAmbientRays(count, laneMask, traceLane, traceAll, rays, fraction);
            for (int n = 0; n < normals; n++)
            {
                for (int lane = 0; lane < L; lane++)
                {
                    int k = (n * L) + lane;
                    ambient[k] = ambient[k] + (fraction[lane] * dots[k]);
                }
            }
        }

        if (emit)
        {
            return;
        }

        for (int lane = 0; lane < L; lane++)
        {
            output.Falloff[lane] = 1.0f;
            for (int n = 0; n < normals; n++)
            {
                int k = (n * L) + lane;
                // Stock: two ReciprocalSIMD estimates; correct: the divides
                // they stand for, ambient * possible0 / (possible_k * sumdot).
                if (_estimates)
                {
                    float factor = StockSimd.Reciprocal(possibleHits[lane], true) * possibleHits[k];
                    float d = factor * sumdot[lane];
                    d = StockSimd.Reciprocal(d, true);
                    output.Dot[k] = ambient[k] * d;
                }
                else
                {
                    float factor = possibleHits[k] / possibleHits[lane];
                    output.Dot[k] = ambient[k] / (factor * sumdot[lane]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitAmbientRays(
        ReadOnlySpan<Vec3> start,
        ReadOnlySpan<Vec3> stop,
        int count,
        int laneMask,
        ReadOnlySpan<bool> contributes,
        bool traceAll,
        LightRayLog rays)
    {
        if (traceAll)
        {
            EmitSkyTest(start, stop, count, rays, canRecurse: true);
            return;
        }

        // No recursion possible, so each lane is independent and a lane whose
        // dot is zero, or which is outside the light's PVS, cannot change the
        // result: it is answered "visible" without a ray.
        for (int lane = 0; lane < count; lane++)
        {
            if (contributes[lane] && (laneMask & (1 << lane)) != 0)
            {
                rays.EmitSky(start[lane], stop[lane]);
            }
        }

        rays.EndCall();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveAmbientRays(
        int count,
        int laneMask,
        ReadOnlySpan<bool> contributes,
        bool traceAll,
        LightRayLog rays,
        Span<float> fraction)
    {
        if (traceAll)
        {
            ResolveSkyTest(count, rays, fraction, canRecurse: true);
            return;
        }

        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            if (lane >= count)
            {
                fraction[lane] = fraction[count - 1];
                continue;
            }

            bool needed = contributes[lane] && (laneMask & (1 << lane)) != 0;
            fraction[lane] = needed ? 1.0f - rays.ReadSkyOcclusion() : 1.0f;
        }

        rays.SkipCallPadding();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitStandardLight(
        DirectLight light,
        SampleGroup group,
        ReadOnlySpan<bool> laneNeeded,
        LightRayLog rays,
        GatherOutput scratch,
        GatherFlags flags)
    {
        bool ignoreNormals = (flags & GatherFlags.IgnoreNormals) != 0;
        bool hardFalloff = light.EndFadeDistance > light.StartFadeDistance;
        int normals = group.NormalCount;
        int count = group.Count;
        scratch.Clear();

        int traced = 0;
        if (light.Type == EmitType.Point && !_estimates && !ScalarStandardLights)
        {
            traced = GatherPointFour(light, group, laneNeeded, hardFalloff, ignoreNormals, rays, scratch);
        }
        else
        {
            for (int lane = 0; lane < count; lane++)
            {
                if (GatherStandardLane(
                    light, group, lane, laneNeeded.IsEmpty || laneNeeded[lane], hardFalloff,
                    ignoreNormals, rays, scratch))
                {
                    traced |= 1 << lane;
                }
            }
        }

        rays.EndCall();
        GatherTape tape = rays.Tape;
        tape.Int(TapeStandard);
        tape.Int(normals);
        tape.Int(count);
        tape.Int(traced);
        for (int lane = 0; lane < count; lane++)
        {
            tape.Float(scratch.Falloff[lane]);
            for (int n = 0; n < normals; n++)
            {
                tape.Float(scratch.Dot[(n * SampleGroup.Lanes) + lane]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ResolveStandardLight(int normals, LightRayLog rays, GatherOutput output)
    {
        GatherTape tape = rays.Tape;
        int count = tape.ReadInt();
        int traced = tape.ReadInt();
        for (int lane = 0; lane < count; lane++)
        {
            output.Falloff[lane] = tape.ReadFloat();
            for (int n = 0; n < normals; n++)
            {
                output.Dot[(n * SampleGroup.Lanes) + lane] = tape.ReadFloat();
            }

            // Visibility multiplies the dot and nothing else.
            if ((traced & (1 << lane)) != 0)
            {
                output.Dot[lane] = rays.ReadVisibility() * output.Dot[lane];
            }
        }

        rays.SkipCallPadding();

        // Padding: the last real lane's answer.
        int last = count - 1;
        for (int lane = count; lane < SampleGroup.Lanes; lane++)
        {
            for (int n = 0; n < normals; n++)
            {
                output.Dot[(n * SampleGroup.Lanes) + lane] = output.Dot[(n * SampleGroup.Lanes) + last];
            }

            output.Falloff[lane] = output.Falloff[last];
        }
    }

    /// <summary>
    /// One lane of <c>GatherSampleStandardLightSSE</c>
    /// up to its visibility ray: records the ray when the lane needs one and
    /// leaves the unoccluded dot in <paramref name="output"/>.
    /// </summary>
    /// <returns>True when a visibility ray was recorded for the lane.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool GatherStandardLane(
        DirectLight light,
        SampleGroup group,
        int lane,
        bool needed,
        bool hardFalloff,
        bool ignoreNormals,
        LightRayLog rays,
        GatherOutput output)
    {
        Vec3 pos = group.Points[lane];

        // Every light made by AllocDLight has facenum
        // -1, surface lights included, so src is always the light's origin.
        Vec3 src = light.FaceNum == -1 ? light.Origin : Vec3.Zero;

        // Stock: rsqrt ESTIMATE then multiply; here exact.
        Vec3 delta = src - pos;
        float dist2 = delta.LengthSquared();
        float rpcDist = StockSimd.ReciprocalSqrt(dist2, _estimates);
        delta *= rpcDist;
        float dist = MathF.Sqrt(dist2);

        float dot = ignoreNormals ? LightConstants.ConstantDot : Vec3.Dot(delta, group.Normal(0, lane));
        dot = MaxPs(0.0f, dot);

        // Past the hard fade distance the dot is zero.
        if (hardFalloff && !(dist <= light.EndFadeDistance))
        {
            return false;
        }

        dist = MaxPs(dist, 1.0f);
        float falloffEvalDist = MinPs(dist, light.CapDist);
        float falloff;

        switch (light.Type)
        {
            case EmitType.Point:
                falloff = InverseQuadratic(light, falloffEvalDist);
                break;

            case EmitType.Surface:
            {
                float dot2 = -Vec3.Dot(delta, light.Normal);
                dot2 = MaxPs(0.0f, dot2);
                if (dot == 0.0f)
                {
                    return false;
                }

                falloff = Divide(dot2, dist2);

                // Push the ray's end off the emitting surface.
                src += light.Normal * LightConstants.DistEpsilon;
                break;
            }

            case EmitType.Spotlight:
            {
                float dot2 = -Vec3.Dot(delta, light.Normal);
                if (!(dot2 > light.StopDot2))
                {
                    return false;
                }

                falloff = InverseQuadratic(light, falloffEvalDist) * dot2;

                bool inFringe = dot2 <= light.StopDot;
                float mult = Divide(dot2 - light.StopDot2, light.StopDot - light.StopDot2);
                mult = MinPs(mult, 1.0f);
                mult = MaxPs(mult, 0.0f);

                // Stock's PowSIMD is fixed point ("isn't the most
                // accurate, but it doesn't need to be"); exact pow here.
                if (light.Exponent != 0.0f && light.Exponent != 1.0f)
                {
                    mult = _quarterPow ? StockSimd.FixedPointPow(mult, light.Exponent) : MathF.Pow(mult, light.Exponent);
                }

                mult = inFringe ? mult : 1.0f;
                falloff = mult * falloff;
                break;
            }

            default:
                return false;
        }

        // The fade curve, QuinticInterpolatingPolynomial, in the
        // order the SSE spells it.
        if (hardFalloff)
        {
            float t = Divide(dist - light.StartFadeDistance, light.EndFadeDistance - light.StartFadeDistance);
            t = MinPs(t, 1.0f);
            t = MaxPs(t, 0.0f);
            t = 1.0f - t;
            float mult = (6.0f * t) - 15.0f;
            mult = (mult * t) + 10.0f;
            mult = (t * t) * mult;
            mult = t * mult;
            falloff = mult * falloff;
        }

        // Only a lane whose contribution could be nonzero is
        // worth a ray: visibility multiplies the dot and nothing else.
        bool trace = needed && dot != 0.0f && falloff != 0.0f;
        if (trace)
        {
            rays.EmitVisibility(pos, src);
        }

        output.Dot[lane] = dot;
        output.Falloff[lane] = falloff;

        for (int n = 1; n < group.NormalCount; n++)
        {
            output.Dot[(n * SampleGroup.Lanes) + lane] = ignoreNormals
                ? LightConstants.ConstantDot
                : MaxPs(0.0f, Vec3.Dot(group.Normal(n, lane), delta));
        }

        return trace;
    }

    /// <summary>
    /// <see cref="GatherStandardLane"/> for a POINT light with exact
    /// arithmetic, all four lanes at once.
    /// </summary>
    /// <remarks>
    /// Every operation is the scalar lane's, element-wise and in the same
    /// order -- IEEE single, no fused multiply-add, <c>maxps</c>/<c>minps</c>
    /// as selects on the same comparison -- so each lane's bits are the scalar
    /// path's; a lane that the scalar code returns from early is masked to the
    /// zeros it would have left. The rays are recorded in lane order, as the
    /// scalar loop records them.
    /// </remarks>
    /// <returns>The traced-lane mask.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int GatherPointFour(
        DirectLight light,
        SampleGroup group,
        ReadOnlySpan<bool> laneNeeded,
        bool hardFalloff,
        bool ignoreNormals,
        LightRayLog rays,
        GatherOutput output)
    {
        const int L = SampleGroup.Lanes;
        Vec3[] p = group.Points;
        Vec3[] nrm = group.Normals;
        Vector128<float> zero = Vector128<float>.Zero;
        Vector128<float> one = Vector128.Create(1.0f);

        Vec3 src = light.FaceNum == -1 ? light.Origin : Vec3.Zero;

        Vector128<float> dx = Vector128.Create(src.X) - Vector128.Create(p[0].X, p[1].X, p[2].X, p[3].X);
        Vector128<float> dy = Vector128.Create(src.Y) - Vector128.Create(p[0].Y, p[1].Y, p[2].Y, p[3].Y);
        Vector128<float> dz = Vector128.Create(src.Z) - Vector128.Create(p[0].Z, p[1].Z, p[2].Z, p[3].Z);
        Vector128<float> dist2 = ((dx * dx) + (dy * dy)) + (dz * dz);
        Vector128<float> dist = Vector128.Sqrt(dist2);
        Vector128<float> rpcDist = one / dist;
        dx *= rpcDist;
        dy *= rpcDist;
        dz *= rpcDist;

        Vector128<float> dot = ignoreNormals
            ? Vector128.Create(LightConstants.ConstantDot)
            : ((dx * Vector128.Create(nrm[0].X, nrm[1].X, nrm[2].X, nrm[3].X))
               + (dy * Vector128.Create(nrm[0].Y, nrm[1].Y, nrm[2].Y, nrm[3].Y)))
              + (dz * Vector128.Create(nrm[0].Z, nrm[1].Z, nrm[2].Z, nrm[3].Z));
        dot = MaxPs4(zero, dot);

        // A lane past the hard fade distance keeps its zeros.
        Vector128<float> live = hardFalloff
            ? Vector128.LessThanOrEqual(dist, Vector128.Create(light.EndFadeDistance))
            : Vector128<float>.AllBitsSet;

        dist = MaxPs4(dist, one);
        Vector128<float> d = MinPs4(dist, Vector128.Create(light.CapDist));

        // InverseQuadratic.
        Vector128<float> f = d * d;
        f *= Vector128.Create(light.QuadraticAttn);
        f += Vector128.Create(light.LinearAttn) * d;
        f += Vector128.Create(light.ConstantAttn);
        Vector128<float> falloff = one / f;

        if (hardFalloff)
        {
            Vector128<float> t = (dist - Vector128.Create(light.StartFadeDistance))
                / Vector128.Create(light.EndFadeDistance - light.StartFadeDistance);
            t = MinPs4(t, one);
            t = MaxPs4(t, zero);
            t = one - t;
            Vector128<float> mult = (Vector128.Create(6.0f) * t) - Vector128.Create(15.0f);
            mult = (mult * t) + Vector128.Create(10.0f);
            mult = (t * t) * mult;
            mult = t * mult;
            falloff = mult * falloff;
        }

        dot = Vector128.ConditionalSelect(live, dot, zero);
        falloff = Vector128.ConditionalSelect(live, falloff, zero);

        int traced = 0;
        int count = group.Count;
        for (int lane = 0; lane < count; lane++)
        {
            float dl = dot.GetElement(lane);
            float fl = falloff.GetElement(lane);
            bool needed = laneNeeded.IsEmpty || laneNeeded[lane];

            if (needed && dl != 0.0f && fl != 0.0f)
            {
                rays.EmitVisibility(p[lane], src);
                traced |= 1 << lane;
            }

            output.Dot[lane] = dl;
            output.Falloff[lane] = fl;
        }

        for (int n = 1; n < group.NormalCount; n++)
        {
            Vector128<float> bump;
            if (ignoreNormals)
            {
                bump = Vector128.Create(LightConstants.ConstantDot);
            }
            else
            {
                int b = n * L;
                bump = ((Vector128.Create(nrm[b].X, nrm[b + 1].X, nrm[b + 2].X, nrm[b + 3].X) * dx)
                        + (Vector128.Create(nrm[b].Y, nrm[b + 1].Y, nrm[b + 2].Y, nrm[b + 3].Y) * dy))
                       + (Vector128.Create(nrm[b].Z, nrm[b + 1].Z, nrm[b + 2].Z, nrm[b + 3].Z) * dz);
                bump = MaxPs4(zero, bump);
            }

            bump = Vector128.ConditionalSelect(live, bump, zero);
            for (int lane = 0; lane < count; lane++)
            {
                output.Dot[(n * L) + lane] = bump.GetElement(lane);
            }
        }

        return traced;
    }

    // maxps / minps: the second operand unless the first is strictly
    // greater / less, NaN included -- the scalar MaxPs / MinPs, four wide.
    private static Vector128<float> MaxPs4(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);

    private static Vector128<float> MinPs4(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);

    private float InverseQuadratic(DirectLight light, float d)
    {
        // ((d*d)*q + l*d) + c, then its reciprocal.
        float f = d * d;
        f *= light.QuadraticAttn;
        f += light.LinearAttn * d;
        f += light.ConstantAttn;
        return StockSimd.Reciprocal(f, _estimates);
    }

    /// <summary>
    /// <c>MulSIMD(ReciprocalSIMD(den), num)</c> as stock computes it, or the
    /// division it stands for.
    /// </summary>
    private float Divide(float num, float den) =>
        _estimates ? StockSimd.Reciprocal(den, true) * num : num / den;

    // The leaf's area has no sky camera of its own. A
    // pure function of the point, so the tape remembers the last few: an
    // ambient gather asks it for the same four points in all 162 directions.
    private bool LaneRecurses(Vec3 start, GatherTape cache)
    {
        if (cache.TryRecursion(start, out bool known))
        {
            return known;
        }

        bool result = LaneRecurses(start);
        cache.StoreRecursion(start, result);
        return result;
    }

    private bool LaneRecurses(Vec3 start)
    {
        int leafIndex = Tree.LeafFromPoint(start);
        if (leafIndex < 0 || leafIndex >= Leaves.Length)
        {
            return false;
        }

        int area = Leaves[leafIndex].Area;
        return area >= 0 && area < SkyCameras.AreaCount && SkyCameras.CameraInArea(area) < 0;
    }

    private static Vec3[] SamplerPrefix(int count)
    {
        Vec3[] values = new Vec3[Math.Max(count, 0)];
        DirectionalSampler sampler = new();
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = sampler.NextValue();
        }

        return values;
    }
}
