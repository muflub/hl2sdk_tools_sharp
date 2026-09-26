//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The parts of static-prop lighting that need no model: the <c>.vhv</c>
/// layout, <c>PositionInSolid</c>, the prop filters, and the indirect term on
/// the leaf-ambient fixture.
/// </summary>
public sealed class StaticPropUnitTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public StaticPropUnitTests(AmbientFixture fixture) => _fixture = fixture;

    private static byte[] TwoMeshes() => StaticPropLighting.EncodeVhv(
        0x12345678,
        [(0, [Vec3.Zero, Vec3.Zero, Vec3.Zero]), (1, [Vec3.Zero, Vec3.Zero])]);

    [Fact]
    public void TheHeaderCarriesVersionChecksumAndVertexFormat()
    {
        byte[] b = TwoMeshes();
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(b));
        Assert.Equal(0x12345678, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(StaticPropLighting.VertexColor, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12)));
    }

    [Fact]
    public void TheHeaderCountsVertexesAndMeshes()
    {
        byte[] b = TwoMeshes();
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20)));
    }

    [Fact]
    public void VertexDataStartsOnA512ByteBoundary()
    {
        byte[] b = TwoMeshes();
        Assert.Equal(512, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(StaticPropLighting.FileHeaderSize + 8)));
    }

    [Fact]
    public void EachMeshHeaderNamesItsLodCountAndOffset()
    {
        byte[] b = TwoMeshes();
        Span<byte> second = b.AsSpan(StaticPropLighting.FileHeaderSize + StaticPropLighting.MeshHeaderSize);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(second));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(second[4..]));
        Assert.Equal(512 + 12, BinaryPrimitives.ReadInt32LittleEndian(second[8..]));
    }

    [Fact]
    public void TheFileEndsOnA512ByteBoundary()
    {
        Assert.Equal(1024, TwoMeshes().Length);
    }

    [Fact]
    public void AFileWithNoMeshesIsOneAlignedBlock()
    {
        Assert.Equal(512, StaticPropLighting.EncodeVhv(1, []).Length);
    }

    [Fact]
    public void VertexColoursAreStoredBgra()
    {
        // Linear 0.25 red: vertex light 68, as in StockLightColorTests.
        byte[] b = StaticPropLighting.EncodeVhv(1, [(0, [new Vec3(0.25f * 255.0f, 0, 0)])]);
        Assert.Equal(new byte[] { 0, 0, 68, 255 }, b[512..516]);
    }

    [Fact]
    public void FileNamesFollowThePass()
    {
        Assert.Equal("sp_3.vhv", StaticPropLighting.FileName(3, hdr: false));
        Assert.Equal("sp_hdr_3.vhv", StaticPropLighting.FileName(3, hdr: true));
    }

    [Fact]
    public void ALightIsNotInSolid()
    {
        Vec3 origin = _fixture.Ldr.WorldLights[0].Origin;
        Assert.False(StaticPropLighting.PositionInSolid(_fixture.Ldr, origin));
    }

    [Fact]
    public void APointFarOutsideTheWorldIsInSolid()
    {
        Assert.True(StaticPropLighting.PositionInSolid(_fixture.Ldr, new Vec3(30000, 30000, 30000)));
    }

    [Fact]
    public async Task APropWithoutPerVertexLightingWritesNoFile()
    {
        StaticPropLightingResult r = await RunOneAsync(StaticPropFlags.NoPerVertexLighting | StaticPropFlags.NoPerTexelLighting);
        Assert.Empty(r.Files);
    }

    [Fact]
    public async Task APropWhoseModelDidNotLoadIsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunOneAsync(StaticPropFlags.NoPerTexelLighting));
    }

    [Fact]
    public async Task APropNamingAMissingModelIsRefused()
    {
        await Assert.ThrowsAsync<InvalidBspException>(() => RunOneAsync(StaticPropFlags.None, propType: 1));
    }

    [Fact]
    public void IgnoringNormalsMakesTheIndirectTermIndependentOfTheNormal()
    {
        Vec3 p = _fixture.Ldr.WorldLights[0].Origin;
        DispTestedScratch scratch = _fixture.Ldr.Tracer.Displacements.CreateScratch();
        Vec3 up = PropIndirectLighting.Compute(_fixture.Ldr, p, new Vec3(0, 0, 1), true, true, scratch, ComplianceOptions.Stock);
        Vec3 down = PropIndirectLighting.Compute(_fixture.Ldr, p, new Vec3(0, 0, -1), true, true, scratch, ComplianceOptions.Stock);
        Assert.Equal(up, down);
    }

    [Fact]
    public void TheIndirectTermSeesLight()
    {
        Vec3 p = _fixture.Ldr.WorldLights[0].Origin;
        DispTestedScratch scratch = _fixture.Ldr.Tracer.Displacements.CreateScratch();
        Vec3 c = PropIndirectLighting.Compute(_fixture.Ldr, p, new Vec3(0, 0, -1), true, false, scratch, ComplianceOptions.Stock);
        Assert.True(c.X + c.Y + c.Z > 0);
    }

    [Fact]
    public void AReusedEnumeratorChangesTheIndirectTermSomewhere()
    {
        DispTestedScratch scratch = _fixture.Ldr.Tracer.Displacements.CreateScratch();
        ComplianceOptions fresh = ComplianceOptions.Stock.Flipping(StockQuirk.IndirectSurfaceEnumeratorReused);
        int differ = 0;
        foreach (DWorldLight light in _fixture.Ldr.WorldLights)
        {
            foreach (Vec3 n in new[] { new Vec3(0, 0, 1), new Vec3(0, 0, -1), new Vec3(1, 0, 0) })
            {
                Vec3 stock = PropIndirectLighting.Compute(_fixture.Ldr, light.Origin, n, true, false, scratch, ComplianceOptions.Stock);
                Vec3 correct = PropIndirectLighting.Compute(_fixture.Ldr, light.Origin, n, true, false, scratch, fresh);
                differ += stock == correct ? 0 : 1;
            }
        }

        Assert.True(differ > 0);
    }

    private async Task<StaticPropLightingResult> RunOneAsync(StaticPropFlags flags, ushort propType = 0)
    {
        await using ContentFileSystem empty = new([]);
        StaticPropModel missing = await new StaticPropModelLoader(empty, NullPropCollisionSource.Instance)
            .LoadAsync("models/p4g/none.mdl");

        StaticPropLump lump = new();
        lump.ModelNames.Add("models/p4g/none.mdl");
        lump.Props.Add(new StaticProp { PropType = propType, Flags = flags, Origin = _fixture.Ldr.WorldLights[0].Origin });

        PropLightSampler sampler = new(
            KdRayTracer.Build([new TracedTriangle(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), 0)]),
            ComplianceOptions.Stock);
        return await StaticPropLighting.ComputeAsync(
            _fixture.Ldr, lump, [missing], [], sampler, new StaticPropLightingOptions(), CancellationToken.None);
    }
}
