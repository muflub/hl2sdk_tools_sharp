//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The committed leaf-ambient fixture: <c>Fixtures/p4g_ambient.bsp</c>, a
/// self-made room compiled by the stock x64 tools (<c>vrad -both -threads 1</c>)
/// and stripped of its pak lump; the recipe is in <c>Fixtures/gen_ambient_fixture.py</c>.
/// </summary>
/// <remarks>
/// It exercises every branch of the stage in 37 leaves: a power-3 displacement
/// floor, a <c>func_detail</c> pillar, 32 texlight <c>emit_surface</c> lights
/// baked into the cubes (with <c>TestLine</c> visibility), a sky half-ceiling
/// with a <c>light_environment</c>, and a point light. Because leaf ambient is
/// the last lighting stage, the file holds both the stage's inputs and stock's
/// outputs, so the unit tier gets a byte-exact oracle with no wine and no
/// game content.
/// </remarks>
public sealed class AmbientFixture : IAsyncLifetime
{
    /// <summary>The fixture's file name.</summary>
    public const string FileName = "p4g_ambient.bsp";

    /// <summary>The loaded map.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>
    /// The LDR scene, built under <see cref="ComplianceOptions.Stock"/>: this
    /// fixture is stock's oracle, so its walk tests the sky as stock does.
    /// </summary>
    public AmbientScene Ldr { get; private set; } = null!;

    /// <summary>The HDR scene, under <see cref="ComplianceOptions.Stock"/> as <see cref="Ldr"/> is.</summary>
    public AmbientScene Hdr { get; private set; } = null!;

    /// <summary>
    /// The LDR scene under <see cref="ComplianceOptions.Correct"/>, for the
    /// facts that pin the default policy's output and so must not take the
    /// sky test's estimate (<see cref="StockQuirk.SkyWindingNormalise"/>).
    /// </summary>
    public AmbientScene CorrectLdr { get; private set; } = null!;

    /// <summary>
    /// <c>TestLine</c> over the map's shadow casters, stock arithmetic: the
    /// KD tracer is built under <see cref="ComplianceOptions.Stock"/> too, so
    /// its traversal takes stock's estimates.
    /// </summary>
    public IAmbientLightVisibility Visibility { get; private set; } = null!;

    /// <summary>
    /// <c>TestLine</c> over the same casters under
    /// <see cref="ComplianceOptions.Correct"/>: no estimate in the KD tracer
    /// or in the segments' normalise.
    /// </summary>
    public IAmbientLightVisibility CorrectVisibility { get; private set; } = null!;

    /// <summary>The LDR scene under a compliance: <see cref="Ldr"/> or <see cref="CorrectLdr"/>.</summary>
    /// <param name="compliance">Decides <see cref="StockQuirk.SkyWindingNormalise"/>.</param>
    /// <returns>The scene.</returns>
    public AmbientScene LdrFor(ComplianceOptions compliance) =>
        compliance.Emulates(StockQuirk.SkyWindingNormalise) ? Ldr : CorrectLdr;

    /// <summary>The fixtures directory of THIS worktree.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">It is missing.</exception>
    public static string Directory()
    {
        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException($"no checkout root above {AppContext.BaseDirectory}");
        string dir = Path.Combine(
            root, "src", "SourceSharp.Tests", "MapTools", "Rad", "Ambient", "Fixtures");
        return System.IO.Directory.Exists(dir)
            ? dir
            : throw new InvalidOperationException($"{dir} is missing; these are COMMITTED files");
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using FileStream stream = File.OpenRead(Path.Combine(Directory(), FileName));
        Bsp = await BspFile.LoadAsync(stream);
        Ldr = AmbientScene.Create(Bsp, LightingMode.Ldr, ComplianceOptions.Stock);
        Hdr = AmbientScene.Create(Bsp, LightingMode.Hdr, ComplianceOptions.Stock);
        CorrectLdr = AmbientScene.Create(Bsp, LightingMode.Ldr, ComplianceOptions.Correct);

        // The map has no static props, so the casters need no game content:
        // an empty content file system proves it (a prop lookup would miss).
        await using ContentFileSystem content = new([]);
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            Bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        Visibility = new TracerLineVisibility(casters.Set.BuildTracer(ComplianceOptions.Stock), ComplianceOptions.Stock);
        CorrectVisibility = new TracerLineVisibility(casters.Set.BuildTracer(ComplianceOptions.Correct), ComplianceOptions.Correct);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Runs the stage on one scene.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="options">The options.</param>
    /// <returns>The result.</returns>
    public Task<LeafAmbientResult> BuildAsync(AmbientScene scene, LeafAmbientOptions options) =>
        LeafAmbientBuilder.BuildAsync(scene, scene.WorldLights.ToArray(), options, Visibility, CancellationToken.None);

    /// <summary>A lump as structs.</summary>
    /// <typeparam name="T">The struct.</typeparam>
    /// <param name="lump">The lump.</param>
    /// <returns>A copy.</returns>
    public T[] Lump<T>(BspLump lump)
        where T : unmanaged => BspStructView.As<T>(Bsp[lump]).ToArray();
}
