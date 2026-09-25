using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// Properties of the driver on real maps: determinism across thread counts,
/// and what <c>-both</c> may share between its passes.
/// </summary>
public sealed class VradDriverStockTests
{
    private static readonly VradOptions BounceZero = VradOptions.Default with { Bounces = 0 };

    [VradStockTheory]
    [InlineData("l1_long_corridor")]
    [InlineData("p4c_texlights")]
    [InlineData("ss_sandbox")]
    public async Task OneThreadAndEightThreadsWriteTheSameMap(string name)
    {
        (BspData one, _) = await VradStockReference.CompileAsync("b0", name, BounceZero, threads: 1);
        (BspData eight, _) = await VradStockReference.CompileAsync("b0", name, BounceZero, threads: 8);

        Assert.Equal(await DigestAsync(one), await DigestAsync(eight));
    }

    /// <summary>
    /// Plan 4p's hypothesis for <c>-both</c>: everything before the light
    /// colours is range-independent. The transfers are built from the patch
    /// tree, so the tree must be identical for the LDR and HDR passes before
    /// anything is shared; the transfers themselves are compared once the
    /// bounce (4d) exists.
    /// </summary>
    [VradStockTheory]
    [InlineData("l1_long_corridor")]
    [InlineData("p4c_texlights")]
    [InlineData("ss_sandbox")]
    public async Task BothRangesBuildTheSamePatchTree(string name)
    {
        BspData bsp = await VradStockReference.LoadAsync(VradStockReference.InputFor(name));
        TextureLightTable texLights = new(new SourceSharp.MapFormats.Text.RadLightFile(), name);
        RadWorld ldr = RadWorld.Build(bsp, Settings(false), texLights);
        RadWorld hdr = RadWorld.Build(bsp, Settings(true), texLights);

        Assert.Equal(PatchTreeDigest(ldr), PatchTreeDigest(hdr));
    }

    /// <summary>
    /// The transfer-identity fact plan 4p asks for before <c>-both</c> shares
    /// anything: built independently -- each range with its own settings and
    /// its own casters and KD-tree -- the LDR and HDR passes' transfers are
    /// the same, patch for patch and weight for weight. The driver then
    /// builds them once (<c>RadWorld.ReuseTransfers</c>).
    /// </summary>
    [VradStockTheory]
    [InlineData("l1_long_corridor")]
    [InlineData("p4c_texlights")]
    [InlineData("ss_sandbox")]
    public async Task BothRangesBuildTheSameTransfers(string name)
    {
        BspData bsp = await VradStockReference.LoadAsync(VradStockReference.InputFor(name));
        TextureLightTable texLights = new(new SourceSharp.MapFormats.Text.RadLightFile(), name);

        Assert.Equal(await TransferDigestAsync(bsp, texLights, hdr: false), await TransferDigestAsync(bsp, texLights, hdr: true));
    }

    private static async Task<string> TransferDigestAsync(BspData bsp, TextureLightTable texLights, bool hdr)
    {
        DirectLightingSettings settings = DirectLightingSettings.FromVrad(
            VradOptions.Default with { Compliance = ComplianceOptions.Stock }, hdr);
        RadWorld world = RadWorld.Build(bsp, settings, texLights);
        using WorkQueue queue = new(CompileParallelism.Default);
        TransferSet transfers = await new VisMatrix(world.BounceContext())
            .BuildAsync(StockRadWorld.Tracer(bsp, hdr), queue, CancellationToken.None);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(transfers.Arena));
        for (int p = 0; p < transfers.PatchCount; p++)
        {
            hash.AppendData(BitConverter.GetBytes(transfers.CountFor(p)));
        }

        Assert.True(transfers.Total > 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static DirectLightingSettings Settings(bool hdr) =>
        DirectLightingSettings.FromVrad(BounceZero with { Compliance = ComplianceOptions.Stock }, hdr);

    private static string PatchTreeDigest(RadWorld world)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        PatchSet patches = world.Patches;
        byte[] buffer = new byte[4];

        void Int(int v)
        {
            BitConverter.TryWriteBytes(buffer, v);
            hash.AppendData(buffer);
        }

        void Float(float v)
        {
            BitConverter.TryWriteBytes(buffer, v);
            hash.AppendData(buffer);
        }

        Int(patches.Count);
        for (int i = 0; i < patches.Count; i++)
        {
            ref Patch p = ref patches.At(i);
            Int(p.FaceNumber);
            Int(p.ClusterNumber);
            Int(p.Parent);
            Int(p.Child1);
            Int(p.Child2);
            Int(p.Next);
            Float(p.Area);
            Float(p.Origin.X);
            Float(p.Origin.Y);
            Float(p.Origin.Z);
            Float(p.Normal.X);
            Float(p.Normal.Y);
            Float(p.Normal.Z);
            foreach (SourceSharp.MapFormats.Geometry.Vec3 v in patches.Arena.Points(p.Winding))
            {
                Float(v.X);
                Float(v.Y);
                Float(v.Z);
            }
        }

        foreach (int f in patches.FacePatches)
        {
            Int(f);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> DigestAsync(BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
