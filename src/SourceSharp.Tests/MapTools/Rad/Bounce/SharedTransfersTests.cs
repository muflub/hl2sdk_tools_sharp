//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// Plan 4p's <c>-both</c> sharing: the transfers are geometry, so the HDR pass
/// may use the LDR pass's instead of tracing them again -- once it has been
/// shown that the two ranges build the same ones, and only when the trees match.
/// </summary>
public sealed class SharedTransfersTests
{
    private static async Task<RadWorld> BouncedAsync(bool hdr, SharedTransfers? reuse = null, float chop = 4.0f)
    {
        LightTestMap map = BounceBox.Map();
        RadWorld world = await BounceBox.LitAsync(map, LightBox.Settings(hdr: hdr) with { MinChop = chop, MaxChop = chop });
        world.ReuseTransfers = reuse;
        await world.BounceAsync(map.Tracer(), BounceBox.One, CancellationToken.None);
        return world;
    }

    private static byte[] Transfers(RadWorld world)
    {
        TransferSet t = world.Transfers!;
        List<byte> bytes = [.. MemoryMarshal.AsBytes(t.Arena)];
        for (int p = 0; p < t.PatchCount; p++)
        {
            bytes.AddRange(BitConverter.GetBytes(t.CountFor(p)));
        }

        return [.. bytes];
    }

    private static byte[] BouncedLight(RadWorld world)
    {
        List<byte> bytes = [];
        for (int p = 0; p < world.Patches.Count; p++)
        {
            BumpLights total = world.Patches.At(p).TotalLight;
            bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<BumpLights>(in total)).ToArray());
        }

        return [.. bytes];
    }

    [Fact]
    public async Task TheLdrAndHdrPassesBuildTheSameTransfers()
    {
        RadWorld ldr = await BouncedAsync(hdr: false);
        RadWorld hdr = await BouncedAsync(hdr: true);
        Assert.Equal(ldr.PatchTreeDigest(), hdr.PatchTreeDigest());
        Assert.Equal(Transfers(ldr), Transfers(hdr));
    }

    [Fact]
    public async Task TheHdrPassUsesTheLdrPassesTransfers()
    {
        RadWorld ldr = await BouncedAsync(hdr: false);
        RadWorld hdr = await BouncedAsync(hdr: true, ldr.ShareTransfers());
        Assert.True(hdr.TransfersWereShared);
        Assert.Same(ldr.Transfers, hdr.Transfers);
    }

    [Fact]
    public async Task SharedTransfersBounceTheSameLightAsItsOwn()
    {
        RadWorld ldr = await BouncedAsync(hdr: false);
        RadWorld shared = await BouncedAsync(hdr: true, ldr.ShareTransfers());
        RadWorld own = await BouncedAsync(hdr: true);
        Assert.False(own.TransfersWereShared);
        Assert.Equal(BouncedLight(own), BouncedLight(shared));
        Assert.Equal(own.BounceEnergies, shared.BounceEnergies);
    }

    [Fact]
    public async Task ADifferentPatchTreeBuildsItsOwnTransfers()
    {
        // A different chop makes a different tree: the digest refuses the share.
        RadWorld ldr = await BouncedAsync(hdr: false);
        RadWorld other = await BouncedAsync(hdr: true, ldr.ShareTransfers(), chop: 16.0f);
        Assert.NotEqual(ldr.PatchTreeDigest(), other.PatchTreeDigest());
        Assert.False(other.TransfersWereShared);
        Assert.Equal(other.Patches.Count, other.Transfers!.PatchCount);
    }

    [Fact]
    public async Task ThePatchTreeDigestSeesAMovedPatch()
    {
        RadWorld world = await BouncedAsync(hdr: false);
        ulong before = world.PatchTreeDigest();
        world.Patches.At(0).Origin += new SourceSharp.MapFormats.Geometry.Vec3(0, 0, 1);
        Assert.NotEqual(before, world.PatchTreeDigest());
    }
}
