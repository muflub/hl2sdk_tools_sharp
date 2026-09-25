using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// The <c>utilmatlib</c> getters, over fixture content.
/// </summary>
/// <remarks>
/// The differential against a real compiled map lives in
/// <see cref="InstalledMaterialFactsTests"/> and skips when the game is not
/// installed. These run everywhere, and pin the answers a fixture can state
/// exactly: which variable wins, what an absent file gives, where a number
/// came from.
/// </remarks>
public class MaterialFactsTests
{
    [Fact]
    public async Task AMaterialResolvesThroughTheMaterialsDirectory()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("brick/brickwall001a", "LightmappedGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("brick/brickwall001a", content);

        Assert.True(facts.Found);
    }

    [Fact]
    public async Task AMaterialResolvesInTheCasingAMapWroteIt()
    {
        // Hammer writes Brick/BrickWall001a; the disk holds lower case.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("brick/brickwall001a", "LightmappedGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("Brick/BrickWall001a", content);

        Assert.True(facts.Found);
    }

    [Fact]
    public async Task ABackslashInAMaterialNameResolves()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("brick/brickwall001a", "LightmappedGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync(@"brick\brickwall001a", content);

        Assert.True(facts.Found);
    }

    [Fact]
    public async Task AMissingMaterialIsNotFoundRatherThanAFailure()
    {
        await using ContentFileSystem content = await new MaterialContent().MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("nothing/here", content);

        Assert.False(facts.Found);
    }

    [Fact]
    public async Task AMissingMaterialStillReportsTheFallbackDimensions()
    {
        // substitutes 128x128 rather than erroring, and
        // that is what lands in TEXDATA.
        await using ContentFileSystem content = await new MaterialContent().MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("nothing/here", content);

        Assert.Equal((128, 128), (facts.Width, facts.Height));
    }

