//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// Where a device's slab buffers live, decided once when the device opens.
/// </summary>
/// <param name="DirectRays">
/// The kernel reads rays straight from the memory the host packs them into,
/// so a slab has no upload copy. False keeps a host staging buffer and a
/// device-local ray buffer with a copy between them.
/// </param>
/// <param name="DirectOut">
/// The kernel writes answers straight into memory the host reads, so a slab
/// has no download copy. False keeps a device-local output buffer and a copy
/// into host memory.
/// </param>
internal readonly record struct SlabMemoryLayout(bool DirectRays, bool DirectOut);

/// <summary>
/// The sizing and memory-type rules for a device's slab slots, as pure
/// functions over what the device reports, so they are testable without a
/// GPU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Budget.</b> The requested rays per slab are a budget shared by every
/// slot, not a size each slot gets. A slot holds 40 bytes a ray on the host
/// (32 of rays, 8 of answers) and as much again on the device when copies
/// are staged, so the default 4,194,304-ray budget is 160 MB each side, the
/// same as a single slab took before slabs were pipelined. Giving each of
/// three slots the full budget would triple that for no gain: vrad's queue
/// rarely holds more than a few hundred thousand rays, so a third of the
/// budget (about 1.4 million rays) still fits everything queued in one slab.
/// </para>
/// <para>
/// <b>Rays read in place.</b> A memory type that is device-local, host
/// visible and host coherent lets the host pack rays where the kernel reads
/// them. On an integrated GPU, an APU or a CPU device (llvmpipe) every type
/// is like that and the upload copy is pure waste. On a discrete GPU the
/// type is the PCIe BAR: with resizable BAR (AMD SAM) its heap is the whole
/// of VRAM, and host writes to it are write-combined streams across the bus,
/// which is what the upload copy did anyway, minus the second pass. Without
/// resizable BAR the heap is 256 MB shared with the driver, too small to
/// spend on slabs, so a heap under <see cref="DirectHeapFloor"/> (or under
/// four times the slabs' bytes) keeps the staged copy.
/// </para>
/// <para>
/// Host-visible memory that is not device-local (reading rays across PCIe
/// from system memory inside the kernel) is deliberately not used: each
/// lane's two loads would stall a ray-query kernel on bus latency, and a
/// copy engine moves the same bytes at full bandwidth.
/// </para>
/// <para>
/// <b>Answers written in place.</b> The host reads answers back, and reads
/// from uncached memory (the BAR, or write-combined system memory) are
/// slow, so the kernel writes answers in place only into a type that is
/// device-local and host-cached as well, which in practice means unified
/// memory. Elsewhere the answers are copied into host-cached memory when
/// the device offers it, and into any host-coherent memory when it does not.
/// </para>
/// <para>
/// None of this can change an answer: the kernel reads the same bytes and
/// writes the same words whichever buffer holds them.
/// </para>
/// </remarks>
internal static class SlabMemory
{
    /// <summary><c>VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT</c>.</summary>
    public const uint DeviceLocal = 0x1;

    /// <summary><c>VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT</c>.</summary>
    public const uint HostVisible = 0x2;

    /// <summary><c>VK_MEMORY_PROPERTY_HOST_COHERENT_BIT</c>.</summary>
    public const uint HostCoherent = 0x4;

    /// <summary><c>VK_MEMORY_PROPERTY_HOST_CACHED_BIT</c>.</summary>
    public const uint HostCached = 0x8;

    /// <summary>The smallest heap slab buffers are placed in directly: 1 GiB, four times a BAR without resizable BAR.</summary>
    public const ulong DirectHeapFloor = 1UL << 30;

    /// <summary>Slots a tracer keeps unless told otherwise: one running, one queued behind it, one being packed or scattered.</summary>
    public const int DefaultSlots = 3;

    /// <summary>The most slots a tracer may ask for; past a few, slots only shrink slabs.</summary>
    public const int MaxSlots = 8;

    private const int Workgroup = 64;

    /// <summary>
    /// Rays one slot holds: the budget split across the slots, clamped to
    /// what one storage-buffer binding and one dispatch can address, in whole
    /// 64-ray workgroups, and never below one workgroup.
    /// </summary>
    /// <param name="budgetRays">Rays requested for all slots together.</param>
    /// <param name="slots">How many slots share them.</param>
    /// <param name="maxStorageBufferRange">The device's storage-buffer binding limit, in bytes.</param>
    /// <returns>Rays per slot.</returns>
    public static int RaysPerSlot(long budgetRays, int slots, ulong maxStorageBufferRange)
    {
        // Rays are 32 bytes and the largest output is 8 bytes a ray, so the
        // ray buffer is the binding that meets the limit first. The last
        // clamp keeps a slot's staging (8 floats a ray) one addressable span.
        ulong perSlot = (ulong)Math.Max(0L, budgetRays) / (ulong)Math.Max(1, slots);
        ulong byBinding = maxStorageBufferRange / 32UL;
        ulong byDispatch = (ulong)uint.MaxValue / Workgroup * Workgroup;
        ulong cap = Math.Min(perSlot, Math.Min(byBinding, Math.Min(byDispatch, int.MaxValue / 8)));
        cap &= ~(ulong)(Workgroup - 1);
        return (int)Math.Max(Workgroup, cap);
    }

