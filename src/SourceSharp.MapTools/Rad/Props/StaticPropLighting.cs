//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
    int TexelLightingNotComputed)
{
    /// <summary>
    /// The most direct-light samples any one worker's batch held at once. Not
    /// part of the output: it is here so the facts can check that a batch's
    /// planned state stays bounded however large a prop is.
    /// </summary>
    internal int PeakBatchSamples { get; init; }
}

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
    /// <c>-StaticPropIndirectMode</c> (<c></c>): which falloff the
    /// indirect gather weights samples by. 0 (default) keeps stock; 1 and 2
    /// are the TF2/Orangebox-era weightings; anything else skips weighting and
    /// reflectivity. See <see cref="PropIndirectLighting.Compute"/>.
    /// </summary>
    public int StaticPropIndirectMode { get; init; }

    /// <summary>Which defects to reproduce.</summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>How many props at once; 0 for every core.</summary>
    public int Parallelism { get; init; }

    /// <summary>The compile's shared thread pool, or null for threads of this stage's own.</summary>
    public CompilePool? Pool { get; init; }

    /// <summary>
    /// How many segments a worker's batch closes at; see
    /// <see cref="TestLineStage.DefaultBatchSegments"/>. Internal so the facts
    /// can trace each prop alone; no answer depends on it.
    /// </summary>
    internal int BatchSegments { get; init; } = TestLineStage.DefaultBatchSegments;

    /// <summary>
    /// How many light points one work item of the tracing stage plans; see
    /// <see cref="StaticPropLighting.DefaultPointsPerChunk"/>. Internal so the
    /// facts can force one point an item, or a whole prop an item; no answer
    /// depends on it.
    /// </summary>
    internal int PointsPerChunk { get; init; } = StaticPropLighting.DefaultPointsPerChunk;

    /// <summary>
    /// How many planned direct-light samples a worker's batch closes at; see
    /// <see cref="StaticPropLighting.DefaultBatchSamples"/>. Internal so the
    /// facts can close a batch after every item; no answer depends on it.
    /// </summary>
    internal int BatchSamples { get; init; } = StaticPropLighting.DefaultBatchSamples;
}

/// <summary>
/// The lighting half of the reference implementation: per-vertex colours for
/// every static prop, written as <c>.vhv</c> files into the pak.
/// </summary>
/// <remarks>
/// <para>
/// Per prop (<c>CVradStaticPropMgr::ComputeLighting</c>): every
/// LOD-0 vertex of every body part and model is transformed into the world and
/// lit with <c>ComputeDirectLightingAtPoint</c> plus
/// <see cref="PropIndirectLighting"/>. A vertex in solid is relit from a point
/// crawled toward the prop's lighting origin or the nearest good vertex.
/// <c>ApplyLightingToStaticProp</c> then copies the colours into
/// every strip group of every LOD through <c>origMeshVertID</c>, and
/// <c>SerializeLighting</c> encodes them.
/// </para>
/// <para>
/// PARALLEL COMPUTE, SERIAL COMMIT: stock runs props on its thread pool with a
/// per-thread displacement scratch, one prop a work item. Here a pass is
/// three parallel steps, and the files come back in prop order:
/// </para>
/// <list type="number">
/// <item>
/// PREPARE, one prop an item: every vertex transformed and tested for solid,
/// every bad vertex's replacement point crawled out, and the result kept as
/// a flat list of LIGHT POINTS (where to light, with which normal, flags and
/// skip id, and which colour slot the answer goes to). Nothing here traces.
/// </item>
/// <item>
/// LIGHT, a few points an item (<see cref="DefaultPointsPerChunk"/>): each
/// point's direct samples planned into the worker's
/// <see cref="TestLineBatch"/> and its indirect term gathered, the batch
/// traced, each point's colour resolved into its slot.
/// </item>
/// <item>
/// ENCODE, one prop an item: the colours spread over the LODs and the
/// <c>.vhv</c> written.
/// </item>
/// </list>
/// <para>
/// WHY THE LIGHTING ITEM IS NOT A PROP. A point plans one pending sample for
/// every style-0 light its cluster can see -- on a real map hundreds, most of
/// them lights too far or facing away that plan no segment -- so a prop of a
/// few thousand vertices is millions of samples and around a million
/// segments. The stage only closes a batch between items, so with one prop
/// an item a single prop filled one worker's batch alone, and every worker
/// kept that capacity for the rest of the pass: gigabytes of sample lists
/// and ray arrays, most of it on the large object heap. Chunks of points
/// bound what one item adds, and the worker's own sample bound
/// (<see cref="DefaultBatchSamples"/>, through
/// <see cref="TestLineWorker{TState, TResult}.IsBatchFull"/>) bounds what a
/// batch holds.
/// </para>
/// <para>
/// NONE OF THIS CHANGES A BYTE. A point's arithmetic is the same whichever
/// item it lands in: its samples are planned in light order and resolved in
/// that order from segments traced alone, its indirect term reads nothing
/// another point wrote, and its colour goes to its own slot. The prepare
/// step keeps stock's per-model order for everything that is order
/// dependent -- the nearest good vertex a bad one is relit from is chosen
/// among the model's good vertices only, which the prepare step knows
/// before any point is lit.
/// </para>
/// </remarks>
public static class StaticPropLighting
{
    /// <summary><c>VHV_VERSION</c>.</summary>
    public const int VhvVersion = 2;

