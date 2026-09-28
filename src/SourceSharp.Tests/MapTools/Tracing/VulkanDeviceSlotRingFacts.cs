//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using Silk.NET.Vulkan;

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="VulkanDevice"/>'s slot ring and its teardown when work does
/// not finish: a slot whose wait timed out is not reused until its fence
/// signals, a dispose with a fence that never signals returns within its
/// bound and frees nothing, a ring that fails part way is released whole,
/// and a machine with no Vulkan loader gets a clear decline.
/// </summary>
/// <remarks>
/// <para>
/// A hung GPU cannot be ordered up, so the facts replace the device's
/// fence calls (<see cref="IFenceWaits"/>) with <see cref="GatedFences"/>:
/// a fence that reports a timeout until the fact opens the gate, and then
/// hands the call to the driver. The kernel really runs; only the wait's
/// answer is the fake's.
/// </para>
/// <para>
/// The facts on <see cref="VulkanDevice.DrainedWithin"/>, on
/// <see cref="VulkanDevice.IsMissingLoader"/> and on the loader decline
/// need no device and run everywhere. The others need a device that
/// exposes ray query (llvmpipe will do) and skip with the probe's reason
/// elsewhere.
/// </para>
/// </remarks>
public sealed class VulkanDeviceSlotRingFacts
{
    private const int SmallSlab = 4096;

    // ------------------------------------------------------------------
    // Dispose's bounded wait, without a device
    // ------------------------------------------------------------------

    /// <summary>With nothing in flight there is nothing to wait for, and no wait is made.</summary>
    [Fact]
    public void NothingPendingIsDrainedWithoutAWait()
    {
        CountingFences fences = new(Result.Timeout);

        Assert.True(VulkanDevice.DrainedWithin(fences, [], TimeSpan.FromSeconds(1)));
        Assert.Equal(0, fences.Waits);
    }

    /// <summary>
    /// Signalled fences and a lost device may be released; a timeout or any
    /// other error from the wait may not, because the GPU may still be
    /// reading.
    /// </summary>
    /// <param name="answer">What the wait returns.</param>
    /// <param name="drained">Whether the device's memory may be freed after it.</param>
    [Theory]
    [InlineData(Result.Success, true)]
    [InlineData(Result.ErrorDeviceLost, true)]
    [InlineData(Result.Timeout, false)]
    [InlineData(Result.ErrorOutOfHostMemory, false)]
    public void TheWaitsAnswerDecidesWhetherTheDeviceMayBeReleased(Result answer, bool drained)
    {
        CountingFences fences = new(answer);

        Assert.Equal(drained, VulkanDevice.DrainedWithin(fences, [new Fence(1), new Fence(2)], TimeSpan.FromSeconds(2)));
        Assert.Equal(1, fences.Waits);
        Assert.Equal(2, fences.LastCount);
        Assert.Equal(2_000_000_000UL, fences.LastTimeoutNs);
    }

    /// <summary>A negative bound waits for no time at all rather than wrapping to a huge one.</summary>
    [Fact]
    public void ANegativeBoundWaitsZero()
    {
        CountingFences fences = new(Result.Timeout);

        Assert.False(VulkanDevice.DrainedWithin(fences, [new Fence(1)], TimeSpan.FromSeconds(-1)));
        Assert.Equal(0UL, fences.LastTimeoutNs);
    }

    /// <summary>
    /// A fence that never signals: the wait blocks for exactly the timeout it
    /// is given, as a driver does, and a wait given no bound would block
    /// forever. The drain returns within its bound, false.
    /// </summary>
    [Fact]
    public async Task AFenceThatNeverSignalsIsGivenUpOnWithinTheBound()
    {
        GatedFences fences = new(real: null) { BlockForTheTimeout = true };
        TimeSpan bound = TimeSpan.FromMilliseconds(200);
        Stopwatch clock = Stopwatch.StartNew();

        bool drained = await Task.Run(() => VulkanDevice.DrainedWithin(fences, [new Fence(1)], bound))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(drained);
        Assert.True(clock.Elapsed >= bound - TimeSpan.FromMilliseconds(20), $"returned after {clock.Elapsed}");
        Assert.Equal(200_000_000UL, fences.LastTimeoutNs);
    }

    // ------------------------------------------------------------------
    // The missing-loader decline, without a device
    // ------------------------------------------------------------------

