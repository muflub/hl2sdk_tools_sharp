using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// One face of the BSP, from the moment a portal becomes one until it is
/// written (<c>face_t</c>, <c>src/utils/vbsp/vbsp.h:102</c>).
/// </summary>
/// <remarks>
/// <para>
/// A face is never removed from the list it is on. <see cref="Merged"/> and
/// <see cref="Split"/> are how a face stops being real: merging two faces
/// leaves both originals in the chain with <see cref="Merged"/> pointing at the
/// new one, and subdividing a face leaves it in the chain with its two halves
/// in <see cref="Split"/>. Stock's comment says so and every stage re-checks
/// the three fields before touching a face. That is why the port keeps the
/// linked list rather than a <c>List&lt;Face&gt;</c>: the identity of a dead
/// face is load-bearing, and so is its position.
/// </para>
/// <para>
/// <b>The winding and the vertex list are two different representations of the
/// same polygon, live at different times.</b> <see cref="Winding"/> holds
/// points and is what merging and subdivision work on;
/// <see cref="VertexNumbers"/> holds indices into the welded vertex table and
/// is what <c>FixTjuncs</c> produces and <c>WriteBSP</c> consumes. Between
/// <c>EmitFaceVertexes</c> and <c>FixFaceEdges</c> both are valid and they can
/// disagree in length, because a t-junction adds vertices the winding never
/// had.
/// </para>
/// </remarks>
public sealed class Face : IBspFace
{
    /// <summary>
    /// <c>MAXEDGES</c> (<c>vbsp.h:100</c>): the most vertices one face may
    /// carry before <c>FaceFromSuperverts</c> fragments it.
    /// </summary>
    public const int MaxEdges = 32;

    private readonly int[] _vertexNumbers = new int[MaxEdges];
    private readonly Face?[] _split = new Face?[2];

    /// <summary>Creates a zeroed face with the given identity.</summary>
    /// <param name="id">The value <c>AllocFace</c> would have stamped on it.</param>
    public Face(int id)
    {
        Id = id;
        DispInfo = 0;
        Winding = Winding.Null;
    }

    /// <summary>
    /// <c>id</c>: the value <c>AllocFace</c>'s running counter held.
    /// </summary>
    /// <remarks>
    /// <b>Not unique.</b> <c>NewFaceFromFace</c> allocates a face — which
    /// stamps a fresh id — and then does <c>*newf = *f</c>, which copies the
    /// source's id straight back over it. So every face descended from one
    /// original shares its id, and the counter runs ahead of the ids in use.
    /// That is arbitrary rather than wrong (nothing but diagnostics reads it),
    /// so <see cref="FaceAllocator"/> reproduces it unconditionally.
    /// </remarks>
    public int Id { get; set; }

    /// <summary><c>next</c>: the next face on the same node.</summary>
    public Face? Next { get; set; }

    /// <summary>
    /// <c>merged</c>: the face this one was merged into, which makes this one
    /// dead.
    /// </summary>
    public Face? Merged { get; set; }

    /// <summary>
    /// <c>split[2]</c>: the two faces this one was cut into, which makes this
    /// one dead.
    /// </summary>
    /// <remarks>
    /// <c>ClipFaceToBrushList</c> (<c>detail.cpp:571</c>) also uses
    /// <c>split[0] = self</c> as a "this fragment was clipped away" marker, so
    /// a face can be its own split without a second half.
    /// </remarks>
    public Span<Face?> Split => _split;

    /// <summary><c>portal</c>: the portal this face was made from, if any.</summary>
    public Portal? Portal { get; set; }

    /// <summary><c>texinfo</c>: index into the compile's texinfo table.</summary>
    public int TexInfo { get; set; }

    /// <summary><c>dispinfo</c>: index into the displacement table, or -1.</summary>
    public int DispInfo { get; set; }

    /// <summary>
    /// <c>fogVolumeLeaf</c>: the water leaf this face bounds, for faces that
    /// are the boundary of a fog volume.
    /// </summary>
    public IBspNode? FogVolumeLeaf { get; set; }

    /// <summary><c>planenum</c>: index into the map's plane table.</summary>
    public int PlaneNumber { get; set; }

    /// <summary><c>contents</c>: faces in different contents never merge.</summary>
    public int Contents { get; set; }

