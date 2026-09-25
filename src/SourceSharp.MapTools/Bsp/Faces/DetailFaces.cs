using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Building faces for detail brushes and folding them into the world tree
/// </summary>
/// <remarks>
/// <para>
/// A detail brush is one the world BSP was built WITHOUT, so that it cannot
/// split the world's leaves and inflate the vis set. That leaves two problems
/// this file solves. Its visible surfaces have to be found without a tree —
/// <c>ComputeVisibleBrushSides</c> does it by clipping every brush side
/// against every other detail brush that could overlap it — and its geometry
/// has to be filtered into the world's leaves afterwards, so the engine knows
/// which leaves to draw it in.
/// </para>
/// <para>
/// <b>Detail faces are never fragmented.</b> When a face clips into more than
/// one visible piece, <c>ComputeVisibleBrushSides</c> throws the pieces away
/// and keeps the whole face, with a comment saying
/// a 2D convex hull would be the real fix. So a detail surface partly hidden
/// behind another detail brush is drawn whole, overdraw and all.
/// </para>
/// </remarks>
public sealed class DetailFaces
{
    private readonly FaceBuildContext _context;
    private readonly BspBuildContext _build;
    private readonly IReadOnlyList<MapBrushSide> _brushSides;

    /// <summary>Creates the detail stage.</summary>
    /// <param name="context">The face stage's state.</param>
    /// <param name="build">The CSG stage's state, for brush allocation and splitting.</param>
    /// <param name="brushSides">The map's shared brush side list.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DetailFaces(
        FaceBuildContext context,
        BspBuildContext build,
        IReadOnlyList<MapBrushSide> brushSides)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(brushSides);

        _context = context;
        _build = build;
        _brushSides = brushSides;
    }

    /// <summary>
    /// Whether two brushes' boxes overlap
    /// (<c>BrushBoxOverlap</c>).
    /// </summary>
    /// <param name="p1">One brush.</param>
    /// <param name="p2">The other.</param>
    /// <returns>False when they cannot possibly intersect, including when they are the same brush.</returns>
    public static bool BrushBoxOverlap(BspBrush p1, BspBrush p2)
    {
        ArgumentNullException.ThrowIfNull(p1);
        ArgumentNullException.ThrowIfNull(p2);

        if (ReferenceEquals(p1, p2))
        {
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (p1.Mins[i] > p2.Maxs[i] || p1.Maxs[i] < p2.Mins[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Counts the faces in a list that have not been split
    /// (<c>CountFaceList</c>).
    /// </summary>
    /// <param name="head">The list.</param>
    /// <returns>How many are still whole.</returns>
    /// <remarks>
    /// It tests <c>split[0]</c> only, not <c>merged</c> and not
    /// <c>split[1]</c>, because this list is built by the clipper and
    /// <c>split[0] = self</c> is the only mark the clipper uses.
    /// </remarks>
    public static int CountFaceList(Face? head)
    {
        int count = 0;

        for (Face? f = head; f is not null; f = f.Next)
        {
            if (f.Split[0] is not null)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// The map brush side a BSP brush side came from
    /// (<c>FindOriginalSide</c>).
    /// </summary>
    /// <param name="brush">The original map brush.</param>
    /// <param name="planeNumber">The BSP side's plane number.</param>
    /// <returns>The map side with the same plane, or the closest-facing visible one.</returns>
    /// <exception cref="InvalidOperationException">The brush had no usable side at all.</exception>
    public MapBrushSide FindOriginalSide(MapBrush brush, int planeNumber)
    {
        ArgumentNullException.ThrowIfNull(brush);

        MapBrushSide? best = null;
        float bestDot = 0f;

        Plane p1 = _context.Planes[planeNumber];

        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = _brushSides[brush.FirstSide + i];

            if (side.Bevel)
            {
                continue;
            }

            if (side.TexInfo == TexInfoTable.TexInfoNode)
            {
                continue;       // non-visible
            }

            if ((side.PlaneNumber & ~1) == (planeNumber & ~1))
            {
                return side;    // exact match
            }

            // see how close the match is
            Plane p2 = _context.Planes[side.PlaneNumber & ~1];
            float dot = Vec3.Dot(p1.Normal, p2.Normal);

            if (dot > bestDot)
            {
                bestDot = dot;
                best = side;
            }
        }

        return best ?? throw new InvalidOperationException("Bad detail brush side");
    }

    /// <summary>
    /// Makes a face from a brush side and a winding
    /// (<c>MakeBrushFace</c>).
    /// </summary>
    /// <param name="originalSide">The map side the face is textured from.</param>
    /// <param name="winding">The polygon, which is copied.</param>
    /// <returns>The face.</returns>
    public Face MakeBrushFace(MapBrushSide originalSide, Winding winding)
    {
        ArgumentNullException.ThrowIfNull(originalSide);

        Face face = _context.Faces.Alloc();
        face.Winding = _context.Windings.Copy(winding);
        face.OriginalFace = originalSide;
        face.TexInfo = originalSide.TexInfo;
        face.DispInfo = -1;
        face.PlaneNumber = originalSide.PlaneNumber;
        face.Contents = originalSide.Contents;
        return face;
    }

    /// <summary>
    /// Clips a face against every side of a brush
    /// (<c>ClipFaceToBrush</c>).
    /// </summary>
    /// <param name="face">The face to clip. It is not modified.</param>
    /// <param name="brush">The brush to clip against.</param>
    /// <param name="output">The visible fragments, or null.</param>
    /// <returns>True when the brush clipped the whole face away.</returns>
    /// <remarks>
    /// <para>
    /// Only brushes that have a side on the face's own plane are considered —
    /// <c>foundSide</c> — so this is really "is this face's surface buried
    /// inside that brush". The axial sides are clipped FIRST (they are moved to
    /// the head of <c>sortedSides</c>), which costs nothing and throws away
    /// most of the winding before the slanted sides are reached.
    /// </para>
    /// <para>
    /// The early break when a plane leaves nothing behind it FREES the
    /// fragments collected so far, because a face that is in front of any one
    /// side of the brush is not inside the brush at all.
    /// </para>
    /// </remarks>
    public bool ClipFaceToBrush(Face face, BspBrush brush, out Face? output)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(brush);

        output = null;

        int planeNumber = face.PlaneNumber & ~1;
        int foundSide = -1;

        for (int i = 0; i < brush.SideCount && foundSide < 0; i++)
        {
            if ((brush.Sides[i].PlaneNumber & ~1) == planeNumber)
            {
                foundSide = i;
            }
        }

        if (foundSide < 0)
        {
            return false;
        }

        Vec3 offset = (brush.Maxs + brush.Mins) * -0.5f;
        Face current = _context.Faces.CopyFace(face);

        List<int> sortedSides = [];

        for (int i = 0; i < brush.SideCount; i++)
        {
            // don't clip to bevels
            if (brush.Sides[i].Bevel)
            {
                continue;
            }

            if (_context.Planes[brush.Sides[i].PlaneNumber].Type <= PlaneType.Z)
            {
                sortedSides.Insert(0, i);
            }
            else
            {
                sortedSides.Add(i);
            }
        }

        int index;

        for (index = 0; index < sortedSides.Count; index++)
        {
            int side = sortedSides[index];

            if (side == foundSide)
            {
                continue;
            }

            Plane plane = _context.Planes[brush.Sides[side].PlaneNumber];

            _context.Windings.ClipEpsilonOffset(
                current.Winding, plane.Normal, plane.Dist, 0.001f, offset,
                out Winding front, out Winding back);

            // only clip if some part of this face is on the back side of all
            // brush sides
            if (back.IsNull || BrushGeometry.WindingIsTiny(_build, back))
            {
                _context.Faces.FreeList(output);
                output = null;
                break;
            }

            if (!front.IsNull && !BrushGeometry.WindingIsTiny(_build, front))
            {
                // add this fragment to the return list
                Face fragment = _context.Faces.NewFaceFromFace(face);
                fragment.Winding = front;
                fragment.Next = output;
                output = fragment;
            }

            // update the current winding to be the part behind each plane
            _context.Windings.Free(current.Winding);
            current.Winding = back;
        }

        // free the bit that is left in solid or not clipped
        _context.Faces.Free(current);

        // if we made it all the way through and didn't produce any fragments
        // then the whole face was clipped away
        return output is null && index == sortedSides.Count;
    }

    /// <summary>
    /// The brushes that could cut a source brush's faces
    /// (<c>GetListOfCutBrushes</c>).
    /// </summary>
    /// <param name="source">The brush whose faces are being cut.</param>
    /// <param name="list">Every detail brush.</param>
    /// <returns>The candidates, in list order.</returns>
    public static List<BspBrush> GetListOfCutBrushes(BspBrush source, BspBrush? list)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<BspBrush> output = [];
        MapBrush original = source.Original!;

        for (BspBrush? walk = list; walk is not null; walk = walk.Next)
        {
            if (ReferenceEquals(walk, source))
            {
                continue;
            }

            // only clip to transparent brushes if the original brush is transparent
            if ((walk.Original!.Contents & BrushCsg.TransparentContents) != 0
                && (original.Contents & BrushCsg.TransparentContents) == 0)
            {
                continue;
            }

            // don't clip to clip brushes, etc.
            if ((walk.Original.Contents & MapFileLoader.AllVisibleContents) == 0)
            {
                continue;
            }

            if (!BrushBoxOverlap(source, walk))
            {
                continue;
            }

            output.Add(walk);
        }

        return output;
    }

    // GetListOfCutBrushes for every brush of one list, which does not change
    // while ComputeVisibleBrushSides walks it: the list laid out once, in its
    // order, so each query is a walk over contiguous boxes rather than a
    // pointer chase through every brush (plan 3p: the O(n^2) scan was 6% of a
    // goldrush compile). Same tests, same order, same output as
    // GetListOfCutBrushes.
    internal sealed class CutBrushScan
    {
        private readonly BspBrush[] _brushes;
        private readonly float[] _box;
        private readonly int[] _contents;

        public CutBrushScan(BspBrush? list)
        {
            List<BspBrush> brushes = [];
            for (BspBrush? b = list; b is not null; b = b.Next)
            {
                brushes.Add(b);
            }

            _brushes = [.. brushes];
            _box = new float[_brushes.Length * 6];
            _contents = new int[_brushes.Length];
            for (int i = 0; i < _brushes.Length; i++)
            {
                BspBrush b = _brushes[i];
                _box[(i * 6) + 0] = b.Mins.X;
                _box[(i * 6) + 1] = b.Mins.Y;
                _box[(i * 6) + 2] = b.Mins.Z;
                _box[(i * 6) + 3] = b.Maxs.X;
                _box[(i * 6) + 4] = b.Maxs.Y;
                _box[(i * 6) + 5] = b.Maxs.Z;
                _contents[i] = b.Original!.Contents;
            }
        }

        public List<BspBrush> CutBrushesOf(int source)
        {
            List<BspBrush> output = [];
            ReadOnlySpan<float> box = _box;
            int s = source * 6;
            float minX = box[s], minY = box[s + 1], minZ = box[s + 2];
            float maxX = box[s + 3], maxY = box[s + 4], maxZ = box[s + 5];
            bool sourceTransparent = (_contents[source] & BrushCsg.TransparentContents) != 0;

            for (int i = 0; i < _brushes.Length; i++)
            {
                if (i == source)
                {
                    continue;
                }

                // only clip to transparent brushes if the original brush is transparent
                int contents = _contents[i];
                if ((contents & BrushCsg.TransparentContents) != 0 && !sourceTransparent)
                {
                    continue;
                }

                // don't clip to clip brushes, etc.
                if ((contents & MapFileLoader.AllVisibleContents) == 0)
                {
                    continue;
                }

                // BrushBoxOverlap, the source as p1.
                int w = i * 6;
                if (minX > box[w + 3] || maxX < box[w]
                    || minY > box[w + 4] || maxY < box[w + 1]
                    || minZ > box[w + 5] || maxZ < box[w + 2])
                {
                    continue;
                }

                output.Add(_brushes[i]);
            }

            return output;
        }
    }

    /// <summary>
    /// Clips one face against a list of brushes
    /// (<c>ClipFaceToBrushList</c>).
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="cutBrushes">The brushes that might cut it.</param>
    /// <returns>The fragment list, or null when nothing clipped it.</returns>
    public Face? ClipFaceToBrushList(Face face, IReadOnlyList<BspBrush> cutBrushes)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(cutBrushes);

        if (face.Split[0] is not null)
        {
            return null;
        }

        Face clipList = _context.Faces.CopyFace(face);
        clipList.Next = null;
        bool clipped = false;

        foreach (BspBrush cut in cutBrushes)
        {
            for (Face? cutFace = clipList; cutFace is not null; cutFace = cutFace.Next)
            {
                // already split, no need to clip
                if (cutFace.Split[0] is not null)
                {
                    continue;
                }

                if (ClipFaceToBrush(cutFace, cut, out Face? pieces))
                {
                    clipped = true;

                    // mark face bad, the brush clipped it away
                    cutFace.Split[0] = cutFace;
                }
                else if (pieces is not null)
                {
                    clipped = true;
                    cutFace.Split[0] = cutFace;

                    // insert face fragments at head of list
                    while (pieces is not null)
                    {
                        Face? next = pieces.Next;
                        pieces.Next = clipList;
                        clipList = pieces;
                        pieces = next;
                    }
                }
            }
        }

        if (clipped)
        {
            return clipList;
        }

        _context.Faces.FreeList(clipList);
        return null;
    }

    /// <summary>
    /// The visible faces of a list of already-chopped detail brushes
    /// (<c>ComputeVisibleBrushSides</c>).
    /// </summary>
    /// <param name="list">The chopped detail brushes.</param>
    /// <returns>The face list.</returns>
    public Face? ComputeVisibleBrushSides(BspBrush? list)
    {
        Face? total = null;
        CutBrushScan scan = new(list);
        int index = -1;

        for (BspBrush? brush = list; brush is not null; brush = brush.Next)
        {
            index++;
            Face? faces = null;
            MapBrush original = brush.Original!;

            if ((original.Contents & MapFileLoader.AllVisibleContents) == 0)
            {
                continue;
            }

            // Make a face for each brush side, then clip it by the other
            // details to see if any fragments are visible
            for (int i = 0; i < brush.SideCount; i++)
            {
                Winding winding = brush.Sides[i].Winding;

                if (winding.IsNull)
                {
                    continue;
                }

                if ((brush.Sides[i].Contents & MapFileLoader.AllVisibleContents) == 0)
                {
                    continue;
                }

                MapBrushSide side = FindOriginalSide(original, brush.Sides[i].PlaneNumber);
                Face face = MakeBrushFace(side, winding);

                face.Next = faces;
                faces = face;
            }

            List<BspBrush> cutBrushes = scan.CutBrushesOf(index);

            if (cutBrushes.Count > 0)
            {
                for (Face? f = faces; f is not null; f = f.Next)
                {
                    Face? pieces = ClipFaceToBrushList(f, cutBrushes);

                    if (pieces is null)
                    {
                        continue;
                    }

                    if (CountFaceList(pieces) <= 1)
                    {
                        // was removed or cut down, mark as split
                        f.Split[0] = f;

                        while (pieces is not null)
                        {
                            Face? next = pieces.Next;
                            pieces.Next = faces;
                            faces = pieces;
                            pieces = next;
                        }
                    }
                    else
                    {
                        // it cut into more than one visible fragment.
                        // Don't fragment details.
                        _context.Faces.FreeList(pieces);
                    }
                }
            }

            // move visible fragments to global face list
            while (faces is not null)
            {
                Face? next = faces.Next;

                if (faces.Split[0] is not null)
                {
                    _context.Faces.Free(faces);
                }
                else
                {
                    faces.Next = total;
                    total = faces;
                }

                faces = next;
            }
        }

        return total;
    }

    /// <summary>
    /// Merges coplanar detail faces, bucketed by plane
    /// (<c>TryMergeFaceList</c>).
    /// </summary>
    /// <param name="head">The face list.</param>
    /// <returns>The list afterwards, reordered by plane.</returns>
    /// <exception cref="InvalidOperationException">A dead face was in the list.</exception>
    /// <remarks>
    /// The bucketing is what makes this different from
    /// <c>MergeFaceList</c> on a node: there is no node here, so a detail face
    /// could be adjacent to any other with the same plane anywhere in the map.
    /// The output order is therefore the plane table's, not the input's.
    /// </remarks>
    public Face? TryMergeFaceList(Face? head)
    {
        Face?[] byPlane = new Face?[_context.Planes.Count];

        Face? faces = head;
        Face? output = null;

        while (faces is not null)
        {
            Face? next = faces.Next;

            if (faces.IsDead)
            {
                throw new InvalidOperationException("Split face in merge list!");
            }

            faces.Next = byPlane[faces.PlaneNumber];
            byPlane[faces.PlaneNumber] = faces;

            faces = next;
        }

        FaceMerger merger = new(_context);

        for (int i = 0; i < byPlane.Length; i++)
        {
            if (byPlane[i] is not null)
            {
                byPlane[i] = merger.MergeFaceList(byPlane[i]);
            }

            Face? list = byPlane[i];

            while (list is not null)
            {
                Face? next = list.Next;
                list.Next = output;
                output = list;
                list = next;
            }
        }

        return output;
    }

    /// <summary>
    /// Filters a brush fragment down to the leaves it falls in
    /// (<c>MergeBrush_r</c>).
    /// </summary>
    /// <param name="node">The subtree root.</param>
    /// <param name="brush">The fragment, which is consumed.</param>
    public void MergeBrushRecursive(IBspNode node, BspBrush brush)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(brush);

        if (node.IsLeaf())
        {
            if ((node.Contents & PortalContents.Solid) != 0)
            {
                _build.FreeBrush(brush);
            }
            else
            {
                _context.Lists.AddBrushToLeaf(node, brush);
            }

            return;
        }

        BrushGeometry.SplitBrush(_build, brush, node.PlaneNumber, out BspBrush? front, out BspBrush? back);
        _build.FreeBrush(brush);

        if (front is not null)
        {
            MergeBrushRecursive(node.Front!, front);
        }

        if (back is not null)
        {
            MergeBrushRecursive(node.Back!, back);
        }
    }

    /// <summary>
    /// Filters a face down to the leaves it falls in, leaving a reference to
    /// the whole face in each(<c>MergeFace_r</c>).
    /// </summary>
    /// <param name="node">The subtree root.</param>
    /// <param name="face">The clipped fragment, which is consumed.</param>
    /// <param name="original">The unclipped face the leaves reference.</param>
    /// <returns>True when any leaf took a reference.</returns>
    /// <remarks>
    /// A fragment exactly in the split plane goes down the side the FACE
    /// faces, not the side the fragment is on — there is no such side. That is
    /// the <c>onwinding</c> branch, and it is why a detail face lying flat on a
    /// world plane lands in the leaf in front of it rather than in both.
    /// </remarks>
    public bool MergeFaceRecursive(IBspNode node, Face face, Face original)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(original);

        bool referenced = false;

        if (node.IsLeaf())
        {
            if ((node.Contents & PortalContents.Solid) != 0)
            {
                _context.Faces.Free(face);
                return false;
            }

            _context.Lists.AddLeafFace(node, original);
            referenced = true;
        }
        else
        {
            Plane plane = _context.Planes[node.PlaneNumber];
            Vec3 offset = _context.Windings.Center(face.Winding);

            _context.Windings.ClassifyEpsilonOffset(
                face.Winding, plane.Normal, plane.Dist, 0.001f, -offset,
                out Winding front, out Winding back, out Winding on);

            if (!on.IsNull)
            {
                // face is in the split plane, go down the appropriate side
                // according to the facing direction
                if (Vec3.Dot(_context.Planes[face.PlaneNumber].Normal, plane.Normal) > 0f)
                {
                    front = on;
                }
                else
                {
                    back = on;
                }
            }

            if (!front.IsNull)
            {
                Face fragment = _context.Faces.NewFaceFromFace(face);
                fragment.Winding = front;
                referenced = MergeFaceRecursive(node.Front!, fragment, original);
            }

            if (!back.IsNull)
            {
                Face fragment = _context.Faces.NewFaceFromFace(face);
                fragment.Winding = back;
                bool test = MergeFaceRecursive(node.Back!, fragment, original);
                referenced = referenced || test;
            }
        }

        _context.Faces.Free(face);

        return referenced;
    }

    /// <summary>
    /// Filters every detail face into the tree
    /// (<c>FilterFacesIntoTree</c>).
    /// </summary>
    /// <param name="headNode">The world tree's root.</param>
    /// <param name="faces">The detail faces.</param>
    /// <returns>The faces that landed in at least one visible leaf.</returns>
    public Face? FilterFacesIntoTree(IBspNode headNode, Face? faces)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        Face? leafFaceList = null;

        for (Face? f = faces; f is not null; f = f.Next)
        {
            if (f.IsDead)
            {
                continue;
            }

            Face clipped = _context.Faces.CopyFace(f);
            Face original = _context.Faces.CopyFace(f);

            if (MergeFaceRecursive(headNode, clipped, original))
            {
                // clear out portal (comes from a different tree)
                original.Portal = null;
                original.Next = leafFaceList;
                leafFaceList = original;
            }
            else
            {
                _context.Faces.Free(original);
            }
        }

        return leafFaceList;
    }

    /// <summary>
    /// Filters every detail brush into the tree
    /// (<c>FilterBrushesIntoTree</c>).
    /// </summary>
    /// <param name="headNode">The world tree's root.</param>
    /// <param name="brushes">The detail brushes.</param>
    public void FilterBrushesIntoTree(IBspNode headNode, BspBrush? brushes)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        for (BspBrush? brush = brushes; brush is not null; brush = brush.Next)
        {
            MergeBrushRecursive(headNode, BrushGeometry.CopyBrush(_build, brush));
        }
    }

    /// <summary>
    /// The whole detail pass for one model
    /// (<c>MergeDetailTree</c>).
    /// </summary>
    /// <param name="headNode">The world tree's root.</param>
    /// <param name="brushStart">The model's first brush index.</param>
    /// <param name="brushEnd">One past its last.</param>
    /// <param name="mapMins">The map's lower bound.</param>
    /// <param name="mapMaxs">The map's upper bound.</param>
    /// <returns>The leaf face list, which <c>FixTjuncs</c> takes next.</returns>
    public Face? MergeDetailTree(
        IBspNode headNode,
        int brushStart,
        int brushEnd,
        Vec3 mapMins,
        Vec3 mapMaxs)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        BspBrush? detail = BrushCsg.MakeBspBrushList(
            _build, brushStart, brushEnd, mapMins, mapMaxs, DetailScreen.OnlyDetail);

        if (detail is null)
        {
            return null;
        }

        // if there are detail brushes, chop them against each other
        if (!_context.Options.NoCsg)
        {
            detail = BrushCsg.ChopBrushes(_build, detail);
        }

        // Now mark the visible sides so we can eliminate all detail brush sides
        // that are covered by other detail brush sides. NOTE: This still leaves
        // detail brush sides that are covered by the world -- those are removed
        // by the merge below.
        Face? faces = ComputeVisibleBrushSides(detail);
        faces = TryMergeFaceList(faces);

        FaceSubdivider subdivider = new(_context);
        faces = subdivider.SubdivideFaceList(faces);

        // Merge the detail solids and faces into the world tree
        Face? leafFaceList = FilterFacesIntoTree(headNode, faces);
        FilterBrushesIntoTree(headNode, detail);

        _context.Faces.FreeList(faces);
        _build.FreeBrushList(detail);

        return leafFaceList;
    }
}
