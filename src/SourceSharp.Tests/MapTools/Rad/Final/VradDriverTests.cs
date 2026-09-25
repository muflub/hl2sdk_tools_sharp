using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary><see cref="Vrad.LightAsync"/> on in-memory maps: stage order, seams, reports.</summary>
public sealed class VradDriverTests
{
    private static readonly VradOptions BounceZero = VradOptions.Default with { Bounces = 0 };

    private static LightTestMap LitBox()
    {
        // One cluster that sees itself: a map WITH vis, so bounces are not
        // forced to zero.
        LightTestMap map = LightBox.Map();
        map.Visibility = LightTestMap.VisLump([[true]]);
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        return map;
    }

    private static async Task<IContentFileSystem> ContentAsync(params (string Name, string Text)[] files)
    {
        InMemoryFileSystem fs = new();
        foreach ((string name, string text) in files)
        {
            fs.AddText(name, text);
        }

        return new ContentFileSystem([await DirectoryContentMount.MountAsync(fs, VPath.Empty)]);
    }

    private static async Task<(BspData Map, RadResult Result)> LightAsync(
        LightTestMap map,
        VradOptions options,
        VradStages? stages = null,
        IContentFileSystem? content = null)
    {
        BspData bsp = map.Build();
        RadResult result = await Vrad.LightAsync(bsp, new VradContext
        {
            Options = options,
            MapName = "box",
            Content = content,
            Tracer = map.Tracer(),
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            Stages = stages ?? VradStages.None,
        });
        return (bsp, result);
    }

    [Fact]
    public void BothIsLdrThenHdr()
    {
        //: mode 0 is -ldr, mode 1 -hdr.
        Assert.Equal([false, true], Vrad.Passes(VradLightingRange.Both));
    }

    [Fact]
    public void HdrAloneIsOnePass()
    {
        Assert.Equal([true], Vrad.Passes(VradLightingRange.Hdr));
    }

    [Fact]
    public async Task TheLightingLumpIsWrittenAtTheLayoutsSize()
    {
        (BspData map, RadResult result) = await LightAsync(LitBox(), BounceZero);
        Assert.Equal(result.Passes[0].LightDataSize, map[BspLump.Lighting].Length);
    }

    [Fact]
    public async Task ABothCompileWritesBothLightingLumps()
    {
        (BspData map, _) = await LightAsync(LitBox(), BounceZero with { Range = VradLightingRange.Both });
        Assert.Equal(map[BspLump.Lighting].Length, map[BspLump.LightingHdr].Length);
    }

