//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools;

/// <summary>
/// No library code takes a raw pointer with <see cref="Unsafe.AsPointer{T}(ref T)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Unsafe.AsPointer</c> turns a managed reference into a pointer without
/// pinning anything. Aimed at a field of a class, or an array element, the
/// pointer goes stale the moment a compacting GC moves the object, and
/// nothing reports it: the native side simply reads whatever now lies at
/// that address. The Vulkan device once handed the driver its acceleration
/// structure geometry this way, from a field; the BLAS build then read
/// garbage whenever a GC ran between taking the address and the call, and
/// the test host crashed in 2 of 25 runs with the ray-tracing
/// facts running side by side (none in 75 runs once the geometry moved to
/// the stack).
/// </para>
/// <para>
/// Every legitimate use has a pinning form that says what it relies on: a
/// local's address with <c>&amp;</c>, or a <c>fixed</c> block for a field or
/// array. So the rule is simply that the call does not appear, checked in
/// the IL of the built assemblies like the file-system seam.
/// </para>
/// </remarks>
public class PinnedPointerRuleTests
{
    public static TheoryData<string> LibraryAssemblies =>
        [typeof(BspFile).Assembly.GetName().Name!, typeof(IFileSystem).Assembly.GetName().Name!,
         typeof(VulkanDevice).Assembly.GetName().Name!];

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void NoLibraryCodeTakesAnUnpinnedPointer(string assemblyName)
    {
        IReadOnlyList<string> offenders = FileSystemSeamTests.ScanCalls(
            Assembly.Load(assemblyName), [], (m, h, _, _) => IsAsPointer(m, h));

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The scan must be able to fail: it finds the call in this class's own
    /// helper, reached through the generic instantiation the compiler emits.
    /// </summary>
    [Fact]
    public void TheScanSeesAnAsPointerCall()
    {
        IReadOnlyList<string> offenders = FileSystemSeamTests.ScanCalls(
            typeof(PinnedPointerRuleTests).Assembly, [], (m, h, _, _) => IsAsPointer(m, h));

        Assert.Contains(offenders, static o => o.Contains(nameof(UnpinnedAddressOfALocal), StringComparison.Ordinal));
    }

    /// <summary>What the scan must flag. Harmless here: the target is a local.</summary>
    private static unsafe nint UnpinnedAddressOfALocal()
    {
        int local = 0;
        return (nint)Unsafe.AsPointer(ref local);
    }

    private static string? IsAsPointer(MetadataReader metadata, EntityHandle handle)
    {
        // A generic method is called through a MethodSpecification that
        // wraps the open method; unwrap it to see the name.
        if (handle.Kind is HandleKind.MethodSpecification)
        {
            handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        if (handle.Kind is not HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind is not HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        return metadata.GetString(type.Namespace) == "System.Runtime.CompilerServices"
            && metadata.GetString(type.Name) == nameof(Unsafe)
            && metadata.GetString(member.Name) == nameof(Unsafe.AsPointer)
                ? "Unsafe.AsPointer"
                : null;
    }
}
