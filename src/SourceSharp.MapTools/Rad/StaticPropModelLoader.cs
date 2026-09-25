using System.Buffers;
using System.Buffers.Binary;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// Loads vrad's static prop model dictionary:
/// <c>CVradStaticPropMgr::UnserializeModelDict</c>
/// And the
/// <c>CreateCollisionModel</c> it calls once
/// per entry.
/// </summary>
/// <remarks>
/// <para>
/// Stock's decision tree, in order, is: load the <c>.mdl</c> or give up on
/// this entry with a zeroed hull; take the hull bounds from the header; take
/// collision from the <c>.phy</c> if there is a usable one and from a per-mesh
/// convex hull if there is not; keep a copy of the studio header; load the
/// <c>.vtx</c> or mark the entry's render mesh disabled. Reproduced here in
/// that order, because the order is what decides which of a broken model's
/// three files gets blamed.
/// </para>
/// <para>
/// One thing moves. Stock loads the <c>.vvd</c> LAZILY, from
/// <c>CVradStaticPropMgr::SetupStudioHdr</c>'s vertex-data callback
/// The first time anything asks a mesh for
/// its vertices; this loader reads it up front with the other three. The
/// difference is WHEN, not WHAT -- the bytes and the fixup pass are identical
/// -- and it buys an immutable <see cref="StaticPropModel"/> instead of one
/// that mutates on first use. Stock treats a missing <c>.vvd</c> as
/// <c>Error()</c>, a fatal abort; here it is a null
/// <see cref="StaticPropModel.Vvd"/> and the render-mesh path says so when it
/// needs one, so that the default path still runs on content whose
/// <c>.vvd</c> files are absent.
/// </para>
/// </remarks>
public sealed class StaticPropModelLoader
{
    private readonly IContentFileSystem _content;
    private readonly IPropCollisionSource _collision;
    private readonly HashSet<string> _forcedTextureShadows;

    /// <summary>Creates a loader over some content and a collision source.</summary>
    /// <param name="content">Where the <c>.mdl</c>, <c>.vvd</c>, <c>.vtx</c> and <c>.phy</c> live.</param>
    /// <param name="collision">Where a model's collision triangles come from.</param>
    /// <param name="forcedTextureShadowModels">
    /// The models <c>lights.rad</c> named in <c>forcetextureshadow</c> lines
    /// In any spelling;
    /// <see cref="StaticPropModel.CleanModelName"/> is applied to both sides.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="collision"/> is null.</exception>
    public StaticPropModelLoader(
        IContentFileSystem content,
        IPropCollisionSource collision,
        IReadOnlyList<string>? forcedTextureShadowModels = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(collision);

        _content = content;
        _collision = collision;
        _forcedTextureShadows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (forcedTextureShadowModels is not null)
        {
            foreach (string name in forcedTextureShadowModels)
            {
                _forcedTextureShadows.Add(StaticPropModel.CleanModelName(name));
            }
        }
    }

    /// <summary>
    /// Loads a whole <c>sprp</c> model dictionary, in lump order.
    /// </summary>
    /// <param name="modelNames">
    /// <see cref="MapFormats.Bsp.Structs.StaticPropLump.ModelNames"/>, which
    /// <see cref="MapFormats.Bsp.Structs.StaticProp.PropType"/> indexes.
    /// </param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>One entry per name, in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelNames"/> is null.</exception>
    /// <remarks>
    /// SEQUENTIAL, and in lump order, because the order is observable: stock's
    /// dictionary index IS the loop counter of <c>UnserializeModelDict</c>
    /// And every prop names its model by
    /// that index.
    /// </remarks>
    public async ValueTask<IReadOnlyList<StaticPropModel>> LoadDictionaryAsync(
        IReadOnlyList<string> modelNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelNames);

        List<StaticPropModel> models = new(modelNames.Count);
        foreach (string name in modelNames)
        {
            models.Add(await LoadAsync(name, cancellationToken).ConfigureAwait(false));
        }

