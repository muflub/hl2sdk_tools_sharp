using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// Radiosity patches for displacements: <c>CVRadDispMgr::MakePatches</c> and
/// <c>SubdividePatch</c> over
/// <c>CVRADDispColl</c>'s patch builders.
/// </summary>
/// <remarks>
/// <para>
/// A displacement's root patch is the QUAD of its four corner vertices
/// (<c>CreateParentPatches</c>), appended after every brush
/// face's patch. Subdivision then splits it along the grid: the root quad into
/// two triangles on its diagonal, each triangle in two at the grid vertex
/// halfway along its long edge -- <c>(indices[0] + indices[1]) / 2</c> -- for
/// <c>2 * power</c> levels, and past the grid's resolution
/// (<c>CreateChildPatchesSub</c>) by halving the longest edge in world space.
/// A patch stops splitting when its longest edge is under
/// <c>luxel size * dispchop</c> or its area under the square of that (half of
/// it for a triangle).
/// </para>
/// <para>
/// Everything here writes the shared <see cref="PatchSet"/> and runs serially,
/// as stock's does, inside <see cref="PatchSubdivider.Subdivide"/>'s loop so the
/// patches are numbered exactly as stock numbers them.
/// </para>
/// </remarks>
public static class DispPatchBuilder
{
    /// <summary>
    /// <c>CVRadDispMgr::MakePatches</c>: one root
    /// patch per displacement, appended to the set in LUMP_DISPINFO order.
    /// </summary>
    /// <param name="displacements">The map's displacements.</param>
    /// <param name="geometry">The map.</param>
    /// <param name="patches">The patch set, brush patches already in it.</param>
    /// <param name="texLights">The texlights (for <c>BaseLightForFace</c>).</param>
    /// <param name="settings">The switches (<c>dispchop</c>, the normalise).</param>
    /// <returns>The summed displacement area, stock's "Square Inches".</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static float MakePatches(
        IReadOnlyList<VradDispSurface?> displacements,
        LightGeometry geometry,
        PatchSet patches,
        TextureLightTable texLights,
        DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(displacements);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(texLights);
        ArgumentNullException.ThrowIfNull(settings);

        float total = 0.0f;
        foreach (VradDispSurface? d in displacements)
        {
            if (d is null)
            {
                continue;
            }

            total += CreateParentPatch(d, geometry, patches, texLights, settings);
        }

