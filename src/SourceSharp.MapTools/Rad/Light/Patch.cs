using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// One radiosity patch: <c>CPatch</c> (<c>vrad.h:186</c>).
/// </summary>
/// <remarks>
/// <para>
/// A mutable struct held in <see cref="PatchSet"/>'s array and reached by
/// <c>ref</c>, which is what stock's <c>CUtlVector&lt;CPatch&gt;</c> is. Nothing
/// here is a reference type, deliberately: a real map produces millions of
/// these and the subdivision pass appends to the array WHILE holding indices
/// into it, so an object graph would trade a bounds check for a pointer chase
/// several million times.
/// </para>
/// <para>
/// <b>Links are indices, and -1 means none.</b> Stock uses
/// <c>g_Patches.InvalidIndex()</c>, which is <c>-1</c> for a
/// <c>CUtlVector</c>; the header still carries the commented-out pointer
/// versions those indices replaced (<c>vrad.h:235-237</c>). The links matter
/// more than they look: <see cref="Next"/> chains a face's patches CHILDREN
/// FIRST, and three separate loops in <c>BuildPatchLights</c> depend on that
/// order to push sample light up to parents in one pass.
/// </para>
/// <para>
/// <c>normalMajorAxis</c> from the C++ is absent. It is declared
/// (<c>vrad.h:199</c>) and never written or read anywhere in
/// <c>src/utils/vrad</c>; carrying a field no code touches would only invite
/// somebody to start touching it.
/// </para>
/// </remarks>
public struct Patch
{
    /// <summary>The value every link field uses for "none".</summary>
    public const int Invalid = -1;

    /// <summary>The patch's polygon, in <see cref="PatchSet.Arena"/>.</summary>
    public Winding Winding;

    /// <summary>The winding's bounding box minimum.</summary>
    public Vec3 Mins;

    /// <summary>The winding's bounding box maximum.</summary>
    public Vec3 Maxs;

    /// <summary>
    /// The bounding box of the WHOLE FACE this patch came from, inherited
    /// unchanged by every child.
    /// </summary>
    /// <remarks>
    /// Read by <c>CreateChildPatch</c> (<c>vrad.cpp:819</c>) to tell whether a
    /// child touches the face's edge, which is the only thing that drives the
    /// chop below <c>-maxchop</c>. A child that lies strictly inside the face
    /// never subdivides further than its parent's chop.
    /// </remarks>
    public Vec3 FaceMins;

    /// <summary>The face's bounding box maximum.</summary>
    public Vec3 FaceMaxs;

    /// <summary>
    /// The patch's centre, offset into the owning brush model's position.
    /// </summary>
    /// <remarks>
    /// <c>WindingCenter</c> for a root patch (<c>vrad.cpp:617</c>) but
    /// <c>WindingAreaAndBalancePoint</c>'s balance point for a child
    /// (<c>:898-899</c>). Those are different points on a non-convex or
    /// irregular winding: the first is the mean of the VERTICES, the second the
    /// area-weighted centroid. Reproduced as stock has it.
    /// </remarks>
    public Vec3 Origin;

    /// <summary>The normal of the patch's plane, corrected for an origined model.</summary>
    public Vec3 PlaneNormal;

    /// <summary>
    /// The distance of the patch's plane, already fixed up for an origined
    /// brush model.
    /// </summary>
    /// <remarks>
    /// Stock reaches this through <c>patch-&gt;plane</c>, a pointer into
    /// <c>dplanes</c> -- or into the FAKE PLANES it appends past
    /// <c>numplanes</c> for origined models (<c>vrad.cpp:604-613</c>), writing
    /// into the plane lump's spare capacity. Held inline here, because the
    /// fake planes are never written to the BSP and nothing but the patch reads
    /// them.
    /// </remarks>
    public float PlaneDist;

    /// <summary>
    /// <c>planeDist</c>: a copy of <see cref="PlaneDist"/> taken at the start of
    /// subdivision.
    /// </summary>
    /// <remarks>
    /// Redundant with <see cref="PlaneDist"/> here and not in stock, where
    /// <c>plane</c> is a pointer that could in principle be repointed. Kept
    /// because <c>vismat.cpp:190,220</c> reads this field by name when it
    /// decides whether two patches can see each other, and a reader comparing
    /// the two files should find the same name.
    /// </remarks>
    public float CachedPlaneDist;

    /// <summary>
    /// The patch's shading normal: the plane normal for a root patch, the phong
    /// normal at <see cref="Origin"/> for a child.
    /// </summary>
    public Vec3 Normal;

    /// <summary>Whether the face carries <c>SURF_SKY</c>.</summary>
    public bool Sky;

    /// <summary>Whether the face carries <c>SURF_BUMPLIGHT</c>.</summary>
    public bool NeedsBumpmap;

    /// <summary>
    /// The smallest acceptable width of this patch, in luxel widths.
    /// </summary>
    /// <remarks>
    /// Starts at <c>-maxchop</c> and is HALVED, down to <c>-chop</c>, by two
    /// separate rules: a child that touches the face's edge
    /// (<c>vrad.cpp:815-826</c>) and a patch that is more than twice as long as
    /// it is wide (<c>:874-885</c>). So it is per-patch state and not a setting.
    /// </remarks>
    public float Chop;

    /// <summary>
    /// The mean of the two lightmap axes' lengths: how many luxels one world
    /// unit spans.
    /// </summary>
    public float LuxScale;

    /// <summary>The texture's s scale, <c>scale[0]</c>.</summary>
    public float ScaleS;

    /// <summary>The texture's t scale, <c>scale[1]</c>.</summary>
    public float ScaleT;

