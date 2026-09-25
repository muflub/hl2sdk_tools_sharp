using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.Tests.MapFormats;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// The committed golden map, loaded once for a whole diff test class.
/// </summary>
/// <remarks>
/// <c>dm_lockdown.bsp</c> is a real, tracked, BSP version 19 map, so a fact
/// over it can never skip for want of build output -- which this project has
/// already had decide a verdict the wrong way. Corruption for the mutation
/// facts happens on COPIES in memory; the file on disk is never written.
/// </remarks>
public sealed class LockdownDiffFixture
{
    /// <summary>Loads <c>dm_lockdown.bsp</c> from this worktree.</summary>
    public LockdownDiffFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        Bsp = BspFile.LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The loaded map. Treat as read-only; mutate a <see cref="DiffMaps.Clone"/>.</summary>
    public BspData Bsp { get; }
}

/// <summary>
/// Copies of a map, perturbed in exactly one place.
/// </summary>
/// <remarks>
/// Each helper changes ONE thing and says what it changed, because the
/// mutation facts are only worth anything if the perturbation is as small as
/// they claim. A helper that quietly rewrote a second lump would turn "the diff
/// found exactly one difference" into a coincidence.
/// </remarks>
internal static class DiffMaps
{
    /// <summary>
    /// A shallow copy: the same lump bytes, a new <see cref="BspData"/>.
    /// </summary>
    /// <param name="source">The map to copy.</param>
    /// <returns>A copy whose lumps can be REPLACED without touching the source.</returns>
    /// <remarks>
    /// The byte arrays are shared, which is safe because nothing here writes
    /// through them: a mutation always allocates a fresh array with
    /// <see cref="MutableLump{T}"/> or <see cref="MutableBytes"/> and installs
    /// it in place of the shared one.
    /// </remarks>
    internal static BspData Clone(BspData source)
    {
        BspData copy = new()
        {
            FileVersion = source.FileVersion,
            MapRevision = source.MapRevision,
        };

        for (int slot = 0; slot < BspData.HeaderLumps; slot++)
        {
            copy[slot] = source[slot];
        }

        copy.GameLumps.AddRange(source.GameLumps);
        return copy;
    }

    /// <summary>
    /// Replaces one lump with a writable copy of its bytes and hands back a
    /// typed view of it.
    /// </summary>
    /// <typeparam name="T">The lump's element struct.</typeparam>
    /// <param name="bsp">The map to change.</param>
    /// <param name="lump">Which lump.</param>
    /// <returns>A span over the copy; writes through it change the map.</returns>
    internal static Span<T> MutableLump<T>(BspData bsp, BspLump lump)
        where T : unmanaged
    {
        byte[] bytes = MutableBytes(bsp, lump);
        return MemoryMarshal.Cast<byte, T>(bytes.AsSpan());
    }

    /// <summary>
    /// Replaces one lump with a writable copy of its bytes.
    /// </summary>
    /// <param name="bsp">The map to change.</param>
    /// <param name="lump">Which lump.</param>
    /// <returns>The copy; writes to it change the map.</returns>
    internal static byte[] MutableBytes(BspData bsp, BspLump lump)
    {
        BspLumpData original = bsp[lump];
        byte[] bytes = original.Data.ToArray();
        bsp[lump] = new BspLumpData(bytes, original.Version, original.UncompressedSize);
        return bytes;
    }

    /// <summary>
    /// The two maps a comparison starts from: A is the golden map with its
    /// entity lump rewritten by this tree's writer, and B is the same again.
    /// </summary>
    /// <param name="source">The golden map.</param>
    /// <returns>A and B, which must compare identical.</returns>
    /// <remarks>
    /// Both sides go through <see cref="EntityLump.Write"/> so that an entity
    /// mutation test is measuring the comparison and not the writer's spacing.
    /// </remarks>
    internal static (BspData A, BspData B) RewrittenEntities(BspData source)
    {
        List<BspEntity> entities = EntityLump.Parse(source[BspLump.Entities]);

        BspData a = Clone(source);
        a[BspLump.Entities] = EntityLump.Write(entities);

        BspData b = Clone(source);
        b[BspLump.Entities] = EntityLump.Write(entities);
        return (a, b);
    }
}
