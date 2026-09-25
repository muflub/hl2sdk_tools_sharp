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

    /// <summary>The LDR scene.</summary>
    public AmbientScene Ldr { get; private set; } = null!;

    /// <summary>The HDR scene.</summary>
    public AmbientScene Hdr { get; private set; } = null!;

    /// <summary><c>TestLine</c> over the map's shadow casters, stock arithmetic.</summary>
    public IAmbientLightVisibility Visibility { get; private set; } = null!;

    /// <summary>The fixtures directory of THIS worktree.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">It is missing.</exception>
    public static string Directory()
    {
        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException($"no checkout root above {AppContext.BaseDirectory}");
        string dir = Path.Combine(
            root, "src", "sourcesharp", "managed", "SourceSharp.Tests", "MapTools", "Rad", "Ambient", "Fixtures");
        return System.IO.Directory.Exists(dir)
            ? dir
            : throw new InvalidOperationException($"{dir} is missing; these are COMMITTED files");
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using FileStream stream = File.OpenRead(Path.Combine(Directory(), FileName));
        Bsp = await BspFile.LoadAsync(stream);
        Ldr = AmbientScene.Create(Bsp, LightingMode.Ldr);
        Hdr = AmbientScene.Create(Bsp, LightingMode.Hdr);

        // The map has no static props, so the casters need no game content:
        // an empty content file system proves it (a prop lookup would miss).
        await using ContentFileSystem content = new([]);
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            Bsp, VradOptions.Default, content, NullPropCollisionSource.Instance);
        Visibility = new TracerLineVisibility(casters.Set.BuildTracer(), ComplianceOptions.Stock);
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
