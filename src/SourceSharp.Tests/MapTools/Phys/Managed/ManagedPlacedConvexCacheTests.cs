//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// A session makes a collide's placed convexes once per placement and reuses
/// them (<see cref="ManagedCollisionSession.PlacedConvexes"/>): the answers
/// are the ones a fresh build gives, placements are told apart bit for bit,
/// and a destroyed collide takes its entries with it.
/// </summary>
public sealed class ManagedPlacedConvexCacheTests
{
    [Fact]
    public async Task TheSamePlacementIsBuiltOnceAndDifferentOnesAreNot()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ManagedCollisionSession s = (ManagedCollisionSession)cooker.OpenSession();
        CollideHandle box = Box(s, 16);

        var first = s.PlacedConvexes(box, new Vec3(1, 2, 3), new Vec3(0, 90, 0));
        var again = s.PlacedConvexes(box, new Vec3(1, 2, 3), new Vec3(0, 90, 0));
        var moved = s.PlacedConvexes(box, new Vec3(1, 2, 4), new Vec3(0, 90, 0));
        var signed = s.PlacedConvexes(box, new Vec3(1, 2, 3), new Vec3(-0f, 90, 0));

        Assert.Same(first, again);
        Assert.NotSame(first, moved);
        Assert.NotSame(first, signed);
    }

    [Fact]
    public async Task ADestroyedCollidesPlacementsAreDropped()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ManagedCollisionSession s = (ManagedCollisionSession)cooker.OpenSession();
        CollideHandle box = Box(s, 16);
        CollideHandle other = Box(s, 8);
        var kept = s.PlacedConvexes(other, Vec3.Zero, new Vec3(0, 45, 0));
        s.PlacedConvexes(box, Vec3.Zero, new Vec3(0, 45, 0));

        s.DestroyCollide(box);

        Assert.Throws<ArgumentException>(() => s.PlacedConvexes(box, Vec3.Zero, new Vec3(0, 45, 0)));
        Assert.Same(kept, s.PlacedConvexes(other, Vec3.Zero, new Vec3(0, 45, 0)));
    }

    /// <summary>
    /// Many overlap tests against one placed collide in one session (a
    /// prop's leaf walk) answer as the same tests do each in a session of
    /// its own, where nothing can be reused.
    /// </summary>
    [Fact]
    public async Task RepeatedOverlapTestsAnswerAsFreshSessionsDo()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ManagedCollisionSession shared = (ManagedCollisionSession)cooker.OpenSession();
        byte[] prop = shared.CollideWrite(Box(shared, 24));
        CollideHandle placed = shared.UnserializeCollide(prop, 0);
        Vec3 origin = new(100, -50, 8);
        Vec3 angles = new(10, 33, -5);

        (Vec3 Mins, Vec3 Maxs) box = shared.CollideGetAABB(placed, origin, angles);
        Random random = new(5);
        int hits = 0;
        for (int i = 0; i < 60; i++)
        {
            Vec3 centre = new(
                origin.X + ((float)random.NextDouble() * 80) - 40,
                origin.Y + ((float)random.NextDouble() * 80) - 40,
                origin.Z + ((float)random.NextDouble() * 80) - 40);
            CollisionPlane[] leaf = Cube(centre, 6 + ((float)random.NextDouble() * 20));

            bool reused = Overlap(shared, placed, leaf, origin, angles);

            ManagedCollisionSession fresh = (ManagedCollisionSession)cooker.OpenSession();
            CollideHandle freshProp = fresh.UnserializeCollide(prop, 0);
            Assert.Equal(box, fresh.CollideGetAABB(freshProp, origin, angles));
            Assert.Equal(Overlap(fresh, freshProp, leaf, origin, angles), reused);
            hits += reused ? 1 : 0;
        }

        Assert.InRange(hits, 5, 55);
    }

    private static bool Overlap(ICollisionSession s, CollideHandle prop, CollisionPlane[] leaf, Vec3 origin, Vec3 angles)
    {
        ConvexHandle convex = s.ConvexFromPlanes(leaf, 0f);
        CollideHandle leafCollide = s.ConvertConvexToCollide([convex]);
        bool overlap = s.TraceCollide(Vec3.Zero, Vec3.Zero, leafCollide, Vec3.Zero, prop, origin, angles).StartSolid;
        s.DestroyCollide(leafCollide);
        return overlap;
    }

    private static CollideHandle Box(ICollisionSession s, float half) =>
        s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube(Vec3.Zero, half), 0f)]);

    private static CollisionPlane[] Cube(Vec3 centre, float half) =>
    [
        new(new Vec3(1, 0, 0), centre.X + half),
        new(new Vec3(-1, 0, 0), -(centre.X - half)),
        new(new Vec3(0, 1, 0), centre.Y + half),
        new(new Vec3(0, -1, 0), -(centre.Y - half)),
        new(new Vec3(0, 0, 1), centre.Z + half),
        new(new Vec3(0, 0, -1), -(centre.Z - half)),
    ];
}
