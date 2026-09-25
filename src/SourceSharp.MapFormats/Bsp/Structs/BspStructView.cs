using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// Reinterprets a lump's raw bytes as a span of one of the structs in this
/// namespace, without copying.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BspData"/> holds every lump as bytes, which is what lets a lump
/// this port has no reader for survive a round trip. A typed view is how a
/// stage reads one: <c>MemoryMarshal.Cast</c> over the same memory, so a
/// 6 MB lighting lump costs nothing to "parse" and writing one element back
/// writes the file's bytes.
/// </para>
/// <para>
/// This is only sound because every struct here is blittable, little-endian
/// and explicitly padded. On a big-endian machine it would be wrong -- and so
/// is stock bsplib, which swaps in place and says so.
/// </para>
/// </remarks>
public static class BspStructView
{
    /// <summary>
    /// The lump's bytes as a read-only span of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="lump">The lump to view.</param>
    /// <returns>A span over the same memory, never a copy.</returns>
    /// <exception cref="InvalidBspException">
    /// The lump's length is not a whole number of <typeparamref name="T"/>.
    /// That is thrown rather than truncated because a non-multiple length is
    /// the signature of reading a lump with the wrong struct, and silently
    /// dropping the remainder hides it.
    /// </exception>
    public static ReadOnlySpan<T> As<T>(BspLumpData lump)
        where T : unmanaged =>
        As<T>(lump.Data.Span);

    /// <summary>
    /// Some bytes as a read-only span of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="bytes">The bytes to view.</param>
    /// <returns>A span over the same memory.</returns>
    /// <exception cref="InvalidBspException">
    /// The length is not a whole number of <typeparamref name="T"/>.
    /// </exception>
    public static ReadOnlySpan<T> As<T>(ReadOnlySpan<byte> bytes)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (bytes.Length % size != 0)
        {
            throw new InvalidBspException(
                $"a lump of {bytes.Length} bytes is not a whole number of {typeof(T).Name} "
                + $"({size} bytes each); {bytes.Length % size} bytes are left over");
        }

        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    /// <summary>
    /// The lump's bytes as a writable span of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="bytes">The bytes to view.</param>
    /// <returns>A span over the same memory; writes through it change the bytes.</returns>
    /// <exception cref="InvalidBspException">
    /// The length is not a whole number of <typeparamref name="T"/>.
    /// </exception>
    public static Span<T> AsWritable<T>(Span<byte> bytes)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (bytes.Length % size != 0)
        {
            throw new InvalidBspException(
                $"a lump of {bytes.Length} bytes is not a whole number of {typeof(T).Name} "
                + $"({size} bytes each); {bytes.Length % size} bytes are left over");
        }

        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    /// <summary>
    /// Whether the lump's length is a whole number of
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="lump">The lump to check.</param>
    /// <returns>True when <see cref="As{T}(BspLumpData)"/> would succeed.</returns>
    public static bool Fits<T>(BspLumpData lump)
        where T : unmanaged =>
        lump.Length % Unsafe.SizeOf<T>() == 0;

    /// <summary>
    /// How many <typeparamref name="T"/> the lump holds.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="lump">The lump to measure.</param>
    /// <returns>The element count, rounding DOWN if the length does not divide.</returns>
    public static int Count<T>(BspLumpData lump)
        where T : unmanaged =>
        lump.Length / Unsafe.SizeOf<T>();

    /// <summary>
    /// Packs a run of structs into lump bytes.
    /// </summary>
    /// <typeparam name="T">A blittable lump struct.</typeparam>
    /// <param name="items">The elements to write.</param>
    /// <param name="version">The lump version to record.</param>
    /// <returns>A lump holding exactly those elements' bytes.</returns>
    public static BspLumpData ToLump<T>(ReadOnlySpan<T> items, int version)
        where T : unmanaged
    {
        byte[] bytes = MemoryMarshal.AsBytes(items).ToArray();
        return new BspLumpData(bytes, version, 0);
    }
}
