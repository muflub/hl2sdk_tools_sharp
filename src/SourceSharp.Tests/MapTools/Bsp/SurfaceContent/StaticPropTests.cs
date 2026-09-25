using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>, fact by fact.</summary>
public class StaticPropTests
{
    private const string Model = "models/unit/prop.mdl";

    [Fact]
    public async Task APropStraddlingANodeListsTheBackLeafFirst()
    {
        //: back child, then front.
        List<ushort> leaves = await LeavesAsync(OneNode(), new Vec3(0f, 0f, 0f));

        Assert.Equal([1, 0], leaves);
    }

    [Fact]
    public async Task APropWhollyInFrontRecordsTheFlippedPlane()
    {
        // then:322-330.
        BoxCollision collision = new(8f);
        List<ushort> leaves = await LeavesAsync(OneNode(), new Vec3(100f, 0f, 0f), collision);

        Assert.Equal([0], leaves);
        Assert.Equal((new Vec3(-1f, 0f, 0f), -0f), Assert.Single(Assert.Single(collision.Queries)));
    }

    [Fact]
    public async Task ABoxEndingExactlyOnThePlaneIsBehindIt()
    {
        //: <= dist.
        List<ushort> leaves = await LeavesAsync(OneNode(), new Vec3(-8f, 0f, 0f));

        Assert.Equal([1], leaves);
    }

    [Fact]
    public async Task ASolidLeafIsNeverTested()
    {
        BspTreeView tree = OneNode() with { LeafContents = [0, BspTreeView.ContentsSolid] };
        BoxCollision collision = new(8f);

        List<ushort> leaves = await LeavesAsync(tree, new Vec3(0f, 0f, 0f), collision);

        Assert.Equal([0], leaves);
        Assert.Single(collision.Queries);
    }

    [Fact]
    public async Task TheLeafPlanesAreDeepestFirst()
    {
        //: for (i = depth; --i >= 0;).
        BspTreeView tree = new(
            [Node(0, -1, 1), Node(1, -2, -3)],
            [Plane(1, 0, 0, 0), Plane(0, 1, 0, 0)],
            [0, 0, 0]);
        BoxCollision collision = new(1f);

        await LeavesAsync(tree, new Vec3(-50f, -50f, 0f), collision);

        (Vec3 Normal, float Dist)[] planes = Assert.Single(collision.Queries);
        Assert.Equal(new Vec3(0f, 1f, 0f), planes[0].Normal);
        Assert.Equal(new Vec3(1f, 0f, 0f), planes[1].Normal);
    }

