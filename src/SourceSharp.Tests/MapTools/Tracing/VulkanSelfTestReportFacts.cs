//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The self-test's full report: the device and driver build, the flags the
/// scene and rays were built with, and every ray's expected and actual
/// answer, carried in <see cref="SelfTestRecord.Detail"/> and at the end of
/// a rejection's reason.
/// </summary>
/// <remarks>
/// The NVIDIA rejection that started this said only which legs failed; the
/// report now says what came back, so the next rejection on a machine
/// nobody here can reach is diagnosable from its one warning line.
/// </remarks>
public sealed class VulkanSelfTestReportFacts
{
    // ------------------------------------------------------------------
    // Device identity
    // ------------------------------------------------------------------

    /// <summary>NVIDIA packs its driver version 10.8.8.6.</summary>
    [Fact]
    public void NvidiaDriverVersionsDecodeTheNvidiaWay()
    {
        uint raw = (610u << 22) | (57u << 14) | (4u << 6);
        Assert.Equal("610.57.4.0", DeviceIdentity.FormatDriverVersion(0x10DE, "NvidiaProprietary", raw));
    }

    /// <summary>Intel's Windows driver packs 18.14.</summary>
    [Fact]
    public void IntelWindowsDriverVersionsDecodeTheIntelWay()
    {
        uint raw = (101u << 14) | 5445u;
        Assert.Equal("101.5445", DeviceIdentity.FormatDriverVersion(0x8086, "IntelProprietaryWindows", raw));
    }

    /// <summary>Everyone else, Mesa included, packs the API version's 10.10.12.</summary>
    [Fact]
    public void OtherDriverVersionsDecodeLikeApiVersions()
    {
        uint raw = (25u << 22) | (2u << 12) | 8u;
        Assert.Equal("25.2.8", DeviceIdentity.FormatDriverVersion(0x1002, "MesaRadv", raw));
        Assert.Equal("25.2.8", DeviceIdentity.FormatDriverVersion(0x8086, "IntelOpenSourceMesa", raw));
        Assert.Equal("1.4.303", DeviceIdentity.FormatApiVersion((1u << 22) | (4u << 12) | 303u));
    }

    /// <summary>The identity line names the device and the driver build, decoded, and nothing machine-specific.</summary>
    [Fact]
    public void TheIdentityLineNamesTheDriverBuild()
    {
        DeviceIdentity id = new(
            "NVIDIA GeForce RTX 2070 SUPER", 0x10DE, 0x1E84, "NvidiaProprietary", "NVIDIA", "610.57.04",
            (610u << 22) | (57u << 14) | (4u << 6), (1u << 22) | (4u << 12) | 303u, "1.4.1.0");

        Assert.Equal(
            "device 'NVIDIA GeForce RTX 2070 SUPER' (vendor 0x10DE, device 0x1E84); driver NvidiaProprietary "
            + "'NVIDIA' '610.57.04', driver version 610.57.4.0; Vulkan 1.4.303; conformance 1.4.1.0",
            id.Describe());
    }

    /// <summary>A device that was never selected describes itself as unknown rather than throwing.</summary>
    [Fact]
    public void AnUnselectedDeviceIsUnknown()
    {
        Assert.StartsWith("device '?'", DeviceIdentity.Unknown.Describe());
    }

    // ------------------------------------------------------------------
    // Per-ray answers
    // ------------------------------------------------------------------

    /// <summary>A ray that hit its own triangle at t = 0.5 matches, and says so in full.</summary>
    [Fact]
    public void ARayOnItsKnownHitMatches()
    {
        SelfTestRayResult r = new(1, true, 1, 0x3F000000u, 0, 0);

        Assert.True(r.ClosestMatches);
        Assert.Equal(0.5f, r.T);
        Assert.Equal(
            "ray 1: any-hit expected hit, got hit; closest-hit expected primitive 1 at t=0.5 (0x3F000000), "
            + "got primitive 1 at t=0.5 (0x3F000000); telemetry 0 proceed iteration(s), 0 candidate(s)",
            r.Describe());
    }

