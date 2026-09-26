//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// <c>EmitStaticProps</c> run as parallel
/// passes (plan 3p): hulls cooked and leaves traced into slots, committed in
/// entity order. Stock does one prop at a time, so every fact here is "the
/// same as one at a time".
/// </summary>
public class StaticPropParallelTests
{
    [Fact]
    public async Task EveryDegreeWritesTheSameLump()
    {
        StaticPropLump serial = await EmitManyAsync(1);
        StaticPropLump parallel = await EmitManyAsync(8);

        Assert.Equal(serial.ModelNames, parallel.ModelNames);
        Assert.Equal(serial.LeafEntries, parallel.LeafEntries);
        Assert.Equal(serial.Props.Select(Describe), parallel.Props.Select(Describe));
    }

    [Fact]
    public async Task ThePropsKeepEntityOrderWhenTheirQueriesFinishOutOfOrder()
    {
        // The hull of the FIRST model is the slowest to cook and to trace.
        StaticPropLump lump = await EmitManyAsync(8);

        Assert.Equal(["models/a.mdl", "models/b.mdl", "models/c.mdl"], lump.ModelNames);
        Assert.Equal([0, 1, 2, 0, 1, 2, 0, 1, 2], lump.Props.Select(p => (int)p.PropType));
        Assert.Equal(Enumerable.Range(0, 9).Select(i => new Vec3(-60f + (15f * i), 0f, 0f)), lump.Props.Select(p => p.Origin));
    }

    [Fact]
    public async Task EachPropsLeavesAreItsOwnAtEveryDegree()
    {
        // x < -8 is wholly behind the plane (leaf 1), x > 8 wholly in front
        // (leaf 0), and between them the box straddles it: back leaf first.
        StaticPropLump lump = await EmitManyAsync(8);

        List<ushort[]> perProp = [.. lump.Props.Select(p => lump.LeafEntries.Skip(p.FirstLeaf).Take(p.LeafCount).ToArray())];
        ushort[][] expected = [[1], [1], [1], [1], [1, 0], [0], [0], [0], [0]];
        Assert.Equal(expected, perProp);
    }

