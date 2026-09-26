//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// <c>KdRayTracer.TestLines</c>' <c>skip_id</c>,
/// which static-prop lighting uses so a <c>NO_SELF_SHADOWING</c> prop does not
/// shadow itself.
/// </summary>
public sealed class TestLinesSkipIdTests
{
    private const int PropId = 0x04000000 | 7;

    // A large triangle in the z = 0 plane, between (0,0,10) and (0,0,-10).
    private static readonly KdRayTracer Tracer = KdRayTracer.Build(
    [
        new TracedTriangle(PropId, new Vec3(-100, -100, 0), new Vec3(100, -100, 0), new Vec3(0, 100, 0), 0),
    ]);

    private static bool Blocked(int skipId)
    {
        Span<bool> blocked = [false];
        Tracer.TestLines([new Vec3(0, 0, 10)], [new Vec3(0, 0, -10)], blocked, stockReciprocal: true, skipId: skipId);
        return blocked[0];
    }

    [Fact]
    public void WithoutASkipIdTheTriangleBlocks()
    {
        Assert.True(Blocked(-1));
    }

    [Fact]
    public void TheSkippedIdDoesNotBlock()
    {
        Assert.False(Blocked(PropId));
    }

    [Fact]
    public void AnotherPropsIdStillBlocks()
    {
        Assert.True(Blocked(PropId + 1));
    }
}
