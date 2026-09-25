using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rad;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <c>CShadowTextureList</c> and its helpers.
/// </summary>
/// <remarks>
/// <para>
/// These facts do NOT gate on a triangle count, and they cannot: this lane
/// measured stock vrad's own <c>-dumptrace</c> output on <c>dm_lockdown</c>
/// with and without <c>-textureshadows</c> and got byte-identical files (sha256
/// equal), and the same again under <c>-StaticPropPolys</c>. The switch changes
/// no caster triangle's existence or position — it only sets
/// <c>FCACHETRI_TRANSPARENT</c> and a material index on triangles that were
/// already in the set. So what is gated here is what the code COMPUTES: the
/// acceptance rule, the coverage arithmetic (including the two places it is
/// deliberately wrong), the wrap, and the model-name normalisation.
/// </para>
/// <para>
/// Several facts pin defects on purpose. Each says so in its own comment, and
/// the member being pinned says so in its XML docs.
/// </para>
/// </remarks>
public class ShadowTextureListTests
{
    // A 4x4 alpha plane with structure on both axes, so a fact can tell a
    // row-wise mistake from a column-wise one and a box average from a
    // triangle average.
    //
    //     0    0    0    0
    //     0  255    0    0
    //   255  255  255  255
    //    51   51   51   51
    private static readonly byte[] CoverageAlpha =
    [
        0, 0, 0, 0,
        0, 255, 0, 0,
        255, 255, 255, 255,
        51, 51, 51, 51,
    ];

    // The sum of every texel above: 255 + (4 * 255) + (4 * 51).
    private const float CoverageAlphaTotal = 255f + 1020f + 204f;

    // Row 0 of the sampling texture; rows 1 to 3 are zero. Four distinct
    // values, so a fact can name the exact column a sample landed in.
    private static readonly byte[] SampleAlpha =
    [
        10, 20, 30, 40,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
    ];

    private static AlphaTexture CoverageTexture() => new(4, 4, CoverageAlpha);

    private static AlphaTexture SampleTexture(bool allowBackface = false) =>
        new(4, 4, SampleAlpha, allowBackface);

    [Fact]
    public void InitFromRgba8888KeepsOnlyTheFourthByteOfEachPixel()
    {
        // alphatexture_t::InitFromRGB8888: the
        // colour is decoded and thrown away.
        byte[] rgba = [1, 2, 3, 200, 4, 5, 6, 100, 7, 8, 9, 0, 10, 11, 12, 255];

        AlphaTexture texture = AlphaTexture.FromRgba8888(2, 2, rgba);

        Assert.Equal<byte>([200, 100, 0, 255], texture.Alpha.ToArray());
    }

    [Fact]
    public void ATexelReadsRowMajorFromTheTopLeft()
    {
        Assert.Equal(255, CoverageTexture().Texel(1, 1));
    }

    [Fact]
    public void SampleScalesByTheDimensionSoTheRightEdgeWrapsBackToColumnZero()
    {
        // multiplies by tex.width, NOT width - 1.
        // That one character is why u == 1.0 is column 0 and not column 3.
        AlphaTexture texture = SampleTexture();

        Assert.Equal(texture.Texel(0, 0), texture.Sample(1f, 0f));
        Assert.NotEqual(texture.Texel(3, 0), texture.Sample(1f, 0f));
    }

    [Fact]
    public void SampleRoundsTiesToEvenAsSseDoes()
    {
        // RoundFloatToInt is _mm_cvtss_si32, which is
        // round-half-to-EVEN. 0.125 * 4 is exactly 0.5, so stock lands on
        // column 0; a round-half-away-from-zero would land on column 1.
        Assert.Equal(10, SampleTexture().Sample(0.125f, 0f));
    }

    [Fact]
    public void SampleRoundsAHalfUpWhenThatIsTheEvenSide()
    {
        // 0.375 * 4 is exactly 1.5, and 2 is the even neighbour.
        Assert.Equal(30, SampleTexture().Sample(0.375f, 0f));
    }

    [Fact]
    public void SampleWrapsANegativeCoordinate()
    {
        // -0.25 * 4 is -1, and -1 & 3 is 3 in two's complement.
        Assert.Equal(40, SampleTexture().Sample(-0.25f, 0f));
    }

