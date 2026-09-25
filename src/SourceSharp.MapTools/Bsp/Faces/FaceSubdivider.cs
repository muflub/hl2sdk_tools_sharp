using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Cutting faces down until their lightmaps fit
/// (<c>SubdivideFace</c>).
/// </summary>
/// <remarks>
/// <para>
/// A face's lightmap is a rectangle in luxels, and the engine's surface cache
/// has a hard limit on how big one may be. This measures the face along each
/// of its two lightmap axes and, while the extent exceeds
/// <see cref="FaceBuildContext.MaxLightmapDimension"/> luxels, splits it one
/// tile off the low end and recurses into both halves.
/// </para>
/// <para>
/// <b>The split distance is deliberately one luxel short.</b>
/// <c>(mins + g_maxLightmapDimension - 1) / luxelsPerWorldUnit</c> leaves the
/// front piece 31 luxels wide, not 32, because vrad's lightmap is
/// <c>size + 1</c> samples across.
/// </para>
/// </remarks>
public sealed class FaceSubdivider
{
    private readonly FaceBuildContext _context;

    /// <summary>Creates a subdivider over one compile's face stage.</summary>
    /// <param name="context">The stage's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public FaceSubdivider(FaceBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Subdivides one face, prepending its pieces to a list
    /// (<c>SubdivideFace</c>).
    /// </summary>
    /// <param name="head">The list the pieces are pushed onto.</param>
    /// <param name="face">The face to subdivide.</param>
    /// <returns>The head of the list afterwards.</returns>
    public Face? SubdivideFace(Face? head, Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.IsDead)
        {
            return head;
        }

        // special (non-surface cached) faces don't need subdivision
        TexInfo tex = _context.TexInfos[face.TexInfo];

        if ((tex.Flags & (int)SurfaceFlags.NoLight) != 0)
        {
            return head;
        }

        for (int axis = 0; axis < 2; axis++)
        {
            while (true)
            {
                float mins = 999999f;
                float maxs = -999999f;

                Vec3 temp = new(
                    tex.LightmapVecsLuxelsPerWorldUnits[(axis * 4) + 0],
                    tex.LightmapVecsLuxelsPerWorldUnits[(axis * 4) + 1],
                    tex.LightmapVecsLuxelsPerWorldUnits[(axis * 4) + 2]);

                foreach (Vec3 point in _context.Windings.Points(face.Winding))
                {
                    float v = Vec3.Dot(point, temp);

                    if (v < mins)
                    {
                        mins = v;
                    }

                    if (v > maxs)
                    {
                        maxs = v;
                    }
                }

                if (maxs - mins <= _context.MaxLightmapDimension)
                {
                    break;
                }

                // split it
                _context.Counters.Subdivided++;

                (Vec3 normalised, float luxelsPerWorldUnit) = temp.NormaliseLikeStock();

                float dist = (mins + _context.MaxLightmapDimension - 1f) / luxelsPerWorldUnit;

                _context.Windings.ClipEpsilon(
                    face.Winding,
                    normalised,
                    dist,
                    GeometryEpsilons.OnEpsilonFloat,
                    out Winding front,
                    out Winding back);

                if (front.IsNull || back.IsNull)
                {
                    throw new InvalidOperationException("SubdivideFace: didn't split the polygon");
                }

                Face frontFace = _context.Faces.NewFaceFromFace(face);
                frontFace.Winding = front;
                face.Split[0] = frontFace;
                frontFace.Next = head;
                head = frontFace;

                Face backFace = _context.Faces.NewFaceFromFace(face);
                backFace.Winding = back;
                face.Split[1] = backFace;
                backFace.Next = head;
                head = backFace;

                head = SubdivideFace(head, frontFace);
                head = SubdivideFace(head, backFace);
                return head;
            }
        }

        return head;
    }

    /// <summary>
    /// Subdivides every face on one node's list
    /// (<c>SubdivideFaceList</c>).
    /// </summary>
    /// <param name="head">The head of the list.</param>
    /// <returns>The head afterwards, which changes when anything was split.</returns>
    /// <remarks>
    /// The walk continues through the pieces the split just pushed onto the
    /// FRONT of the list, which it has already passed — so it does not revisit
    /// them, and it does not need to: <c>SubdivideFace</c> recurses into them
    /// itself before returning.
    /// </remarks>
    public Face? SubdivideFaceList(Face? head)
    {
        for (Face? f = head; f is not null; f = f.Next)
        {
            head = SubdivideFace(head, f);
        }

        return head;
    }
}