    /// <summary>A miss, the wrong triangle and the wrong distance each fail to match, and say what came back.</summary>
    [Fact]
    public void AWrongAnswerIsDescribedAsWhatCameBack()
    {
        SelfTestRayResult miss = new(0, false, 0xFFFFFFFFu, 0u, 2, 1);
        SelfTestRayResult wrongPrim = new(0, true, 1, 0x3F000000u, 0, 0);
        SelfTestRayResult wrongT = new(0, true, 0, BitConverter.SingleToUInt32Bits(0.75f), 0, 0);

        Assert.False(miss.ClosestMatches);
        Assert.False(wrongPrim.ClosestMatches);
        Assert.False(wrongT.ClosestMatches);
        Assert.Contains("any-hit expected hit, got miss", miss.Describe());
        Assert.Contains("got a miss (t bits 0x00000000)", miss.Describe());
        Assert.Contains("telemetry 2 proceed iteration(s), 1 candidate(s)", miss.Describe());
        Assert.Contains("got primitive 1 at t=0.5", wrongPrim.Describe());
        Assert.Contains("got primitive 0 at t=0.75 (0x3F400000)", wrongT.Describe());
    }

    // ------------------------------------------------------------------
    // The detail and the reason
    // ------------------------------------------------------------------

    /// <summary>
    /// The detail names the device, every flag, the readback words and each
    /// ray; with the readback broken it says no ray was traced.
    /// </summary>
    /// <param name="traced">Whether the rays were traced.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDetailCarriesDeviceFlagsAndRays(bool traced)
    {
        DeviceIdentity id = new("dev", 0x1002, 0x7550, "MesaRadv", "radv", "Mesa 25.2", 1, (1u << 22) | (4u << 12), "1.4.0.0");
        SelfTestOutcome o = new(traced, traced, false, 0, 0)
        {
            Rays = traced ? [new(0, true, 0, 0x3F000000u, 0, 0), new(1, true, 0xFFFFFFFFu, 0, 0, 0)] : [],
            ReadbackWords = traced ? (0xFFFFFFFFu, 0xFFFFFFFFu) : (0u, 0u),
        };

        string detail = VulkanDevice.SelfTestDetail(id, o);

        Assert.StartsWith("device 'dev' (vendor 0x1002", detail);
        Assert.Contains(VulkanDevice.SelfTestConfiguration, detail);
        if (traced)
        {
            Assert.Contains("readback words 0xFFFFFFFF 0xFFFFFFFF (expected 0xFFFFFFFF 0xFFFFFFFF)", detail);
            Assert.Contains("; ray 0: any-hit expected hit, got hit", detail);
            Assert.Contains("; ray 1: any-hit expected hit, got hit; closest-hit expected primitive 1", detail);
            Assert.DoesNotContain("no ray was traced", detail);
        }
        else
        {
            Assert.Contains("readback words 0x00000000 0x00000000", detail);
            Assert.Contains("no ray was traced", detail);
        }
    }

    /// <summary>
    /// The configuration the report states is the one that runs: each
    /// number in it is checked against the constant or kernel text that
    /// sets it.
    /// </summary>
    [Fact]
    public void TheStatedConfigurationMatchesTheCode()
    {
        const string C = VulkanDevice.SelfTestConfiguration;
        string k = Kernels.RayGlsl;

        Assert.Contains("mask 0xFF", C);
        Assert.Equal(0xFFu, VulkanDevice.SceneInstance(1).Mask);
        Assert.Contains("TRIANGLE_FACING_CULL_DISABLE", C);
        Assert.Equal(
            Silk.NET.Vulkan.GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
            VulkanDevice.SceneInstance(1).Flags);
        Assert.Contains($"(0x{VulkanDevice.SelfTestTminBits:X8})", C);
        // The largest float below 1 is 1 - 2^-24.
        Assert.Equal(VulkanDevice.TmaxScaleBits, BitConverter.SingleToUInt32Bits(1f - (1f / 16_777_216f)));
        Assert.Contains($"(0x{VulkanDevice.TmaxScaleBits:X8})", C);
        Assert.Contains("anyMode ? gl_RayFlagsTerminateOnFirstHitEXT : 0u", k);
        Assert.Contains("any-hit ray flags TerminateOnFirstHit", C);
        Assert.Contains("rayQueryInitializeEXT(rq, BLAS, qflags, 0xFFu", k);
        Assert.Contains("rayQueryInitializeEXT(rq5, BLAS, 0u, 0xFFu", k);
        Assert.Contains("cull mask 0xFF", C);
        Assert.Contains("closest-hit and telemetry ray flags None", C);
    }

