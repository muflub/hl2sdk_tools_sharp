//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

using PortalNode = SourceSharp.MapTools.Bsp.Portals.BspNode;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// <see cref="VisClusterVolumes"/>: which viscluster, if any, claims a leaf.
/// </summary>
/// <remarks>
/// Every leaf here is the cube (0,0,0)-(100,100,100), a million cubic units,
/// so a viscluster slab <c>x</c> units thick covers <c>x</c> percent of it.
/// The viscluster brushes are world brushes of a loaded map, added by brush
/// range the way the loader adds an entity's.
/// </remarks>
public sealed class VisClusterVolumesTests
{
    private static readonly Vec3 LeafMins = new(0f, 0f, 0f);
    private static readonly Vec3 LeafMaxs = new(100f, 100f, 100f);

    private static async Task<(BspBuildContext Build, MapFile Map)> LoadAsync(
        params ((float, float, float) Mins, (float, float, float) Maxs)[] boxes)
    {
        return await CsgFixture.LoadAsync(CsgFixture.World(
            [.. boxes.Select(b => (UnitMap.Plain, b.Mins, b.Maxs))]));
    }

    private static BspNode Leaf(BspBuildContext build, Vec3 mins, Vec3 maxs) => new()
    {
        PlaneNumber = BspNode.Leaf,
        Volume = BrushGeometry.BrushFromBounds(build, mins, maxs),
        Mins = mins,
        Maxs = maxs,
    };

    // One viscluster per map brush, in brush order.
    private static async Task<(VisClusterVolumes Volumes, BspNode Leaf)> OnePerBrushAsync(
        params ((float, float, float) Mins, (float, float, float) Maxs)[] boxes)
    {
        (BspBuildContext build, MapFile map) = await LoadAsync(boxes);
        VisClusterVolumes volumes = new();
        for (int i = 0; i < map.BrushCount; i++)
        {
            volumes.Add(build, i, 1);
        }

        return (volumes, Leaf(build, LeafMins, LeafMaxs));
    }

