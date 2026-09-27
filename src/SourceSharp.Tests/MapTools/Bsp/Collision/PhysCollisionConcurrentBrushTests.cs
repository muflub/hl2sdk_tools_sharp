//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// A model's brush convexes built on several threads (the world's above all):
/// the lump must be the one-brush-at-a-time loop's, byte for byte, at every
/// degree, with shrunk and water-cut brushes, under both compliances.
/// </summary>
public class PhysCollisionConcurrentBrushTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The world: 40 solid boxes, 10 player-clip, 2 monster-clip and 12 water
    /// boxes of which half reach above the surface at z=20; model 1: 16 boxes,
    /// some thinner than the shrink test allows, some with hidden sides;
    /// model 2: three boxes, below the concurrency threshold.
    /// </summary>
    private static PhysCollisionInput Many(ComplianceOptions compliance, int degree, int worldSolids = 40)
    {
        CollisionFixture f = new();
        int metal = f.TexInfoFor("metal");
        int wood = f.TexInfoFor("wood");
        int shell = f.TexInfoFor("shell");
        List<IReadOnlyList<bool>> visible = [];

        void Add(int model, Vec3 lo, Vec3 hi, int contents, int tex, bool[]? sides = null)
        {
            f.Box(model, lo, hi, contents, tex);
            visible.Add(sides ?? [true, true, true, true, true, true]);
        }

        for (int i = 0; i < worldSolids; i++)
        {
            float x = (i % 8) * 70f, y = (i / 8) * 55f, w = 8 + ((i * 7) % 29), d = 4 + ((i * 3) % 17), h = 2 + ((i * 5) % 41);
            Add(0, new Vec3(x, y, -h), new Vec3(x + w, y + d, h * 0.75f), CollisionContents.Solid, (i % 3) switch { 0 => metal, 1 => wood, _ => shell });
        }

        for (int i = 0; i < 10; i++)
        {
            Add(0, new Vec3(-200 - (i * 20), 0, 0), new Vec3(-190 - (i * 20), 16 + i, 64 + i), CollisionContents.PlayerClip, metal);
        }

        for (int i = 0; i < 2; i++)
        {
            Add(0, new Vec3(0, -300 - (i * 40), 0), new Vec3(32, -280 - (i * 40), 48), CollisionContents.MonsterClip, metal);
        }

        for (int i = 0; i < 12; i++)
        {
            float top = i % 2 == 0 ? 20f + (i * 3) : 12f + i * 0.5f;
            Add(0, new Vec3(600 + (i * 40), 0, -30 - i), new Vec3(632 + (i * 40), 30, top), CollisionContents.Water, wood);
        }

        for (int i = 0; i < 16; i++)
        {
            float thin = i % 4 == 0 ? 1f : 8 + i;
            bool[] sides = [true, i % 3 != 0, true, i % 5 != 0, true, true];
            Add(1, new Vec3(i * 20, 0, 0), new Vec3((i * 20) + 12, thin, 24 + i), CollisionContents.Solid, i % 2 == 0 ? metal : shell, sides);
        }

        for (int i = 0; i < 3; i++)
        {
            Add(2, new Vec3(i * 16, 0, 0), new Vec3((i * 16) + 8, 8, 8), CollisionContents.Solid, wood);
        }

        f.Face(1, metal, 400f);
        f.Face(2, wood, 64f);
        f.SideVisible = visible;
        f.Water.Add(new WaterModel(0, CollisionContents.Water, true, new Vec3(0, 0, 1), 20f, 0, [0]));
        return f.Build(3) with { Compliance = compliance, MaxDegree = degree };
    }

    private static async Task<PhysCollisionResult> EmitAsync(PhysCollisionInput input, TaskScheduler? scheduler = null)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(input.Compliance);
        if (scheduler is not null)
        {
            cooker.Scheduler = scheduler;
        }

        return await PhysCollisionEmitter.EmitAsync(input, cooker).WaitAsync(Patience);
    }

    private static void AssertSame(PhysCollisionResult expected, PhysCollisionResult actual)
    {
        Assert.Equal(expected.PhysCollide, actual.PhysCollide);
        Assert.Equal(expected.PhysDisp, actual.PhysDisp);
        Assert.Equal(expected.WorldMaterials, actual.WorldMaterials);
        Assert.Equal(expected.LeafWaterDataIds, actual.LeafWaterDataIds);
        Assert.Equal(expected.Models.Select(m => m.KeyText), actual.Models.Select(m => m.KeyText));
    }

    [Theory]
    [InlineData(CompliancePolicy.Correct, 2)]
    [InlineData(CompliancePolicy.Correct, 3)]
    [InlineData(CompliancePolicy.Correct, 8)]
    [InlineData(CompliancePolicy.Stock, 2)]
    [InlineData(CompliancePolicy.Stock, 8)]
    public async Task TheLumpIsTheOneBrushAtATimeLoopsAtEveryDegree(CompliancePolicy policy, int degree)
    {
        ComplianceOptions compliance = policy == CompliancePolicy.Stock ? ComplianceOptions.Stock : ComplianceOptions.Correct;

        PhysCollisionResult serial = await EmitAsync(Many(compliance, 1));
        PhysCollisionResult parallel = await EmitAsync(Many(compliance, degree));

        AssertSame(serial, parallel);
    }

    [Fact]
    public async Task TheFixtureReallyCutsItsWaterBrushes()
    {
        // Guards the fact above against a fixture that stops exercising the
        // water cut's second build: the fluid's bytes change with it. (Every
        // brush of a brush model takes the shrink test's second build.)
        PhysCollisionResult correct = await EmitAsync(Many(ComplianceOptions.Correct, 4));
        PhysCollisionResult uncut = await EmitAsync(Many(ComplianceOptions.Correct.Flipping(StockQuirk.WaterBrushNotClippedAtSurface), 4));

        Assert.NotEqual(correct.Models[0].Solids[^1], uncut.Models[0].Solids[^1]);
        Assert.Equal(3, correct.Models.Count);
    }

    [Fact]
    public async Task OnTheCompilesPoolTheLumpIsTheSame()
    {
        PhysCollisionResult serial = await EmitAsync(Many(ComplianceOptions.Correct, 1));

        using CompilePool pool = new(3);
        PhysCollisionResult pooled = await EmitAsync(Many(ComplianceOptions.Correct, 3), pool.Scheduler);

        AssertSame(serial, pooled);
    }

    [Fact]
    public async Task AOneThreadPoolDoesNotDeadlock()
    {
        // The cook holds the pool's only thread; its brush helpers can never
        // start, so the cook must build every brush itself rather than wait.
        PhysCollisionResult serial = await EmitAsync(Many(ComplianceOptions.Correct, 1));

        using CompilePool pool = new(1);
        PhysCollisionResult pooled = await EmitAsync(Many(ComplianceOptions.Correct, 8), pool.Scheduler);

        AssertSame(serial, pooled);
    }

    [Fact]
    public async Task TheWorldsConvexesKeepTheirBrushOrder()
    {
        // The solid collide's convexes carry their brush numbers as game data;
        // read back, they are the serial loop's, in the serial loop's order.
        PhysCollisionResult serial = await EmitAsync(Many(ComplianceOptions.Correct, 1));
        PhysCollisionResult parallel = await EmitAsync(Many(ComplianceOptions.Correct, 8));

        List<uint> expected = await GameDataAsync(serial.Models[0].Solids[0]);
        List<uint> actual = await GameDataAsync(parallel.Models[0].Solids[0]);

        Assert.Equal(expected, actual);
        Assert.Equal(Enumerable.Range(0, 40).Select(i => (uint)i).Order(), actual.Order());
    }

    private static async Task<List<uint>> GameDataAsync(byte[] solid)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        return await cooker.RunAsync(s =>
        {
            CollideHandle c = s.UnserializeCollide(solid, 0);
            List<uint> data = [];
            s.WithQueryModel(c, q =>
            {
                for (int i = 0; i < q.ConvexCount; i++)
                {
                    data.Add(q.GetGameData(i));
                }
            });
            s.DestroyCollide(c);
            return data;
        });
    }

    [Fact]
    public async Task EachLargeBrushListIsBuiltConcurrentlyAndSmallOnesAreNot()
    {
        await using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ConcurrentSpyCooker spy = new(managed);

        await PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 4), spy).WaitAsync(Patience);

        // World solids, player clip and water, then model 1; not the two
        // monster-clip brushes nor model 2's three.
        Assert.Equal(
            [(10, 4), (12, 4), (16, 4), (40, 4)],
            spy.Calls.Order());
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 1)]
    public async Task TheThresholdIsEightBrushes(int solids, int calls)
    {
        Assert.Equal(8, PhysCollisionEmitter.MinConcurrentBrushes);
        CollisionFixture f = new();
        int metal = f.TexInfoFor("metal");
        for (int i = 0; i < solids; i++)
        {
            f.Box(0, new Vec3(i * 20, 0, 0), new Vec3((i * 20) + 10, 10, 10), CollisionContents.Solid, metal);
        }

        await using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ConcurrentSpyCooker spy = new(managed);
        await PhysCollisionEmitter.EmitAsync(f.Build() with { MaxDegree = 4 }, spy).WaitAsync(Patience);

        Assert.Equal(calls, spy.Calls.Count);
    }

    [Fact]
    public async Task OneThreadNeverBuildsConcurrently()
    {
        await using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        ConcurrentSpyCooker spy = new(managed);

        await PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 1), spy).WaitAsync(Patience);

        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task ASessionWithoutTheCapabilityKeepsTheOneBrushAtATimeLoop()
    {
        // The native library's session is driven exactly as before: the fake,
        // like it, is not concurrent, and sees the same calls in the same order
        // at any degree.
        FakeCollisionCooker one = new();
        FakeCollisionCooker eight = new();
        Assert.IsNotAssignableFrom<IConcurrentConvexSession>(one.Session);

        await PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 1), one);
        await PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 8), eight);

        Assert.Equal(one.Session.PlaneCalls.Count, eight.Session.PlaneCalls.Count);
        for (int i = 0; i < one.Session.PlaneCalls.Count; i++)
        {
            Assert.Equal(one.Session.PlaneCalls[i], eight.Session.PlaneCalls[i]);
        }

        Assert.Equal(0, eight.Session.LiveCollides);
    }

    [Fact]
    public async Task CancellingMidCookStopsTheCompileAndLeavesTheCookerFit()
    {
        using CompilePool pool = new(4);
        await using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        managed.Scheduler = pool.Scheduler;
        using CancellationTokenSource cts = new();
        ConcurrentSpyCooker spy = new(managed)
        {
            BeforeItem = i =>
            {
                if (i == 5)
                {
                    cts.Cancel();
                }
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 4), spy, null, cts.Token).WaitAsync(Patience));

        // The same cooker and pool compile the next map as if nothing happened.
        PhysCollisionResult serial = await EmitAsync(Many(ComplianceOptions.Correct, 1));
        PhysCollisionResult next = await PhysCollisionEmitter.EmitAsync(Many(ComplianceOptions.Correct, 4), managed).WaitAsync(Patience);
        AssertSame(serial, next);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ABrushThatFailsFailsTheCompileTheSameWayAtEveryDegree(int degree)
    {
        // A side whose plane does not exist: the build throws inside a worker.
        PhysCollisionInput good = Many(ComplianceOptions.Correct, degree);
        List<DBrushSide> sides = [.. good.BrushSides];
        DBrushSide broken = sides[6 * 17];
        broken.PlaneNum = 60000;
        sides[6 * 17] = broken;

        using CompilePool pool = new(4);
        await using ManagedCollisionCooker managed = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        managed.Scheduler = pool.Scheduler;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PhysCollisionEmitter.EmitAsync(good with { BrushSides = sides }, managed).WaitAsync(Patience));

        PhysCollisionResult serial = await EmitAsync(Many(ComplianceOptions.Correct, 1));
        PhysCollisionResult next = await PhysCollisionEmitter.EmitAsync(good, managed).WaitAsync(Patience);
        AssertSame(serial, next);
    }
}
