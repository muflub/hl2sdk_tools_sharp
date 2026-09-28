//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapGen.Content;
using SourceSharp.Tests.MapTools.Rad.Ambient;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// A static-prop scene built from committed data only (the leaf-ambient map
/// and the synthetic models), so the chunking facts run on every machine: ten
/// props on and through the map's floor, one for each path a vertex can take.
/// </summary>
/// <remarks>
/// <para>
/// The map's floor is solid below z = -16 under its first world light at
/// (200, 0, 300). The props are:
/// </para>
/// <list type="number">
/// <item>0: a crate standing on the floor -- every vertex good.</item>
/// <item>1: a crate half through the floor -- bad vertices relit from the nearest good one.</item>
/// <item>2: the same with <c>IGNORE_NORMALS | NO_SELF_SHADOWING</c> -- the bad-vertex relight either keeps or drops them.</item>
/// <item>3: a turned oil drum, <c>IGNORE_NORMALS</c>.</item>
/// <item>4: a crate wholly in solid with a lighting origin -- every vertex crawled toward it.</item>
/// <item>5: a crate wholly in solid without one -- nothing relit, the file all zeros.</item>
/// <item>6: a crate with <c>NO_PER_VERTEX_LIGHTING</c> -- no file.</item>
/// <item>7: a turned and tilted chair (several meshes), per-texel lighting off.</item>
/// <item>8: a propane canister half through the floor with a lighting origin.</item>
/// <item>9: a crate with <c>NO_SELF_SHADOWING</c>.</item>
/// </list>
/// <para>
/// The sampler's tracer is a floor, a wall, a sky and every prop's bounding
/// box under its own id, so direct samples are blocked by the world, by the
/// prop itself (unless skipped) and by other props, and the sun passes the
/// sky. The lights are the map's own (a point, a sun, the sky's ambient and
/// surface lights) plus a point, a spot and a surface light over the props.
/// </para>
/// </remarks>
internal static class StaticPropChunkingScene
{
    private const string Crate = "models/props_junk/wood_crate001a.mdl";
    private const string Drum = "models/props_c17/oildrum001.mdl";
    private const string Chair = "models/props_c17/furniturechair001a.mdl";
    private const string Propane = "models/props_junk/propanecanister001a.mdl";

    /// <summary>The props and their models, loaded from the synthetic content.</summary>
    /// <returns>The lump and the model dictionary.</returns>
    public static async Task<(StaticPropLump Lump, IReadOnlyList<StaticPropModel> Models)> PropsAsync()
    {
        await using ContentFileSystem content = await StudioModelWriterTests.MountAsync(SyntheticContent.Build());
        IReadOnlyList<StaticPropModel> models = await new StaticPropModelLoader(content, NullPropCollisionSource.Instance)
            .LoadDictionaryAsync([Crate, Drum, Chair, Propane], CancellationToken.None);

        StaticPropLump lump = new();
        lump.ModelNames.AddRange([Crate, Drum, Chair, Propane]);
        Vec3 lit = new(200, 0, 100);
        lump.Props.AddRange(
        [
            new StaticProp { PropType = 0, Origin = new Vec3(200, 0, -16) },
            new StaticProp { PropType = 0, Origin = new Vec3(150, 40, -36), Angles = new Vec3(0, 20, 0) },
            new StaticProp
            {
                PropType = 0, Origin = new Vec3(250, 40, -30),
                Flags = StaticPropFlags.IgnoreNormals | StaticPropFlags.NoSelfShadowing,
            },
            new StaticProp { PropType = 1, Origin = new Vec3(250, -40, -16), Angles = new Vec3(0, 30, 0), Flags = StaticPropFlags.IgnoreNormals },
            new StaticProp { PropType = 0, Origin = new Vec3(200, 0, -300), Flags = StaticPropFlags.UseLightingOrigin, LightingOrigin = lit },
            new StaticProp { PropType = 0, Origin = new Vec3(100, 0, -300) },
            new StaticProp { PropType = 0, Origin = new Vec3(150, -40, -16), Flags = StaticPropFlags.NoPerVertexLighting },
            new StaticProp
            {
                PropType = 2, Origin = new Vec3(100, -60, -16), Angles = new Vec3(0, 90, 10),
                Flags = StaticPropFlags.NoPerTexelLighting,
            },
            new StaticProp
            {
                PropType = 3, Origin = new Vec3(300, 60, -26), Flags = StaticPropFlags.UseLightingOrigin, LightingOrigin = lit,
            },
            new StaticProp { PropType = 0, Origin = new Vec3(300, -60, -16), Flags = StaticPropFlags.NoSelfShadowing },
        ]);

        return (lump, models);
    }

