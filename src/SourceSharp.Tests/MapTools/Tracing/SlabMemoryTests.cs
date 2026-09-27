//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// <see cref="SlabMemory"/>'s slot sizing and memory-type choices, on the
/// memory layouts real devices report: a unified-memory device (llvmpipe,
/// integrated GPUs), a discrete GPU without resizable BAR, and one with it.
/// </summary>
public sealed class SlabMemoryTests
{
    private const uint DL = SlabMemory.DeviceLocal;
    private const uint HV = SlabMemory.HostVisible;
    private const uint HC = SlabMemory.HostCoherent;
    private const uint Cached = SlabMemory.HostCached;
    private const ulong GiB = 1UL << 30;
    private const ulong SpecMinStorage = 128UL << 20;

    // Rays and answers for the default budget, all slots together.
    private const ulong RayBytes = 4_194_304UL * 32;
    private const ulong OutBytes = 4_194_304UL * 8;

    private static SlabMemoryLayout Choose(uint[] flags, int[] heaps, ulong[] heapSizes, uint allowed = ~0u, bool forceStaged = false) =>
        SlabMemory.Choose(flags, heaps, heapSizes, allowed, allowed, RayBytes, OutBytes, forceStaged);

    [Theory]
    [InlineData(4_194_304L, 1, 4_194_304)] // one slot takes the whole budget
    [InlineData(4_194_304L, 3, 1_398_080)] // a third, in whole workgroups
    [InlineData(4_194_304L, 2, 2_097_152)]
    [InlineData(1_000L, 1, 960)] // rounded down to a workgroup
    [InlineData(64L, 3, 64)] // never below one workgroup
    [InlineData(0L, 3, 64)] // a defaulted options value
    [InlineData(-5L, 3, 64)]
    [InlineData(100_000_000L, 1, 4_194_304)] // the binding limit caps a slot
    public void TheBudgetIsSharedByTheSlots(long budget, int slots, int expected) =>
        Assert.Equal(expected, SlabMemory.RaysPerSlot(budget, slots, SpecMinStorage));

    [Fact]
    public void ALargerBindingLimitLetsASlotGrowUpToWhatOneDispatchAddresses()
    {
        Assert.Equal(8_388_608, SlabMemory.RaysPerSlot(8_388_608, 1, 4UL * GiB));
        int huge = SlabMemory.RaysPerSlot(long.MaxValue, 1, ulong.MaxValue);
        Assert.Equal(0, huge % 64);
        Assert.True((long)huge * 8 <= int.MaxValue, "the batcher's answer buffer must stay addressable");
    }

    [Fact]
    public void FindTypeTakesThePreferredFlagsWhenSomeAllowedTypeHasThem()
    {
        uint[] flags = [DL, HV | HC, DL | HV | HC, HV | HC | Cached];

        Assert.Equal(3, SlabMemory.FindType(flags, ~0u, HV | HC, HV | HC | Cached));
        Assert.Equal(1, SlabMemory.FindType(flags, ~0u, HV | HC, HV | HC));
        Assert.Equal(0, SlabMemory.FindType(flags, ~0u, DL, DL));
    }

    [Fact]
    public void FindTypeFallsBackToTheRequiredFlagsAndHonoursTheAllowedBits()
    {
        uint[] flags = [DL, HV | HC, DL | HV | HC, HV | HC | Cached];

        // The cached type is not allowed: the first coherent one is.
        Assert.Equal(1, SlabMemory.FindType(flags, 0b0111, HV | HC, HV | HC | Cached));

        // Only the BAR type is allowed and has the flags.
        Assert.Equal(2, SlabMemory.FindType(flags, 0b0100, HV | HC, HV | HC | Cached));

        // Nothing allowed has them.
        Assert.Equal(-1, SlabMemory.FindType(flags, 0b0001, HV | HC, HV | HC));
    }

    [Fact]
    public void UnifiedMemoryReadsRaysAndWritesAnswersInPlace()
    {
        // llvmpipe's one type, and an integrated GPU's: everything at once.
        uint[] flags = [DL | HV | HC | Cached];

        Assert.Equal(new SlabMemoryLayout(true, true), Choose(flags, [0], [16 * GiB]));
    }

    [Fact]
    public void UnifiedMemoryWithoutCachedHostReadsStillCopiesAnswersBack()
    {
        uint[] flags = [DL | HV | HC];

        Assert.Equal(new SlabMemoryLayout(true, false), Choose(flags, [0], [16 * GiB]));
    }