    /// <summary>The patch's surface area in world units.</summary>
    public float Area;

    /// <summary>
    /// <c>baselight</c>: the emissive colour of the face's material, before any
    /// area scaling.
    /// </summary>
    public Vec3 BaseLight;

    /// <summary>
    /// <c>basearea</c>: the material's <c>width * height</c> in texels.
    /// </summary>
    /// <remarks>
    /// Not an area in world units despite the name. It divides the emitted
    /// intensity at <c>lightmap.cpp:1577</c> so that a texlight's brightness is
    /// per texture INSTANCE rather than per world area -- which is why a
    /// texlight scaled up in Hammer does not get brighter.
    /// </remarks>
    public float BaseArea;

    /// <summary>The face's average reflectivity, clamped below 0.99.</summary>
    public Vec3 Reflectivity;

    /// <summary>
    /// <c>directlight</c>: the direct light this patch received, averaged over
    /// its samples.
    /// </summary>
    public Vec3 DirectLight;

    /// <summary><c>samplelight</c>: the area-weighted sum of sample light.</summary>
    public Vec3 SampleLight;

    /// <summary><c>samplearea</c>: the total area of the samples summed into it.</summary>
    public float SampleArea;

    /// <summary>
    /// <c>totallight</c>: the bounced light, one vector per bump basis
    /// direction plus the flat one.
    /// </summary>
    public BumpLights TotalLight;

    /// <summary>The face this patch belongs to.</summary>
    public int FaceNumber;

    /// <summary>
    /// The vis cluster <see cref="Origin"/> falls in, or -1 when it fell in
    /// solid and no winding point rescued it.
    /// </summary>
    public int ClusterNumber;

    /// <summary>The patch this one was split from, or -1.</summary>
    public int Parent;

    /// <summary>The first half of the split, or -1.</summary>
    public int Child1;

    /// <summary>The second half, or -1.</summary>
    public int Child2;

    /// <summary>The next patch on the same face, children first.</summary>
    public int Next;

    /// <summary>The next ROOT patch on the same face.</summary>
    public int NextParent;

    /// <summary>The next leaf patch in the same cluster.</summary>
    public int NextClusterChild;

    /// <summary>
    /// <c>m_IterationKey</c>: guards against visiting a patch twice in one
    /// sample-hash query.
    /// </summary>
    public ushort IterationKey;

    /// <summary>
    /// <c>indices[3]</c>: the displacement triangle this patch subdivides.
    /// Meaningless on a brush patch, and copied to children regardless.
    /// </summary>
    public short Index0;

    /// <summary>The second displacement index.</summary>
    public short Index1;

    /// <summary>The third displacement index.</summary>
    public short Index2;

    /// <summary>Whether this patch was subdivided.</summary>
    public readonly bool HasChildren => Child1 != Invalid;
}

/// <summary>
/// <c>bumplights_t</c> (<c>vrad.h:103</c>): one colour per bump basis vector
/// plus the unbumped one.
/// </summary>
/// <remarks>
/// Four named fields rather than an inline array so that the struct copies by
/// value without an unsafe fixed buffer; <see cref="this[int]"/> gives the
/// indexed access the C++ uses.
/// </remarks>
public struct BumpLights
{
    /// <summary>The unbumped light, <c>light[0]</c>.</summary>
    public Vec3 Flat;

    /// <summary><c>light[1]</c>.</summary>
    public Vec3 Bump1;

    /// <summary><c>light[2]</c>.</summary>
    public Vec3 Bump2;

    /// <summary><c>light[3]</c>.</summary>
    public Vec3 Bump3;

    /// <summary>One of the four, by index.</summary>
    /// <param name="index">0 for the flat light, 1..3 for the bump directions.</param>
    /// <returns>That light.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is not 0..3.</exception>
    public Vec3 this[int index]
    {
        readonly get => index switch
        {
            0 => Flat,
            1 => Bump1,
            2 => Bump2,
            3 => Bump3,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        set
        {
            switch (index)
            {
                case 0: Flat = value; break;
                case 1: Bump1 = value; break;
                case 2: Bump2 = value; break;
                case 3: Bump3 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }
}

/// <summary>
/// <c>face_centroids</c> (<c>lightmap.cpp:64</c>): the un-offset centre of each
/// face, as the patch pass computed it.
/// </summary>
/// <remarks>
/// <para>
/// Filled by <see cref="PatchBuilder"/> and read only by
/// <see cref="PhongNormals"/>, which is a dependency worth stating out loud:
/// <b>phong normals are undefined until patches exist.</b> Stock has the same
/// ordering constraint and expresses it only by the order of two calls in
/// <c>RadWorld_Start</c>, so a face that produced no patch -- a degenerate one,
/// or a displacement -- silently keeps the zero its global array was born with
/// and phong-shades around the world origin.
/// </para>
/// <para>
/// The zero is reproduced rather than replaced with the real centre, because
/// the faces it affects are exactly the faces vrad declines to light.
/// </para>
/// </remarks>
public sealed class FaceCentroids
{
    private readonly Vec3[] _centroids;

    /// <summary>Creates a zero-filled table.</summary>
    /// <param name="faceCount">How many faces the map has.</param>
    public FaceCentroids(int faceCount) => _centroids = new Vec3[faceCount];

    /// <summary>How many faces this covers.</summary>
    public int Count => _centroids.Length;

    /// <summary>One face's centroid.</summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>Its centroid, or zero when no patch was made for it.</returns>
    public Vec3 this[int faceNum]
    {
        get => _centroids[faceNum];
        internal set => _centroids[faceNum] = value;
    }
}