    /// <summary>The sampler's tracer: the world stand-ins and each prop's box under its own id.</summary>
    /// <param name="lump">The props.</param>
    /// <returns>The tracer, under <see cref="ComplianceOptions.Correct"/>.</returns>
    public static KdRayTracer Tracer(StaticPropLump lump) => Tracer(lump, ComplianceOptions.Correct);

    /// <summary>The sampler's tracer under a compliance.</summary>
    /// <param name="lump">The props.</param>
    /// <param name="compliance">What the KD tracer reproduces of stock's traversal.</param>
    /// <returns>The tracer.</returns>
    public static KdRayTracer Tracer(StaticPropLump lump, ComplianceOptions compliance)
    {
        List<TracedTriangle> triangles =
        [
            new(TraceId.Opaque, new Vec3(-4000, -4000, -16), new Vec3(4000, -4000, -16), new Vec3(0, 4000, -16), 0),
            new(TraceId.Opaque, new Vec3(330, -200, -50), new Vec3(330, 200, -50), new Vec3(330, 0, 400), 0),
            new(TraceId.Sky, new Vec3(-4000, -4000, 1500), new Vec3(4000, -4000, 1500), new Vec3(0, 4000, 1500), 0),
        ];

        for (int i = 0; i < lump.Props.Count; i++)
        {
            Vec3 o = lump.Props[i].Origin;
            Box(triangles, TraceId.StaticProp | i, o + new Vec3(-20, -20, 0), o + new Vec3(20, 20, 40));
        }

        return KdRayTracer.Build(triangles.ToArray(), compliance);
    }

    /// <summary>The map's lights and three more over the props.</summary>
    /// <param name="ambient">The map.</param>
    /// <returns>The lights, in list order.</returns>
    public static IReadOnlyList<PropLight> Lights(AmbientFixture ambient)
    {
        byte[] all = [.. Enumerable.Repeat((byte)0xFF, 8192)];
        List<PropLight> lights = [.. PropLights.FromWorldLights(ambient.Bsp, LightingMode.Ldr, out _)];
        lights.Add(new PropLight { Type = EmitType.Point, Origin = new Vec3(220, 10, 150), Intensity = new Vec3(3, 2, 1), QuadraticAttn = 1, Pvs = all });
        lights.Add(new PropLight
        {
            Type = EmitType.Spotlight, Origin = new Vec3(180, -20, 200), Normal = new Vec3(0, 0, -1), StopDot = 0.9f, StopDot2 = 0.5f,
            Exponent = 2, Intensity = new Vec3(1, 1, 1), QuadraticAttn = 1, Pvs = all,
        });
        lights.Add(new PropLight
        {
            Type = EmitType.Surface, Origin = new Vec3(260, 0, 120), Normal = new Vec3(0, 0, -1), Intensity = new Vec3(2, 2, 2), Pvs = all,
        });
        return lights;
    }

