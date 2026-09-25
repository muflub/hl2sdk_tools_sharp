using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>How many triangles one caster source contributed, and where they are.</summary>
/// <param name="Triangles">The count.</param>
/// <param name="Min">The lower corner of their bounding box.</param>
/// <param name="Max">The upper corner.</param>
/// <remarks>
/// Counts alone are a weak gate: a load path that emitted the right NUMBER of
/// triangles in the wrong place, or scaled, or untransformed, passes it. The
/// bounds cost six floats and close most of that -- a prop set that forgot the
/// per-prop origin collapses to the models' own hull bounds around zero, which
/// no map's bounds resemble.
/// </remarks>
public readonly record struct ShadowCasterStats(int Triangles, Vec3 Min, Vec3 Max)
{
    /// <summary>A source that contributed nothing.</summary>
    /// <remarks>
    /// The bounds are an INVERTED box -- positive infinity to negative
    /// infinity -- rather than zero. A zero box is a real, tiny box at the
    /// origin, and the union of it with a real one moves the answer; the
    /// inverted one is the identity for union, which is what stock's
    /// <c>CalculateTriangleListBounds</c> sentinel (<c>raytrace.cpp:610</c>)
    /// is too.
    /// </remarks>
    public static ShadowCasterStats Empty => new(
        0,
        new Vec3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
        new Vec3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity));

    /// <summary>Whether this source contributed any triangles.</summary>
    public bool IsEmpty => Triangles == 0;
}

/// <summary>
/// Everything vrad's load path puts in front of the ray tracer.
/// </summary>
/// <remarks>
/// <para>
/// One immutable object rather than a global, which is the whole difference
/// from stock: <c>g_RtEnv</c> (<c>vrad.cpp:126</c>) is a file-scope
/// <c>RayTracingEnvironment</c> that eleven translation units reach into, and
/// the reason vrad cannot run two maps in one process is that emptying it is
/// not an operation anybody wrote.
/// </para>
/// <para>
/// Coverage and material index live in parallel arrays here rather than inside
/// <see cref="TracedTriangle"/>, exactly as stock keeps <c>TriangleColors</c>
/// and <c>TriangleMaterials</c> beside <c>OptimizedTriangleList</c>. The
/// tracer never reads either: they are consumed by the transparency callback
/// on the CPU side, which is the seam §10c draws so that a GPU backend answers
/// bits and nothing else.
/// </para>
/// </remarks>
public sealed class ShadowCasterSet
{
    private readonly TracedTriangle[] _triangles;
    private readonly float[] _coverage;
    private readonly int[] _materials;
    private readonly ShadowCasterStats[] _stats;

    internal ShadowCasterSet(
        TracedTriangle[] triangles,
        float[] coverage,
        int[] materials,
        ShadowCasterStats[] stats)
    {
        _triangles = triangles;
        _coverage = coverage;
        _materials = materials;
        _stats = stats;
    }

    /// <summary>How many caster triangles the map has.</summary>
    public int Count => _triangles.Length;

    /// <summary>The triangles, in the order vrad added them.</summary>
    public ReadOnlySpan<TracedTriangle> Triangles => _triangles;

    /// <summary>Per-triangle coverage: stock's <c>TriangleColors[i].x</c>.</summary>
    public ReadOnlySpan<float> Coverage => _coverage;

    /// <summary>Per-triangle texture-shadow material index, or -1.</summary>
    public ReadOnlySpan<int> MaterialIndices => _materials;

    /// <summary>What one source contributed.</summary>
    /// <param name="source">The source.</param>
    /// <returns>Its count and bounds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such source.</exception>
    public ShadowCasterStats Stats(ShadowCasterSource source)
    {
        int index = (int)source;
        if ((uint)index >= (uint)_stats.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        return _stats[index];
    }

    /// <summary>How many triangles carry <see cref="TracedTriangle.Transparent"/>.</summary>
    /// <remarks>
    /// The one number that moves under <c>-textureshadows</c>. The caster SET
    /// does not: stock's own dumps with and without the switch are byte
    /// identical, on this map and with <c>-StaticPropPolys</c> as well.
    /// </remarks>
    public int TransparentCount
    {
        get
        {
            int count = 0;
            foreach (TracedTriangle triangle in _triangles)
            {
                if ((triangle.Flags & TracedTriangle.Transparent) != 0)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Builds the KD-tree tracer over this scene.</summary>
    /// <returns>The tracer, which is <c>g_RtEnv</c> after
    /// <c>SetupAccelerationStructure</c>.</returns>
    /// <exception cref="InvalidOperationException">The scene is empty.</exception>
    /// <remarks>
    /// Separate from construction because it is separate in stock, and because
    /// it is where the time goes: <c>-StaticPropPolys</c> on this project's
    /// golden map takes stock's build from 0.41 s to 1.27 s while changing no
    /// traced stage at all.
    /// </remarks>
    public KdRayTracer BuildTracer() => BuildTracer(ComplianceOptions.Correct);

    /// <summary>Builds the KD-tree tracer over this scene under a compliance.</summary>
    /// <param name="compliance">What the tracer reproduces of stock's traversal defects
    /// (<see cref="KdRayTracer.Build(ReadOnlySpan{TracedTriangle}, ComplianceOptions)"/>).</param>
    /// <returns>The tracer.</returns>
    /// <exception cref="InvalidOperationException">The scene is empty.</exception>
    public KdRayTracer BuildTracer(ComplianceOptions compliance)
    {
        if (_triangles.Length == 0)
        {
            throw new InvalidOperationException(
                "a map with no shadow casters has no acceleration structure to build; stock "
                + "would divide by zero in CalculateTriangleListBounds rather than say so");
        }

        return KdRayTracer.Build(_triangles, compliance);
    }

    /// <summary>
    /// <see cref="BuildTracer(ComplianceOptions)"/> with the tree built on a
    /// queue's workers: the same tracer, node for node.
    /// </summary>
    /// <param name="compliance">What the tracer reproduces of stock's traversal defects.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The tracer.</returns>
    /// <exception cref="InvalidOperationException">The scene is empty.</exception>
    public Task<KdRayTracer> BuildTracerAsync(
        ComplianceOptions compliance, WorkQueue queue, CancellationToken cancellationToken = default)
    {
        if (_triangles.Length == 0)
        {
            throw new InvalidOperationException(
                "a map with no shadow casters has no acceleration structure to build; stock "
                + "would divide by zero in CalculateTriangleListBounds rather than say so");
        }

        return KdRayTracer.BuildAsync(_triangles, compliance, queue, cancellationToken);
    }
}
