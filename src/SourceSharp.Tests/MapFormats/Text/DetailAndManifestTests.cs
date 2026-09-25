using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the three schemas that sit on top of a parsed file:
/// <c>detail.vbsp</c>, the
/// surfaceproperties manifest and
/// the <c>.vmm</c> map manifest, plus
/// <c>func_instance</c> path resolution
/// </summary>
public class DetailAndManifestTests
{
    private static DetailObjectFile ParseDetail(string text) =>
        DetailObjectFile.ParseAsync(text, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    [Fact]
    public void DetailTypeNameIsTheKeyItWasDeclaredUnder()
    {
        // It is what a material's %detailtype names.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tdetail_grass\n\t{\n\t\t\"density\" \"3000\"\n\t\tgroup\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"props/grass.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal("detail_grass", file.Types[0].Name);
    }

    [Fact]
    public void DensityIsReadAtTheTypeLevelNotThePerGroupLevel()
    {
        // The reference implementation. Easy to get wrong because `alpha` right beside
        // it in the file IS per group.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\t\"density\" \"3000\"\n\t\tg\n\t\t{\n\t\t\t\"alpha\" \"0.5\"\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(3000f, file.Types[0].Density);
        Assert.Equal(0.5f, file.Types[0].Groups[0].Alpha);
    }

    [Fact]
    public void DensityDefaultsToZero()
    {
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(0f, file.Types[0].Density);
    }

    [Fact]
    public void SampleCountIsAreaTimesDensityTimesAMillionthTruncated()
    {
        // (int)(area * density * 0.000001). Truncated,
        // NOT rounded, which is why a low-density type places nothing at all on
        // a small face.
        DetailObjectType type = new("t", 3000f);

        Assert.Equal(0, type.SampleCount(100));
        Assert.Equal(3, type.SampleCount(1000));
    }

    [Fact]
    public void AlphaDefaultsToOne()
    {
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(1f, file.Types[0].Groups[0].Alpha);
    }

    [Fact]
    public void GroupsAreSortedByAscendingAlpha()
    {
        // Inserted after the first group more
        // transparent than this one.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n" +
            "\t\thigh\n\t\t{\n\t\t\t\"alpha\" \"0.9\"\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"a.mdl\"\n\t\t\t}\n\t\t}\n" +
            "\t\tlow\n\t\t{\n\t\t\t\"alpha\" \"0.1\"\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"b.mdl\"\n\t\t\t}\n\t\t}\n" +
            "\t}\n}\n");

        Assert.Equal([0.1f, 0.9f], file.Types[0].Groups.Select(g => g.Alpha));
    }

    [Fact]
    public void GroupNamesAreNeverRead()
    {
        // Only checks that a group HAS subkeys; the
        // name is never looked at, and groups are identified afterwards by
        // position in the alpha-sorted list.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tanything_at_all\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Single(file.Types[0].Groups);
    }

    [Fact]
    public void TopLevelLeafKeysAreSilentlyIgnored()
    {
        // `if (!pIter->GetFirstSubKey) continue`.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\t\"stray\" \"1\"\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Single(file.Types);
    }

    [Fact]
    public void ModelKeyWinsOverEverySpriteKeyInTheSameBlock()
    {
        // When `model` is present the whole sprite
        // branch is never entered, so sprite, spritesize, sway and
        // the shape keys are not read at all. Note the deliberately invalid
        // "sprite" value here, which would be FATAL on the other branch.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n" +
            "\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t\t\"sprite\" \"nonsense\"\n\t\t\t\t\"sway\" \"1\"\n" +
            "\t\t\t}\n\t\t}\n\t}\n}\n");

        DetailObjectModel model = file.Types[0].Groups[0].Models[0];

