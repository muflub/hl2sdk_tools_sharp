//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// Facts about the displacement manager that need a real compiled map: the
/// hashes' contents, the per-leaf ray clip, and the face job's routing.
/// </summary>
public sealed class DispCorpusTests
{
    [StockDispFact]
    public async Task TheSampleHashHoldsEveryLitFacesSamplesOnce()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_grid_mixed");
        int expected = world.FaceLights.Where(f => f is not null).Sum(f => f!.Samples.Length);
        Assert.Equal(expected, world.DisplacementHash!.Samples.Entries);
    }

    [StockDispFact]
    public async Task ASampleIsFiledUnderTheVoxelOfItsPosition()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_grid_mixed");
        FaceLight fl = world.FaceLights.First(f => f is not null && world.Geometry.Faces[f.FaceNum].DispInfo != -1)!;
        Vec3 p = fl.Samples[17].Position;
        Assert.Contains(new SampleHandle(fl.FaceNum, 17), world.DisplacementHash!.Samples.Find(VoxelKey.Of(p.X, p.Y, p.Z)).ToArray());
    }

    [StockDispFact]
    public async Task ThePatchHashHoldsEveryLeafPatchWhenLightBounces()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_grid_mixed");
        int leaves = world.Patches.AsSpan().ToArray().Count(p => !p.HasChildren);
        Assert.Equal(leaves, world.DisplacementHash!.Patches.Entries);
    }

    [StockDispFact]
    public async Task ThePatchHashIsEmptyWithoutBounce()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_grid_mixed", VradOptions.Default with { Bounces = 0 });
        Assert.Equal(0, world.DisplacementHash!.Patches.Entries);
    }

    [StockDispFact]
    public async Task ARayDownOntoTheDisplacementClipsToItsFace()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_p2_flat");
        VradDispSurface d = world.Displacements.Surfaces[0]!;
        Vec3 centre = (d.Tree.Mins + d.Tree.Maxs) * 0.5f;
        Vec3 start = centre + new Vec3(0, 0, 64);
        Vec3 delta = new(0, 0, -128);
        int leaf = world.Tree.LeafFromPoint(centre);

        DispRayTestState state = new(world.Displacements.Count);
        state.StartRayTest();
        (float dist, int face, _, Vec3 normal) = world.Displacements.Leaves.ClipRayToDispInLeaf(state, start, delta, leaf, true);

        Assert.Equal(d.ParentFace, face);
        Assert.InRange(dist, 0.0f, 1.0f);
        Assert.True(Vec3.Dot(normal, new Vec3(0, 0, 1)) != 0.0f);
    }

    [StockDispFact]
    public async Task EveryDisplacementIsFiledInSomeLeaf()
    {
        RadWorld world = await StockDispVrad.LightAsync("p3f_grid_mixed");
        for (int d = 0; d < world.Displacements.Count; d++)
        {
            bool found = false;
            for (int leaf = 0; leaf < world.Geometry.Leaves.Length && !found; leaf++)
            {
                found = world.Displacements.Leaves.InLeaf(leaf).Contains(d);
            }

            Assert.True(found, $"displacement {d} is in no leaf");
        }
    }

    [StockDispFact]
    public async Task WithoutADisplacementManagerTheFaceIsDeferred()
    {
        // The context the p4c tests build has no displacements: the face is
        // flagged, as it was before lane 4e, and nothing throws.
        RadWorld world = await StockDispVrad.LightAsync("p3f_p2_flat");
        FaceLightContext ctx = new(world.Geometry, world.Neighbours, world.Patches, world.Tree, world.Settings, world.Gatherer);
        int face = world.Displacements.Surfaces[0]!.ParentFace;
        FaceLightJob job = new(ctx, face);
        job.Prepare(new WindingArena());
        Assert.True(job.Result!.IsDisplacementDeferred);
    }

    [StockDispFact]
    public async Task ADisplacementFaceIsNotSupersampled()
    {
        //: one round -- the direct gather -- and done.
        RadWorld world = await StockDispVrad.LightAsync("p3f_p2_flat");
        FaceLightContext ctx = new(
            world.Geometry, world.Neighbours, world.Patches, world.Tree, world.Settings, world.Gatherer, world.Displacements);
        int face = world.Displacements.Surfaces[0]!.ParentFace;
        FaceLightJob job = new(ctx, face);
        job.Prepare(new WindingArena());

        IRayTracer tracer = StockRadWorld.Tracer(world.Bsp, hdr: false);
        while (!job.Done)
        {
            job.RunRound();
            Ray[] vis = job.Rays.VisibilityRays().ToArray();
            Ray[] sky = job.Rays.SkyRays().ToArray();
            HitId[] visHits = new HitId[vis.Length];
            HitId[] skyHits = new HitId[sky.Length];
            await tracer.TraceClosestAsync(vis, visHits, RayTraceOptions.StockExact);
            await tracer.TraceClosestAsync(sky, skyHits, RayTraceOptions.StockExact);
            ulong[] bits = new ulong[(vis.Length + 63) / 64];
            for (int i = 0; i < vis.Length; i++)
            {
                if (LightRayLog.IsBlocking(visHits[i]))
                {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }

            job.Rays.BeginReplay(bits, 0, skyHits, 0);
            job.RunRound();
        }

        Assert.Equal(1, job.RoundsCompleted);
    }
}
