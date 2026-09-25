using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The entity model's key rules, which decide the entity lump's byte order.
/// </summary>
public class MapEntityTests
{
    [Fact]
    public void SetKeyValuePrependsANewKey()
    {
        MapEntity entity = new();

        entity.SetKeyValue("first", "1");
        entity.SetKeyValue("second", "2");

        Assert.Equal("second", entity.Pairs[0].Key);
        Assert.Equal("first", entity.Pairs[1].Key);
    }

    [Fact]
    public void SetKeyValueReplacesInPlaceWithoutMovingThePair()
    {
        MapEntity entity = new();
        entity.SetKeyValue("first", "1");
        entity.SetKeyValue("second", "2");

        entity.SetKeyValue("FIRST", "changed");

        Assert.Equal(2, entity.Pairs.Count);
        Assert.Equal("first", entity.Pairs[1].Key);
        Assert.Equal("changed", entity.Pairs[1].Value);
    }

    [Fact]
    public void AddKeyValueAppendsAndAllowsDuplicates()
    {
        MapEntity entity = new();

        entity.AddKeyValue("OnTrigger", "a,Use,,0,-1");
        entity.AddKeyValue("OnTrigger", "b,Use,,0,-1");

        Assert.Equal(2, entity.Pairs.Count);
        Assert.Equal("a,Use,,0,-1", entity.Pairs[0].Value);
    }

    [Fact]
    public void ValueForKeyIsTheEmptyStringWhenTheKeyIsAbsent()
    {
        MapEntity entity = new();

        Assert.Equal(string.Empty, entity.ValueForKey("nothing"));
        Assert.False(entity.HasKey("nothing"));
    }

    /// <summary>
    /// <c>GetVectorForKey</c> is a bare <c>sscanf("%lf %lf %lf")</c>, not the
    /// bracketed <c>ReadKeyValueVector3</c>.
    /// </summary>
    [Fact]
    public void GetVectorForKeyTakesThreeBareNumbers()
    {
        MapEntity entity = new();
        entity.SetKeyValue("origin", "-16 32.5 -8");

        Assert.Equal(new Vec3(-16f, 32.5f, -8f), entity.GetVectorForKey("origin"));
    }

    [Fact]
    public void GetVectorForKeyLeavesMissingFieldsAtZero()
    {
        MapEntity entity = new();
        entity.SetKeyValue("origin", "10 20");

        Assert.Equal(new Vec3(10f, 20f, 0f), entity.GetVectorForKey("origin"));
    }

    /// <summary>
    /// <c>sscanf</c> stops at the first field it cannot convert, so everything
    /// after a junk field is left at zero even when it would have parsed.
    /// </summary>
    [Fact]
    public void GetVectorForKeyStopsAtTheFirstUnparseableField()
    {
        MapEntity entity = new();
        entity.SetKeyValue("origin", "10 x 30");

        Assert.Equal(new Vec3(10f, 0f, 0f), entity.GetVectorForKey("origin"));
    }

    [Fact]
    public void ClearBlanksTheEntityWithoutRemovingIt()
    {
        MapEntity entity = new() { BrushCount = 3 };
        entity.SetKeyValue("classname", "func_instance");

        entity.Clear();

        Assert.Equal(0, entity.BrushCount);
        Assert.Empty(entity.Pairs);
    }
}

/// <summary>
/// <c>-replacematerials</c>'s lookup order and slash handling.
/// </summary>
public class MaterialReplacementsTests
{
    private const string Config = """
        "MaterialReplacements"
        {
            "AllMaps"
            {
                "tools/toolsnodraw"  "tools/toolsskip"
                "dev/devblue"        "dev/devgreen"
            }
            "unit"
            {
                "tools/toolsnodraw"  "tools/toolsblack"
                "dev/devred"         "dev/devorange"
            }
        }
        """;

    [Fact]
    public void AnUnknownNameComesBackUnchanged()
    {
        MaterialReplacements replacements = new();

        Assert.Equal("nature/blendrock", replacements.Replace("nature/blendrock"));
    }