    /// <summary>A rejection carries the detail after its reason on both branches; a pass has no reason.</summary>
    [Fact]
    public void ARejectionCarriesTheDetail()
    {
        string? broken = VulkanRayTracer.ReasonFor(false, "dev", new SelfTestOutcome(false, false, false, 0, 0), "DETAIL");
        string? wrong = VulkanRayTracer.ReasonFor(false, "dev", new SelfTestOutcome(true, false, true, 0, 0), "DETAIL");
        string? bare = VulkanRayTracer.ReasonFor(false, "dev", new SelfTestOutcome(true, false, true, 0, 0));

        Assert.EndsWith("device can be trusted. Self-test detail: DETAIL", broken);
        Assert.EndsWith("Rejecting the device. Self-test detail: DETAIL", wrong);
        Assert.EndsWith("Rejecting the device", bare);
        Assert.Null(VulkanRayTracer.ReasonFor(true, "dev", new SelfTestOutcome(true, true, true, 0, 0), "DETAIL"));
    }

    // ------------------------------------------------------------------
    // On a device
    // ------------------------------------------------------------------

    /// <summary>
    /// The self-test traces its two real rays and reports both, with the
    /// device's identity filled in from the driver.
    /// </summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void TheSelfTestReportsBothRaysAndTheDriver()
    {
        using VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1);

        (bool passed, SelfTestOutcome outcome) = device.RunSelfTest();

        Assert.True(passed, VulkanDevice.SelfTestDetail(device.Identity, outcome));
        Assert.Equal(VulkanDevice.SelfTestRealRays, outcome.Rays.Count);
        Assert.All(outcome.Rays, r => Assert.True(r.AnyHit && r.ClosestMatches, r.Describe()));
        Assert.Equal((0xFFFFFFFFu, 0xFFFFFFFFu), outcome.ReadbackWords);
        Assert.Equal(device.DeviceName, device.Identity.DeviceName);
        Assert.NotEqual(0u, device.Identity.VendorId);
        Assert.True(device.Identity.ApiVersion >= VulkanDevice.MinimumApiVersion);
    }

    /// <summary>A passing attempt keeps the full detail on its record, with no reason.</summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void APassingAttemptKeepsTheDetail()
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(VulkanRayTracerReleaseFacts.TwoTriangles());
        using VulkanRayTracer? tracer = a.Tracer;
        SelfTestRecord rec = a.Report.Selected!.Value;

        Assert.True(a.Success, rec.Reason);
        Assert.Null(rec.Reason);
        Assert.NotNull(rec.Detail);
        Assert.StartsWith($"device '{rec.DeviceName}'", rec.Detail);
        Assert.Contains("ray 0: any-hit expected hit, got hit; closest-hit expected primitive 0 at t=0.5 "
            + "(0x3F000000), got primitive 0 at t=0.5 (0x3F000000)", rec.Detail);
        Assert.Contains("ray 1: any-hit expected hit, got hit; closest-hit expected primitive 1 at t=0.5 "
            + "(0x3F000000), got primitive 1 at t=0.5 (0x3F000000)", rec.Detail);
    }
}
