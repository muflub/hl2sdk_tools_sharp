using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// One face's luxel-space frame: <c>lightinfo_t</c> (<c>lightmap.h:88</c>) and
/// <c>CalcFaceVectors</c> (<c>lightmap.cpp:417</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every sample position vrad computes is a point on the face's plane named by
/// a luxel coordinate, so this affine map is the coordinate system the whole
/// lighting pass lives in. It is a 2x3 pair each way rather than a 4x4: the
/// third dimension is pinned by the face's own plane, which is why the inverse
/// exists at all and why <see cref="LuxelOrigin"/> has to be solved for rather
/// than read from the texinfo.
/// </para>
/// <para>
/// <b>The frame is DEGENERATE when the lightmap axes lie in the face's plane.</b>
/// Stock warns and sets <c>luxelOrigin</c> to the world origin
/// (<c>lightmap.cpp:451-455</c>), leaving <c>luxelToWorldSpace</c> as the zeros
/// <c>memset</c> left -- so every sample on that face collapses onto the model
/// origin and the face lights as if it were one point. Reproduced, including
/// the zeros, because the alternative is inventing a frame stock does not have;
/// <see cref="IsDegenerate"/> makes it reportable.
/// </para>
/// </remarks>
public sealed class FaceLightInfo
{
    /// <summary>
    /// How near zero the solve's determinant may come before the frame is
    /// called degenerate: <c>1.0e-20</c> (<c>lightmap.cpp:451</c>).
    /// </summary>
    /// <remarks>
    /// Compared with <c>fabs</c> against a value computed in FLOAT and then
    /// widened, so the threshold is far below what a float determinant can
    /// represent meaningfully; it catches an exact-zero cross product rather
    /// than a near-degenerate one.
    /// </remarks>
    public const double DegenerateDeterminant = 1.0e-20;

    private FaceLightInfo(
        int faceNum,
        Vec3 faceNormal,
        float faceDist,
        Vec3 modelOrigin,
        Vec3 luxelOrigin,
        Vec3 worldToLuxelS,
        Vec3 worldToLuxelT,
        Vec3 luxelToWorldS,
        Vec3 luxelToWorldT,
        int luxelMinS,
        int luxelMinT,
        int width,
        int height,
        bool isFlat,
        bool isDegenerate)
    {
        FaceNum = faceNum;
        FaceNormal = faceNormal;
        FaceDist = faceDist;
        ModelOrigin = modelOrigin;
        LuxelOrigin = luxelOrigin;
        WorldToLuxelS = worldToLuxelS;
        WorldToLuxelT = worldToLuxelT;
        LuxelToWorldS = luxelToWorldS;
        LuxelToWorldT = luxelToWorldT;
        LuxelMinS = luxelMinS;
        LuxelMinT = luxelMinT;
        Width = width;
        Height = height;
        IsFlat = isFlat;
        IsDegenerate = isDegenerate;
    }

    /// <summary><c>lightinfo_t::facenum</c>.</summary>
    public int FaceNum { get; }

    /// <summary><c>lightinfo_t::facenormal</c>: the face's plane normal.</summary>
    public Vec3 FaceNormal { get; }

    /// <summary><c>lightinfo_t::facedist</c>.</summary>
    public float FaceDist { get; }

    /// <summary>
    /// <c>lightinfo_t::modelorg</c>: the owning brush model's origin, zero for
    /// the world.
    /// </summary>
    public Vec3 ModelOrigin { get; }

    /// <summary>
    /// <c>lightinfo_t::luxelOrigin</c>: the world point luxel (0,0) maps to,
    /// already shifted by the model origin.
    /// </summary>
    public Vec3 LuxelOrigin { get; }

    /// <summary><c>worldToLuxelSpace[0]</c>: the s row.</summary>
    public Vec3 WorldToLuxelS { get; }

    /// <summary><c>worldToLuxelSpace[1]</c>: the t row.</summary>
    public Vec3 WorldToLuxelT { get; }

    /// <summary><c>luxelToWorldSpace[0]</c>: one luxel of s, in world units.</summary>
    public Vec3 LuxelToWorldS { get; }

    /// <summary><c>luxelToWorldSpace[1]</c>: one luxel of t, in world units.</summary>
    public Vec3 LuxelToWorldT { get; }

    /// <summary><c>m_LightmapTextureMinsInLuxels[0]</c>.</summary>
    public int LuxelMinS { get; }