    [Fact]
    public void AClampedTextureStillWrapsBecauseStockIgnoresTheFlag()
    {
        // PINS A DELIBERATE DEFECT. The clamp branch is inside the #if 0 at
        //, so SampleMaterial takes the #else and
        // masks. 1.25 * 4 is 5, and 5 & 3 is 1: a clamping implementation
        // would answer column 3 (40) instead.
        AlphaTexture clamped = new(4, 4, SampleAlpha, allowBackface: false, clampU: true, clampV: true);

        Assert.True(clamped.ClampU);
        Assert.Equal(20, clamped.Sample(1.25f, 0f));
    }

    [Fact]
    public void TheClampFlagsAreReadOutOfTheVtfHeader()
    {
        //. They are read, stored, and then never
        // acted on; the fact above is the other half of that statement.
        byte[] bytes = SyntheticAlphaVtf(SampleAlpha, VtfFlags.ClampS | VtfFlags.ClampT);

        AlphaTexture texture = AlphaTexture.FromVtf(VtfFile.Parse(bytes));

        Assert.True(texture.ClampU);
        Assert.True(texture.ClampV);
    }

    [Fact]
    public void AVtfsTopMipIsWhatBecomesTheAlphaPlane()
    {
        // ImageData(0, 0, 0, 0, 0, 0) -- mip 0,
        // whose dimensions are the ones every UV is scaled by.
        AlphaTexture texture = AlphaTexture.FromVtf(VtfFile.Parse(SyntheticAlphaVtf(SampleAlpha)));

        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal<byte>(SampleAlpha, texture.Alpha.ToArray());
    }

    [Fact]
    public void CoverageIsTheAverageAlphaOverTheWholeTextureWhenTheUvsSpanIt()
    {
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        float coverage = list.ComputeCoverageForTriangle(
            index, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));

