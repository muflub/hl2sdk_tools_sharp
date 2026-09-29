//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Silk.NET.Vulkan;

using SourceSharp.MapTools.Gpu.Interop;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// Which memory a device-addressed buffer is allocated from. No device is
/// needed: the rule is a function of the buffer's usage, so these run
/// everywhere, CI included.
/// </summary>
public sealed class VulkanAllocateFlagsTests
{
    [Fact]
    public void ABufferWhoseAddressIsTakenGetsDeviceAddressMemory()
    {
        // The vertex, scratch and instance buffers and the structures'
        // storage all carry this usage; the spec requires the flag for it.
        Assert.Equal(
            MemoryAllocateFlags.DeviceAddressBit,
            VulkanDevice.AllocateFlagsFor(BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit));
    }

    [Fact]
    public void ABufferWithoutDeviceAddressUsageGetsPlainMemory()
    {
        // The slab rays and outputs are only bound as descriptors; they need
        // no address and allocate as before.
        Assert.Equal(
            (MemoryAllocateFlags)0,
            VulkanDevice.AllocateFlagsFor(BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit));
    }
}
