using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Disp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The driver-facing hooks: <c>DispGetFaceInfo</c>'s checks
/// (<c>disp_vbsp.cpp:622-660</c>) and the lump write.
/// </summary>
public sealed class DispVbspHooksTests
{
    private static readonly Vec3[] Floor = DispFixtures.UnitFloor();

    /// <summary>A displacement on a brush entity is fatal: <c>disp_vbsp.cpp:628-632</c>.</summary>
    [Fact]
    public void ADisplacementOnABrushEntityIsFatal()
    {
        MapCompileException e = Assert.Throws<MapCompileException>(
            () => DispVbspHooks.CheckBrush(3, "func_detail", 12, 4));

        Assert.Contains("func_detail", e.Message, StringComparison.Ordinal);
    }

    /// <summary>A non-quad displacement side is fatal: <c>disp_vbsp.cpp:639-640</c>.</summary>
    [Fact]
    public void ANonQuadDisplacementIsFatal()
    {
        Assert.Throws<MapCompileException>(() => DispVbspHooks.CheckBrush(0, "worldspawn", 1, 5));
    }

    /// <summary>A world quad passes.</summary>
    [Fact]
    public void AWorldQuadPasses()
    {
        DispVbspHooks.CheckBrush(0, "worldspawn", 1, 4);
    }

    /// <summary>The face takes the texinfo's lightmap rows 0 and 1, spatial parts only.</summary>
    [Fact]
    public void TheFaceTakesTheLightmapRows()
    {
        TexInfo t = default;
        for (int k = 0; k < 8; k++)
        {
            t.LightmapVecsLuxelsPerWorldUnits[k] = k + 1;
        }

        DisplacementFace f = DispVbspHooks.Face(4, Floor, 1, t);

        Assert.Equal(new Vec3(1, 2, 3), f.LightmapVecU);
        Assert.Equal(new Vec3(5, 6, 7), f.LightmapVecV);
    }

    /// <summary>The face copies the winding rather than aliasing the caller's.</summary>
    [Fact]
    public void TheFaceCopiesTheWinding()
    {
        Vec3[] winding = (Vec3[])Floor.Clone();

        DisplacementFace f = DispVbspHooks.Face(0, winding, 1, default);
        winding[0] = new Vec3(9, 9, 9);

        Assert.Equal(Vec3.Zero, f.Winding[0]);
    }

    /// <summary>
    /// The write sets each base face's lightmap size (<c>disp_vbsp.cpp:208-209</c>)
    /// and nothing else of it — the texinfo stays (the swap never reaches the BSP).
    /// </summary>
    [Fact]
    public void TheWriteSetsTheFaceLightmapSizeAndKeepsItsTexInfo()
    {
        BspData bsp = BspWithFaces(3, texInfo: 5);
        (IReadOnlyList<DisplacementResult> r, DisplacementLumps lumps) =
            DispFixtures.Build([(DispFixtures.Heightfield(2, Floor[0]), DispFixtures.FloorQuad(Vec3.Zero, 256, 128))]);

        DispVbspHooks.WriteLumps(bsp, r, lumps);

        DFace face = BspStructView.As<DFace>(bsp[BspLump.Faces])[0];
        Assert.Equal(17, face.LightmapTextureSizeInLuxels[0]);
        Assert.Equal(9, face.LightmapTextureSizeInLuxels[1]);
        Assert.Equal(5, face.TexInfo);
    }

    /// <summary>The four displacement lumps hold what the builder produced.</summary>
    [Fact]
    public void TheWriteFillsTheFourLumps()
    {
        BspData bsp = BspWithFaces(1, texInfo: 0);
        (IReadOnlyList<DisplacementResult> r, DisplacementLumps lumps) =
            DispFixtures.Build([(DispFixtures.Heightfield(3, Floor[0]), Floor)]);

        DispVbspHooks.WriteLumps(bsp, r, lumps);

        Assert.Equal(1, BspStructView.Count<DispInfo>(bsp[BspLump.DispInfo]));
        Assert.Equal(81, BspStructView.Count<DispVert>(bsp[BspLump.DispVerts]));
        Assert.Equal(128, BspStructView.Count<DispTri>(bsp[BspLump.DispTris]));
        Assert.Equal(lumps.LightmapSamplePositions.Count, bsp[BspLump.DispLightmapSamplePositions].Length);
    }

    /// <summary>A result naming a face that does not exist is refused.</summary>
    [Fact]
    public void AMissingBaseFaceIsRefused()
    {
        BspData bsp = BspWithFaces(1, texInfo: 0);
        (IReadOnlyList<DisplacementResult> r, DisplacementLumps lumps) =
            DispFixtures.Build([(DispFixtures.Heightfield(2, Floor[0]), Floor), (DispFixtures.Heightfield(2, Floor[0]), Floor)]);

        Assert.Throws<ArgumentException>(() => DispVbspHooks.WriteLumps(bsp, r, lumps));
    }

    private static BspData BspWithFaces(int count, short texInfo)
    {
        DFace[] faces = new DFace[count];
        for (int i = 0; i < count; i++)
        {
            faces[i].TexInfo = texInfo;
            faces[i].DispInfo = (short)i;
        }

        BspData bsp = new();
        bsp[BspLump.Faces] = BspStructView.ToLump<DFace>(faces, 0);
        return bsp;
    }
}
