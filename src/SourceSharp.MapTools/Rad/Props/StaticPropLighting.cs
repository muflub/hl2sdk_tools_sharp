using System.Buffers.Binary;
using System.Collections.Immutable;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary>One prop's <c>.vhv</c>, as <c>SerializeLighting</c> adds it to the pak.</summary>
/// <param name="PropIndex">The prop's index in <c>sprp</c>.</param>
/// <param name="FileName"><c>sp_N.vhv</c> or <c>sp_hdr_N.vhv</c>.</param>
/// <param name="Data">The file's bytes.</param>
public sealed record StaticPropVhvFile(int PropIndex, string FileName, byte[] Data);

/// <summary>What one static-prop lighting pass produces.</summary>
/// <param name="Files">One file per prop without <c>NO_PER_VERTEX_LIGHTING</c>, in prop order.</param>
/// <param name="BadVertices">Vertices that sat in solid and were relit from a nearby point.</param>
/// <param name="SelfShadowingSkipped">
/// Props lit without their own triangles in the way (<c>NO_SELF_SHADOWING</c>,
/// or <see cref="StaticPropLightingOptions.DisableSelfShadowing"/>).
/// </param>
/// <param name="TexelLightingNotComputed">
/// Props without <c>NO_PER_TEXEL_LIGHTING</c>: stock also writes a
/// <c>texelslighting_N.ppl</c> for them, which is not ported. Their <c>.vhv</c>
/// is unaffected.
/// </param>
public sealed record StaticPropLightingResult(
    ImmutableArray<StaticPropVhvFile> Files,
    int BadVertices,
    int SelfShadowingSkipped,
    int TexelLightingNotComputed);

/// <summary>The switches static-prop lighting reads.</summary>
public sealed record StaticPropLightingOptions
{
    /// <summary>The HDR pass: names the files <c>sp_hdr_N.vhv</c>.</summary>
    public bool Hdr { get; init; }

    /// <summary><c>numbounce &gt;= 1</c>: add indirect light to the vertices that are not in solid.</summary>
    public bool Indirect { get; init; } = true;

    /// <summary><c>g_bDisablePropSelfShadowing</c> (<c>-disablepropselfshadowing</c>).</summary>
    public bool DisableSelfShadowing { get; init; }

    /// <summary>
    /// <c>-StaticPropIndirectMode</c> (<c>0x1417194ec</c>): which falloff the
    /// indirect gather weights samples by. 0 (default) keeps stock; 1 and 2
    /// are ++'s TF2/Orangebox weightings; anything else skips weighting and
    /// reflectivity. See <see cref="PropIndirectLighting.Compute"/>.
    /// </summary>
    public int StaticPropIndirectMode { get; init; }

    /// <summary>Which defects to reproduce.</summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>How many props at once; 0 for every core.</summary>
    public int Parallelism { get; init; }
}

/// <summary>
/// The lighting half of <c>vradstaticprops.cpp</c>: per-vertex colours for
/// every static prop, written as <c>.vhv</c> files into the pak.
/// </summary>
/// <remarks>
/// <para>
/// Per prop (<c>CVradStaticPropMgr::ComputeLighting</c>, <c>:1311</c>): every
/// LOD-0 vertex of every body part and model is transformed into the world and
/// lit with <c>ComputeDirectLightingAtPoint</c> (<c>:1152</c>) plus
/// <see cref="PropIndirectLighting"/>. A vertex in solid is relit from a point
/// crawled toward the prop's lighting origin or the nearest good vertex.
/// <c>ApplyLightingToStaticProp</c> (<c>:1213</c>) then copies the colours into
/// every strip group of every LOD through <c>origMeshVertID</c>, and
/// <c>SerializeLighting</c> (<c>:1510</c>) encodes them.
/// </para>
/// <para>
/// PARALLEL COMPUTE, SERIAL COMMIT: stock runs props on its thread pool with a
/// per-thread displacement scratch; each prop here is one work item with its
/// own, and the files come back in prop order.
/// </para>
/// </remarks>
public static class StaticPropLighting
{
    /// <summary><c>VHV_VERSION</c> (<c>hardwareverts.h</c>).</summary>
    public const int VhvVersion = 2;

