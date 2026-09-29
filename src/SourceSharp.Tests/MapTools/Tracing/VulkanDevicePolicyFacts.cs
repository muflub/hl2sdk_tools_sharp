//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Silk.NET.Vulkan;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// "Use the faster one by default": which device an attempt opens, and
/// when an unpinned attempt keeps the CPU tracer instead of a device that
/// works but would be slower (a CPU implementation of Vulkan, or a staged
/// ray upload below the floor). A pinned device is always used.
/// </summary>
/// <remarks>
/// The rules are pure functions and are tested with made-up devices and
/// measurements, so they run everywhere. The device facts need a ray-query
/// device; in a container that is llvmpipe, which is exactly the CPU case.
/// </remarks>
public sealed class VulkanDevicePolicyFacts(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly VulkanRayTracerOptions Auto = new() { DeclineSlowDevicesUnlessPinned = true };

    // ------------------------------------------------------------------
    // Device choice
    // ------------------------------------------------------------------

    /// <summary>The ranking: discrete, integrated, virtual, CPU, other.</summary>
    [Fact]
    public void DeviceTypesRankFromDiscreteToOther()
    {
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.DiscreteGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.IntegratedGpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.IntegratedGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.VirtualGpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.VirtualGpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.Cpu));
        Assert.True(VulkanDevice.DeviceScore(PhysicalDeviceType.Cpu) > VulkanDevice.DeviceScore(PhysicalDeviceType.Other));
    }

    /// <summary>With a real GPU present, an unpinned choice takes it over llvmpipe, whatever the order.</summary>
    [Fact]
    public void AGpuWinsOverACpuImplementationWhenUnpinned()
    {
        DeviceCandidate[] withDiscrete =
        [
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
            new("AMD Radeon RX 9070 XT (RADV GFX1201)", PhysicalDeviceType.DiscreteGpu, true),
            new("NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu, true),
        ];
        DeviceCandidate[] withIntegrated =
        [
            new("AMD Radeon Graphics (RADV RAPHAEL_MENDOCINO)", PhysicalDeviceType.IntegratedGpu, true),
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
        ];

        Assert.Equal(1, VulkanDevice.ChooseDevice(withDiscrete, null, -1));
        Assert.Equal(0, VulkanDevice.ChooseDevice(withIntegrated, null, -1));
    }

    /// <summary>A name pin reaches the device it names, a CPU one included, case-insensitively.</summary>
    [Fact]
    public void ANamePinReachesTheDeviceItNames()
    {
        DeviceCandidate[] devices =
        [
            new("llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu, true),
            new("AMD Radeon RX 9070 XT (RADV GFX1201)", PhysicalDeviceType.DiscreteGpu, true),
            new("NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu, true),
        ];

        Assert.Equal(0, VulkanDevice.ChooseDevice(devices, "LLVMPIPE", -1));
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, "nvidia", -1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, "intel", -1));
    }

    /// <summary>
    /// Devices without ray query or acceleration structures are never
    /// chosen, and an index pin skips that many traceable devices and ranks
    /// the rest.
    /// </summary>
    [Fact]
    public void OnlyTraceableDevicesAreChosenAndAnIndexSkipsThatMany()
    {
        DeviceCandidate[] devices =
        [
            new("old GPU", PhysicalDeviceType.DiscreteGpu, false),
            new("llvmpipe", PhysicalDeviceType.Cpu, true),
            new("new GPU", PhysicalDeviceType.DiscreteGpu, true),
        ];

        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, -1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, "old", -1));
        // An index skips that many traceable devices and ranks the rest, so
        // index 0 (a defaulted options value) still means the best device.
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, 0));
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, null, 1));
        Assert.Equal(-1, VulkanDevice.ChooseDevice(devices, null, 2));
        DeviceCandidate[] cpuLast = [devices[2], devices[1]];
        Assert.Equal(1, VulkanDevice.ChooseDevice(cpuLast, null, 1));
        // The index wins over a name.
        Assert.Equal(2, VulkanDevice.ChooseDevice(devices, "llvmpipe", 1));
    }

    // ------------------------------------------------------------------
    // The decline rule
    // ------------------------------------------------------------------

    /// <summary>Without the policy, every device that passes is used, however slow.</summary>
    [Fact]
    public void WithoutThePolicyNothingIsDeclined()
    {
        Assert.Null(VulkanRayTracer.SlowDeviceReason(default, "llvmpipe", true, 1e6));
    }

    /// <summary>Unpinned, a CPU implementation is declined with a reason that says how to use it anyway.</summary>
    [Fact]
    public void AnUnpinnedCpuDeviceIsDeclined()
    {
        Assert.Equal(
            "llvmpipe (LLVM 20.1.2, 256 bits) is a CPU implementation of Vulkan; the built-in CPU tracer is "
            + "faster, so it is used instead. Pin the device by name to trace on it anyway",
            VulkanRayTracer.SlowDeviceReason(Auto, "llvmpipe (LLVM 20.1.2, 256 bits)", true, null));
    }

    /// <summary>Unpinned, an upload below the floor is declined with the measured rate in the reason.</summary>
    [Fact]
    public void AnUnpinnedSlowUploadIsDeclined()
    {
        Assert.Equal(
            "NVIDIA GeForce RTX 2070 SUPER: rays upload at 0.67 GB/s, below the 2.50 GB/s at which tracing on "
            + "it could beat the CPU; the built-in CPU tracer is faster; pin the device by name to trace on it anyway",
            VulkanRayTracer.SlowDeviceReason(Auto, "NVIDIA GeForce RTX 2070 SUPER", false, 0.67e9));
    }

    /// <summary>At or above the floor, or with no upload to measure, the device is used.</summary>
    /// <param name="rate">The measured rate, or null for a device that reads rays in place.</param>
    [Theory]
    [InlineData(2.5e9)]
    [InlineData(12.1e9)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(null)]
    public void AFastOrDirectDeviceIsUsed(double? rate)
    {
        Assert.Null(VulkanRayTracer.SlowDeviceReason(Auto, "AMD Radeon RX 9070 XT", false, rate));
    }

    /// <summary>A pinned device is used, CPU or slow, whether pinned by name or by index.</summary>
    [Fact]
    public void APinnedDeviceIsAlwaysUsed()
    {
        VulkanRayTracerOptions byName = Auto with { DeviceMatch = "llvmpipe" };
        VulkanRayTracerOptions byIndex = Auto with { DeviceIndex = 1 };

        Assert.True(byName.IsPinned);
        Assert.True(byIndex.IsPinned);
        Assert.False(Auto.IsPinned);
        Assert.False((Auto with { DeviceIndex = -1 }).IsPinned);
        Assert.False((Auto with { DeviceMatch = "" }).IsPinned);
        Assert.Null(VulkanRayTracer.SlowDeviceReason(byName, "llvmpipe", true, 0.1e9));
        Assert.Null(VulkanRayTracer.SlowDeviceReason(byIndex, "NVIDIA", false, 0.1e9));
    }

    /// <summary>The floor defaults to 2.5 GB/s and can be moved.</summary>
    [Fact]
    public void TheFloorDefaultsAndCanBeMoved()
    {
        VulkanRayTracerOptions lower = Auto with { MinUploadBytesPerSecond = 0.5e9 };

        Assert.Equal(2.5e9, Auto.UploadFloor);
        Assert.Equal(0.5e9, lower.UploadFloor);
        Assert.Null(VulkanRayTracer.SlowDeviceReason(lower, "NVIDIA", false, 0.67e9));
        Assert.Contains("below the 0.50 GB/s", VulkanRayTracer.SlowDeviceReason(lower, "NVIDIA", false, 0.4e9));
    }

    /// <summary>The rate is bytes over seconds; a copy too fast for the clock counts as infinitely fast.</summary>
    [Fact]
    public void UploadRateIsBytesOverSeconds()
    {
        Assert.Equal(0.5e9, VulkanDevice.UploadRate(64UL << 20, (64 << 20) / 0.5e9), 1.0);
        Assert.Equal(double.PositiveInfinity, VulkanDevice.UploadRate(1, 0));
    }

    // ------------------------------------------------------------------
    // The unpinned walk
    // ------------------------------------------------------------------

    private const ulong GiB = 1UL << 30;

    private static VulkanDeviceInfo Row(
        int index, string name, PhysicalDeviceType type, bool rayQuery = true, ulong vram = 0, uint cores = 0) =>
        new(index, name, type.ToString(), rayQuery) { DeviceLocalBytes = vram, ShaderCores = cores };

    /// <summary>Within a type, the device with more device-local memory is tried first.</summary>
    [Fact]
    public void MoreVramWinsWithinAType()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB, cores: 40),
            Row(1, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, vram: 16 * GiB, cores: 64),
        ];

        Assert.Equal([1, 0], VulkanRayTracer.RankedDevices(rows));
    }

    /// <summary>With equal memory, more shader cores go first; with both equal, loader order decides.</summary>
    [Fact]
    public void ShaderCoresBreakVramTies()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "fewer cores", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB, cores: 36),
            Row(1, "more cores", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB, cores: 60),
            Row(2, "same again, later", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB, cores: 60),
            Row(3, "unknown cores", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB),
        ];

        Assert.Equal([1, 2, 0, 3], VulkanRayTracer.RankedDevices(rows));
    }

    /// <summary>A discrete device beats an integrated one whatever their memory, and the CPU type goes last.</summary>
    [Fact]
    public void DiscreteBeatsIntegratedRegardlessOfVram()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "llvmpipe", PhysicalDeviceType.Cpu, vram: 64 * GiB),
            Row(1, "iGPU with a big shared pool", PhysicalDeviceType.IntegratedGpu, vram: 32 * GiB, cores: 16),
            Row(2, "small discrete", PhysicalDeviceType.DiscreteGpu, vram: 4 * GiB, cores: 8),
        ];

        Assert.Equal([2, 1, 0], VulkanRayTracer.RankedDevices(rows));
    }

    /// <summary>The walk moves past a bigger card that is declined to the smaller one that passes.</summary>
    [Fact]
    public void TheWalkContinuesPastADeclinedBiggerCard()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "small but fast link", PhysicalDeviceType.DiscreteGpu, vram: 8 * GiB),
            Row(1, "big on a Gen2 x1 slot", PhysicalDeviceType.DiscreteGpu, vram: 24 * GiB),
        ];
        List<int> opened = [];

        VulkanTracerAttempt a = VulkanRayTracer.Walk(
            VulkanRayTracer.RankedDevices(rows),
            i =>
            {
                opened.Add(i);
                return i == 1 ? Fake(rows, rows[1].Name, ": rays upload at 0.50 GB/s") : Fake(rows, rows[0].Name, null);
            },
            rows,
            CancellationToken.None);

        Assert.True(a.Success);
        Assert.Equal([1, 0], opened);
    }

    /// <summary>The largest device-local heap counts, not the sum and not a host heap.</summary>
    [Fact]
    public void TheLargestDeviceLocalHeapIsTheVram()
    {
        // VRAM, system RAM, and the BAR window reported as a small device-local heap.
        Assert.Equal(16 * GiB, VulkanDevice.LargestDeviceLocalHeap([16 * GiB, 64 * GiB, 256UL << 20], [true, false, true]));
        Assert.Equal(0UL, VulkanDevice.LargestDeviceLocalHeap([64 * GiB], [false]));
        Assert.Equal(0UL, VulkanDevice.LargestDeviceLocalHeap([], []));
    }

    /// <summary>The core count comes from the first vendor source that reports one.</summary>
    [Fact]
    public void TheCoreCountComesFromTheFirstSourceThatHasOne()
    {
        Assert.Equal(64u, VulkanDevice.FirstCoreCount(64, 72, 0, 0));
        Assert.Equal(72u, VulkanDevice.FirstCoreCount(0, 72, 0, 0));
        Assert.Equal(40u, VulkanDevice.FirstCoreCount(0, 0, 40, 0));
        Assert.Equal(12u, VulkanDevice.FirstCoreCount(0, 0, 0, 12));
        Assert.Equal(0u, VulkanDevice.FirstCoreCount(0, 0, 0, 0));
    }

    /// <summary>The probe reads every device's device-local memory from the driver.</summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void TheProbeReportsDeviceLocalMemory()
    {
        IReadOnlyList<VulkanDeviceInfo> rows = VulkanRayTracer.ProbeDevices();

        // The spec guarantees every device at least one device-local heap.
        Assert.All(rows.Where(r => r.Index >= 0), r => Assert.True(r.DeviceLocalBytes > 0, r.Name));
    }

    /// <summary>A fake attempt: accepted, or declined with a reason that starts with the device's name.</summary>
    private static VulkanTracerAttempt Fake(IReadOnlyList<VulkanDeviceInfo> rows, string name, string? why) =>
        why is null
            ? new VulkanTracerAttempt(null, new VulkanDeviceReport(rows, null, null), true)
            : new VulkanTracerAttempt(null, new VulkanDeviceReport(rows, null, name + why), false);

    /// <summary>
    /// The walk's order is selection's ranking over ray-query devices:
    /// discrete, integrated, virtual, CPU, loader order within a type;
    /// devices without ray query are not tried.
    /// </summary>
    [Fact]
    public void TheWalkTriesRayQueryDevicesBestFirst()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "llvmpipe", PhysicalDeviceType.Cpu),
            Row(1, "old GPU", PhysicalDeviceType.DiscreteGpu, rayQuery: false),
            Row(2, "iGPU", PhysicalDeviceType.IntegratedGpu),
            Row(3, "NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu),
            Row(4, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu),
            Row(-1, "(no Vulkan loader)", PhysicalDeviceType.Other, rayQuery: false),
        ];

        Assert.Equal([3, 4, 2, 0], VulkanRayTracer.RankedDevices(rows));
        Assert.Empty(VulkanRayTracer.RankedDevices([rows[1], rows[5]]));
    }

    /// <summary>[slow discrete, good discrete]: the slow one is declined and released, the good one used.</summary>
    [Fact]
    public void ASlowDiscreteLeadsToTheNextDiscrete()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu),
            Row(1, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu),
        ];
        List<string> events = [];

        VulkanTracerAttempt a = VulkanRayTracer.Walk(
            VulkanRayTracer.RankedDevices(rows),
            i =>
            {
                events.Add("open " + i);
                VulkanTracerAttempt r = i == 0
                    ? Fake(rows, rows[0].Name, ": rays upload at 0.67 GB/s, below the 2.50 GB/s floor")
                    : Fake(rows, rows[1].Name, null);
                events.Add("release-or-hand-over " + i);
                return r;
            },
            rows,
            CancellationToken.None);

        Assert.True(a.Success);
        Assert.Equal(["open 0", "release-or-hand-over 0", "open 1", "release-or-hand-over 1"], events);
    }

    /// <summary>[CPU-type, good integrated]: the integrated device is tried first and used; llvmpipe is never opened.</summary>
    [Fact]
    public void AnIntegratedGpuWinsOverACpuDevice()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "llvmpipe (LLVM 20.1.2, 256 bits)", PhysicalDeviceType.Cpu),
            Row(1, "AMD Radeon Graphics (RADV RAPHAEL_MENDOCINO)", PhysicalDeviceType.IntegratedGpu),
        ];
        List<int> opened = [];

        VulkanTracerAttempt a = VulkanRayTracer.Walk(
            VulkanRayTracer.RankedDevices(rows),
            i =>
            {
                opened.Add(i);
                return i == 1 ? Fake(rows, rows[1].Name, null) : Fake(rows, rows[0].Name, " is a CPU implementation");
            },
            rows,
            CancellationToken.None);

        Assert.True(a.Success);
        Assert.Equal([1], opened);
    }

    /// <summary>Every device declined: the CPU tracer, with each device and its reason on the one line.</summary>
    [Fact]
    public void AllDeclinedFallsToTheCpuWithEveryReason()
    {
        VulkanDeviceInfo[] rows =
        [
            Row(0, "llvmpipe", PhysicalDeviceType.Cpu),
            Row(1, "NVIDIA GeForce RTX 2070 SUPER", PhysicalDeviceType.DiscreteGpu),
            Row(2, "Broken GPU", PhysicalDeviceType.DiscreteGpu),
        ];

        VulkanTracerAttempt a = VulkanRayTracer.Walk(
            VulkanRayTracer.RankedDevices(rows),
            i => i switch
            {
                0 => Fake(rows, "llvmpipe", " is a CPU implementation of Vulkan"),
                1 => Fake(rows, "NVIDIA GeForce RTX 2070 SUPER", ": rays upload at 0.67 GB/s"),
                // A driver refusal whose message does not name the device gets the name prefixed.
                _ => new VulkanTracerAttempt(null, new VulkanDeviceReport(rows, null, "vkCreateDevice (VkResult ErrorInitializationFailed)"), false),
            },
            rows,
            CancellationToken.None);

        Assert.False(a.Success);
        Assert.Null(a.Tracer);
        Assert.Null(a.Report.Selected);
        Assert.Equal(
            "no Vulkan device here is faster than the built-in CPU tracer: "
            + "NVIDIA GeForce RTX 2070 SUPER: rays upload at 0.67 GB/s | "
            + "Broken GPU: vkCreateDevice (VkResult ErrorInitializationFailed) | "
            + "llvmpipe is a CPU implementation of Vulkan",
            a.Report.Failure);
    }

    /// <summary>A cancelled walk stops before the next device is opened.</summary>
    [Fact]
    public void ACancelledWalkOpensNothingMore()
    {
        VulkanDeviceInfo[] rows = [Row(0, "a", PhysicalDeviceType.DiscreteGpu), Row(1, "b", PhysicalDeviceType.DiscreteGpu)];
        using CancellationTokenSource cts = new();
        List<int> opened = [];

        Assert.Throws<OperationCanceledException>(() => VulkanRayTracer.Walk(
            [0, 1],
            i =>
            {
                opened.Add(i);
                cts.Cancel();
                return Fake(rows, rows[i].Name, ": declined");
            },
            rows,
            cts.Token));
        Assert.Equal([0], opened);
    }

    /// <summary>A declined attempt's line leads with the device name exactly once.</summary>
    [Fact]
    public void ADeclineLineNamesTheDeviceOnce()
    {
        VulkanDeviceReport named = new([], null, "dev: slow");
        VulkanDeviceReport bare = new([], null, "vkCreateDevice failed");
        VulkanDeviceReport none = new([], null, null);

        Assert.Equal("dev: slow", VulkanRayTracer.DeclineLine("dev", named));
        Assert.Equal("dev: vkCreateDevice failed", VulkanRayTracer.DeclineLine("dev", bare));
        Assert.Equal("dev: declined without a reason", VulkanRayTracer.DeclineLine("dev", none));
    }

    /// <summary>
    /// On a real device: a walk that declines releases every device before
    /// opening the next (the same device twice, so it runs anywhere).
    /// </summary>
    [VulkanStageFact(VulkanNeed.OnlyCpuDevice)]
    public void EveryDeclinedAttemptIsReleasedBeforeTheNext()
    {
        VulkanDeviceInfo[] rows = [.. VulkanRayTracer.ProbeDevices()];
        int cpu = rows.First(r => r.RayQuery).Index;
        List<VulkanDevice> opened = [];
        VulkanDevice Open()
        {
            // Everything opened so far must already be fully released.
            Assert.All(opened, d => Assert.Equal(0, d.LiveBytes));
            VulkanDevice d = new();
            opened.Add(d);
            return d;
        }

        VulkanTracerAttempt a = VulkanRayTracer.Walk(
            [cpu, cpu],
            i => VulkanRayTracer.TryOne(VulkanRayTracerReleaseFacts.TwoTriangles(), null, Open, Auto, i, CancellationToken.None),
            rows,
            CancellationToken.None);

        Assert.False(a.Success);
        Assert.Equal(2, opened.Count);
        Assert.All(opened, d => Assert.Equal(0, d.LiveBytes));
        Assert.Equal(2, a.Report.Failure!.Split(" | ").Length);
    }

    /// <summary>A physical-device index opens exactly that device when it can trace, else none.</summary>
    [Fact]
    public void APhysicalIndexOpensExactlyThatDevice()
    {
        DeviceCandidate[] devices =
        [
            new("old GPU", PhysicalDeviceType.DiscreteGpu, false),
            new("llvmpipe", PhysicalDeviceType.Cpu, true),
        ];

        Assert.Equal(1, VulkanDevice.ChoosePhysical(devices, 1));
        Assert.Equal(-1, VulkanDevice.ChoosePhysical(devices, 0));
        Assert.Equal(-1, VulkanDevice.ChoosePhysical(devices, 2));
    }

    // ------------------------------------------------------------------
    // The host's -gpu value
    // ------------------------------------------------------------------

    /// <summary>The CLI's decline line names the device once, whether or not the reason already did.</summary>
    [Fact]
    public void TheHostDeclineLineNamesTheDeviceOnce()
    {
        Assert.Equal(
            "NVIDIA GeForce RTX 2070 SUPER: any-hit missed a known hit on the two-triangle self-test scene",
            HostBackends.FormatDecline(
                "NVIDIA GeForce RTX 2070 SUPER",
                "NVIDIA GeForce RTX 2070 SUPER: any-hit missed a known hit on the two-triangle self-test scene"));
        Assert.Equal(
            "NVIDIA GeForce RTX 2070 SUPER: vkCreateDevice (VkResult ErrorInitializationFailed)",
            HostBackends.FormatDecline("NVIDIA GeForce RTX 2070 SUPER", "vkCreateDevice (VkResult ErrorInitializationFailed)"));
        Assert.Equal("no Vulkan loader: x", HostBackends.FormatDecline(null, "no Vulkan loader: x"));
    }

    /// <summary><c>-gpu auto</c> (any case) and an empty value pin nothing; anything else is a name pin.</summary>
    [Fact]
    public void GpuAutoPinsNothing()
    {
        Assert.Null(HostBackends.GpuDevicePin("auto"));
        Assert.Null(HostBackends.GpuDevicePin("AUTO"));
        Assert.Null(HostBackends.GpuDevicePin(""));
        Assert.Null(HostBackends.GpuDevicePin(null));
        Assert.Equal("nvidia", HostBackends.GpuDevicePin("nvidia"));
        Assert.Equal("llvmpipe", HostBackends.GpuDevicePin("llvmpipe"));
    }

    // ------------------------------------------------------------------
    // On a device
    // ------------------------------------------------------------------

    /// <summary>
    /// A staged device's probe measures a rate and allocates nothing; a
    /// device that reads rays in place is not probed.
    /// </summary>
    /// <param name="forceStaged">Keep the upload copy even where rays could be read in place.</param>
    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProbeMeasuresOnlyAStagedUploadAndAllocatesNothing(bool forceStaged)
    {
        // The default budget, so a staged probe moves what it would in a
        // real run (a third of 128 MB of rays, under the 64 MiB cap).
        VulkanDevice device = new();
        device.Construct(null, -1, 4_194_304, 3, forceStaged);
        long before = device.LiveBytes;

        double? rate = device.MeasureUploadRate();

        Assert.Equal(before, device.LiveBytes);
        if (device.SlabLayout.DirectRays)
        {
            Assert.Null(rate);
        }
        else
        {
            Assert.NotNull(rate);
            Assert.True(rate > 0, $"measured {rate} B/s");
            output.WriteLine($"{device.DeviceName}: staged ray upload {rate / 1e9:F2} GB/s over "
                + $"{Math.Min(VulkanDevice.UploadProbeBytes, (ulong)device.MaxSlabRays * 32UL)} bytes");
        }

        device.Dispose();
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// A probe that fails part way propagates its failure and leaves the
    /// device usable and fully releasable: it borrowed slot 0 and owns
    /// nothing.
    /// </summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void AFailedProbeLeavesTheDeviceUsable()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1, forceStaged: true);
        InvalidDataException planted = new("planted failure");
        device.Observe = step =>
        {
            if (step == VulkanStep.UploadProbeRecorded)
            {
                throw planted;
            }
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.MeasureUploadRate());
        device.Observe = null;
        (bool passed, SelfTestOutcome outcome) = device.RunSelfTest();
        device.Dispose();

        Assert.Same(planted, thrown);
        Assert.True(passed, VulkanDevice.SelfTestDetail(device.Identity, outcome));
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// End to end on a machine whose only device is a CPU implementation:
    /// the unpinned policy declines it before the self-test and releases it;
    /// a name pin uses it.
    /// </summary>
    [VulkanStageFact(VulkanNeed.OnlyCpuDevice)]
    public void OnlyACpuDeviceIsDeclinedUnpinnedAndUsedPinned()
    {
        VulkanDeviceInfo[] rq = [.. VulkanRayTracer.ProbeDevices().Where(r => r.RayQuery)];

        List<TryCreateStage> seen = [];
        VulkanTracerAttempt unpinned = VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), seen.Add, Auto, CancellationToken.None);
        Assert.False(unpinned.Success);
        Assert.Null(unpinned.Report.Selected);
        Assert.Equal(
            $"no Vulkan device here is faster than the built-in CPU tracer: {rq[0].Name} is a CPU implementation "
            + "of Vulkan; the built-in CPU tracer is faster, so it is used instead. Pin the device by name to trace "
            + "on it anyway",
            unpinned.Report.Failure);
        Assert.Equal([TryCreateStage.Opened, TryCreateStage.Constructed, TryCreateStage.Released], seen);

        VulkanTracerAttempt pinned = VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), Auto with { DeviceMatch = rq[0].Name });
        using VulkanRayTracer? tracer = pinned.Tracer;
        Assert.True(pinned.Success, pinned.Report.Selected?.Reason ?? pinned.Report.Failure);
    }

    /// <summary>
    /// The host's factory applies the policy: <c>-gpu auto</c> on a
    /// CPU-only machine declines with the CPU reason, and naming the
    /// device traces on it.
    /// </summary>
    [VulkanStageFact(VulkanNeed.OnlyCpuDevice)]
    public async Task TheHostDeclinesAutoOnACpuOnlyMachine()
    {

        TracedTriangle[] tris = VulkanRayTracerReleaseFacts.TwoTriangles();
        ShadowCasterSet casters = new(tris, new float[tris.Length], new int[tris.Length], []);

        GpuTracerOffer auto = await HostBackends.TryOfferGpuAsync(casters, HostBackends.GpuAnyDevice, null);
        GpuTracerOffer named = await HostBackends.TryOfferGpuAsync(casters, "llvmpipe", null);
        (named.Tracer as IDisposable)?.Dispose();

        Assert.Null(auto.Tracer);
        Assert.Contains("is a CPU implementation of Vulkan; the built-in CPU tracer is faster", auto.DeclineReason);
        Assert.NotNull(named.Tracer);
    }
}