        Assert.Equal(DetailPropType.Model, model.Type);
        Assert.Equal(0, model.SwayAmount);
    }

    [Fact]
    public void SpriteWithFewerThanFiveNumbersIsFatal()
    {
        // All five fields mandatory despite the
        // initialisers above the sscanf. The one hard error in this parser.
        Assert.Throws<DetailObjectFileException>(() => ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 0 64 64\"\n\t\t\t}\n\t\t}\n\t}\n}\n"));
    }

    [Fact]
    public void SpriteWithAZeroTextureSizeIsFatal()
    {
        // The other half of the reference implementation.
        Assert.Throws<DetailObjectFileException>(() => ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 0 64 64 0\"\n\t\t\t}\n\t\t}\n\t}\n}\n"));
    }

    [Fact]
    public void SpriteShapeSelectsTheProceduralTypeCaseInsensitively()
    {
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n" +
            "\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"sprite_shape\" \"TRI\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(DetailPropType.ShapeTri, file.Types[0].Groups[0].Models[0].Type);
    }

    [Fact]
    public void UnrecognisedSpriteShapeFallsBackToAPlainSprite()
    {
        // Any other non-empty string.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n" +
            "\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"sprite_shape\" \"hexagon\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(DetailPropType.Sprite, file.Types[0].Groups[0].Models[0].Type);
    }

    [Fact]
    public void AmountIsARunningCumulativeSum()
    {
        // M_Amount = amount + totalAmount. The
        // stored value is a point on a cumulative distribution, not the
        // model's own weight.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n" +
            "\t\t\ta\n\t\t\t{\n\t\t\t\t\"model\" \"a.mdl\"\n\t\t\t\t\"amount\" \"0.25\"\n\t\t\t}\n" +
            "\t\t\tb\n\t\t\t{\n\t\t\t\t\"model\" \"b.mdl\"\n\t\t\t\t\"amount\" \"0.25\"\n\t\t\t}\n" +
            "\t\t}\n\t}\n}\n");

        Assert.Equal(
            [0.25f, 0.5f],
            file.Types[0].Groups[0].Models.Select(m => m.CumulativeAmount));
    }

    [Fact]
    public void AmountsUnderOneAreLeftAloneSoTheRemainderIsEmptySpace()
    {
        // Renormalised ONLY when the total
        // exceeds 1. Below it, SelectDetail returns -1 for the
        // remainder and places nothing.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n" +
            "\t\t\ta\n\t\t\t{\n\t\t\t\t\"model\" \"a.mdl\"\n\t\t\t\t\"amount\" \"0.3\"\n\t\t\t}\n" +
            "\t\t}\n\t}\n}\n");

        Assert.Equal(0.3f, file.Types[0].Groups[0].Models[0].CumulativeAmount);
    }

    [Fact]
    public void AmountsOverOneAreRenormalised()
    {
        // The other branch of the reference implementation.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n" +
            "\t\t\ta\n\t\t\t{\n\t\t\t\t\"model\" \"a.mdl\"\n\t\t\t\t\"amount\" \"1\"\n\t\t\t}\n" +
            "\t\t\tb\n\t\t\t{\n\t\t\t\t\"model\" \"b.mdl\"\n\t\t\t\t\"amount\" \"1\"\n\t\t\t}\n" +
            "\t\t}\n\t}\n}\n");

        Assert.Equal(
            [0.5f, 1.0f],
            file.Types[0].Groups[0].Models.Select(m => m.CumulativeAmount));
    }

    [Fact]
    public void AmountDefaultsToOne()
    {
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(1f, file.Types[0].Groups[0].Models[0].CumulativeAmount);
    }

    [Fact]
    public void AnglesAreStoredAsCosinesAndDefaultToMinusOne()
    {
        // Cos(180 degrees) is -1, which restricts
        // nothing.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n\t\t\t\t\"model\" \"x.mdl\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(-1f, file.Types[0].Groups[0].Models[0].MinCosAngle, 6);
    }

    [Fact]
    public void ShapeAngleWrapsBecauseItIsStoredInAByteWithNoClamp()
    {
        // GetInt with no clamp, into an unsigned char.
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n" +
            "\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"shape_angle\" \"360\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(104, file.Types[0].Groups[0].Models[0].ShapeAngle);
    }

    [Fact]
    public void SwayIsClampedToOneAndQuantisedToAByte()
    {
        DetailObjectFile file = ParseDetail(
            "detail\n{\n\tt\n\t{\n\t\tg\n\t\t{\n\t\t\tm\n\t\t\t{\n" +
            "\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"sway\" \"5\"\n\t\t\t}\n\t\t}\n\t}\n}\n");

        Assert.Equal(255, file.Types[0].Groups[0].Models[0].SwayAmount);
    }

    [Fact]
    public async Task SurfacePropsManifestCollectsEveryRepeatedFileKey()
    {
        // The key is `file`, case-insensitive, and it
        // repeats. Duplicate KeyValues keys are kept,
        // which is what makes this shape work at all.
        SurfacePropertiesManifest manifest = await SurfacePropertiesManifest
            .ParseAsync(
                "surfaceproperties_manifest\n{\n\t\"file\" \"scripts/surfaceproperties.txt\"\n\t\"FILE\" \"scripts/mod.txt\"\n}\n",
                CancellationToken.None);

        Assert.Equal(["scripts/surfaceproperties.txt", "scripts/mod.txt"], manifest.Files);
    }

    [Fact]
    public async Task SurfacePropsManifestRootNameIsNeverValidated()
    {
        // Constructs the KeyValues with the manifest path
        // as its name and LoadFromFile overwrites it; nothing ever compares it.
        // The real shipped file's root is even misspelled.
        SurfacePropertiesManifest manifest = await SurfacePropertiesManifest
            .ParseAsync(
                "anything_at_all\n{\n\t\"file\" \"scripts/surfaceproperties.txt\"\n}\n",
                CancellationToken.None);

        Assert.Single(manifest.Files);
    }

    [Fact]
    public async Task SurfacePropsManifestRecordsKeysVbspIgnores()
    {
        // vbsp ignores them in silence (the reference implementation has no else); the
        // GAME warns about them. Recording them
        // lets a caller produce that warning at compile time instead.
        SurfacePropertiesManifest manifest = await SurfacePropertiesManifest
            .ParseAsync(
                "m\n{\n\t\"file\" \"a.txt\"\n\t\"folder\" \"b\"\n}\n",
                CancellationToken.None);

        Assert.Equal(["folder"], manifest.UnexpectedKeys);
    }

    [Fact]
    public async Task VmmManifestListsItsVmfsInOrder()
    {
        // A "Maps" chunk of "VMF" chunks.
        VmfManifest manifest = await VmfManifest.ParseAsync(
            "Maps\r\n{\r\n" +
            "\tVMF\r\n\t{\r\n\t\t\"Name\" \"base\"\r\n\t\t\"File\" \"parts/base.vmf\"\r\n\t\t\"TopLevel\" \"1\"\r\n\t}\r\n" +
            "\tVMF\r\n\t{\r\n\t\t\"Name\" \"props\"\r\n\t\t\"File\" \"parts/props.vmf\"\r\n\t\t\"TopLevel\" \"0\"\r\n\t}\r\n" +
            "}\r\n",
            CancellationToken.None);

        Assert.Equal(["parts/base.vmf", "parts/props.vmf"], manifest.Maps.Select(m => m.File));
        Assert.True(manifest.Maps[0].IsTopLevel);
        Assert.False(manifest.Maps[1].IsTopLevel);
    }

    [Fact]
    public async Task VmmTopLevelIsAnEqualityWithOneNotATruthTest()
    {
        // `atoi(szValue) == 1`, so "2" is FALSE. Not the
        // `> 0` that ReadKeyValueBool uses elsewhere in the same file.
        VmfManifest manifest = await VmfManifest.ParseAsync(
            "Maps\r\n{\r\n\tVMF\r\n\t{\r\n\t\t\"File\" \"a.vmf\"\r\n\t\t\"TopLevel\" \"2\"\r\n\t}\r\n}\r\n",
            CancellationToken.None);

        Assert.False(manifest.Maps[0].IsTopLevel);
    }

    [Fact]
    public async Task CordonBoundsAreParenthesisedPointsNotBracketedVectors()
    {
        // Call ReadKeyValuePoint, whose format string is
        // "(%f %f %f)". The bracketed spelling belongs to
        // ReadKeyValueVector3 and would NOT parse here.
        VmfDocument prefs = await VmfDocument.ParseAsync(
            "cordoning\r\n{\r\n\tcordons\r\n\t{\r\n\t\tcordon\r\n\t\t{\r\n" +
            "\t\t\t\"name\" \"area\"\r\n\t\t\t\"active\" \"1\"\r\n" +
            "\t\t\tbox\r\n\t\t\t{\r\n\t\t\t\t\"mins\" \"(-64 -64 0)\"\r\n\t\t\t\t\"maxs\" \"(64 64 128)\"\r\n\t\t\t}\r\n" +
            "\t\t}\r\n\t}\r\n}\r\n",
            CancellationToken.None);

        VmfCordon cordon = Assert.Single(VmfManifest.ReadCordons(prefs));

        Assert.True(cordon.Active);
        Assert.Equal(new Vec3(-64, -64, 0), cordon.Boxes[0].Mins);
        Assert.Equal(new Vec3(64, 64, 128), cordon.Boxes[0].Maxs);
    }

    [Fact]
    public void InstanceIsLookedForBesideTheReferringMapFirst()
    {
        IReadOnlyList<string> candidates =
            FuncInstance.ResolveCandidates("c:\\mod\\maps\\town.vmf", "parts/shop");

        Assert.Equal("c:\\mod\\maps\\parts\\shop.vmf", candidates[0]);
    }

    [Fact]
    public void InstanceExtensionIsForcedToVmf()
    {
        // V_SetExtension, so a "file" key naming a.txt still
        // looks for a .vmf.
        IReadOnlyList<string> candidates =
            FuncInstance.ResolveCandidates("c:\\mod\\maps\\town.vmf", "parts/shop.txt");

        Assert.EndsWith("shop.vmf", candidates[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SecondCandidateIsRelativeToTheEnclosingMapsDirectory()
    {
        // Truncated just after the first "\maps\", and
        // lower-cased along the way.
        IReadOnlyList<string> candidates =
            FuncInstance.ResolveCandidates("C:\\Mod\\Maps\\Sub\\town.vmf", "parts/shop.vmf");

        Assert.Equal("c:\\mod\\maps\\parts\\shop.vmf", candidates[1]);
    }

    [Fact]
    public void ThirdCandidateUsesTheORIGINALNameWithNoExtensionForced()
    {
        // THE QUIRK. The reference implementation concatenates the ORIGINAL
        // argument -- not the buffer the extension was forced onto
        // earlier. So an entry whose `file` key omits the extension is found
        // beside the map and NOT under InstancePath.
        IReadOnlyList<string> candidates = FuncInstance.ResolveCandidates(
            "c:\\mod\\maps\\town.vmf", "parts/shop", "instances\\");

        Assert.Equal("instances\\parts/shop", candidates[2]);
    }

    [Fact]
    public void ThereIsNoThirdCandidateWithoutAnInstancePath()
    {
        // The branch is guarded on m_InstancePath being set,
        // which comes from gameinfo.txt's InstancePath key.
        IReadOnlyList<string> candidates =
            FuncInstance.ResolveCandidates("c:\\mod\\maps\\town.vmf", "parts/shop.vmf");

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void InstanceClassnameIsMatchedCaseSensitively()
    {
        // Uses strcmp, not Q_stricmp -- unlike almost every other
        // classname comparison in the tree. The constant is pinned so a caller
        // comparing against it inherits the rule.
        Assert.Equal("func_instance", FuncInstance.ClassName);
    }
}