    /// <summary>
    /// <c>materialsub.cpp:61</c> chains the map-specific section as the
    /// FALLBACK of <c>AllMaps</c>, so AllMaps is searched first and wins — the
    /// opposite of "more specific wins".
    /// </summary>
    [Fact]
    public async Task AllMapsBeatsThePerMapSection()
    {
        MaterialReplacements replacements = await LoadAsync();

        Assert.Equal("tools/toolsskip", replacements.Replace("tools/toolsnodraw"));
    }

    [Fact]
    public async Task ThePerMapSectionStillAppliesToNamesAllMapsDoesNotMention()
    {
        MaterialReplacements replacements = await LoadAsync();

        Assert.Equal("dev/devorange", replacements.Replace("dev/devred"));
    }

    /// <summary>
    /// The stock tool runs the name through <c>Q_FixSlashes</c> before the
    /// lookup, so a config written for it has backslashes in its keys. Both
    /// spellings resolve here, because that config is the only kind there is.
    /// </summary>
    [Fact]
    public async Task ABackslashedNameResolvesAgainstForwardSlashedKeys()
    {
        MaterialReplacements replacements = await LoadAsync();

        Assert.Equal("dev/devgreen", replacements.Replace(@"dev\devblue"));
    }

    private static async Task<MaterialReplacements> LoadAsync()
    {
        KeyValuesDocument document = await KeyValuesDocument.ParseAsync(Config);
        return new MaterialReplacements(document, "unit");
    }
}

/// <summary>
/// The five matrix operations instance merging needs.
/// </summary>
public class InstanceTransformTests
{
    [Fact]
    public void ZeroAnglesGiveTheIdentityRotationAndTheGivenTranslation()
    {
        InstanceTransform transform =
            InstanceTransform.FromAngles(Vec3.Zero, new Vec3(10f, 20f, 30f));

        Assert.Equal(new Vec3(11f, 22f, 33f), transform.TransformPoint(new Vec3(1f, 2f, 3f)));
        Assert.Equal(new Vec3(1f, 2f, 3f), transform.RotateVector(new Vec3(1f, 2f, 3f)));
    }

    /// <summary>
    /// A <c>QAngle</c> is pitch, yaw, roll — so the SECOND component is the
    /// yaw, and a 90-degree yaw sends +X to +Y.
    /// </summary>
    [Fact]
    public void TheSecondAngleComponentIsTheYaw()
    {
        InstanceTransform transform =
            InstanceTransform.FromAngles(new Vec3(0f, 90f, 0f), Vec3.Zero);

        Vec3 rotated = transform.RotateVector(new Vec3(1f, 0f, 0f));

        Assert.Equal(0f, rotated.X, 5);
        Assert.Equal(1f, rotated.Y, 5);
    }

    /// <summary>
    /// <c>TransformAABB</c> produces a BOUND, not a rotated box: a unit cube
    /// yawed 45 degrees comes back wider than it went in.
    /// </summary>
    [Fact]
    public void TransformBoundsGrowsARotatedBox()
    {
        InstanceTransform transform =
            InstanceTransform.FromAngles(new Vec3(0f, 45f, 0f), Vec3.Zero);

        (Vec3 mins, Vec3 maxs) = transform.TransformBounds(
            new Vec3(-1f, -1f, -1f), new Vec3(1f, 1f, 1f));

        Assert.Equal(MathF.Sqrt(2f), maxs.X, 4);
        Assert.Equal(-MathF.Sqrt(2f), mins.X, 4);
        Assert.Equal(1f, maxs.Z, 5);
    }

