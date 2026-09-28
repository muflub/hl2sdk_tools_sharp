//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Silk.NET.Vulkan;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// The two fence calls <see cref="VulkanDevice"/> makes to learn whether
/// the device has finished with a slab or a set-up submit:
/// <c>vkWaitForFences</c> and <c>vkResetFences</c>.
/// </summary>
/// <remarks>
/// <para>
/// A seam for facts, not a plug-in point. What the device does when a fence
/// does not signal (a slot that stays in flight and is refused until it
/// does, a dispose that gives up after its bound and abandons the device
/// rather than free memory the GPU may still read) cannot be provoked on a
/// working device, and a hung GPU is not something a test can order. A fact
/// replaces <see cref="VulkanDevice.FenceWaits"/> with a fence that never
/// signals, and later hands the real calls back so the device is released.
/// </para>
/// <para>
/// Production code never sets it: the device's own implementation calls the
/// driver.
/// </para>
/// </remarks>
internal interface IFenceWaits
{
    /// <summary>Waits until every fence in <paramref name="fences"/> has signalled, or the timeout passes.</summary>
    /// <param name="fences">The fences, all of them waited for.</param>
    /// <param name="timeoutNs">How long to wait, in nanoseconds.</param>
    /// <returns><c>Success</c>, <c>Timeout</c>, or the driver's error.</returns>
    Result Wait(ReadOnlySpan<Fence> fences, ulong timeoutNs);

    /// <summary>Returns a signalled fence to unsignalled so it can guard the next submit.</summary>
    /// <param name="fence">The fence.</param>
    /// <returns><c>Success</c> or the driver's error.</returns>
    Result Reset(Fence fence);
}