    [Fact]
    public async Task APropIsEmittedWithItsKeysAndItsEntityIsCleared()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));
        MapEntity prop = Prop(("origin", "10 20 30"), ("angles", "1 2 3"), ("skin", "2"), ("solid", "2"));
        List<MapEntity> entities = [prop];

        StaticPropLump lump = await Emitter(context).EmitAsync(entities, OneNode());

        StaticProp p = Assert.Single(lump.Props);
        Assert.Equal(new Vec3(10f, 20f, 30f), p.Origin);
        Assert.Equal(new Vec3(1f, 2f, 3f), p.Angles);
        Assert.Equal(2, p.Skin);
        Assert.Equal(2, p.Solid);
        Assert.Equal([Model], lump.ModelNames);
        Assert.Empty(prop.Pairs);
    }

    [Fact]
    public void ANegativeFadeMinTakesTheMax()
    {
        StaticPropBuild build = StaticPropEmitter.ReadBuild(Prop(("fademindist", "-1"), ("fademaxdist", "400")));

        Assert.Equal(400f, build.FadeMinDist);
        Assert.True(build.FadesOut);
    }

    [Fact]
    public void NoFadeMaxMeansNoFadeAtAll()
    {
        StaticPropBuild build = StaticPropEmitter.ReadBuild(Prop(("fademindist", "300"), ("fademaxdist", "0")));

        Assert.Equal(0f, build.FadeMinDist);
        Assert.False(build.FadesOut);
    }

    [Fact]
    public void EachBooleanKeySetsItsFlagOnlyWhenOne()
    {
        //: == 1, so "2" sets nothing.
        StaticPropBuild build = StaticPropEmitter.ReadBuild(Prop(
            ("ignorenormals", "1"), ("disableshadows", "1"), ("disablevertexlighting", "1"),
            ("disableselfshadowing", "1"), ("screenspacefade", "2"), ("generatelightmaps", "1")));

        Assert.Equal(
            StaticPropFlags.IgnoreNormals | StaticPropFlags.NoShadow | StaticPropFlags.NoPerVertexLighting | StaticPropFlags.NoSelfShadowing,
            build.Flags);
    }

    [Fact]
    public void WithoutGenerateLightmapsThereIsNoPerTexelLightingAndNoResolution()
    {
        StaticPropBuild build = StaticPropEmitter.ReadBuild(Prop(("lightmapresolutionx", "32")));

        Assert.Equal(StaticPropFlags.NoPerTexelLighting, build.Flags);
        Assert.Equal(0, build.LightmapResolutionX);
    }

    [Fact]
    public void AnEmptyFadeScaleIsOne()
    {
        //: an ABSENT or empty key is 1, "0" is 0.
        Assert.Equal(1f, StaticPropEmitter.ReadBuild(Prop()).ForcedFadeScale);
        Assert.Equal(0f, StaticPropEmitter.ReadBuild(Prop(("fadescale", "0"))).ForcedFadeScale);
    }

    [Fact]
    public void DxLevelsAreTruncatedToSixteenBits()
    {
        Assert.Equal(1, StaticPropEmitter.ReadBuild(Prop(("mindxlevel", "65537"))).MinDxLevel);
    }

    [Fact]
    public async Task TheFadesFlagIsAddedWhenTheMaxIsPositive()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop(("fademaxdist", "10"))], OneNode());

        Assert.True(lump.Props[0].Flags.HasFlag(StaticPropFlags.Fades));
    }

    [Fact]
    public async Task AMatchingInfoLightingSetsTheLightingOriginAndIsCleared()
    {
        //,513-519,675-679.
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));
        MapEntity lighting = Entity(("classname", "info_lighting"), ("targetname", "l"), ("origin", "1 2 3"));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop(("lightingorigin", "l")), lighting], OneNode());

        Assert.Equal(new Vec3(1f, 2f, 3f), lump.Props[0].LightingOrigin);
        Assert.True(lump.Props[0].Flags.HasFlag(StaticPropFlags.UseLightingOrigin));
        Assert.Empty(lighting.Pairs);
    }

    [Fact]
    public async Task TheLastMatchingInfoLightingWins()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));

        StaticPropLump lump = await Emitter(context).EmitAsync(
            [Prop(("lightingorigin", "l")),
             Entity(("classname", "info_lighting"), ("targetname", "l"), ("origin", "1 1 1")),
             Entity(("classname", "info_lighting"), ("targetname", "l"), ("origin", "2 2 2"))],
            OneNode());

        Assert.Equal(new Vec3(2f, 2f, 2f), lump.Props[0].LightingOrigin);
    }

    [Fact]
    public async Task ALightingOriginNamingNothingLeavesTheFlagClear()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop(("lightingorigin", "nobody"))], OneNode());

        Assert.False(lump.Props[0].Flags.HasFlag(StaticPropFlags.UseLightingOrigin));
    }

    [Fact]
    public async Task TwoPropsOfOneModelShareADictionaryEntryAndOneHull()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));
        BoxCollision collision = new(8f);

        StaticPropLump lump = await new StaticPropEmitter(context, collision).EmitAsync([Prop(), Prop()], OneNode());

        Assert.Single(lump.ModelNames);
        Assert.Equal(1, collision.Builds);
        Assert.Equal((ushort)2, lump.Props[1].FirstLeaf);
    }

    [Fact]
    public async Task TwoSpellingsOfOneModelShareAHullButNotADictionaryEntry()
    {
        // The hull cache lower-cases:248-257); the dictionary memcmp's:138).
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));
        BoxCollision collision = new(8f);

        StaticPropLump lump = await new StaticPropEmitter(context, collision)
            .EmitAsync([Prop(), Prop(("model", "MODELS/unit/PROP.mdl"))], OneNode());

        Assert.Equal(2, lump.ModelNames.Count);
        Assert.Equal(1, collision.Builds);
    }

    [Fact]
    public async Task AMissingModelIsWarnedOnceAndItsPropsDropped()
    {
        //, the failure cached.
        (VbspContext context, _) = await ContextAsync(_ => { });

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop(), Prop()], OneNode());

        Assert.Empty(lump.Props);
        Assert.Single(context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.StudioModelLoadFailed);
    }

    [Fact]
    public async Task AModelWithoutTheStaticPropFlagIsRefused()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model, flags: 0));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Empty(lump.Props);
        Assert.Contains(context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.StudioModelNotStaticProp);
    }

    [Fact]
    public async Task PropDataWithoutAllowStaticIsRefused()
    {
        (VbspContext context, _) = await ContextAsync(
            f => StudioFixture.AddModel(f, Model, keyValues: "mdlkeyvalue\n{\n\tprop_data\n\t{\n\t\tbase \"Metal.Small\"\n\t}\n}\n"));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Empty(lump.Props);
        Assert.Contains(context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.StudioModelDynamicOnly);
    }

    [Fact]
    public async Task PropDataWithAllowStaticIsAccepted()
    {
        (VbspContext context, _) = await ContextAsync(
            f => StudioFixture.AddModel(f, Model, keyValues: "mdlkeyvalue\n{\n\tprop_data\n\t{\n\t\tallowstatic 1\n\t}\n}\n"));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Single(lump.Props);
    }

    [Fact]
    public async Task AVersion44ModelLoads()
    {
        // slams the version before
        // tests it. HL2's own props are version 44.
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model, version: 44));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Single(lump.Props);
        Assert.DoesNotContain(context.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task UnderStockAVersion37ModelIsSlammedAndRead()
    {
        //: any version becomes 48.
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model, version: 37), ComplianceOptions.Stock);

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Single(lump.Props);
    }

    [Fact]
    public async Task AVersion37ModelIsRefusedWhenTheQuirkIsCorrected()
    {
        // StockQuirk.StudioVersionSlam, flipped alone.
        (VbspContext context, _) = await ContextAsync(
            f => StudioFixture.AddModel(f, Model, version: 37), ComplianceOptions.Stock.Flipping(StockQuirk.StudioVersionSlam));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Empty(lump.Props);
        Assert.Contains(context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.StudioModelLoadFailed);
    }

    [Fact]
    public async Task AVersion49ModelIsRefusedWhenCorrected()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model, version: 49));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], OneNode());

        Assert.Empty(lump.Props);
    }

    [Fact]
    public async Task AVertexFileWithTheWrongChecksumIsFatal()
    {
        //, Error.
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model, vvdChecksum: 99));

        await Assert.ThrowsAsync<MapCompileException>(() => Emitter(context).EmitAsync([Prop()], OneNode()));
    }

    [Fact]
    public async Task APropInNoLeafIsWarnedAndDropped()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));
        BspTreeView tree = OneNode() with { LeafContents = [BspTreeView.ContentsSolid, BspTreeView.ContentsSolid] };

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop()], tree);

        Assert.Empty(lump.Props);
        Assert.Contains(context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.StaticPropOutsideMap);
    }

    [Fact]
    public async Task StaticPropIsTheOldSpellingAndIsEmittedToo()
    {
        (VbspContext context, _) = await ContextAsync(f => StudioFixture.AddModel(f, Model));

        StaticPropLump lump = await Emitter(context).EmitAsync([Prop(("classname", "static_prop"))], OneNode());

        Assert.Single(lump.Props);
    }

    [Fact]
    public async Task TheManagedHullSeesALeafThePointsReach()
    {
        IStaticPropHull hull = (await new ManagedStaticPropCollision().BuildHullAsync([Cube(8f)]))!;

        Assert.True(await hull.IntersectsAsync(new[] { (new Vec3(1f, 0f, 0f), 0f) }, new Vec3(4f, 0f, 0f), Vec3.Zero));
    }

    [Fact]
    public async Task TheManagedHullMissesALeafThePointsDoNotReach()
    {
        IStaticPropHull hull = (await new ManagedStaticPropCollision().BuildHullAsync([Cube(8f)]))!;

        Assert.False(await hull.IntersectsAsync(new[] { (new Vec3(1f, 0f, 0f), 0f) }, new Vec3(9f, 0f, 0f), Vec3.Zero));
    }

    [Fact]
    public async Task TheManagedHullIsRotatedByTheAngles()
    {
        // A 16x2x2 bar along X, yawed 90 degrees, reaches y = 8 and not x = 8.
        IStaticPropHull hull = (await new ManagedStaticPropCollision().BuildHullAsync([Bar()]))!;

        (Vec3 mins, Vec3 maxs) = await hull.GetAabbAsync(Vec3.Zero, new Vec3(0f, 90f, 0f));

        Assert.InRange(maxs.Y, 7.99f, 8.01f);
        Assert.InRange(maxs.X, 0.99f, 1.01f);
    }

    [Fact]
    public async Task AModelWithNoUsableMeshHasNoHull()
    {
        Assert.Null(await new ManagedStaticPropCollision().BuildHullAsync([[Vec3.Zero, Vec3.Zero, Vec3.Zero]]));
    }

    private static Vec3[] Cube(float h) =>
    [
        new(-h, -h, -h), new(h, -h, -h), new(-h, h, -h), new(h, h, -h),
        new(-h, -h, h), new(h, -h, h), new(-h, h, h), new(h, h, h),
    ];

    private static Vec3[] Bar() =>
    [
        new(-8, -1, -1), new(8, -1, -1), new(-8, 1, -1), new(8, 1, -1),
        new(-8, -1, 1), new(8, -1, 1), new(-8, 1, 1), new(8, 1, 1),
    ];

    private static async Task<List<ushort>> LeavesAsync(BspTreeView tree, Vec3 origin, BoxCollision? collision = null)
    {
        IStaticPropHull hull = (await (collision ?? new BoxCollision(8f)).BuildHullAsync([]))!;
        return await StaticPropLeaves.ComputeAsync(tree, hull, origin, Vec3.Zero);
    }

    // One node on x = 0: front child leaf 0, back child leaf 1.
    private static BspTreeView OneNode() => new([Node(0, -1, -2)], [Plane(1, 0, 0, 0)], [0, 0]);

    private static DNode Node(int plane, int front, int back)
    {
        DNode node = default;
        node.PlaneNum = plane;
        node.Children[0] = front;
        node.Children[1] = back;
        return node;
    }

    private static DPlane Plane(float x, float y, float z, float d) => new() { Normal = new Vec3(x, y, z), Dist = d };

    private static StaticPropEmitter Emitter(VbspContext context) => new(context, new BoxCollision(8f));

    private static async Task<(VbspContext, InMemoryFileSystem)> ContextAsync(Action<InMemoryFileSystem> fill, ComplianceOptions? compliance = null)
    {
        InMemoryFileSystem files = new();
        fill(files);
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        VbspOptions options = VbspOptions.Default with { Compliance = compliance ?? ComplianceOptions.Correct };
        return (new VbspContext(options, new ContentFileSystem([mount])), files);
    }

    private static MapEntity Prop(params (string Key, string Value)[] keys)
    {
        MapEntity entity = Entity(("classname", "prop_static"), ("model", Model), ("origin", "0 0 0"), ("angles", "0 0 0"));
        foreach ((string key, string value) in keys)
        {
            entity.SetKeyValue(key, value);
        }

        return entity;
    }

    private static MapEntity Entity(params (string Key, string Value)[] keys)
    {
        MapEntity entity = new();
        foreach ((string key, string value) in keys)
        {
            entity.SetKeyValue(key, value);
        }

        return entity;
    }
}
