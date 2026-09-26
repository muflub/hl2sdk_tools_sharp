//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>MakePatches</c> and <c>MakePatchForFace</c>:
/// one root patch per lit brush face.
/// </summary>
/// <remarks>
/// <para>
/// Radiosity's unit of surface. Everything downstream -- surface lights,
/// transfers, bounce, the per-face sample push-up -- is a statement about
/// patches, so this pass decides the resolution of the whole indirect solve.
/// </para>
/// <para>
/// <b>Displacements are NOT made here.</b> Stock's last line calls
/// <c>StaticDispMgr-&gt;MakePatches</c>, which
/// tessellates each displacement into its own patch tree. That belongs to the
/// displacement lane; <see cref="Build"/> covers the brush half and the patch
/// set it returns is open for that pass to append to.
/// </para>
/// <para>
/// <b>This pass MUTATES the texinfo lump.</b> A face whose material has a
/// non-zero texlight value gets <c>SURF_LIGHT</c> OR-ed into its texinfo
/// -- on the SHARED texinfo, so one emitting face turns
/// the flag on for every face that reuses the same texinfo. It is then read by
/// <c>PreventSubdivision</c> to decide that a
/// <c>SURF_NOLIGHT</c> surface which also emits must still be chopped. The flag
/// is never written back to the BSP, so this is compile-local state carried in
/// the lump array; <see cref="LightGeometry.TexInfos"/> being a copy is what
/// makes that safe.
/// </para>
/// </remarks>
public static class PatchBuilder
{
    /// <summary>
    /// <c>reflectivityScale</c>: 1.0, and not settable in
    /// a release build.
    /// </summary>
    public const float ReflectivityScale = 1.0f;

    /// <summary>
    /// The ceiling a face's reflectivity is clamped to: 0.99
    /// </summary>
    /// <remarks>
    /// Stock's comment says it plainly -- "always keep this less than 1 or the
    /// solution will not converge". A surface that reflects everything it
    /// receives makes the bounce series divergent, so this is a stability
    /// bound rather than a physical one.
    /// </remarks>
    public const float MaxReflectivity = 0.99f;

    /// <summary>
    /// The chop scale a face gets when <c>-notexscale</c> is in force: 16
    /// </summary>
    /// <remarks>
    /// Unreachable in a release build -- <c>texscale</c> is only cleared inside
    /// <c>#if ALLOWDEBUGOPTIONS</c>, which defines as
    /// <c>(0 || _DEBUG)</c> -- but it is the initialiser, and
    /// <see cref="Build"/> takes the flag so the branch is exercised rather
    /// than assumed dead.
    /// </remarks>
    public const float FixedChopScale = 16.0f;

