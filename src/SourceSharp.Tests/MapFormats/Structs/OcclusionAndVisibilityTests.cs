//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The two lumps that are not arrays: occlusion and visibility.
/// </summary>
public class OcclusionAndVisibilityTests : IClassFixture<LockdownFixture>
{
    private readonly LockdownFixture _fixture;

    /// <summary>Takes the shared golden map.</summary>
    /// <param name="fixture">The fixture xUnit constructs once.</param>
    public OcclusionAndVisibilityTests(LockdownFixture fixture) => _fixture = fixture;

    private static OcclusionLump Sample()
    {
        OcclusionLump lump = new();
        lump.Occluders.Add(new DOccluderData
        {
            Flags = (int)OccluderFlags.Inactive,
            FirstPoly = 0,
            PolyCount = 2,
            Mins = new Vec3(-1, -2, -3),
            Maxs = new Vec3(4, 5, 6),
            Area = 1,
        });
        lump.Polys.Add(new DOccluderPolyData { FirstVertexIndex = 0, VertexCount = 4, PlaneNum = 7 });
        lump.Polys.Add(new DOccluderPolyData { FirstVertexIndex = 4, VertexCount = 4, PlaneNum = 8 });
        lump.VertexIndices.AddRange([0, 1, 2, 3, 4, 5, 6, 7]);
        return lump;
    }

    [Fact]
    public void AnEmptyOcclusionLumpIsTwelveBytesOfZeroCounts()
    {
        // Layout: three ints even when everything is empty, which
        // is exactly what dm_lockdown.bsp's 12-byte lump is.
        Assert.Equal(12, new OcclusionLump().Write().Length);
    }

    [Fact]
    public void TheGoldenMapsOcclusionLumpIsTheEmptyTwelveByteForm()
    {
        BspLumpData lump = _fixture.Bsp[BspLump.Occlusion];

        Assert.Equal(12, lump.Length);
    }

    [Fact]
    public void TheGoldenMapsOcclusionLumpIsVersionTwo()
    {
        Assert.Equal(2, _fixture.Bsp[BspLump.Occlusion].Version);
    }

    [Fact]
    public void TheGoldenMapsOcclusionLumpDecodesToNothing()
    {
        OcclusionLump lump = OcclusionLump.Read(_fixture.Bsp[BspLump.Occlusion]);

        Assert.Empty(lump.Occluders);
        Assert.Empty(lump.Polys);
        Assert.Empty(lump.VertexIndices);
    }

    [Fact]
    public void OcclusionWritesAtVersionTwo()
    {
        // The reference implementation, LUMP_OCCLUSION_VERSION.
        Assert.Equal(2, Sample().Write().Version);
    }

    [Fact]
    public void OcclusionRoundTripsItsOccluders()
    {
        OcclusionLump read = OcclusionLump.Read(Sample().Write());

        Assert.Single(read.Occluders);
        Assert.Equal(1, read.Occluders[0].Area);
    }

    [Fact]
    public void OcclusionRoundTripsItsPolygons()
    {
        OcclusionLump read = OcclusionLump.Read(Sample().Write());

        Assert.Equal(2, read.Polys.Count);
        Assert.Equal(8, read.Polys[1].PlaneNum);
    }

    [Fact]
    public void OcclusionRoundTripsItsVertexIndices()
    {
        OcclusionLump read = OcclusionLump.Read(Sample().Write());

        Assert.Equal<int[]>([0, 1, 2, 3, 4, 5, 6, 7], [.. read.VertexIndices]);
    }

    [Fact]
    public void OcclusionIsByteExactOnASecondPass()
    {
        BspLumpData once = Sample().Write();
        BspLumpData twice = OcclusionLump.Read(once).Write();

        Assert.True(once.Data.Span.SequenceEqual(twice.Data.Span));
    }

    [Fact]
    public void OcclusionLengthMatchesBsplibsArithmetic()
    {
        // Occluders*40 + polys*12 + indices*4 + 3*4.
        Assert.Equal((1 * 40) + (2 * 12) + (8 * 4) + 12, Sample().Write().Length);
    }

    [Fact]
    public void OcclusionVersionZeroDecodesToNothingWhateverItsLengthSays()
    {
        // "case 0: break;". A version 0 lump is not read at
        // all, so bytes that look like counts are ignored.
        BspLumpData lump = Sample().Write() with { Version = 0 };

        Assert.Empty(OcclusionLump.Read(lump).Occluders);
    }

