using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// Manifests and cordons, on files built in memory.
/// </summary>
public class MapManifestTests
{
    private const string User = "unituser";

    /// <summary>
    /// <c>LoadSubMaps</c> fabricates one worldspawn and one
    /// <c>func_instance</c> per sub-map, in manifest order, all at the origin
    /// with <c>fixup_style 2</c> (<c>manifest.cpp:291-345</c>).
    /// </summary>
    [Fact]
    public async Task EachSubMapBecomesAFuncInstanceInManifestOrder()
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", """
            "Maps"
            {
                "VMF" { "File" "room_a.vmf"  "TopLevel" "1" }
                "VMF" { "File" "room_b.vmf" }
            }
            """);

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        Assert.Equal(["room_a.vmf", "room_b.vmf"], manifest.SubMaps);
        Assert.Equal(3, manifest.Map.Entities.Count);
        Assert.Equal("worldspawn", manifest.Map.Entities[0].ValueForKey("classname"));

        for (int i = 1; i <= 2; i++)
        {
            Assert.Equal(FuncInstance.ClassName, manifest.Map.Entities[i].ValueForKey("classname"));
            Assert.Equal("0 0 0", manifest.Map.Entities[i].ValueForKey("angles"));
            Assert.Equal(
                MapInstanceMerger.NameFixupNone,
                manifest.Map.Entities[i].IntForKey("fixup_style"));
        }

        Assert.Equal("room_a.vmf", manifest.Map.Entities[1].ValueForKey("file"));
        Assert.Equal("room_b.vmf", manifest.Map.Entities[2].ValueForKey("file"));
    }

    /// <summary>
    /// The sub-VMFs live in a directory named after the manifest, next to it
    /// (<c>manifest.cpp:423-424</c>).
    /// </summary>
    [Fact]
    public async Task TheInstanceDirectoryIsTheManifestsNameWithoutItsExtension()
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", "\"Maps\"\n{\n}\n");

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        Assert.Equal("maps/test/", manifest.InstanceDirectory);
    }

    [Fact]
    public async Task WithNoPrefsFileNothingIsCordoned()
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", "\"Maps\"\n{\n}\n");

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        Assert.False(manifest.IsCordoning);
        Assert.Empty(manifest.Cordons);
    }

    /// <summary>
    /// <c>ReadKeyValueBool</c> is <c>atoi(value) &gt; 0</c>
    /// (<c>chunkfile.cpp:636</c>), so the literal <c>"true"</c> reads as FALSE.
    /// Hammer writes <c>"1"</c>.
    /// </summary>
    [Fact]
    public async Task TheWordTrueDoesNotSwitchCordoningOn()
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", "\"Maps\"\n{\n}\n");
        files.AddText($"maps/test/{User}.vmm_prefs", """
            "cordoning"
            {
                "cordons"
                {
                    "active" "true"
                }
            }
            """);

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        Assert.False(manifest.IsCordoning);
    }

    [Fact]
    public async Task TheCordonVolumesAreReadAsParenthesisedPoints()
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", "\"Maps\"\n{\n}\n");
        files.AddText($"maps/test/{User}.vmm_prefs", Prefs("1", "1", "(-64 -64 -64)", "(64 64 64)"));

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        Assert.True(manifest.IsCordoning);
        MapCordon cordon = Assert.Single(manifest.Cordons);
        Assert.True(cordon.Active);
        (Vec3 mins, Vec3 maxs) = Assert.Single(cordon.Boxes);
        Assert.Equal(new Vec3(-64f, -64f, -64f), mins);
        Assert.Equal(new Vec3(64f, 64f, 64f), maxs);
    }

    /// <summary>
    /// <c>BoundBox::IsIntersectingBox</c> (<c>boundbox.cpp:141</c>) is STRICT,
    /// so a world brush whose face merely touches the cordon's face does not
    /// intersect it and is culled.
    /// </summary>
    [Fact]
    public async Task AWorldBrushTouchingTheCordonFaceIsCulled()
    {
        MapManifest manifest = await CordonedAsync(
            worldBrush: ((64f, 0f, 0f), (128f, 64f, 64f)),
            entityOrigin: null);

        manifest.CordonWorld();

        Assert.Equal(0, manifest.Map.Entities[0].BrushCount);
    }

    [Fact]
    public async Task AWorldBrushOverlappingTheCordonIsKept()
    {
        MapManifest manifest = await CordonedAsync(
            worldBrush: ((32f, 0f, 0f), (128f, 64f, 64f)),
            entityOrigin: null);

        manifest.CordonWorld();

        Assert.Equal(1, manifest.Map.Entities[0].BrushCount);
    }

    /// <summary>
    /// <c>BoundBox::ContainsPoint</c> (<c>boundbox.cpp:122</c>) is INCLUSIVE,
    /// the opposite of the box test, so an entity whose origin sits exactly on
    /// a cordon face survives.
    /// </summary>
    [Fact]
    public async Task AnEntityWithItsOriginOnTheCordonFaceIsKept()
    {
        MapManifest manifest = await CordonedAsync(
            worldBrush: null,
            entityOrigin: new Vec3(64f, 0f, 0f));

        manifest.CordonWorld();

        Assert.NotEmpty(manifest.Map.Entities[^1].Pairs);
    }

    [Fact]
    public async Task AnEntityWithItsOriginOutsideTheCordonIsBlanked()
    {
        MapManifest manifest = await CordonedAsync(
            worldBrush: null,
            entityOrigin: new Vec3(64.5f, 0f, 0f));

        manifest.CordonWorld();

        Assert.Empty(manifest.Map.Entities[^1].Pairs);
    }

    /// <summary>
    /// With cordoning on and no ACTIVE cordon the inner loops never clear the
    /// remove flag, so everything goes (<c>manifest.cpp:487-520</c>). Stock
    /// does not guard against it and neither does this.
    /// </summary>
    [Fact]
    public async Task AnInactiveCordonCullsTheWholeMap()
    {
        MapManifest manifest = await CordonedAsync(
            worldBrush: ((0f, 0f, 0f), (32f, 32f, 32f)),
            entityOrigin: null,
            cordonActive: "0");

        manifest.CordonWorld();

        Assert.Equal(0, manifest.Map.Entities[0].BrushCount);
    }

    private static string Prefs(string master, string cordon, string mins, string maxs) =>
        $$"""
        "cordoning"
        {
            "cordons"
            {
                "active" "{{master}}"
                "cordon"
                {
                    "name"   "unit"
                    "active" "{{cordon}}"
                    "box"
                    {
                        "mins" "{{mins}}"
                        "maxs" "{{maxs}}"
                    }
                }
            }
        }
        """;

    private static async Task<(VbspContext Context, InMemoryFileSystem Files)> SetupAsync()
    {
        VbspContext context = await UnitMap.ContextAsync();
        return (context, new InMemoryFileSystem());
    }

    private static async Task<MapManifest> CordonedAsync(
        ((float X, float Y, float Z) Mins, (float X, float Y, float Z) Maxs)? worldBrush,
        Vec3? entityOrigin,
        string cordonActive = "1")
    {
        (VbspContext context, InMemoryFileSystem files) = await SetupAsync();
        files.AddText("maps/test.vmm", "\"Maps\"\n{\n}\n");
        files.AddText(
            $"maps/test/{User}.vmm_prefs",
            Prefs("1", cordonActive, "(-64 -64 -64)", "(64 64 64)"));

        MapManifest manifest = await MapManifest.LoadAsync(
            context, files, VPath.Create("maps/test.vmm"), User);

        if (worldBrush is { } box)
        {
            VmfDocument document = UnitMap.BoxMap(UnitMap.Plain, box.Mins, box.Maxs);
            MapFile loaded = await MapFileLoader.LoadAsync(context, document);

            // Fold the loaded brush into the manifest's own worldspawn, which
            // is where the instance pipeline would have put it.
            manifest.Map.Brushes.AddRange(loaded.Brushes);
            manifest.Map.Entities[0].BrushCount = loaded.Brushes.Count;
        }

        if (entityOrigin is { } origin)
        {
            MapEntity entity = new() { Origin = origin, FirstBrush = manifest.Map.BrushCount };
            entity.SetKeyValue("classname", "info_player_start");
            manifest.Map.Entities.Add(entity);
        }

        return manifest;
    }
}