    /// <summary><c>VERTEX_COLOR</c>.</summary>
    public const int VertexColor = 0x0004;

    /// <summary><c>sizeof( HardwareVerts::FileHeader_t )</c>.</summary>
    public const int FileHeaderSize = 40;

    /// <summary><c>sizeof( HardwareVerts::MeshHeader_t )</c>.</summary>
    public const int MeshHeaderSize = 28;

    /// <summary>The alignment of the vertex data and of the file's end.</summary>
    public const int Alignment = 512;

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    private const int ContentsSolid = 1;

    /// <summary>The light points one item of the lighting step plans, unless the options say otherwise.</summary>
    /// <remarks>
    /// Small, so one item never adds more than a few tens of thousands of
    /// samples to a batch however many lights a point sees (32 points of
    /// 1,740 lights is 56k samples at the very worst), and large enough that
    /// the per-item overhead -- a claim, a state tuple -- is noise next to
    /// the points' own work.
    /// </remarks>
    internal const int DefaultPointsPerChunk = 32;

    /// <summary>The planned direct-light samples a worker's batch closes at, unless the options say otherwise.</summary>
    /// <remarks>
    /// A pending sample and its light's intensity are 40 bytes, so this is
    /// about 2.5 MB a worker, the same order as the segment bound's rays.
    /// Most samples plan no segment (the light is out of range or faces
    /// away), so the segment bound alone would let the samples grow many
    /// times larger than the rays.
    /// </remarks>
    internal const int DefaultBatchSamples = 1 << 16;

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The options' chunk or sample bound (set only by the facts) is below one.
    /// </exception>
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

        ArgumentOutOfRangeException.ThrowIfLessThan(options.PointsPerChunk, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSamples, 1, nameof(options));

        CompileParallelism degree = (options.Parallelism > 0
            ? new CompileParallelism { MaxDegree = options.Parallelism }
            : CompileParallelism.Default) with { Pool = options.Pool };

        // 1. Every prop's light points and colour arrays, nothing traced.
        PreparedProp[] prepared = await ForEachPropAsync(
            props.Length,
            degree,
            "static prop lighting (prepare)",
            i => PrepareProp(scene, i, props[i], models[props[i].PropType], options),
            cancellationToken).ConfigureAwait(false);

        // 2. The points, a chunk an item, through the worker's batch
        // (TestLineStage): planned with the chunk, traced with the batch,
        // resolved into their props' colours. A chunk never spans props, so
        // a prop's points stay in the order the prepare step listed them.
        List<(int Prop, int Start)> chunkList = [];
        for (int p = 0; p < prepared.Length; p++)
        {
            for (int start = 0; start < prepared[p].Points.Length; start += options.PointsPerChunk)
            {
                chunkList.Add((p, start));
            }
        }

        (int Prop, int Start)[] chunks = [.. chunkList];
        chunkList.Clear();
        List<PointWorker> workers = [];
        await TestLineStage.RunAsync(
            chunks.Length,
            null,
            degree,
            () =>
            {
                PointWorker worker = new(scene, prepared, chunks, lights, sampler, options);
                lock (workers)
                {
                    workers.Add(worker);
                }

                return worker;
            },
            options.BatchSegments,
            TestLineStage.DefaultBatchItems,
            "static prop lighting",
            cancellationToken).ConfigureAwait(false);

        int peakSamples = 0;
        foreach (PointWorker worker in workers)
        {
            peakSamples = Math.Max(peakSamples, worker.PeakSamples);
        }

        workers.Clear();

