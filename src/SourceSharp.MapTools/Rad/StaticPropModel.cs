using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// Why a static prop model is not usable as a shadow caster, or
/// <see cref="StaticPropModelRejection.None"/> when it is.
/// </summary>
/// <remarks>
/// Stock has no such enum: <c>LoadStudioModel</c>
/// (<c>vradstaticprops.cpp:464</c>) prints four different <c>Warning</c>s and
/// returns the same <c>false</c> for all of them, and
/// <c>CreateCollisionModel</c> then leaves the dictionary entry with a null
/// model and a zeroed hull. Recording WHICH warning fired costs one field and
/// is the difference between "261 props, 34 skipped" and a compile that
/// silently boxed half the map.
/// </remarks>
public enum StaticPropModelRejection
{
    /// <summary>The model loaded and is a static prop.</summary>
    None,

    /// <summary>
    /// The <c>.mdl</c> is not in the content: <c>"Unable to load model"</c>,
    /// <c>vradstaticprops.cpp:470</c>.
    /// </summary>
    FileMissing,

    /// <summary>
    /// The ident is neither <c>IDST</c> nor <c>IDAG</c>, or the version is not
    /// <c>STUDIO_VERSION</c> (48, <c>studio.h:70</c>):
    /// <c>vradstaticprops.cpp:476</c> and <c>:487</c>.
    /// </summary>
    NotAStudioModel,

    /// <summary>
    /// The model was not compiled with <c>$staticprop</c>, so
    /// <c>STUDIOHDR_FLAGS_STATIC_PROP</c> is clear and <c>IsStaticProp</c>
    /// (<c>vradstaticprops.cpp:378</c>) says no.
    /// </summary>
    NotAStaticProp,
}

/// <summary>
/// Which of stock's two collision roads a model's caster geometry came down.
/// </summary>
public enum PropCollisionKind
{
    /// <summary>
    /// None: the caller falls back to the model's axis-aligned hull box
    /// (<c>vradstaticprops.cpp:1861</c>).
    /// </summary>
    None,

    /// <summary>
    /// The model's <c>.phy</c>, through <c>VCollideLoad</c>
    /// (<c>vradstaticprops.cpp:967</c>).
    /// </summary>
    PhysicsFile,

    /// <summary>
    /// A convex hull per render mesh, through <c>ComputeConvexHull</c>
    /// (<c>vradstaticprops.cpp:433</c>), which stock uses only when there is
    /// no usable <c>.phy</c>.
    /// </summary>
    RenderHull,
}

/// <summary>
/// One entry of vrad's static prop model dictionary: <c>StaticPropDict_t</c>
/// (<c>vradstaticprops.cpp:150</c>), loaded.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and one per DICTIONARY entry rather than one per prop -- which
/// is the load-bearing part. <c>dm_lockdown</c> has 261 props over a few dozen
/// distinct models, so loading per prop would read each <c>.mdl</c>,
/// <c>.vvd</c> and <c>.vtx</c> several times over; and stock's texture-shadow
/// material cache (<c>vradstaticprops.cpp:1876</c>'s
/// <c>dict.m_triangleMaterialIndex</c>) is keyed on the dictionary entry, so
/// sharing is observable in the output and not only in the clock.
/// </para>
/// <para>
/// The three studio files are held as PARSED objects over their own copies of
/// the bytes. <see cref="IContentFileSystem.ReadAsync"/> hands back a pooled
/// buffer that is returned on dispose, and <see cref="MdlFile"/> and friends
/// keep a <see cref="ReadOnlyMemory{T}"/> over what they were given, so the
/// loader copies rather than aliasing a buffer that will be handed to the next
/// reader.
/// </para>
/// </remarks>
public sealed class StaticPropModel
{
    /// <summary>
    /// <c>STUDIOHDR_FLAGS_STATIC_PROP</c>, <c>studio.h:2039</c>.
    /// </summary>
    /// <remarks>
    /// Defined here rather than in <c>StudioStructs.cs</c> because that file
    /// carries the structs and this is the one flag vrad's prop loader reads;
    /// a later lane that needs the whole <c>STUDIOHDR_FLAGS_</c> set should
    /// move it rather than copy it.
    /// </remarks>
    public const int StudioFlagStaticProp = 0x00000010;

    /// <summary>
    /// <c>STUDIOHDR_FLAGS_CAST_TEXTURE_SHADOWS</c>, <c>studio.h:2088</c>.
    /// </summary>
    public const int StudioFlagCastTextureShadows = 0x00040000;

    private readonly StudioVertex[] _vertices;

