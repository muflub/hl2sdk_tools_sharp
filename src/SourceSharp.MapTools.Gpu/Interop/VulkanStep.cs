//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// Points inside <see cref="VulkanDevice"/> where it holds a temporary native
/// object, reported to an observer so facts can fail the work right there
/// and watch the object be released.
/// </summary>
/// <remarks>
/// Every failure these points exist for is a driver refusal a fact cannot
/// provoke on a working device; throwing from the observer stands in for it.
/// Production code passes no observer.
/// </remarks>
internal enum VulkanStep
{
    /// <summary>The probe's instance exists and the devices are about to be enumerated.</summary>
    ProbeInstanceCreated,

    /// <summary>The probe's instance was destroyed.</summary>
    ProbeInstanceDestroyed,

    /// <summary>The probe released the Vulkan API it loaded; reported last, however the probe ended.</summary>
    ProbeApiReleased,

    /// <summary>The kernel's shader module and entry-point name exist; the pipeline is not built yet.</summary>
    ShaderModuleCreated,

    /// <summary>The shader module and the entry-point name were released.</summary>
    ShaderModuleReleased,

    /// <summary>The scene upload's staging buffer is allocated and not yet copied from.</summary>
    StagingAllocated,

    /// <summary>The BLAS build's scratch buffer is allocated and the build not yet submitted.</summary>
    ScratchAllocated,

    /// <summary>
    /// The TLAS's instance, storage and scratch buffers are allocated (after
    /// the BLAS's) and neither build is submitted yet.
    /// </summary>
    TopLevelScratchAllocated,

    /// <summary>
    /// One slab slot's buffers are allocated; its command buffer and fence
    /// are not made yet. Reported once per slot, in slot order, so a fact can
    /// fail the ring part way through any slot.
    /// </summary>
    SlotBuffersAllocated,
}