        // 3. Each prop's colours spread over its LODs and encoded. A prop's
        // points and colours are dropped as it is encoded, so they do not
        // outlive their use by the rest of the pass.
        PropOutcome[] outcomes = await ForEachPropAsync(
            props.Length,
            degree,
            "static prop lighting (encode)",
            i =>
            {
                PreparedProp prop = prepared[i];
                prepared[i] = null!;
                return FinishProp(models[props[i].PropType], prop);
            },
            cancellationToken).ConfigureAwait(false);

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

        return new StaticPropLightingResult(files.ToImmutable(), bad, selfShadow, texel) { PeakBatchSamples = peakSamples };
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
    /// <c>PositionInSolid</c> over the reference implementation's <c>PointLeafnum</c>
    /// A plain descent, <c>dist &lt; 0</c> going back, axial
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
    /// (<c>SerializeLighting</c>).
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

    /// <summary>
    /// Runs one step of the pass over every prop on the compile's workers,
    /// the results by prop index.
    /// </summary>
    /// <remarks>
    /// The queue observes the token between props; no step does enough for
    /// one prop to need more (the heaviest, the bad-vertex search, is
    /// quadratic in one studio model's vertices, not the map's).
    /// </remarks>
    private static async Task<T[]> ForEachPropAsync<T>(
        int count,
        CompileParallelism degree,
        string stage,
        Func<int, T> body,
        CancellationToken cancellationToken)
    {
        using WorkQueue queue = new(degree);
        return await queue.RunAsync<int, T>(
            count,
            (i, _, _) => body(i),
            _ => 0,
            new WorkQueueOptions { Stage = stage },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One prop up to its lighting: its outcome (with an empty file until
    /// encoded), its models' colour arrays, and the points that fill them.
    /// </summary>
    /// <remarks>
    /// The checks run in the order the one-step pass ran them, so a prop that
    /// cannot be lit fails with the same exception it always did: no file for
    /// <c>NO_PER_VERTEX_LIGHTING</c> before anything is read, then the missing
    /// studio header, then no lighting (and a file with no meshes) without a
    /// <c>.vtx</c>, then the missing <c>.vvd</c>.
    /// </remarks>
    private static PreparedProp PrepareProp(
        AmbientScene scene,
        int index,
        StaticProp prop,
        StaticPropModel model,
        StaticPropLightingOptions options)
    {
        // SerializeLighting: no file, and ComputeLighting's work is thrown away.
        if ((prop.Flags & StaticPropFlags.NoPerVertexLighting) != 0)
        {
            return new PreparedProp(new PropOutcome(null, 0, false, false), null, []);
        }

        if (model.Mdl is null)
        {
            // Stock reads the studio header's checksum through a null pointer.
            throw new InvalidOperationException(
                $"static prop {index}: model {model.Path} has no studio header, which stock dereferences");
        }

        // skip_prop: the prop's own triangles, as the trace ids them.
        bool selfShadowSkip = options.DisableSelfShadowing || (prop.Flags & StaticPropFlags.NoSelfShadowing) != 0;
        int skipId = selfShadowSkip ? TraceId.StaticProp | index : -1;
        bool texel = (prop.Flags & StaticPropFlags.NoPerTexelLighting) == 0;

        int bad = 0;
        List<Vec3[]>? modelColors = null;
        List<LightPoint> points = [];

        // No VTX, no lighting; the file is written with no meshes.
        if (model.Vtx is not null)
        {
            if (model.Vvd is null)
            {
                // The vertex-data callback's Error().
                throw new InvalidOperationException($"static prop {index}: model {model.Path} has no .vvd");
            }

            MdlFile mdl = model.Mdl;
            ReadOnlySpan<StudioBodyParts> bodyParts = mdl.BodyParts();
            modelColors = [];
            for (int b = 0; b < bodyParts.Length; b++)
            {
                ReadOnlySpan<StudioModel> studioModels = mdl.Models(b);
                for (int m = 0; m < studioModels.Length; m++)
                {
                    modelColors.Add(PrepareModel(scene, prop, skipId, model, b, m, options, points, ref bad));
                }
            }
        }

        PropOutcome outcome = new(
            new StaticPropVhvFile(index, FileName(index, options.Hdr), []), bad, selfShadowSkip, texel);
        return new PreparedProp(outcome, modelColors, [.. points]);
    }

    /// <summary>
    /// One studio model up to its lighting: its colour array, zeroed, and a
    /// light point for every vertex that will be lit, added to
    /// <paramref name="points"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The points go in the order the one-step pass lit them: the good
    /// vertices in mesh order, then the bad ones in the order they were
    /// found. A good vertex's point carries the prop's flags and skip id and
    /// gathers indirect light when the pass does; a bad vertex's is lit from
    /// its crawled-out position with its own normal, always gathers indirect
    /// light, and under <see cref="StockQuirk.StaticPropBadVertexDropsPropFlags"/>
    /// loses the prop's flags and skip id.
    /// </para>
    /// <para>
    /// Bad vertices are relit only when there is somewhere to crawl toward:
    /// the prop's lighting origin, or at least one good vertex in the model.
    /// A model wholly in solid without a lighting origin keeps zero colours.
    /// The nearest good vertex is looked for among this model's good
    /// vertices only, which are all known before any bad one is placed.
    /// </para>
    /// </remarks>
    private static Vec3[] PrepareModel(
        AmbientScene scene,
        StaticProp prop,
        int skipId,
        StaticPropModel model,
        int bodyPart,
        int modelIndex,
        StaticPropLightingOptions options,
        List<LightPoint> points,
        ref int badCount)
    {
        bool dropFlags = options.Compliance.Emulates(StockQuirk.StaticPropBadVertexDropsPropFlags);
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
                    valid[numVertexes] = true;
                    positions[numVertexes] = samplePosition;

                    // colors = direct + indirect, once the point is lit.
                    points.Add(new LightPoint(
                        colors, numVertexes, samplePosition, sampleNormal, flags, skipId, ignoreNormals, options.Indirect));
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

                // Position saved, validity not: a later bad vertex never
                // crawls toward this one.
                positions[badIndex] = bestPosition;
                points.Add(new LightPoint(
                    colors, badIndex, bestPosition, badNormal, badFlags, badSkipId, badIgnoreNormals, Indirect: true));
            }
        }

        return colors;
    }

    /// <summary>
    /// One prop after its points are lit: the colours spread over the LODs
    /// and the file encoded.
    /// </summary>
    private static PropOutcome FinishProp(StaticPropModel model, PreparedProp prop)
    {
        if (prop.Outcome.File is null)
        {
            return prop.Outcome;
        }

        List<(int Lod, Vec3[] Colors)> meshes = [];
        if (prop.ModelColors is not null)
        {
            Apply(model, prop.ModelColors, meshes);
        }

        byte[] data = EncodeVhv(model.Mdl!.Checksum, meshes);
        return prop.Outcome with { File = prop.Outcome.File with { Data = data } };
    }

    /// <summary>
    /// Whether the direct samples' light directions are normalised as stock
    /// does (<see cref="StockQuirk.VradVectorNormalise"/>). Read once a
    /// worker, not once a light.
    /// </summary>
    private static bool StockNormalise(ComplianceOptions compliance) =>
        compliance.Emulates(StockQuirk.VradVectorNormalise);

    /// <summary>
    /// <c>ComputeDirectLightingAtPoint</c> up to its traces: each light's
    /// sample planned into the worker's batch, in light order.
    /// </summary>
    /// <returns>The index of the point's first planned sample in <see cref="PointWorker.Samples"/>.</returns>
    private static int PlanDirect(
        AmbientScene scene,
        Vec3 position,
        Vec3 normal,
        PropGatherFlags flags,
        int skipId,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        bool stockNormalise,
        PointWorker scratch)
    {
        int first = scratch.Samples.Count;
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

            PendingPropSample s = sampler.Plan(
                dl, adjusted, normal, scratch.Lines, flags | PropGatherFlags.ForceFast, 0.0f, skipId);
            scratch.Samples.Add((s, dl.Intensity));
        }

        return first;
    }

