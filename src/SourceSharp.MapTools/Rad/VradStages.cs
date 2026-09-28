//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// One lighting pass of a <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/> compile, as the
/// stages after direct lighting see it.
/// </summary>
/// <remarks>
/// Everything stock keeps in globals between <c>RadWorld_Start</c> and
/// <c>VRAD_Finish</c> for one range: the world, the tracer that lit it, the
/// lighting lump once <c>FinalLightFace</c> has filled it.
/// </remarks>
public sealed class RadPass
{
    private Ambient.AmbientScene? _scene;

    internal RadPass(
        RadWorld world,
        IRayTracer tracer,
        VradOptions options,
        IContentFileSystem content,
        CompileParallelism parallelism,
        string mapName,
        IPropCollisionSource propCollision)
    {
        PropCollision = propCollision;
        World = world;
        Tracer = tracer;
        Options = options;
        Content = content;
        Parallelism = parallelism;
        MapName = mapName;
    }

    /// <summary>The patches, facelights and lights of this pass.</summary>
    public RadWorld World { get; }

    /// <summary>True for the HDR pass.</summary>
    public bool Hdr => World.Settings.Hdr;

    /// <summary>The tracer every stage of the compile shares.</summary>
    public IRayTracer Tracer { get; }

    /// <summary>What was asked for.</summary>
    public VradOptions Options { get; }

    /// <summary>Game content: models, materials, textures.</summary>
    public IContentFileSystem Content { get; }

    /// <summary>How many workers a stage may use.</summary>
    public CompileParallelism Parallelism { get; }

    /// <summary>The map's file base name (stock's <c>source</c> after <c>Q_FileBase</c>).</summary>
    public string MapName { get; }

    /// <summary>
    /// This pass's lighting lump, after <c>FinalLightFace</c>; empty before, and
    /// under <c>-onlydetail</c> / <c>-OnlyStaticProps</c>, which skip
    /// <c>RadWorld_Go</c>.
    /// </summary>
    public byte[] LightData { get; internal set; } = [];

    /// <summary>Where static props' collision triangles come from.</summary>
    public IPropCollisionSource PropCollision { get; }

    /// <summary>
    /// The pass's map as the other-lighting stages read it: the lightmaps and
    /// world lights this pass wrote, walked as a BSP. Built once, on first use,
    /// after the pass's lumps are in <paramref name="bsp"/>.
    /// </summary>
    /// <param name="bsp">The map, with this pass's lumps written.</param>
    /// <returns>The scene.</returns>
    public Ambient.AmbientScene Scene(BspData bsp) =>
        _scene ??= Ambient.AmbientScene.Create(
            bsp, Hdr ? Ambient.LightingMode.Hdr : Ambient.LightingMode.Ldr, Options.Compliance);

    /// <summary>Warnings the stages add, reported with the pass.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// <c>MakeAllScales</c> and <c>BounceLight</c>:
/// the radiosity bounce. Lane 4d's.
/// </summary>
public interface IVradBounceStage
{
    /// <summary>
    /// Builds the transfers and gathers <see cref="RadWorld.Settings"/>'
    /// bounces into every patch's <c>TotalLight</c>.
    /// </summary>
    /// <param name="pass">The pass, after direct lighting.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>A task that completes when the patches hold their bounced light.</returns>
    Task BounceAsync(RadPass pass, CancellationToken cancellationToken);
}

/// <summary>
/// One of <c>VRAD_ComputeOtherLighting</c>'s stages:
/// detail props, leaf ambient, static props. Lane 4g's.
/// </summary>
public interface IVradOtherLightingStage
{
    /// <summary>Computes the stage's lighting and writes its lumps into the map.</summary>
    /// <param name="pass">The pass, after <c>FinalLightFace</c>.</param>
    /// <param name="bsp">The map being written.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>A task that completes when the lumps are written.</returns>
    Task ComputeAsync(RadPass pass, BspData bsp, CancellationToken cancellationToken);
}

/// <summary>
/// The stages of a vrad compile that other lanes own, as injectable parts.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> holds the ported stages (4d's bounce, 4g's detail
/// props, leaf ambient and static props). A null member is a stage the compile
/// runs without: <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/> never skips one silently -- when
/// the compile needs a null stage it records a <c>VRAD0701</c> warning naming
/// it, lists it in <see cref="RadResult.StagesNotYetPorted"/>, and carries on.
/// Tests and hosts may pass their own (a fake, or a GPU implementation).
/// </para>
/// <para>
/// Displacements are not a stage here: 4e's sampling and radials are part of
/// <see cref="RadWorld"/> and <c>FinalLightFace</c> themselves.
/// </para>
/// </remarks>
public sealed record VradStages
{
    /// <summary>The bounce (4d). Needed when the pass has bounces.</summary>
    public IVradBounceStage? Bounce { get; init; }

    /// <summary><c>ComputeDetailPropLighting</c> (4g). Needed unless <c>-nodetaillight</c>.</summary>
    public IVradOtherLightingStage? DetailPropLighting { get; init; }

    /// <summary><c>ComputePerLeafAmbientLighting</c> (4g). Always needed.</summary>
    public IVradOtherLightingStage? LeafAmbientLighting { get; init; }

    /// <summary>
    /// <c>StaticPropMgr()-&gt;ComputeLighting</c> (4g). Needed under
    /// <c>-StaticPropLighting</c> without <c>-fast</c>.
    /// </summary>
    public IVradOtherLightingStage? StaticPropLighting { get; init; }

    /// <summary>The stages this build has.</summary>
    public static VradStages Default { get; } = new()
    {
        Bounce = new VradBuiltInStages.Bounce(),
        DetailPropLighting = new VradBuiltInStages.DetailProps(),
        LeafAmbientLighting = new VradBuiltInStages.LeafAmbient(),
        StaticPropLighting = new VradBuiltInStages.StaticProps(),
    };

    /// <summary>No stages at all: every one is reported as not ported.</summary>
    public static VradStages None { get; } = new();
}
