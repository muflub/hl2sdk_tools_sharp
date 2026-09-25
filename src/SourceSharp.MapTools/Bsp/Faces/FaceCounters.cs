namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The running counts keeps in file-scope globals and prints
/// under <c>-v</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the stage's observable output.</b> Everything else the face
/// stage produces reaches the BSP only after <c>WriteBSP</c> has mixed in
/// displacements, brush models and the original-face table, so the numbers
/// printed here are the only place stock states, by itself, what this stage
/// did. Every parity gate on Phase 3d is a comparison against these eleven
/// lines of a stock <c>-v</c> log.
/// </para>
/// <para>
/// The reset points are not uniform and are reproduced as they are:
/// <c>MakeFaces</c> zeroes three of them and
/// <c>FixTjuncs</c> zeroes six, while
/// <c>c_badstartverts</c> is zeroed by NEITHER — it is reset only by the
/// program starting, so the "%5i bad start verts" line of a map with several
/// brush models is cumulative across all of them.
/// </para>
/// </remarks>
public sealed class FaceCounters
{
    /// <summary><c>c_nodefaces</c>: "%5i makefaces".</summary>
    public int NodeFaces { get; set; }

    /// <summary><c>c_merge</c>: "%5i merged".</summary>
    public int Merged { get; set; }

    /// <summary><c>c_subdivide</c>: "%5i subdivided".</summary>
    public int Subdivided { get; set; }

    /// <summary><c>c_totalverts</c>: the second number of "%i unique from %i".</summary>
    public int TotalVerts { get; set; }

    /// <summary><c>c_uniqueverts</c>: the first number of "%i unique from %i".</summary>
    public int UniqueVerts { get; set; }

    /// <summary><c>c_degenerate</c>: "%5i edges degenerated".</summary>
    public int DegenerateEdges { get; set; }

    /// <summary><c>c_facecollapse</c>: "%5i faces degenerated".</summary>
    public int CollapsedFaces { get; set; }

    /// <summary><c>c_tjunctions</c>: "%5i edges added by tjunctions".</summary>
    public int TJunctions { get; set; }

    /// <summary><c>c_faceoverflows</c>: "%5i faces added by tjunctions".</summary>
    public int FaceOverflows { get; set; }

    /// <summary><c>c_badstartverts</c>: "%5i bad start verts". Never reset.</summary>
    public int BadStartVerts { get; set; }

    /// <summary><c>c_tryedges</c>: how many edges <c>GetEdge2</c> was asked for.</summary>
    public int TryEdges { get; set; }

    /// <summary>What <c>MakeFaces</c> zeroes before it runs.</summary>
    public void ResetForMakeFaces()
    {
        Merged = 0;
        Subdivided = 0;
        NodeFaces = 0;
    }

    /// <summary>
    /// What <c>FixTjuncs</c> zeroes: three before the weld and four before the
 /// T-junction pass.
    /// </summary>
    /// <remarks>
    /// <see cref="BadStartVerts"/> is deliberately absent. Stock does not reset
    /// it here or anywhere else after startup.
    /// </remarks>
    public void ResetForFixTjuncs()
    {
        TotalVerts = 0;
        UniqueVerts = 0;
        FaceOverflows = 0;

        TryEdges = 0;
        DegenerateEdges = 0;
        CollapsedFaces = 0;
        TJunctions = 0;
    }
}
