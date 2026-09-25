using System.Buffers;
using System.Buffers.Binary;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Props;

/// <summary>Why <c>LoadStudioModel</c> refused a model.</summary>
public enum StudioModelRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>No such file, or not <c>IDST</c>/<c>IDAG</c> (<c>staticprop.cpp:151-159</c>).</summary>
    Unreadable,

    /// <summary>Not compiled with <c>$staticprop</c> (<c>staticprop.cpp:104-105</c>).</summary>
    NotStaticProp,

    /// <summary>
    /// Has <c>prop_data</c> without <c>allowstatic</c>: must be dynamic
    /// (<c>staticprop.cpp:111-118</c>).
    /// </summary>
    DynamicOnly,
}

/// <summary>A studio model as vbsp's <c>LoadStudioModel</c> leaves it.</summary>
/// <param name="Name">The model path as the entity spelled it.</param>
/// <param name="Rejection">Why it was refused, or <see cref="StudioModelRejection.None"/>.</param>
/// <param name="Mdl">The parsed header, when it parsed.</param>
public sealed record StudioModelLoad(string Name, StudioModelRejection Rejection, MdlFile? Mdl)
{
    /// <summary>Whether vbsp accepts the model.</summary>
    public bool IsValid => Rejection == StudioModelRejection.None;
}

/// <summary>
/// <c>LoadStudioModel</c> and <c>IsStaticProp</c>
/// (<c>utils/vbsp/staticprop.cpp:102-190</c>), shared by the static and the
/// detail prop emitters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Old model versions load.</b> <c>Studio_ConvertStudioHdrToNewVersion</c>
/// runs BEFORE the version test (<c>staticprop.cpp:163-168</c>) and ends by
/// slamming <c>version = STUDIO_VERSION</c> whatever it was
/// (<c>public/studio.h:3171-3172</c>), so the test at <c>:165</c> can never
/// fire and a version 44 HL2 model is accepted. Its only other effect is on
/// animation data vbsp never reads. This slams 44..47 the same way. Slamming
/// a version outside that range is a defect -- the file has another layout --
/// reproduced only under <see cref="StockQuirk.StudioVersionSlam"/>; under
/// Correct such a model is refused like an unreadable one. Either way a model
/// that fails to parse is a warning and a skipped prop, never an exception.
/// </para>
/// </remarks>
public static class StudioModelCheck
{
    /// <summary>
    /// Loads and checks one model, adding stock's warnings.
    /// </summary>
    /// <param name="content">Where models are read from.</param>
    /// <param name="modelName">The <c>model</c> key.</param>
    /// <param name="entityType">"prop_static" or "detail_prop", for the message.</param>
    /// <param name="diagnostics">Receives the warnings.</param>
    /// <param name="compliance">
    /// The compile's compliance: whether a version outside 44..48 is slammed
    /// and read (<see cref="StockQuirk.StudioVersionSlam"/>) or refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The result.</returns>
    public static async ValueTask<StudioModelLoad> LoadAsync(
        IContentFileSystem content,
        string modelName,
        string entityType,
        ICollection<CompileDiagnostic> diagnostics,
        ComplianceOptions compliance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(compliance);

        byte[]? bytes = null;
        if (VPath.TryCreate(modelName, out VPath path) && !path.IsEmpty)
        {
            using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            bytes = owner?.Memory.ToArray();
        }

        if (bytes is null || bytes.Length < 8)
        {
            return new StudioModelLoad(modelName, StudioModelRejection.Unreadable, null);
        }

        int ident = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (ident != StudioIdents.Mdl && ident != StudioIdents.AnimationGroup)
        {
            return new StudioModelLoad(modelName, StudioModelRejection.Unreadable, null);
        }

        // studio.h:3171-3172 slams ANY version to 48 and staticprop.cpp:165's
        // check can then never fire. Valve's comment is "they're compatible",
        // which holds for 44..47 (the conversion above it fixes those); a
        // model older than 44 or newer than 48 has another layout and is read
        // as garbage. Correct refuses those (StockQuirk.StudioVersionSlam).
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (version is < OldestCompatibleVersion or > StudioIdents.MdlVersion &&
            !compliance.Emulates(StockQuirk.StudioVersionSlam))
        {
            return new StudioModelLoad(modelName, StudioModelRejection.Unreadable, null);
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), StudioIdents.MdlVersion);

