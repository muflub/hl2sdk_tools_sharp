using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The struct sizes measured against a real, committed map.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate the plan asks for and it is a strong one: on an 11 MB map
/// a struct whose size is off by a pad byte almost never divides its lump
/// cleanly. <c>dface_t</c> at 55 instead of 56 fails against 366,016 bytes
/// immediately.
/// </para>
/// <para>
/// <c>dm_lockdown.bsp</c> is BSP version 19 from an older bsplib. It has no
/// leaf-ambient lumps and no HDR lumps, and it DOES carry lump 49 and lump 32,
/// which current tools no longer write. That is the specimen, not a defect:
/// the whole point of a map-tool port is that it opens maps its own compiler
/// did not produce.
/// </para>
/// </remarks>
public class BspLumpLayoutTests : IClassFixture<LockdownFixture>
{
    private readonly LockdownFixture _fixture;

    /// <summary>Takes the shared, once-loaded golden map.</summary>
    /// <param name="fixture">The fixture xUnit constructs once for the class.</param>
    public BspLumpLayoutTests(LockdownFixture fixture) => _fixture = fixture;

    private BspData Lockdown() => _fixture.Bsp;

    public static TheoryData<int> AllLumps()
    {
        TheoryData<int> data = [];
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllLumps))]
    public void EveryLumpOfTheGoldenMapIsAWholeNumberOfItsStruct(int index)
    {
        BspData bsp = Lockdown();
        BspLumpData lump = bsp[index];
        int? size = BspLumpLayout.ElementSize((BspLump)index, lump.Version);

        // A lump that is not an array of a fixed struct, or that this map does
        // not carry, has a remainder of zero by definition -- computed rather
        // than early-returned, so this theory always makes one real assertion
        // and never a vacuous one.
        int remainder = size is null || lump.IsEmpty ? 0 : lump.Length % size.Value;

        Assert.True(
            remainder == 0,
            $"lump {index} ({(BspLump)index}) is {lump.Length} bytes, which is not a whole "
            + $"number of its {size}-byte struct: {remainder} left over");
    }

    [Fact]
    public void TheGoldenMapIsTheVersionNineteenSpecimenTheseFactsAssume()
    {
        // If this map is ever replaced with a version 20 one, the leaf-size and
        // lump-49 facts below stop measuring what they claim to.
        Assert.Equal(19, Lockdown().FileVersion);
    }

    [Fact]
    public void TheGoldenMapRecordsLeafsAtVersionZero()
    {
        Assert.Equal(0, Lockdown()[BspLump.Leafs].Version);
    }

    [Fact]
    public void TheGoldenMapsLeavesAreFiftySixBytesEach()
    {
        // 152,600 bytes. Divides by 56 and NOT by 32, so this single number
        // decides which of the two leaf structs is right and proves the other
        // would have been wrong.
        BspLumpData leafs = Lockdown()[BspLump.Leafs];

        Assert.Equal(0, leafs.Length % 56);
        Assert.NotEqual(0, leafs.Length % 32);
    }

    [Fact]
    public void TheGoldenMapsLeafCountMatchesItsMinDistToWaterLump()
    {
        // An independent cross-check on the 56: LUMP_LEAFMINDISTTOWATER is one
        // ushort per leaf, so leafLength/56 must equal waterLength/2. A wrong
        // leaf size fails this even if it happened to divide.
        BspData bsp = Lockdown();
        int leaves = bsp[BspLump.Leafs].Length / 56;
        int distances = bsp[BspLump.LeafMinDistToWater].Length / sizeof(ushort);

        Assert.Equal(leaves, distances);
    }

    [Fact]
    public void TheGoldenMapsFaceCountMatchesItsMacroTextureLump()
    {
        // LUMP_FACE_MACRO_TEXTURE_INFO is one ushort per face, so this pins
        // dface_t at 56 from a second direction.
        BspData bsp = Lockdown();
        int faces = bsp[BspLump.Faces].Length / 56;
        int macros = bsp[BspLump.FaceMacroTextureInfo].Length / sizeof(ushort);

        Assert.Equal(faces, macros);
    }

    [Fact]
    public void TheGoldenMapCarriesTheDeprecatedPhysCollideSurfaceLump()
    {
        // bspfile.h:341 calls lump 49 deprecated and nothing in this tree
        // writes it, yet this map has 1.1 MB of it. A port that "cleans up"
        // by dropping unknown lumps silently changes the map.
        Assert.False(Lockdown()[BspLump.PhysCollideSurface].IsEmpty);
    }

    [Fact]
    public void TheGoldenMapHasNoLeafAmbientLumpsAtAll()
    {
        // The companion to the version 0 leaves: the ambient data is INSIDE
        // them, so the separate lumps are absent.
        BspData bsp = Lockdown();

        Assert.True(bsp[BspLump.LeafAmbientLighting].IsEmpty);
        Assert.True(bsp[BspLump.LeafAmbientIndex].IsEmpty);
    }

    [Fact]
    public void TheGoldenMapHasNoHdrLumps()
    {
        BspData bsp = Lockdown();

        Assert.True(bsp[BspLump.LightingHdr].IsEmpty);
        Assert.True(bsp[BspLump.FacesHdr].IsEmpty);
        Assert.True(bsp[BspLump.WorldLightsHdr].IsEmpty);
    }

    [Fact]
    public void LeafElementSizeIsChosenByTheLumpVersionNotTheFileVersion()
    {
        Assert.Equal(56, BspLumpLayout.ElementSize(BspLump.Leafs, 0));
        Assert.Equal(32, BspLumpLayout.ElementSize(BspLump.Leafs, 1));
    }

    /// <summary>
    /// The lumps whose element size depends on their own lump version.
    /// </summary>
    /// <remarks>
    /// This list was one entry long, and that was a bug rather than a fact
    /// about the format: the validator lane found that at any version but 1 the
    /// engine casts the leaf-ambient lump to <c>CompressedLightCube</c> (24
    /// bytes) rather than <c>dleafambientlighting_t</c> (28) and asserts the
    /// length divides by THAT (<c>modelloader.cpp:2203-2211</c>). Answering 28
    /// unconditionally made a legacy map's lump look misaligned when it was
    /// correct.
    /// </remarks>
    private static readonly BspLump[] VersionSized =
        [BspLump.Leafs, BspLump.LeafAmbientLighting, BspLump.LeafAmbientLightingHdr];

    public static TheoryData<BspLump> VersionSizedLumps => [.. VersionSized];

    [Theory]
    [MemberData(nameof(VersionSizedLumps))]
    public void AVersionSizedLumpReallyChangesSize(BspLump lump)
    {
        Assert.NotEqual(
            BspLumpLayout.ElementSize(lump, 0),
            BspLumpLayout.ElementSize(lump, 1));
    }

    [Fact]
    public void NoOtherLumpChangesSizeWithItsVersion()
    {
        HashSet<int> versionSized = [.. VersionSized.Select(l => (int)l)];

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (versionSized.Contains(i))
            {
                continue;
            }

            Assert.Equal(
                BspLumpLayout.ElementSize((BspLump)i, 0),
                BspLumpLayout.ElementSize((BspLump)i, 1));
        }
    }

    [Fact]
    public void TheNonArrayLumpsAnswerNullRatherThanOne()
    {
        // "no fixed element" must be a distinct answer from "bytes", or the
        // divisibility gate passes trivially for the lumps most likely to be
        // misread.
        Assert.Null(BspLumpLayout.ElementSize(BspLump.Visibility, 0));
        Assert.Null(BspLumpLayout.ElementSize(BspLump.Occlusion, 2));
        Assert.Null(BspLumpLayout.ElementSize(BspLump.GameLump, 0));
        Assert.Null(BspLumpLayout.ElementSize(BspLump.PakFile, 0));
        Assert.Null(BspLumpLayout.ElementSize(BspLump.PhysCollide, 0));
        Assert.Null(BspLumpLayout.ElementSize(BspLump.PhysDisp, 0));
    }

    [Fact]
    public void OriginalFacesIsVersionZeroEvenThoughItHoldsFaces()
    {
        // bsplib.cpp:2671 versions LUMP_FACES and LUMP_FACES_HDR and lets
        // LUMP_ORIGINALFACES default to zero. The golden map agrees.
        Assert.Equal(0, BspLumpLayout.CurrentVersion(BspLump.OriginalFaces));
        Assert.Equal(0, Lockdown()[BspLump.OriginalFaces].Version);
    }

    [Fact]
    public void TheFiveVersionedLumpsAreTheOnesBspfileLists()
    {
        // bspfile.h:360 -- LIGHTING 1, FACES 1, OCCLUSION 2, LEAFS 1,
        // LEAF_AMBIENT_LIGHTING 1, plus the HDR twins that share their
        // constants.
        Assert.Equal(1, BspLumpLayout.CurrentVersion(BspLump.Lighting));
        Assert.Equal(1, BspLumpLayout.CurrentVersion(BspLump.Faces));
        Assert.Equal(2, BspLumpLayout.CurrentVersion(BspLump.Occlusion));
        Assert.Equal(1, BspLumpLayout.CurrentVersion(BspLump.Leafs));
        Assert.Equal(1, BspLumpLayout.CurrentVersion(BspLump.LeafAmbientLighting));
    }

    [Fact]
    public void EveryOtherLumpIsVersionZero()
    {
        int versioned = 0;
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (BspLumpLayout.CurrentVersion((BspLump)i) != 0)
            {
                versioned++;
            }
        }

        // The five from bspfile.h:360 plus LIGHTING_HDR, FACES_HDR and
        // LEAF_AMBIENT_LIGHTING_HDR, which reuse the same three constants.
        Assert.Equal(8, versioned);
    }

    [Fact]
    public void ATypedViewOverTheGoldenMapsPlanesCountsThemWithoutCopying()
    {
        BspData bsp = Lockdown();
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);

        Assert.Equal(bsp[BspLump.Planes].Length / 20, planes.Length);
    }

    [Fact]
    public void TheGoldenMapHasAnEvenNumberOfPlanes()
    {
        // bspfile.h:473 -- planes come in opposite pairs, so an odd count means
        // the struct size is wrong even though the length divided.
        BspData bsp = Lockdown();
        Assert.Equal(0, BspStructView.Count<DPlane>(bsp[BspLump.Planes]) % 2);
    }

    [Fact]
    public void ATypedViewRejectsALengthThatIsNotAWholeNumberOfElements()
    {
        BspLumpData ragged = new(new byte[21], 0, 0);

        Assert.Throws<InvalidBspException>(() => BspStructView.As<DPlane>(ragged));
    }

    [Fact]
    public void TheGoldenMapsFirstModelIsTheWorldAndStartsAtFaceZero()
    {
        // bspfile.h:441 -- model 0 is the world. A shifted dmodel_t would show
        // up here as a nonsense head node or first face.
        BspData bsp = Lockdown();
        DModel world = BspStructView.As<DModel>(bsp[BspLump.Models])[0];

        Assert.Equal(0, world.HeadNode);
        Assert.Equal(0, world.FirstFace);
    }

    [Fact]
    public void TheGoldenMapsWorldModelCoversEveryFaceOfTheFirstModel()
    {
        BspData bsp = Lockdown();
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        int totalFaces = BspStructView.Count<DFace>(bsp[BspLump.Faces]);

        // Every model's face run must lie inside the face lump. A wrong
        // dmodel_t size makes these indices garbage immediately.
        foreach (DModel model in models)
        {
            Assert.InRange(model.FirstFace, 0, totalFaces);
            Assert.InRange(model.FirstFace + model.NumFaces, 0, totalFaces);
        }
    }

    [Fact]
    public void TheGoldenMapsFacesAllReferenceRealPlanesAndTexInfos()
    {
        // The cheapest possible semantic check that dface_t's fields are in the
        // right ORDER: swap two same-width fields and these indices go out of
        // range on an 11 MB map within the first few faces.
        BspData bsp = Lockdown();
        int planes = BspStructView.Count<DPlane>(bsp[BspLump.Planes]);
        int texInfos = BspStructView.Count<TexInfo>(bsp[BspLump.TexInfo]);

        foreach (DFace face in BspStructView.As<DFace>(bsp[BspLump.Faces]))
        {
            Assert.InRange(face.PlaneNum, 0, planes - 1);
            Assert.InRange(face.TexInfo, (short)0, (short)(texInfos - 1));
        }
    }

    [Fact]
    public void TheGoldenMapsTexInfosAllReferenceRealTexData()
    {
        BspData bsp = Lockdown();
        int texDatas = BspStructView.Count<DTexData>(bsp[BspLump.TexData]);

        foreach (TexInfo info in BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]))
        {
            Assert.InRange(info.TexData, 0, texDatas - 1);
        }
    }

    [Fact]
    public void TheGoldenMapsTexDataReflectivitiesAreAllInZeroToOne()
    {
        // vbsp copies these out of each VTF's header. They are colours, so
        // every component must be a sane fraction; a field-order mistake in
        // dtexdata_t puts a pixel count here instead.
        BspData bsp = Lockdown();

        foreach (DTexData texData in BspStructView.As<DTexData>(bsp[BspLump.TexData]))
        {
            Assert.InRange(texData.Reflectivity.X, 0.0f, 1.0f);
            Assert.InRange(texData.Reflectivity.Y, 0.0f, 1.0f);
            Assert.InRange(texData.Reflectivity.Z, 0.0f, 1.0f);
        }
    }

    [Fact]
    public void TheGoldenMapsTexDataDimensionsArePowersOfTwo()
    {
        BspData bsp = Lockdown();

        foreach (DTexData texData in BspStructView.As<DTexData>(bsp[BspLump.TexData]))
        {
            Assert.True(texData.Width > 0 && (texData.Width & (texData.Width - 1)) == 0,
                $"width {texData.Width} is not a power of two");
            Assert.True(texData.Height > 0 && (texData.Height & (texData.Height - 1)) == 0,
                $"height {texData.Height} is not a power of two");
        }
    }

    [Fact]
    public void TheGoldenMapsDisplacementsAllHaveALegalPower()
    {
        // bspfile.h:47 -- MIN_MAP_DISP_POWER 2, MAX_MAP_DISP_POWER 4. This is
        // the sharpest available check on ddispinfo_t's 176 bytes: get the
        // size wrong and the power field reads as a coordinate.
        BspData bsp = Lockdown();

        foreach (DispInfo info in BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]))
        {
            Assert.InRange(info.Power, 2, 4);
        }
    }

    [Fact]
    public void TheGoldenMapsDisplacementVertexRunsTileTheVertexLumpExactly()
    {
        // Each displacement owns NumVerts() consecutive entries of
        // LUMP_DISP_VERTS, so the runs must sum to the lump's element count.
        BspData bsp = Lockdown();
        int total = 0;

        foreach (DispInfo info in BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]))
        {
            total += info.NumVerts();
        }

        Assert.Equal(BspStructView.Count<DispVert>(bsp[BspLump.DispVerts]), total);
    }

    [Fact]
    public void TheGoldenMapsDisplacementTriangleRunsTileTheTriangleLumpExactly()
    {
        BspData bsp = Lockdown();
        int total = 0;

        foreach (DispInfo info in BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]))
        {
            total += info.NumTris();
        }

        Assert.Equal(BspStructView.Count<DispTri>(bsp[BspLump.DispTris]), total);
    }

    [Fact]
    public void TheGoldenMapsCubemapSizesAreAllSmall()
    {
        // bspfile.h:997 -- 0 means default, otherwise 1<<(size-1). A shifted
        // dcubemapsample_t puts a coordinate byte here and this blows up.
        BspData bsp = Lockdown();

        foreach (DCubemapSample sample in BspStructView.As<DCubemapSample>(bsp[BspLump.Cubemaps]))
        {
            Assert.InRange(sample.Size, (byte)0, (byte)8);
        }
    }

    [Fact]
    public void TheGoldenMapsWorldLightsAllHaveAKnownEmitType()
    {
        // bspfile.h:954 -- six emit types. dworldlight_t is 88 bytes of mostly
        // floats, so this int field is the one that catches a shift.
        BspData bsp = Lockdown();

        foreach (DWorldLight light in BspStructView.As<DWorldLight>(bsp[BspLump.WorldLights]))
        {
            Assert.InRange(light.Type, (int)EmitType.Surface, (int)EmitType.SkyAmbient);
        }
    }

    [Fact]
    public void TheGoldenMapsEdgesAllReferenceRealVertices()
    {
        BspData bsp = Lockdown();
        int vertices = BspStructView.Count<Vec3>(bsp[BspLump.Vertexes]);

        foreach (DEdge edge in BspStructView.As<DEdge>(bsp[BspLump.Edges]))
        {
            Assert.InRange(edge.V[0], (ushort)0, (ushort)(vertices - 1));
            Assert.InRange(edge.V[1], (ushort)0, (ushort)(vertices - 1));
        }
    }

    [Fact]
    public void TheGoldenMapsBrushSideRunsLieInsideTheBrushSideLump()
    {
        BspData bsp = Lockdown();
        int sides = BspStructView.Count<DBrushSide>(bsp[BspLump.BrushSides]);

        foreach (DBrush brush in BspStructView.As<DBrush>(bsp[BspLump.Brushes]))
        {
            Assert.InRange(brush.FirstSide, 0, sides);
            Assert.InRange(brush.FirstSide + brush.NumSides, 0, sides);
        }
    }

    [Fact]
    public void TheGoldenMapsLeafRunsLieInsideTheLeafFaceAndLeafBrushLumps()
    {
        // Read with the version 0 struct, because that is what the lump's own
        // version says. The same read with DLeaf would put these counts in
        // different fields and fail.
        BspData bsp = Lockdown();
        int leafFaces = bsp[BspLump.LeafFaces].Length / sizeof(ushort);
        int leafBrushes = bsp[BspLump.LeafBrushes].Length / sizeof(ushort);

        foreach (DLeafVersion0 leaf in BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]))
        {
            Assert.InRange(leaf.FirstLeafFace + leaf.NumLeafFaces, 0, leafFaces);
            Assert.InRange(leaf.FirstLeafBrush + leaf.NumLeafBrushes, 0, leafBrushes);
        }
    }

    [Fact]
    public void TheGoldenMapsLeafAreasAllFitInNineBits()
    {
        // bspfile.h:807 -- area:9. MAX_MAP_AREAS is 256, so a real map's areas
        // are far below the 511 the field allows; a bitfield read from the
        // wrong end produces values above it.
        BspData bsp = Lockdown();

        foreach (DLeafVersion0 leaf in BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]))
        {
            Assert.InRange(leaf.GetArea(), 0, 255);
        }
    }

    [Fact]
    public void TheGoldenMapsNodeChildrenAllNameARealNodeOrLeaf()
    {
        // bspfile.h:490 -- negative is -(leaf + 1). This is the fact that
        // proves dnode_t's trailing pad is in the right place: put it at the
        // front and every child index shifts.
        BspData bsp = Lockdown();
        int nodes = BspStructView.Count<DNode>(bsp[BspLump.Nodes]);
        int leaves = bsp[BspLump.Leafs].Length / 56;

        foreach (DNode node in BspStructView.As<DNode>(bsp[BspLump.Nodes]))
        {
            for (int i = 0; i < 2; i++)
            {
                int child = node.Children[i];
                if (child < 0)
                {
                    Assert.InRange(-(child + 1), 0, leaves - 1);
                }
                else
                {
                    Assert.InRange(child, 0, nodes - 1);
                }
            }
        }
    }
}