    /// <summary><c>VERTEX_COLOR</c> (<c>imaterial.h:33</c>).</summary>
    public const int VertexColor = 0x0004;

    /// <summary><c>sizeof( HardwareVerts::FileHeader_t )</c>.</summary>
    public const int FileHeaderSize = 40;

    /// <summary><c>sizeof( HardwareVerts::MeshHeader_t )</c>.</summary>
    public const int MeshHeaderSize = 28;

    /// <summary>The alignment of the vertex data and of the file's end.</summary>
    public const int Alignment = 512;

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    private const int ContentsSolid = 1;

    /// <summary>The pak name stock gives a prop's file.</summary>
    /// <param name="prop">The prop index.</param>
    /// <param name="hdr">The HDR pass.</param>
    /// <returns><c>sp_N.vhv</c> or <c>sp_hdr_N.vhv</c>.</returns>
    public static string FileName(int prop, bool hdr) => hdr ? $"sp_hdr_{prop}.vhv" : $"sp_{prop}.vhv";

    /// <summary>Lights every static prop.</summary>
    /// <param name="scene">This pass's map (lightmaps and BSP walk).</param>
    /// <param name="lump">The <c>sprp</c> game lump.</param>
    /// <param name="models">The model dictionary, as <see cref="StaticPropModelLoader.LoadDictionaryAsync"/> loads it.</param>
    /// <param name="lights">This pass's <c>activelights</c>, in list order.</param>
    /// <param name="sampler">Evaluates one light at one point, over a tracer that holds the props.</param>
    /// <param name="options">The switches.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The files and what could not be reproduced.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A prop's model has no studio header or no <c>.vvd</c>, which stock
    /// dereferences or aborts on.
    /// </exception>
    /// <exception cref="InvalidBspException">A prop names a model the dictionary does not have.</exception>
    public static async Task<StaticPropLightingResult> ComputeAsync(
        AmbientScene scene,
        StaticPropLump lump,
        IReadOnlyList<StaticPropModel> models,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        StaticPropLightingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lump);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        StaticProp[] props = [.. lump.Props];
        for (int i = 0; i < props.Length; i++)
        {
            if (props[i].PropType >= models.Count)
            {
                throw new InvalidBspException(
                    $"static prop {i} names model {props[i].PropType} of a {models.Count}-entry dictionary");
            }
        }

        CompileParallelism degree = options.Parallelism > 0
            ? new CompileParallelism { MaxDegree = options.Parallelism }
            : CompileParallelism.Default;

        PropOutcome[] outcomes;
        using (WorkQueue queue = new(degree))
        {
            outcomes = await queue.RunAsync(
                    props.Length,
                    (i, scratch, _) => LightProp(scene, i, props[i], models[props[i].PropType], lights, sampler, options, scratch),
                    _ => scene.Tracer.Displacements.CreateScratch(),
                    new WorkQueueOptions { Stage = "static prop lighting" },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ImmutableArray<StaticPropVhvFile>.Builder files = ImmutableArray.CreateBuilder<StaticPropVhvFile>();
        int bad = 0, selfShadow = 0, texel = 0;
        foreach (PropOutcome o in outcomes)
        {
            if (o.File is not null)
            {
                files.Add(o.File);
            }

            bad += o.BadVertices;
            selfShadow += o.SelfShadowingSkipped ? 1 : 0;
            texel += o.TexelLighting ? 1 : 0;
        }

        return new StaticPropLightingResult(files.ToImmutable(), bad, selfShadow, texel);
    }

    /// <summary>
    /// Adds a pass's files to the map's pak, replacing any of the same name
    /// (<c>AddBufferToPak</c>, stored rather than compressed).
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="result">The pass's result.</param>
    /// <param name="cancellationToken">Cancels the pak parse.</param>
    /// <returns>A task that completes when the pak lump is replaced.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task WriteIntoAsync(BspData bsp, StaticPropLightingResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(result);

        ZipArchiveWriter writer = new();
        HashSet<string> replaced = new(result.Files.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
        if (!bsp[BspLump.PakFile].IsEmpty)
        {
            ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data, cancellationToken).ConfigureAwait(false);
            writer.Comment = pak.Comment;
            foreach (ZipEntry e in pak.Entries)
            {
                if (!replaced.Contains(e.Name))
                {
                    writer.Add(e);
                }
            }
        }

        foreach (StaticPropVhvFile f in result.Files)
        {
            writer.Add(f.FileName, f.Data);
        }

        bsp.SetLump(BspLump.PakFile, writer.ToBytes());
    }