        MdlFile mdl;
        try
        {
            mdl = MdlFile.Parse(bytes);
        }
        catch (InvalidStudioException)
        {
            return new StudioModelLoad(modelName, StudioModelRejection.Unreadable, null);
        }

        if ((mdl.Header.Flags & StaticPropFlag) == 0)
        {
            diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.StudioModelNotStaticProp,
                DiagnosticSeverity.Warning,
                $"Error! To use model \"{modelName}\"\n      with {entityType}, it must be compiled with $staticprop!"));
            return new StudioModelLoad(modelName, StudioModelRejection.NotStaticProp, mdl);
        }

        if (await IsDynamicOnlyAsync(mdl, cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.StudioModelDynamicOnly,
                DiagnosticSeverity.Warning,
                $"Error! {entityType} using model \"{modelName}\", which must be used on a dynamic entity (i.e. prop_physics). Deleted."));
            return new StudioModelLoad(modelName, StudioModelRejection.DynamicOnly, mdl);
        }

        return new StudioModelLoad(modelName, StudioModelRejection.None, mdl);
    }

    /// <summary>
    /// The oldest version <c>Studio_ConvertStudioHdrToNewVersion</c> knows how
    /// to convert (its oldest branch is <c>version &lt; 46</c> for v45's
    /// sectioned animations, and v44 is what HL2 shipped).
    /// </summary>
    public const int OldestCompatibleVersion = 44;

    /// <summary><c>STUDIOHDR_FLAGS_STATIC_PROP</c>, <c>public/studio.h</c>.</summary>
    public const int StaticPropFlag = 0x10;

    /// <summary>
    /// Every mesh's vertex positions, in body-part, model, mesh order: what
    /// <c>ComputeConvexHull</c> hands the physics library, one convex per mesh
    /// (<c>staticprop.cpp:196-239</c>).
    /// </summary>
    /// <param name="mdl">The model.</param>
    /// <param name="vvd">Its vertex file.</param>
    /// <returns>One point list per mesh.</returns>
    /// <remarks>
    /// The positions come from the VVD's RAW vertex block
    /// (<c>vertexFileHeader_t::GetVertexData</c>, <c>studio.h:1959</c>, reached
    /// through <c>mstudiomodel_t::GetVertexData</c> at <c>:1985-1995</c>), not
    /// from a fixed-up LOD 0 list: vbsp's <c>CacheVertexData</c> hands the file
    /// over as read.
    /// </remarks>
    public static List<Vec3[]> MeshHulls(MdlFile mdl, VvdFile vvd)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        ArgumentNullException.ThrowIfNull(vvd);

        ReadOnlySpan<StudioVertex> raw = vvd.RawVertices();
        List<Vec3[]> hulls = [];

        ReadOnlySpan<StudioBodyParts> parts = mdl.BodyParts();
        for (int body = 0; body < parts.Length; body++)
        {
            ReadOnlySpan<StudioModel> models = mdl.Models(body);
            for (int model = 0; model < models.Length; model++)
            {
                ReadOnlySpan<StudioMesh> meshes = mdl.Meshes(body, model);
                foreach (StudioMesh mesh in meshes)
                {
                    int start = Rad.StaticPropModel.MeshVertexBase(models[model], mesh);
                    Vec3[] points = new Vec3[mesh.NumVertices];
                    for (int i = 0; i < points.Length; i++)
                    {
                        points[i] = raw[start + i].Position;
                    }

                    hulls.Add(points);
                }
            }
        }

        return hulls;
    }

    // IsStaticProp's second half, staticprop.cpp:107-121: the model's own
    // keyvalues, "prop_data" under the root, "allowstatic" as an int.
    private static async ValueTask<bool> IsDynamicOnlyAsync(MdlFile mdl, CancellationToken cancellationToken)
    {
        string text = mdl.KeyValueText();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        KeyValuesDocument document;
        try
        {
            document = await KeyValuesDocument.ParseAsync(text, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ChunkFileException)
        {
            return false;
        }

        KeyValuesNode? propData = document.Root?.Find("prop_data");
        return propData is not null && propData.GetInt("allowstatic", 0) == 0;
    }
}