    [Fact]
    public void ADiscreteGpuWithoutResizableBarKeepsBothCopies()
    {
        // VRAM, system memory, the 256 MB BAR, cached system memory.
        uint[] flags = [DL, HV | HC, DL | HV | HC, HV | HC | Cached];
        int[] heaps = [0, 1, 2, 1];
        ulong[] heapSizes = [16 * GiB, 32 * GiB, 256UL << 20];

        Assert.Equal(new SlabMemoryLayout(false, false), Choose(flags, heaps, heapSizes));
    }

    [Fact]
    public void ADiscreteGpuWithResizableBarReadsRaysInPlaceAndCopiesAnswersBack()
    {
        // The BAR type now sits on the whole VRAM heap; it is still uncached
        // for the host, so answers keep their copy into cached memory.
        uint[] flags = [DL, HV | HC, DL | HV | HC, HV | HC | Cached];
        int[] heaps = [0, 1, 0, 1];
        ulong[] heapSizes = [16 * GiB, 32 * GiB];

        Assert.Equal(new SlabMemoryLayout(true, false), Choose(flags, heaps, heapSizes));
    }

    [Fact]
    public void ForcingStagedKeepsBothCopiesEvenOnUnifiedMemory()
    {
        uint[] flags = [DL | HV | HC | Cached];

        Assert.Equal(new SlabMemoryLayout(false, false), Choose(flags, [0], [16 * GiB], forceStaged: true));
    }

    [Fact]
    public void ATypeTheBuffersMayNotUseIsNotReliedOn()
    {
        uint[] flags = [DL, DL | HV | HC | Cached];

        Assert.Equal(new SlabMemoryLayout(false, false), Choose(flags, [0, 0], [16 * GiB], allowed: 0b01));
        Assert.Equal(new SlabMemoryLayout(true, true), Choose(flags, [0, 0], [16 * GiB], allowed: 0b10));
    }

    [Fact]
    public void AHeapMustHoldFourTimesTheSlabsAndAtLeastTheFloor()
    {
        uint[] flags = [DL | HV | HC];

        // Four times 1 GiB of rays is 4 GiB: a 2 GiB heap is too tight, 4 GiB is enough.
        Assert.Equal(-1, SlabMemory.FindRoomyType(flags, [0], [2 * GiB], ~0u, DL | HV | HC, GiB));
        Assert.Equal(0, SlabMemory.FindRoomyType(flags, [0], [4 * GiB], ~0u, DL | HV | HC, GiB));

        // Tiny slabs still need the 1 GiB floor.
        Assert.Equal(-1, SlabMemory.FindRoomyType(flags, [0], [GiB - 1], ~0u, DL | HV | HC, 1024));
        Assert.Equal(0, SlabMemory.FindRoomyType(flags, [0], [GiB], ~0u, DL | HV | HC, 1024));

        // A byte count past a quarter of the address space cannot overflow into a small floor.
        Assert.Equal(-1, SlabMemory.FindRoomyType(flags, [0], [ulong.MaxValue - 1], ~0u, DL | HV | HC, ulong.MaxValue / 2));
    }

    [Theory]
    [InlineData(0, SlabMemory.DefaultSlots)] // a defaulted options value
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(SlabMemory.MaxSlots, SlabMemory.MaxSlots)]
    public void TheTracerOptionsNameTheSlotCount(int asked, int slots) =>
        Assert.Equal(slots, VulkanRayTracer.SlotsFor(new VulkanRayTracerOptions { SlabsInFlight = asked }));

    [Theory]
    [InlineData(-1)]
    [InlineData(SlabMemory.MaxSlots + 1)]
    public void AnOutOfRangeSlotCountIsRefusedBeforeAnyDeviceOpens(int asked)
    {
        VulkanRayTracerOptions options = new() { SlabsInFlight = asked };

        Assert.Throws<ArgumentOutOfRangeException>(() => VulkanRayTracer.SlotsFor(options));

        // TryCreate refuses it at the call, before it touches the loader.
        Assert.Throws<ArgumentOutOfRangeException>(() => VulkanRayTracer.TryCreate(ReadOnlyMemory<TracedTriangle>.Empty, options));
    }

    [Fact]
    public void AHeapIndexTheDeviceDidNotReportIsNeverRoomy()
    {
        uint[] flags = [DL | HV | HC | Cached];

        Assert.Equal(-1, SlabMemory.FindRoomyType(flags, [3], [16 * GiB], ~0u, DL | HV | HC, 1024));
        Assert.Equal(-1, SlabMemory.FindRoomyType(flags, [-1], [16 * GiB], ~0u, DL | HV | HC, 1024));
    }
}
