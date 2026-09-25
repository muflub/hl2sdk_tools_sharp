using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Which faces share a vertex with which, and the smoothed normal at every
/// face corner: <c>PairEdges</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the input to phong shading and to the bump basis, and it is the one
/// place smoothing groups are honoured. Nothing else in vrad decides whether
/// two faces are "the same surface".
/// </para>
/// <para>
/// Stock writes into <c>faceneighbor[MAX_MAP_FACES]</c>, a 65,536-entry
/// file-scope array of structs holding two <c>calloc</c>ed pointers each, and
/// never frees either. The shape here is jagged arrays on one object, which is
/// the same data; the only behavioural change is that two maps can be paired
/// in one process.
/// </para>
/// <para>
/// <b>The smoothing rules are three, not one</b>, and which applies is decided
/// per NEIGHBOUR rather than per face:
/// </para>
/// <list type="number">
/// <item><description>
/// If THIS face is a displacement, every neighbour smooths, whatever the angle
/// and whatever the smoothing groups say. Displacements are always welded.
/// </description></item>
/// <item><description>
/// Else, if NEITHER face has smoothing groups, the angle threshold decides.
/// </description></item>
/// <item><description>
/// Else the groups decide, and a shared bit in the top byte
/// (<c>SMOOTHING_GROUP_HARD_EDGE</c>) vetoes -- so a hard edge beats a shared
/// group but does NOT beat rule 1.
/// </description></item>
/// </list>
/// <para>
/// Note the asymmetry in rule 1: a non-displacement face SKIPS displacement
/// neighbours entirely while a displacement face accepts
/// brush neighbours. So the smoothing relation is not symmetric, and the seam
/// between a displacement and the brush it sits on is smoothed from one side
/// only. That is stock's behaviour and it is reproduced: it is a deliberate
/// choice about which surface bends, not a defect.
/// </para>
/// </remarks>
public sealed class FaceNeighbours
{
    /// <summary>
    /// <c>SMOOTHING_GROUP_HARD_EDGE</c>: the top byte
    /// of the smoothing-group mask, which vetoes smoothing rather than
    /// requesting it.
    /// </summary>
    public const uint HardEdgeGroups = 0xFF000000u;

    /// <summary>
    /// How many neighbours one face may have before stock's
    /// <c>tmpneighbor[64]</c> overflows.
    /// </summary>
    /// <remarks>
    /// Stock's bound check runs AFTER the write that
 /// overflows -- <c>tmpneighbor[m] =...</c> with
    /// <c>m == 64</c> is already out of bounds -- so its <c>Error</c> is raised
    /// from a corrupted stack. There is no output difference to reproduce
    /// either way, because both tools stop; this one simply checks first.
    /// </remarks>
    public const int MaxNeighbours = 64;

    private readonly Vec3[] _faceNormals;
    private readonly Vec3[][] _cornerNormals;
    private readonly int[][] _neighbours;
    private readonly bool[] _hasDisp;

    private FaceNeighbours(
        Vec3[] faceNormals,
        Vec3[][] cornerNormals,
        int[][] neighbours,
        bool[] hasDisp)
    {
        _faceNormals = faceNormals;
        _cornerNormals = cornerNormals;
        _neighbours = neighbours;
        _hasDisp = hasDisp;
    }

    /// <summary>How many faces this covers.</summary>
    public int Count => _faceNormals.Length;

    /// <summary>
    /// <c>faceneighbor[i].facenormal</c>: the face's plane normal, uncorrected
    /// for facing.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>The plane normal.</returns>
    /// <remarks>
    /// Copied straight from <c>dplanes[f-&gt;planenum].normal</c> without
    /// consulting <c>f-&gt;side</c>, so a face on the
    /// back of its plane gets the plane's normal rather than its own. vbsp does
    /// not emit those for world faces, and vrad's patch path corrects
    /// separately through <c>patch-&gt;plane</c>; this value is what the
    /// smoothing comparison uses and is reproduced as written.
    /// </remarks>
    public Vec3 FaceNormal(int faceNum) => _faceNormals[faceNum];

    /// <summary>
    /// <c>faceneighbor[i].normal</c>: the smoothed normal at each of the face's
    /// corners, in edge order.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>One unit normal per edge.</returns>
    public ReadOnlySpan<Vec3> CornerNormals(int faceNum) => _cornerNormals[faceNum];

    /// <summary>
    /// <c>faceneighbor[i].neighbor</c>: the faces that smooth with this one.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>Their indices, in the order stock discovered them.</returns>
    public ReadOnlySpan<int> Neighbours(int faceNum) => _neighbours[faceNum];

    /// <summary><c>faceneighbor[i].bHasDisp</c>.</summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>True when the face is a valid displacement face.</returns>
    public bool HasDisplacement(int faceNum) => _hasDisp[faceNum];

