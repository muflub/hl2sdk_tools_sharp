//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary>What one detail-prop lighting pass produces.</summary>
/// <param name="Props">
/// The props with <c>m_Lighting</c>, <c>m_LightStyles</c> and
/// <c>m_LightStyleCount</c> written; everything else as it came in.
/// </param>
/// <param name="LightStyles">This pass's <c>dplt</c> (LDR) or <c>dplh</c> (HDR) records.</param>
/// <param name="BogusProps">Props whose centre or normal was not finite.</param>
public sealed record DetailPropLightingResult(
    DetailObjectLump[] Props, DetailPropLightstylesLump[] LightStyles, int BogusProps)
{
    /// <summary>
    /// Each prop's style 0 ambient light (the rays' part of its colour, before
    /// the direct light is added and the sum encoded), in prop order; null
    /// for a result not built by <see cref="DetailPropLighting"/>. A room's
    /// door response keeps it: the light an emitter behind a door sends a
    /// prop directly is evaluated at link from the neighbour's own lights,
    /// and only what the room's surfaces reflect onto the prop is the
    /// response's.
    /// </summary>
    internal Vec3[]? Ambient { get; init; }
}

/// <summary>
/// <c>ComputeDetailPropLighting</c>: each detail
/// prop's base colour and lightstyle colours.
/// </summary>
/// <remarks>
/// <para>
/// Per prop (<c>ComputeLighting</c>): the brightest direct light --
/// every <c>activelights</c> entry whose PVS holds the prop's cluster, through
/// <c>GatherSampleLightSSE</c> -- plus the same 162-ray ambient
/// <c>CalcRayAmbientLighting</c> leaf ambient uses, over all 64 lightstyles
/// and scaled by 255/162. Style 0 is the prop's own colour; every other
/// non-black style, halved, is appended to the style lump.
/// </para>
/// <para>
/// PARALLEL COMPUTE, SERIAL COMMIT: stock runs this loop on one thread because
/// the style lump is appended to in prop order. Here each prop's colours are
/// computed independently and the appends happen afterwards in prop order, so
/// the lump is the same bytes at any degree.
/// </para>
/// <para>
/// ONE STOCK OBSERVATION worth keeping in mind when reading a stock-lit map:
/// the props' <c>m_Lighting</c> is overwritten by EVERY pass, so after
/// <c>-both</c> it holds the HDR pass's colour (the LDR one survives only in
/// the pass that wrote it), and <c>m_LightStyles</c> is left untouched on a
/// prop with no styles.
/// </para>
/// </remarks>
public static class DetailPropLighting
{
    /// <summary><c>studiohdr_t::hull_min</c>'s offset.</summary>
    private const int HullMinOffset = 104;

