using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Portals;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Welding every face's vertices into one table and splitting edges wherever
/// another face's vertex lands on them
/// (<c>FixTjuncs</c>).
/// </summary>
/// <remarks>
/// <para>
/// A t-junction is a vertex of one face sitting in the middle of another
/// face's edge. Nothing is geometrically wrong with it, but the two faces are
/// rasterised independently and the shared edge is then interpolated from
/// different endpoints on each side, so a line of background pixels shows
/// through. The fix is to add the offending vertex to the long edge, turning
/// one edge into two collinear ones.
/// </para>
/// <para>
/// The pass runs in two halves. <c>EmitNodeFaceVertexes_r</c> welds every
/// face's points into the shared vertex table, which is what makes "another
/// face's vertex" a question about indices; then <c>FixEdges_r</c> walks each
/// face's edges and asks the hash which welded vertices lie on them.
/// </para>
/// <para>
/// <b>A face can come out of this with more vertices than a face may hold.</b>
/// <c>FaceFromSuperverts</c> then fragments it into a chain of
/// <see cref="Face.MaxEdges"/>-vertex faces, which is the "%5i faces added by
/// tjunctions" line.
/// </para>
/// </remarks>
public sealed class TJunctionFixer
{
    /// <summary><c>OFF_EPSILON</c>: how far off an edge still counts as on it.</summary>
    public const double OffEpsilon = 0.25;

    /// <summary><c>MAX_SUPERVERTS</c>.</summary>
    public const int MaxSuperVerts = 512;

    private readonly FaceBuildContext _context;
    private readonly int[] _superVerts = new int[MaxSuperVerts];

    // stock's count[MAX_SUPERVERTS] and start[MAX_SUPERVERTS], which are
    // FixFaceEdges locals; fields here because the routine is not reentrant and
    // a 512-entry pair per face would be pure garbage.
    private readonly int[] _edgeVertCount = new int[MaxSuperVerts];
    private readonly int[] _edgeVertStart = new int[MaxSuperVerts];
    private int _numSuperVerts;

    private Vec3 _edgeStart;
    private Vec3 _edgeDir;

    /// <summary>Creates a fixer over one model's face stage.</summary>
    /// <param name="context">The stage's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public TJunctionFixer(FaceBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// The vertex indices collected for the face currently being processed
    /// (<c>superverts</c>).
    /// </summary>
    public ReadOnlySpan<int> SuperVerts => _superVerts.AsSpan(0, _numSuperVerts);

    /// <summary>
    /// Fills a face's vertex list from <see cref="SuperVerts"/>, fragmenting it
    /// when there are too many(<c>FaceFromSuperverts</c>).
    /// </summary>
    /// <param name="head">The list the fragments are pushed onto.</param>
    /// <param name="face">The face to fill.</param>
    /// <param name="baseVertex">Which supervert becomes vertex 0.</param>
    /// <remarks>
    /// Each fragment takes <see cref="Face.MaxEdges"/> superverts and the next
    /// one restarts two short of where it ended, so the fragments share an edge
    /// rather than meeting at a point. That is the <c>MAXEDGES-2</c> and
    /// <c>MAXEDGES-1</c> pair, and it is why a 33-vertex face becomes two faces
    /// and not one of 32 plus one of 1.
    /// </remarks>
    public void FaceFromSuperverts(ref Face? head, Face face, int baseVertex)
    {
        ArgumentNullException.ThrowIfNull(face);

        int remaining = _numSuperVerts;

        while (remaining > Face.MaxEdges)
        {
            // must split into two faces, because of vertex overload
            _context.Counters.FaceOverflows++;

            Face fragment = _context.Faces.NewFaceFromFace(face);
            face.Split[0] = fragment;

            fragment.Next = head;
            head = fragment;

            fragment.NumPoints = Face.MaxEdges;

            for (int i = 0; i < Face.MaxEdges; i++)
            {
                fragment.VertexNumbers[i] = _superVerts[(i + baseVertex) % _numSuperVerts];
            }

            face.Split[1] = _context.Faces.NewFaceFromFace(face);
            face = face.Split[1]!;

            face.Next = head;
            head = face;

            remaining -= Face.MaxEdges - 2;
            baseVertex = (baseVertex + Face.MaxEdges - 1) % _numSuperVerts;
        }

        // copy the vertexes back to the face
        face.NumPoints = remaining;

        for (int i = 0; i < remaining; i++)
        {
            face.VertexNumbers[i] = _superVerts[(i + baseVertex) % _numSuperVerts];
        }
    }

