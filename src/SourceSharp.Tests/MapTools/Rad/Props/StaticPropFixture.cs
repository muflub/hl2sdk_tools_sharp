using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The committed static-prop fixture: <c>Rad/Ambient/Fixtures/p4g_props.bsp</c>,
/// a floor with four stock HL2 props (<c>gen_ambient_fixture.py --props</c>),
/// compiled by stock x64 <c>vrad -both -threads 1 -StaticPropLighting
/// -StaticPropPolys</c>, its pak cut down to the eight <c>.vhv</c> files stock
/// wrote. Prop 0 is <c>NO_SELF_SHADOWING</c>, prop 1 <c>IGNORE_NORMALS</c> and
/// prop 2 lit from an <c>info_lighting</c>; 1364 vertices sit in solid. On this
/// map stock's LDR and HDR files are byte-identical.
/// </summary>
/// <remarks>
/// The props' models are NOT committed: they are read from the installed game,
/// so every fact that lights them is an <see cref="InstalledGameFactAttribute"/>
/// and this fixture loads nothing when the game is absent.
/// </remarks>
public sealed class StaticPropFixture : IAsyncLifetime
{
    /// <summary>The fixture's file name.</summary>
    public const string FileName = "p4g_props.bsp";

    private ContentFileSystem? _content;

    /// <summary>The map.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>The <c>sprp</c> lump.</summary>
    public StaticPropLump Lump { get; private set; } = null!;

    /// <summary>The model dictionary.</summary>
    public IReadOnlyList<StaticPropModel> Models { get; private set; } = [];

    /// <summary><c>g_RtEnv</c> with the props' render meshes in it.</summary>
    public SourceSharp.MapTools.Tracing.KdRayTracer Environment { get; private set; } = null!;

    /// <summary>The stock pak's <c>.vhv</c> files.</summary>
    public ZipArchiveReader StockPak { get; private set; } = null!;

    /// <summary>Loads the map from a fresh copy.</summary>
    /// <returns>The map.</returns>
    public static async Task<BspData> LoadAsync()
    {
        await using FileStream stream = File.OpenRead(Path.Combine(AmbientFixture.Directory(), FileName));
        return await BspFile.LoadAsync(stream);
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Bsp = await LoadAsync();
        Lump = StaticPropLump.Read(Bsp.GameLumps.First(e => e.Id == GameLumpId.MakeId(GameLumpId.StaticProps)));
        StockPak = await ZipArchiveReader.ParseAsync(Bsp[BspLump.PakFile].Data);

        if (InstalledGameContent.SkipReason is not null)
        {
            return;
        }

        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
            new ReadOnlyFileSystem(host),
            host.ToVirtualPath(Path.Combine(InstalledGameContent.BaseDirectory, "hl2mp", "gameinfo.txt")),
            host.ToVirtualPath(InstalledGameContent.BaseDirectory));
        _content = mounted.Content;

        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            Bsp, VradOptions.Default with { StaticPropPolys = true }, _content, NullPropCollisionSource.Instance);
        Environment = casters.Set.BuildTracer();
        Models = await new StaticPropModelLoader(_content, NullPropCollisionSource.Instance).LoadDictionaryAsync(Lump.ModelNames);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_content is not null)
        {
            await _content.DisposeAsync();
        }
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="mode">Which pass.</param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="compliance">Which defects to reproduce, Stock by default.</param>
    /// <returns>The result.</returns>
    public Task<StaticPropLightingResult> RunAsync(LightingMode mode, int parallelism = 1, ComplianceOptions? compliance = null)
    {
        compliance ??= ComplianceOptions.Stock;
        AmbientScene scene = AmbientScene.Create(Bsp, mode);
        IReadOnlyList<PropLight> lights = PropLights.FromWorldLights(Bsp, mode, out _);
        PropLightSampler sampler = new(Environment, compliance);
        return StaticPropLighting.ComputeAsync(
            scene,
            Lump,
            Models,
            lights,
            sampler,
            new StaticPropLightingOptions { Hdr = mode == LightingMode.Hdr, Compliance = compliance, Parallelism = parallelism },
            CancellationToken.None);
    }

    /// <summary>The files of a result that differ from stock's, by name.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The names of the files that are missing from, or differ from, the stock pak.</returns>
    public IReadOnlyList<string> Mismatches(StaticPropLightingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<string> bad = [];
        foreach (StaticPropVhvFile f in result.Files)
        {
            ZipEntry? stock = StockPak.Find(f.FileName);
            if (stock is null || !stock.Data.AsSpan().SequenceEqual(f.Data))
            {
                bad.Add(f.FileName);
            }
        }

        return bad;
    }
}