    internal StaticPropModel(
        VPath path,
        StaticPropModelRejection rejection,
        MdlFile? mdl,
        VvdFile? vvd,
        VtxFile? vtx,
        StudioVertex[] vertices,
        Vec3 hullMin,
        Vec3 hullMax,
        int physicsSolidCount,
        PropCollisionMesh? collision,
        PropCollisionKind collisionKind,
        bool forcedTextureShadows)
    {
        Path = path;
        Rejection = rejection;
        Mdl = mdl;
        Vvd = vvd;
        Vtx = vtx;
        _vertices = vertices;
        HullMin = hullMin;
        HullMax = hullMax;
        PhysicsSolidCount = physicsSolidCount;
        Collision = collision;
        CollisionKind = collisionKind;
        ForcedTextureShadows = forcedTextureShadows;
    }

    /// <summary>
    /// <c>sizeof(mstudiovertex_t)</c>, which the global vertex index divides by.
    /// </summary>
    /// <remarks>
    /// Read off the managed struct rather than written down as 48, because the
    /// number that matters is the one this port's reader will actually stride
    /// by. <c>studio.h:1204</c> says 48 and a fact pins the agreement; if the
    /// two ever part, the fact fails instead of the geometry quietly shifting.
    /// </remarks>
    public static int VertexStride => Unsafe.SizeOf<StudioVertex>();

    /// <summary>The model's content path, as the <c>sprp</c> dictionary spells it.</summary>
    public VPath Path { get; }

    /// <summary>Why the model is unusable, or <see cref="StaticPropModelRejection.None"/>.</summary>
    public StaticPropModelRejection Rejection { get; }

    /// <summary>Whether the model loaded and is a static prop.</summary>
    public bool IsUsable => Rejection == StaticPropModelRejection.None;

    /// <summary>The parsed <c>.mdl</c>, or null when it did not load.</summary>
    public MdlFile? Mdl { get; }

    /// <summary>The parsed <c>.vvd</c>, or null when it is not in the content.</summary>
    public VvdFile? Vvd { get; }

    /// <summary>The parsed <c>.dx80.vtx</c>, or null when it did not load.</summary>
    /// <remarks>
    /// Null is stock's <c>m_VtxBuf.Purge()</c>
    /// (<c>vradstaticprops.cpp:1000</c>) -- "failed, leave state identified as
    /// disabled" -- and it is one of the two things that make
    /// <c>AddPolysForRayTrace</c> abandon the whole prop loop.
    /// </remarks>
    public VtxFile? Vtx { get; }

    /// <summary>
    /// The LOD 0 vertices with the <c>.vvd</c> fixup table applied.
    /// </summary>
    /// <remarks>
    /// <c>VvdFile.VerticesForLod(0)</c>, which is
    /// <c>Studio_LoadVertexes( pVvdHdr, pNewVvdHdr, 0, true )</c>
    /// (<c>vradstaticprops.cpp:2235</c>) exactly: walk the fixup table and copy
    /// every run whose lod is at least 0, which is all of them. The RAW vertex
    /// block is a different order and indexing it with a mesh's
    /// <c>origMeshVertID</c> produces geometry that is scrambled within each
    /// model rather than obviously broken.
    /// </remarks>
    public ReadOnlySpan<StudioVertex> Vertices => _vertices;

    /// <summary>
    /// The model's movement hull minimum, <c>studiohdr_t::hull_min</c>.
    /// </summary>
    /// <remarks>
    /// ZERO WHEN THE MODEL DID NOT LOAD, which is stock's
    /// <c>VectorCopy( vec3_origin, m_Mins )</c>
    /// (<c>vradstaticprops.cpp:953</c>) and matters more than it looks: a
    /// failed load is the ONLY way stock reaches the AABB fallback at
    /// <c>:1861</c>, so the box stock actually adds there is a DEGENERATE
    /// point box at the prop's origin -- twelve zero-area triangles, not a
    /// bounding box. This port reaches the same branch for a model with no
    /// collision source as well (see <see cref="NullPropCollisionSource"/>),
    /// where the hull is real.
    /// </remarks>
    public Vec3 HullMin { get; }

    /// <summary>The model's movement hull maximum, <c>studiohdr_t::hull_max</c>.</summary>
    public Vec3 HullMax { get; }

    /// <summary>
    /// How many solids a usable <c>.phy</c> declared; zero when there is none.
    /// </summary>
    /// <remarks>
    /// "Usable" is <c>LoadStudioCollisionModel</c>'s whole test
    /// (<c>vradstaticprops.cpp:519</c>): the file is there, its
    /// <c>header.size</c> equals <c>sizeof(phyheader_t)</c>, and
    /// <c>solidCount</c> is positive. Anything else is "the model simply has
    /// no PHY file" -- not an error.
    /// </remarks>
    public int PhysicsSolidCount { get; }

    /// <summary>Whether the model has a usable <c>.phy</c>.</summary>
    public bool HasPhysicsFile => PhysicsSolidCount > 0;