        return models;
    }

    /// <summary>Loads one dictionary entry.</summary>
    /// <param name="modelName">The model path, as the dictionary spells it.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The entry, usable or not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    public async ValueTask<StaticPropModel> LoadAsync(
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelName);

        VPath path = VPath.Create(modelName);
        bool forced = _forcedTextureShadows.Contains(StaticPropModel.CleanModelName(modelName));

        byte[]? mdlBytes = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (mdlBytes is null)
        {
            return Rejected(path, StaticPropModelRejection.FileMissing, forced);
        }

        // Studio_ConvertStudioHdrToNewVersion, called from
        // LoadStudioModel -- BEFORE the version
 // test, which is the only reason that test ever passes.
        //
        // NOT OPTIONAL, and skipping it is not a small divergence: EVERY prop
        // model dm_lockdown references is studiohdr version 44, and without
        // this the whole map's props are rejected as "invalid model version"
        // and the -StaticPropPolys pass emits nothing at all.
        ConvertToCurrentStudioVersion(mdlBytes);

        MdlFile mdl;
        try
        {
            // MdlFile.Parse enforces both of LoadStudioModel's format tests:
            // the IDST/IDAG ident and
            // version == STUDIO_VERSION (:487).
            mdl = MdlFile.Parse(mdlBytes);
        }
        catch (InvalidStudioException)
        {
            return Rejected(path, StaticPropModelRejection.NotAStudioModel, forced);
        }

        // IsStaticProp. A model without $staticprop
        // has no usable vertex data here at all: its vertices are in bone
        // space and stock's loader does not set up bones, which is what the
 // warning is really saying.
        if ((mdl.Header.Flags & StaticPropModel.StudioFlagStaticProp) == 0)
        {
            return Rejected(path, StaticPropModelRejection.NotAStaticProp, forced);
        }

        Vec3 hullMin = mdl.Header.HullMin;
        Vec3 hullMax = mdl.Header.HullMax;

        int physicsSolidCount = await PhysicsSolidCountAsync(path, cancellationToken)
            .ConfigureAwait(false);

        PropCollisionMesh? collision =
            await _collision.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        PropCollisionKind kind = collision is null
            ? PropCollisionKind.None
            : physicsSolidCount > 0 ? PropCollisionKind.PhysicsFile : PropCollisionKind.RenderHull;

        VvdFile? vvd = null;
        StudioVertex[] vertices = [];
        byte[]? vvdBytes = await ReadAsync(
            WithExtension(path, ".vvd"), cancellationToken).ConfigureAwait(false);
        if (vvdBytes is not null)
        {
            try
            {
                vvd = VvdFile.Parse(vvdBytes);

                // Studio_LoadVertexes( pVvdHdr, pNewVvdHdr, 0, true ),
                // LOD 0, fixups applied.
                vertices = vvd.VerticesForLod(0);
            }
            catch (InvalidStudioException)
            {
                vvd = null;
                vertices = [];
            }
        }

        VtxFile? vtx = await LoadVtxAsync(path, mdl, cancellationToken).ConfigureAwait(false);

        return new StaticPropModel(
            path,
            StaticPropModelRejection.None,
            mdl,
            vvd,
            vtx,
            vertices,
            hullMin,
            hullMax,
            physicsSolidCount,
            collision,
            kind,
            forced);
    }

    /// <summary>
    /// <c>LoadVTXFile</c>.
    /// </summary>
    /// <remarks>
    /// <c>.dx80.vtx</c>, NOT <c>.dx90.vtx</c> and not the bare <c>.vtx</c>
    /// The three differ: a dx90 file can
    /// carry more strip groups and a different vertex ordering for hardware
    /// skinning, so reading the wrong one gives a triangle count that is
    /// plausible, close, and not stock's. The version and the checksum are
    /// both checked, and a failure is not fatal -- stock purges the buffer and
    /// leaves the entry's render mesh disabled.
    /// </remarks>
    private async ValueTask<VtxFile?> LoadVtxAsync(
        VPath path,
        MdlFile mdl,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadAsync(WithExtension(path, ".dx80.vtx"), cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        VtxFile vtx;
        try
        {
            vtx = VtxFile.Parse(bytes);
        }
        catch (InvalidStudioException)
        {
            return null;
        }

        // -- a VTX whose checksum does not match the
        // MDL is a stale compile of the same model, and its indices name
        // vertices that moved.
        return vtx.Checksum == mdl.Checksum ? vtx : null;
    }

    /// <summary>
    /// <c>LoadStudioCollisionModel</c>'s gate.
    /// </summary>
    /// <remarks>
    /// The whole of stock's test, and nothing more: the file is there, its
    /// <c>header.size</c> equals <c>sizeof(phyheader_t)</c> -- 16 -- and
    /// <c>solidCount</c> is positive. Note what is NOT tested: the checksum.
    /// Stock never compares a <c>.phy</c>'s checksum against the MDL's here,
    /// so a stale physics file is loaded and cast from without complaint.
    /// </remarks>
    private async ValueTask<int> PhysicsSolidCountAsync(
        VPath path,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadAsync(WithExtension(path, ".phy"), cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            // "this is not an error, the model simply has no PHY file"
            return 0;
        }

        try
        {
            PhyFile phy = PhyFile.Parse(bytes);
            return phy.Header.Size == PhyHeaderSize && phy.Header.SolidCount > 0
                ? phy.Header.SolidCount
                : 0;
        }
        catch (InvalidStudioException)
        {
            return 0;
        }
    }

    /// <summary><c>sizeof(phyheader_t)</c>.</summary>
    private const int PhyHeaderSize = 16;

    /// <summary>
    /// <c>Studio_ConvertStudioHdrToNewVersion</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole of it, for a static prop. The function's own comment is "for
    /// now, just slam the version number since they're compatible": the only
    /// real work it does is to ANIMATION descriptors -- zeroing v45 section
    /// frames and v47 zeroframe caches -- and a static prop's caster path
    /// reads the header, the body parts, the meshes and the textures, none of
    /// which moved between 44 and 48. So the conversion reduces to writing the
    /// current version into the header, in place, exactly as stock does to its
    /// own buffer.
    /// </para>
    /// <para>
    /// Stock puts NO FLOOR under this, and neither does this: a file claiming
    /// version 1 is slammed to 48 and read with the 48 layout. That is stock's
    /// exposure and is reproduced rather than tightened, but it is bounded
    /// here in a way it is not in C++ -- every offset a studio reader follows
    /// in this port is range checked against the file's length, so a header
    /// that is not really a version 44 one fails as
    /// <see cref="StaticPropModelRejection.NotAStudioModel"/> instead of
    /// reading whatever follows the buffer.
    /// </para>
    /// </remarks>
    private static void ConvertToCurrentStudioVersion(byte[] bytes)
    {
        const int versionAt = 4;
        if (bytes.Length < versionAt + sizeof(int))
        {
            return;
        }

        Span<byte> version = bytes.AsSpan(versionAt, sizeof(int));
        if (BinaryPrimitives.ReadInt32LittleEndian(version) != StudioIdents.MdlVersion)
        {
            BinaryPrimitives.WriteInt32LittleEndian(version, StudioIdents.MdlVersion);
        }
    }

    private static StaticPropModel Rejected(
        VPath path,
        StaticPropModelRejection rejection,
        bool forcedTextureShadows) =>

        // The dictionary entry is kept, its model
        // pointer stays null, and BOTH hull corners are set to vec3_origin
        // rather than left at whatever the header would have said. That zero
        // hull is what makes stock's AABB fallback a degenerate point box.
        new(
            path,
            rejection,
            mdl: null,
            vvd: null,
            vtx: null,
            vertices: [],
            hullMin: default,
            hullMax: default,
            physicsSolidCount: 0,
            collision: null,
            collisionKind: PropCollisionKind.None,
            forcedTextureShadows);

    /// <summary>
    /// <c>Q_StripExtension</c> then the new extension:
    /// </summary>
    private static VPath WithExtension(VPath path, string extension)
    {
        string value = path.Value;
        int slash = value.LastIndexOf('/');
        int dot = value.LastIndexOf('.');
        string stem = dot > slash ? value[..dot] : value;
        return VPath.Create(stem + extension);
    }

    /// <summary>
    /// Reads a content file into an array this object may keep.
    /// </summary>
    /// <remarks>
    /// The copy is not optional. <see cref="IContentFileSystem.ReadAsync"/>
    /// hands back a POOLED buffer that goes back to the pool on dispose, and
    /// <see cref="MdlFile"/>, <see cref="VvdFile"/>, <see cref="VtxFile"/> and
    /// <see cref="PhyFile"/> all keep a <see cref="ReadOnlyMemory{T}"/> over
    /// the bytes they were parsed from. Aliasing one would give the next
    /// reader's file to this model's accessors.
    /// </remarks>
    private async ValueTask<byte[]?> ReadAsync(VPath path, CancellationToken cancellationToken)
    {
        using IMemoryOwner<byte>? owner =
            await _content.ReadAsync(path, cancellationToken).ConfigureAwait(false);

        return owner?.Memory.ToArray();
    }
}