    [Fact]
    public async Task ALeafInsideAVisClusterIsClaimed()
    {
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(((-10, -10, -10), (110, 110, 110)));

        Assert.Equal(0, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task AVisClusterCoveringATwentiethOfALeafDoesNotClaimIt()
    {
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(((-10, -10, -10), (5, 110, 110)));

        Assert.Equal(-1, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task AVisClusterCoveringAFifthOfALeafClaimsIt()
    {
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(((-10, -10, -10), (20, 110, 110)));

        Assert.Equal(0, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task AVisClusterThatMissesTheLeafsBoxDoesNotClaimIt()
    {
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(((200, 200, 200), (300, 300, 300)));

        Assert.Equal(-1, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task OfTwoQualifyingVisClustersTheLowerNumberedWinsNotTheLarger()
    {
        // Viscluster 0 covers half the leaf, viscluster 1 all of it. The
        // threshold is not raised when one qualifies, so the walk from the
        // last to the first ends on 0.
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(
            ((-10, -10, -10), (50, 110, 110)),
            ((-10, -10, -10), (110, 110, 110)));

        Assert.Equal(0, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task AVisClustersBrushesAreSummed()
    {
        // Two brushes of ONE viscluster, 6% each and apart: 12% together.
        (BspBuildContext build, _) = await LoadAsync(
            ((-10, -10, -10), (6, 110, 110)),
            ((94, -10, -10), (110, 110, 110)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 2);

        Assert.Equal(0, volumes.GetVisCluster(Leaf(build, LeafMins, LeafMaxs)));
    }

    [Fact]
    public async Task AVisClustersOverlappingBrushesAreNotCountedTwice()
    {
        // Two identical 6% brushes of one viscluster are chopped to one
        // brush, so the cover is 6% and not 12%.
        (BspBuildContext build, _) = await LoadAsync(
            ((-10, -10, -10), (6, 110, 110)),
            ((-10, -10, -10), (6, 110, 110)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 2);

        Assert.Equal(-1, volumes.GetVisCluster(Leaf(build, LeafMins, LeafMaxs)));
    }

    [Fact]
    public async Task AddReturnsTheNewIndexAndKeepsTheChoppedList()
    {
        (BspBuildContext build, _) = await LoadAsync(
            ((0, 0, 0), (10, 10, 10)),
            ((20, 20, 20), (30, 30, 30)));
        VisClusterVolumes volumes = new();

        Assert.Equal(
            (0, 1, 2, true),
            (volumes.Add(build, 0, 1), volumes.Add(build, 1, 1), volumes.Count, volumes.Volume(1) is not null));
    }

    [Fact]
    public async Task AVisClusterWithNoBrushesHasNoVolumeAndClaimsNothing()
    {
        (BspBuildContext build, _) = await LoadAsync(((0, 0, 0), (10, 10, 10)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 0);

        Assert.Equal(
            (true, -1),
            (volumes.Volume(0) is null, volumes.GetVisCluster(Leaf(build, LeafMins, LeafMaxs))));
    }

    [Fact]
    public async Task NoVisClustersClaimNothing()
    {
        (BspBuildContext build, _) = await LoadAsync(((0, 0, 0), (10, 10, 10)));

        Assert.Equal(-1, new VisClusterVolumes().GetVisCluster(Leaf(build, LeafMins, LeafMaxs)));
    }

    [Fact]
    public async Task ALeafWithNoVolumeBrushIsNotClaimed()
    {
        (VisClusterVolumes volumes, _) = await OnePerBrushAsync(((-10, -10, -10), (110, 110, 110)));
        BspNode leaf = new() { PlaneNumber = BspNode.Leaf, Mins = LeafMins, Maxs = LeafMaxs };

        Assert.Equal(-1, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task ALeafThatIsNotABrushBspNodeIsNotClaimed()
    {
        (VisClusterVolumes volumes, _) = await OnePerBrushAsync(((-10, -10, -10), (110, 110, 110)));
        PortalNode leaf = new(1) { PlaneNumber = -1, Mins = LeafMins, Maxs = LeafMaxs };

        Assert.Equal(-1, volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task AskingWithNoContextThrows()
    {
        (VisClusterVolumes volumes, BspNode leaf) = await OnePerBrushAsync(((-10, -10, -10), (110, 110, 110)));
        volumes.Context = null;

        Assert.Throws<InvalidOperationException>(() => volumes.GetVisCluster(leaf));
    }

    [Fact]
    public async Task TheFirstAddSetsTheContextAndALaterOneKeepsIt()
    {
        (BspBuildContext build, MapFile map) = await LoadAsync(
            ((0, 0, 0), (10, 10, 10)),
            ((20, 20, 20), (30, 30, 30)));
        BspBuildContext other = new(build.Compile, map);
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 1);
        volumes.Add(other, 1, 1);

        Assert.Same(build, volumes.Context);
    }

    [Fact]
    public async Task VolumeOfIntersectionIsZeroForALeafWithNoVolume()
    {
        (BspBuildContext build, MapFile map) = await LoadAsync(((-10, -10, -10), (110, 110, 110)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 1);
        BspNode leaf = new() { PlaneNumber = BspNode.Leaf, Mins = LeafMins, Maxs = LeafMaxs };

        Assert.Equal(0f, VisClusterVolumes.VolumeOfIntersection(build, volumes.Volume(0), leaf));
        _ = map;
    }

    [Fact]
    public async Task VolumeOfIntersectionIsTheSharedVolume()
    {
        (BspBuildContext build, _) = await LoadAsync(((-10, -10, -10), (25, 110, 110)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 1);

        float shared = VisClusterVolumes.VolumeOfIntersection(
            build, volumes.Volume(0), Leaf(build, LeafMins, LeafMaxs));

        Assert.Equal(250_000f, shared, 1f);
    }

    [Fact]
    public async Task ABrushOnlyTouchingTheLeafsBoxAddsNothing()
    {
        // The box test is inclusive, so the carve runs, and finds nothing.
        (BspBuildContext build, _) = await LoadAsync(((100, 0, 0), (150, 100, 100)));
        VisClusterVolumes volumes = new();
        volumes.Add(build, 0, 1);

        float shared = VisClusterVolumes.VolumeOfIntersection(
            build, volumes.Volume(0), Leaf(build, LeafMins, LeafMaxs));

        Assert.Equal(0f, shared);
    }
}
