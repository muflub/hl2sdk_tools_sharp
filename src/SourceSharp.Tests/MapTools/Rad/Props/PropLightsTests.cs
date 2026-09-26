//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The two ways to get <c>activelights</c> agree: the lights a compile builds
/// (<see cref="PropLights.FromDirectLights"/> over p4c's
/// <see cref="RadWorld"/>) and the lights reconstructed from the lump stock
/// exported (<see cref="PropLights.FromWorldLights"/>), which is what every
/// prop gate against a stock-lit map runs on.
/// </summary>
public sealed class PropLightsTests
{
    private static async Task<(IReadOnlyList<PropLight> Built, IReadOnlyList<PropLight> Reconstructed)> BothAsync(bool hdr)
    {
        BspData bsp = await StaticPropFixture.LoadAsync();
        string rad = Path.Combine(AmbientFixture.Directory(), "p4g_props.rad");
        RadLightFile file = await RadLightFile.ParseAsync(
            await File.ReadAllTextAsync(rad), new RadLightOptions(Hdr: hdr, SourceFile: rad));
        RadWorld world = RadWorld.Build(
            bsp,
            DirectLightingSettings.FromVrad(VradOptions.Default with { Compliance = ComplianceOptions.Stock }, hdr),
            new TextureLightTable(file, "p4g_props"));

        IReadOnlyList<PropLight> built = PropLights.FromDirectLights(world.Lights.Active);
        IReadOnlyList<PropLight> reconstructed = PropLights.FromWorldLights(bsp, hdr ? LightingMode.Hdr : LightingMode.Ldr, out _);
        Assert.Equal(37, built.Count);
        return (built, reconstructed);
    }

    private static object Key(PropLight l) => (
        l.Type, l.Style, l.Origin, l.Intensity, l.Normal, l.StopDot, l.StopDot2, l.Exponent,
        l.ConstantAttn, l.LinearAttn, l.QuadraticAttn);

    [Fact]
    public async Task TheBuiltAndReconstructedLdrListsMatch()
    {
        (IReadOnlyList<PropLight> built, IReadOnlyList<PropLight> reconstructed) = await BothAsync(hdr: false);
        Assert.Equal(reconstructed.Select(Key), built.Select(Key));
    }

    [Fact]
    public async Task TheBuiltAndReconstructedHdrListsMatch()
    {
        (IReadOnlyList<PropLight> built, IReadOnlyList<PropLight> reconstructed) = await BothAsync(hdr: true);
        Assert.Equal(reconstructed.Select(Key), built.Select(Key));
    }

    [Fact]
    public async Task TheBuiltAndReconstructedVisibilityMatches()
    {
        (IReadOnlyList<PropLight> built, IReadOnlyList<PropLight> reconstructed) = await BothAsync(hdr: false);
        Assert.Equal(reconstructed.Select(l => l.Pvs), built.Select(l => l.Pvs));
    }
}
