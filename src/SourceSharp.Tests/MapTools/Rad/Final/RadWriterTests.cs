using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>vrad's <c>SaveVertexNormals</c> and the lumps one pass writes.</summary>
public sealed class RadWriterTests
{
    [Fact]
    public void ABoxHasSixUniqueCornerNormals()
    {
        // -degree corners are not smoothed, so every
        // corner of a face carries that face's normal.
        RadWorld world = LightBox.Build(LightBox.Map());
        (Vec3[] normals, _) = VradVertexNormals.Save(world.Geometry, world.Neighbours);

        Assert.Equal(6, normals.Length);
    }

    [Fact]
    public void EveryFaceCornerGetsOneIndex()
    {
        // One entry per edge of every face, in face order.
        RadWorld world = LightBox.Build(LightBox.Map());
        (_, ushort[] indices) = VradVertexNormals.Save(world.Geometry, world.Neighbours);

        Assert.Equal(24, indices.Length);
    }

    [Fact]
    public void AFacesCornersShareItsFirstNormalsIndex()
    {
        RadWorld world = LightBox.Build(LightBox.Map());
        (_, ushort[] indices) = VradVertexNormals.Save(world.Geometry, world.Neighbours);

        Assert.Equal([1, 1, 1, 1], indices[4..8]);
    }

    [Fact]
    public void TheNormalsLumpIsTwelveBytesANormal()
    {
        (byte[] n, byte[] x) = VradVertexNormals.ToLumps([new Vec3(0, 0, 1)], [0, 0, 0]);
        Assert.Equal((12, 6, 1.0f), (n.Length, x.Length, BitConverter.ToSingle(n, 8)));
    }

    [Fact]
    public void AnHdrPassWritesFacesHdrFromTheLdrFaces()
    {
        //: dfaces_hdr starts as a copy of dfaces.
        BspData bsp = LightBox.Map().Build();
        LightmapLayout layout = new([0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255,
            0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255], [4, 100, 200, 300, 400, 500], 600);

        RadLumpWriter.WriteFaces(bsp, layout, hdr: true);

        DFace[] hdr = MemoryMarshal.Cast<byte, DFace>(bsp[BspLump.FacesHdr].Data.Span).ToArray();
        Assert.Equal((6, 100, (byte)0), (hdr.Length, hdr[1].LightOfs, hdr[1].Styles[0]));
    }

    [Fact]
    public void AnHdrPassLeavesTheLdrFacesAlone()
    {
        BspData bsp = LightBox.Map().Build();
        byte[] before = bsp[BspLump.Faces].Data.ToArray();
        LightmapLayout layout = new(new byte[24], [4, 100, 200, 300, 400, 500], 600);

        RadLumpWriter.WriteFaces(bsp, layout, hdr: true);

        Assert.Equal(before, bsp[BspLump.Faces].Data.ToArray());
    }

    [Fact]
    public void StaticPropLightingSetsThisPassesLevelFlag()
    {
        BspData bsp = new();
        RadLumpWriter.WriteLevelFlags(bsp, hdr: true, staticPropLighting: true);
        Assert.Equal(RadLumpWriter.BakedStaticPropLightingHdr, BinaryPrimitives.ReadUInt32LittleEndian(bsp[BspLump.MapFlags].Data.Span));
    }