        return total;
    }

    /// <summary>
    /// <c>CreateParentPatches</c> + <c>InitParentPatch</c>
    /// </summary>
    /// <param name="d">The displacement.</param>
    /// <param name="geometry">The map.</param>
    /// <param name="patches">The patch set.</param>
    /// <param name="texLights">The texlights.</param>
    /// <param name="settings">The switches.</param>
    /// <returns>The patch's area.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// The quad is corners 0, <c>w(w-1)</c>, <c>w^2-1</c>, <c>w-1</c>; its
    /// normal is <c>(p3 - p0) x (p1 - p0)</c> and its AREA is that cross
    /// product's length -- exact only for a parallelogram, and
    /// <c>VectorNormalize</c>'s return value, so stock's estimate
    /// (<see cref="Options.StockQuirk.VradVectorNormalise"/>) reaches the
    /// "Square Inches" line.
    /// </remarks>
    public static float CreateParentPatch(
        VradDispSurface d,
        LightGeometry geometry,
        PatchSet patches,
        TextureLightTable texLights,
        DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(texLights);
        ArgumentNullException.ThrowIfNull(settings);

        // BaseLightForFace, and the bump flag from the face's texinfo.
        (Vec3 baseLight, float baseArea, Vec3 reflectivity) =
            PatchBuilder.BaseLightForFace(geometry, texLights, d.ParentFace);
        return CreateParentPatch(
            d, patches, settings, NeedsBumpmap(geometry, d.ParentFace), baseLight, baseArea, reflectivity);
    }

    /// <summary>
    /// The root patch with the face's material terms already looked up.
    /// </summary>
    /// <param name="d">The displacement.</param>
    /// <param name="patches">The patch set.</param>
    /// <param name="settings">The switches.</param>
    /// <param name="needsBumpmap">Whether the face's texinfo is bump-lit.</param>
    /// <param name="baseLight"><c>BaseLightForFace</c>'s emission.</param>
    /// <param name="baseArea">Its texel area.</param>
    /// <param name="reflectivity">Its reflectivity.</param>
    /// <returns>The patch's area.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static float CreateParentPatch(
        VradDispSurface d,
        PatchSet patches,
        DirectLightingSettings settings,
        bool needsBumpmap,
        Vec3 baseLight,
        float baseArea,
        Vec3 reflectivity)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(settings);

        int w = d.Width;
        ReadOnlySpan<Vec3> verts = d.Verts;
        Span<Vec3> points = [verts[0], verts[w * (w - 1)], verts[(w * w) - 1], verts[w - 1]];

        int face = d.ParentFace;
        Patch patch = NewPatch(settings.DispChop);

        // A root: prepended to the face's list.
        patch.Next = patches.FacePatches[face];
        patch.FaceNumber = face;

        Vec3 e0 = points[1] - points[0];
        Vec3 e1 = points[3] - points[0];
        (Vec3 normal, float area) = VradDispSurface.NormaliseWithLength(Vec3.Cross(e1, e0), settings.StockNormalise);

        // The centre is summed from zero in point order, times 1/4.
        Vec3 center = Vec3.Zero;
        foreach (Vec3 p in points)
        {
            center = p + center;
        }

        patch.Winding = patches.Arena.Create(points);
        patch.Origin = center * (1.0f / 4.0f);
        FillPlaneAndBounds(ref patch, normal, points, area);
        patch.FaceMins = patch.Mins;
        patch.FaceMaxs = patch.Maxs;
        patch.NeedsBumpmap = needsBumpmap;
        patch.BaseLight = baseLight;
        patch.BaseArea = baseArea;
        patch.Reflectivity = reflectivity;

        int index = patches.Add(patch);
        patches.FacePatches[face] = index;
        return area;
    }

    /// <summary>
    /// <c>CVRadDispMgr::SubdividePatch</c>:
    /// <c>CreateChildPatches(iPatch, 0)</c> on the patch's displacement.
    /// </summary>
    /// <param name="surfaceForFace">The displacement of a face (by its <c>dispinfo</c>).</param>
    /// <param name="patches">The patch set.</param>
    /// <param name="patchIndex">The root patch.</param>
    /// <param name="settings">The switches.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static void SubdividePatch(VradDispSurface surfaceForFace, PatchSet patches, int patchIndex, DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(surfaceForFace);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(settings);

        CreateChildPatches(surfaceForFace, patches, patchIndex, 0, settings);
    }

    /// <summary>
    /// <c>CreateChildPatches</c>.
    /// </summary>
    /// <param name="d">The displacement.</param>
    /// <param name="patches">The patch set.</param>
    /// <param name="parentIndex">The patch to split.</param>
    /// <param name="level">How many grid levels deep this is.</param>
    /// <param name="settings">The switches.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static void CreateChildPatches(VradDispSurface d, PatchSet patches, int parentIndex, int level, DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(settings);

        WindingArena arena = patches.Arena;
        Patch parent = patches.At(parentIndex);
        ReadOnlySpan<Vec3> pw = arena.Points(parent.Winding);

        // The root is a quad.
        if (pw.Length == 4)
        {
            (int c0, int c1) = CreateChildPatchesFromRoot(d, patches, parentIndex, settings);
            if (c0 != Patch.Invalid && c1 != Patch.Invalid)
            {
                CreateChildPatches(d, patches, c0, 0, settings);
                CreateChildPatches(d, patches, c1, 0, settings);
            }

            return;
        }

        if (pw.Length != 3)
        {
            return;
        }

        float maxLength = MaxMacro(d.SampleWidth, d.SampleHeight);
        float minEdgeLength = maxLength * settings.DispChop;

        Vec3 e0 = pw[1] - pw[0];
        Vec3 e1 = pw[2] - pw[0];
        Vec3 e2 = pw[2] - pw[1];
        float edgeLength = LongestEdge(e0, e1, e2, out _);
        if (edgeLength < minEdgeLength)
        {
            return;
        }

        // Slivers: area under half the square of the chop.
        float minArea = (settings.DispChop * maxLength) * (settings.DispChop * maxLength) * 0.5f;
        float testArea = VradDispSurface.NormaliseWithLength(Vec3.Cross(e1, e0), settings.StockNormalise).Length;
        testArea *= 0.5f;
        if (testArea < minArea)
        {
            return;
        }

        // Out of grid vertices: split in world space.
        if (level >= d.Power * 2)
        {
            CreateChildPatchesSub(d, patches, parentIndex, settings);
            return;
        }

        int i0 = parent.Index0;
        int i1 = parent.Index1;
        int i2 = parent.Index2;
        int newIndex = (i1 + i0) / 2;
        Span<int> child0 = [i2, i0, newIndex];
        Span<int> child1 = [i1, i2, newIndex];

        ReadOnlySpan<Vec3> verts = d.Verts;
        Span<Vec3> pts0 = [verts[child0[0]], verts[child0[1]], verts[child0[2]]];
        Span<Vec3> pts1 = [verts[child1[0]], verts[child1[1]], verts[child1[2]]];

        int c0Index = InitPatch(d, patches, parentIndex, 0, pts0, child0, settings);
        int c1Index = InitPatch(d, patches, parentIndex, 1, pts1, child1, settings);

        CreateChildPatches(d, patches, c0Index, level + 1, settings);
        CreateChildPatches(d, patches, c1Index, level + 1, settings);
    }

    /// <summary>
    /// <c>CreateChildPatchesFromRoot</c>: the
    /// root quad split on the diagonal from the last vertex to the first.
    /// </summary>
    private static (int Child0, int Child1) CreateChildPatchesFromRoot(
        VradDispSurface d, PatchSet patches, int parentIndex, DirectLightingSettings settings)
    {
        int w = d.Width;
        ReadOnlySpan<Vec3> pw = patches.Arena.Points(patches.At(parentIndex).Winding);

        Vec3 e0 = pw[1] - pw[0];
        Vec3 e1 = pw[2] - pw[1];
        Vec3 e2 = pw[3] - pw[2];
        Vec3 e3 = pw[3] - pw[0];

        float maxLength = MaxMacro(d.SampleWidth, d.SampleHeight);
        float minEdgeLength = maxLength * settings.DispChop;

        float edgeLength = 0.0f;
        foreach (Vec3 e in (ReadOnlySpan<Vec3>)[e0, e1, e2, e3])
        {
            float length = e.Length();
            if (edgeLength < length)
            {
                edgeLength = e.Length();
            }
        }

        if (edgeLength < minEdgeLength)
        {
            return (Patch.Invalid, Patch.Invalid);
        }

        float minArea = (settings.DispChop * maxLength) * (settings.DispChop * maxLength);
        float testArea = VradDispSurface.NormaliseWithLength(Vec3.Cross(e3, e0), settings.StockNormalise).Length;
        if (testArea < minArea)
        {
            return (Patch.Invalid, Patch.Invalid);
        }

        ReadOnlySpan<Vec3> verts = d.Verts;
        Span<int> idx0 = [(w * w) - 1, 0, w * (w - 1)];
        Span<Vec3> pts0 = [verts[idx0[0]], verts[idx0[1]], verts[idx0[2]]];
        int c0 = InitPatch(d, patches, parentIndex, 0, pts0, idx0, settings);

        Span<int> idx1 = [0, (w * w) - 1, w - 1];
        Span<Vec3> pts1 = [verts[idx1[0]], verts[idx1[1]], verts[idx1[2]]];
        int c1 = InitPatch(d, patches, parentIndex, 1, pts1, idx1, settings);
        return (c0, c1);
    }

    /// <summary>
    /// <c>CreateChildPatchesSub</c>: a triangle
    /// past the grid's resolution, halved on its longest edge in world space.
    /// </summary>
    private static void CreateChildPatchesSub(VradDispSurface d, PatchSet patches, int parentIndex, DirectLightingSettings settings)
    {
        ReadOnlySpan<Vec3> pw = patches.Arena.Points(patches.At(parentIndex).Winding);
        if (pw.Length != 3)
        {
            return;
        }

        float maxLength = MaxMacro(d.SampleWidth, d.SampleHeight);
        float minEdgeLength = maxLength * settings.DispChop;

        // NOTE the edges differ from CreateChildPatches': a cycle.
        Vec3 e0 = pw[1] - pw[0];
        Vec3 e1 = pw[2] - pw[1];
        Vec3 e2 = pw[0] - pw[2];
        float edgeLength = LongestEdge(e0, e1, e2, out int longEdge);
        if (edgeLength < minEdgeLength)
        {
            return;
        }

        float minArea = (settings.DispChop * maxLength) * (settings.DispChop * maxLength) * 0.5f;
        float testArea = VradDispSurface.NormaliseWithLength(Vec3.Cross(e1, e0), settings.StockNormalise).Length;
        testArea *= 0.5f;
        if (testArea < minArea)
        {
            return;
        }

        Vec3 p0 = pw[0];
        Vec3 p1 = pw[1];
        Vec3 p2 = pw[2];
        Span<Vec3> c0 = stackalloc Vec3[3];
        Span<Vec3> c1 = stackalloc Vec3[3];
        switch (longEdge)
        {
            case 0:
            {
                Vec3 mid = (p0 + p1) * 0.5f;
                c0[0] = p0; c0[1] = mid; c0[2] = p2;
                c1[0] = mid; c1[1] = p1; c1[2] = p2;
                break;
            }

            case 1:
            {
                Vec3 mid = (p1 + p2) * 0.5f;
                c0[0] = p0; c0[1] = p1; c0[2] = mid;
                c1[0] = mid; c1[1] = p2; c1[2] = p0;
                break;
            }

            default:
            {
                Vec3 mid = (p0 + p2) * 0.5f;
                c0[0] = p0; c0[1] = p1; c0[2] = mid;
                c1[0] = mid; c1[1] = p1; c1[2] = p2;
                break;
            }
        }

        Span<int> none = [-1, -1, -1];
        int child0 = InitPatch(d, patches, parentIndex, 0, c0, none, settings);
        int child1 = InitPatch(d, patches, parentIndex, 1, c1, none, settings);

        CreateChildPatchesSub(d, patches, child0, settings);
        CreateChildPatchesSub(d, patches, child1, settings);
    }

    /// <summary>
    /// <c>InitPatch</c> for a child: appends a
    /// triangle patch and links it under its parent.
    /// </summary>
    private static int InitPatch(
        VradDispSurface d,
        PatchSet patches,
        int parentIndex,
        int child,
        ReadOnlySpan<Vec3> points,
        ReadOnlySpan<int> indices,
        DirectLightingSettings settings)
    {
        Patch parent = patches.At(parentIndex);
        Patch patch = NewPatch(settings.DispChop);
        patch.Next = Patch.Invalid;
        patch.FaceNumber = parent.FaceNumber;
        patch.Parent = parentIndex;

        Vec3 e0 = points[1] - points[0];
        Vec3 e1 = points[2] - points[0];
        (Vec3 normal, float area) = VradDispSurface.NormaliseWithLength(Vec3.Cross(e1, e0), settings.StockNormalise);
        area *= 0.5f;

        Vec3 center = Vec3.Zero;
        foreach (Vec3 p in points)
        {
            center = p + center;
        }

        patch.Winding = patches.Arena.Create(points);
        patch.Index0 = (short)indices[0];
        patch.Index1 = (short)indices[1];
        patch.Index2 = (short)indices[2];
        patch.Origin = center * (1.0f / 3.0f);
        FillPlaneAndBounds(ref patch, normal, points, area);

 // From the parent.
        patch.FaceMins = parent.FaceMins;
        patch.FaceMaxs = parent.FaceMaxs;
        patch.BaseLight = parent.BaseLight;
        patch.BaseArea = parent.BaseArea;
        patch.Reflectivity = parent.Reflectivity;
        patch.NeedsBumpmap = parent.NeedsBumpmap;

        int index = patches.Add(patch);
        ref Patch p2 = ref patches.At(parentIndex);
        if (child == 0)
        {
            p2.Child1 = index;
        }
        else
        {
            p2.Child2 = index;
        }

        _ = d;
        return index;
    }

    private static Patch NewPatch(float dispChop)
    {
 // Memset(0), /:925-953.
        Patch patch = default;
        patch.Child1 = Patch.Invalid;
        patch.Child2 = Patch.Invalid;
        patch.Parent = Patch.Invalid;
        patch.NextClusterChild = Patch.Invalid;
        patch.NextParent = Patch.Invalid;
        patch.ScaleS = 1.0f;
        patch.ScaleT = 1.0f;
        patch.Chop = dispChop;
        patch.Sky = false;
        patch.IterationKey = 0;
        return patch;
    }

    private static void FillPlaneAndBounds(ref Patch patch, Vec3 normal, ReadOnlySpan<Vec3> points, float area)
    {
        patch.Normal = normal;
        patch.PlaneNormal = normal;
        patch.PlaneDist = Vec3.Dot(normal, points[0]);
        patch.CachedPlaneDist = patch.PlaneDist;
        patch.Area = area;

 // The max is seeded with FLT_MIN -- the smallest POSITIVE
        // float, not -FLT_MAX -- so a displacement lying wholly on the negative
        // side of an axis gets a max of ~0 on it. Reproduced, not switched:
        // nothing reads a displacement patch's mins/maxs (the flat subdivider
        // never sees one; the patch hash takes the origin), so it cannot reach
        // any output.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = FltMin, maxY = FltMin, maxZ = FltMin;
        foreach (Vec3 p in points)
        {
            minX = MinMacro(minX, p.X);
            minY = MinMacro(minY, p.Y);
            minZ = MinMacro(minZ, p.Z);
            maxX = MaxMacro(maxX, p.X);
            maxY = MaxMacro(maxY, p.Y);
            maxZ = MaxMacro(maxZ, p.Z);
        }

        patch.Mins = new Vec3(minX, minY, minZ);
        patch.Maxs = new Vec3(maxX, maxY, maxZ);
    }

    /// <summary><c>FLT_MIN</c>: the smallest positive normal float.</summary>
    public const float FltMin = 1.17549435e-38f;

    private static bool NeedsBumpmap(LightGeometry geometry, int face) =>
        (geometry.TexInfos[geometry.Faces[face].TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0;

    /// <summary>
    /// The longest of three edges as stock finds it: strictly greater replaces,
    /// so the first of equal edges wins.
    /// </summary>
    private static float LongestEdge(Vec3 e0, Vec3 e1, Vec3 e2, out int longest)
    {
        float edgeLength = 0.0f;
        longest = -1;
        ReadOnlySpan<Vec3> edges = [e0, e1, e2];
        for (int i = 0; i < 3; i++)
        {
            if (edgeLength < edges[i].Length())
            {
                edgeLength = edges[i].Length();
                longest = i;
            }
        }

        return edgeLength;
    }

    // The `max`/`min` macros: `a > b ? a : b`, `a < b ? a : b`.
    private static float MaxMacro(float a, float b) => a > b ? a : b;

    private static float MinMacro(float a, float b) => a < b ? a : b;
}
