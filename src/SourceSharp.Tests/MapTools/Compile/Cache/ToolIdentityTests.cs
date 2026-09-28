//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Reflection;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="ToolIdentity"/> names the code, not only the version: the
/// module version ids of MapTools and MapFormats are folded in, so a build
/// with changed code never reads an older build's rows under the same
/// version string.
/// </summary>
public sealed class ToolIdentityTests
{
    private static readonly Assembly MapTools = typeof(ToolIdentity).Assembly;
    private static readonly Assembly MapFormats = typeof(Vec3).Assembly;

    [Fact]
    public void TheIdentityCarriesTheCodeDigestOfMapToolsAndMapFormats() =>
        Assert.EndsWith(" code " + ToolIdentity.CodeDigest([MapTools, MapFormats]), ToolIdentity.Current, StringComparison.Ordinal);

    [Fact]
    public void AChangeToEitherAssemblyChangesTheDigest()
    {
        // Another assembly in either place stands for that assembly rebuilt
        // from other code: another module version id.
        string both = ToolIdentity.CodeDigest([MapTools, MapFormats]);
        Assembly other = typeof(ToolIdentityTests).Assembly;

        Assert.NotEqual(both, ToolIdentity.CodeDigest([other, MapFormats]));
        Assert.NotEqual(both, ToolIdentity.CodeDigest([MapTools, other]));
    }

    [Fact]
    public void TheDigestFollowsTheModuleVersionIdNotOnlyTheName()
    {
        // Two assemblies with one name and version but different code differ
        // only in their MVID; the digest's pre-image carries it.
        string digest = ToolIdentity.CodeDigest([MapTools]);
        string byName = CacheKey.HashComponents([$"{MapTools.FullName}=no-mvid"])[..16];

        Assert.NotEqual(byName, digest);
        Assert.Equal(
            CacheKey.HashComponents(
                [$"{MapTools.FullName}={MapTools.ManifestModule.ModuleVersionId:N}"])[..16],
            digest);
    }

    [Fact]
    public void TheDigestIgnoresOrder() =>
        Assert.Equal(ToolIdentity.CodeDigest([MapTools, MapFormats]), ToolIdentity.CodeDigest([MapFormats, MapTools]));

    [Fact]
    public void TheDigestIsSixteenHexCharacters() =>
        Assert.Matches("^[0-9a-f]{16}$", ToolIdentity.CodeDigest([MapTools]));
}
