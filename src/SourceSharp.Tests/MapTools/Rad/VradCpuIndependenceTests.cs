//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Ambient;
using SourceSharp.Tests.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// vrad under the default policy computes the same bits on every CPU: the
/// guard for <see cref="StockQuirk.KdTracerReciprocalEstimate"/>,
/// <see cref="StockQuirk.SkyWindingNormalise"/> and every other estimate
/// that <see cref="CompliancePolicy.Correct"/> replaces in vrad.
/// </summary>
/// <remarks>
/// <para>
/// <b>These digests are deliberately NOT per-vendor</b>, as in
/// <c>VbspCpuIndependenceTests</c>. The Stock-side goldens read per-CPU
/// deltas (<c>VendorGolden</c>) because the estimate instructions are the
/// CPU's; nothing the Correct policy computes is. CI runs each digest on AMD
/// (Linux, Windows), Intel (macOS x64) and arm64 (macOS arm64), and a digest
/// that holds on some of those and not others is a vrad path still taking an
/// estimate under Correct, not a flaky test to split per vendor.
/// </para>
/// <para>
/// Each digest pins everything its stage produces, so an intended change to
/// that output moves it too. Recapture it then from the failure message, on
/// any one runner, and check the others agree.
/// </para>
/// <para>
/// Three levels, so a failure says where to look: the KD tracer on its own
/// (both committed scenes, every ray's hit and distance and every triangle's
/// plane), leaf ambient on the committed fixture (the stage that walks sky
/// faces, through <see cref="AmbientRayTracer"/>, and traces its
/// <c>TestLine</c>s through the KD tracer), and the whole chain on the
/// sandbox map.
/// </para>
/// <para>
/// Checked when they were pinned, on an AMD host, by building with
/// <see cref="FloatEstimate"/> patched to return the exact reciprocal and
/// reciprocal square root scaled by 1 +- 2^-10, far outside any CPU's
/// estimate: every digest here, the static-prop digests and vbsp's still came
/// out the same, while the Stock-side KD parity facts failed, as they must
/// when the estimate moves. The same digests held again with hardware
/// intrinsics switched off (<c>DOTNET_EnableHWIntrinsic=0</c>), which takes
/// the portable <see cref="System.Runtime.Intrinsics.Vector128"/> paths
/// arm64 shares with no x86 instruction behind them. A digest here does not
/// depend on what the estimate returns.
/// </para>
/// </remarks>
public sealed class VradCpuIndependenceTests : IClassFixture<AmbientFixture>
{
    /// <summary>
    /// The SHA-256 of each committed KD scene traced under
    /// <see cref="ComplianceOptions.Correct"/>: the triangles' intersection
    /// format, then every ray's closest hit.
    /// </summary>
    public static TheoryData<string, string> KdSceneDigests => new()
    {
        { "kd-scene", "45088531120712BDD4436079A4E0ED5605F56BB3847084D33B8A01E36F8334E9" },
        { "kd-boxes", "43A7BF66182DD274ADE62C49DE3D725EF907F10ABD584E7F02E069DF952F891E" },
    };

    /// <summary>
    /// The SHA-256 of the committed fixture's LDR leaf-ambient lumps (index,
    /// then lighting) under <see cref="ComplianceOptions.Correct"/>.
    /// </summary>
    private const string LeafAmbientDigest = "BCF63933234C205FC0978F1773DED9A73B9C119D81EE9A91E8FCCBCDC5EC29B0";

    /// <summary>
    /// The SHA-256 of ss_sandbox's BSP compiled by the whole chain under
    /// <see cref="ComplianceOptions.Correct"/> against the synthetic content,
    /// with no bounces.
    /// </summary>
    /// <remarks>
    /// No bounces because direct light is where the KD tracer's shadow rays
    /// are, and a bounce costs half a minute more of CI for a transfer pass
    /// whose own estimate is <see cref="StockQuirk.TransferRayReciprocalEstimate"/>
    /// and whose rays go through the same traversal. Measured when it was
    /// pinned, on AMD: flipping only <see cref="StockQuirk.KdTracerReciprocalEstimate"/>
    /// to the estimate moves the Lighting and LeafAmbientLighting lumps and so
    /// this digest; flipping only <see cref="StockQuirk.SkyWindingNormalise"/>
    /// moves nothing on this map, whose sky windings have no point near the
    /// estimate's margins on that CPU.
    /// </remarks>
    private const string SandboxDigest = "F0E93B577F8FBF1A2C2F962DE81315EA3A1D429B9903B1391C86EBB1782C8EAB";

    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The leaf-ambient map.</param>
    public VradCpuIndependenceTests(AmbientFixture fixture) => _fixture = fixture;