    /// <summary>
    /// A deferred vertex's colour, once its batch is traced:
    /// <c>ComputeDirectLightingAtPoint</c>'s accumulation, in light order,
    /// plus the indirect term.
    /// </summary>
    private static void ResolveVertex(PropLightSampler sampler, PointWorker scratch, PendingVertex v)
    {
        Vec3 outColor = Vec3.Zero;
        for (int k = 0; k < v.SampleCount; k++)
        {
            (PendingPropSample pending, Vec3 intensity) = scratch.Samples[v.FirstSample + k];
            PropLightSample s = sampler.Resolve(in pending, scratch.Lines);

            // VectorMA( outColor, falloff * dot, intensity, outColor ).
            float scale = s.Falloff * s.Dot;
            outColor = new Vec3(
                outColor.X + (scale * intensity.X),
                outColor.Y + (scale * intensity.Y),
                outColor.Z + (scale * intensity.Z));
        }

        v.Colors[v.Index] = outColor + v.Indirect;
    }

    /// <summary>
    /// Where one colour of a prop is lit from: everything the prepare step
    /// decided, so the lighting step needs nothing but the point.
    /// </summary>
    /// <param name="Colors">The studio model's colour array the answer goes to.</param>
    /// <param name="Index">The slot in <paramref name="Colors"/>.</param>
    /// <param name="Position">Where to light: the vertex, or a bad vertex's crawled-out point.</param>
    /// <param name="Normal">The vertex's normal.</param>
    /// <param name="Flags">The gather flags for the direct samples.</param>
    /// <param name="SkipId">The trace id the direct samples pass through, or -1.</param>
    /// <param name="IgnoreNormals">Whether the indirect gather ignores the normal.</param>
    /// <param name="Indirect">Whether the point gathers indirect light at all.</param>
    private readonly record struct LightPoint(
        Vec3[] Colors,
        int Index,
        Vec3 Position,
        Vec3 Normal,
        PropGatherFlags Flags,
        int SkipId,
        bool IgnoreNormals,
        bool Indirect);

