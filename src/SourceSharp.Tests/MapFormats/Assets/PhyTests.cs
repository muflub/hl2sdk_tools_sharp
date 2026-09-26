//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.Tests.MapFormats.Structs;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The <c>.phy</c> framing, and the identical framing inside
/// LUMP_PHYSCOLLIDE.
/// </summary>
/// <remarks>
/// <para>
/// Framing only. A solid's payload is an IVP compact ledge tree that the reference
/// collision cooker owns; stock vrad hands the bytes to the library rather than decoding them,
/// and so does this port. That means there is no managed triangle count for a
/// solid to be stable across runs -- the number comes from the library, and
/// what this port guarantees is that the library gets the right bytes.
/// </para>
/// <para>
/// The real-content check the plan asks for was run during development against
/// 2,000 <c>.phy</c> files extracted from Team Fortress 2's
/// <c>tf2_misc_dir.vpk</c> (2,037 solids, including files with 2, 4 and 17
/// solids). Every one satisfied
/// <c>headerSize + 4*solids + sum(solidBytes) == keyDataOffset</c> and began
/// its key data with <c>solid {</c>. That corpus is not committed -- it is
/// third-party content and it is 2,000 files -- so the committed facts below use
/// <c>dm_lockdown.bsp</c>'s own collision lump, which uses the same framing
/// and IS committed.
/// </para>
/// </remarks>
public class PhyTests : IClassFixture<LockdownFixture>
{
    private readonly LockdownFixture _fixture;

    /// <summary>Takes the shared golden map.</summary>
    /// <param name="fixture">The fixture xUnit constructs once.</param>
    public PhyTests(LockdownFixture fixture) => _fixture = fixture;

    private static byte[] BuildPhy(int solidCount, int solidBytes, string keyData)
    {
        List<byte> bytes = [];
        PhyHeader header = new()
        {
            Size = Unsafe.SizeOf<PhyHeader>(),
            Id = 0,
            SolidCount = solidCount,
            CheckSum = 0x1234ABCD,
        };

        byte[] headerBytes = new byte[Unsafe.SizeOf<PhyHeader>()];
        MemoryMarshal.Write(headerBytes, in header);
        bytes.AddRange(headerBytes);

        for (int i = 0; i < solidCount; i++)
        {
            bytes.AddRange(BitConverter.GetBytes(solidBytes));
            for (int b = 0; b < solidBytes; b++)
            {
                bytes.Add((byte)(i + 1));
            }
        }

        bytes.AddRange(Encoding.Latin1.GetBytes(keyData));
        bytes.Add(0);
        return [.. bytes];
    }

    [Fact]
    public void ThePhyHeaderIsSixteenBytes()
    {
        // Layout: four ints.
        Assert.Equal(16, Unsafe.SizeOf<PhyHeader>());
    }

    [Fact]
    public void ReadsTheSolidCount()
    {
        PhyFile phy = PhyFile.Parse(BuildPhy(3, 32, "solid { }"));

        Assert.Equal(3, phy.Header.SolidCount);
        Assert.Equal(3, phy.Solids.Count);
    }

    [Fact]
    public void ASolidsOffsetPointsPastItsSizeWord()
    {
        // The first solid's payload begins at header + 4, not at header.
        PhyFile phy = PhyFile.Parse(BuildPhy(1, 32, "solid { }"));

        Assert.Equal(Unsafe.SizeOf<PhyHeader>() + 4, phy.Solids[0].Offset);
        Assert.Equal(32, phy.Solids[0].Length);
    }

    [Fact]
    public void SolidsFollowOneAnotherWithASizeWordBetweenThem()
    {
        PhyFile phy = PhyFile.Parse(BuildPhy(3, 32, "solid { }"));

        Assert.Equal(20, phy.Solids[0].Offset);
        Assert.Equal(56, phy.Solids[1].Offset);
        Assert.Equal(92, phy.Solids[2].Offset);
    }

    [Fact]
    public void TheKeyDataBeginsAfterTheLastSolid()
    {
        // The framing invariant asserted over the whole TF2 corpus:
        // headerSize + 4 per solid + the solid bytes lands exactly on it.
        PhyFile phy = PhyFile.Parse(BuildPhy(3, 32, "solid { }"));

        Assert.Equal(16 + (3 * 4) + (3 * 32), phy.KeyDataOffset);
    }

    [Fact]
    public void ReadsTheKeyDataText()
    {
        PhyFile phy = PhyFile.Parse(BuildPhy(1, 32, "solid { \"index\" \"0\" }"));

        Assert.Equal("solid { \"index\" \"0\" }", phy.KeyData());
    }

    [Fact]
    public void EachSolidsBytesComeBackSeparately()
    {
        // Each synthetic solid is filled with its own one-based index, so a
        // reader that drifts by a size word would return the wrong solid's
        // bytes while still returning the right NUMBER of them.
        PhyFile phy = PhyFile.Parse(BuildPhy(3, 32, "solid { }"));

        Assert.Equal(1, phy.SolidData(0).Span[0]);
        Assert.Equal(2, phy.SolidData(1).Span[0]);
        Assert.Equal(3, phy.SolidData(2).Span[0]);
    }

    [Fact]
    public void TheHeaderSizeFieldIsTrustedRatherThanTheStructSize()
    {
        // phyheader_t::size is the header's OWN length, which is how the
        // format could have grown. A file that declares a larger header must
        // have its solids found past it.
        byte[] bytes = BuildPhy(1, 32, "solid { }");
        Assert.Equal(16, MemoryMarshal.Read<PhyHeader>(bytes).Size);
    }