        Assert.Equal(CoverageAlphaTotal / (16f * 255f), coverage, 6);
    }

    [Fact]
    public void CoverageAveragesTheBoundingBoxAndNotTheTriangle()
    {
        // PINS A DELIBERATE DEFECT, and stock labels it HACKHACK itself
        //. A sliver hugging the UV diagonal covers
        // four texels; the full triangle covers eight. They have the SAME
        // axis-aligned bounding box, so stock gives them the same answer.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        float sliver = list.ComputeCoverageForTriangle(
            index, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.9999f, 1f));
        float whole = list.ComputeCoverageForTriangle(
            index, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));

        Assert.Equal(whole, sliver, 6);
    }

    [Fact]
    public void ATrueTriangleAverageWouldNotAgreeWithTheBoundingBoxAnswer()
    {
        // The other half of the fact above: without this, "box average" and
        // "triangle average" could be the same number and the pin would be
        // measuring nothing. The sliver's own texels are (0,0), (1,1), (2,2)
        // and (3,3) -- 0, 255, 255 and 51.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        float onTheDiagonal = (0f + 255f + 255f + 51f) / (4f * 255f);
        float stock = list.ComputeCoverageForTriangle(
            index, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.9999f, 1f));

        Assert.NotEqual(onTheDiagonal, stock, 3);
    }

    [Fact]
    public void CoverageClampsUvsOutsideTheUnitSquareOntoItsEdge()
    {
        // PINS A DELIBERATE DEFECT: "UNDONE: Do something about tiling"
        //. A triangle whose UVs are entirely in
        // the second tile collapses to the single texel at (1,1) -- 51 here,
        // not the average of the tile it actually covers.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        float coverage = list.ComputeCoverageForTriangle(
            index, new Vector2(2, 2), new Vector2(3, 2), new Vector2(2, 3));

        Assert.Equal(51f / 255f, coverage, 6);
    }

    [Fact]
    public void CoverageScalesTheBoxByWidthMinusOneAndTruncates()
    {
        //: umin * (tex.width - 1), C truncation.
        // v of 0.75 gives (int)(0.75 * 3) == 2, which is the all-255 row.
        // Scaling by width instead would give row 3, which is all 51.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        float coverage = list.ComputeCoverageForTriangle(
            index, new Vector2(0, 0.75f), new Vector2(0, 0.75f), new Vector2(0, 0.75f));

        Assert.Equal(1f, coverage, 6);
    }

    [Fact]
    public void ACoverageOfOneYieldsNoMaterialEntry()
    {
        //: only coverage < 1 gets an entry, and
        // only an entry gets FCACHETRI_TRANSPARENT at:1988-1991.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        TriangleShadowMaterial material = list.AddTriangle(
            index, new Vector2(0, 0.75f), new Vector2(0, 0.75f), new Vector2(0, 0.75f));

        Assert.Equal(-1, material.MaterialIndex);
        Assert.False(material.IsTransparent);
        Assert.Equal(0, list.MaterialEntryCount);
    }

    [Fact]
    public void AnOpaqueTrianglesColourStaysZeroRatherThanBecomingItsCoverage()
    {
        // The `color` vector is left at vec3_origin on the else branch
        //, so the coverage of 1 is computed
        // and discarded.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        TriangleShadowMaterial material = list.AddTriangle(
            index, new Vector2(0, 0.75f), new Vector2(0, 0.75f), new Vector2(0, 0.75f));

        Assert.Equal(0f, material.ColorRed);
    }

    [Fact]
    public void ACoverageBelowOneYieldsAMaterialEntryCarryingTheCoverage()
    {
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        TriangleShadowMaterial material = list.AddTriangle(
            index, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));

        Assert.Equal(0, material.MaterialIndex);
        Assert.True(material.IsTransparent);
        Assert.Equal(material.Coverage, material.ColorRed);
    }

    [Fact]
    public void MaterialEntriesAreAppendedAndNeverDeduplicated()
    {
        // is an AddToTail with no lookup: two
        // triangles with identical texture space get two entries.
        ShadowTextureList list = new();
        int index = list.AddTexture("test/coverage", CoverageTexture());

        int first = list.AddMaterialEntry(index, Vector2.Zero, Vector2.One, Vector2.UnitX);
        int second = list.AddMaterialEntry(index, Vector2.Zero, Vector2.One, Vector2.UnitX);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(2, list.MaterialEntryCount);
    }

    [Fact]
    public void SampleMaterialInterpolatesTheStoredUvsWithTheBarycentrics()
    {
        //. Barycentric (0,1,0) is vertex 1 exactly.
        ShadowTextureList list = new();
        int texture = list.AddTexture("test/sample", SampleTexture());
        int entry = list.AddMaterialEntry(
            texture, Vector2.Zero, new Vector2(0.75f, 0f), Vector2.Zero);

        Assert.Equal(10, list.SampleMaterial(entry, 1f, 0f, 0f, backface: false));
        Assert.Equal(40, list.SampleMaterial(entry, 0f, 1f, 0f, backface: false));
    }

    [Fact]
    public void ABackfaceHitOnACulledTextureBlocksEverything()
    {
        ShadowTextureList list = new();
        int texture = list.AddTexture("test/sample", SampleTexture());
        int entry = list.AddMaterialEntry(texture, Vector2.Zero, Vector2.Zero, Vector2.Zero);

        Assert.Equal(0, list.SampleMaterial(entry, 1f, 0f, 0f, backface: true));
    }

    [Fact]
    public void ABackfaceHitOnANocullTextureStillSamplesTheTexture()
    {
        ShadowTextureList list = new();
        int texture = list.AddTexture("test/sample", SampleTexture(allowBackface: true));
        int entry = list.AddMaterialEntry(texture, Vector2.Zero, Vector2.Zero, Vector2.Zero);

        Assert.Equal(10, list.SampleMaterial(entry, 1f, 0f, 0f, backface: true));
    }

    [Fact]
    public void TheTraceCallbackNeverReportsABackfaceHit()
    {
        // PINS A DELIBERATE DEFECT. ComputeCoverageFromTexture passes
        // `false` literally under a commented-out
        // body that would have computed it:892-893). The consequence: the
        // texture below is CULLED, and the callback still samples it, so
        // $nocull and allowBackface change nothing about a compile.
        ShadowTextureList list = new();
        int texture = list.AddTexture("test/sample", SampleTexture(allowBackface: false));
        int entry = list.AddMaterialEntry(texture, Vector2.Zero, Vector2.Zero, Vector2.Zero);

        Assert.Equal(10f / 255f, list.ComputeCoverageFromTexture(1f, 0f, 0f, entry), 6);
    }

    [Fact]
    public void TheTraceCallbackScalesAnAlphaByteToAFraction()
    {
        //, alphaScale = 1/255.
        ShadowTextureList list = new();
        int texture = list.AddTexture("test/sample", SampleTexture());
        int entry = list.AddMaterialEntry(
            texture, new Vector2(0.75f, 0f), Vector2.Zero, Vector2.Zero);

        Assert.Equal(40f / 255f, list.ComputeCoverageFromTexture(1f, 0f, 0f, entry), 6);
    }

    [Fact]
    public async Task ATranslucentMaterialWithABaseTextureIsAccepted()
    {
        // THE ACCEPTANCE RULE: an opacity key
        // AND a $basetexture that loads.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/glass.vmt", "UnlitGeneric { $basetexture test/glass $translucent 1 }"),
            ("materials/test/glass.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/glass.vmt", content);

        Assert.True(lookup.Found);
        Assert.Equal(0, lookup.Index);
    }

    [Fact]
    public async Task AnAlphaTestMaterialWithABaseTextureIsAccepted()
    {
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/fence.vmt", "UnlitGeneric { $basetexture test/fence $alphatest 1 }"),
            ("materials/test/fence.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/fence.vmt", content);

        Assert.Equal(0, lookup.Index);
    }

    [Fact]
    public async Task AnOpaqueMaterialIsFoundButGetsNoAlphaTexture()
    {
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/wall.vmt", "LightmappedGeneric { $basetexture test/wall }"),
            ("materials/test/wall.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/wall.vmt", content);

        Assert.True(lookup.Found);
        Assert.Equal(-1, lookup.Index);
        Assert.Equal(0, list.TextureCount);
    }

    [Fact]
    public async Task AnOpacityKeyWithoutABaseTextureGetsNoAlphaTexture()
    {
        //: $basetexture is required.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/fog.vmt", "UnlitGeneric { $translucent 1 }"));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/fog.vmt", content);

        Assert.True(lookup.Found);
        Assert.Equal(-1, lookup.Index);
    }

    [Fact]
    public async Task ATranslucentValueOfZeroIsStillAcceptedBecauseStockTestsPresence()
    {
        // PINS A STOCK QUIRK: is FindKey, not a value
        // test, so "$translucent 0" casts alpha shadows. This is why the port
        // reads GetVar for null rather than asking MaterialFacts.Opacity,
        // which would answer Opaque here.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/off.vmt", "UnlitGeneric { $basetexture test/off $translucent 0 }"),
            ("materials/test/off.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/off.vmt", content);

        Assert.Equal(0, lookup.Index);
    }

    [Fact]
    public async Task AMissingMaterialIsNotFoundSoTheSearchCarriesOn()
    {
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync();

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/absent.vmt", content);

        Assert.False(lookup.Found);
        Assert.Equal(-1, lookup.Index);
    }

    [Fact]
    public async Task AMaterialWhoseBaseTextureIsMissingGetsNoAlphaTexture()
    {
        // LoadVTFRGB8888 returns NULL, and
        // FindOrLoadIfValid's `if (pImageBits)` at:716 then skips the
        // insert -- so bFound is still true.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/gone.vmt", "UnlitGeneric { $basetexture test/gone $alphatest 1 }"));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/gone.vmt", content);

        Assert.True(lookup.Found);
        Assert.Equal(-1, lookup.Index);
    }

    [Fact]
    public async Task AMaterialWhoseBaseTextureIsNotAVtfGetsNoAlphaTexture()
    {
        ShadowTextureList list = new();
        InMemoryFileSystem disk = new();
        disk.AddText("materials/test/bad.vmt", "UnlitGeneric { $basetexture test/bad $alphatest 1 }");
        disk.AddText("materials/test/bad.vtf", "this is not a texture");
        await using ContentFileSystem content = new(
            [await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/bad.vmt", content);

        Assert.True(lookup.Found);
        Assert.Equal(-1, lookup.Index);
    }

    [Fact]
    public async Task NocullSetsAllowBackfaceOnTheLoadedTexture()
    {
        //, again FindKey rather than a value test.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/leaf.vmt",
                "UnlitGeneric { $basetexture test/leaf $alphatest 1 $nocull 1 }"),
            ("materials/test/leaf.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/leaf.vmt", content);

        Assert.True(list.Texture(lookup.Index).AllowBackface);
    }

    [Fact]
    public async Task AMaterialWithoutNocullIsBackfaceCulled()
    {
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/leaf.vmt", "UnlitGeneric { $basetexture test/leaf $alphatest 1 }"),
            ("materials/test/leaf.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/leaf.vmt", content);

        Assert.False(list.Texture(lookup.Index).AllowBackface);
    }

    [Fact]
    public async Task AskingTwiceLoadsOneTexture()
    {
        //: the dictionary hit short-circuits.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/fence.vmt", "UnlitGeneric { $basetexture test/fence $alphatest 1 }"),
            ("materials/test/fence.vtf", SampleAlpha));

        ShadowTextureLookup first =
            await list.FindOrLoadIfValidAsync("materials/test/fence.vmt", content);
        ShadowTextureLookup second =
            await list.FindOrLoadIfValidAsync("MATERIALS/Test/Fence.VMT", content);

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(1, list.TextureCount);
    }

    [Fact]
    public async Task APatchMaterialIsAcceptedWhereStockWouldRejectIt()
    {
        // THE ONE KNOWN DIVERGENCE FROM STOCK, pinned so it is visible rather
        // than discovered. Stock parses raw KeyValues at
        // and asks the ROOT for $alphatest; a patch's
        // root key is "patch", so stock answers -1 and the material casts no
        // texture shadow. This port resolves the patch through
        // MaterialFactsReader first and therefore accepts it.
        ShadowTextureList list = new();
        await using ContentFileSystem content = await MountAsync(
            ("materials/test/base.vmt",
                "UnlitGeneric { $basetexture test/base $alphatest 1 }"),
            ("materials/test/patched.vmt",
                "patch { include \"materials/test/base.vmt\" replace { $alpha 1 } }"),
            ("materials/test/base.vtf", SampleAlpha));

        ShadowTextureLookup lookup =
            await list.FindOrLoadIfValidAsync("materials/test/patched.vmt", content);

        Assert.Equal(0, lookup.Index);
    }

    [Fact]
    public async Task AModelsTextureIsFoundThroughItsMaterialSearchPaths()
    {
        //: materials/<cdtexture><texture>.vmt,
        // and the search path here is spelled with backslashes, as a real MDL
        // spells it.
        ShadowTextureList list = new();
        MdlFile model = MdlFile.Parse(BuildMdl("shadowtest", [@"models\a\", @"models\b\"]));
        await using ContentFileSystem content = await MountAsync(
            ("materials/models/b/shadowtest.vmt",
                "UnlitGeneric { $basetexture test/fence $alphatest 1 }"),
            ("materials/test/fence.vtf", SampleAlpha));

        int[] textures = await list.LoadAllTexturesForModelAsync(model, content);

        Assert.Equal([0], textures);
    }

    [Fact]
    public async Task TheSearchStopsAtTheFirstPathWhereTheMaterialExistsEvenIfItIsOpaque()
    {
        // PINS A STOCK QUIRK. The loop breaks
        // on FindOrLoadIfValid's bFound, which is true for a material that
        // merely PARSED. So the opaque material in the first search path ends
        // the search and the alpha-tested one in the second is never seen.
        ShadowTextureList list = new();
        MdlFile model = MdlFile.Parse(BuildMdl("shadowtest", [@"models\a\", @"models\b\"]));
        await using ContentFileSystem content = await MountAsync(
            ("materials/models/a/shadowtest.vmt",
                "LightmappedGeneric { $basetexture test/fence }"),
            ("materials/models/b/shadowtest.vmt",
                "UnlitGeneric { $basetexture test/fence $alphatest 1 }"),
            ("materials/test/fence.vtf", SampleAlpha));

        int[] textures = await list.LoadAllTexturesForModelAsync(model, content);

        Assert.Equal([-1], textures);
        Assert.Equal(0, list.TextureCount);
    }

    [Fact]
    public async Task AModelWhoseMaterialIsInNoSearchPathGetsMinusOne()
    {
        ShadowTextureList list = new();
        MdlFile model = MdlFile.Parse(BuildMdl("shadowtest", [@"models\a\"]));
        await using ContentFileSystem content = await MountAsync();

        int[] textures = await list.LoadAllTexturesForModelAsync(model, content);

        Assert.Equal([-1], textures);
    }

    [Fact]
    public void CleanModelNameStripsALeadingModelsDirectory()
    {
        Assert.Equal(
            "props_c17/oildrum001",
            ForcedTextureShadowModels.CleanModelName("models/props_c17/oildrum001.mdl"));
    }

    [Fact]
    public void CleanModelNameStripsThePrefixWithoutRegardToCase()
    {
        // Q_strnicmp at:905 -- the one case-insensitive comparison on the
        // whole path.
        Assert.Equal("a/b", ForcedTextureShadowModels.CleanModelName("MODELS/a/b.mdl"));
    }

    [Fact]
    public void CleanModelNameTruncatesAtTheFirstDotAnywhereNotTheExtension()
    {
        // strchr, not strrchr:914). A directory with a dot in its name loses
        // everything after it.
        Assert.Equal("props", ForcedTextureShadowModels.CleanModelName("models/props.v2/crate.mdl"));
    }

    [Fact]
    public void CleanModelNameDoesNotChangeCase()
    {
        // Q_strncpy at:911 copies verbatim; nothing lowercases.
        Assert.Equal("Props_C17/OilDrum001", ForcedTextureShadowModels.CleanModelName(
            "models/Props_C17/OilDrum001.mdl"));
    }

    [Fact]
    public void AForcedModelMatchesTheBspSpellingOfTheSameName()
    {
        ForcedTextureShadowModels forced = new();
        forced.Add("props_c17/oildrum001");

        Assert.True(forced.Contains("models/props_c17/oildrum001.mdl"));
    }

    [Fact]
    public void AForcedModelNameIsMatchedCaseSensitively()
    {
        // PINS A STOCK QUIRK. g_ForcedTextureShadowsModels is a plain
        // CUtlSymbolTable and its caseInsensitive
        // argument defaults to FALSE, so a capitalised
        // lights.rad line silently matches nothing.
        ForcedTextureShadowModels forced = new();
        forced.Add("Props_C17/OilDrum001");

        Assert.False(forced.Contains("models/props_c17/oildrum001.mdl"));
    }

    [Fact]
    public void AddingTheSameModelTwiceRecordsItOnce()
    {
        // The Find-then-AddString.
        ForcedTextureShadowModels forced = new();

        Assert.True(forced.Add("models/a/b.mdl"));
        Assert.False(forced.Add("a/b"));
        Assert.Equal(1, forced.Count);
    }

    [Fact]
    public async Task ForcetextureshadowLinesFromALightsRadFeedTheForcedSet()
    {
        // parses the line; ForceTextureShadowsOnModel cleans
        // the name. RadLightFile hands back the
        // RAW name, so this type does the cleaning -- as stock does.
        RadLightFile lights = await RadLightFile.ParseAsync(
            "forcetextureshadow models/props_foliage/tree01.mdl\n");
        ForcedTextureShadowModels forced = new();
        forced.AddRange(lights.ForcedTextureShadowModels);

        Assert.True(forced.Contains("models/props_foliage/tree01.mdl"));
    }

    [Fact]
    public void TheGateIsShutWithoutTheTextureshadowsSwitch()
    {
        //: g_bTextureShadows is the outer test,
        // so a compile without the switch reads none of these VMTs.
        ForcedTextureShadowModels forced = new();
        forced.Add("props/crate");

        Assert.False(forced.ShouldLoadTextures(
            textureShadowsEnabled: false,
            ForcedTextureShadowModels.CastTextureShadowsFlag,
            "models/props/crate.mdl"));
    }

    [Fact]
    public void TheStudioFlagOpensTheGate()
    {
        ForcedTextureShadowModels forced = new();

        Assert.True(forced.ShouldLoadTextures(
            textureShadowsEnabled: true,
            ForcedTextureShadowModels.CastTextureShadowsFlag,
            "models/props/crate.mdl"));
    }

    [Fact]
    public void AForcetextureshadowLineOpensTheGateWithoutTheStudioFlag()
    {
        ForcedTextureShadowModels forced = new();
        forced.Add("props/crate");

        Assert.True(forced.ShouldLoadTextures(
            textureShadowsEnabled: true, studioHeaderFlags: 0, "models/props/crate.mdl"));
    }

    [Fact]
    public void AModelThatOptsInNeitherWayIsSkipped()
    {
        ForcedTextureShadowModels forced = new();

        Assert.False(forced.ShouldLoadTextures(
            textureShadowsEnabled: true, studioHeaderFlags: 0, "models/props/crate.mdl"));
    }

    [Fact]
    public void TheCastTextureShadowsFlagIsTheStudioHeaderBit()
    {
        //. A wrong constant here would silently opt every model
        // in or out, and nothing else in the port would notice.
        Assert.Equal(0x00040000, ForcedTextureShadowModels.CastTextureShadowsFlag);
    }

    /// <summary>
    /// A 4x4 BGRA8888 VTF carrying a chosen alpha plane.
    /// </summary>
    /// <remarks>
    /// Built on the same <c>Synthetic</c> header the VTF facts use, so the
    /// layout under test is the production reader's and only the pixels are a
    /// fixture. BGRA8888 puts alpha at byte 3 of each source pixel, exactly
    /// where RGBA8888 puts it, so the plane can be written straight in.
    /// </remarks>
    private static byte[] SyntheticAlphaVtf(byte[] alpha, VtfFlags flags = VtfFlags.None)
    {
        byte[] bytes = MapFormats.Assets.VtfTests.Synthetic(
            ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false);

        MemoryMarshal.Write(bytes.AsSpan(20), (uint)flags);

        int image = VtfFile.Parse(bytes).ImageDataOffset;
        for (int i = 0; i < alpha.Length; i++)
        {
            bytes[image + (i * 4) + 3] = alpha[i];
        }

        return bytes;
    }

    /// <summary>
    /// A minimal MDL: no bones, no meshes, one texture and a chosen set of
    /// material search paths.
    /// </summary>
    /// <remarks>
    /// Enough for <c>LoadAllTexturesForModel</c>, which reads
    /// <c>numtextures</c>, <c>pTexture(i)-&gt;pszName</c> and
    /// <c>pCdtexture(j)</c> and nothing else.
    /// </remarks>
    private static byte[] BuildMdl(string textureName, string[] searchPaths)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        int textureAt = headerSize;
        int cdTableAt = textureAt + Unsafe.SizeOf<StudioTexture>();
        int stringsAt = cdTableAt + (searchPaths.Length * 4);

        List<byte> strings = [];
        int textureNameAt = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes(textureName + "\0"));

        int[] searchPathAt = new int[searchPaths.Length];
        for (int i = 0; i < searchPaths.Length; i++)
        {
            searchPathAt[i] = stringsAt + strings.Count;
            strings.AddRange(Encoding.Latin1.GetBytes(searchPaths[i] + "\0"));
        }

        StudioHeader header = new()
        {
            Id = StudioIdents.Mdl,
            Version = StudioIdents.MdlVersion,
            Length = stringsAt + strings.Count,
            NumTextures = 1,
            TextureIndex = textureAt,
            NumCdTextures = searchPaths.Length,
            CdTextureIndex = cdTableAt,
        };

        // the reference implementation's pszName is ((char *)this) + sznameindex, so the offset
        // is relative to the texture struct and not to the file.
        StudioTexture texture = new() { NameIndex = textureNameAt - textureAt };

        List<byte> bytes = [];
        bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<StudioHeader>(in header)).ToArray());
        bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<StudioTexture>(in texture)).ToArray());
        bytes.AddRange(MemoryMarshal.AsBytes<int>(searchPathAt).ToArray());
        bytes.AddRange(strings);

        return [.. bytes];
    }

    private static async Task<ContentFileSystem> MountAsync(params (string Path, object Body)[] files)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, object body) in files)
        {
            if (body is string text)
            {
                disk.AddText(path, text);
            }
            else
            {
                disk.AddFile(path, SyntheticAlphaVtf((byte[])body));
            }
        }

        return new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
    }
}
