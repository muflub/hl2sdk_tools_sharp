//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// A 3D skybox from outside the lit map (<see cref="VradSkybox"/>): its
/// casters moved and appended after the map's own, its props renumbered,
/// its cameras joining the map's in no area.
/// </summary>
public sealed class VradSkyboxTests
{
    private static ShadowCasterSet Set(params (int Id, Vec3 A, Vec3 B, Vec3 C)[] triangles)
    {
        ShadowCasterBuilder builder = new();
        foreach ((int id, Vec3 a, Vec3 b, Vec3 c) in triangles)
        {
            builder.AddTriangle(id, a, b, c, 0.5f, TracedTriangle.Transparent, materialIndex: 7);
        }

        return builder.Build();
    }

    /// <summary>
    /// The map's own triangles come first and unchanged; each skybox
    /// triangle follows, every vertex moved (offset, then the quarter turn
    /// into the room's frame), its coverage and flags kept, its material
    /// dropped, and a static prop's index moved past the map's props while
    /// its tag bits stay; brush and sky triangles keep their ids.
    /// </summary>
    [Fact]
    public void TheSkyboxsCastersFollowTheMapsMovedAndRenumbered()
    {
        ShadowCasterSet own = Set((TraceId.Opaque, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)), (TraceId.StaticProp | 2, new(2, 0, 0), new(0, 2, 0), new(0, 0, 2)));
        ShadowCasterSet sky = Set(
            (TraceId.Sky, new(10, 0, 0), new(0, 10, 0), new(0, 0, 10)),
            (TraceId.StaticProp | 1, new(5, 6, 7), new(8, 9, 10), new(11, 12, 13)));
        VradSkybox skybox = new(new BspData(), new Vec3(0, 0, -256), 1, [], propIndexBase: 3);

        ShadowCasterSet both = skybox.AppendTo(own, sky);
        Assert.Equal(4, both.Count);
        Assert.Equal(own.Triangles[0], both.Triangles[0]);
        Assert.Equal(own.Triangles[1], both.Triangles[1]);
        Assert.Equal([7, 7, -1, -1], both.MaterialIndices.ToArray());
        Assert.Equal([0.5f, 0.5f, 0.5f, 0.5f], both.Coverage.ToArray());

        // One quarter turn into the room's frame: (x, y, z) to (y, -x, z).
        Assert.Equal(new TracedTriangle(TraceId.Sky, new(0, -10, -256), new(10, 0, -256), new(0, 0, -246), TracedTriangle.Transparent), both.Triangles[2]);
        Assert.Equal(TraceId.StaticProp | 4, both.Triangles[3].Id);
        Assert.Equal(new Vec3(6, -5, -249), both.Triangles[3].V0);
        Assert.Equal(skybox.Move(new Vec3(5, 6, 7)), both.Triangles[3].V0);
    }

    /// <summary>A skybox prop renumbered past what a trace id holds is refused, not wrapped into a tag bit.</summary>
    [Fact]
    public void APropIndexPastTheTraceIdsBitsIsRefused()
    {
        ShadowCasterSet sky = Set((TraceId.StaticProp | 1, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)));
        VradSkybox skybox = new(new BspData(), Vec3.Zero, 0, [], propIndexBase: TraceId.PropIndexMask);
        Assert.Throws<InvalidOperationException>(() => skybox.AppendTo(Set(), sky));
    }

    /// <summary>
    /// Cameras from outside the map join after the map's own and stand in
    /// no area, so no area's slot names them; none leaves the table as it
    /// is; a camera claiming an area of the map is refused.
    /// </summary>
    [Fact]
    public void OutsideCamerasJoinTheMapsInNoArea()
    {
        Assert.Same(SkyCameras.None, SkyCameras.None.WithOutside([]));
        SkyCamera outside = new(new Vec3(1, 2, 3), 16, 1f / 16, -1);
        SkyCameras joined = SkyCameras.None.WithOutside([outside]);
        Assert.Equal([outside], joined.Cameras.ToArray());
        Assert.Equal(0, joined.AreaCount);
        Assert.Equal(-1, joined.CameraInArea(0));
        Assert.Throws<ArgumentException>(() => SkyCameras.None.WithOutside([outside with { Area = 1 }]));
    }
}
