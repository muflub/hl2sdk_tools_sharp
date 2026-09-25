using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// vbsp's displacement emitter, <c>utils/vbsp/disp_vbsp.cpp</c>: the lump
/// layout, the fields it copies, and the helpers the driver needs.
/// </summary>
public sealed class DisplacementLumpBuilderTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    private static readonly Vec3[] Neighbour = DispFixtures.FloorQuad(new Vec3(256, 0, 0), 256, 256);

    /// <summary>
    /// Vertex and triangle runs are laid out back to back in displacement
    /// order: <c>EmitInitialDispInfos</c>, <c>disp_vbsp.cpp:309-313</c>.
    /// </summary>
    [Fact]
    public void TheRunsAreLaidOutBackToBack()
    {
        (IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps) = DispFixtures.Build(
            [(DispFixtures.Heightfield(3, Floor[0]), Floor), (DispFixtures.Heightfield(2, Neighbour[0]), Neighbour)]);

        Assert.Equal(0, results[0].Info.DispVertStart);
        Assert.Equal(81, results[1].Info.DispVertStart);
        Assert.Equal(128, results[1].Info.DispTriStart);
        Assert.Equal(81 + 25, lumps.Verts.Count);
        Assert.Equal(128 + 32, lumps.Tris.Count);
    }

    /// <summary>
    /// <c>minTess</c> is the VMF flags with the top bit set, never the VMF's
    /// mintess: <c>disp_vbsp.cpp:326-328</c>.
    /// </summary>
    [Fact]
    public void MinTessIsTheFlagsWithTheTopBitSet()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0]);
        disp.Flags = 2;
        disp.MinTess = 3;

        (IReadOnlyList<DisplacementResult> results, _) = DispFixtures.Build([(disp, Floor)]);

        Assert.Equal(unchecked((int)0x80000002), results[0].Info.MinTess);
    }

    /// <summary>
    /// The lump's contents is the brush's, unforced: <c>disp_vbsp.cpp:333</c>.
    /// </summary>
    [Fact]
    public void TheLumpContentsIsTheBrushsUnforced()
    {
        List<DisplacementFace> faces = [DispFixtures.Face(Floor, contents: 0)];
        DisplacementLumps lumps = new();

        IReadOnlyList<DisplacementResult> results = DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, Floor[0])], faces, new VbspOptions(), lumps);

        Assert.Equal(0, results[0].Info.Contents);
    }

    /// <summary>
    /// The core's own contents is forced solid when nothing visible or
    /// clipping is set: <c>disp_vbsp.cpp:161-165</c>.
    /// </summary>
    [Fact]
    public void TheCoresContentsIsForcedSolidWhenInvisible()
    {
        CoreDispInfo core = new(2);
        core.SetListBase([core]);

        DisplacementLumpBuilder.DispMapToCoreDispInfo(
            DispFixtures.Heightfield(2, Floor[0]), DispFixtures.Face(Floor, contents: 0), core, false);

        Assert.Equal((int)BrushContents.Solid, core.Surface.Contents);
    }

    /// <summary>Player clip alone is enough to escape the forcing: <c>disp_vbsp.cpp:163</c>.</summary>
    [Fact]
    public void PlayerClipContentsIsNotForcedSolid()
    {
        CoreDispInfo core = new(2);
        core.SetListBase([core]);
        int clip = (int)BrushContents.PlayerClip;

        DisplacementLumpBuilder.DispMapToCoreDispInfo(
            DispFixtures.Heightfield(2, Floor[0]), DispFixtures.Face(Floor, contents: clip), core, false);

        Assert.Equal(clip, core.Surface.Contents);
    }

    /// <summary>
    /// <c>ALL_VISIBLE_CONTENTS</c> is every bit up to <c>CONTENTS_OPAQUE</c>:
    /// <c>bspflags.h:36</c>.
    /// </summary>
    [Fact]
    public void AllVisibleContentsIsTheLowBitsUpToOpaque()
    {
        Assert.Equal(0xFF, DisplacementLumpBuilder.AllVisibleContents);
    }

    /// <summary><c>m_iMapFace</c> is the base face's index: <c>disp_vbsp.cpp:554</c>.</summary>
    [Fact]
    public void MapFaceIsTheBaseFacesIndex()
    {
        DisplacementLumps lumps = new();

        IReadOnlyList<DisplacementResult> results = DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, Floor[0])], [DispFixtures.Face(Floor, faceIndex: 17)],
            new VbspOptions(), lumps);

        Assert.Equal(17, results[0].Info.MapFace);
    }

    /// <summary>
    /// A vertex's lump direction is the normalised combined field and its
    /// distance the combined length: <c>disp_vbsp.cpp:342-349</c>.
    /// </summary>
    [Fact]
    public void AVertexStoresTheCombinedFieldAsDirectionAndLength()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], (_, _) => 3);
        disp.VectorOffsets[4] = new Vec3(4, 0, 0);

        (_, DisplacementLumps lumps) = DispFixtures.Build([(disp, Floor)]);

        Assert.Equal(5.0f, lumps.Verts[4].Dist);
        Assert.Equal(new Vec3(0.8f, 0, 0.6f), lumps.Verts[4].Vector);
    }

    /// <summary>The alphas go straight into the lump: <c>disp_vbsp.cpp:351</c>.</summary>
    [Fact]
    public void TheAlphasAreCopied()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], alpha: (x, y) => (x * 10) + y);

        (_, DisplacementLumps lumps) = DispFixtures.Build([(disp, Floor)]);

        Assert.Equal(21.0f, lumps.Verts[(1 * 5) + 2].Alpha);
    }

    /// <summary>The triangle tags go straight into the lump: <c>disp_vbsp.cpp:357</c>.</summary>
    [Fact]
    public void TheTriangleTagsAreCopied()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0]);
        disp.TriangleTags[9] = (ushort)DispTriTags.Buildable;

        (_, DisplacementLumps lumps) = DispFixtures.Build([(disp, Floor)]);

        Assert.Equal((ushort)DispTriTags.Buildable, lumps.Tris[9].Tags);
    }

    /// <summary>A displacement with no base face of four points is refused.</summary>
    [Fact]
    public void ANonQuadBaseFaceIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, Floor[0])],
            [DispFixtures.Face(Floor[..3])],
            new VbspOptions(),
            new DisplacementLumps()));
    }

    /// <summary>A face list of the wrong length is refused.</summary>
    [Fact]
    public void MismatchedListsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, Floor[0])], [], new VbspOptions(), new DisplacementLumps()));
    }

    /// <summary>
    /// Each sample position run starts where the previous ended:
    /// <c>disp_vbsp.cpp:584</c>.
    /// </summary>
    [Fact]
    public void EachSampleRunStartsWhereThePreviousEnded()
    {
        (IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Floor[0]), Floor), (DispFixtures.Heightfield(2, Neighbour[0]), Neighbour)]);

        // 256 / 16 + 1 = 17 luxels a side, (17 + 1)^2 samples, 4 bytes each
        // while every triangle index is below 255.
        Assert.Equal(0, results[0].Info.LightmapSamplePositionStart);
        Assert.Equal(18 * 18 * 4, results[1].Info.LightmapSamplePositionStart);
        Assert.Equal(2 * 18 * 18 * 4, lumps.LightmapSamplePositions.Count);
    }

    /// <summary>
    /// The reported lightmap size is the core's luxel count:
    /// <c>disp_vbsp.cpp:208-209</c>.
    /// </summary>
    [Fact]
    public void TheLightmapSizeIsTheLuxelCount()
    {
        (IReadOnlyList<DisplacementResult> results, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, Floor[0]), DispFixtures.FloorQuad(Vec3.Zero, 256, 128))]);

        Assert.Equal(17, results[0].LightmapSizeU);
        Assert.Equal(9, results[0].LightmapSizeV);
    }

    /// <summary>
    /// The texture coordinate is the dot product then the offset:
    /// <c>CalcTextureCoordsAtPoints</c>, <c>bsplib.cpp:3288</c>.
    /// </summary>
    [Fact]
    public void ATextureCoordinateIsTheDotProductPlusTheOffset()
    {
        DispUv[] uv = DisplacementLumpBuilder.CalcTextureCoordsAtPoints(
            [1, 2, 3, 4, 0, 0, -1, 0.5f], [new Vec3(1, 1, 1)]);

        Assert.Equal(new DispUv(10, -0.5f), uv[0]);
    }

    /// <summary>A texture vector array that is not two rows of four is refused.</summary>
    [Fact]
    public void AMalformedTextureVectorIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => DisplacementLumpBuilder.CalcTextureCoordsAtPoints([1, 2, 3], [Vec3.Zero]));
    }

    /// <summary>
    /// Without a face, the corner texture coordinates are stock's unit-square
    /// defaults: <c>disp_vbsp.cpp:171</c>.
    /// </summary>
    [Fact]
    public void WithoutAFaceTheTextureCoordinatesAreTheUnitSquare()
    {
        CoreDispInfo core = new(2);
        core.SetListBase([core]);

        DisplacementLumpBuilder.DispMapToCoreDispInfo(
            DispFixtures.Heightfield(2, Floor[0]), DispFixtures.Face(Floor), core, false, withFace: false);

        Assert.Equal(new DispUv(0, 1), core.Surface.TexCoords[1]);
        Assert.Equal(new DispUv(1, 1), core.Surface.TexCoords[3]);
    }

    /// <summary>
    /// A rectangle whose long side runs along the lightmap V axis needs its
    /// axes swapped: <c>CalcLuxelCoords</c>, <c>builddisp.cpp:489-500</c>.
    /// </summary>
    [Fact]
    public void ALongSideAlongLightmapVNeedsASwap()
    {
        Vec3[] quad = DispFixtures.FloorQuad(Vec3.Zero, 256, 64);
        List<DisplacementFace> faces =
        [
            DispFixtures.Face(quad, lightmapU: new Vec3(0, DispFixtures.LuxelsPerUnit, 0),
                lightmapV: new Vec3(DispFixtures.LuxelsPerUnit, 0, 0)),
        ];

        IReadOnlyList<DisplacementResult> results = DisplacementLumpBuilder.Build(
            [DispFixtures.Heightfield(2, quad[0])], faces, new VbspOptions(), new DisplacementLumps());

        Assert.True(results[0].NeedsSwappedTexInfo);
    }

    /// <summary>The same rectangle with the lightmap U along its long side needs none.</summary>
    [Fact]
    public void ALongSideAlongLightmapUNeedsNoSwap()
    {
        Vec3[] quad = DispFixtures.FloorQuad(Vec3.Zero, 256, 64);

        (IReadOnlyList<DisplacementResult> results, _) = DispFixtures.Build(
            [(DispFixtures.Heightfield(2, quad[0]), quad)]);

        Assert.False(results[0].NeedsSwappedTexInfo);
    }

    /// <summary>
    /// The swapped texinfo's lightmap row 0 is the old row 1, and row 1 is the
    /// old row 0 negated, offset included: <c>disp_vbsp.cpp:222-228</c>.
    /// </summary>
    [Fact]
    public void SwappingMovesRowOneUpAndNegatesRowZero()
    {
        TexInfo t = default;
        for (int i = 0; i < 8; i++)
        {
            t.LightmapVecsLuxelsPerWorldUnits[i] = i + 1;
        }

        TexInfo s = DisplacementLumpBuilder.SwapLightmapAxes(t);

        Assert.Equal([5f, 6f, 7f, 8f, -1f, -2f, -3f, -4f], ((ReadOnlySpan<float>)s.LightmapVecsLuxelsPerWorldUnits).ToArray());
    }

    /// <summary>Swapping leaves the texture vectors and flags alone.</summary>
    [Fact]
    public void SwappingLeavesTheTextureVectorsAlone()
    {
        TexInfo t = default;
        t.TextureVecsTexelsPerWorldUnits[3] = 9;
        t.Flags = 4;

        TexInfo s = DisplacementLumpBuilder.SwapLightmapAxes(t);

        Assert.Equal(9, s.TextureVecsTexelsPerWorldUnits[3]);
        Assert.Equal(4, s.Flags);
    }

    /// <summary>
    /// One swapped copy per ORIGINAL texinfo, shared by every swapped face that
    /// used it: <c>pSwappedTexInfos</c>, <c>disp_vbsp.cpp:214</c>.
    /// </summary>
    [Fact]
    public void TwoSwappedFacesOnOneTexInfoShareOneCopy()
    {
        List<TexInfo> table = [default, default];
        DisplacementResult[] results = [Result(0, swapped: true), Result(1, swapped: true)];

        int[] assigned = DisplacementLumpBuilder.AssignSwappedTexInfos(results, [1, 1], table);

        Assert.Equal(3, table.Count);
        Assert.Equal([2, 2], assigned);
    }

    /// <summary>An unswapped face keeps its texinfo.</summary>
    [Fact]
    public void AnUnswappedFaceKeepsItsTexInfo()
    {
        List<TexInfo> table = [default];

        int[] assigned = DisplacementLumpBuilder.AssignSwappedTexInfos(
            [Result(0, swapped: false)], [0], table);

        Assert.Single(table);
        Assert.Equal([0], assigned);
    }

    /// <summary>
    /// Copies are numbered in FACE order, not displacement order — stock's
    /// loop runs over <c>dfaces</c> (<c>disp_vbsp.cpp:547</c>).
    /// </summary>
    [Fact]
    public void CopiesAreNumberedInFaceOrder()
    {
        List<TexInfo> table = [default, default];
        DisplacementResult[] results = [Result(9, swapped: true), Result(3, swapped: true)];

        int[] assigned = DisplacementLumpBuilder.AssignSwappedTexInfos(results, [0, 1], table);

        // Face 3 (displacement 1, texinfo 1) is visited first and gets entry 2.
        Assert.Equal([3, 2], assigned);
    }

    /// <summary>
    /// Under stock the world-bounds box is the FLAT base quad puffed by 0.1,
    /// whatever the displacement's height: <c>ComputeDispInfoBounds</c> via
    /// <c>GetDispBox</c>, <c>disp_vbsp.cpp:39</c>, <c>disp_common.cpp:770</c>.
    /// </summary>
    [Fact]
    public void UnderStockTheWorldBoundsBoxIgnoresTheDisplacement()
    {
        DispBox box = DisplacementLumpBuilder.ComputeDispInfoBounds(
            DispFixtures.Heightfield(2, Floor[0], (_, _) => 500), DispFixtures.Face(Floor), ComplianceOptions.Stock);

        Assert.Equal(0.1f, box.Max.Z);
        Assert.Equal(-0.1f, box.Min.Z);
    }

    /// <summary>
    /// Under correct it is the box of the displaced surface:
    /// <see cref="StockQuirk.DispWorldBoundsBaseQuad"/>.
    /// </summary>
    [Fact]
    public void UnderCorrectTheWorldBoundsBoxIsTheDisplacedSurface()
    {
        DispBox box = DisplacementLumpBuilder.ComputeDispInfoBounds(
            DispFixtures.Heightfield(2, Floor[0], (_, _) => 500), DispFixtures.Face(Floor), ComplianceOptions.Correct);

        Assert.Equal(new DispBox(new Vec3(0, 0, 500), new Vec3(256, 256, 500)), box);
    }

    /// <summary>
    /// The neighbour tables and allowed-vertex words are copied into the lump
    /// entry: <c>ExportCoreDispNeighborData</c> / <c>ExportCoreDispAllowedVertList</c>,
    /// <c>disp_vbsp.cpp:368</c> and <c>:389</c>.
    /// </summary>
    [Fact]
    public void ExportCopiesTheAllowedVerts()
    {
        CoreDispInfo core = new(2);
        core.AllowedVertsSetAll();
        core.AllowedVertsClear(0);
        DispInfo info = default;

        DisplacementLumpBuilder.ExportNeighbourData(core, ref info);

        Assert.Equal(0xFFFFFFFEu, info.AllowedVerts[0]);
        Assert.Equal(0xFFFFFFFFu, info.AllowedVerts[9]);
    }

    /// <summary>
    /// A vertex the tessellation drops is folded onto the kept surface with a
    /// distance of exactly one: <c>SnapRemainingVertsToSurface</c>,
    /// <c>disp_vbsp.cpp:483-490</c>.
    /// </summary>
    [Fact]
    public void ADroppedVertexIsSnappedWithADistanceOfOne()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], (x, y) => (x == 1 && y == 1) ? 50 : 0);
        CoreDispInfo core = DispFixtures.Core(disp, Floor);
        core.AllowedVertsSetAll();
        core.AllowedVertsClear(DispFixtures.Index(core, 1, 1));

        DisplacementLumpVertexView view = new();
        for (int i = 0; i < disp.VertCount; i++)
        {
            (Vec3 v, float d) = disp.CombinedField(i, false);
            view.Verts.Add(new DispVert { Vector = v, Dist = d });
        }

        DisplacementLumpBuilder.SnapRemainingVertsToSurface(core, default, view);

        int index = DispFixtures.Index(core, 1, 1);
        Assert.Equal(1.0f, view.Verts[index].Dist);
        Assert.Equal(0.0f, core.Vert(index).Z, 1e-4f);
    }

    /// <summary>A vertex the tessellation keeps is not touched.</summary>
    [Fact]
    public void AKeptVertexIsNotSnapped()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], (x, y) => (x == 1 && y == 1) ? 50 : 0);
        CoreDispInfo core = DispFixtures.Core(disp, Floor);
        core.AllowedVertsSetAll();

        DisplacementLumpVertexView view = new();
        for (int i = 0; i < disp.VertCount; i++)
        {
            (Vec3 v, float d) = disp.CombinedField(i, false);
            view.Verts.Add(new DispVert { Vector = v, Dist = d });
        }

        DisplacementLumpBuilder.SnapRemainingVertsToSurface(core, default, view);

        Assert.Equal(50.0f, view.Verts[DispFixtures.Index(core, 1, 1)].Dist);
    }

    /// <summary>
    /// The stock normalise quirk moves only the direction, never the distance:
    /// <see cref="StockQuirk.DispVertNormalise"/>, <c>disp_vbsp.cpp:345-346</c>.
    /// </summary>
    [Fact]
    public void TheStockNormaliseLeavesTheDistanceAlone()
    {
        MapDisplacement disp = DispFixtures.Heightfield(2, Floor[0], (x, y) => (x * 1.37f) + (y * 0.11f));
        for (int i = 0; i < disp.VertCount; i++)
        {
            disp.VectorOffsets[i] = new Vec3(0.3f * i, 0.07f, 0);
        }

        (_, DisplacementLumps correct) = DispFixtures.Build([(disp, Floor)], ComplianceOptions.Correct);
        (_, DisplacementLumps stock) = DispFixtures.Build([(disp, Floor)], ComplianceOptions.Stock);

        for (int i = 0; i < disp.VertCount; i++)
        {
            Assert.True(DispFixtures.BitEqual(correct.Verts[i].Dist, stock.Verts[i].Dist), $"vertex {i}");
        }
    }

    private static DisplacementResult Result(int mapFace, bool swapped) =>
        new(new DispInfo { MapFace = (ushort)mapFace }, new CoreDispInfo(2), 0, 0, swapped);
}
