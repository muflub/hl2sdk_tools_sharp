//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>TestLine</c> as leaf ambient uses it: how much of
/// the segment from a sample to a surface light is unobstructed.
/// </summary>
/// <remarks>
/// A seam rather than a type because the answer comes from <c>g_RtEnv</c> --
/// brushes, displacements and static props in the KD-tree -- which the direct
/// lighting lane owns. Implementations must be safe to call from many workers
/// at once and must not depend on which worker calls.
/// </remarks>
public interface IAmbientLightVisibility
{
    /// <summary>
    /// For each end, the fraction of the segment from <paramref name="start"/>
    /// that is unobstructed: 0 or 1, or between with texture shadows.
    /// </summary>
    /// <param name="start">The sample.</param>
    /// <param name="ends">The lights.</param>
    /// <param name="fractions">Receives one fraction per end.</param>
    /// <remarks>
    /// A batch because a sample tests every baked light at once; each segment's
    /// answer must not depend on the others in the batch.
    /// </remarks>
    void FractionsVisible(Vec3 start, ReadOnlySpan<Vec3> ends, Span<float> fractions);

    /// <summary>
    /// <see cref="FractionsVisible(Vec3, ReadOnlySpan{Vec3}, Span{float})"/>
    /// for several samples at once: every start against every end.
    /// </summary>
    /// <param name="starts">The samples.</param>
    /// <param name="ends">The lights.</param>
    /// <param name="fractions">
    /// Receives <c>starts.Length * ends.Length</c> fractions, sample-major:
    /// the fraction for start <c>s</c> and end <c>e</c> is at
    /// <c>s * ends.Length + e</c>.
    /// </param>
    /// <remarks>
    /// The whole leaf in one call, so a tracer that wants large batches (a
    /// GPU) sees a leaf's samples together. The default asks the one-start
    /// overload once per start, which is what an implementation that has no
    /// use for the larger batch wants; each answer must be the one-start
    /// answer either way.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="fractions"/> is too short.</exception>
    void FractionsVisible(ReadOnlySpan<Vec3> starts, ReadOnlySpan<Vec3> ends, Span<float> fractions)
    {
        if (fractions.Length < starts.Length * ends.Length)
        {
            throw new ArgumentException("one fraction per start and end", nameof(fractions));
        }

        for (int s = 0; s < starts.Length; s++)
        {
            FractionsVisible(starts[s], ends, fractions.Slice(s * ends.Length, ends.Length));
        }
    }
}

/// <summary>
/// One work item's ambient-cube computer: <c>ComputeAmbientFromSphericalSamples</c>
/// With every buffer it needs owned here.
/// </summary>
/// <remarks>
/// <para>
/// ONE PER WORK ITEM, never shared between workers: it owns the 162-colour
/// scratch and the <see cref="DispTestedScratch"/> that replaces stock's
/// per-thread <c>s_DispTested[iThread]</c>. Nothing it holds survives from one
/// sample to the next except buffers that every sample fully overwrites, so
/// reusing one instance across a leaf's samples (or across leaves) cannot
/// change an answer.
/// </para>
/// </remarks>
public sealed class AmbientSampler
{
    private readonly AmbientScene _scene;
    private readonly DWorldLight[] _lights;
    private readonly IAmbientLightVisibility? _visibility;
    private readonly ComplianceOptions _compliance;

    /// <summary>
    /// <see cref="StockQuirk.AmbientCubeReciprocalEstimate"/>, as
    /// <see cref="AmbientCube.AddEmitSurfaceLights"/> decides it from
    /// <see cref="_compliance"/>: which arithmetic says whether a pair needs its line.
    /// </summary>
    private readonly bool _estimate;
    private readonly Vec3? _skyAmbient;
    private readonly float _tanTheta;
    private readonly Vec3[] _radColor = new Vec3[VertexNormals.Count];
    private readonly Vec3[] _styleColors = new Vec3[RayAmbientLighting.MaxLightStyles];

    /// <summary>Makes a sampler.</summary>
    /// <param name="scene">The map.</param>
    /// <param name="lights">The classified world lights (not copied; read only).</param>
    /// <param name="visibility">The surface-light visibility, or null.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AmbientSampler(
        AmbientScene scene,
        DWorldLight[] lights,
        IAmbientLightVisibility? visibility,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(compliance);
        _scene = scene;
        _lights = lights;
        _visibility = visibility;
        _compliance = compliance;
        _estimate = compliance.Emulates(StockQuirk.AmbientCubeReciprocalEstimate);
        _skyAmbient = RayAmbientLighting.FindSkyAmbient(scene);