    /// <summary><c>m_LightmapTextureMinsInLuxels[1]</c>.</summary>
    public int LuxelMinT { get; }

    /// <summary>
    /// The lightmap's sample grid width: <c>m_LightmapTextureSizeInLuxels[0] + 1</c>.
    /// </summary>
    /// <remarks>
    /// The "+1" is not a fencepost slip. The size field records the number of
    /// luxel CELLS; samples sit at the cell corners, so there is one more of
    /// them in each axis. Every buffer in the lighting path is sized from this,
    /// which is why it is stored rather than recomputed at each of the eleven
    /// C++ sites that spell <c>...InLuxels[0]+1</c>.
    /// </remarks>
    public int Width { get; }

    /// <summary>The sample grid height.</summary>
    public int Height { get; }

    /// <summary><c>lightinfo_t::isflat</c>.</summary>
    public bool IsFlat { get; }

    /// <summary>
    /// Whether <c>CalcFaceVectors</c> found the lightmap axes parallel to the
    /// face normal and gave up.
    /// </summary>
    public bool IsDegenerate { get; }

    /// <summary>
    /// Builds a face's frame: <c>InitLightinfo</c> (<c>lightmap.cpp:2979</c>).
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="neighbours">The smoothing pass, for the flatness test.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="modelOrigin">
    /// <c>face_offset[facenum]</c>: the owning entity's origin.
    /// </param>
    /// <param name="smoothingThreshold">The smoothing cosine.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="geometry"/> or <paramref name="neighbours"/> is null.
    /// </exception>
    public static FaceLightInfo Build(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        int faceNum,
        Vec3 modelOrigin,
        float smoothingThreshold)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(neighbours);

        ref readonly DFace face = ref geometry.Faces[faceNum];
        ref readonly DPlane plane = ref geometry.Planes[face.PlaneNum];
        ref readonly TexInfo tex = ref geometry.TexInfos[face.TexInfo];

        Vec3 faceNormal = plane.Normal;

        // lightmap.cpp:425-431. The s and t rows are the LIGHTMAP axes'
        // xyz, with column 3 -- the offset -- deliberately left out; it is
        // folded into luxelOrigin at :470-471 instead.
        Vec3 worldToLuxelS = new(
            tex.LightmapVecsLuxelsPerWorldUnits[0],
            tex.LightmapVecsLuxelsPerWorldUnits[1],
            tex.LightmapVecsLuxelsPerWorldUnits[2]);
        Vec3 worldToLuxelT = new(
            tex.LightmapVecsLuxelsPerWorldUnits[4],
            tex.LightmapVecsLuxelsPerWorldUnits[5],
            tex.LightmapVecsLuxelsPerWorldUnits[6]);
        float offsetS = tex.LightmapVecsLuxelsPerWorldUnits[3];
        float offsetT = tex.LightmapVecsLuxelsPerWorldUnits[7];

        // :440-448. NOT Cross(t, s) and not Cross(s, t): each component pairs
        // a t term with an s term in an order that is neither, because the
        // expression is the 2x2 minor of the 3x3 solve at :433-436 rather than
        // a cross product that happens to be written out. Transcribed
        // component by component for that reason.
        Vec3 luxelSpaceCross = new(
            (worldToLuxelT.Y * worldToLuxelS.Z) - (worldToLuxelT.Z * worldToLuxelS.Y),
            (worldToLuxelT.Z * worldToLuxelS.X) - (worldToLuxelT.X * worldToLuxelS.Z),
            (worldToLuxelT.X * worldToLuxelS.Y) - (worldToLuxelT.Y * worldToLuxelS.X));

        float det = -Vec3.Dot(faceNormal, luxelSpaceCross);

        Vec3 luxelOrigin;
        Vec3 luxelToWorldS = Vec3.Zero;
        Vec3 luxelToWorldT = Vec3.Zero;
        bool degenerate = Math.Abs(det) < DegenerateDeterminant;

