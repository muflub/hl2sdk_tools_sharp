using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The committed detail-prop fixture: <c>Rad/Ambient/Fixtures/p4g_detail.bsp</c>,
/// the leaf-ambient room with a <c>%detailtype</c> floor (764 sprite props from
/// <c>p4g_detail.vbsp</c>), a spot light and a style-5 light added, compiled by
/// stock x64 <c>vrad -both -threads 1</c> and stripped of its pak lump. After
/// <c>-both</c> the props' <c>m_Lighting</c> holds the HDR pass; <c>dplt</c>
/// and <c>dplh</c> hold each pass's lightstyle records.
/// </summary>
public sealed class DetailPropFixture : IAsyncLifetime
{
    /// <summary>The map.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>The detail prop lump as stock left it.</summary>
    public DetailPropLump Stock { get; private set; } = null!;

    /// <summary><c>g_RtEnv</c>.</summary>
    public SourceSharp.MapTools.Tracing.KdRayTracer Environment { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using FileStream stream = File.OpenRead(Path.Combine(AmbientFixture.Directory(), "p4g_detail.bsp"));
        Bsp = await BspFile.LoadAsync(stream);
        Stock = DetailPropLump.Read(Lump(GameLumpId.DetailProps));

        await using ContentFileSystem content = new([]);
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            Bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        Environment = casters.Set.BuildTracer();
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>One game lump.</summary>
    /// <param name="code">Its four-character code.</param>
    /// <returns>The entry.</returns>
    public GameLumpEntry Lump(string code) =>
        Bsp.GameLumps.First(e => e.Id == GameLumpId.MakeId(code));

    /// <summary>Runs one pass.</summary>
    /// <param name="mode">Which pass.</param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="compliance">Which defects to reproduce, Stock by default.</param>
    /// <returns>The result.</returns>
    public Task<DetailPropLightingResult> RunAsync(LightingMode mode, int parallelism = 1, ComplianceOptions? compliance = null)
    {
        compliance ??= ComplianceOptions.Stock;
        AmbientScene scene = AmbientScene.Create(Bsp, mode);
        IReadOnlyList<PropLight> lights = PropLights.FromWorldLights(Bsp, mode, out _);
        PropLightSampler sampler = new(Environment, compliance);
        return DetailPropLighting.ComputeAsync(
            scene, Stock, Array.Empty<Vec3>(), lights, sampler, compliance, parallelism, CancellationToken.None);
    }
}