    /// <summary>What the API load throws with no loader present counts as "no loader"; nothing else does.</summary>
    [Fact]
    public void OnlyTheLoadersOwnFailuresMeanNoLoader()
    {
        Assert.True(VulkanDevice.IsMissingLoader(new FileNotFoundException("libvulkan.so.1")));
        Assert.True(VulkanDevice.IsMissingLoader(new DllNotFoundException("vulkan-1")));
        Assert.False(VulkanDevice.IsMissingLoader(new VulkanException(Result.ErrorInitializationFailed, "vkCreateInstance", null)));
        Assert.False(VulkanDevice.IsMissingLoader(new InvalidOperationException("driver refused")));
    }

    /// <summary>
    /// With no loader, the attempt declines with the loader's message and no
    /// device record, instead of throwing, so the caller keeps its CPU tracer.
    /// </summary>
    /// <param name="dll">Whether the runtime's <see cref="DllNotFoundException"/> is thrown, rather than Silk.NET's <see cref="FileNotFoundException"/>.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoLoaderIsAClearDecline(bool dll)
    {
        Exception missing = dll ? new DllNotFoundException("vulkan-1 not found") : new FileNotFoundException("vulkan-1 not found");

        VulkanTracerAttempt attempt = VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), null, () => throw missing, default, CancellationToken.None);

        Assert.False(attempt.Success);
        Assert.Null(attempt.Tracer);
        Assert.Null(attempt.Report.Selected);
        Assert.Equal("no Vulkan loader: vulkan-1 not found", attempt.Report.Failure);
    }

    /// <summary>Any other failure while loading the API is not a decline: it propagates.</summary>
    [Fact]
    public void AnotherFailureLoadingTheApiPropagates()
    {
        InvalidDataException planted = new("planted failure");

        Exception thrown = Assert.ThrowsAny<Exception>(() => VulkanRayTracer.TryCreate(
            VulkanRayTracerReleaseFacts.TwoTriangles(), null, () => throw planted, default, CancellationToken.None));

        Assert.Same(planted, thrown);
    }

    // ------------------------------------------------------------------
    // The slot ring on a device
    // ------------------------------------------------------------------

    /// <summary>
    /// A slab whose wait timed out keeps its slot: completing, staging and
    /// submitting it again are all refused while its fence has not
    /// signalled, and the fence is not reset. Once it signals, the slot is
    /// retired (one reset) and traces again.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void ATimedOutSlotIsNotReusedUntilItsFenceSignals()
    {
        VulkanDevice device = new();
        IFenceWaits driver = device.FenceWaits;
        try
        {
            device.Construct(null, -1, SmallSlab, 1);
            device.LoadScene(VulkanDeviceReleaseFacts.Vertices());
            GatedFences fences = new(driver);
            device.FenceWaits = fences;
            uint[] words = new uint[2];

            Submit(device);
            Assert.Equal(Result.Timeout, Assert.Throws<VulkanException>(() => device.Complete(0, words)).Result);

            // Still in flight: every use of the slot waits again, and fails again.
            Assert.Equal(Result.Timeout, Assert.Throws<VulkanException>(() => device.Complete(0, words)).Result);
            Assert.Equal(Result.Timeout, Assert.Throws<VulkanException>(() => device.StageRays(0, 64)).Result);
            Assert.Equal(Result.Timeout, Assert.Throws<VulkanException>(
                () => device.Submit(0, 4, 64, 2, 0, VulkanDevice.TmaxScaleBits)).Result);
            Assert.Equal(4, fences.Waits);
            Assert.Equal(0, fences.Resets);

            fences.Signalled = true;
            _ = device.StageRays(0, 64);
            Assert.Equal(1, fences.Resets);

            // And the slot traces again: mode 4 writes all-ones.
            device.Submit(0, 4, 64, 2, 0, VulkanDevice.TmaxScaleBits);
            device.Complete(0, words);
            Assert.Equal([0xFFFFFFFFu, 0xFFFFFFFFu], words);
        }
        finally
        {
            device.FenceWaits = driver;
            device.Dispose();
        }
    }

    /// <summary>
    /// A dispose with a slab in flight whose fence never signals returns
    /// within its bound, frees nothing (the GPU may still be using that
    /// memory) and reports the device abandoned. Once the fence has
    /// signalled, a second dispose releases every byte.
    /// </summary>
    /// <remarks>
    /// Red before the fix: dispose called <c>vkDeviceWaitIdle</c>, which on
    /// this working device returned and freed everything, and after a real
    /// hang has no timeout at all.
    /// </remarks>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public async Task DisposeWithAFenceThatNeverSignalsReturnsWithinItsBoundAndFreesNothing()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        device.LoadScene(VulkanDeviceReleaseFacts.Vertices());
        IFenceWaits driver = device.FenceWaits;
        GatedFences fences = new(driver);
        device.FenceWaits = fences;
        Submit(device);
        Assert.Throws<VulkanException>(() => device.Complete(0, new uint[2]));
        long held = device.LiveBytes;
        Assert.True(held > 0);

        fences.BlockForTheTimeout = true;
        device.DisposeWait = TimeSpan.FromMilliseconds(300);
        Stopwatch clock = Stopwatch.StartNew();
        await Task.Run(device.Dispose).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(280), $"returned after {clock.Elapsed}");
        Assert.Equal(300_000_000UL, fences.LastTimeoutNs);
        Assert.True(device.Abandoned);
        Assert.Equal(held, device.LiveBytes);

        // The GPU was never really hung: let the wait through, and the
        // retry releases everything.
        fences.Signalled = true;
        device.Dispose();

        Assert.False(device.Abandoned);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// With nothing in flight, dispose waits for nothing and releases every
    /// byte (the fence calls are never made).
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void DisposeWithNothingInFlightMakesNoWait()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 2);
        device.LoadScene(VulkanDeviceReleaseFacts.Vertices());
        GatedFences fences = new(device.FenceWaits);
        device.FenceWaits = fences;

        device.Dispose();

        Assert.Equal(0, fences.Waits);
        Assert.False(device.Abandoned);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// A ring that fails part way through a slot (buffers made, command
    /// buffer and fence not) leaves complete slots, the partial one and
    /// never-made ones behind, and dispose releases every byte of them, in
    /// both memory layouts.
    /// </summary>
    /// <param name="failAt">Which slot's buffers, counting from 1, the failure follows.</param>
    /// <param name="forceStaged">Whether the slots keep separate host and kernel buffers.</param>
    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void ARingThatFailsPartWayIsReleasedWhole(int failAt, bool forceStaged)
    {
        int seen = 0;
        InvalidDataException planted = new("planted failure");
        VulkanDevice device = new()
        {
            Observe = step =>
            {
                if (step == VulkanStep.SlotBuffersAllocated && ++seen == failAt)
                {
                    throw planted;
                }
            },
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.Construct(null, -1, SmallSlab, 3, forceStaged));
        long held = device.LiveBytes;
        device.Dispose();

        Assert.Same(planted, thrown);
        Assert.True(held > 0);
        Assert.False(device.Abandoned);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>Stages one workgroup in slot 0 and submits it in mode 4 (no traversal).</summary>
    private static void Submit(VulkanDevice device)
    {
        device.StageRays(0, 64).Clear();
        device.Submit(0, 4, 64, 2, 0, VulkanDevice.TmaxScaleBits);
    }

    /// <summary>Answers every wait with a fixed result and counts the calls.</summary>
    private sealed class CountingFences(Result answer) : IFenceWaits
    {
        public int Waits { get; private set; }

        public int LastCount { get; private set; }

        public ulong LastTimeoutNs { get; private set; }

        public Result Wait(ReadOnlySpan<Fence> fences, ulong timeoutNs)
        {
            Waits++;
            LastCount = fences.Length;
            LastTimeoutNs = timeoutNs;
            return answer;
        }

        public Result Reset(Fence fence) => Result.Success;
    }

    /// <summary>
    /// A fence that has not signalled until <see cref="Signalled"/> is set:
    /// until then every wait answers <c>Timeout</c> (at once, or after
    /// blocking for the whole timeout like a driver when
    /// <see cref="BlockForTheTimeout"/> is set), and afterwards the calls go
    /// to the real calls it was given, or succeed when there are none.
    /// </summary>
    private sealed class GatedFences(IFenceWaits? real) : IFenceWaits
    {

        public volatile bool Signalled;

        public volatile bool BlockForTheTimeout;

        public int Waits { get; private set; }

        public int Resets { get; private set; }

        public ulong LastTimeoutNs { get; private set; }

        public Result Wait(ReadOnlySpan<Fence> fences, ulong timeoutNs)
        {
            Waits++;
            LastTimeoutNs = timeoutNs;
            if (Signalled)
            {
                return real?.Wait(fences, timeoutNs) ?? Result.Success;
            }

            if (BlockForTheTimeout)
            {
                // A wait with no usable bound blocks for good, as the
                // driver's would on a hung GPU.
                ulong ms = timeoutNs / 1_000_000UL;
                Thread.Sleep(ms > int.MaxValue ? Timeout.Infinite : (int)ms);
            }

            return Result.Timeout;
        }

        public Result Reset(Fence fence)
        {
            Resets++;
            return real?.Reset(fence) ?? Result.Success;
        }
    }
}