    /// <summary>
    /// Welds one face's winding into the vertex table
    /// (<c>EmitFaceVertexes</c>).
    /// </summary>
    /// <param name="head">The list any fragments are pushed onto.</param>
    /// <param name="face">The face to weld.</param>
    public void EmitFaceVertexes(ref Face? head, Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.IsDead)
        {
            return;
        }

        Span<Vec3> points = _context.Windings.Points(face.Winding);

        for (int i = 0; i < points.Length; i++)
        {
            _superVerts[i] = _context.Options.NoWeld
                ? _context.Vertices.EmitUnwelded(points[i])
                : _context.Vertices.GetVertexNumber(points[i]);
        }

        _numSuperVerts = points.Length;

        // this may fragment the face if > MAXEDGES
        FaceFromSuperverts(ref head, face, 0);
    }

    /// <summary>
    /// Welds every face on every node of a subtree
    /// (<c>EmitNodeFaceVertexes_r</c>).
    /// </summary>
    /// <param name="node">The subtree root.</param>
    /// <remarks>Leaf faces are welded separately, in a second pass.</remarks>
    public void EmitNodeFaceVertexesRecursive(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            return;
        }

        Face? head = _context.Lists.FacesOf(node);

        for (Face? f = head; f is not null; f = f.Next)
        {
            EmitFaceVertexes(ref head, f);
        }

        _context.Lists.SetFacesOf(node, head);

        EmitNodeFaceVertexesRecursive(node.Front!);
        EmitNodeFaceVertexesRecursive(node.Back!);
    }

    /// <summary>
    /// Welds every face on the detail leaf-face list
    /// (<c>EmitLeafFaceVertexes</c>).
    /// </summary>
    /// <param name="head">The leaf face list.</param>
    /// <returns>The head afterwards.</returns>
    public Face? EmitLeafFaceVertexes(Face? head)
    {
        for (Face? f = head; f is not null; f = f.Next)
        {
            EmitFaceVertexes(ref head, f);
        }

        return head;
    }

    /// <summary>
    /// Adds every welded vertex that lies strictly between two points of an
    /// edge, recursing into the two halves
    /// (<c>TestEdge</c>).
    /// </summary>
    /// <param name="start">Distance along the edge where this segment begins.</param>
    /// <param name="end">Distance along the edge where it ends.</param>
    /// <param name="p1">The vertex at <paramref name="start"/>.</param>
    /// <param name="p2">The vertex at <paramref name="end"/>.</param>
    /// <param name="startVert">Where in the candidate list to resume scanning.</param>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxSuperVerts"/> verts on one edge.</exception>
    /// <remarks>
    /// <para>
    /// Only <paramref name="p1"/> is ever emitted, at the end of the recursion
    /// — so the edge contributes its start vertex and every split point, and
    /// its END vertex is left to the next edge of the face. That is what makes
    /// the superverts a closed ring rather than a list with a duplicated seam.
    /// </para>
    /// <para>
    /// A degenerate edge (both ends the same vertex) emits NOTHING and counts
    /// as a degenerate, which is how a face can lose vertices here.
    /// </para>
    /// </remarks>
    public void TestEdge(float start, float end, int p1, int p2, int startVert)
    {
        if (p1 == p2)
        {
            _context.Counters.DegenerateEdges++;
            return;     // degenerate edge
        }

        IReadOnlyList<int> candidates = _context.Vertices.EdgeVerts;

        for (int k = startVert; k < candidates.Count; k++)
        {
            int j = candidates[k];

            if (j == p1 || j == p2)
            {
                continue;
            }

            Vec3 p = _context.Vertices[j];

            float dist = Vec3.Dot(p - _edgeStart, _edgeDir);

            if (dist <= start || dist >= end)
            {
                continue;       // off an end
            }

            Vec3 exact = _edgeStart + (_edgeDir * dist);
            float error = (p - exact).Length();

            if (error > OffEpsilon)
            {
                continue;       // not on the edge
            }

            // break the edge
            _context.Counters.TJunctions++;
            TestEdge(start, dist, p1, j, k + 1);
            TestEdge(dist, end, j, p2, k + 1);
            return;
        }

        // the edge p1 to p2 is now free of tjunctions
        if (_numSuperVerts >= MaxSuperVerts)
        {
            throw new InvalidOperationException(
                $"Edge with too many vertices due to t-junctions.  Max {MaxSuperVerts} verts along an edge!");
        }

        _superVerts[_numSuperVerts] = p1;
        _numSuperVerts++;
    }

    /// <summary>
    /// Splits every edge of one face at its t-junctions
    /// (<c>FixFaceEdges</c>).
    /// </summary>
    /// <param name="head">The list any fragments are pushed onto.</param>
    /// <param name="face">The face to fix.</param>
    public void FixFaceEdges(ref Face? head, Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.IsDead)
        {
            return;
        }

        _numSuperVerts = 0;

        int[] count = _edgeVertCount;
        int[] start = _edgeVertStart;

        int originalPoints = face.NumPoints;

        for (int i = 0; i < face.NumPoints; i++)
        {
            int p1 = face.VertexNumbers[i];
            int p2 = face.VertexNumbers[(i + 1) % face.NumPoints];

            _edgeStart = _context.Vertices[p1];
            Vec3 e2 = _context.Vertices[p2];

            _context.Vertices.FindEdgeVerts(_edgeStart, e2);

            (_edgeDir, float len) = (e2 - _edgeStart).NormaliseLikeStock();

            start[i] = _numSuperVerts;
            TestEdge(0f, len, p1, p2, 0);

            count[i] = _numSuperVerts - start[i];
        }

        if (_numSuperVerts < 3)
        {
            // entire face collapsed
            face.NumPoints = 0;
            _context.Counters.CollapsedFaces++;
            return;
        }

        // we want to pick a vertex that doesn't have tjunctions on either side,
        // which can cause artifacts on trifans, especially underwater
        int chosen;

        for (chosen = 0; chosen < face.NumPoints; chosen++)
        {
            if (count[chosen] == 1 && count[(chosen + face.NumPoints - 1) % face.NumPoints] == 1)
            {
                break;
            }
        }

        int baseVertex;

        if (chosen == face.NumPoints)
        {
            face.BadStartVert = true;
            _context.Counters.BadStartVerts++;
            baseVertex = 0;
        }
        else
        {
            // rotate the vertex order
            baseVertex = start[chosen];
        }

        int superVertCount = _numSuperVerts;

        // this may fragment the face if > MAXEDGES
        FaceFromSuperverts(ref head, face, baseVertex);

        // if this is the world, then re-triangulate to sew cracks
        if (face.BadStartVert && _context.EntityNumber == 0)
        {
            BuildCrackSewingPrimitive(face, count, start, originalPoints, superVertCount);
        }
    }

    /// <summary>
    /// Splits every face on every node of a subtree
    /// (<c>FixEdges_r</c>).
    /// </summary>
    /// <param name="node">The subtree root.</param>
    public void FixEdgesRecursive(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            return;
        }

        Face? head = _context.Lists.FacesOf(node);

        for (Face? f = head; f is not null; f = f.Next)
        {
            FixFaceEdges(ref head, f);
        }

        _context.Lists.SetFacesOf(node, head);

        FixEdgesRecursive(node.Front!);
        FixEdgesRecursive(node.Back!);
    }

    /// <summary>
    /// Splits every face on the detail leaf-face list
    /// (<c>FixLeafFaceEdges</c>).
    /// </summary>
    /// <param name="head">The leaf face list.</param>
    /// <returns>The head afterwards.</returns>
    public Face? FixLeafFaceEdges(Face? head)
    {
        for (Face? f = head; f is not null; f = f.Next)
        {
            FixFaceEdges(ref head, f);
        }

        return head;
    }

    /// <summary>
    /// Welds all vertices and removes all t-junctions for one model
    /// (<c>FixTjuncs</c>).
    /// </summary>
    /// <param name="headNode">The model's tree root.</param>
    /// <param name="leafFaceList">The detail faces filtered into leaves.</param>
    /// <returns>The leaf face list afterwards.</returns>
    /// <remarks>
    /// <para>
    /// <b>The order of the two halves depends on <c>-allowdetailcracks</c>, and
    /// it is not a stylistic difference.</b> With detail cracks allowed, node
    /// faces are t-junction-fixed BEFORE the detail faces have even been welded
    /// into the vertex table, so a detail vertex can never split a world edge.
    /// Without it, every vertex is in the table before any edge is tested and
    /// details sew to the world. That is the whole meaning of the switch.
    /// </para>
    /// <para>
    /// Only the weld HASH is cleared here, which
    /// makes welding per-MODEL; the vertex table itself is per-MAP, so a brush
    /// model's vertices are numbered after the world's in the one vertex lump.
    /// (Clearing the table, as this once did, renumbered every submodel's
    /// vertices from zero over the world's.)
    /// </para>
    /// </remarks>
    public Face? FixTjuncs(IBspNode headNode, Face? leafFaceList)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        // snap and merge all vertexes
        _context.Vertices.ResetHash();
        _context.Counters.ResetForFixTjuncs();

        EmitNodeFaceVertexesRecursive(headNode);

        // break edges on tjunctions
        if (_context.Options.AllowDetailCracks)
        {
            FixEdgesRecursive(headNode);
            leafFaceList = EmitLeafFaceVertexes(leafFaceList);
            leafFaceList = FixLeafFaceEdges(leafFaceList);
        }
        else
        {
            leafFaceList = EmitLeafFaceVertexes(leafFaceList);

            if (!_context.Options.NoTJunc)
            {
                FixEdgesRecursive(headNode);
                leafFaceList = FixLeafFaceEdges(leafFaceList);
            }
        }

        return leafFaceList;
    }

    /// <summary>
    /// Whether two superverts can be joined by a diagonal, i.e. do NOT share an
    /// edge(<c>IsDiagonal</c>).
    /// </summary>
    /// <param name="v0">One vertex's edge membership.</param>
    /// <param name="v1">The other's.</param>
    /// <returns>True when they share no edge.</returns>
    public static bool IsDiagonal(FaceVertEdges v0, FaceVertEdges v1) =>
        !v1.HasEdge(v0.Edge0) && !v1.HasEdge(v0.Edge1);

    /// <summary>
    /// Fans a polygon into triangles, never cutting along an edge two vertices
    /// share(<c>Triangulate_r</c>).
    /// </summary>
    /// <param name="output">Where triangle indices are appended.</param>
    /// <param name="indices">The polygon, as supervert indices.</param>
    /// <param name="poly">Each supervert's edge membership.</param>
    /// <exception cref="InvalidOperationException">
    /// No diagonal was found, which stock reaches as <c>Assert(0)</c> and then
    /// silently returns from, leaving the polygon untriangulated.
    /// </exception>
    public static void TriangulateRecursive(
        List<int> output,
        IReadOnlyList<int> indices,
        IReadOnlyList<FaceVertEdges> poly)
    {
        // one triangle left, return
        if (indices.Count == 3)
        {
            for (int i = 0; i < indices.Count; i++)
            {
                output.Add(indices[i]);
            }

            return;
        }

        // check each pair of verts and see if they are diagonal (not on a
        // shared edge); if so, split and recurse
        for (int i = 0; i < indices.Count; i++)
        {
            int count = indices.Count;

            // i + count is myself, i + count-1 is previous, so we stop at i+count-2
            for (int j = 2; j < count - 1; j++)
            {
                int index = indices[i];
                int nextArray = (i + j) % count;
                int nextIndex = indices[nextArray];

                if (!IsDiagonal(poly[index], poly[nextIndex]))
                {
                    continue;
                }

                // add the poly up to the diagonal
                List<int> in1 = [];

                for (int k = i; k != nextArray; k = (k + 1) % count)
                {
                    in1.Add(indices[k]);
                }

                in1.Add(nextIndex);

                // add the rest of the poly starting with the diagonal
                List<int> in2 = [index];

                for (int l = nextArray; l != i; l = (l + 1) % count)
                {
                    in2.Add(indices[l]);
                }

                TriangulateRecursive(output, in1, poly);
                TriangulateRecursive(output, in2, poly);
                return;
            }
        }

        throw new InvalidOperationException(
            "Triangulate_r: no diagonal found in a t-junction-fixed face");
    }

    private void BuildCrackSewingPrimitive(
        Face face,
        int[] count,
        int[] start,
        int originalPoints,
        int superVertCount)
    {
        FaceVertEdges[] poly = new FaceVertEdges[superVertCount];

        for (int i = 0; i < superVertCount; i++)
        {
            poly[i] = FaceVertEdges.Empty;
        }

        for (int i = 0; i < originalPoints; i++)
        {
            // edge may not have output any points.  Don't mark
            if (count[i] == 0)
            {
                continue;
            }

            // mark each edge the point is a member of; we'll use this as a fast
            // "is collinear" test. The loop runs to count[i] INCLUSIVE, so the
            // corner shared with the next edge is marked by both.
            for (int j = 0; j <= count[i]; j++)
            {
                int polyIndex = (start[i] + j) % superVertCount;
                poly[polyIndex] = poly[polyIndex].AddEdge(i);
            }
        }

        List<int> inIndices = [];

        for (int i = 0; i < superVertCount; i++)
        {
            inIndices.Add(i);
        }

        List<int> outIndices = [];
        TriangulateRecursive(outIndices, inIndices, poly);

        face.FirstPrimId = _context.Primitives.Primitives.Count;
        face.NumPrims = 1;

        _context.Primitives.AddTriangleList(outIndices);
    }
}

