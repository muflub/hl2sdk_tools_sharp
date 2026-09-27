//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The fact classes that open real Vulkan devices. They run one at a time,
/// in parallel with everything else but never with each other.
/// </summary>
/// <remarks>
/// Mesa's lavapipe (llvmpipe) crashes the process when two of its devices
/// are alive at once and one is destroyed while the other dispatches: the
/// surviving device ran JIT-compiled compute code that faulted in an AVX-512
/// gather, reading memory that was gone. Observed as a test host crash in
/// about one run in five while these classes ran side by side, and never
/// with them serialised. The driver is process-wide shared state, so the
/// facts that use it share this collection rather than disabling
/// parallelism for the assembly.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class VulkanDeviceCollection
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "vulkan-device";
}