    /// <summary>A vertex whose colour waits on its traces.</summary>
    private readonly record struct PendingVertex(Vec3[] Colors, int Index, Vec3 Indirect, int FirstSample, int SampleCount);

    /// <summary>
    /// A prop between the prepare step and the encode step: its outcome (with
    /// an empty file until encoded), its models' colour arrays (null without
    /// a <c>.vtx</c>), and its light points.
    /// </summary>
    private sealed record PreparedProp(PropOutcome Outcome, List<Vec3[]>? ModelColors, LightPoint[] Points);

    /// <summary>
    /// One worker of the lighting step: its displacement marks, its segment
    /// batch, and the samples and vertices that wait on the batch.
    /// </summary>
    /// <remarks>
    /// An item is one chunk of one prop's light points. The worker closes its
    /// batch once it holds <see cref="StaticPropLightingOptions.BatchSamples"/>
    /// samples, which the stage's segment bound cannot see: most samples plan
    /// no segment.
    /// </remarks>
    private sealed class PointWorker(
        AmbientScene scene,
        PreparedProp[] prepared,
        (int Prop, int Start)[] chunks,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        StaticPropLightingOptions options)
        : TestLineWorker<(int FirstVertex, int Count), bool>(sampler.CreateBatch())
    {
        private readonly bool _stockNormalise = StockNormalise(options.Compliance);

        public DispTestedScratch Displacements { get; } = scene.Tracer.Displacements.CreateScratch();

        public List<(PendingPropSample Sample, Vec3 Intensity)> Samples { get; } = [];

        public List<PendingVertex> Vertices { get; } = [];

        /// <summary>The most samples any of this worker's batches held.</summary>
        public int PeakSamples { get; private set; }

        public override bool IsBatchFull => Samples.Count >= options.BatchSamples;

        public override void BeginBatch()
        {
            // The last batch's samples are all still here: BeginBatch runs
            // before every fill, including the final one that finds no items.
            PeakSamples = Math.Max(PeakSamples, Samples.Count);
            Samples.Clear();
            Vertices.Clear();
        }

        public override (int FirstVertex, int Count) Plan(int item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int prop, int start) = chunks[item];
            LightPoint[] points = prepared[prop].Points;
            int end = start + Math.Min(options.PointsPerChunk, points.Length - start);
            int firstVertex = Vertices.Count;
            for (int i = start; i < end; i++)
            {
                ref readonly LightPoint p = ref points[i];
                int firstSample = PlanDirect(scene, p.Position, p.Normal, p.Flags, p.SkipId, lights, sampler, _stockNormalise, this);
                Vec3 indirect = Vec3.Zero;
                if (p.Indirect)
                {
                    indirect = PropIndirectLighting.Compute(
                        scene, p.Position, p.Normal, forceFast: true, p.IgnoreNormals, Displacements,
                        options.Compliance, options.StaticPropIndirectMode);
                }

                Vertices.Add(new PendingVertex(p.Colors, p.Index, indirect, firstSample, Samples.Count - firstSample));
            }

            return (firstVertex, end - start);
        }

        public override bool Resolve(int item, (int FirstVertex, int Count) state)
        {
            for (int v = state.FirstVertex; v < state.FirstVertex + state.Count; v++)
            {
                ResolveVertex(sampler, this, Vertices[v]);
            }

            return true;
        }
    }

    /// <summary>
    /// <c>ApplyLightingToStaticProp</c>: every LOD's strip groups,
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