        if (degenerate)
        {
            // :452-455. vec3_origin, and the two luxelToWorldSpace rows stay
            // as the memset at :2985 left them: zero.
            luxelOrigin = Vec3.Zero;
        }
        else
        {
            // :459-467. The inverse, written out per component.
            luxelToWorldS = new Vec3(
                ((faceNormal.Z * worldToLuxelT.Y) - (faceNormal.Y * worldToLuxelT.Z)) / det,
                ((faceNormal.X * worldToLuxelT.Z) - (faceNormal.Z * worldToLuxelT.X)) / det,
                ((faceNormal.Y * worldToLuxelT.X) - (faceNormal.X * worldToLuxelT.Y)) / det);
            luxelToWorldT = new Vec3(
                ((faceNormal.Y * worldToLuxelS.Z) - (faceNormal.Z * worldToLuxelS.Y)) / det,
                ((faceNormal.Z * worldToLuxelS.X) - (faceNormal.X * worldToLuxelS.Z)) / det,
                ((faceNormal.X * worldToLuxelS.Y) - (faceNormal.Y * worldToLuxelS.X)) / det);
            luxelOrigin = new Vec3(
                -(plane.Dist * luxelSpaceCross.X) / det,
                -(plane.Dist * luxelSpaceCross.Y) / det,
                -(plane.Dist * luxelSpaceCross.Z) / det);

            // :470-471. Now the texinfo's luxel offsets, subtracted along the
            // world-space luxel axes.
            luxelOrigin += luxelToWorldS * -offsetS;
            luxelOrigin += luxelToWorldT * -offsetT;
        }

        // :474, and OUTSIDE the else: an origined brush model's degenerate
        // face still gets its model origin added to a zero luxel origin.
        luxelOrigin += modelOrigin;

        return new FaceLightInfo(
            faceNum,
            faceNormal,
            plane.Dist,
            modelOrigin,
            luxelOrigin,
            worldToLuxelS,
            worldToLuxelT,
            luxelToWorldS,
            luxelToWorldT,
            face.LightmapTextureMinsInLuxels[0],
            face.LightmapTextureMinsInLuxels[1],
            face.LightmapTextureSizeInLuxels[0] + 1,
            face.LightmapTextureSizeInLuxels[1] + 1,
            neighbours.IsFlat(faceNum, smoothingThreshold),
            degenerate);
    }

    /// <summary>
    /// <c>WorldToLuxelSpace</c> (<c>radial.cpp:18</c>).
    /// </summary>
    /// <param name="world">A world point, expected to lie on the face's plane.</param>
    /// <returns>Its luxel coordinate, relative to the lightmap's own origin.</returns>
    public (float S, float T) WorldToLuxel(Vec3 world)
    {
        Vec3 pos = world - LuxelOrigin;
        return (
            Vec3.Dot(pos, WorldToLuxelS) - LuxelMinS,
            Vec3.Dot(pos, WorldToLuxelT) - LuxelMinT);
    }

    /// <summary>
    /// <c>LuxelSpaceToWorld</c> (<c>radial.cpp:27</c>).
    /// </summary>
    /// <param name="s">The luxel s coordinate.</param>
    /// <param name="t">The luxel t coordinate.</param>
    /// <returns>The world point on the face's plane.</returns>
    /// <remarks>
    /// The two multiply-adds are SEQUENTIAL in stock -- <c>VectorMA</c> into a
    /// temporary, then <c>VectorMA</c> of that into the result -- so the
    /// rounding order is origin, then s, then t. Written the same way here
    /// because a fused or reordered form changes the last bit of every sample
    /// position in the map.
    /// </remarks>
    public Vec3 LuxelToWorld(float s, float t)
    {
        s += LuxelMinS;
        t += LuxelMinT;
        Vec3 pos = LuxelOrigin + (LuxelToWorldS * s);
        return pos + (LuxelToWorldT * t);
    }

    /// <summary>
    /// <c>LightmapCoordWindingForFace</c> (<c>lightmap.cpp:479</c>): the face's
    /// polygon flattened into luxel space, with z zeroed.
    /// </summary>
    /// <param name="arena">Where the winding is allocated.</param>
    /// <param name="geometry">The map's lumps.</param>
    /// <returns>The winding in luxel coordinates.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="arena"/> or <paramref name="geometry"/> is null.
    /// </exception>
    public Winding LightmapCoordWinding(WindingArena arena, LightGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(geometry);

        Winding w = geometry.WindingFromFace(arena, FaceNum, ModelOrigin);
        Span<Vec3> points = arena.Points(w);
        for (int i = 0; i < points.Length; i++)
        {
            (float s, float t) = WorldToLuxel(points[i]);
            points[i] = new Vec3(s, t, 0f);
        }

        return w;
    }
}