    /// <summary>
    /// The model centre offsets (<c>UnserializeModelDict</c>):
    /// half the sum of each dictionary model's hull extents, or zero when the
    /// model cannot be loaded.
    /// </summary>
    /// <param name="lump">The detail prop lump.</param>
    /// <param name="content">Where the models live.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <returns>One offset per model name.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<Vec3[]> LoadModelCentresAsync(
        DetailPropLump lump, IContentFileSystem content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lump);
        ArgumentNullException.ThrowIfNull(content);

        Vec3[] centres = new Vec3[lump.ModelNames.Count];
        for (int i = 0; i < centres.Length; i++)
        {
            using IMemoryOwner<byte>? bytes = await content
                .ReadAsync(VPath.Create(lump.ModelNames[i]), cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null || bytes.Memory.Length < HullMinOffset + 24)
            {
                continue;
            }

            ReadOnlySpan<byte> s = bytes.Memory.Span;
            Vec3 min = ReadVec(s, HullMinOffset);
            Vec3 max = ReadVec(s, HullMinOffset + 12);

            // VectorAdd( hull_min, hull_max ); *= 0.5f.
            Vec3 sum = min + max;
            centres[i] = new Vec3(sum.X * 0.5f, sum.Y * 0.5f, sum.Z * 0.5f);
        }

        return centres;
    }

    /// <summary>
    /// The sprite centre offsets (<c>UnserializeSpriteDict</c>):
    /// x 0, y and z the mid-points of the sprite's corners.
    /// </summary>
    /// <param name="lump">The detail prop lump.</param>
    /// <returns>One offset per sprite.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lump"/> is null.</exception>
    public static Vec3[] SpriteCentres(DetailPropLump lump)
    {
        ArgumentNullException.ThrowIfNull(lump);
        Vec3[] centres = new Vec3[lump.Sprites.Count];
        for (int i = 0; i < centres.Length; i++)
        {
            DetailSpriteDictLump d = lump.Sprites[i];
            float y = d.LowerRight[0] + d.UpperLeft[0];
            float z = d.LowerRight[1] + d.UpperLeft[1];
            centres[i] = new Vec3(0.0f * 0.5f, y * 0.5f, z * 0.5f);
        }

        return centres;
    }

    /// <summary>Lights every detail prop for one pass.</summary>
    /// <param name="scene">The map, for this pass's mode.</param>
    /// <param name="lump">The detail prop lump.</param>
    /// <param name="modelCentres">From <see cref="LoadModelCentresAsync"/>.</param>
    /// <param name="lights">This pass's <c>activelights</c>, in list order.</param>
    /// <param name="sampler">Evaluates one light at one point.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <param name="parallelism">How many props at once; 0 for every core.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The lit props and this pass's style lump.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Task<DetailPropLightingResult> ComputeAsync(
        AmbientScene scene,
        DetailPropLump lump,
        IReadOnlyList<Vec3> modelCentres,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        ComplianceOptions compliance,
        int parallelism,
        CancellationToken cancellationToken) =>
        ComputeAsync(scene, lump, modelCentres, lights, sampler, compliance, parallelism, null, cancellationToken);

    /// <summary>Lights every detail prop for one pass, on a compile's shared thread pool.</summary>
    /// <param name="scene">The map, for this pass's mode.</param>
    /// <param name="lump">The detail prop lump.</param>
    /// <param name="modelCentres">From <see cref="LoadModelCentresAsync"/>.</param>
    /// <param name="lights">This pass's <c>activelights</c>, in list order.</param>
    /// <param name="sampler">Evaluates one light at one point.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <param name="parallelism">How many props at once; 0 for every core.</param>
    /// <param name="pool">The compile's shared thread pool, or null for threads of this stage's own.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The lit props and this pass's style lump.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Task<DetailPropLightingResult> ComputeAsync(
        AmbientScene scene,
        DetailPropLump lump,
        IReadOnlyList<Vec3> modelCentres,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        ComplianceOptions compliance,
        int parallelism,
        CompilePool? pool,
        CancellationToken cancellationToken) =>
        ComputeAsync(
            scene, lump, modelCentres, lights, sampler, compliance, parallelism, pool,
            TestLineStage.DefaultBatchSegments, cancellationToken);

    /// <summary>
    /// Lights every detail prop for one pass, with a worker's batch closing
    /// at <paramref name="batchSegments"/> segments; the facts use it to
    /// trace each prop alone, and no answer depends on it.
    /// </summary>
    internal static Task<DetailPropLightingResult> ComputeAsync(
        AmbientScene scene,
        DetailPropLump lump,
        IReadOnlyList<Vec3> modelCentres,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        ComplianceOptions compliance,
        int parallelism,
        CompilePool? pool,
        int batchSegments,
        CancellationToken cancellationToken) =>
        ComputeAsync(scene, lump, modelCentres, lights, sampler, compliance, parallelism, pool, batchSegments, 0, cancellationToken);

    /// <summary>
    /// Lights every detail prop for one pass in a room bake's frame: the
    /// ambient rays turned into the room's frame by <paramref name="frameTurns"/>
    /// quarter turns (<see cref="Light.BakeFrame"/>), in their order, as the
    /// leaf ambient's and the static props' are, so a room lit for a turned
    /// placement sees the sky and the room as the turned map does. The
    /// sampler carries the same turns for the sun's jitter. 0 is the
    /// arithmetic vrad always ran.
    /// </summary>
    internal static async Task<DetailPropLightingResult> ComputeAsync(
        AmbientScene scene,
        DetailPropLump lump,
        IReadOnlyList<Vec3> modelCentres,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        ComplianceOptions compliance,
        int parallelism,
        CompilePool? pool,
        int batchSegments,
        int frameTurns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lump);
        ArgumentNullException.ThrowIfNull(modelCentres);
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(compliance);
        cancellationToken.ThrowIfCancellationRequested();

        Vec3[] spriteCentres = SpriteCentres(lump);
        DetailObjectLump[] props = [.. lump.Props];

        // Each cluster's lights (every style but the ambient sky) in its
        // PVS, built as the first prop in the cluster asks and dropped with
        // the pass.
        PropClusterLights clusterLights = PropClusterLights.ForDetailProps(scene, lights);

        CompileParallelism degree = (parallelism > 0
            ? new CompileParallelism { MaxDegree = parallelism }
            : CompileParallelism.Default) with { Pool = pool };

        // Each prop's direct-light segments go to the tracer in its worker's
        // batch (TestLineStage): planned with the prop, traced with the
        // batch, resolved into the prop's colours.
        PropColours[] colours = await TestLineStage.RunAsync(
            props.Length,
            null,
            degree,
            w => new DetailWorker(scene, props, modelCentres, spriteCentres, lights, clusterLights, sampler, w, frameTurns),
            batchSegments,
            TestLineStage.DefaultBatchItems,
            "detail prop lighting",
            cancellationToken).ConfigureAwait(false);

        // ComputeLighting's commit, in prop order.
        List<DetailPropLightstylesLump> styles = [];
        int bogus = 0;
        for (int i = 0; i < props.Length; i++)
        {
            PropColours c = colours[i];
            bogus += c.Bogus ? 1 : 0;

            ref DetailObjectLump prop = ref props[i];
            prop.Lighting = StockLightColor.Encode(c.Direct[0] + c.Ambient[0]);

            bool hasLightstyles = false;
            prop.LightStyleCount = 0;
            for (int s = 1; s < RayAmbientLighting.MaxLightStyles; s++)
            {
                Vec3 total = c.Direct[s] + c.Ambient[s];
                total = new Vec3(total.X * 0.5f, total.Y * 0.5f, total.Z * 0.5f);
                if (total.X != 0.0f || total.Y != 0.0f || total.Z != 0.0f)
                {
                    if (!hasLightstyles)
                    {
                        prop.LightStyles = (uint)styles.Count;
                        hasLightstyles = true;
                    }

                    styles.Add(new DetailPropLightstylesLump { Lighting = StockLightColor.Encode(total), Style = (byte)s });
                    prop.LightStyleCount++;
                }
            }
        }

        _ = compliance;
        return new DetailPropLightingResult(props, [.. styles], bogus) { Ambient = [.. colours.Select(c => c.Ambient[0])] };
    }

    /// <summary>
    /// Writes a pass's result into a map: the props back into <c>dprp</c> and
    /// the styles into this pass's lump (<c>WriteDetailLightingLumps</c>,
    ///), leaving the other pass's style lump as it was.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="lump">The detail prop lump the pass read.</param>
    /// <param name="result">The pass's result.</param>
    /// <param name="hdr">Whether it was the HDR pass.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Stock rewrites BOTH style lumps every pass, having first read the other
    /// Pass's back; an absent one is written empty. The
    /// same holds here: a missing other-pass lump becomes an empty one.
    /// </remarks>
    public static void WriteInto(BspData bsp, DetailPropLump lump, DetailPropLightingResult result, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(lump);
        ArgumentNullException.ThrowIfNull(result);

        lump.Props.Clear();
        lump.Props.AddRange(result.Props);
        Replace(bsp, lump.Write());

        int other = GameLumpId.MakeId(hdr ? GameLumpId.DetailPropLighting : GameLumpId.DetailPropLightingHdr);
        DetailPropLightstylesLump[] kept = [];
        foreach (GameLumpEntry e in bsp.GameLumps)
        {
            if (e.Id == other && e.Version == 0)
            {
                kept = ReadStyleLump(e);
            }
        }

        Replace(bsp, WriteStyleLump(kept, !hdr));
        Replace(bsp, WriteStyleLump(result.LightStyles, hdr));
    }

    private static void Replace(BspData bsp, GameLumpEntry entry)
    {
        for (int i = 0; i < bsp.GameLumps.Count; i++)
        {
            if (bsp.GameLumps[i].Id == entry.Id)
            {
                bsp.GameLumps[i] = entry;
                return;
            }
        }

        bsp.GameLumps.Add(entry);
    }

    /// <summary>
    /// A style lump as its game lump: <c>int count</c> then the records
    /// (<c>WriteDetailLightingLump</c>), version 0.
    /// </summary>
    /// <param name="styles">The records.</param>
    /// <param name="hdr">Whether this is <c>dplh</c>.</param>
    /// <returns>The game lump.</returns>
    public static GameLumpEntry WriteStyleLump(ReadOnlySpan<DetailPropLightstylesLump> styles, bool hdr)
    {
        ReadOnlySpan<byte> records = MemoryMarshal.AsBytes(styles);
        byte[] bytes = new byte[4 + records.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, styles.Length);
        records.CopyTo(bytes.AsSpan(4));
        return new GameLumpEntry(
            GameLumpId.MakeId(hdr ? GameLumpId.DetailPropLightingHdr : GameLumpId.DetailPropLighting),
            0,
            (ushort)(hdr ? GameLumpVersions.DetailPropLightingHdr : GameLumpVersions.DetailPropLighting),
            bytes);
    }

    /// <summary>
    /// A style game lump's records (<c>UnserializeDetailPropLighting</c>).
    /// </summary>
    /// <param name="entry">The <c>dplt</c> or <c>dplh</c> lump.</param>
    /// <returns>The records.</returns>
    public static DetailPropLightstylesLump[] ReadStyleLump(GameLumpEntry entry)
    {
        ReadOnlySpan<byte> bytes = entry.Data.Span;
        if (bytes.Length < 4)
        {
            return [];
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return MemoryMarshal.Cast<byte, DetailPropLightstylesLump>(
            bytes.Slice(4, count * System.Runtime.CompilerServices.Unsafe.SizeOf<DetailPropLightstylesLump>())).ToArray();
    }

    /// <summary><c>ComputeWorldCenter</c>: the prop's centre and up vector.</summary>
    /// <param name="prop">The prop.</param>
    /// <param name="modelCentres">Model centre offsets.</param>
    /// <param name="spriteCentres">Sprite centre offsets.</param>
    /// <returns>The centre and the normal.</returns>
    public static (Vec3 Centre, Vec3 Normal) WorldCentre(
        in DetailObjectLump prop, IReadOnlyList<Vec3> modelCentres, IReadOnlyList<Vec3> spriteCentres)
    {
        ArgumentNullException.ThrowIfNull(modelCentres);
        ArgumentNullException.ThrowIfNull(spriteCentres);

        (Vec3 forward, Vec3 right, Vec3 up) = AngleVectors(prop.Angles);
        Vec3 centre = prop.Origin;

        switch (prop.Type)
        {
            case 0: // DETAIL_PROP_TYPE_MODEL
            {
                Vec3 o = modelCentres[prop.DetailModel];
                centre = Ma(centre, o.X, forward);
                centre = Ma(centre, -o.Y, right);
                centre = Ma(centre, o.Z, up);
                break;
            }

            case 1: // DETAIL_PROP_TYPE_SPRITE
            {
                Vec3 s = spriteCentres[prop.DetailModel];
                Vec3 o = new(s.X * prop.Scale, s.Y * prop.Scale, s.Z * prop.Scale);
                centre = Ma(centre, o.X, forward);
                centre = Ma(centre, -o.Y, right);
                centre = Ma(centre, o.Z, up);
                break;
            }
        }

        return (centre, up);
    }

    /// <summary>
    /// <c>AngleVectors</c> with <c>SinCos</c> as
    /// <c>sinf</c>/<c>cosf</c>.
    /// </summary>
    /// <param name="angles">Pitch, yaw, roll in degrees.</param>
    /// <returns>Forward, right, up.</returns>
    /// <remarks>
    /// The single-precision sine and cosine are taken through double and
    /// rounded, which is correctly rounded for every float input -- the property
    /// MSVC's <c>sinf</c> has in practice and glibc's <c>sinf</c> does not
    /// promise.
    /// </remarks>
    public static (Vec3 Forward, Vec3 Right, Vec3 Up) AngleVectors(Vec3 angles)
    {
        // DEG2RAD( x ) = (float)x * (float)(M_PI_F / 180.f).
        const float Scale = (float)(3.14159265358979323846) / 180.0f;
        (float sy, float cy) = SinCos(angles.Y * Scale);
        (float sp, float cp) = SinCos(angles.X * Scale);
        (float sr, float cr) = SinCos(angles.Z * Scale);

        Vec3 forward = new(cp * cy, cp * sy, -sp);
        Vec3 right = new(
            (-1 * sr * sp * cy) + (-1 * cr * -sy),
            (-1 * sr * sp * sy) + (-1 * cr * cy),
            -1 * sr * cp);
        Vec3 up = new(
            (cr * sp * cy) + (-sr * -sy),
            (cr * sp * sy) + (-sr * cy),
            cr * cp);
        return (forward, right, up);
    }

    private static (float Sin, float Cos) SinCos(float radians) =>
        (DetMath.SinToSingle(radians), DetMath.CosToSingle(radians));

    /// <summary><c>VectorMA</c>: <c>start + scale * dir</c>, per component.</summary>
    private static Vec3 Ma(Vec3 start, float scale, Vec3 dir) =>
        new(start.X + (scale * dir.X), start.Y + (scale * dir.Y), start.Z + (scale * dir.Z));

    private static Vec3 ReadVec(ReadOnlySpan<byte> s, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(s[at..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[(at + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(s[(at + 8)..]));

    /// <summary>
    /// One prop up to its traces (<c>ComputeMaxDirectLighting</c>'s first
    /// half): the debug colour for a bogus prop, else every light's sample
    /// planned into the worker's batch in light order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference implementation walks the whole light list for every
    /// prop, skipping the ambient sky and lights whose PVS does not hold the
    /// prop's cluster (none, for a negative cluster). Both tests depend on
    /// the prop only through its cluster, so the walk here is over
    /// <paramref name="clusterLights"/>' list for that cluster, which holds
    /// exactly the lights the full walk keeps, in the same order.
    /// </para>
    /// <para>
    /// That list's length is the prop's sample count, so the planned samples
    /// go into an array of exactly that length. A list grown from empty
    /// allocated about twice as much, all of it garbage by the next batch:
    /// 31 MB on 2fort. One list per worker for the whole batch was tried
    /// and was worse: a batch of 256 props of a thousand lights each grows
    /// it onto the large-object heap, which is what this work is removing.
    /// </para>
    /// </remarks>
    private static DetailPlan Plan(
        AmbientScene scene,
        in DetailObjectLump prop,
        IReadOnlyList<Vec3> modelCentres,
        Vec3[] spriteCentres,
        IReadOnlyList<PropLight> lights,
        PropClusterLights clusterLights,
        PropLightSampler sampler,
        TestLineBatch lines)
    {
        (Vec3 origin, Vec3 normal) = WorldCentre(in prop, modelCentres, spriteCentres);

        if (!IsValid(origin) || !IsValid(normal))
        {
            // 168-183: fill with the debug colour.
            PropColours c = new();
            for (int s = 0; s < RayAmbientLighting.MaxLightStyles; s++)
            {
                c.Direct[s] = new Vec3(1, 0, 0);
                c.Ambient[s] = new Vec3(1, 0, 0);
            }

            c.Bogus = true;
            return new DetailPlan(c, origin, []);
        }

        int[] kept = clusterLights.For(ClusterFromPoint(scene, origin));
        (PendingPropSample Sample, PropLight Light)[] planned = new (PendingPropSample, PropLight)[kept.Length];
        for (int k = 0; k < kept.Length; k++)
        {
            PropLight dl = lights[kept[k]];
            planned[k] = (sampler.Plan(dl, origin, normal, lines), dl);
        }

        return new DetailPlan(null, origin, planned);
    }

    /// <summary>
    /// One prop after its traces: the direct sums in light order, then
    /// <c>ComputeAmbientLightingAtPoint</c>.
    /// </summary>
    private static PropColours Resolve(DetailPlan plan, PropLightSampler sampler, PropAmbient ambient, TestLineBatch lines)
    {
        if (plan.Bogus is { } bogus)
        {
            return bogus;
        }

        PropColours c = new();
        foreach ((PendingPropSample pending, PropLight dl) in plan.Planned)
        {
            PropLightSample s = sampler.Resolve(in pending, lines);

            // VectorMA( maxcolor[style], falloff * dot, intensity, maxcolor[style] ).
            float scale = s.Falloff * s.Dot;
            c.Direct[dl.Style] = Ma(c.Direct[dl.Style], scale, dl.Intensity);
        }

        // ComputeAmbientLightingAtPoint.
        ambient.Compute(plan.Origin, c.Ambient);
        return c;
    }

    /// <summary>A prop between plan and resolve: its debug colours, or its origin and planned samples.</summary>
    private sealed record DetailPlan(PropColours? Bogus, Vec3 Origin, (PendingPropSample Sample, PropLight Light)[] Planned);

    /// <summary>A worker of the stage: its ambient computer and its segment batch.</summary>
    private sealed class DetailWorker(
        AmbientScene scene,
        DetailObjectLump[] props,
        IReadOnlyList<Vec3> modelCentres,
        Vec3[] spriteCentres,
        IReadOnlyList<PropLight> lights,
        PropClusterLights clusterLights,
        PropLightSampler sampler,
        int workerIndex,
        int frameTurns)
        : TestLineWorker<DetailPlan, PropColours>(sampler.CreateBatch(workerIndex))
    {
        private readonly PropAmbient _ambient = new(scene, frameTurns);

        public override DetailPlan Plan(int item, CancellationToken cancellationToken) =>
            DetailPropLighting.Plan(scene, in props[item], modelCentres, spriteCentres, lights, clusterLights, sampler, Lines);

        public override PropColours Resolve(int item, DetailPlan state) =>
            DetailPropLighting.Resolve(state, sampler, _ambient, Lines);
    }

    private static bool IsValid(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>
    /// <c>ClusterFromPoint</c> over <c>PointInLeaf</c>
    /// Straddling the plane within <c>TEST_EPSILON</c> (0.1, a
    /// double) tries the front child first and keeps it unless its cluster is -1.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <param name="point">The point.</param>
    /// <returns>The cluster, possibly -1.</returns>
    public static int ClusterFromPoint(AmbientScene scene, Vec3 point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.Leaves[PointInLeaf(scene, 0, point)].Cluster;
    }

    private static int PointInLeaf(AmbientScene scene, int node, Vec3 point)
    {
        while (true)
        {
            if (node < 0)
            {
                return -1 - node;
            }

            ref readonly DNode n = ref scene.Nodes[node];
            ref readonly DPlane plane = ref scene.Planes[n.PlaneNum];
            float dist = Vec3.Dot(point, plane.Normal) - plane.Dist;
            if ((double)dist > 0.1)
            {
                node = n.Children[0];
            }
            else if ((double)dist < -0.1)
            {
                node = n.Children[1];
            }
            else
            {
                int test = PointInLeaf(scene, n.Children[0], point);
                if (scene.Leaves[test].Cluster != -1)
                {
                    return test;
                }

                node = n.Children[1];
            }
        }
    }

    /// <summary>A prop's colours.</summary>
    private sealed class PropColours
    {
        public Vec3[] Direct { get; } = new Vec3[RayAmbientLighting.MaxLightStyles];

        public Vec3[] Ambient { get; } = new Vec3[RayAmbientLighting.MaxLightStyles];

        public bool Bogus { get; set; }
    }

    /// <summary>
    /// <c>ComputeAmbientLightingAtPoint</c>
    /// with a worker's displacement scratch.
    /// </summary>
    private sealed class PropAmbient
    {
        /// <summary><c>255.0f / (float)NUMVERTEXNORMALS</c>.</summary>
        private const float Scale = 255.0f / VertexNormals.Count;

        private readonly AmbientScene _scene;
        private readonly DispTestedScratch _scratch;
        private readonly Vec3? _sky;
        private readonly float _tanTheta;
        private readonly Vec3[]? _turned;

        public PropAmbient(AmbientScene scene, int frameTurns)
        {
            _scene = scene;
            _scratch = scene.Tracer.Displacements.CreateScratch();
            _sky = RayAmbientLighting.FindSkyAmbient(scene);
            _tanTheta = DetMathF.Tan(VertexNormals.ConeInnerAngleRadians);

            // The ray directions in a room bake's frame, in the table's order;
            // none turned (the table itself) in every other compile.
            _turned = (frameTurns & 3) == 0 ? null : Light.BakeFrame.ToRoom(VertexNormals.All, frameTurns);
        }

        public void Compute(Vec3 origin, Vec3[] color)
        {
            Array.Clear(color);
            ReadOnlySpan<Vec3> anorms = _turned ?? VertexNormals.All;
            for (int i = 0; i < VertexNormals.Count; i++)
            {
                // VectorMA( origin, COORD_EXTENT * 1.74, g_anorms[i], upend ).
                Vec3 end = Ma(origin, AmbientCube.RayLength, anorms[i]);
                RayAmbientLighting.Accumulate(_scene, origin, end, _tanTheta, _sky, color, _scratch);
            }

            for (int s = 0; s < color.Length; s++)
            {
                color[s] = new Vec3(color[s].X * Scale, color[s].Y * Scale, color[s].Z * Scale);
            }
        }
    }
}
