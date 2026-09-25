using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// The whole leaf-ambient stage against stock's own bytes, on the committed
/// fixture -- the unit-tier twin of <see cref="LeafAmbientStockParityTests"/>.
/// </summary>
public sealed class LeafAmbientFixtureTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public LeafAmbientFixtureTests(AmbientFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheLdrLightingLumpIsStocksByteForByte()
    {
        LeafAmbientResult r = await _fixture.BuildAsync(_fixture.Ldr, LeafAmbientOptions.StockParity);

        Assert.Equal(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(_fixture.Lump<DLeafAmbientLighting>(BspLump.LeafAmbientLighting)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting).ToArray());
    }

    [Fact]
    public async Task TheLdrIndexLumpIsStocksEntryForEntry()
    {
        LeafAmbientResult r = await _fixture.BuildAsync(_fixture.Ldr, LeafAmbientOptions.StockParity);

        Assert.Equal(
            MemoryMarshal.AsBytes<DLeafAmbientIndex>(_fixture.Lump<DLeafAmbientIndex>(BspLump.LeafAmbientIndex)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientIndex>(r.Index).ToArray());
    }

    [Fact]
    public async Task TheHdrLightingLumpIsStocksByteForByte()
    {
        LeafAmbientResult r = await _fixture.BuildAsync(_fixture.Hdr, LeafAmbientOptions.StockParity);

        Assert.Equal(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(_fixture.Lump<DLeafAmbientLighting>(BspLump.LeafAmbientLightingHdr)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting).ToArray());
    }

    [Fact]
    public async Task TheFixtureBakesSurfaceLightsSoVisibilityIsExercised()
    {
        // leaf_ambient_lighting.cpp:626-640: the fixture's .rad makes every
        // texlight dim enough to go in the cubes. A fixture that baked none
        // would leave AddEmitSurfaceLights and TestLine untested.
        LeafAmbientResult r = await _fixture.BuildAsync(_fixture.Ldr, LeafAmbientOptions.StockParity);

        Assert.Equal((32, 32), (r.LightsInAmbientCube, r.SurfaceLights));
    }

    [Fact]
    public void TheFixtureHasADisplacementSoTheClipIsExercised()
    {
        Assert.Equal(1, _fixture.Ldr.Tracer.Displacements.Count);
    }

    [Fact]
    public async Task OneAndFourWorkersProduceTheSameBytes()
    {
        LeafAmbientResult serial = await _fixture.BuildAsync(_fixture.Ldr, LeafAmbientOptions.StockParity);
        LeafAmbientResult wide = await _fixture.BuildAsync(
            _fixture.Ldr, LeafAmbientOptions.StockParity with { Parallelism = 4 });

        Assert.Equal(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(serial.Lighting).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(wide.Lighting).ToArray());
    }

    [Fact]
    public async Task WithoutVisibilityTheBakedLightsLeakThroughWalls()
    {
        // The visibility seam is load-bearing: treating every baked light as
        // visible (leaf_ambient_lighting.cpp:111 skipped) must change the lump.
        LeafAmbientResult r = await LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr,
            _fixture.Ldr.WorldLights.ToArray(),
            LeafAmbientOptions.StockParity with { RequireSurfaceLightVisibility = false },
            visibility: null,
            CancellationToken.None);

        Assert.NotEqual(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(_fixture.Lump<DLeafAmbientLighting>(BspLump.LeafAmbientLighting)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting).ToArray());
    }

    [Fact]
    public async Task BakedLightsWithNoVisibilityAreRefusedByDefault()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr,
            _fixture.Ldr.WorldLights.ToArray(),
            LeafAmbientOptions.StockParity,
            visibility: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task ThePassWritesTheAmbientCubeFlagIntoTheLights()
    {
        // leaf_ambient_lighting.cpp:630-633 SETS and CLEARS the flag: start
        // from lights with it cleared and the pass must set it back.
        DWorldLight[] lights = _fixture.Ldr.WorldLights.ToArray();
        for (int i = 0; i < lights.Length; i++)
        {
            lights[i].Flags &= ~1;
        }

        _ = await LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr, lights, LeafAmbientOptions.StockParity, _fixture.Visibility, CancellationToken.None);

        Assert.Equal(32, lights.Count(l => (l.Flags & 1) != 0));
    }

    [Fact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LeafAmbientBuilder.BuildAsync(
            _fixture.Ldr, _fixture.Ldr.WorldLights.ToArray(), LeafAmbientOptions.StockParity,
            _fixture.Visibility, cts.Token));
    }

    [Fact]
    public async Task TheSampleCountQuirkMovesTheLump()
    {
        // Mutation proof on the unit tier: the byte gate above can fail.
        LeafAmbientResult r = await _fixture.BuildAsync(
            _fixture.Ldr,
            LeafAmbientOptions.StockParity with
            {
                Compliance = ComplianceOptions.Stock.Flipping(StockQuirk.LeafAmbientSampleCountAxes),
            });

        Assert.NotEqual(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(_fixture.Lump<DLeafAmbientLighting>(BspLump.LeafAmbientLighting)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting).ToArray());
    }

    [Fact]
    public async Task TheTieBreakQuirkMovesTheLump()
    {
        LeafAmbientResult r = await _fixture.BuildAsync(
            _fixture.Ldr,
            LeafAmbientOptions.StockParity with
            {
                Compliance = ComplianceOptions.Stock.Flipping(StockQuirk.AmbientSampleTieBreakNeverFires),
            });

        Assert.NotEqual(
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(_fixture.Lump<DLeafAmbientLighting>(BspLump.LeafAmbientLighting)).ToArray(),
            MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting).ToArray());
    }

    [Fact]
    public async Task EveryEmptyLeafPointsAtALitLeafOrItself()
    {
        // leaf_ambient_lighting.cpp:693-707: a zero count makes firstAmbientSample a LEAF.
        LeafAmbientResult r = await _fixture.BuildAsync(_fixture.Ldr, LeafAmbientOptions.StockParity);

        for (int leaf = 0; leaf < r.Index.Length; leaf++)
        {
            if (r.Index[leaf].AmbientSampleCount == 0)
            {
                int target = r.Index[leaf].FirstAmbientSample;
                Assert.True(target == leaf || r.Index[target].AmbientSampleCount > 0, $"leaf {leaf} -> {target}");
            }
        }
    }
}