    [Fact]
    public async Task TheShaderNameIsTheVmtsRootKey()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "WorldVertexTransition { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal("WorldVertexTransition", facts.ShaderName);
    }

    [Fact]
    public async Task AMissingMaterialLooksUpEveryVarAsNull()
    {
        await using ContentFileSystem content = await new MaterialContent().MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("nothing/here", content);

        Assert.Null(facts.GetVar("%compileSky"));
    }

    [Fact]
    public async Task AVariableComesBackWithItsCaseIgnored()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { \"$SurfaceProp\" \"metal\" }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal("metal", facts.SurfaceProp);
    }

    [Fact]
    public async Task AnAbsentVariableIsNullAndNotEmpty()
    {
        // Every caller in the reference implementation tests the pointer, so null is the
        // answer that keeps the reference control flow.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Null(facts.SurfaceProp2);
    }

    [Fact]
    public async Task SurfacePropTwoIsReadSeparatelyFromSurfaceProp()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "WorldVertexTransition { $surfaceprop dirt $surfaceprop2 concrete }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(("dirt", "concrete"), (facts.SurfaceProp, facts.SurfaceProp2));
    }

    [Fact]
    public async Task SubdivSizeIsAMaterialVariable()
    {
        // reads it off the material. An earlier brief called it
        // a vbsp command-line option; it is not one.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $subdivsize 64 }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(64f, facts.SubdivSize);
    }

    [Fact]
    public async Task AnAbsentSubdivSizeIsZero()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(0f, facts.SubdivSize);
    }

    [Fact]
    public async Task DetailTypeIsRead()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { %detailtype coastline }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal("coastline", facts.DetailType);
    }

    [Fact]
    public async Task MacroTextureIsRead()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $macro_texture maps/x/macro }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal("maps/x/macro", facts.MacroTexture);
    }

    [Fact]
    public async Task ChopIsRead()
    {
        // Read because the material carries it. vrad in this drop does not:
        // is inside a comment block.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { %chop 32 }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal("32", facts.Chop);
    }

    [Fact]
    public async Task ReflectivityComesFromTheVtfHeaderWhenTheMaterialHasNone()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddTexture("a/b", 64, 32, (0.25f, 0.5f, 0.75f))
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(new Vec3(0.25f, 0.5f, 0.75f), facts.Reflectivity);
    }

    [Fact]
    public async Task AnExplicitReflectivityOverridesTheVtfHeader()
    {
        //: $reflectivity is looked up FIRST and the
        // texture's value is only the fallback.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b \"$reflectivity\" \"[1 0 0]\" }")
            .AddTexture("a/b", 64, 32, (0.25f, 0.5f, 0.75f))
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(new Vec3(1f, 0f, 0f), facts.Reflectivity);
    }

    [Fact]
    public async Task WhereTheReflectivityCameFromIsReported()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddTexture("a/b", 64, 32, (0.25f, 0.5f, 0.75f))
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.False(facts.ReflectivityFromVar);
    }

    [Fact]
    public async Task DimensionsComeFromTheVtfHeader()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddTexture("a/b", 64, 32, (0f, 0f, 0f))
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal((64, 32), (facts.Width, facts.Height));
    }

    [Fact]
    public async Task AMaterialWithNoBaseTextureFallsBackToOneTwentyEight()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "UnlitGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal((128, 128), (facts.Width, facts.Height));
    }

    [Fact]
    public async Task AMaterialWhoseBaseTextureIsAbsentFallsBackToOneTwentyEight()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture missing/texture }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal((128, 128), (facts.Width, facts.Height));
    }

    [Fact]
    public async Task AMaterialWhoseBaseTextureIsCorruptFallsBackRatherThanThrowing()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddCorruptTexture("a/b")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.False(facts.HasPreviewImage);
    }

    [Fact]
    public async Task AMaterialWithNoBaseTextureHasNoReflectivity()
    {
        // GetReflectivity on a material with no representative texture gives
        // zero, which is what vbsp then writes into TEXDATA.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "UnlitGeneric { }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(Vec3.Zero, facts.Reflectivity);
    }

    [Fact]
    public async Task TheBaseTexturePathIsReported()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture Nature/BlendRocks }")
            .AddTexture("nature/blendrocks", 8, 8, (0f, 0f, 0f))
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.Equal(VPath.Create("materials/nature/blendrocks.vtf"), facts.BaseTexture);
    }

    [Fact]
    public async Task APatchThatChangesSomethingReportsTheIncludedShader()
    {
        // The string "patch" does not survive resolution, so a patched water
        // material answers "Water" to the shader-name test in.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("base/water", "Water { $normalmap base/n }")
            .AddMaterial(
                "maps/x/water",
                "patch { include \"materials/base/water.vmt\" insert { $surfaceprop water } }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("maps/x/water", content);

        Assert.Equal("Water", facts.ShaderName);
    }

    [Fact]
    public async Task AnEmptyPatchStaysAPatchUnderTheCompilerDialect()
    {
        // Not a defect in the port: reassigns
        // keyValues only inside the insert and the replace branches, so a
        // patch carrying NEITHER never advances -- the loop spins to the depth
        // limit and the root is still "patch". A material of this shape
        // therefore has no shader, no $basetexture, and reaches TEXDATA as
        // 128x128 with zero reflectivity.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("base/water", "Water { $normalmap base/n }")
            .AddMaterial("maps/x/water", "patch { include \"materials/base/water.vmt\" }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("maps/x/water", content);

        Assert.Equal("patch", facts.ShaderName);
    }

    [Fact]
    public async Task AnEmptyPatchResolvesUnderTheEngineDialect()
    {
        // The other half of the same fact, and the reason the dialect is an
        // option rather than a constant: assigns the base
        // wholesale whether or not the patch changed anything, and
        // goes through the ENGINE's FindMaterial.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("base/water", "Water { $normalmap base/n }")
            .AddMaterial("maps/x/water", "patch { include \"materials/base/water.vmt\" }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync(
            "maps/x/water",
            content,
            new MaterialFactsOptions { PatchDialect = VmtPatchDialect.Engine });

        Assert.Equal("Water", facts.ShaderName);
    }

    [Fact]
    public async Task APatchsInsertReachesTheFacts()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("base/b", "LightmappedGeneric { $surfaceprop metal }")
            .AddMaterial(
                "maps/x/b",
                "patch { include \"materials/base/b.vmt\" insert { $surfaceprop dirt } }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("maps/x/b", content);

        Assert.Equal("dirt", facts.SurfaceProp);
    }

    [Fact]
    public async Task APatchWhoseIncludeIsMissingIsNotFound()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("maps/x/b", "patch { include \"materials/gone.vmt\" }")
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("maps/x/b", content);

        Assert.False(facts.Found);
    }

    [Fact]
    public async Task EveryMaterialLookupPassesTheDependencyRecorder()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddTexture("a/b", 8, 8, (0f, 0f, 0f))
            .MountAsync();

        RecordingContentFileSystem recording = new(content);
        await MaterialFactsReader.ReadAsync("a/b", recording);

        Assert.Equal(2, recording.Recorder.Count);
    }

    [Fact]
    public async Task AMissingMaterialIsRecordedAsADependencyToo()
    {
        // The miss is the input a later run invalidates by adding one file, so
        // a reader that skipped the seam for absent materials would make the
        // cache wrong rather than slow.
        await using ContentFileSystem content = await new MaterialContent().MountAsync();

        RecordingContentFileSystem recording = new(content);
        await MaterialFactsReader.ReadAsync("nothing/here", recording);

        Assert.Equal(1, recording.Recorder.Count);
    }

    [Fact]
    public async Task ReadingManyMaterialsCollapsesDuplicateSpellings()
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { }")
            .MountAsync();

        IReadOnlyDictionary<string, MaterialFacts> facts =
            await MaterialFactsReader.ReadManyAsync(["a/b", "A/B", @"a\b", "a/b.vmt"], content);

        Assert.Single(facts);
    }

    [Fact]
    public async Task ReadingManyMaterialsReadsASharedTextureOnce()
    {
        // The load-then-compute rule's other half: two materials on one base
        // texture are two VMT reads and ONE VTF read.
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/one", "LightmappedGeneric { $basetexture shared/t }")
            .AddMaterial("a/two", "LightmappedGeneric { $basetexture shared/t }")
            .AddTexture("shared/t", 16, 16, (0f, 0f, 0f))
            .MountAsync();

        RecordingContentFileSystem recording = new(content);
        await MaterialFactsReader.ReadManyAsync(["a/one", "a/two"], recording);

        Assert.Equal(3, recording.Recorder.Count);
    }

    [Fact]
    public void NormalizingStripsTheMaterialsPrefixAndTheExtension()
    {
        Assert.Equal("metal/wall", MaterialFactsReader.Normalize("Materials/Metal/Wall.VMT"));
    }

    [Fact]
    public void AMaterialNameBecomesAContentPath()
    {
        Assert.Equal(
            VPath.Create("materials/metal/wall.vmt"),
            MaterialFactsReader.ContentPath(@"Metal\Wall"));
    }
}