    /// <summary>The KD tracer's Correct answers are the same bits on every CPU.</summary>
    /// <param name="scene">Which committed scene.</param>
    /// <param name="digest">Its pinned digest.</param>
    [Theory]
    [MemberData(nameof(KdSceneDigests))]
    public void TheCorrectKdSceneDistancesArePinnedOnEveryCpu(string scene, string digest)
    {
        StockKdScene loaded = StockKdScene.Load(scene);
        KdRayTracer tracer = KdRayTracer.Build(loaded.Triangles, ComplianceOptions.Correct);
        HitId[] hits = new HitId[loaded.RayCount];
        tracer.TraceClosest(loaded.Rays, hits, RayTraceOptions.StockExact);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < tracer.TriangleCount; i++)
        {
            (SourceSharp.MapFormats.Geometry.Vec3 n, float d, int id, float[] edges, int cs0, int cs1) = tracer.Triangle(i);
            Append(hash, n.X, n.Y, n.Z, d);
            Append(hash, edges);
            hash.AppendData(BitConverter.GetBytes(id));
            hash.AppendData([(byte)cs0, (byte)cs1]);
        }

        foreach (HitId hit in hits)
        {
            hash.AppendData(BitConverter.GetBytes(hit.Surface));
            Append(hash, hit.Fraction);
        }

        AssertDigest(scene, digest, Convert.ToHexString(hash.GetHashAndReset()), []);
    }

    /// <summary>
    /// Leaf ambient's Correct lumps are the same bits on every CPU. The
    /// fixture has a sky half-ceiling, so the walk's sky test is reached, and
    /// 32 texlights baked into the cubes, so the KD tracer's <c>TestLine</c> is.
    /// </summary>
    [Fact]
    public async Task TheCorrectLeafAmbientIsPinnedOnEveryCpu()
    {
        LeafAmbientResult r = await LeafAmbientBuilder.BuildAsync(
            _fixture.CorrectLdr,
            _fixture.CorrectLdr.WorldLights.ToArray(),
            new LeafAmbientOptions { Compliance = ComplianceOptions.Correct, Parallelism = 2 },
            _fixture.CorrectVisibility,
            CancellationToken.None);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes<DLeafAmbientIndex>(r.Index));
        hash.AppendData(MemoryMarshal.AsBytes<DLeafAmbientLighting>(r.Lighting));

        AssertDigest("leaf ambient", LeafAmbientDigest, Convert.ToHexString(hash.GetHashAndReset()), []);
    }

    /// <summary>
    /// The sandbox, compiled vbsp to vrad under the default policy, is the
    /// same bytes on every CPU.
    /// </summary>
    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheCorrectSandboxIsPinnedOnEveryCpu()
    {
        BspData bsp = await CompileSandboxAsync(ComplianceOptions.Correct);

        using MemoryStream stream = new();
        await BspFile.SaveAsync(bsp, stream, BspWriteMode.Canonical, CancellationToken.None);
        string actual = Convert.ToHexString(SHA256.HashData(stream.ToArray()));

        List<string> lumps = [];
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            ReadOnlySpan<byte> data = bsp[lump].Data.Span;
            if (data.Length > 0)
            {
                lumps.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {lump}: {data.Length} bytes {Convert.ToHexString(SHA256.HashData(data))}"));
            }
        }

        AssertDigest("ss_sandbox", SandboxDigest, actual, lumps);
    }

    /// <summary>ss_sandbox through vbsp, vvis and vrad, in memory, under one compliance for all three.</summary>
    internal static async Task<BspData> CompileSandboxAsync(ComplianceOptions compliance)
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in SyntheticContent.Build())
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile("maps/ss_sandbox.vmf", vmf);
        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        CompileResult result = await MapCompiler.CompileAsync(
            new CompileRequest
            {
                Source = MapSource.FromVmf(disk, VPath.Create("maps/ss_sandbox.vmf")),
                Content = content,
                Vbsp = VbspOptions.Default with { Compliance = compliance },
                Vrad = VradOptions.Default with { Compliance = compliance, Bounces = 0 },
                Parallel = new CompileParallelism { MaxDegree = 2 },
                Output = CompileOutput.InMemory,
            },
            null);
        Assert.NotNull(result.Rad);
        return result.Bsp!;
    }

    private static void Append(IncrementalHash hash, params float[] values)
    {
        foreach (float v in values)
        {
            hash.AppendData(BitConverter.GetBytes(BitConverter.SingleToInt32Bits(v)));
        }
    }

    /// <summary>
    /// Compares a digest with the pinned one; on a mismatch the message
    /// carries the actual digest and any per-part digests, which is what a
    /// person comparing two CI runners needs.
    /// </summary>
    private static void AssertDigest(string name, string expected, string actual, IReadOnlyList<string> parts)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"{name}: Correct digest {actual}, pinned {expected}\n");
        foreach (string part in parts)
        {
            message.Append(part).Append('\n');
        }

        Assert.Fail(message.ToString());
    }
}