    /// <summary>
    /// Makes one patch per lit brush face of every model.
    /// </summary>
    /// <param name="geometry">The map's lumps. Its texinfo flags are MUTATED; see the remarks.</param>
    /// <param name="entities">The parsed entity lump, in file order.</param>
    /// <param name="texLights">The texlight table.</param>
    /// <param name="maxChop">
    /// <c>maxchop</c>: the coarsest patch width in luxels, stock's 4.
    /// </param>
    /// <param name="texScale">
    /// <c>texscale</c>: true in every release build.
    /// </param>
    /// <returns>The patch set, with <c>face_offset</c> and the centroids filled.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public static PatchSet Build(
        LightGeometry geometry,
        IReadOnlyList<BspEntity> entities,
        TextureLightTable texLights,
        float maxChop,
        bool texScale = true)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(entities);
                ArgumentNullException.ThrowIfNull(texLights);

        WindingArena arena = new();
        PatchSet patches = new(geometry.Faces.Length, geometry.ClusterCount, arena);

        for (int modelIndex = 0; modelIndex < geometry.Models.Length; modelIndex++)
        {
            ref readonly DModel model = ref geometry.Models[modelIndex];

            // The entity that references "*<n>", or
            // worldspawn when nothing does.
            int entityIndex = EntityForModel(entities, modelIndex);
            Vec3 origin = entityIndex >= 0
                ? EntityKeys.GetVectorForKey(entities[entityIndex], "origin")
                : Vec3.Zero;

            for (int j = 0; j < model.NumFaces; j++)
            {
                int faceNum = model.FirstFace + j;
                patches.FaceEntities[faceNum] = entityIndex;
                patches.FaceOffsets[faceNum] = origin;

                // Displacements are skipped here and made by the
                // displacement manager instead.
                if (geometry.Faces[faceNum].DispInfo != -1)
                {
                    continue;
                }

                Winding w = geometry.WindingFromFace(arena, faceNum, origin);
                MakePatchForFace(geometry, patches, texLights, faceNum, w, maxChop, texScale);
            }
        }

        return patches;
    }

    /// <summary>
    /// <c>EntityForModel</c>.
    /// </summary>
    /// <param name="entities">The entity list.</param>
    /// <param name="modelIndex">The submodel number.</param>
    /// <returns>
    /// The index of the entity whose <c>model</c> key is <c>*&lt;n&gt;</c>, or 0
    /// -- worldspawn -- when no entity claims it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    /// <remarks>
    /// The fallback is <c>&amp;entities[0]</c> and NOT null, so an orphaned
    /// submodel takes worldspawn's origin, which is almost always absent and so
    /// zero. That is how model 0 -- the world, which nothing references as
    /// <c>*0</c> -- ends up unoffset.
    /// </remarks>
    public static int EntityForModel(IReadOnlyList<BspEntity> entities, int modelIndex)
    {
        ArgumentNullException.ThrowIfNull(entities);

        string name = $"*{modelIndex}";
        for (int i = 0; i < entities.Count; i++)
        {
            if (string.Equals(EntityKeys.ValueForKey(entities[i], "model"), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return entities.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// <c>BaseLightForFace</c>: a face's emission, texel
    /// area and reflectivity.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="texLights">The texlight table.</param>
    /// <param name="faceNum">The face.</param>
    /// <returns>
    /// The emitted colour, the material's texel area, and its clamped
    /// reflectivity.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// The "area" is <c>height * width</c> IN TEXELS, taken from the texdata
    /// lump. It divides the emitted intensity later, which is what makes a
    /// texlight's brightness per texture instance rather than per world area.
    /// </remarks>
    public static (Vec3 Light, float BaseArea, Vec3 Reflectivity) BaseLightForFace(
        LightGeometry geometry,
        TextureLightTable texLights,
        int faceNum)
    {
        ArgumentNullException.ThrowIfNull(geometry);
                ArgumentNullException.ThrowIfNull(texLights);

        ref readonly TexInfo tex = ref geometry.TexInfos[geometry.Faces[faceNum].TexInfo];
        ref readonly DTexData texData = ref geometry.TexDatas[tex.TexData];

        Vec3 light = texLights.Lookup(geometry.TexDataName(texData.NameStringTableId));

        Vec3 reflectivity = texData.Reflectivity * ReflectivityScale;
        reflectivity = new Vec3(
            Math.Min(reflectivity.X, MaxReflectivity),
            Math.Min(reflectivity.Y, MaxReflectivity),
            Math.Min(reflectivity.Z, MaxReflectivity));

        return (light, (float)texData.Height * texData.Width, reflectivity);
    }

    private static void MakePatchForFace(
        LightGeometry geometry,
        PatchSet patches,
        TextureLightTable texLights,
        int faceNum,
        Winding winding,
        float maxChop,
        bool texScale)
    {
        WindingArena arena = patches.Arena;
        ref readonly DFace face = ref geometry.Faces[faceNum];
        ref TexInfo tex = ref geometry.TexInfos[face.TexInfo];

        // A face with no area is counted and dropped, and
        // its winding is LEAKED in stock; here the arena takes it back.
        float area = arena.Area(winding);
        if (area <= 0)
        {
            patches.DegenerateFaces++;
            arena.Free(winding);
            return;
        }

        patches.TotalArea += area;

        Patch patch = default;
        patch.Next = Patch.Invalid;
        patch.NextParent = Patch.Invalid;
        patch.NextClusterChild = Patch.Invalid;
        patch.Child1 = Patch.Invalid;
        patch.Child2 = Patch.Invalid;
        patch.Parent = Patch.Invalid;
        patch.NeedsBumpmap = (tex.Flags & (int)SurfaceFlags.BumpLight) != 0;

        // TWO scales from the same shape: patch->scale from the
        // TEXTURE axes, chopscale from the LIGHTMAP axes. They are different
        // numbers and are used for different things -- scale multiplies a
        // surface light's intensity, chopscale decides how
        // finely the patch is subdivided -- so conflating them changes both
        // the brightness and the resolution of every texlight.
        float chopScaleS = FixedChopScale;
        float chopScaleT = FixedChopScale;

        if (texScale)
        {
            patch.ScaleS = AxisLength(tex.TextureVecsTexelsPerWorldUnits, 0);
            patch.ScaleT = AxisLength(tex.TextureVecsTexelsPerWorldUnits, 1);
            chopScaleS = AxisLength(tex.LightmapVecsLuxelsPerWorldUnits, 0);
            chopScaleT = AxisLength(tex.LightmapVecsLuxelsPerWorldUnits, 1);
        }
        else
        {
            patch.ScaleS = 1.0f;
            patch.ScaleT = 1.0f;
        }

        patch.Area = area;
        patch.Sky = geometry.IsSky(faceNum);
        patch.LuxScale = (chopScaleS + chopScaleT) / 2f;
        patch.Chop = maxChop;
        patch.Winding = winding;

        // The face's plane, displaced along its own normal when the
        // owning model has an origin brush. Stock appends a FAKE PLANE past
        // numplanes for this; held inline here.
        ref readonly DPlane plane = ref geometry.Planes[face.PlaneNum];
        patch.PlaneNormal = plane.Normal;
        patch.PlaneDist = plane.Dist;

        Vec3 offset = patches.FaceOffsets[faceNum];
        if (offset.X != 0f || offset.Y != 0f || offset.Z != 0f)
        {
            patch.PlaneDist += Vec3.Dot(offset, plane.Normal);
        }

        patch.FaceNumber = faceNum;
        patch.Origin = arena.Center(winding);

        // The centroid PhongNormals reads is the patch origin with the
        // model offset taken back off, so it lives in the same space as
        // dvertexes.
        patches.Centroids[faceNum] = patch.Origin - offset;

        patch.Normal = patch.PlaneNormal;

        arena.Bounds(winding, out Vec3 mins, out Vec3 maxs);
        patch.FaceMins = mins;
        patch.FaceMaxs = maxs;
        patch.Mins = mins;
        patch.Maxs = maxs;

        (Vec3 baseLight, float baseArea, Vec3 reflectivity) =
            BaseLightForFace(geometry, texLights, faceNum);
        patch.BaseLight = baseLight;
        patch.BaseArea = baseArea;
        patch.Reflectivity = reflectivity;

        // An emitting material turns SURF_LIGHT on for every face
        // that shares the texinfo. See the type remarks.
        if (baseLight != Vec3.Zero)
        {
            tex.Flags |= (int)SurfaceFlags.Light;
        }

        // A no-op -- chop is already maxchop -- and kept because
        // stock's comment says it is deliberately undoing -extra, which a
        // future change to the line above would silently break.
        if (geometry.IsValidDispFace(faceNum))
        {
            patch.Chop = maxChop;
        }

        // Prepend to the face's list; the head moves to the new
        // patch and the old head becomes its Next.
        patch.Next = patches.FacePatches[faceNum];
        int index = patches.Add(patch);
        patches.FacePatches[faceNum] = index;
    }

    private static float AxisLength(FloatArray8 axes, int row)
    {
        // The reference sums the squares of components 0..2 of row `row` and takes
        // the square root:.
        int b = row * 4;
        float sum = (axes[b] * axes[b])
            + (axes[b + 1] * axes[b + 1])
            + (axes[b + 2] * axes[b + 2]);
        return MathF.Sqrt(sum);
    }
}
