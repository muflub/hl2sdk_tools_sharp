//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Gpu;

/// <summary>
/// The points a <see cref="VulkanRayTracer.TryCreate(ReadOnlyMemory{Tracing.TracedTriangle}, Action{TryCreateStage}?, VulkanRayTracerOptions, CancellationToken)"/>
/// attempt reports to its observer, in the order it reaches them.
/// </summary>
/// <remarks>
/// The attempt owns a native device from <see cref="Opened"/> until it either
/// hands the device to a tracer or reports <see cref="Released"/>. The stages
/// exist so facts can fail an attempt at each point that holds the device
/// (by throwing from the observer) and prove the release happens exactly
/// once, whatever was thrown; production callers pass no observer.
/// </remarks>
internal enum TryCreateStage
{
    /// <summary>The Vulkan API is loaded and the attempt owns a device object; nothing is selected yet.</summary>
    Opened,

    /// <summary>The instance and logical device exist, and the pipeline and staging buffers are built.</summary>
    Constructed,

    /// <summary>The known-hit self-test has run, before its verdict is acted on.</summary>
    SelfTested,

    /// <summary>The real scene's BLAS is built, just before a tracer takes the device.</summary>
    SceneLoaded,

    /// <summary>The device was disposed because no tracer took it; reported after the disposal.</summary>
    Released,
}