    [Fact]
    public async Task AMissingBounceStageIsReportedAsNotYetPorted()
    {
        (_, RadResult result) = await LightAsync(LitBox(), VradOptions.Default);
        Assert.Contains(result.StagesNotYetPorted, s => s.StartsWith("bounce", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingStageCarriesAWarningDiagnostic()
    {
        (_, RadResult result) = await LightAsync(LitBox(), VradOptions.Default);
        Assert.Contains(result.Diagnostics, d => d.Code == VradCodes.StageNotYetPorted && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task BounceZeroNeedsNoBounceStage()
    {
        (_, RadResult result) = await LightAsync(LitBox(), BounceZero);
        Assert.DoesNotContain(result.StagesNotYetPorted, s => s.StartsWith("bounce", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeafAmbientIsAlwaysNeeded()
    {
        (_, RadResult result) = await LightAsync(LitBox(), BounceZero);
        Assert.Contains(result.StagesNotYetPorted, s => s.StartsWith("leaf ambient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoDetailLightNeedsNoDetailStage()
    {
        (_, RadResult result) = await LightAsync(LitBox(), BounceZero with { NoDetailLighting = true });
        Assert.DoesNotContain(result.StagesNotYetPorted, s => s.StartsWith("detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuppliedBounceStageRunsOncePerPass()
    {
        CountingBounce bounce = new();
        _ = await LightAsync(
            LitBox(),
            VradOptions.Default with { Range = VradLightingRange.Both },
            new VradStages { Bounce = bounce });

        Assert.Equal([false, true], bounce.Passes);
    }

    [Fact]
    public async Task OtherLightingRunsAfterTheLightingLumpExists()
    {
        //: VRAD_ComputeOtherLighting follows RadWorld_Go.
        CountingOther leaf = new();
        _ = await LightAsync(LitBox(), BounceZero, new VradStages { LeafAmbientLighting = leaf });

        Assert.True(leaf.LightDataLength > 0);
    }

    [Fact]
    public async Task AMissingLightsRadIsAWarningAsInStock()
    {
        //: "Couldn't open texlight file" and carry on.
        (_, RadResult result) = await LightAsync(LitBox(), BounceZero);
        Assert.Contains(result.Diagnostics, d => d.Code == VradCodes.TexlightFileMissing);
    }

    [Fact]
    public async Task TheLevelRadFileIsReadThroughTheContent()
    {
        //: <map>.rad, optional and implied. A texlight on
        // the floor material makes the floor a light.
        IContentFileSystem content = await ContentAsync(("box.rad", "concrete/floor 255 255 255 200\n"));
        (_, RadResult with) = await LightAsync(LitBox(), BounceZero, content: content);
        (_, RadResult without) = await LightAsync(LitBox(), BounceZero);

        Assert.True(with.Passes[0].World.DirectLights > without.Passes[0].World.DirectLights);
    }

    [Fact]
    public async Task ATexlightRedefinedByALaterFileIsReported()
    {
        IContentFileSystem content = await ContentAsync(
            ("lights.rad", "concrete/floor 255 255 255 100\n"),
            ("box.rad", "concrete/floor 255 255 255 200\n"));
        (_, RadResult result) = await LightAsync(LitBox(), BounceZero, content: content);

        Assert.Contains(result.Diagnostics, d => d.Code == VradCodes.TexlightOverride);
    }

    [Fact]
    public async Task LuxelDensityRewritesTheMapBeforeLighting()
    {
        (BspData map, _) = await LightAsync(LitBox(), BounceZero with { LuxelDensity = 32 });
        TexInfo tex = MemoryMarshal.Cast<byte, TexInfo>(map[BspLump.TexInfo].Data.Span)[0];

        Assert.Equal(1f / 32, tex.LightmapVecsLuxelsPerWorldUnits[0]);
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        BspData bsp = LitBox().Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Vrad.LightAsync(bsp, new VradContext { Options = BounceZero }, cancelled.Token));
    }

    [Fact]
    public async Task OnlyDetailLeavesTheLightingLumpAlone()
    {
        //: RadWorld_Go is skipped.
        (BspData map, _) = await LightAsync(LitBox(), BounceZero with { OnlyDetail = true });
        Assert.True(map[BspLump.Lighting].IsEmpty);
    }

    [Fact]
    public async Task OnlyDetailStillSavesTheVertexNormals()
    {
        (BspData map, _) = await LightAsync(LitBox(), BounceZero with { OnlyDetail = true });
        Assert.Equal(72, map[BspLump.VertNormals].Length);
    }

    [Fact]
    public async Task AMapWithNoDetailPropsGetsNoDetailLightingLumps()
    {
        //: no props, return before writing
        // dplt/dplh. ss_sandbox's game lump grew 60 -> 100 bytes without this.
        BspData bsp = LitBox().Build();
        bsp.GameLumps.Add(new DetailPropLump().Write());
        int before = bsp.GameLumps.Count;

        _ = await Vrad.LightAsync(bsp, new VradContext
        {
            Options = BounceZero,
            MapName = "box",
            Tracer = LitBox().Tracer(),
            Stages = new VradStages { DetailPropLighting = VradStages.Default.DetailPropLighting },
        });

        Assert.Equal(before, bsp.GameLumps.Count);
    }

    private sealed class CountingBounce : IVradBounceStage
    {
        public List<bool> Passes { get; } = [];

        public Task BounceAsync(RadPass pass, CancellationToken cancellationToken)
        {
            Passes.Add(pass.Hdr);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingOther : IVradOtherLightingStage
    {
        public int LightDataLength { get; private set; }

        public Task ComputeAsync(RadPass pass, BspData bsp, CancellationToken cancellationToken)
        {
            LightDataLength = pass.LightData.Length;
            return Task.CompletedTask;
        }
    }
}