    [Fact]
    public void OcclusionVersionOneReadsTheShorterOccluderStruct()
    {
        // DoccluderdataV1_t has no area field, so a version 1
        // lump is 36 bytes per occluder and reading it with the 40-byte struct
        // shifts everything after the first.
        DOccluderDataV1 v1 = new()
        {
            Flags = 0,
            FirstPoly = 0,
            PolyCount = 1,
            Mins = new Vec3(-1, -1, -1),
            Maxs = new Vec3(1, 1, 1),
        };

        List<byte> bytes = [];
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(new ReadOnlySpan<DOccluderDataV1>(in v1)).ToArray());
        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.AddRange(BitConverter.GetBytes(0));

        OcclusionLump read = OcclusionLump.Read(new BspLumpData(bytes.ToArray(), 1, 0));

        Assert.Equal(1, read.Occluders[0].PolyCount);
    }

    [Fact]
    public void OcclusionVersionOneOccludersGetAreaZero()
    {
        // The field did not exist, and 0 is the outside-of-all-areas default
        // the reference implementation already uses.
        DOccluderDataV1 v1 = new() { PolyCount = 1 };

        List<byte> bytes = [];
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(new ReadOnlySpan<DOccluderDataV1>(in v1)).ToArray());
        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.AddRange(BitConverter.GetBytes(0));

        OcclusionLump read = OcclusionLump.Read(new BspLumpData(bytes.ToArray(), 1, 0));

        Assert.Equal(0, read.Occluders[0].Area);
    }

    [Fact]
    public void OcclusionRejectsAnUnknownVersion()
    {
        BspLumpData lump = Sample().Write() with { Version = 3 };

        Assert.Throws<InvalidBspException>(() => OcclusionLump.Read(lump));
    }

    [Fact]
    public void OcclusionRejectsALumpThatRunsShort()
    {
        byte[] bytes = BitConverter.GetBytes(5);

        Assert.Throws<InvalidBspException>(() =>
            OcclusionLump.Read(new BspLumpData(bytes, 2, 0)));
    }

    [Fact]
    public void AnEmptyVisibilityLumpReadsAsNullRatherThanThrowing()
    {
        // vvis has not run. That is a normal state mid-compile, not corruption.
        Assert.Null(VisibilityLump.Read(BspLumpData.Empty));
    }

    [Fact]
    public void TheGoldenMapsVisibilityLumpDeclaresAClusterCount()
    {
        VisibilityLump? vis = VisibilityLump.Read(_fixture.Bsp[BspLump.Visibility]);

        Assert.NotNull(vis);
        Assert.True(vis.NumClusters > 0);
    }

    [Fact]
    public void TheGoldenMapsClusterCountFitsInsideItsLeafCount()
    {
        // Every cluster is at least one leaf, so clusters can never exceed
        // leaves. A visibility header read at the wrong offset blows past this
        // by orders of magnitude.
        VisibilityLump vis = VisibilityLump.Read(_fixture.Bsp[BspLump.Visibility])!;
        int leaves = _fixture.Bsp[BspLump.Leafs].Length / 56;

        Assert.InRange(vis.NumClusters, 1, leaves);
    }

    [Fact]
    public void TheGoldenMapsVisibilityOffsetsAllLieInsideTheLump()
    {
        VisibilityLump vis = VisibilityLump.Read(_fixture.Bsp[BspLump.Visibility])!;
        int length = _fixture.Bsp[BspLump.Visibility].Length;

        for (int cluster = 0; cluster < vis.NumClusters; cluster++)
        {
            Assert.InRange(vis.BitOffset(cluster, VisibilityLump.Pvs), 0, length - 1);
            Assert.InRange(vis.BitOffset(cluster, VisibilityLump.Pas), 0, length - 1);
        }
    }

    [Fact]
    public void AVisibilityRowIsOneBitPerCluster()
    {
        // CompressVis: (numclusters + 7) >> 3.
        VisibilityLump vis = VisibilityLump.Read(_fixture.Bsp[BspLump.Visibility])!;

        Assert.Equal((vis.NumClusters + 7) / 8, vis.RowBytes());
    }

    [Fact]
    public void EveryClusterSeesItselfInItsOwnPvs()
    {
        // The one semantic invariant of a vised map that needs no other lump:
        // a cluster is always in its own potentially visible set. It fails
        // immediately if the offset table or the run-length decoder is wrong.
        BspLumpData lump = _fixture.Bsp[BspLump.Visibility];
        VisibilityLump vis = VisibilityLump.Read(lump)!;
        byte[] row = new byte[vis.RowBytes()];

        for (int cluster = 0; cluster < vis.NumClusters; cluster++)
        {
            int offset = vis.BitOffset(cluster, VisibilityLump.Pvs);
            vis.DecompressRow(lump.Data.Span[offset..], row);

            Assert.True(
                (row[cluster >> 3] & (1 << (cluster & 7))) != 0,
                $"cluster {cluster} is not in its own PVS");
        }
    }
}
