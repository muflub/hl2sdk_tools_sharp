//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="VulkanRayTracer.TryCreate(ReadOnlyMemory{TracedTriangle}, VulkanRayTracerOptions, CancellationToken)"/>
/// releases the device it opened on every path that does not hand it to a
/// tracer: a decline, a failed gate, cancellation, and an exception of a type
/// nobody expected.
/// </summary>
/// <remarks>
/// <para>
/// The attempt's stage observer is the seam: a fact throws from it at a
/// chosen stage and counts <see cref="TryCreateStage.Released"/>. The device
/// is real, so each fact needs as much of a Vulkan stack as its stage does:
/// <see cref="TryCreateStage.Opened"/> needs only a loader,
/// <see cref="TryCreateStage.Constructed"/> and
/// <see cref="TryCreateStage.SelfTested"/> a ray-query device (llvmpipe
/// will do), and <see cref="TryCreateStage.SceneLoaded"/> a device that
/// passes the self-test. Each skips with the probe's reason otherwise.
/// </para>
/// <para>
/// The skips come from <see cref="VulkanStageFactAttribute"/> and
/// <see cref="VulkanStageTheoryAttribute"/>.
/// </para>
/// </remarks>
[Collection(VulkanDeviceCollection.Name)]
public sealed class VulkanRayTracerReleaseFacts
{
    /// <summary>What a fact throws from the observer.</summary>
    public enum Failure
    {
        /// <summary>An <see cref="OperationCanceledException"/>: must propagate, never decline.</summary>
        Cancelled,

        /// <summary>A type the attempt does not expect: must propagate.</summary>
        Unexpected,

        /// <summary>A <see cref="VulkanException"/>: an expected driver refusal, declined.</summary>
        Vulkan,

        /// <summary>A <see cref="NotSupportedException"/>: declined.</summary>
        NotSupported,

        /// <summary>An <see cref="InvalidOperationException"/>: declined.</summary>
        InvalidOperation,
    }

    /// <summary>Failing right after the device object exists releases it, whatever was thrown.</summary>
    /// <param name="failure">What the observer throws.</param>
    [VulkanStageTheory(VulkanNeed.Loader)]
    [InlineData(Failure.Cancelled)]
    [InlineData(Failure.Unexpected)]
    [InlineData(Failure.Vulkan)]
    [InlineData(Failure.NotSupported)]
    [InlineData(Failure.InvalidOperation)]
    public void FailureAfterOpenReleasesTheDevice(Failure failure) =>
        AssertReleasedOnce(TryCreateStage.Opened, failure);

