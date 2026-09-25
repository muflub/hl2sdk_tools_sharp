using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The welded vertex table and the 2D spatial hash over it
/// </summary>
/// <remarks>
/// <para>
/// This is what makes two faces that meet along an edge share the same vertex
/// indices, and therefore what makes the BSP watertight. A point is first
/// snapped to an integer on any axis it is within
/// <see cref="IntegralEpsilon"/> of, then looked up in a hash bucket keyed on
/// its x and y rounded down to a 128-unit cell, and accepted as an existing
/// vertex if it is within <see cref="PointEpsilon"/> on every axis.
/// </para>
/// <para>
/// <b>The hash is 2D on purpose.</b> z is not in the key at all
/// (<c>HashVec</c>), so a column of vertices stacked above each
/// other all land in one bucket. That is what makes the chains long in tall
/// maps, and it is also what makes <c>FindEdgeVerts</c> able to answer "every
/// vertex near this edge" by sweeping a rectangle of buckets.
/// </para>
/// </remarks>
public sealed class VertexWeld
{
    /// <summary><c>INTEGRAL_EPSILON</c>: snap to integer within this.</summary>
    public const double IntegralEpsilon = 0.01;

    /// <summary><c>POINT_EPSILON</c>: two points this close are one.</summary>
    public const double PointEpsilon = 0.1;

    /// <summary><c>HASH_BITS</c>: the cell is 2^7 = 128 units.</summary>
    public const int HashBits = 7;

    /// <summary><c>MAX_COORD_INTEGER</c>.</summary>
    public const int MaxCoordInteger = 16384;

    /// <summary><c>HASH_SIZE</c>: <c>COORD_EXTENT &gt;&gt; HASH_BITS</c> = 256 cells per axis.</summary>
    public const int HashSize = (2 * MaxCoordInteger) >> HashBits;

    /// <summary><c>MAX_MAP_VERTS</c>.</summary>
    public const int MaxMapVerts = 65536;

    private readonly List<Vec3> _vertexes = [];
    private readonly int[] _hashVerts = new int[HashSize * HashSize];
    // Indexed by vertex number; 0 is the chain terminator, which is safe
    // because vertex 0 is the reserved error vertex and is never hashed.
    private readonly int[] _vertexChain = new int[MaxMapVerts + 1];
    private readonly FaceCounters _counters;
    private readonly List<int> _edgeVerts = [];

    /// <summary>Creates an empty vertex table.</summary>
    /// <param name="counters">Where the weld's two counts are kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="counters"/> is null.</exception>
    public VertexWeld(FaceCounters counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        _counters = counters;
        ReserveErrorVertex();
    }

    /// <summary><c>numvertexes</c>: how many vertices have been emitted.</summary>
    public int Count => _vertexes.Count;

    /// <summary><c>dvertexes</c>: the vertex table, in emission order.</summary>
    public IReadOnlyList<Vec3> Vertexes => _vertexes;

    /// <summary>
    /// The vertices <see cref="FindEdgeVerts"/> last collected, for
    /// <c>TestEdge</c> to walk.
    /// </summary>
    public IReadOnlyList<int> EdgeVerts => _edgeVerts;

    /// <summary>One vertex by index.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The point.</returns>
    public Vec3 this[int index] => _vertexes[index];

    /// <summary>
    /// The hash bucket a point falls in(<c>HashVec</c>).
    /// </summary>
    /// <param name="point">The point, already snapped.</param>
    /// <returns>The bucket index.</returns>
    /// <exception cref="InvalidOperationException">
    /// The point is outside the world, which is stock's
    /// <c>Error("HashVec: point outside valid range")</c>.
    /// </exception>
    public static int HashVec(Vec3 point)
    {
        // (int)(vec[0]+0.5) in C adds a DOUBLE 0.5 to a float and truncates the
        // double, so the addition is done at double precision. Identical inside
        // the world box, and written the same way regardless.
        int x = (MaxCoordInteger + (int)(point.X + 0.5)) >> HashBits;
        int y = (MaxCoordInteger + (int)(point.Y + 0.5)) >> HashBits;

        if (x < 0 || x >= HashSize || y < 0 || y >= HashSize)
        {
            throw new InvalidOperationException("HashVec: point outside valid range");
        }

        return (y * HashSize) + x;
    }

    /// <summary>Clears both hash arrays and keeps the table, as <c>FixTjuncs</c> does.</summary>
    public void ResetHash()
    {
        // FixTjuncs memsets hashverts and vertexchain and
        // NOTHING else: numvertexes and dvertexes carry on across models, so a
        // brush model's faces index the same vertex lump the world's do and
        // start numbering where the previous model stopped. Only the weld's
        // lookup forgets, so a submodel never welds onto a world vertex.
        Array.Clear(_hashVerts);
        Array.Clear(_vertexChain);
        _edgeVerts.Clear();
    }

    /// <summary>
    /// Empties the table back to its just-constructed state: nothing but the
    /// reserved error vertex 0, and an empty hash. The compile driver never
    /// calls this (<see cref="ResetHash"/> is what <c>FixTjuncs</c> does); it
    /// is for a caller that owns one table per experiment.
    /// </summary>
    public void Reset()
    {
        _vertexes.Clear();
        Array.Clear(_hashVerts);
        Array.Clear(_vertexChain);
        _edgeVerts.Clear();
        ReserveErrorVertex();
    }

