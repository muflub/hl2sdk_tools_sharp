using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Turning portals into faces (<c>MakeFaces</c>,
/// <c>src/utils/vbsp/faces.cpp:1307-1810</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every portal between two leaves whose contents differ visibly becomes one
/// face, drawn on the side the solid is NOT on, and hung on the node whose
/// split plane created the portal. The four cases stock lists in its comment
/// are the whole rule: solid/empty gives a solid face, solid/water gives a
/// solid face, water/empty gives a water face, and water/water gives nothing.
/// </para>
/// <para>
/// The recursion is post-order: both children are built before the node's own
/// list is merged and subdivided, because the faces on a node are put there by
/// the LEAVES below it, and all of them have to be present before merging can
/// see which ones are adjacent.
/// </para>
/// </remarks>
public sealed class FaceBuilder
{
    /// <summary>A water face whose material has no <c>$bottommaterial</c>.</summary>
    public const string MissingBottomMaterial = "VBSP0320";

    private readonly FaceBuildContext _context;
    private readonly FaceMerger _merger;
    private readonly FaceSubdivider _subdivider;

    /// <summary>Creates a face builder over one model's face stage.</summary>
    /// <param name="context">The stage's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public FaceBuilder(FaceBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _merger = new FaceMerger(context);
        _subdivider = new FaceSubdivider(context);
    }

    /// <summary>
    /// The face a portal makes when seen from one side, or null
    /// (<c>FaceFromPortal</c>, <c>faces.cpp:1307</c>).
    /// </summary>
    /// <param name="portal">The portal.</param>
    /// <param name="side">0 to look from the front node, 1 from the back.</param>
    /// <returns>The face, or null when this side draws nothing.</returns>
    public Face? FaceFromPortal(Portal portal, int side)
    {
        ArgumentNullException.ThrowIfNull(portal);

        // portal does not bridge different visible contents
        MapBrushSide? brushSide = portal.Side;

        if (brushSide is null)
        {
            return null;
        }

        Face face = _context.Faces.Alloc();

        // save the original "side" from the map brush -- portal->side
        face.OriginalFace = brushSide;

        face.TexInfo = brushSide.TexInfo;
        face.DispInfo = -1;     // all faces with displacement info are created elsewhere
        face.SmoothingGroups = brushSide.SmoothingGroups;

        face.PlaneNumber = (brushSide.PlaneNumber & ~1) | side;

        if (_context.EntityNumber != 0)
        {
            // The brush model renderer doesn't use PLANEBACK, so write the real
            // plane -- except on water, where the face was generated on the
            // inside of the brush and so may be flipped.
            //
            // The water branch assigns exactly what the line above already
            // assigned. That is stock, verbatim (faces.cpp:1338-1345), and it
            // is kept rather than folded away because the two branches are what
            // the code MEANS even though one of them is a no-op.
            if ((portal.NodeAt(side)!.Contents & MapFileLoader.MaskWater) != 0)
            {
                face.PlaneNumber = (brushSide.PlaneNumber & ~1) | side;
            }
            else
            {
                face.PlaneNumber = brushSide.PlaneNumber;
            }
        }

        face.Portal = portal;
        face.FogVolumeLeaf = null;

        int nearContents = portal.NodeAt(side)!.Contents;
        int farContents = portal.NodeAt(side == 0 ? 1 : 0)!.Contents;
        int deltaContents = PortalContents.VisibleContents(farContents ^ nearContents);

        // don't show insides of windows or grates
        if (((nearContents & (int)BrushContents.Window) != 0 && deltaContents == (int)BrushContents.Window)
            || ((nearContents & (int)BrushContents.Grate) != 0 && deltaContents == (int)BrushContents.Grate))
        {
            _context.Faces.Free(face);
            return null;
        }

        if ((nearContents & MapFileLoader.MaskWater) != 0)
        {
            face.FogVolumeLeaf = portal.NodeAt(side);
        }
        else if ((farContents & MapFileLoader.MaskWater) != 0)
        {
            face.FogVolumeLeaf = portal.NodeAt(side == 0 ? 1 : 0);
        }

        // If it's the underside of water, we need to figure out what material to use
        if ((nearContents & (int)BrushContents.Water) != 0 && deltaContents == (int)BrushContents.Water)
        {
            if (!AssignBottomWaterMaterialToFace(face))
            {
                _context.Faces.Free(face);
                return null;
            }
        }

        if (side != 0)
        {
            face.Winding = _context.Windings.Reverse(portal.Winding);
            face.Contents = portal.BackNode!.Contents;
        }
        else
        {
            face.Winding = _context.Windings.Copy(portal.Winding);
            face.Contents = portal.FrontNode!.Contents;
        }

        face.NumPrims = 0;
        face.FirstPrimId = 0;

        return face;
    }

    /// <summary>
    /// Gives a water underside the texinfo of its material's
    /// <c>$bottommaterial</c>
    /// (<c>AssignBottomWaterMaterialToFace</c>, <c>faces.cpp:1255</c>).
    /// </summary>
    /// <param name="face">The face to retexture.</param>
    /// <returns>False when the material has no bottom material, which discards the face.</returns>
    /// <remarks>
    /// Stock warns unless the material's name contains "nodraw" or
    /// "toolsskip"; the same suppression is reproduced, because a nodraw water
    /// brush is a normal thing to author and the warning would be noise.
    /// </remarks>
    public bool AssignBottomWaterMaterialToFace(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        bool warn = true;

        if (_context.Materials is not null
            && _context.Materials.TryGetBottomTexInfo(face.TexInfo, out int bottom, out warn))
        {
            face.TexInfo = bottom;
            return true;
        }

        if (warn)
        {
            _context.Diagnostics.Add(new CompileDiagnostic(
                MissingBottomMaterial,
                DiagnosticSeverity.Warning,
                $"error: material for texinfo {face.TexInfo} doesn't have a $bottommaterial"));
        }

        return false;
    }

    /// <summary>
    /// Builds faces for a subtree, merging and subdividing on the way back up
    /// (<c>MakeFaces_r</c>, <c>faces.cpp:1415</c>).
    /// </summary>
    /// <param name="node">The root of the subtree.</param>
    public void MakeFacesRecursive(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // recurse down to leafs
        if (!node.IsLeaf())
        {
            MakeFacesRecursive(node.Front!);
            MakeFacesRecursive(node.Back!);

            // merge together all visible faces on the node
            if (!_context.Options.NoMerge)
            {
                _context.Lists.SetFacesOf(node, _merger.MergeFaceList(_context.Lists.FacesOf(node)));
            }

            if (!_context.Options.NoSubdiv)
            {
                _context.Lists.SetFacesOf(
                    node, _subdivider.SubdivideFaceList(_context.Lists.FacesOf(node)));
            }

            return;
        }

        // solid leafs never have visible faces
        if ((node.Contents & PortalContents.Solid) != 0)
        {
            return;
        }

        // see which portals are valid
        for (Portal? p = node.Portals; p is not null;)
        {
            int s = ReferenceEquals(p.BackNode, node) ? 1 : 0;

            Face? face = FaceFromPortal(p, s);
            _context.Lists.SetPortalFace(p, s, face);

            if (face is not null)
            {
                _context.Counters.NodeFaces++;
                _context.Lists.PrependFace(p.OnNode!, face);
            }

            p = p.NextAt(s);
        }
    }

    /// <summary>
    /// Turns portals with one solid side into faces, for a whole model
    /// (<c>MakeFaces</c>, <c>faces.cpp:1798</c>).
    /// </summary>
    /// <param name="headNode">The model's tree root.</param>
    public void MakeFaces(IBspNode headNode)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        _context.Counters.ResetForMakeFaces();
        MakeFacesRecursive(headNode);
    }
}