        // Tan(DEG2RAD(7.275)), all float.
        _tanTheta = DetMathF.Tan(VertexNormals.ConeInnerAngleRadians);
        Displacements = scene.Tracer.Displacements.CreateScratch();

        // The flagged set is fixed once the pass has classified its lights.
        List<int> flagged = [];
        for (int i = 0; i < lights.Length; i++)
        {
            if ((lights[i].Flags & (int)WorldLightFlags.InAmbientCube) != 0)
            {
                flagged.Add(i);
            }
        }

        _flagged = [.. flagged];
        _flaggedOrigins = new Vec3[_flagged.Length];
        for (int i = 0; i < _flagged.Length; i++)
        {
            _flaggedOrigins[i] = lights[_flagged[i]].Origin;
        }

        _fractions = new float[_flagged.Length];
        _sampleFractions = new float[_flagged.Length];
    }

    private readonly int[] _flagged;
    private readonly Vec3[] _flaggedOrigins;
    private readonly float[] _sampleFractions;
    private float[] _fractions;

    /// <summary>This work item's displacement scratch.</summary>
    public DispTestedScratch Displacements { get; }

    /// <summary>
    /// Builds one ambient cube at a point
    /// (<c>ComputeAmbientFromSphericalSamples</c>).
    /// </summary>
    /// <param name="start">Where to sample.</param>
    /// <param name="cube">Receives the six colours.</param>
    /// <remarks>
    /// ONLY LIGHTSTYLE 0: stock clears element 0 of a 64-wide per-ray array,
    /// fires the ray into it, and reads element 0. The other
    /// styles are written by faces with switchable styles and never read.
    /// </remarks>
    public void ComputeCube(Vec3 start, Span<Vec3> cube)
    {
        ComputeRayCube(start, cube);
        AddSurfaceLights([start], cube);
    }

    /// <summary>How many world lights are baked into the cubes, each a segment per sample.</summary>
    public int SurfaceLightCount => _flagged.Length;

    /// <summary>
    /// The first half of <see cref="ComputeCube"/>: the 162 rays into the
    /// map, projected onto the cube.
    /// </summary>
    /// <param name="start">Where to sample.</param>
    /// <param name="cube">Receives the six colours.</param>
    public void ComputeRayCube(Vec3 start, Span<Vec3> cube)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cube.Length, AmbientCube.Sides);

        ReadOnlySpan<Vec3> anorms = VertexNormals.All;
        Span<Vec3> styles = _styleColors;
        for (int i = 0; i < VertexNormals.Count; i++)
        {
            Vec3 end = start + (anorms[i] * AmbientCube.RayLength);

            styles[0] = Vec3.Zero;
            RayAmbientLighting.Accumulate(_scene, start, end, _tanTheta, _skyAmbient, styles, Displacements);
            _radColor[i] = styles[0];
        }

        AmbientCube.Project(_radColor, cube);
    }

    /// <summary>
    /// The second half of <see cref="ComputeCube"/> for several samples: the
    /// baked surface lights each sample sees, added to its cube.
    /// </summary>
    /// <param name="starts">The samples.</param>
    /// <param name="cubes">
    /// Their cubes from <see cref="ComputeRayCube"/>, six colours per sample
    /// in sample order.
    /// </param>
    /// <remarks>
    /// Every sample's segments to every light go to the visibility as ONE
    /// call, so a leaf's samples reach the tracer as one batch. Each sample's
    /// cube then gets exactly what the one-sample call gave it: the segments
    /// are answered one by one, and the lights are added in the same order.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="cubes"/> holds fewer than six colours per start.</exception>
    public void AddSurfaceLights(ReadOnlySpan<Vec3> starts, Span<Vec3> cubes)
    {
        if (cubes.Length < starts.Length * AmbientCube.Sides)
        {
            throw new ArgumentException("six colours per start", nameof(cubes));
        }

        int lights = _flagged.Length;
        if (lights == 0 || starts.IsEmpty)
        {
            return;
        }

        int count = starts.Length * lights;
        if (_fractions.Length < count)
        {
            _fractions = new float[count];
        }

        Span<float> fractions = _fractions.AsSpan(0, count);
        if (_visibility is null)
        {
            fractions.Fill(1.0f);
        }
        else
        {
            _visibility.FractionsVisible(starts, _flaggedOrigins, fractions);
        }

        ApplySurfaceLights(starts, cubes, fractions);
    }

    /// <summary>
    /// <see cref="AddSurfaceLights"/> in two halves around a trace: the
    /// segments from every sample to every baked light whose visibility can
    /// matter, sample-major, added to a batch.
    /// </summary>
    /// <param name="starts">The samples.</param>
    /// <param name="batch">The worker's batch.</param>
    /// <param name="stockReciprocal">Whether the rays are normalised as stock does (<see cref="TracerLineVisibility.StockReciprocal"/>).</param>
    /// <returns>The batch index of the first segment.</returns>
    /// <remarks>
    /// A pair whose light adds nothing whatever the line's answer
    /// (<see cref="AmbientCube.VisibilityMatters"/>: the sample is behind the
    /// emitter or out of its radius) gets no segment. Stock traces those too;
    /// the answer is thrown away, so leaving them out changes nothing but the
    /// work. <see cref="ResolveSurfaceLights"/> asks the same question of the
    /// same inputs to know which pairs have a segment, so the two halves need
    /// no record between them.
    /// </remarks>
    internal int PlanSurfaceLights(ReadOnlySpan<Vec3> starts, TestLineBatch batch, bool stockReciprocal)
    {
        int first = batch.Count;
        RayTraceOptions options = RayTraceOptions.TestLine();
        for (int s = 0; s < starts.Length; s++)
        {
            for (int e = 0; e < _flaggedOrigins.Length; e++)
            {
                if (AmbientCube.VisibilityMatters(in _lights[_flagged[e]], starts[s], _estimate))
                {
                    batch.Add(Ray.Segment(starts[s], _flaggedOrigins[e], stockReciprocal), options);
                }
            }
        }

        return first;
    }

    /// <summary>
    /// The second half of <see cref="PlanSurfaceLights"/>: each sample's
    /// fractions read from the traced batch and its lights added to its cube,
    /// exactly as <see cref="AddSurfaceLights"/> adds them.
    /// </summary>
    /// <param name="starts">The samples, as planned.</param>
    /// <param name="cubes">Their ray cubes, six colours per sample.</param>
    /// <param name="batch">The traced batch.</param>
    /// <param name="first">What <see cref="PlanSurfaceLights"/> returned.</param>
    /// <remarks>
    /// ONE SAMPLE'S FRACTIONS AT A TIME, into a buffer of one fraction per
    /// light made with the sampler. Adding a sample's lights reads only its
    /// own fractions, so filling them just before they are added gives every
    /// cube exactly what the whole-leaf array gave it -- and the buffer never
    /// grows. The whole-leaf array it replaces grew with the largest leaf
    /// (128 samples times every baked light) on every worker, which a
    /// profile of vrad on ctf_2fort showed on the large-object heap.
    /// </remarks>
    internal void ResolveSurfaceLights(ReadOnlySpan<Vec3> starts, Span<Vec3> cubes, TestLineBatch batch, int first)
    {
        int lights = _flagged.Length;
        if (lights == 0 || starts.IsEmpty)
        {
            return;
        }

        Span<float> fractions = _sampleFractions;
        int at = first;
        for (int s = 0; s < starts.Length; s++)
        {
            for (int e = 0; e < lights; e++)
            {
                // A pair the plan gave no segment adds nothing either way;
                // 0 is as good an answer as 1 for it.
                fractions[e] = AmbientCube.VisibilityMatters(in _lights[_flagged[e]], starts[s], _estimate)
                    && !batch.IsBlocked(at++) ? 1.0f : 0.0f;
            }

            AmbientCube.AddEmitSurfaceLights(
                _lights,
                _flagged,
                fractions,
                starts[s],
                cubes.Slice(s * AmbientCube.Sides, AmbientCube.Sides),
                _compliance);
        }
    }

    private void ApplySurfaceLights(ReadOnlySpan<Vec3> starts, Span<Vec3> cubes, ReadOnlySpan<float> fractions)
    {
        int lights = _flagged.Length;
        for (int s = 0; s < starts.Length; s++)
        {
            AmbientCube.AddEmitSurfaceLights(
                _lights,
                _flagged,
                fractions.Slice(s * lights, lights),
                starts[s],
                cubes.Slice(s * AmbientCube.Sides, AmbientCube.Sides),
                _compliance);
        }
    }
}