    // BeginBSPFile: "leave vertex 0 as an error" -- numvertexes = 1
    // Stock's dvertexes is a zeroed global, so the
    // placeholder is (0,0,0); it is never entered in the hash and not counted
    // by c_uniqueverts/c_totalverts. With it in place the first welded vertex
    // is 1, so the zero terminator of GetVertexnum's chain can
    // never hide a real vertex, and Count is c_uniqueverts + 1 on a one-model
    // compile.
    private void ReserveErrorVertex() => _vertexes.Add(default);

    /// <summary>
    /// The index of a point in the welded table, emitting it if it is new
    /// (<c>GetVertexnum</c>).
    /// </summary>
    /// <param name="point">The point to weld.</param>
    /// <returns>The index into <see cref="Vertexes"/>.</returns>
    /// <exception cref="InvalidOperationException">The table is full.</exception>
    public int GetVertexNumber(Vec3 point)
    {
        _counters.TotalVerts++;

        Vec3 vert = new(Snap(point.X), Snap(point.Y), Snap(point.Z));

        int h = HashVec(vert);

        // The chain terminator is the index 0. That hides
        // nothing: vertex 0 is the reserved error vertex and is never hashed.
        for (int vnum = _hashVerts[h]; vnum != 0; vnum = _vertexChain[vnum])
        {
            Vec3 p = _vertexes[vnum];

            if (Math.Abs(p.X - vert.X) < PointEpsilon
                && Math.Abs(p.Y - vert.Y) < PointEpsilon
                && Math.Abs(p.Z - vert.Z) < PointEpsilon)
            {
                return vnum;
            }
        }

        if (_vertexes.Count == MaxMapVerts)
        {
            throw new InvalidOperationException(
                $"Too many unique verts, max = {MaxMapVerts} (map has too much brush geometry)");
        }

        int index = _vertexes.Count;
        _vertexes.Add(vert);

        _vertexChain[index] = _hashVerts[h];
        _hashVerts[h] = index;

        _counters.UniqueVerts++;

        return index;
    }

    /// <summary>
    /// Emits a point without welding it (<c>-noweld</c>,
    /// <c>EmitFaceVertexes</c>).
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The index it was given.</returns>
    /// <exception cref="InvalidOperationException">The table is full.</exception>
    /// <remarks>
    /// The point is stored raw: <c>-noweld</c> skips the integer snapping too,
    /// because the snapping lives inside <c>GetVertexnum</c>. Nothing is added
    /// to the hash either, so a later welded lookup cannot find it.
    /// </remarks>
    public int EmitUnwelded(Vec3 point)
    {
        if (_vertexes.Count == MaxMapVerts)
        {
            throw new InvalidOperationException(
                $"Too many unique verts, max = {MaxMapVerts} (map has too much brush geometry)");
        }

        _vertexes.Add(point);
        _counters.UniqueVerts++;
        _counters.TotalVerts++;
        return _vertexes.Count - 1;
    }

    /// <summary>
    /// Collects every hashed vertex in the rectangle of buckets the two
    /// endpoints span(<c>FindEdgeVerts</c>).
    /// </summary>
    /// <param name="v1">One end of the edge.</param>
    /// <param name="v2">The other end.</param>
    /// <remarks>
    /// <para>
    /// The bucket rectangle is the bounding box of the two ENDPOINTS, not of
    /// the edge plus a margin: the <c>x1--; x2++;</c> expansion stock wrote is
    /// inside an <c>#if 0</c>. A cell is 128 units and
    /// <c>OFF_EPSILON</c> is 0.25, so a t-junction vertex can only be missed if
    /// it sits outside the endpoints' own cell span, which for a straight edge
    /// it cannot.
    /// </para>
    /// <para>
    /// The loops are inclusive at both ends and nothing clamps them, exactly as
    /// stock leaves it; the clamping is in the same disabled block.
    /// </para>
    /// </remarks>
    public void FindEdgeVerts(Vec3 v1, Vec3 v2)
    {
        int x1 = (MaxCoordInteger + (int)(v1.X + 0.5)) >> HashBits;
        int y1 = (MaxCoordInteger + (int)(v1.Y + 0.5)) >> HashBits;
        int x2 = (MaxCoordInteger + (int)(v2.X + 0.5)) >> HashBits;
        int y2 = (MaxCoordInteger + (int)(v2.Y + 0.5)) >> HashBits;

        if (x1 > x2)
        {
            (x1, x2) = (x2, x1);
        }

        if (y1 > y2)
        {
            (y1, y2) = (y2, y1);
        }

        _edgeVerts.Clear();

        for (int x = x1; x <= x2; x++)
        {
            for (int y = y1; y <= y2; y++)
            {
                for (int vnum = _hashVerts[(y * HashSize) + x]; vnum != 0; vnum = _vertexChain[vnum])
                {
                    _edgeVerts.Add(vnum);
                }
            }
        }
    }

    private static float Snap(float value)
    {
        int rounded = (int)(value + 0.5);
        return Math.Abs(value - rounded) < IntegralEpsilon ? rounded : value;
    }
}
