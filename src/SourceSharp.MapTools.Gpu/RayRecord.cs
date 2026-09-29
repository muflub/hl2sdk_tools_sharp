//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Gpu;

/// <summary>
/// How one slab's rays are laid out on the wire: the record the host packs
/// and the kernel reads back, chosen per slab.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two records, both lossless.</b> The <see cref="Wide"/> record is the
/// ray's seven floats in <see cref="Ray"/>'s own order, 28 bytes: origin,
/// direction, reach. The <see cref="UniformReach"/> record drops the reach,
/// 24 bytes, and is used only when every ray in the slab has the same reach
/// bits, which then travel once, in the dispatch's push constants. The ray
/// epsilon (<c>tmin</c>) already travels that way, one value a slab.
/// </para>
/// <para>
/// <b>Why not less.</b> The wire used to be two <c>vec4</c> per ray, 32
/// bytes, one of whose floats was padding. Removing the padding is free;
/// going below 28 bytes needs one of the seven floats to be known without
/// sending it, bit for bit, because the kernel must hand the ray query the
/// very floats the wide record would, or answers move. On 2fort the
/// candidates measured as follows. The reach is 1.0 on every ray of the
/// correct-mode light rays (<c>LightRayLog.MakeRay</c> sends the whole
/// segment as the direction) and the sky's closest-hit rays: 565 M of 717 M
/// rays, 79 %. Every other ray kind sends a unit-ish direction and the
/// segment's length as the reach (stock's parametrisation, which the stock
/// compliance mode keeps everywhere: 16 of 952 M rays had a shared reach).
/// That length cannot be rebuilt on the device: it is a float <c>sqrt</c>
/// of the host's sum of squares, and the direction was divided by it with
/// either a correctly rounded divide or a CPU's reciprocal estimate, while
/// Vulkan lets a shader's <c>sqrt</c> and divide be several ulps out and
/// has no estimate to match. Scaling the direction by the length and
/// tracing to 1 is not the same query either (the driver's intersection
/// arithmetic rounds differently), so it is not an encoding. Sharing
/// origins or directions between neighbouring rays was measured too: a
/// third of the stock visibility rays repeat the previous ray's origin, but
/// an index per ray costs what the repeats save.
/// </para>
/// <para>
/// <b>Why the answers are the same bits.</b> The host never converts a
/// float: <see cref="Pack"/> copies the ray's bytes (the wide record is the
/// <see cref="Ray"/> struct itself, whose layout a fact pins), and the
/// uniform reach is compared and sent as raw bits. The kernel loads each
/// float straight from the ray buffer, as it loaded the <c>vec4</c>
/// components before, and loads the shared reach from the push constants
/// exactly as it loads <c>tmin</c>. A load is not arithmetic: no rounding,
/// no flush, no NaN rewriting. So the ray query receives the same seven
/// values whichever record carried them, and every expression after it
/// (the any-hit scale, the definedness guard) is the same code. The one
/// conversion that could be questioned is <c>uintBitsToFloat</c> of the
/// shared reach, which GLSL leaves unspecified for an infinity or NaN
/// pattern; a reach that is not finite therefore never goes in the push
/// constants (<see cref="UniformReachOf"/>), and such a slab stays wide.
/// </para>
/// <para>
/// <b>Every device uses it.</b> A staged device (no resizable BAR) copies
/// the record across PCIe each slab, where the bytes are the whole cost.
/// A direct device has the host write it across the bus into the BAR, where
/// the host's pack was measured as the bottleneck on an RX 9070, and an
/// integrated one writes and reads it in shared memory; fewer bytes cost
/// less in all three, the kernel reads fewer, and one record format per
/// slab keeps one code path whatever the layout.
/// </para>
/// </remarks>
internal readonly record struct RayRecord
{
    /// <summary>Words in a wide record: origin, direction, reach.</summary>
    public const int WideWords = 7;

    /// <summary>Words in a uniform-reach record: origin, direction.</summary>
    public const int UniformReachWords = 6;

    /// <summary>The largest record in bytes; slab buffers are sized for it.</summary>
    public const int MaxBytes = WideWords * sizeof(uint);

    private RayRecord(bool sharedReach, uint reachBits)
    {
        HasUniformReach = sharedReach;
        ReachBits = reachBits;
    }

    /// <summary>The 28-byte record, which carries every ray's own reach.</summary>
    public static RayRecord Wide => default;

    /// <summary>Whether the reach is left out of each record and sent once for the slab.</summary>
    public bool HasUniformReach { get; }

    /// <summary>The slab's shared reach as float bits; 0 for a wide record.</summary>
    public uint ReachBits { get; }

    /// <summary>Words one ray takes.</summary>
    public int Words => HasUniformReach ? UniformReachWords : WideWords;

    /// <summary>Bytes one ray takes.</summary>
    public int Bytes => Words * sizeof(uint);

    /// <summary>The 24-byte record for a slab whose every ray has this reach.</summary>
    /// <param name="reachBits">The shared reach as float bits; must be finite.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reach is an infinity or a NaN.</exception>
    public static RayRecord UniformReach(uint reachBits)
    {
        if (!IsFiniteBits(reachBits))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reachBits), $"0x{reachBits:X8} is not a finite float; such a reach stays in the wide record");
        }

        return new RayRecord(true, reachBits);
    }

    /// <summary>The record for a slab of requests whose shared reach, if any, is <paramref name="reachBits"/>.</summary>
    /// <param name="reachBits">What <see cref="UniformReachOf"/> said of every request in the slab.</param>
    /// <returns>The uniform-reach record when there is one, the wide record otherwise.</returns>
    public static RayRecord For(uint? reachBits) => reachBits is { } bits ? UniformReach(bits) : Wide;

    /// <summary>
    /// The reach every ray shares, as bits, when there is one that can go in
    /// the push constants.
    /// </summary>
    /// <param name="rays">The rays; at least one.</param>
    /// <returns>
    /// The shared reach's bits, or null when two rays differ in any bit
    /// (so <c>0.0</c> and <c>-0.0</c> differ, and so do two NaNs with different
    /// payloads) or the shared reach is not finite.
    /// </returns>
    /// <remarks>
    /// Run on the caller's thread when a request is queued, so the scan
    /// costs the drainer nothing. It reads one word in seven.
    /// </remarks>
    public static uint? UniformReachOf(ReadOnlySpan<Ray> rays)
    {
        if (rays.IsEmpty)
        {
            return null;
        }

        uint first = BitsOfReach(rays[0]);
        if (!IsFiniteBits(first))
        {
            return null;
        }

        for (int i = 1; i < rays.Length; i++)
        {
            if (BitsOfReach(rays[i]) != first)
            {
                return null;
            }
        }

        return first;
    }

    /// <summary>
    /// Writes <paramref name="rays"/> into <paramref name="records"/> in this
    /// record, as raw bytes.
    /// </summary>
    /// <param name="rays">The rays.</param>
    /// <param name="records">At least <c>Words * rays.Length</c> words.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="records"/> is too short, or the record has a uniform
    /// reach some ray does not share (packing it would change that ray).
    /// </exception>
    /// <remarks>
    /// The wide record is the <see cref="Ray"/> struct's own bytes, so it is
    /// one block copy. The uniform-reach record is the first 24 bytes of
    /// each, copied as a 16-byte and an 8-byte move: whole sequential
    /// stores, which is what write-combined memory (a BAR the host writes
    /// through) wants. Neither loads a float into a register.
    /// </remarks>
    public void Pack(ReadOnlySpan<Ray> rays, Span<uint> records)
    {
        int words = Words;
        if (records.Length < (long)rays.Length * words)
        {
            throw new ArgumentException($"{rays.Length} rays need {rays.Length * words} words, not {records.Length}", nameof(records));
        }

        if (!HasUniformReach)
        {
            MemoryMarshal.Cast<Ray, uint>(rays).CopyTo(records);
            return;
        }

        ref byte src = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(rays));
        ref byte dst = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(records));
        for (int i = 0; i < rays.Length; i++)
        {
            ref byte from = ref Unsafe.Add(ref src, i * MaxBytes);
            if (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, UniformReachWords * sizeof(uint))) != ReachBits)
            {
                throw new ArgumentException($"ray {i} does not have the slab's reach 0x{ReachBits:X8}", nameof(rays));
            }

            ref byte to = ref Unsafe.Add(ref dst, i * UniformReachWords * sizeof(uint));
            Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Vector128<byte>>(ref from));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, 16), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref from, 16)));
        }
    }

    /// <summary>
    /// Writes one ray <paramref name="count"/> times, for the padding before
    /// an aligned segment.
    /// </summary>
    /// <param name="ray">The ray.</param>
    /// <param name="count">How many copies.</param>
    /// <param name="records">At least <c>Words * count</c> words.</param>
    public void Fill(in Ray ray, int count, Span<uint> records)
    {
        ReadOnlySpan<Ray> one = new(in ray);
        for (int i = 0; i < count; i++)
        {
            Pack(one, records.Slice(i * Words, Words));
        }
    }

    /// <summary>
    /// The ray one record says, as the kernel reads it: the host's mirror of
    /// the kernel's <c>load_ray</c>, for facts and the CPU stand-in device.
    /// </summary>
    /// <param name="record">One record's words.</param>
    /// <returns>The ray, every float exactly the bits it was sent as.</returns>
    public Ray Decode(ReadOnlySpan<uint> record)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(record.Length, Words, nameof(record));
        Span<uint> words = stackalloc uint[WideWords];
        record[..Words].CopyTo(words);
        if (HasUniformReach)
        {
            words[UniformReachWords] = ReachBits;
        }

        return MemoryMarshal.Cast<uint, Ray>(words)[0];
    }

    private static uint BitsOfReach(in Ray ray) =>
        Unsafe.ReadUnaligned<uint>(
            ref Unsafe.Add(ref Unsafe.As<Ray, byte>(ref Unsafe.AsRef(in ray)), UniformReachWords * sizeof(uint)));

    private static bool IsFiniteBits(uint bits) => (bits & 0x7F800000u) != 0x7F800000u;
}
