//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// Accumulates vrad's shadow casters in the order stock adds them.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>RayTracingEnvironment</c>'s three append-only lists --
/// <c>OptimizedTriangleList</c>, <c>TriangleColors</c> and
/// <c>TriangleMaterials</c> -- and nothing else.
/// The KD-tree is not built here: <c>SetupAccelerationStructure</c> is a
/// separate call in stock too, and keeping the two apart
/// is what makes <c>-dumptrace</c> possible at all, since
/// <c>ChangeIntoIntersectionFormat</c> destroys the vertices it reads.
/// </para>
/// <para>
/// ORDER IS PART OF THE CONTRACT. Stock's triangle ids are not unique -- every
/// world brush triangle in a map carries the same <c>TRACE_ID_OPAQUE</c> -- so
/// the only thing that identifies a triangle is its position in this list, and
/// that position is what a <see cref="HitId"/> from the KD tracer reports.
/// A builder that sorted, deduplicated or parallelised the appends would
/// produce a numerically identical scene that no recorded answer could be
/// compared against.
/// </para>
/// <para>
/// The per-source bookkeeping around <see cref="BeginSource"/> is this port's
/// own, and it is the 4b gate: stock emits no caster count anywhere
/// (<c>Total triangle count:</c>, a
/// <c>sum of dfaces[i].numedges - 2</c> that does not move when
/// <c>-StaticPropPolys</c> triples the scene).
/// </para>
/// </remarks>
public sealed class ShadowCasterBuilder
{
    private readonly List<TracedTriangle> _triangles = [];
    private readonly List<float> _coverage = [];
    private readonly List<int> _materials = [];
    private readonly SourceRun[] _runs =
        new SourceRun[Enum.GetValues<ShadowCasterSource>().Length];

    private ShadowCasterSource _current = ShadowCasterSource.BrushEntity;

    /// <summary>Starts the builder with every source run empty.</summary>
    public ShadowCasterBuilder()
    {
        for (int i = 0; i < _runs.Length; i++)
        {
            _runs[i] = SourceRun.Empty;
        }
    }

    /// <summary>
    /// The compliance the builder runs under.
    /// </summary>
    /// <remarks>
    /// Only <see cref="AddQuad"/> reads it. Defaults to
    /// <see cref="ComplianceOptions.Correct"/>, so a builder made in a unit
    /// test gets the right triangle ids.
    /// </remarks>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>How many triangles have been added so far.</summary>
    public int Count => _triangles.Count;

    /// <summary>
    /// Declares which of vrad's load calls the following triangles come from.
    /// </summary>
    /// <param name="source">The call.</param>
    /// <remarks>
    /// Purely bookkeeping: it changes no triangle and no identity. Calling it
    /// twice for the same source is allowed, because stock's brush path is
    /// entered once per entity.
    /// </remarks>
    public void BeginSource(ShadowCasterSource source) => _current = source;

    /// <summary>
    /// Appends one triangle, exactly as <c>RayTracingEnvironment::AddTriangle</c>.
    /// </summary>
    /// <param name="id">The triangle identity; see <see cref="TraceId"/>.</param>
    /// <param name="v0">First vertex.</param>
    /// <param name="v1">Second vertex.</param>
    /// <param name="v2">Third vertex.</param>
    /// <param name="coverage">
    /// Stock's per-triangle colour, of which only <c>x</c> is ever read
    /// One means the triangle blocks light completely.
    /// </param>
    /// <param name="flags"><c>FCACHETRI_*</c>; see <see cref="TracedTriangle.Flags"/>.</param>
    /// <param name="materialIndex">
    /// An index into the texture-shadow material table, or -1 for none.
    /// </param>
    /// <remarks>
    /// Degenerate triangles are NOT rejected, and that is deliberate rather
    /// than an omission. <c>AddBrushToRaytraceEnvironment</c> clips its
    /// windings with an epsilon of exactly zero, so a
    /// brush with a sliver side yields zero-area triangles; stock keeps them,
    /// they reach the KD build, and dropping them here would move the triangle
    /// index of everything after them.
    /// </remarks>
    public void AddTriangle(
        int id,
        Vec3 v0,
        Vec3 v1,
        Vec3 v2,
        float coverage,
        byte flags = 0,
        int materialIndex = -1)
    {
        _triangles.Add(new TracedTriangle(id, v0, v1, v2, flags));
        _coverage.Add(coverage);
        _materials.Add(materialIndex);

        ref SourceRun run = ref _runs[(int)_current];
        run.Add(v0);
        run.Add(v1);
        run.Add(v2);
    }