    /// <summary>
    /// A transformed plane still passes through the transformed image of a
    /// point that was on it — which is the property the instance merge needs
    /// and the one a wrong translation term would break.
    /// </summary>
    [Fact]
    public void TransformPlaneKeepsThePlaneThroughItsTransformedPoints()
    {
        InstanceTransform transform =
            InstanceTransform.FromAngles(new Vec3(0f, 30f, 0f), new Vec3(5f, -7f, 2f));

        Plane plane = new(new Vec3(0f, 0f, 1f), 64f);
        Plane moved = transform.TransformPlane(plane);

        Vec3 onPlane = new(12f, -3f, 64f);
        Vec3 movedPoint = transform.TransformPoint(onPlane);

        Assert.Equal(0f, moved.DistanceTo(movedPoint), 3);
    }
}

/// <summary>
/// The blend-material patch's name, shader and stripped variables.
/// </summary>
public class WorldVertexTransitionFixupTests
{
    [Fact]
    public void ThePatchedNameIsLowercasedAndSlashNormalised()
    {
        string name = WorldVertexTransitionFixup.PatchedMaterialName(
            @"Nature\BlendRock001", "Unit_Map");

        Assert.Equal("maps/unit_map/nature/blendrock001_wvt_patch", name);
    }

    [Fact]
    public void AnOverlongPatchedNameIsRejected()
    {
        Assert.Throws<SourceSharp.MapTools.Diagnostics.MapCompileException>(
            () => WorldVertexTransitionFixup.PatchedMaterialName(new string('a', 200), "unit"));
    }

    [Theory]
    [InlineData("WorldVertexTransition", true)]
    [InlineData("LightmappedGeneric_WorldVertexTransition", true)]
    [InlineData("worldvertextransition_dx8", true)]
    [InlineData("LightmappedGeneric", false)]
    [InlineData(null, false)]
    public void IsBlendShaderIsACaseInsensitiveSubstringTest(string? shader, bool expected) =>
        Assert.Equal(expected, WorldVertexTransitionFixup.IsBlendShader(shader));

    [Fact]
    public async Task ThePatchedMaterialIsLightmappedGenericWithTheSecondTextureStripped()
    {
        KeyValuesDocument document = await KeyValuesDocument.ParseAsync("""
            "WorldVertexTransition"
            {
                "$basetexture"          "nature/rock"
                "$basetexture2"         "nature/sand"
                "$bumpmap2"             "nature/sand_normal"
                "$blendmodulatetexture" "nature/blendmask"
                "$surfaceprop2"         "sand"
                "$envmap"               "env_cubemap"
            }
            """);

        KeyValuesNode patched = WorldVertexTransitionFixup.PatchMaterial(document.Root!);

        Assert.Equal("LightmappedGeneric", patched.Name);
        Assert.Equal("nature/rock", patched.GetString("$basetexture"));
        Assert.All(
            WorldVertexTransitionFixup.StrippedVariables,
            variable => Assert.Null(patched.Find(variable)));
    }

    /// <summary>
    /// <c>$envmap</c> goes only when <c>$basetexturenoenvmap</c> is non-zero.
    /// Stock reads that variable through a <c>FindKey</c> that can return null
    /// and calls <c>GetInt</c> on it unguarded; an absent variable reads as
    /// zero, so the key is KEPT.
    /// </summary>
    [Fact]
    public async Task EnvmapSurvivesWhenBasetexturenoenvmapIsAbsent()
    {
        KeyValuesDocument document = await KeyValuesDocument.ParseAsync("""
            "WorldVertexTransition"
            {
                "$envmap" "env_cubemap"
            }
            """);

        KeyValuesNode patched = WorldVertexTransitionFixup.PatchMaterial(document.Root!);

        Assert.NotNull(patched.Find("$envmap"));
    }

    [Fact]
    public async Task EnvmapGoesWhenBasetexturenoenvmapIsSet()
    {
        KeyValuesDocument document = await KeyValuesDocument.ParseAsync("""
            "WorldVertexTransition"
            {
                "$envmap"                "env_cubemap"
                "$basetexturenoenvmap"   "1"
            }
            """);

        KeyValuesNode patched = WorldVertexTransitionFixup.PatchMaterial(document.Root!);

        Assert.Null(patched.Find("$envmap"));
    }
}