    /// <summary>Lights the scene.</summary>
    /// <param name="ambient">The map.</param>
    /// <param name="lump">The props.</param>
    /// <param name="models">Their models.</param>
    /// <param name="compliance">Which defects to reproduce.</param>
    /// <param name="indirect">Whether good vertices gather indirect light.</param>
    /// <param name="disableSelfShadowing"><c>-disablepropselfshadowing</c>.</param>
    /// <param name="batching">How the work is cut up.</param>
    /// <param name="tracer">
    /// The sampler's tracer, or null for <see cref="Tracer(StaticPropLump, ComplianceOptions)"/>
    /// under <paramref name="compliance"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The result.</returns>
    public static Task<StaticPropLightingResult> LightAsync(
        AmbientFixture ambient,
        StaticPropLump lump,
        IReadOnlyList<StaticPropModel> models,
        ComplianceOptions compliance,
        bool indirect,
        bool disableSelfShadowing,
        Batching batching,
        IRayTracer? tracer = null,
        CancellationToken cancellationToken = default) =>
        StaticPropLighting.ComputeAsync(
            ambient.LdrFor(compliance),
            lump,
            models,
            Lights(ambient),
            new PropLightSampler(tracer ?? Tracer(lump, compliance), compliance, sunAngularExtent: 0.05f),
            new StaticPropLightingOptions
            {
                Compliance = compliance,
                Indirect = indirect,
                DisableSelfShadowing = disableSelfShadowing,
                Parallelism = batching.Parallelism,
                BatchSegments = batching.BatchSegments,
                PointsPerChunk = batching.PointsPerChunk,
                BatchSamples = batching.BatchSamples,
            },
            cancellationToken);

    /// <summary>
    /// A digest of everything a pass returns: each file's name and bytes, in
    /// order, and the three counts.
    /// </summary>
    /// <param name="result">The pass's result.</param>
    /// <returns>The hex SHA-256.</returns>
    public static string Digest(StaticPropLightingResult result)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (StaticPropVhvFile f in result.Files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{f.PropIndex}:{f.FileName}:{f.Data.Length}\n"));
            hash.AppendData(f.Data);
        }

        hash.AppendData(Encoding.UTF8.GetBytes(
            $"{result.BadVertices}/{result.SelfShadowingSkipped}/{result.TexelLightingNotComputed}"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Box(List<TracedTriangle> triangles, int id, Vec3 min, Vec3 max)
    {
        Vec3 C(int i) => new((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        int[][] faces = [[0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]];
        foreach (int[] q in faces)
        {
            triangles.Add(new TracedTriangle(id, C(q[0]), C(q[1]), C(q[2]), 0));
            triangles.Add(new TracedTriangle(id, C(q[0]), C(q[2]), C(q[3]), 0));
        }
    }
}

/// <summary>How a static-prop pass cuts up its work; none of it may change a byte.</summary>
/// <param name="Name">A label for the theory rows.</param>
/// <param name="Parallelism">How many workers.</param>
/// <param name="BatchSegments">The segments a batch closes at.</param>
/// <param name="PointsPerChunk">The light points an item plans.</param>
/// <param name="BatchSamples">The samples a batch closes at.</param>
internal sealed record Batching(string Name, int Parallelism, int BatchSegments, int PointsPerChunk, int BatchSamples)
{
    /// <summary>
    /// The shape before chunking: a whole prop an item, and (one segment
    /// bound) a prop a batch.
    /// </summary>
    public static Batching PropAtATime { get; } = new("prop at a time", 1, 1, int.MaxValue, int.MaxValue);

    /// <summary>Every bound at its smallest: one point an item, one item a batch.</summary>
    public static Batching Smallest { get; } = new("smallest", 1, 1, 1, 1);

    /// <summary>The pass's own defaults, on one worker.</summary>
    public static Batching Defaults { get; } = new(
        "defaults", 1, TestLineStage.DefaultBatchSegments, StaticPropLighting.DefaultPointsPerChunk, StaticPropLighting.DefaultBatchSamples);

    /// <summary>Every shape the facts light the scene in, by name.</summary>
    public static IReadOnlyList<Batching> All { get; } =
    [
        PropAtATime,
        Smallest,
        Smallest with { Name = "smallest, four workers", Parallelism = 4 },
        new("one point, sample bound only", 3, TestLineStage.DefaultBatchSegments, 1, 1),
        new("odd sizes", 3, 13, 5, 37),
        new("whole props, default bounds", 2, TestLineStage.DefaultBatchSegments, int.MaxValue, StaticPropLighting.DefaultBatchSamples),
        Defaults,
        Defaults with { Name = "defaults, four workers", Parallelism = 4 },
    ];

    /// <summary>A shape by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The shape.</returns>
    public static Batching Named(string name) => All.Single(b => b.Name == name);

    /// <inheritdoc />
    public override string ToString() => Name;
}
