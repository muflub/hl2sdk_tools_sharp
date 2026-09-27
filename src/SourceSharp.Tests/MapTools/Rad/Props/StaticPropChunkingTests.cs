//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapGen.Content;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// Static-prop lighting lights a few vertices a work item rather than a whole
/// prop, and closes a worker's batch at a bound on its planned samples, so no
/// prop, however large, fills one worker's batch on its own. None of that may
/// change a byte: every vertex path (good, bad and relit from a neighbour, bad
/// and crawled toward a lighting origin, wholly in solid and left dark) must
/// come out as it did when a prop was one item and one batch.
/// </summary>
/// <remarks>
/// <para>
/// The invariance facts compare every batching with the prop-at-a-time shape
/// (one prop an item, as the pass was before chunking) on the machine that
/// runs them, under both compliance policies. That is the property chunking
/// must keep, and it holds on every CPU.
/// </para>
/// <para>
/// The digests themselves are pinned separately, per CPU family, by
/// <see cref="TheCorrectDigestsArePinnedForThisCpu"/>. Even compliance correct
/// is not the same bytes everywhere here: the KD tracer's traversal keeps the
/// estimated reciprocal under both policies, so a ray grazing a split can
/// resolve differently on arm64 than on x86 (README, "Platform differences").
/// The base digests were recorded on x86 from the pass as it was before
/// chunking, so they also pin that chunking changed no byte; AMD and Intel
/// agree on them (both kinds of CI runner passed them), and arm64 has a
/// captured delta.
/// </para>
/// </remarks>
public sealed class StaticPropChunkingTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _ambient;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="ambient">The leaf-ambient map the props stand in.</param>
    public StaticPropChunkingTests(AmbientFixture ambient) => _ambient = ambient;

    /// <summary>Every batching with each indirect and self-shadowing switch.</summary>
    /// <returns>The rows.</returns>
    public static TheoryData<bool, bool, string> EveryBatching()
    {
        TheoryData<bool, bool, string> rows = [];
        foreach (bool indirect in new[] { true, false })
        {
            foreach (bool disableSelfShadowing in new[] { false, true })
            {
                foreach (Batching b in Batching.All)
                {
                    rows.Add(indirect, disableSelfShadowing, b.Name);
                }
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryBatching))]
    public async Task CorrectOutputIsThePropAtATimeBytesWhateverTheBatching(bool indirect, bool disableSelfShadowing, string batching)
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();

        StaticPropLightingResult whole = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, indirect, disableSelfShadowing, Batching.PropAtATime);
        StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, indirect, disableSelfShadowing, Batching.Named(batching));

        Assert.Equal(StaticPropChunkingScene.Digest(whole), StaticPropChunkingScene.Digest(r));
    }

    /// <summary>
    /// The compliance-correct digests of the prop-at-a-time shape on x86, one
    /// line per indirect / self-shadowing combination, in the order
    /// <see cref="TheCorrectDigestsArePinnedForThisCpu"/> produces them.
    /// </summary>
    private static IReadOnlyList<string> PinnedCorrectDigests =>
    [
        "F0C198800BB393D6D4399142305C7CC05D553F734980F8198CB87AFD4688E304", // indirect, self-shadowing
        "FFC6EAC288843090891D6BD4E43B3C2388CAF765D168BB13299F42E77CB20294", // indirect, self-shadowing disabled
        "DBA68BECEBEEB9C56383F9793B49A72EB933444DA6BF65E0136E817090D056EE", // direct only, self-shadowing
        "EA3C9E27546D81B9FD941B33982C598EA8A2EB518ADC3C76CD0197C8A595D1B3", // direct only, self-shadowing disabled
    ];

    [ReferenceRsqrtFact]
    public async Task TheCorrectDigestsArePinnedForThisCpu()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();

        List<string> actual = [];
        foreach ((bool indirect, bool disableSelfShadowing) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
                _ambient, lump, models, ComplianceOptions.Correct, indirect, disableSelfShadowing, Batching.PropAtATime);
            actual.Add(StaticPropChunkingScene.Digest(r));
        }

        Assert.Equal(VendorGolden.Expected("static-prop-chunking.correct", PinnedCorrectDigests, actual), actual);
    }

    [Theory]
    [MemberData(nameof(EveryBatching))]
    public async Task StockOutputIsThePropAtATimeBytesWhateverTheBatching(bool indirect, bool disableSelfShadowing, string batching)
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();

        StaticPropLightingResult whole = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Stock, indirect, disableSelfShadowing, Batching.PropAtATime);
        StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Stock, indirect, disableSelfShadowing, Batching.Named(batching));

        Assert.Equal(StaticPropChunkingScene.Digest(whole), StaticPropChunkingScene.Digest(r));
    }

    [Fact]
    public async Task TheSceneTakesEveryVertexPath()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();

        StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, true, false, Batching.Smallest);

        // Prop 6 has no per-vertex lighting and no file; prop 7 alone keeps
        // per-texel lighting off; props 2 and 9 skip their own triangles.
        Assert.Equal([0, 1, 2, 3, 4, 5, 7, 8, 9], r.Files.Select(f => f.PropIndex));
        Assert.Equal(8, r.TexelLightingNotComputed);
        Assert.Equal(2, r.SelfShadowingSkipped);
        Assert.Equal(122, r.BadVertices);

        // Wholly in solid: dark without a lighting origin, lit with one.
        Assert.False(HasColour(r.Files.Single(f => f.PropIndex == 5)));
        Assert.True(HasColour(r.Files.Single(f => f.PropIndex == 4)));
        Assert.All(r.Files.Where(f => f.PropIndex != 5), f => Assert.True(HasColour(f)));
    }

    [Fact]
    public async Task RelightingABadVertexWithThePropsFlagsChangesOnlyAPropWithFlags()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();
        ComplianceOptions dropping = ComplianceOptions.Correct.Flipping(StockQuirk.StaticPropBadVertexDropsPropFlags);

        StaticPropLightingResult keep = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, true, false, Batching.Smallest);
        StaticPropLightingResult drop = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, dropping, true, false, Batching.Smallest);

        // Prop 2 is in the floor with IGNORE_NORMALS and NO_SELF_SHADOWING;
        // prop 1 is in the floor with neither; prop 0 has no bad vertex.
        Assert.NotEqual(File(keep, 2), File(drop, 2));
        Assert.Equal(File(keep, 1), File(drop, 1));
        Assert.Equal(File(keep, 0), File(drop, 0));
    }

    [Fact]
    public async Task ABatchHoldsABoundedNumberOfSamplesHoweverLargeTheProp()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();
        int lights = StaticPropChunkingScene.Lights(_ambient).Count;
        const int Bound = 64;

        StaticPropLightingResult whole = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, false, false, Batching.PropAtATime);
        StaticPropLightingResult bounded = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, false, false,
            new Batching("bounded", 1, TestLineStage.DefaultBatchSegments, 1, Bound));

        // A batch closes once it holds the bound, so it passes it by less
        // than one item: one point, at most one sample a light.
        Assert.InRange(bounded.PeakBatchSamples, 1, Bound - 1 + lights);

        // A whole prop an item is what the bound is for: one prop alone
        // already holds more.
        Assert.True(
            whole.PeakBatchSamples > Bound - 1 + lights,
            $"a prop at a time peaked at {whole.PeakBatchSamples} samples");
        Assert.Equal(StaticPropChunkingScene.Digest(whole), StaticPropChunkingScene.Digest(bounded));
    }

    [Fact]
    public async Task TheDefaultsBoundABatchByAChunkPastTheSampleBound()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();
        int lights = StaticPropChunkingScene.Lights(_ambient).Count;

        StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, false, false, Batching.Defaults);

        Assert.InRange(
            r.PeakBatchSamples,
            1,
            StaticPropLighting.DefaultBatchSamples - 1 + (StaticPropLighting.DefaultPointsPerChunk * lights));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task BoundsBelowOneAreRefused(int pointsPerChunk, int batchSamples)
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, false, false,
            new Batching("bad", 1, 1, pointsPerChunk, batchSamples)));
    }

    [Fact]
    public async Task ACancelledPassStopsBetweenChunks()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();
        using CancellationTokenSource source = new();
        CancellingTracer tracer = new(StaticPropChunkingScene.Tracer(lump), source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StaticPropChunkingScene.LightAsync(
            _ambient, lump, models, ComplianceOptions.Correct, false, false, Batching.Smallest, tracer, source.Token));

        // The first batch cancelled the pass; with one point a batch the
        // scene has well over a hundred.
        Assert.InRange(tracer.Calls, 1, 4);
    }

    [Fact]
    public async Task AModelWithoutAVtxWritesAFileWithNoMeshes()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await CrateWithoutAsync(".dx80.vtx", ".dx90.vtx");
        Assert.Null(models[0].Vtx);

        StaticPropLightingResult r = await LightOneAsync(lump, models);

        StaticPropVhvFile file = Assert.Single(r.Files);
        Assert.Equal(StaticPropLighting.EncodeVhv(models[0].Mdl!.Checksum, []), file.Data);
        Assert.Equal(0, r.BadVertices);
    }

    [Fact]
    public async Task AModelWithAVtxButNoVvdIsRefused()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await CrateWithoutAsync(".vvd");
        Assert.NotNull(models[0].Vtx);
        Assert.Null(models[0].Vvd);

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(() => LightOneAsync(lump, models));
        Assert.Contains("has no .vvd", e.Message, StringComparison.Ordinal);
    }

    private static bool HasColour(StaticPropVhvFile file)
    {
        // Every vertex is b, g, r, a from the first 512-byte boundary on.
        byte[] d = file.Data;
        for (int i = StaticPropLighting.Alignment; i + 3 < d.Length; i += 4)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] File(StaticPropLightingResult r, int prop) => r.Files.Single(f => f.PropIndex == prop).Data;

    private static async Task<(StaticPropLump Lump, IReadOnlyList<StaticPropModel> Models)> CrateWithoutAsync(params string[] suffixes)
    {
        const string Crate = "models/props_junk/wood_crate001a.mdl";
        Dictionary<string, byte[]> files = new(SyntheticContent.Build());
        foreach (string suffix in suffixes)
        {
            Assert.True(files.Remove("models/props_junk/wood_crate001a" + suffix), suffix);
        }

        await using ContentFileSystem content = await StudioModelWriterTests.MountAsync(files);
        IReadOnlyList<StaticPropModel> models = await new StaticPropModelLoader(content, NullPropCollisionSource.Instance)
            .LoadDictionaryAsync([Crate], CancellationToken.None);
        Assert.NotNull(models[0].Mdl);

        StaticPropLump lump = new();
        lump.ModelNames.Add(Crate);
        lump.Props.Add(new StaticProp { PropType = 0, Origin = new Vec3(200, 0, -16) });
        return (lump, models);
    }

    private Task<StaticPropLightingResult> LightOneAsync(StaticPropLump lump, IReadOnlyList<StaticPropModel> models) =>
        StaticPropChunkingScene.LightAsync(_ambient, lump, models, ComplianceOptions.Correct, true, false, Batching.Defaults);

    // Answers with the KD tracer and cancels the pass on its first call.
    private sealed class CancellingTracer(KdRayTracer inner, CancellationTokenSource source) : IRayTracer
    {
        private int _calls;

        public int Calls => _calls;

        public string TracerIdentity => "cancelling";

        public bool Supports(RayTraceOptions options) => true;

        public ValueTask TraceVisibilityAsync(
            ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, RayTraceOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            inner.TraceVisibility(rays.Span, hitBits.Span, options);
            source.Cancel();
            return ValueTask.CompletedTask;
        }

        public ValueTask TraceClosestAsync(
            ReadOnlyMemory<Ray> rays, Memory<HitId> hits, RayTraceOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("prop lighting asks no closest hits");
    }
}
