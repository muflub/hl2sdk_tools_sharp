//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.RegularExpressions;

using Silk.NET.Vulkan;

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The scene build, device checks and kernel rules the Vulkan tracer needs
/// to be portable beyond RADV: the TLAS that is bound and its instance, the
/// memory and barrier rules of the builds, the features and API version a
/// device must have, and the kernel's refusal of undefined ray queries.
/// </summary>
/// <remarks>
/// The pure facts run everywhere. The device facts run on any ray-query
/// device (llvmpipe in a container) and skip with the probe's words where
/// there is none. Every one of these rules was broken at some point in a
/// way RADV did not show; the validation layer on llvmpipe reported the
/// build-side ones.
/// </remarks>
public sealed class VulkanSceneBuildFacts
{
    // ------------------------------------------------------------------
    // The TLAS and its instance
    // ------------------------------------------------------------------

    /// <summary>
    /// The instance is the BLAS under the identity transform, custom index
    /// 0, mask 0xFF, binding-table offset 0, facing culling disabled.
    /// </summary>
    [Fact]
    public unsafe void TheSceneInstanceIsAnIdentityInstanceOfTheBlas()
    {
        const ulong Address = 0x0000_1234_5678_9A00UL;

        AccelerationStructureInstanceKHR instance = VulkanDevice.SceneInstance(Address);

        float[] transform = new float[12];
        for (int i = 0; i < 12; i++)
        {
            transform[i] = instance.Transform.Matrix[i];
        }

        Assert.Equal([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f], transform);
        Assert.Equal(0u, instance.InstanceCustomIndex);
        Assert.Equal(0xFFu, instance.Mask);
        Assert.Equal(0u, instance.InstanceShaderBindingTableRecordOffset);
        Assert.Equal(GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr, instance.Flags);
        Assert.Equal(Address, instance.AccelerationStructureReference);
    }

    /// <summary>
    /// After a scene load the kernel's binding holds the scene's TLAS, never
    /// the BLAS, and every buffer that may be asked for a device address was
    /// given device-address memory.
    /// </summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void TheTlasIsBoundAndAddressableMemoryIsFlagged()
    {
        using VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1);
        Assert.False(device.BindsTopLevel);

        device.LoadScene(VulkanDeviceReleaseFacts.Vertices());

