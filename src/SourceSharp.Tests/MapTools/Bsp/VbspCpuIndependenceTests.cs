//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Bsp.Faces;
using SourceSharp.Tests.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// vbsp under the default policy writes the same bytes on every CPU: the
/// guard for <see cref="StockQuirk.VbspVectorNormalise"/> and every other
/// estimate that <see cref="CompliancePolicy.Correct"/> replaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>These digests are deliberately NOT per-vendor.</b> The facts that pin
/// Stock-side values read per-CPU deltas (<c>VendorGolden</c>) because
/// <c>rsqrtss</c> is the CPU's; the point of the Correct policy is that
/// nothing it computes is. So each digest here is one string, and CI runs
/// it on AMD (Linux, Windows), Intel (macOS x64) and arm64 (macOS arm64). A
/// digest that holds on some of those runners and not others is not a flaky
/// test to be split per vendor: it is a vbsp path still taking an estimate
/// under Correct, and the failure message lists every lump's digest so the
/// runners' logs say which lump moved.
/// </para>
/// <para>
/// A digest pins everything vbsp writes, so any intended change to vbsp's
/// output moves it too. Recapture it then, from the failure message, on any
/// one runner, and check the others agree.
/// </para>
/// <para>
/// The maps were chosen to reach the sites that used to take the estimate
/// under Correct: <c>ss_sandbox</c> is subdivided, merged and t-junction
/// fixed (<see cref="TheSandboxReachesTheFaceStagesNormalises"/> checks that
/// it still is), and the displacement catalogue's slope, clamped lightmap and
/// swapped texture put displacement surfaces, non-axial faces and a
/// lightmap-axis swap into the BSP.
/// </para>
/// <para>
/// Measured when they were pinned, on an Intel Xeon: every one of these
/// digests moves when only <see cref="StockQuirk.VbspVectorNormalise"/> is
/// flipped to the estimate, so each would have caught the AMD-versus-Intel
/// difference that quirk used to cause under Correct.
/// </para>
/// </remarks>
public sealed class VbspCpuIndependenceTests
{
    /// <summary>
    /// The SHA-256 of ss_sandbox's BSP compiled by vbsp under
    /// <see cref="ComplianceOptions.Correct"/> against the synthetic content.
    /// </summary>
    private const string SandboxDigest = "CFC0D12F397088D105C22635DFFD29C32B9CFAB16D3E5EF05E65012CC9D2F01B";

    /// <summary>
    /// The SHA-256 of each displacement catalogue map's BSP under
    /// <see cref="ComplianceOptions.Correct"/>, with no game content mounted.
    /// </summary>
    public static TheoryData<string, string> DisplacementDigests => new()
    {
        { "p3f_slope", "812A81A7FD17314005DF80FA2B77DE4E6A2D8747815C638592DC78E8DE2679CB" },
        { "p3f_lm8_clamp", "9CA28110EB4DF1CFAF65502A9B32E40F4FF6DACD48FF46FD830647F13EDCFA87" },
        { "p3f_swap", "F83366BD6B8A7A8BDBC57C331BC63984C2CA2129651C6D019A32CECCA6C6FF45" },
        { "p3f_rotated", "FA0F7C00AAB4D3090338E072BB00A356FEA69F2D07B837156FA7D678EA09C086" },
    };

    /// <summary>The sandbox's Correct BSP is the same bytes on every CPU.</summary>
    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheCorrectSandboxBspIsPinnedOnEveryCpu()
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);

        BspData bsp = await CompileAsync("ss_sandbox", vmf, SyntheticContent.Build());

        await AssertDigestAsync("ss_sandbox", SandboxDigest, bsp);
    }

    /// <summary>
    /// Each displacement catalogue map's Correct BSP is the same bytes on
    /// every CPU.
    /// </summary>
    [Theory]
    [MemberData(nameof(DisplacementDigests))]
    public async Task TheCorrectDisplacementBspIsPinnedOnEveryCpu(string name, string digest)
    {
        string vmf = DispCatalogueMaps.All.Single(e => e.Name == name).Vmf;

        BspData bsp = await CompileAsync(name, Encoding.ASCII.GetBytes(vmf), new Dictionary<string, byte[]>());

        await AssertDigestAsync(name, digest, bsp);
    }

    /// <summary>
    /// The sandbox still subdivides, merges and fixes t-junctions, so the
    /// digest above covers the three face-stage normalises. A regenerated
    /// sandbox that stopped doing any of them would leave the guard pinning
    /// nothing, and this says so.
    /// </summary>
    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheSandboxReachesTheFaceStagesNormalises()
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);
        InMemoryFileSystem disk = Disk("ss_sandbox", vmf, SyntheticContent.Build());
        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        VbspContext context = new(VbspOptions.Default, content) { MapBase = "ss_sandbox" };
        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create("maps/ss_sandbox.vmf"));
        WorldFacePass pass = await WorldFacePass.RunAsync(context, map, ComplianceOptions.Correct);

        Assert.True(pass.Counters.Subdivided > 0, "the sandbox subdivides nothing");
        Assert.True(pass.Counters.Merged > 0, "the sandbox merges nothing");
        Assert.True(pass.Counters.TJunctions > 0, "the sandbox has no t-junctions");
    }

    private static InMemoryFileSystem Disk(
        string name, byte[] vmf, IReadOnlyDictionary<string, byte[]> files)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in files)
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile($"maps/{name}.vmf", vmf);
        return disk;
    }

    private static async Task<BspData> CompileAsync(
        string name, byte[] vmf, IReadOnlyDictionary<string, byte[]> files)
    {
        InMemoryFileSystem disk = Disk(name, vmf, files);
        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        VbspContext context = new(VbspOptions.Default, content) { MapBase = name };
        Assert.Equal(CompliancePolicy.Correct, context.Options.Compliance.Policy);
        Assert.Empty(context.Options.Compliance.Except);

        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create($"maps/{name}.vmf"));
        VbspResult result = await Vbsp.CompileAsync(map, context);
        return result.Bsp!;
    }

    /// <summary>
    /// Compares the canonical BSP's digest with the pinned one. On a mismatch
    /// the message carries the whole file's digest and every lump's, which is
    /// what a person comparing two CI runners needs.
    /// </summary>
    private static async Task AssertDigestAsync(string name, string expected, BspData bsp)
    {
        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        string actual = Convert.ToHexString(SHA256.HashData(stream.ToArray()));

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"{name}: Correct BSP digest {actual}, pinned {expected}\n");
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            ReadOnlySpan<byte> data = bsp[lump].Data.Span;
            if (data.Length > 0)
            {
                message.Append(CultureInfo.InvariantCulture,
                    $"  {lump}: {data.Length} bytes {Convert.ToHexString(SHA256.HashData(data))}\n");
            }
        }

        Assert.Fail(message.ToString());
    }
}