    /// <summary>
    /// How many of a <c>.phy</c>'s solids stock's shadow path actually casts
    /// from: one, however many the file holds.
    /// </summary>
    /// <remarks>
    /// A STOCK DEFECT, pinned rather than fixed.
    /// <c>vradstaticprops.cpp:971</c> is
    /// <c>m_pModel = m_loadedModel.solids[0]</c> -- the dictionary keeps the
    /// whole <c>vcollide_t</c> but the caster path only ever queries the first
    /// solid, so every solid after the first in a multi-solid <c>.phy</c>
    /// casts no shadow at all. Models compiled with several physics pieces
    /// (a door and its frame in one file, say) therefore shadow as one piece.
    /// Reproduced because the caster set is gated against stock's; naming it
    /// here so that casting from all of them later is a decision and not a
    /// slip.
    /// </remarks>
    public int PhysicsSolidsUsedByStock => Math.Min(PhysicsSolidCount, 1);

    /// <summary>The model's collision triangles in model space, or null.</summary>
    public PropCollisionMesh? Collision { get; }

    /// <summary>Which road <see cref="Collision"/> came down.</summary>
    public PropCollisionKind CollisionKind { get; }

    /// <summary>
    /// Whether <c>lights.rad</c> named this model in a
    /// <c>forcetextureshadow</c> line.
    /// </summary>
    public bool ForcedTextureShadows { get; }

    /// <summary>
    /// Whether the model opts into texture shadows at all:
    /// <c>vradstaticprops.cpp:1006</c>.
    /// </summary>
    public bool CastsTextureShadows =>
        ForcedTextureShadows
        || (Mdl is not null && (Mdl.Header.Flags & StudioFlagCastTextureShadows) != 0);

    /// <summary>
    /// Strips <c>models/</c> and the extension off a model path, the way
    /// <c>CleanModelName</c> does (<c>vradstaticprops.cpp:899</c>).
    /// </summary>
    /// <param name="modelName">The model path.</param>
    /// <returns>The cleaned name, for comparing against a <c>lights.rad</c> line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    /// <remarks>
    /// Truncates at the FIRST dot, not the last, because stock uses
    /// <c>strchr</c>. A model under a directory with a dot in its name is
    /// therefore cut short -- reproduced, since the forced-shadow list is
    /// matched against exactly these strings.
    /// </remarks>
    public static string CleanModelName(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);

        string name = modelName.Replace('\\', '/');
        if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            name = name["models/".Length..];
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// The first global vertex index of one mesh:
    /// <c>mstudio_meshvertexdata_t::Position(0)</c> resolved.
    /// </summary>
    /// <param name="model">The owning <c>mstudiomodel_t</c>.</param>
    /// <param name="mesh">The mesh.</param>
    /// <returns>An index into <see cref="Vertices"/>.</returns>
    /// <remarks>
    /// <para>
    /// Two indirections collapsed, both from <c>studio.h</c>:
    /// <c>mstudio_meshvertexdata_t::GetModelVertexIndex</c>
    /// (<c>studio.h:1532</c>) adds the MESH's <c>vertexoffset</c>, which is an
    /// index; then
    /// <c>mstudio_modelvertexdata_t::GetGlobalVertexIndex</c>
    /// (<c>studio.h:1456</c>) adds the MODEL's <c>vertexindex</c> DIVIDED BY
    /// <see cref="VertexStride"/>, because that one is a byte offset.
    /// </para>
    /// <para>
    /// The two fields are spelled differently in C++ for exactly this reason
    /// (<c>vertexoffset</c> against <c>vertexindex</c>) and treating either as
    /// the other kind is the classic way to read a studio model 48 times too
    /// far into the file, or a mesh's worth of vertices short.
    /// </para>
    /// </remarks>
    public static int MeshVertexBase(StudioModel model, StudioMesh mesh) =>
        (model.VertexIndex / VertexStride) + mesh.VertexOffset;

    /// <summary>One LOD 0 vertex's position, in model space.</summary>
    /// <param name="globalVertexIndex">An index into <see cref="Vertices"/>.</param>
    /// <returns>The position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is not a vertex of this model.</exception>
    public Vec3 VertexPosition(int globalVertexIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalVertexIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            globalVertexIndex, _vertices.Length);

        return _vertices[globalVertexIndex].Position;
    }

    /// <summary>One LOD 0 vertex's texture coordinate.</summary>
    /// <param name="globalVertexIndex">An index into <see cref="Vertices"/>.</param>
    /// <returns>The coordinate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is not a vertex of this model.</exception>
    public PropTexCoord VertexTexCoord(int globalVertexIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalVertexIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            globalVertexIndex, _vertices.Length);

        FloatArray2 uv = _vertices[globalVertexIndex].TexCoord;
        return new PropTexCoord(uv[0], uv[1]);
    }
}