    [Fact]
    public void WithoutStaticPropLightingBothLevelFlagsAreCleared()
    {
        //: an LDR rerun forgets an HDR bake.
        BspData bsp = new();
        byte[] both = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(both, 3u | 0x10u);
        bsp.SetLump(BspLump.MapFlags, both, 0);

        RadLumpWriter.WriteLevelFlags(bsp, hdr: false, staticPropLighting: false);
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(bsp[BspLump.MapFlags].Data.Span));
    }

    [Fact]
    public void LeafFlagsReplaceTheTopSevenBitsAndKeepTheArea()
    {
        // dleaf_t: area:9, flags:7 in one short at byte 6.
        RadWorld world = LightBox.Build(LightBox.Map());
        BspData bsp = LightBox.Map().Build();
        byte[] leafs = bsp[BspLump.Leafs].Data.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(leafs.AsSpan(6), (ushort)(5 | (0x7F << 9)));
        bsp.SetLump(BspLump.Leafs, leafs, 1);
        world.SkyLeaves.Build(
            world.Geometry, world.Visibility, new DirectLight(), new DirectLight());

        RadLumpWriter.WriteLeafFlags(bsp, world);

        ushort word = BinaryPrimitives.ReadUInt16LittleEndian(bsp[BspLump.Leafs].Data.Span[6..]);
        Assert.Equal((5, 0), (word & 0x1FF, word >> 9));
    }
}

/// <summary><c>-luxeldensity</c>.</summary>
public sealed class LuxelDensityTests
{
    [Fact]
    public void ADensityAboveOneIsItsReciprocal()
    {
        Assert.Equal(0.5f, LuxelDensity.Effective(2f));
    }

    [Fact]
    public void ADensityBelowOneIsTakenAsGiven()
    {
        Assert.Equal(0.25f, LuxelDensity.Effective(0.25f));
    }

    [Fact]
    public void TheCapShortensALightmapAxisToTheDensity()
    {
        // The box is 1/16 luxel a unit; capped at 1/32.
        BspData bsp = LightBox.Map().Build();
        LuxelDensity.Apply(bsp, 1f / 32, hdr: false, ComplianceOptions.Correct);

        TexInfo tex = MemoryMarshal.Cast<byte, TexInfo>(bsp[BspLump.TexInfo].Data.Span)[0];
        Assert.Equal(1f / 32, tex.LightmapVecsLuxelsPerWorldUnits[0]);
    }

    [Fact]
    public void AnAxisAlreadyCoarserThanTheCapIsLeftAlone()
    {
        BspData bsp = LightBox.Map().Build();
        LuxelDensity.Apply(bsp, 0.5f, hdr: false, ComplianceOptions.Correct);

        TexInfo tex = MemoryMarshal.Cast<byte, TexInfo>(bsp[BspLump.TexInfo].Data.Span)[0];
        Assert.Equal(1f / 16, tex.LightmapVecsLuxelsPerWorldUnits[0]);
    }

    [Fact]
    public void TheFacesExtentsFollowTheNewAxes()
    {
        // CalcFaceExtents: 256 units at 1/32 is 8 luxels.
        BspData bsp = LightBox.Map().Build();
        LuxelDensity.Apply(bsp, 1f / 32, hdr: false, ComplianceOptions.Correct);

        DFace floor = MemoryMarshal.Cast<byte, DFace>(bsp[BspLump.Faces].Data.Span)[0];
        Assert.Equal(8, floor.LightmapTextureSizeInLuxels[0]);
    }

    [Fact]
    public void StocksHdrPassKeepsTheOldExtents()
    {
        BspData bsp = LightBox.Map().Build();
        LuxelDensity.Apply(bsp, 1f / 32, hdr: true, ComplianceOptions.Stock);

        DFace floor = MemoryMarshal.Cast<byte, DFace>(bsp[BspLump.FacesHdr].Data.Span)[0];
        Assert.Equal(16, floor.LightmapTextureSizeInLuxels[0]);
    }

    [Fact]
    public void TheCorrectHdrPassGetsTheNewExtents()
    {
        BspData bsp = LightBox.Map().Build();
        bsp.SetLump(BspLump.FacesHdr, bsp[BspLump.Faces].Data.ToArray(), 1);
        LuxelDensity.Apply(bsp, 1f / 32, hdr: true, ComplianceOptions.Correct);

        DFace floor = MemoryMarshal.Cast<byte, DFace>(bsp[BspLump.FacesHdr].Data.Span)[0];
        Assert.Equal(8, floor.LightmapTextureSizeInLuxels[0]);
    }
}