        Assert.True(device.BindsTopLevel);
        // Vertex buffer; BLAS storage and scratch; instance buffer, TLAS storage and scratch.
        Assert.Equal(6, device.AddressableAllocations);
        Assert.Equal(device.AddressableAllocations, device.DeviceAddressFlaggedAllocations);
    }

    /// <summary>A load that fails before the binding is written leaves nothing claiming to be bound.</summary>
    [VulkanStageFact(VulkanNeed.RayQueryDevice)]
    public void AFailedLoadBindsNothing()
    {
        VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1);
        device.LoadScene(VulkanDeviceReleaseFacts.Vertices());
        device.Observe = step =>
        {
            if (step == VulkanStep.TopLevelScratchAllocated)
            {
                throw new InvalidDataException("planted failure");
            }
        };

        Assert.ThrowsAny<InvalidDataException>(() => device.LoadScene(VulkanDeviceReleaseFacts.Vertices()));
        Assert.False(device.BindsTopLevel);
        device.Dispose();
        Assert.Equal(0, device.LiveBytes);
    }

    // ------------------------------------------------------------------
    // Barriers
    // ------------------------------------------------------------------

    /// <summary>
    /// The set-up barriers name the accesses the spec gives each edge: the
    /// build reads its vertices as a shader read at the build stage, the
    /// TLAS build reads the BLAS as an acceleration structure, and the
    /// kernel's ray queries read the scene as an acceleration structure at
    /// the compute stage.
    /// </summary>
    [Fact]
    public void TheSetupBarriersNameTheRightAccesses()
    {
        BarrierMasks upload = VulkanDevice.UploadToBuild;
        Assert.Equal(PipelineStageFlags.TransferBit, upload.SrcStage);
        Assert.Equal(AccessFlags.TransferWriteBit, upload.SrcAccess);
        Assert.Equal(PipelineStageFlags.AccelerationStructureBuildBitKhr, upload.DstStage);
        Assert.True(upload.DstAccess.HasFlag(AccessFlags.ShaderReadBit), upload.DstAccess.ToString());

        BarrierMasks between = VulkanDevice.BlasToTlas;
        Assert.Equal(PipelineStageFlags.AccelerationStructureBuildBitKhr, between.SrcStage);
        Assert.Equal(AccessFlags.AccelerationStructureWriteBitKhr, between.SrcAccess);
        Assert.Equal(PipelineStageFlags.AccelerationStructureBuildBitKhr, between.DstStage);
        Assert.Equal(AccessFlags.AccelerationStructureReadBitKhr, between.DstAccess);

        BarrierMasks trace = VulkanDevice.BuildToTrace;
        Assert.Equal(PipelineStageFlags.AccelerationStructureBuildBitKhr, trace.SrcStage);
        Assert.Equal(AccessFlags.AccelerationStructureWriteBitKhr, trace.SrcAccess);
        Assert.Equal(PipelineStageFlags.ComputeShaderBit, trace.DstStage);
        Assert.Equal(AccessFlags.AccelerationStructureReadBitKhr, trace.DstAccess);
    }

    // ------------------------------------------------------------------
    // Scratch alignment
    // ------------------------------------------------------------------

    /// <summary>Rounding up: already aligned stays, anything else goes to the next multiple; 0 counts as 1.</summary>
    [Fact]
    public void AlignUpRoundsToTheNextMultiple()
    {
        Assert.Equal(0UL, VulkanDevice.AlignUp(0, 128));
        Assert.Equal(128UL, VulkanDevice.AlignUp(1, 128));
        Assert.Equal(128UL, VulkanDevice.AlignUp(128, 128));
        Assert.Equal(256UL, VulkanDevice.AlignUp(129, 128));
        Assert.Equal(7UL, VulkanDevice.AlignUp(7, 1));
        Assert.Equal(7UL, VulkanDevice.AlignUp(7, 0));
    }

    /// <summary>
    /// Whatever the buffer's own address, the aligned scratch address plus
    /// the build's scratch size fits inside the allocation.
    /// </summary>
    /// <param name="required">The build's scratch size.</param>
    /// <param name="alignment">The device's scratch alignment.</param>
    [Theory]
    [InlineData(1000UL, 128UL)]
    [InlineData(256UL, 256UL)]
    [InlineData(17UL, 1UL)]
    [InlineData(10UL, 0UL)]
    [InlineData(0UL, 128UL)]
    public void AnAlignedScratchAddressStillFitsItsBuild(ulong required, ulong alignment)
    {
        ulong size = VulkanDevice.ScratchSize(required, alignment);
        ulong a = Math.Max(1UL, alignment);

        Assert.True(size >= 16);
        foreach (ulong baseAddress in new ulong[] { 0x10000, 0x10001, 0x10040, 0x1007F, 0x100FF })
        {
            ulong aligned = VulkanDevice.AlignUp(baseAddress, a);
            Assert.Equal(0UL, aligned % a);
            Assert.True(aligned + required <= baseAddress + size,
                $"base 0x{baseAddress:X}: {required} scratch bytes run past a {size}-byte allocation");
        }
    }

    // ------------------------------------------------------------------
    // Device checks
    // ------------------------------------------------------------------

    /// <summary>A device must offer both ray query and the acceleration-structure feature.</summary>
    /// <param name="rayQuery">The ray-query feature.</param>
    /// <param name="accelerationStructure">The acceleration-structure feature.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TraceableNeedsBothFeatures(bool rayQuery, bool accelerationStructure)
    {
        Assert.Equal(rayQuery && accelerationStructure, VulkanDevice.Traceable(rayQuery, accelerationStructure));
    }

    /// <summary>Vulkan 1.3 and later pass; 1.2 is refused with the version it reported.</summary>
    [Fact]
    public void ADeviceBelowVulkan13IsRefused()
    {
        VulkanDevice.RequireApiVersion("dev", (1u << 22) | (3u << 12));
        VulkanDevice.RequireApiVersion("dev", (1u << 22) | (4u << 12) | 303u);
        NotSupportedException e = Assert.Throws<NotSupportedException>(
            () => VulkanDevice.RequireApiVersion("dev", (1u << 22) | (2u << 12) | 131u));
        Assert.Equal("dev reports Vulkan 1.2.131; the ray-query kernel needs Vulkan 1.3 (update the driver)", e.Message);
    }

    // ------------------------------------------------------------------
    // tmin the kernel may be given
    // ------------------------------------------------------------------

    /// <summary>A negative or NaN minimum distance is not a ray-query tmin: refused, and routed to the CPU.</summary>
    /// <param name="minDistance">The minimum distance.</param>
    /// <param name="supported">Whether the GPU may trace with it.</param>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(1e-3f, true)]
    [InlineData(-1e-3f, false)]
    [InlineData(float.NaN, false)]
    public void OnlyANonNegativeMinDistanceIsSupported(float minDistance, bool supported)
    {
        RayTraceOptions options = new(minDistance);

        Assert.Equal(supported, VulkanRayTracer.SupportsOptions(options));
        if (supported)
        {
            VulkanRayTracer.RequireSupported(options);
        }
        else
        {
            NotSupportedException e = Assert.Throws<NotSupportedException>(
                () => VulkanRayTracer.RequireSupported(options));
            Assert.Contains("tmin must be non-negative", e.Message);
        }
    }

    /// <summary>A skipped id is still refused with its own message, whatever the minimum distance.</summary>
    [Fact]
    public void ASkippedIdKeepsItsOwnRefusal()
    {
        RayTraceOptions options = new RayTraceOptions(-1f) with { SkipId = 7 };

        Assert.False(VulkanRayTracer.SupportsOptions(options));
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => VulkanRayTracer.RequireSupported(options));
        Assert.Contains("cannot skip triangle id 7", e.Message);
    }

    // ------------------------------------------------------------------
    // The kernel
    // ------------------------------------------------------------------

    /// <summary>Every ray query the kernel starts is behind the definedness check.</summary>
    [Fact]
    public void EveryQueryIsGuardedByQueryDefined()
    {
        string code = Kernels.RayGlsl;
        int inits = Regex.Matches(code, @"rayQueryInitializeEXT\(").Count;
        int guards = Regex.Matches(code, @"query_defined\(").Count - 1; // minus the definition

        Assert.Equal(2, inits);
        Assert.Equal(inits, guards);
        Assert.Contains("tmin >= 0.0 && tmax >= tmin", code);
        Assert.Contains("!any(isnan(origin)) && !any(isinf(origin))", code);
        Assert.Contains("!any(isnan(dir)) && !any(isinf(dir))", code);
    }

    /// <summary>The self-test dispatches its two real rays only, never padding lanes as rays.</summary>
    [Fact]
    public void TheSelfTestTracesOnlyItsRealRays()
    {
        VulkanDevice.SelfTestGeometry(out _, out float[] rays, out _);

        Assert.Equal(2, VulkanDevice.SelfTestRealRays);
        Assert.Equal(VulkanDevice.SelfTestTminBits, BitConverter.SingleToUInt32Bits(1e-3f));
        for (int i = 0; i < VulkanDevice.SelfTestRealRays; i++)
        {
            Assert.True(rays[(i * 8) + 7] >= BitConverter.UInt32BitsToSingle(VulkanDevice.SelfTestTminBits),
                $"self-test ray {i} has tmax below tmin");
        }
    }

    /// <summary>
    /// Rays a query is undefined for come back as misses in every mode, next
    /// to a real hit in the same workgroup that still hits.
    /// </summary>
    [VulkanStageFact(VulkanNeed.PassingDevice)]
    public void UndefinedQueriesAreMissesBesideARealHit()
    {
        using VulkanDevice device = new();
        device.Construct(null, -1, 4096, 1);
        device.LoadScene(VulkanDeviceReleaseFacts.Vertices());
        float nan = float.NaN;
        float inf = float.PositiveInfinity;
        // Wire layout: (o, 0), (d, tmax). Ray 0 hits triangle 0 (the z=0
        // one of the release facts' two triangles) at t = 1.
        float[] rays =
        [
            0.25f, 0.25f, 1f, 0f, 0f, 0f, -1f, 2f,          // known hit at t = 1
            0.25f, 0.25f, 1f, 0f, 0f, 0f, -1f, 0.0001f,     // tmax below tmin
            nan, 0.25f, 1f, 0f, 0f, 0f, -1f, 2f,            // NaN origin
            0.25f, 0.25f, 1f, 0f, 0f, 0f, -inf, 2f,         // infinite direction
            0.25f, 0.25f, 1f, 0f, 0f, 0f, -1f, nan,         // NaN tmax
        ];
        const int Count = 5;
        uint[] any = new uint[2];
        uint[] closest = new uint[Count * 2];
        uint[] telemetry = new uint[Count * 2];

        device.DispatchWithStagedRays(0, Count, rays, any, VulkanDevice.SelfTestTminBits, VulkanDevice.TmaxScaleBits);
        device.DispatchWithStagedRays(1, Count, rays, closest, VulkanDevice.SelfTestTminBits, VulkanDevice.TmaxScaleBits);
        device.DispatchWithStagedRays(5, Count, rays, telemetry, VulkanDevice.SelfTestTminBits, VulkanDevice.TmaxScaleBits);

        Assert.Equal(1u, any[0]);
        Assert.Equal(0u, closest[0]);
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(closest[1]), 1e-4f);
        for (int i = 1; i < Count; i++)
        {
            Assert.Equal(0xFFFFFFFFu, closest[i * 2]);
            Assert.Equal(0u, closest[(i * 2) + 1]);
            Assert.Equal(0u, telemetry[i * 2]);
            Assert.Equal(0u, telemetry[(i * 2) + 1]);
        }
    }
}