    /// <summary>
    /// Whether the face is flat: every corner normal equals the face normal.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="smoothingThreshold">
    /// The cosine <c>-smooth</c> produced. A value of exactly 1 disables phong
    /// entirely and makes every face flat.
    /// </param>
    /// <returns><c>lightinfo_t::isflat</c>.</returns>
    /// <remarks>
    /// <c>InitLightinfo</c>. The tolerance is
    /// <c>EQUAL_EPSILON</c>, 0.001, against the DOT PRODUCT and not against an
    /// angle, so a corner bent by up to about 2.6 degrees still counts as flat.
    /// </remarks>
    public bool IsFlat(int faceNum, float smoothingThreshold)
    {
        if (smoothingThreshold == 1f)
        {
            return true;
        }

        Vec3 faceNormal = _faceNormals[faceNum];
        foreach (Vec3 corner in _cornerNormals[faceNum])
        {
            // `dot < 1.0 - EQUAL_EPSILON`, a double comparison.
            if (Vec3.Dot(faceNormal, corner) < 1.0 - LightConstants.EqualEpsilonDouble)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs <c>PairEdges</c> over a map.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="smoothingThreshold">
    /// The cosine of the smoothing angle: stock's <c>smoothing_threshold</c>,
    /// default <c>cos(45 deg)</c> = 0.7071067.
    /// </param>
    /// <returns>The pairing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A face has more than <see cref="MaxNeighbours"/> neighbours.
    /// </exception>
    public static FaceNeighbours Build(LightGeometry geometry, float smoothingThreshold)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        DFace[] faces = geometry.Faces;
        int faceCount = faces.Length;

        // In three passes: count references per vertex,
        // allocate, then fill with a linear dedup. The dedup matters -- a face
        // that touches the same vertex twice (a degenerate winding vbsp did not
        // collapse) would otherwise appear twice in the list and be smoothed in
        // twice.
        int[] vertexRef = new int[geometry.Vertexes.Length];
        for (int i = 0; i < faceCount; i++)
        {
            for (int j = 0; j < faces[i].NumEdges; j++)
            {
                vertexRef[geometry.EdgeVertex(i, j)]++;
            }
        }

        int[][] vertexFace = new int[vertexRef.Length][];
        for (int v = 0; v < vertexRef.Length; v++)
        {
            vertexFace[v] = new int[vertexRef[v]];
            vertexRef[v] = 0;
        }

        for (int i = 0; i < faceCount; i++)
        {
            for (int j = 0; j < faces[i].NumEdges; j++)
            {
                int n = geometry.EdgeVertex(i, j);
                int k = 0;
                while (k < vertexRef[n] && vertexFace[n][k] != i)
                {
                    k++;
                }

                if (k >= vertexRef[n])
                {
                    vertexFace[n][k] = i;
                    vertexRef[n]++;
                }
            }
        }

        // Face normals and the displacement flag, both needed by the
        // neighbour walk below, so they are a separate pass over every face.
        Vec3[] faceNormals = new Vec3[faceCount];
        bool[] hasDisp = new bool[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            faceNormals[i] = geometry.Planes[faces[i].PlaneNum].Normal;
            hasDisp[i] = geometry.IsValidDispFace(i);
        }

        Vec3[][] cornerNormals = new Vec3[faceCount][];
        int[][] neighbours = new int[faceCount][];
        int[] scratch = new int[MaxNeighbours];

        for (int i = 0; i < faceCount; i++)
        {
            int numEdges = faces[i].NumEdges;
            Vec3[] normals = new Vec3[numEdges];
            int numNeighbours = 0;

            for (int j = 0; j < numEdges; j++)
            {
                int n = geometry.EdgeVertex(i, j);

                for (int k = 0; k < vertexRef[n]; k++)
                {
                    int other = vertexFace[n][k];
                    if (other == i)
                    {
                        continue;
                    }

                    // A brush face never smooths with a displacement;
                    // the reverse is allowed by rule 1 below.
                    if (!hasDisp[i] && hasDisp[other])
                    {
                        continue;
                    }

                    Vec3 neighbourNormal = faceNormals[other];

                    if (hasDisp[i])
                    {
                        // Rule 1 (:247-251): always smooth.
                        normals[j] += neighbourNormal;
                    }
                    else if (faces[i].SmoothingGroups == 0
                        && faces[other].SmoothingGroups == 0)
                    {
                        // Rule 2 (:255-266). `cos_normals_angle` is declared
                        // double (:231) but is ASSIGNED a float: DotProduct on two
                        // Vectors is vec_t arithmetic, so the
                        // product is rounded to float first and only then widened.
                        // The comparison against the float threshold therefore
                        // sees no extra bits.
                        double cos = Vec3.Dot(neighbourNormal, faceNormals[i]);
                        if (cos < smoothingThreshold)
                        {
                            continue;
                        }

                        normals[j] += neighbourNormal;
                    }
                    else
                    {
                        // Rule 3 (:269-283).
                        uint shared = faces[i].SmoothingGroups & faces[other].SmoothingGroups;
                        if ((shared & HardEdgeGroups) != 0 || shared == 0)
                        {
                            continue;
                        }

                        normals[j] += neighbourNormal;
                    }

                    int m = 0;
                    while (m < numNeighbours && scratch[m] != other)
                    {
                        m++;
                    }

                    if (m >= numNeighbours)
                    {
                        if (numNeighbours >= MaxNeighbours)
                        {
                            throw new InvalidOperationException(
                                $"Face {i} has more than {MaxNeighbours} smoothing neighbours; "
                                + "stock vrad raises \"Stack overflow in neighbors\" here.");
                        }

                        scratch[m] = other;
                        numNeighbours++;
                    }
                }
            }

            neighbours[i] = numNeighbours == 0 ? [] : scratch[..numNeighbours];

            // The face's OWN normal is added last and once, whatever
            // the neighbour count -- so a corner with no smoothing neighbours
            // normalises to the face normal exactly, and a corner with three
            // neighbours is the average of four vectors rather than three.
            for (int j = 0; j < numEdges; j++)
            {
                normals[j] = BumpBasis.Normalise(
                    normals[j] + faceNormals[i],
                    geometry.StockNormalise);
            }

            cornerNormals[i] = normals;
        }

        return new FaceNeighbours(faceNormals, cornerNormals, neighbours, hasDisp);
    }
}