    /// <summary>Failing with an instance, a device and a pipeline built releases them, whatever was thrown.</summary>
    /// <param name="failure">What the observer throws.</param>
    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(Failure.Cancelled)]
    [InlineData(Failure.Unexpected)]
    [InlineData(Failure.Vulkan)]
    [InlineData(Failure.NotSupported)]
    [InlineData(Failure.InvalidOperation)]
    public void FailureAfterConstructReleasesTheDevice(Failure failure) =>
        AssertReleasedOnce(TryCreateStage.Constructed, failure);

    /// <summary>Failing after the self-test's BLAS and dispatches releases the device, whatever was thrown.</summary>
    /// <param name="failure">What the observer throws.</param>
    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(Failure.Cancelled)]
    [InlineData(Failure.Unexpected)]
    [InlineData(Failure.Vulkan)]
    [InlineData(Failure.NotSupported)]
    [InlineData(Failure.InvalidOperation)]
    public void FailureAfterSelfTestReleasesTheDevice(Failure failure) =>
        AssertReleasedOnce(TryCreateStage.SelfTested, failure);

    /// <summary>
    /// Failing with the real scene loaded, just before a tracer would take
    /// the device, releases it: no tracer exists yet to do it.
    /// </summary>
    /// <param name="failure">What the observer throws.</param>
    [VulkanStageTheory(VulkanNeed.PassingDevice)]
    [InlineData(Failure.Cancelled)]
    [InlineData(Failure.Unexpected)]
    [InlineData(Failure.Vulkan)]
    [InlineData(Failure.NotSupported)]
    [InlineData(Failure.InvalidOperation)]
    public void FailureAfterSceneLoadReleasesTheDevice(Failure failure) =>
        AssertReleasedOnce(TryCreateStage.SceneLoaded, failure);

    /// <summary>
    /// The attempt's own cancellation check, not a thrown observer: a token
    /// cancelled once the device is open ends the attempt with
    /// <see cref="OperationCanceledException"/> and the device released.
    /// </summary>
    [VulkanStageFact(VulkanNeed.Loader)]
    public void CancellationAfterOpenPropagatesAndReleases()
    {
        using CancellationTokenSource cts = new();
        List<TryCreateStage> seen = [];
        OperationCanceledException thrown = Assert.ThrowsAny<OperationCanceledException>(() =>
            VulkanRayTracer.TryCreate(
                TwoTriangles(),
                stage =>
                {
                    seen.Add(stage);
                    if (stage == TryCreateStage.Opened)
                    {
                        cts.Cancel();
                    }
                },
                default,
                cts.Token));
        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.Equal([TryCreateStage.Opened, TryCreateStage.Released], seen);
    }

    /// <summary>
    /// The same after the gate passed: the check before the scene upload
    /// sees the cancellation, and the device the gate approved is released.
    /// </summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void CancellationAfterPassingGatePropagatesAndReleases()
    {
        using CancellationTokenSource cts = new();
        List<TryCreateStage> seen = [];
        Assert.ThrowsAny<OperationCanceledException>(() =>
            VulkanRayTracer.TryCreate(
                TwoTriangles(),
                stage =>
                {
                    seen.Add(stage);
                    if (stage == TryCreateStage.SelfTested)
                    {
                        cts.Cancel();
                    }
                },
                default,
                cts.Token));
        Assert.Equal(
            [TryCreateStage.Opened, TryCreateStage.Constructed, TryCreateStage.SelfTested, TryCreateStage.Released],
            seen);
    }

    /// <summary>
    /// With nothing thrown, the device is released exactly once when the
    /// gate rejects it (llvmpipe does) and not at all when a tracer takes it;
    /// the tracer releases it on dispose.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void UnthrownAttemptReleasesOnlyWhatNoTracerTook()
    {
        List<TryCreateStage> seen = [];
        VulkanTracerAttempt attempt = VulkanRayTracer.TryCreate(TwoTriangles(), seen.Add, default, CancellationToken.None);
        using VulkanRayTracer? tracer = attempt.Tracer;
        if (attempt.Success)
        {
            Assert.NotNull(tracer);
            Assert.DoesNotContain(TryCreateStage.Released, seen);
            Assert.Equal(TryCreateStage.SceneLoaded, seen[^1]);
        }
        else
        {
            Assert.Null(tracer);
            Assert.Single(seen, s => s == TryCreateStage.Released);
            Assert.Equal(TryCreateStage.Released, seen[^1]);
        }
    }

    private static void AssertReleasedOnce(TryCreateStage at, Failure failure)
    {
        Exception planted = failure switch
        {
            Failure.Cancelled => new OperationCanceledException("planted cancellation"),
            Failure.Unexpected => new PlantedException(),
            Failure.Vulkan => new VulkanException(Silk.NET.Vulkan.Result.ErrorDeviceLost, "planted refusal", null),
            Failure.NotSupported => new NotSupportedException("planted refusal"),
            Failure.InvalidOperation => new InvalidOperationException("planted refusal"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        List<TryCreateStage> seen = [];
        void Observe(TryCreateStage stage)
        {
            seen.Add(stage);
            if (stage == at)
            {
                throw planted;
            }
        }

        bool declines = failure is Failure.Vulkan or Failure.NotSupported or Failure.InvalidOperation;
        if (declines)
        {
            VulkanTracerAttempt attempt = VulkanRayTracer.TryCreate(TwoTriangles(), Observe, default, CancellationToken.None);
            Assert.False(attempt.Success);
            Assert.Null(attempt.Tracer);
            Assert.NotNull(attempt.Report.Failure);
            Assert.Contains("planted refusal", attempt.Report.Failure);
        }
        else
        {
            Exception thrown = Assert.ThrowsAny<Exception>(
                () => VulkanRayTracer.TryCreate(TwoTriangles(), Observe, default, CancellationToken.None));
            Assert.Same(planted, thrown);
        }

        Assert.Contains(at, seen);
        Assert.Single(seen, s => s == TryCreateStage.Released);
        Assert.Equal(TryCreateStage.Released, seen[^1]);
    }

    internal static TracedTriangle[] TwoTriangles() =>
    [
        new(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), 0),
        new(1, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), 0),
    ];

    /// <summary>An exception type <c>TryCreate</c> has no reason to expect.</summary>
    private sealed class PlantedException() : Exception("planted unexpected failure");
}