    /// <summary>
    /// The first memory type a buffer may use that has every
    /// <paramref name="required"/> flag and every <paramref name="preferred"/>
    /// one, else the first with the required flags alone.
    /// </summary>
    /// <param name="typeFlags">Each memory type's property flags, in the device's order.</param>
    /// <param name="allowedBits">The buffer's <c>memoryTypeBits</c>.</param>
    /// <param name="required">Flags the type must have.</param>
    /// <param name="preferred">Flags taken when some allowed type has them too.</param>
    /// <returns>The type index, or -1 when no allowed type has the required flags.</returns>
    public static int FindType(ReadOnlySpan<uint> typeFlags, uint allowedBits, uint required, uint preferred)
    {
        int fallback = -1;
        for (int i = 0; i < typeFlags.Length && i < 32; i++)
        {
            if ((allowedBits & (1u << i)) == 0 || (typeFlags[i] & required) != required)
            {
                continue;
            }

            if ((typeFlags[i] & preferred) == preferred)
            {
                return i;
            }

            if (fallback < 0)
            {
                fallback = i;
            }
        }

        return fallback;
    }

    /// <summary>Decides where the slab buffers live (see the class remarks).</summary>
    /// <param name="typeFlags">Each memory type's property flags.</param>
    /// <param name="typeHeaps">Each memory type's heap index.</param>
    /// <param name="heapSizes">Each heap's size in bytes.</param>
    /// <param name="rayAllowedBits"><c>memoryTypeBits</c> of a ray buffer the kernel reads.</param>
    /// <param name="outAllowedBits"><c>memoryTypeBits</c> of an output buffer the kernel writes.</param>
    /// <param name="rayBytes">Ray bytes across every slot.</param>
    /// <param name="outBytes">Output bytes across every slot.</param>
    /// <param name="forceStaged">Keep both copies whatever the device offers; facts use it to compare the paths.</param>
    /// <returns>The layout.</returns>
    public static SlabMemoryLayout Choose(
        ReadOnlySpan<uint> typeFlags,
        ReadOnlySpan<int> typeHeaps,
        ReadOnlySpan<ulong> heapSizes,
        uint rayAllowedBits,
        uint outAllowedBits,
        ulong rayBytes,
        ulong outBytes,
        bool forceStaged)
    {
        if (forceStaged)
        {
            return new SlabMemoryLayout(false, false);
        }

        const uint InPlace = DeviceLocal | HostVisible | HostCoherent;
        bool directRays = HasRoomy(typeFlags, typeHeaps, heapSizes, rayAllowedBits, InPlace, rayBytes);
        bool directOut = HasRoomy(typeFlags, typeHeaps, heapSizes, outAllowedBits, InPlace | HostCached, outBytes);
        return new SlabMemoryLayout(directRays, directOut);
    }

    /// <summary>
    /// The type <see cref="Choose"/> relied on for a direct buffer: the first
    /// allowed type with <paramref name="required"/> whose heap is roomy.
    /// </summary>
    /// <param name="typeFlags">Each memory type's property flags.</param>
    /// <param name="typeHeaps">Each memory type's heap index.</param>
    /// <param name="heapSizes">Each heap's size in bytes.</param>
    /// <param name="allowedBits">The buffer's <c>memoryTypeBits</c>.</param>
    /// <param name="required">Flags the type must have.</param>
    /// <param name="bytes">Bytes the slabs put in it.</param>
    /// <returns>The type index, or -1.</returns>
    public static int FindRoomyType(
        ReadOnlySpan<uint> typeFlags,
        ReadOnlySpan<int> typeHeaps,
        ReadOnlySpan<ulong> heapSizes,
        uint allowedBits,
        uint required,
        ulong bytes)
    {
        ulong floor = Math.Max(DirectHeapFloor, bytes > ulong.MaxValue / 4 ? ulong.MaxValue : bytes * 4);
        for (int i = 0; i < typeFlags.Length && i < 32; i++)
        {
            if ((allowedBits & (1u << i)) == 0 || (typeFlags[i] & required) != required)
            {
                continue;
            }

            int heap = typeHeaps[i];
            if (heap >= 0 && heap < heapSizes.Length && heapSizes[heap] >= floor)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasRoomy(
        ReadOnlySpan<uint> typeFlags,
        ReadOnlySpan<int> typeHeaps,
        ReadOnlySpan<ulong> heapSizes,
        uint allowedBits,
        uint required,
        ulong bytes) =>
        FindRoomyType(typeFlags, typeHeaps, heapSizes, allowedBits, required, bytes) >= 0;
}