    [Fact]
    public async Task AModelsLoadWarningComesAfterTheEarlierPropsOwnWarnings()
    {
        // Stock: prop 0 (a good model, in solid) warns "outside the map"
        // before prop 1's GetCollisionModel warns "Error loading studio model"
        // The loads run first here, so their
        // warnings must wait for their first prop's turn.
        (VbspContext context, _) = await ContextAsync(8, f => StudioFixture.AddModel(f, "models/a.mdl"));
        BspTreeView solid = OneNode() with { LeafContents = [BspTreeView.ContentsSolid, BspTreeView.ContentsSolid] };

        await new StaticPropEmitter(context, new BoxCollision(8f))
            .EmitAsync([Prop("models/a.mdl", 0f), Prop("models/missing.mdl", 0f)], solid);

        Assert.Equal(
            [SurfaceContentDiagnostics.StaticPropOutsideMap, SurfaceContentDiagnostics.StudioModelLoadFailed],
            context.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task AModelFirstNamedLaterWarnsAtItsFirstPropNotAtItsLoad()
    {
        // Three props: missing model, good model in solid, the missing model
        // again. One load warning (the cache remembers the failure.
        //), then the good prop's own warning.
        (VbspContext context, _) = await ContextAsync(8, f => StudioFixture.AddModel(f, "models/a.mdl"));
        BspTreeView solid = OneNode() with { LeafContents = [BspTreeView.ContentsSolid, BspTreeView.ContentsSolid] };

        await new StaticPropEmitter(context, new BoxCollision(8f))
            .EmitAsync([Prop("models/missing.mdl", 0f), Prop("models/a.mdl", 0f), Prop("models/missing.mdl", 0f)], solid);

        Assert.Equal(
            [SurfaceContentDiagnostics.StudioModelLoadFailed, SurfaceContentDiagnostics.StaticPropOutsideMap],
            context.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task ASecondEmitReusesTheHullsAndWarnsNothingAgain()
    {
        (VbspContext context, _) = await ContextAsync(8, _ => { });
        StaticPropEmitter emitter = new(context, new BoxCollision(8f));

        await emitter.EmitAsync([Prop("models/missing.mdl", 0f)], OneNode());
        await emitter.EmitAsync([Prop("models/missing.mdl", 0f)], OneNode());

        Assert.Single(context.Diagnostics);
    }

    [Fact]
    public async Task APrefetchedHullIsCookedOnce()
    {
        (VbspContext context, _) = await ContextAsync(8, f => StudioFixture.AddModel(f, "models/a.mdl"));
        BoxCollision collision = new(8f);
        StaticPropEmitter emitter = new(context, collision);
        List<MapEntity> entities = [Prop("models/a.mdl", 0f), Prop("models/a.mdl", 20f)];

        await emitter.PrefetchAsync(entities);
        StaticPropLump lump = await emitter.EmitAsync(entities, OneNode());

        Assert.Equal(1, collision.Builds);
        Assert.Equal(2, lump.Props.Count);
    }

    [Fact]
    public async Task APrefetchWritesNoWarningUntilItsPropIsEmitted()
    {
        (VbspContext context, _) = await ContextAsync(8, _ => { });
        StaticPropEmitter emitter = new(context, new BoxCollision(8f));
        List<MapEntity> entities = [Prop("models/missing.mdl", 0f)];

        await emitter.PrefetchAsync(entities);
        Assert.Empty(context.Diagnostics);

        await emitter.EmitAsync(entities, OneNode());
        Assert.Equal([SurfaceContentDiagnostics.StudioModelLoadFailed], context.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task APrefetchThatHitsAFatalModelFailsTheEmitNotThePrefetch()
    {
        // A vertex file with the wrong checksum is Error in stock
        //, at the prop's turn.
        (VbspContext context, _) = await ContextAsync(8, f => StudioFixture.AddModel(f, "models/a.mdl", vvdChecksum: 99));
        StaticPropEmitter emitter = new(context, new BoxCollision(8f));
        List<MapEntity> entities = [Prop("models/a.mdl", 0f)];

        await emitter.PrefetchAsync(entities);

        await Assert.ThrowsAsync<MapCompileException>(() => emitter.EmitAsync(entities, OneNode()));
    }

    [Fact]
    public async Task APrefetchAtOneThreadLeavesTheCookingToTheEmit()
    {
        (VbspContext context, _) = await ContextAsync(1, f => StudioFixture.AddModel(f, "models/a.mdl"));
        BoxCollision collision = new(8f);
        StaticPropEmitter emitter = new(context, collision);
        List<MapEntity> entities = [Prop("models/a.mdl", 0f)];

        await emitter.PrefetchAsync(entities);
        Assert.Equal(0, collision.Builds);

        await emitter.EmitAsync(entities, OneNode());
        Assert.Equal(1, collision.Builds);
    }

    [Fact]
    public async Task APropThePrefetchDidNotSeeIsStillEmitted()
    {
        (VbspContext context, _) = await ContextAsync(8, f =>
        {
            StudioFixture.AddModel(f, "models/a.mdl");
            StudioFixture.AddModel(f, "models/b.mdl");
        });
        StaticPropEmitter emitter = new(context, new BoxCollision(8f));

        await emitter.PrefetchAsync([Prop("models/a.mdl", 0f)]);
        StaticPropLump lump = await emitter.EmitAsync([Prop("models/a.mdl", 0f), Prop("models/b.mdl", 20f)], OneNode());

        Assert.Equal(["models/a.mdl", "models/b.mdl"], lump.ModelNames);
    }

    // Nine props over three models, a b c a b c a b c, along x from -60 to 60.
    private static async Task<StaticPropLump> EmitManyAsync(int degree)
    {
        (VbspContext context, _) = await ContextAsync(degree, f =>
        {
            StudioFixture.AddModel(f, "models/a.mdl");
            StudioFixture.AddModel(f, "models/b.mdl");
            StudioFixture.AddModel(f, "models/c.mdl");
        });

        string[] models = ["models/a.mdl", "models/b.mdl", "models/c.mdl"];
        List<MapEntity> entities = [.. Enumerable.Range(0, 9).Select(i => Prop(models[i % 3], -60f + (15f * i)))];

        return await new StaticPropEmitter(context, new SlowCollision()).EmitAsync(entities, OneNode());
    }

    private static string Describe(StaticProp p) =>
        $"{p.PropType} {p.Origin} {p.Angles} {p.FirstLeaf} {p.LeafCount} {p.Flags} {p.Skin} {p.Solid}";

    private static BspTreeView OneNode()
    {
        DNode node = default;
        node.PlaneNum = 0;
        node.Children[0] = -1;
        node.Children[1] = -2;
        return new([node], [new DPlane { Normal = new Vec3(1f, 0f, 0f), Dist = 0f }], [0, 0]);
    }

    private static async Task<(VbspContext, InMemoryFileSystem)> ContextAsync(int degree, Action<InMemoryFileSystem> fill)
    {
        InMemoryFileSystem files = new();
        fill(files);
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        VbspOptions options = VbspOptions.Default with { Compliance = ComplianceOptions.Correct };
        return (new VbspContext(options, new ContentFileSystem([mount]))
        {
            Parallelism = new CompileParallelism { MaxDegree = degree },
        }, files);
    }

    private static MapEntity Prop(string model, float x)
    {
        MapEntity entity = new();
        entity.SetKeyValue("classname", "prop_static");
        entity.SetKeyValue("model", model);
        entity.SetKeyValue("origin", FormattableString.Invariant($"{x} 0 0"));
        entity.SetKeyValue("angles", "0 0 0");
        return entity;
    }

    // A box hull like BoxCollision's, whose cooks and traces finish in the
    // reverse of the order they were asked for: the first model's hull is
    // the slowest, and a trace near the origin is slower than one far away.
    private sealed class SlowCollision : IStaticPropCollision
    {
        private int _cooks;

        public async ValueTask<IStaticPropHull?> BuildHullAsync(IReadOnlyList<Vec3[]> meshes, CancellationToken cancellationToken = default)
        {
            int order = Interlocked.Increment(ref _cooks);
            await Task.Delay(Math.Max(1, 40 - (15 * order)), cancellationToken);
            return new Hull();
        }

        private sealed class Hull : IStaticPropHull
        {
            private const float Half = 8f;

            public ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult((new Vec3(origin.X - Half, origin.Y - Half, origin.Z - Half),
                                      new Vec3(origin.X + Half, origin.Y + Half, origin.Z + Half)));

            public async ValueTask<bool> IntersectsAsync(
                ReadOnlyMemory<(Vec3 Normal, float Dist)> planes, Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Math.Max(1, 30 - (int)Math.Abs(origin.X / 3f)), cancellationToken);
                bool inside = true;
                foreach ((Vec3 n, float d) in planes.ToArray())
                {
                    inside &= Vec3.Dot(n, origin) <= d + Half;
                }

                return inside;
            }
        }
    }
}