/// <summary>
/// Which of a face's edges one supervert belongs to
/// (<c>face_vert_table_t</c>).
/// </summary>
/// <remarks>
/// At most two, because a supervert is either a corner of the face (on the two
/// edges that meet there) or a t-junction split point in the middle of one
/// edge. Stock asserts the third would-be edge away; here the third is
/// silently dropped exactly as a release build does, because the assert is
/// compiled out of the tool that shipped and the port must match what shipped.
/// </remarks>
public readonly record struct FaceVertEdges(int Edge0, int Edge1)
{
    /// <summary>A vertex on no edge yet: stock's constructor sets both to -1.</summary>
    public static FaceVertEdges Empty => new(-1, -1);

    /// <summary>Records that this vertex is on one more edge.</summary>
    /// <param name="edge">The edge index.</param>
    /// <returns>The updated membership.</returns>
    public FaceVertEdges AddEdge(int edge) =>
        Edge0 == -1 ? this with { Edge0 = edge } : this with { Edge1 = edge };

    /// <summary>Whether this vertex is on a given edge.</summary>
    /// <param name="edge">The edge index, or -1 for "no edge".</param>
    /// <returns>True when it is, and always false for -1.</returns>
    public bool HasEdge(int edge) => edge >= 0 && (Edge0 == edge || Edge1 == edge);
}