    /// <summary>
    /// Appends a quad as two triangles.
    /// </summary>
    /// <param name="id">The identity of the FIRST triangle.</param>
    /// <param name="v1">First corner.</param>
    /// <param name="v2">Second corner.</param>
    /// <param name="v3">Third corner.</param>
    /// <param name="v4">Fourth corner.</param>
    /// <param name="coverage">Per-triangle coverage.</param>
    /// <remarks>
    /// Under <see cref="CompliancePolicy.Stock"/> THE SECOND TRIANGLE GETS
    /// <c>id + 1</c>, which is stock's own arithmetic and is a defect
    /// everywhere vrad uses it. The only vrad caller is the static-prop AABB
    /// Fallback, which passes
    /// <c>TRACE_ID_STATICPROP | nProp</c> -- so six of the twelve triangles of
    /// a prop's box are attributed to prop <c>nProp + 1</c>, and a shadow ray
    /// that should skip its own prop skips its neighbour's box instead.
    /// <see cref="CompliancePolicy.Correct"/> gives both triangles the same id.
    /// See <see cref="StockQuirk.AddQuadSecondTriangleId"/>.
    /// </remarks>
    public void AddQuad(int id, Vec3 v1, Vec3 v2, Vec3 v3, Vec3 v4, float coverage)
    {
        AddTriangle(id, v1, v2, v3, coverage);

        // StockQuirk.AddQuadSecondTriangleId.
        int secondId = Compliance.Emulates(StockQuirk.AddQuadSecondTriangleId)
            ? id + 1
            : id;

        AddTriangle(secondId, v1, v3, v4, coverage);
    }

    /// <summary>
    /// Appends the twelve triangles of an axis-aligned box,
    /// </summary>
    /// <param name="id">The identity passed to each of the six quads.</param>
    /// <param name="min">The box's lower corner.</param>
    /// <param name="max">The box's upper corner.</param>
    /// <param name="coverage">Per-triangle coverage.</param>
    /// <remarks>
    /// The six faces go far, near, left, right, top, bottom, and the corner
    /// order within each is stock's. It matters: the winding decides the
    /// triangle's normal, and the order is what a recorded comparison against
    /// stock's dump reproduces vertex for vertex.
    /// </remarks>
    public void AddAxisAlignedRectangularSolid(int id, Vec3 min, Vec3 max, float coverage)
    {
        // "far"
        AddQuad(
            id,
            new Vec3(min.X, max.Y, max.Z),
            new Vec3(max.X, max.Y, max.Z),
            new Vec3(max.X, min.Y, max.Z),
            new Vec3(min.X, min.Y, max.Z),
            coverage);

        // "near"
        AddQuad(
            id,
            new Vec3(min.X, max.Y, min.Z),
            new Vec3(max.X, max.Y, min.Z),
            new Vec3(max.X, min.Y, min.Z),
            new Vec3(min.X, min.Y, min.Z),
            coverage);

        // "left"
        AddQuad(
            id,
            new Vec3(min.X, max.Y, max.Z),
            new Vec3(min.X, max.Y, min.Z),
            new Vec3(min.X, min.Y, min.Z),
            new Vec3(min.X, min.Y, max.Z),
            coverage);

        // "right"
        AddQuad(
            id,
            new Vec3(max.X, max.Y, max.Z),
            new Vec3(max.X, max.Y, min.Z),
            new Vec3(max.X, min.Y, min.Z),
            new Vec3(max.X, min.Y, max.Z),
            coverage);

        // "top"
        AddQuad(
            id,
            new Vec3(min.X, max.Y, max.Z),
            new Vec3(max.X, max.Y, max.Z),
            new Vec3(max.X, max.Y, min.Z),
            new Vec3(min.X, max.Y, min.Z),
            coverage);

        // "bot"
        AddQuad(
            id,
            new Vec3(min.X, min.Y, max.Z),
            new Vec3(max.X, min.Y, max.Z),
            new Vec3(max.X, min.Y, min.Z),
            new Vec3(min.X, min.Y, min.Z),
            coverage);
    }

    /// <summary>Freezes what has been added into a caster set.</summary>
    /// <returns>The set.</returns>
    public ShadowCasterSet Build()
    {
        ShadowCasterStats[] stats = new ShadowCasterStats[_runs.Length];
        for (int i = 0; i < _runs.Length; i++)
        {
            stats[i] = _runs[i].ToStats();
        }

        return new ShadowCasterSet(
            [.. _triangles],
            [.. _coverage],
            [.. _materials],
            stats);
    }

    /// <summary>Running count and bounds for one source.</summary>
    private struct SourceRun
    {
        private float _minX;
        private float _minY;
        private float _minZ;
        private float _maxX;
        private float _maxY;
        private float _maxZ;
        private int _vertices;

        public static SourceRun Empty => new()
        {
            _minX = float.PositiveInfinity,
            _minY = float.PositiveInfinity,
            _minZ = float.PositiveInfinity,
            _maxX = float.NegativeInfinity,
            _maxY = float.NegativeInfinity,
            _maxZ = float.NegativeInfinity,
            _vertices = 0,
        };

        public void Add(Vec3 v)
        {
            _minX = MathF.Min(_minX, v.X);
            _minY = MathF.Min(_minY, v.Y);
            _minZ = MathF.Min(_minZ, v.Z);
            _maxX = MathF.Max(_maxX, v.X);
            _maxY = MathF.Max(_maxY, v.Y);
            _maxZ = MathF.Max(_maxZ, v.Z);
            _vertices++;
        }

        public readonly ShadowCasterStats ToStats() => _vertices == 0
            ? ShadowCasterStats.Empty
            : new ShadowCasterStats(
                _vertices / 3,
                new Vec3(_minX, _minY, _minZ),
                new Vec3(_maxX, _maxY, _maxZ));
    }
}
