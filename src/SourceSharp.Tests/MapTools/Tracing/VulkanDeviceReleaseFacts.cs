//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Gpu.Interop;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="VulkanDevice"/> releases the temporary native objects of a
/// build that fails part-way: the device probe's instance and loaded API,
/// the pipeline build's shader module and entry-point name, the scene
/// upload's staging buffer and the BLAS build's scratch buffer.
/// </summary>
/// <remarks>
/// The device's <see cref="VulkanStep"/> observer is the seam: a fact throws
/// from it where the driver would have refused. Buffers are accounted by
/// <see cref="VulkanDevice.LiveBytes"/>, which a device that released
/// everything brings back to zero when disposed.
/// </remarks>
public sealed class VulkanDeviceReleaseFacts
{
    private const int SmallSlab = 4096;

    [VulkanStageFact(VulkanNeed.Loader)]
    public void AProbeReleasesItsInstanceAndApi()
    {
        List<VulkanStep> seen = [];
        _ = VulkanDevice.ProbeDevices([], seen.Add);

        Assert.Equal([VulkanStep.ProbeInstanceCreated, VulkanStep.ProbeInstanceDestroyed, VulkanStep.ProbeApiReleased], seen);
    }

    [VulkanStageFact(VulkanNeed.Loader)]
    public void AProbeWhoseInstanceFailsStillReleasesTheApi()
    {
        List<VulkanStep> seen = [];
        List<VulkanDeviceInfo> rows = VulkanDevice.ProbeDevices(["VK_SOURCESHARP_no_such_extension"], seen.Add);

        VulkanDeviceInfo only = Assert.Single(rows);
        Assert.StartsWith("instance failed", only.DeviceType);
        Assert.Equal([VulkanStep.ProbeApiReleased], seen);
    }

    [VulkanStageFact(VulkanNeed.Loader)]
    public void AProbeFailingWhileEnumeratingReleasesTheInstanceAndApi()
    {
        List<VulkanStep> seen = [];
        InvalidDataException planted = new("planted failure");

        Exception thrown = Assert.ThrowsAny<Exception>(() => VulkanDevice.ProbeDevices([], step =>
        {
            seen.Add(step);
            if (step == VulkanStep.ProbeInstanceCreated)
            {
                throw planted;
            }
        }));

        Assert.Same(planted, thrown);
        Assert.Equal([VulkanStep.ProbeInstanceCreated, VulkanStep.ProbeInstanceDestroyed, VulkanStep.ProbeApiReleased], seen);
    }

    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void APipelineBuildFailingReleasesTheShaderModule()
    {
        List<VulkanStep> seen = [];
        InvalidDataException planted = new("planted failure");
        using VulkanDevice device = new()
        {
            Observe = step =>
            {
                seen.Add(step);
                if (step == VulkanStep.ShaderModuleCreated)
                {
                    throw planted;
                }
            },
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.Construct(null, -1, SmallSlab, 1));

        Assert.Same(planted, thrown);
        Assert.Equal([VulkanStep.ShaderModuleCreated, VulkanStep.ShaderModuleReleased], seen);
    }

    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void ACleanBuildReleasesEveryByteOnDispose()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        device.LoadScene(Vertices());
        Assert.True(device.LiveBytes > 0);

        device.Dispose();

        Assert.Equal(0, device.LiveBytes);
    }

    [VulkanStageTheory(VulkanNeed.RayQueryDevice)]
    [InlineData(false)]
    [InlineData(true)]
    public void ASceneLoadFailingReleasesItsTemporaryBufferWithTheDevice(bool inBlasBuild)
    {
        // The upload's staging buffer, or the BLAS build's scratch buffer.
        VulkanStep at = inBlasBuild ? VulkanStep.ScratchAllocated : VulkanStep.StagingAllocated;
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        InvalidDataException planted = new("planted failure");
        device.Observe = step =>
        {
            if (step == at)
            {
                throw planted;
            }
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.LoadScene(Vertices()));
        device.Dispose();

        Assert.Same(planted, thrown);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// A scene load uploads the vertices, then builds a BLAS and a TLAS over
    /// it, each with its own scratch, and gives every byte back on dispose.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void ASceneIsBuiltAsABlasWithATlasOverIt()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        List<VulkanStep> seen = [];
        device.Observe = seen.Add;

        device.LoadScene(Vertices());
        device.Dispose();

        Assert.Equal([VulkanStep.StagingAllocated, VulkanStep.ScratchAllocated, VulkanStep.TopLevelScratchAllocated], seen);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// A load failing once the TLAS's buffers exist (after the BLAS's)
    /// propagates its own exception and still releases every buffer with the
    /// device.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void ASceneLoadFailingAtTheTopLevelReleasesEveryBufferWithTheDevice()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        InvalidDataException planted = new("planted failure");
        device.Observe = step =>
        {
            if (step == VulkanStep.TopLevelScratchAllocated)
            {
                throw planted;
            }
        };

        Exception thrown = Assert.ThrowsAny<Exception>(() => device.LoadScene(Vertices()));
        device.Dispose();

        Assert.Same(planted, thrown);
        Assert.Equal(0, device.LiveBytes);
    }

    /// <summary>
    /// The self-test's scene and then the real one: the second load replaces
    /// both structures and their buffers instead of adding to them.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void ASecondSceneReplacesBothStructuresWithoutGrowing()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, SmallSlab, 1);
        device.LoadScene(Vertices());
        long once = device.LiveBytes;

        device.LoadScene(Vertices());
        long twice = device.LiveBytes;
        device.Dispose();

        Assert.Equal(once, twice);
        Assert.Equal(0, device.LiveBytes);
    }

    internal static float[] Vertices()
    {
        float[] v = new float[VulkanRayTracerReleaseFacts.TwoTriangles().Length * 9];
        int b = 0;
        foreach (var t in VulkanRayTracerReleaseFacts.TwoTriangles())
        {
            foreach (var p in (ReadOnlySpan<SourceSharp.MapFormats.Geometry.Vec3>)[t.V0, t.V1, t.V2])
            {
                v[b++] = p.X;
                v[b++] = p.Y;
                v[b++] = p.Z;
            }
        }

        return v;
    }
}