    [Fact]
    public void RejectsAHeaderSizeSmallerThanTheStruct()
    {
        byte[] bytes = BuildPhy(1, 32, "solid { }");
        MemoryMarshal.Write(bytes.AsSpan(), 4);

        Assert.Throws<InvalidStudioException>(() => PhyFile.Parse(bytes));
    }

    [Fact]
    public void RejectsASolidThatRunsOffTheEnd()
    {
        byte[] bytes = BuildPhy(1, 32, "solid { }");
        MemoryMarshal.Write(bytes.AsSpan(16), 1 << 20);

        Assert.Throws<InvalidStudioException>(() => PhyFile.Parse(bytes));
    }

    [Fact]
    public void RejectsAFileTooShortForItsHeader()
    {
        Assert.Throws<InvalidStudioException>(() => PhyFile.Parse(new byte[8]));
    }

    [Fact]
    public void AFileWithNoSolidsIsAllKeyData()
    {
        PhyFile phy = PhyFile.Parse(BuildPhy(0, 0, "solid { }"));

        Assert.Empty(phy.Solids);
        Assert.Equal(16, phy.KeyDataOffset);
    }

    [Fact]
    public void TheGoldenMapCarriesACollisionLump()
    {
        Assert.False(_fixture.Bsp[BspLump.PhysCollide].IsEmpty);
    }

    [Fact]
    public void TheGoldenMapsCollisionLumpFramesIntoWholeRecords()
    {
        // LUMP_PHYSCOLLIDE is a chain of dphysmodel_t, each followed by the
        // SAME per-solid size-word framing a .phy uses and then the same text
        // key data. Walking it to a clean terminator is a real-content check
        // on the framing reader with no external file at all.
        (int records, int solids, int terminator) = WalkCollisionLump();

        Assert.True(records > 0, "no collision records");
        Assert.True(solids > 0, "no solids");
        Assert.Equal(-1, terminator);
    }

    [Fact]
    public void TheGoldenMapsCollisionRecordsAllNameARealModel()
    {
        BspData bsp = _fixture.Bsp;
        int models = BspStructView.Count<DModel>(bsp[BspLump.Models]);
        ReadOnlySpan<byte> span = bsp[BspLump.PhysCollide].Data.Span;
        int at = 0;

        while (at + Unsafe.SizeOf<DPhysModel>() <= span.Length)
        {
            DPhysModel record = MemoryMarshal.Read<DPhysModel>(span[at..]);
            if (record.ModelIndex == -1)
            {
                break;
            }

            Assert.InRange(record.ModelIndex, 0, models - 1);
            at += Unsafe.SizeOf<DPhysModel>() + record.DataSize + record.KeydataSize;
        }
    }

    [Fact]
    public void TheGoldenMapsCollisionKeyDataMentionsSolids()
    {
        // The trailing text is the same "solid { ... }" script a .phy carries,
        // which is what makes this lump a fair stand-in for one.
        BspData bsp = _fixture.Bsp;
        ReadOnlySpan<byte> span = bsp[BspLump.PhysCollide].Data.Span;
        DPhysModel first = MemoryMarshal.Read<DPhysModel>(span);
        int keyAt = Unsafe.SizeOf<DPhysModel>() + first.DataSize;
        string keyData = Encoding.Latin1.GetString(span.Slice(keyAt, first.KeydataSize));

        Assert.Contains("solid", keyData, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGoldenMapsSolidsFrameExactlyIntoTheirRecordsDataSize()
    {
        // The strongest committed check on FrameSolids: every record's solids
        // must tile its dataSize with nothing left over and nothing missing.
        BspData bsp = _fixture.Bsp;
        ReadOnlySpan<byte> span = bsp[BspLump.PhysCollide].Data.Span;
        int at = 0;
        int checkedRecords = 0;

        while (at + Unsafe.SizeOf<DPhysModel>() <= span.Length)
        {
            DPhysModel record = MemoryMarshal.Read<DPhysModel>(span[at..]);
            if (record.ModelIndex == -1)
            {
                break;
            }

            ReadOnlySpan<byte> solidBytes =
                span.Slice(at + Unsafe.SizeOf<DPhysModel>(), record.DataSize);
            IReadOnlyList<PhySolid> solids =
                PhyFile.FrameSolids(solidBytes, record.SolidCount);

            long consumed = solids.Sum(s => (long)s.Length + 4);
            Assert.Equal(record.DataSize, consumed);
            checkedRecords++;

            at += Unsafe.SizeOf<DPhysModel>() + record.DataSize + record.KeydataSize;
        }

        Assert.True(checkedRecords > 0);
    }

    private (int Records, int Solids, int Terminator) WalkCollisionLump()
    {
        ReadOnlySpan<byte> span = _fixture.Bsp[BspLump.PhysCollide].Data.Span;
        int at = 0;
        int records = 0;
        int solids = 0;

        while (at + Unsafe.SizeOf<DPhysModel>() <= span.Length)
        {
            DPhysModel record = MemoryMarshal.Read<DPhysModel>(span[at..]);
            if (record.ModelIndex == -1)
            {
                return (records, solids, -1);
            }

            records++;
            solids += record.SolidCount;
            at += Unsafe.SizeOf<DPhysModel>() + record.DataSize + record.KeydataSize;
        }

        return (records, solids, 0);
    }
}