    /// <summary>
    /// <c>PositionInSolid</c> (<c>:1137</c>) over trace.cpp's <c>PointLeafnum</c>
    /// (<c>:435</c>): a plain descent, <c>dist &lt; 0</c> going back, axial
    /// planes read by component.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="position">The point.</param>
    /// <returns>Whether the point's leaf is solid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    public static bool PositionInSolid(AmbientScene scene, Vec3 position)
    {
        ArgumentNullException.ThrowIfNull(scene);
        int node = 0;
        while (node >= 0)
        {
            ref readonly DNode n = ref scene.Nodes[node];
            ref readonly DPlane plane = ref scene.Planes[n.PlaneNum];
            float dist = plane.Type switch
            {
                0 => position.X - plane.Dist,
                1 => position.Y - plane.Dist,
                2 => position.Z - plane.Dist,
                _ => ((plane.Normal.X * position.X) + (plane.Normal.Y * position.Y) + (plane.Normal.Z * position.Z)) - plane.Dist,
            };

            node = dist < 0.0f ? n.Children[1] : n.Children[0];
        }

        return (scene.Leaves[-1 - node].Contents & ContentsSolid) != 0;
    }

    /// <summary>
    /// Encodes one prop's strip-group colours as a <c>.vhv</c>
    /// (<c>SerializeLighting</c>, <c>:1548-1600</c>).
    /// </summary>
    /// <param name="checksum">The studio header's checksum.</param>
    /// <param name="meshes">Each strip group's LOD and colours, in <c>ApplyLightingToStaticProp</c>'s order.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is null.</exception>
    public static byte[] EncodeVhv(int checksum, IReadOnlyList<(int Lod, Vec3[] Colors)> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        int total = 0;
        foreach ((_, Vec3[] c) in meshes)
        {
            total += c.Length;
        }

        int at = Align(FileHeaderSize + (meshes.Count * MeshHeaderSize));
        int end = Align(at + (total * 4));
        byte[] data = new byte[end];
        Span<byte> s = data;

        BinaryPrimitives.WriteInt32LittleEndian(s, VhvVersion);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], checksum);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], VertexColor);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], total);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], meshes.Count);

        for (int n = 0; n < meshes.Count; n++)
        {
            (int lod, Vec3[] colors) = meshes[n];
            Span<byte> h = s[(FileHeaderSize + (n * MeshHeaderSize))..];
            BinaryPrimitives.WriteInt32LittleEndian(h, lod);
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], colors.Length);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], at);

            foreach (Vec3 color in colors)
            {
                (byte r, byte g, byte b, byte a) = StockLightColor.ToRgba8888(StockLightColor.Encode(color));

                // b,g,r,a order.
                s[at] = b;
                s[at + 1] = g;
                s[at + 2] = r;
                s[at + 3] = a;
                at += 4;
            }
        }

        return data;
    }

    /// <summary><c>ALIGN_TO_POW2( x, 512 )</c>.</summary>
    private static int Align(int x) => (x + (Alignment - 1)) & ~(Alignment - 1);

    private static PropOutcome LightProp(
        AmbientScene scene,
        int index,
        StaticProp prop,
        StaticPropModel model,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        StaticPropLightingOptions options,
        DispTestedScratch scratch)
    {
        // SerializeLighting, :1531: no file, and ComputeLighting's work is thrown away.
        if ((prop.Flags & StaticPropFlags.NoPerVertexLighting) != 0)
        {
            return new PropOutcome(null, 0, false, false);
        }

        if (model.Mdl is null)
        {
            // :1565 reads m_pStudioHdr->checksum through a null pointer.
            throw new InvalidOperationException(
                $"static prop {index}: model {model.Path} has no studio header, which stock dereferences");
        }

        // skip_prop, :1334: the prop's own triangles, as the trace ids them.
        bool selfShadowSkip = options.DisableSelfShadowing || (prop.Flags & StaticPropFlags.NoSelfShadowing) != 0;
        int skipId = selfShadowSkip ? TraceId.StaticProp | index : -1;
        bool texel = (prop.Flags & StaticPropFlags.NoPerTexelLighting) == 0;

        List<(int Lod, Vec3[] Colors)> meshes = [];
        int bad = 0;

        // :1321: no VTX, no lighting; the file is written with no meshes.
        if (model.Vtx is not null)
        {
            if (model.Vvd is null)
            {
                // The vertex-data callback's Error(), :2190.
                throw new InvalidOperationException($"static prop {index}: model {model.Path} has no .vvd");
            }

            MdlFile mdl = model.Mdl;
            ReadOnlySpan<StudioBodyParts> bodyParts = mdl.BodyParts();
            List<Vec3[]> modelColors = [];
            for (int b = 0; b < bodyParts.Length; b++)
            {
                ReadOnlySpan<StudioModel> studioModels = mdl.Models(b);
                for (int m = 0; m < studioModels.Length; m++)
                {
                    modelColors.Add(LightModel(scene, prop, skipId, model, b, m, lights, sampler, options, scratch, ref bad));
                }
            }

            Apply(model, modelColors, meshes);
        }

        byte[] data = EncodeVhv(model.Mdl.Checksum, meshes);
        return new PropOutcome(new StaticPropVhvFile(index, FileName(index, options.Hdr), data), bad, selfShadowSkip, texel);
    }

    /// <summary>One studio model's colours, indexed like its vertices (<c>:1349-1500</c>).</summary>
    private static Vec3[] LightModel(
        AmbientScene scene,
        StaticProp prop,
        int skipId,
        StaticPropModel model,
        int bodyPart,
        int modelIndex,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        StaticPropLightingOptions options,
        DispTestedScratch scratch,
        ref int badCount)
    {
        ComplianceOptions compliance = options.Compliance;
        bool dropFlags = compliance.Emulates(StockQuirk.StaticPropBadVertexDropsPropFlags);
        bool stockNormalise = compliance.Emulates(StockQuirk.VradVectorNormalise);
        bool ignoreNormals = (prop.Flags & StaticPropFlags.IgnoreNormals) != 0;
        PropGatherFlags flags = ignoreNormals ? PropGatherFlags.IgnoreNormals : PropGatherFlags.None;

        InstanceTransform matPos = InstanceTransform.FromAngles(prop.Angles, prop.Origin);
        InstanceTransform matNormal = InstanceTransform.FromAngles(prop.Angles, Vec3.Zero);

        MdlFile mdl = model.Mdl!;
        StudioModel studioModel = mdl.Models(bodyPart)[modelIndex];
        ReadOnlySpan<StudioMesh> studioMeshes = mdl.Meshes(bodyPart, modelIndex);
        ReadOnlySpan<StudioVertex> vertices = model.Vertices;

        // colorVerts.EnsureCount( numvertices ), zeroed.
        int count = Math.Max(studioModel.NumVertices, 0);
        Vec3[] colors = new Vec3[count];
        Vec3[] positions = new Vec3[count];
        bool[] valid = new bool[count];
        List<(int Index, Vec3 Position, Vec3 Normal)> badVerts = [];

        int numVertexes = 0;
        foreach (StudioMesh mesh in studioMeshes)
        {
            int vertexBase = StaticPropModel.MeshVertexBase(studioModel, mesh);
            for (int v = 0; v < mesh.NumVertices; v++)
            {
                if (numVertexes >= count)
                {
                    throw new InvalidStudioException(
                        $"{model.Path}: meshes hold more vertices than the model's {count}");
                }

                StudioVertex vertex = vertices[vertexBase + v];
                Vec3 samplePosition = matPos.TransformPoint(vertex.Position);
                Vec3 sampleNormal = matNormal.TransformPoint(vertex.Normal);

                if (PositionInSolid(scene, samplePosition))
                {
                    badVerts.Add((numVertexes, samplePosition, sampleNormal));
                }
                else
                {
                    Vec3 direct = Direct(scene, samplePosition, sampleNormal, flags, skipId, lights, sampler, stockNormalise);
                    Vec3 indirect = Vec3.Zero;
                    if (options.Indirect)
                    {
                        indirect = PropIndirectLighting.Compute(
                            scene, samplePosition, sampleNormal, forceFast: true, ignoreNormals, scratch,
                            compliance, options.StaticPropIndirectMode);
                    }

                    valid[numVertexes] = true;
                    positions[numVertexes] = samplePosition;
                    colors[numVertexes] = direct + indirect;
                }

                numVertexes++;
            }
        }

        badCount += badVerts.Count;
        bool originValid = (prop.Flags & StaticPropFlags.UseLightingOrigin) != 0;
        if (badVerts.Count > 0 && (originValid || badVerts.Count != numVertexes))
        {
            PropGatherFlags badFlags = dropFlags ? PropGatherFlags.None : flags;
            int badSkipId = dropFlags ? -1 : skipId;
            bool badIgnoreNormals = !dropFlags && ignoreNormals;

            foreach ((int badIndex, Vec3 badPosition, Vec3 badNormal) in badVerts)
            {
                Vec3 bestPosition;
                if (originValid)
                {
                    bestPosition = prop.LightingOrigin;
                }
                else
                {
                    int best = 0;
                    float closest = float.MaxValue;
                    for (int c = 0; c < numVertexes; c++)
                    {
                        if (!valid[c])
                        {
                            continue;
                        }

                        Vec3 delta = positions[c] - badPosition;
                        float distance = delta.Length();
                        if (distance < closest)
                        {
                            closest = distance;
                            best = c;
                        }
                    }

                    bestPosition = positions[best];
                }

                // Crawl toward the best position: 19 halvings (while ( --numIterations > 0 ) from 20).
                int numIterations = 20;
                while (--numIterations > 0)
                {
                    Vec3 mid = bestPosition + badPosition;
                    mid = new Vec3(mid.X * 0.5f, mid.Y * 0.5f, mid.Z * 0.5f);
                    if (PositionInSolid(scene, mid))
                    {
                        break;
                    }

                    bestPosition = mid;
                }

                Vec3 direct = Direct(scene, bestPosition, badNormal, badFlags, badSkipId, lights, sampler, stockNormalise);
                Vec3 indirect = PropIndirectLighting.Compute(
                    scene, bestPosition, badNormal, forceFast: true, badIgnoreNormals, scratch,
                    compliance, options.StaticPropIndirectMode);

                // Position saved, validity not.
                positions[badIndex] = bestPosition;
                colors[badIndex] = direct + indirect;
            }
        }

        return colors;
    }

    /// <summary><c>ComputeDirectLightingAtPoint</c> (<c>:1152</c>).</summary>
    private static Vec3 Direct(
        AmbientScene scene,
        Vec3 position,
        Vec3 normal,
        PropGatherFlags flags,
        int skipId,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        bool stockNormalise)
    {
        Vec3 outColor = Vec3.Zero;
        int cluster = DetailPropLighting.ClusterFromPoint(scene, position);
        for (int i = 0; i < lights.Count; i++)
        {
            PropLight dl = lights[i];
            if (dl.Style != 0)
            {
                continue;
            }

            if (cluster >= 0 && (dl.Pvs[cluster >> 3] & (1 << (cluster & 7))) == 0)
            {
                continue;
            }

            Vec3 adjusted;
            if (dl.Type != EmitType.SkyAmbient)
            {
                Vec3 fudge;
                if (dl.Type == EmitType.SkyLight)
                {
                    fudge = new Vec3(-dl.Normal.X, -dl.Normal.Y, -dl.Normal.Z);
                }
                else
                {
                    fudge = dl.Origin - position;
                    fudge = stockNormalise ? fudge.NormaliseLikeStock().Normalised : fudge.Normalise().Normalised;
                }

                fudge = new Vec3(fudge.X * 4.0f, fudge.Y * 4.0f, fudge.Z * 4.0f);
                adjusted = position + fudge;
            }
            else
            {
                // adjusted_pos += 4.0 * normal.
                adjusted = position + new Vec3(normal.X * 4.0f, normal.Y * 4.0f, normal.Z * 4.0f);
            }

            PropLightSample s = sampler.Gather(dl, adjusted, normal, flags | PropGatherFlags.ForceFast, 0.0f, skipId);

            // VectorMA( outColor, falloff * dot, intensity, outColor ).
            float scale = s.Falloff * s.Dot;
            outColor = new Vec3(
                outColor.X + (scale * dl.Intensity.X),
                outColor.Y + (scale * dl.Intensity.Y),
                outColor.Z + (scale * dl.Intensity.Z));
        }

        return outColor;
    }

    /// <summary>
    /// <c>ApplyLightingToStaticProp</c> (<c>:1213</c>): every LOD's strip groups,
    /// each vertex taking the LOD-0 colour its <c>origMeshVertID</c> names.
    /// </summary>
    private static void Apply(StaticPropModel model, List<Vec3[]> modelColors, List<(int Lod, Vec3[] Colors)> meshes)
    {
        MdlFile mdl = model.Mdl!;
        VtxFile vtx = model.Vtx!;
        int numLods = vtx.Header.NumLods;
        IReadOnlyList<(int Offset, VtxBodyPartHeader Header)> vtxBodyParts = vtx.BodyParts();
        ReadOnlySpan<StudioBodyParts> bodyParts = mdl.BodyParts();
        if (vtxBodyParts.Count < bodyParts.Length)
        {
            throw new InvalidStudioException($"{model.Path}: the .vtx has fewer body parts than the .mdl");
        }

        int next = 0;
        for (int b = 0; b < bodyParts.Length; b++)
        {
            IReadOnlyList<(int Offset, VtxModelHeader Header)> vtxModels = vtx.Models(vtxBodyParts[b].Offset, vtxBodyParts[b].Header);
            ReadOnlySpan<StudioModel> studioModels = mdl.Models(b);
            if (vtxModels.Count < studioModels.Length)
            {
                throw new InvalidStudioException($"{model.Path}: the .vtx has fewer models than the .mdl");
            }

            for (int m = 0; m < studioModels.Length; m++)
            {
                Vec3[] colors = modelColors[next++];
                IReadOnlyList<(int Offset, VtxModelLodHeader Header)> lods = vtx.Lods(vtxModels[m].Offset, vtxModels[m].Header);
                if (lods.Count < numLods)
                {
                    throw new InvalidStudioException($"{model.Path}: a .vtx model has fewer LODs than the header's {numLods}");
                }

                ReadOnlySpan<StudioMesh> studioMeshes = mdl.Meshes(b, m);
                for (int lod = 0; lod < numLods; lod++)
                {
                    IReadOnlyList<(int Offset, VtxMeshHeader Header)> vtxMeshes = vtx.Meshes(lods[lod].Offset, lods[lod].Header);
                    if (vtxMeshes.Count < studioMeshes.Length)
                    {
                        throw new InvalidStudioException($"{model.Path}: a .vtx LOD has fewer meshes than the .mdl");
                    }

                    for (int n = 0; n < studioMeshes.Length; n++)
                    {
                        foreach ((int groupOffset, VtxStripGroupHeader group) in vtx.StripGroups(vtxMeshes[n].Offset, vtxMeshes[n].Header))
                        {
                            ReadOnlySpan<VtxVertex> groupVerts = vtx.Vertices(groupOffset, group);
                            Vec3[] out4 = new Vec3[groupVerts.Length];
                            for (int k = 0; k < groupVerts.Length; k++)
                            {
                                int nIndex = studioMeshes[n].VertexOffset + groupVerts[k].OrigMeshVertId;
                                if ((uint)nIndex >= (uint)colors.Length)
                                {
                                    throw new InvalidStudioException(
                                        $"{model.Path}: strip vertex {nIndex} is outside the model's {colors.Length}");
                                }

                                out4[k] = colors[nIndex];
                            }

                            meshes.Add((lod, out4));
                        }
                    }
                }
            }
        }
    }

    private sealed record PropOutcome(StaticPropVhvFile? File, int BadVertices, bool SelfShadowingSkipped, bool TexelLighting);
}
