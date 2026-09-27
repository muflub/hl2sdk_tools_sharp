//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>How much of a Vulkan stack a fact needs.</summary>
public enum VulkanNeed
{
    /// <summary>A loader: the API can be loaded and an instance asked for; nothing is selected.</summary>
    Loader,

    /// <summary>A device exposing ray query, which can be opened, built on and self-tested (llvmpipe will do).</summary>
    RayQueryDevice,

    /// <summary>A device that passes the tracer's self-test, so the real scene is loaded.</summary>
    PassingDevice,
}

/// <summary>
/// Why a fact needing a <see cref="VulkanNeed"/> cannot run here, probed
/// once per need for the run.
/// </summary>
/// <remarks>
/// The attributes read this when xUnit reads their <c>Skip</c>, not in their
/// constructors: a requirement set as an attribute property would still be
/// unset in the constructor, and the skip would never happen.
/// </remarks>
internal static class VulkanNeeds
{
    private static readonly Lazy<string?> LoaderSkip = new(() =>
    {
        IReadOnlyList<VulkanDeviceInfo> rows = VulkanRayTracer.ProbeDevices();
        return rows.FirstOrDefault(r => r.Index < 0) is { Name: { } } missing && rows.Count == 1
            ? $"no usable Vulkan loader: {missing.Name} {missing.DeviceType}"
            : null;
    });

    private static readonly Lazy<string?> RayQuerySkip = new(() =>
    {
        IReadOnlyList<VulkanDeviceInfo> rows = VulkanRayTracer.ProbeDevices();
        return rows.Any(r => r.RayQuery)
            ? null
            : "no Vulkan device exposes VK_KHR_ray_query (found: " + string.Join("; ", rows.Select(r => r.Name)) + ")";
    });

    private static readonly Lazy<string?> PassingSkip = new(() =>
    {
        VulkanTracerAttempt attempt = VulkanRayTracer.TryCreate(VulkanRayTracerReleaseFacts.TwoTriangles());
        using VulkanRayTracer? tracer = attempt.Tracer;
        return attempt.Success
            ? null
            : "no Vulkan device here passes the self-test: "
              + (attempt.Report.Selected?.Reason ?? attempt.Report.Failure);
    });

    /// <summary>The skip reason for <paramref name="need"/>, or null when the machine has it.</summary>
    /// <param name="need">What the fact needs.</param>
    /// <returns>The reason, prefixed for the runner, or null.</returns>
    public static string? SkipFor(VulkanNeed need)
    {
        string? why = need switch
        {
            VulkanNeed.Loader => LoaderSkip.Value,
            VulkanNeed.RayQueryDevice => LoaderSkip.Value ?? RayQuerySkip.Value,
            _ => LoaderSkip.Value ?? RayQuerySkip.Value ?? PassingSkip.Value,
        };
        return why is null ? null : "skipped: " + why;
    }
}

/// <summary>A fact that skips, with the probe's reason, unless the machine has the <see cref="VulkanNeed"/>.</summary>
/// <param name="need">What the fact needs.</param>
internal sealed class VulkanStageFactAttribute(VulkanNeed need) : FactAttribute
{
    /// <inheritdoc/>
    public override string? Skip
    {
        get => base.Skip ?? VulkanNeeds.SkipFor(need);
        set => base.Skip = value;
    }
}

/// <summary>A theory that skips, with the probe's reason, unless the machine has the <see cref="VulkanNeed"/>.</summary>
/// <param name="need">What the theory needs.</param>
internal sealed class VulkanStageTheoryAttribute(VulkanNeed need) : TheoryAttribute
{
    /// <inheritdoc/>
    public override string? Skip
    {
        get => base.Skip ?? VulkanNeeds.SkipFor(need);
        set => base.Skip = value;
    }
}