    /// <summary><c>outputnumber</c>: the index this face got in LUMP_FACES.</summary>
    public int OutputNumber { get; set; }

    /// <summary><c>w</c>: the face's polygon.</summary>
    public Winding Winding { get; set; }

    /// <summary><c>numpoints</c>: how many of <see cref="VertexNumbers"/> are live.</summary>
    public int NumPoints { get; set; }

    /// <summary>
    /// <c>badstartvert</c>: no vertex of this face had a t-junction-free edge
    /// on both sides, so a trifan over it would show cracks.
    /// </summary>
    public bool BadStartVert { get; set; }

    /// <summary><c>vertexnums[MAXEDGES]</c>: the welded vertex indices.</summary>
    public Span<int> VertexNumbers => _vertexNumbers;

    /// <summary><c>originalface</c>: the brush side this face came from.</summary>
    public MapBrushSide? OriginalFace { get; set; }

    /// <summary><c>firstPrimID</c>: index of this face's first primitive.</summary>
    public int FirstPrimId { get; set; }

    /// <summary><c>numPrims</c>: how many primitives this face has.</summary>
    public int NumPrims { get; set; }

    /// <summary><c>smoothingGroups</c>: the phong mask vrad reads.</summary>
    public uint SmoothingGroups { get; set; }

    /// <summary>
    /// Whether any of the three "this face is dead" fields is set:
    /// <c>f-&gt;merged || f-&gt;split[0] || f-&gt;split[1]</c>.
    /// </summary>
    public bool IsDead => Merged is not null || _split[0] is not null || _split[1] is not null;

    /// <inheritdoc/>
    IBspFace? IBspFace.Next
    {
        get => Next;
        set => Next = (Face?)value;
    }

    /// <summary>
    /// Copies every field of another face over this one, as stock's
    /// <c>*newf = *f</c>.
    /// </summary>
    /// <param name="source">The face to copy.</param>
    /// <remarks>
    /// Struct assignment in C copies everything, <see cref="Id"/> and
    /// <see cref="Next"/> included. Both are reproduced: the id because
    /// <see cref="Id"/>'s remarks explain it, and <see cref="Next"/> because
    /// <c>FaceFromSuperverts</c> and <c>SubdivideFace</c> both rely on the
    /// copy's link being overwritten immediately afterwards while
    /// <c>TryMerge</c> does not.
    /// </remarks>
    public void CopyFrom(Face source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        Next = source.Next;
        Merged = source.Merged;
        _split[0] = source._split[0];
        _split[1] = source._split[1];
        Portal = source.Portal;
        TexInfo = source.TexInfo;
        DispInfo = source.DispInfo;
        FogVolumeLeaf = source.FogVolumeLeaf;
        PlaneNumber = source.PlaneNumber;
        Contents = source.Contents;
        OutputNumber = source.OutputNumber;
        Winding = source.Winding;
        NumPoints = source.NumPoints;
        BadStartVert = source.BadStartVert;
        source._vertexNumbers.AsSpan().CopyTo(_vertexNumbers);
        OriginalFace = source.OriginalFace;
        FirstPrimId = source.FirstPrimId;
        NumPrims = source.NumPrims;
        SmoothingGroups = source.SmoothingGroups;
    }
}

/// <summary>
/// One entry of a leaf's list of faces that overlap it
/// (<c>leafface_t</c>, <c>src/utils/vbsp/vbsp.h:193</c>).
/// </summary>
/// <remarks>
/// A separate node type rather than a second <c>next</c> on <see cref="Face"/>
/// because the same detail face is referenced from every leaf its fragments
/// fell into — <c>MergeFace_r</c> (<c>detail.cpp:96</c>) leaves one of these in
/// each — so the face cannot own the link.
/// </remarks>
public sealed class LeafFace
{
    /// <summary>Creates a reference to a face.</summary>
    /// <param name="face">The face this leaf overlaps.</param>
    /// <param name="next">The rest of the leaf's list.</param>
    public LeafFace(Face face, LeafFace? next)
    {
        Face = face;
        Next = next;
    }

    /// <summary><c>pFace</c>: the referenced face.</summary>
    public Face Face { get; }

    /// <summary><c>pNext</c>: the next reference on the same leaf.</summary>
    public LeafFace? Next { get; set; }
}
