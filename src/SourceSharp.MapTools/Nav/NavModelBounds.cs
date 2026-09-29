//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// The movement hulls of the models a room's props use, read from the
/// game's content before the room's navigation is built, so the build itself
/// stays synchronous and reads nothing.
/// </summary>
/// <remarks>
/// A prop blocks agents with its model's movement hull, the box the engine
/// sweeps it as; a model's header states it (<c>hull_min</c>,
/// <c>hull_max</c>) at a fixed place for every studio version, so only the
/// header's first bytes are needed and a model of any version is read. The
/// dictionary belongs to one room's build and is dropped with it.
/// </remarks>
public static class NavModelBounds
{
    /// <summary>Where a studio header keeps its movement hull's low corner.</summary>
    private const int HullOffset = 104;

    /// <summary>The studio header's ident: <c>IDST</c>.</summary>
    private static ReadOnlySpan<byte> Ident => "IDST"u8;

    /// <summary>The model paths a BSP's dynamic-obstacle props name, distinct, in entity order.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The paths, as the entities spell them.</returns>
    public static IReadOnlyList<string> PropModels(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<string> models = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (NavGeometry.KindOf(entity.ClassName) is not null && entity.Get("model") is { Length: > 0 } model && model[0] != '*'
                && !models.Contains(model, StringComparer.Ordinal))
            {
                models.Add(model);
            }
        }

        return models;
    }

    /// <summary>Reads models' hulls from the content.</summary>
    /// <param name="content">The game's content.</param>
    /// <param name="models">The model paths.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Each path's hull, or null when the content lacks the model or it is not a studio model.</returns>
    public static async Task<IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?>> LoadAsync(
        IContentFileSystem content, IReadOnlyList<string> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(models);
        Dictionary<string, (Vec3, Vec3)?> hulls = new(StringComparer.Ordinal);
        foreach (string model in models)
        {
            (Vec3, Vec3)? hull = null;
            if (VPath.TryCreate(model, out VPath path))
            {
                using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                hull = owner is null ? null : Hull(owner.Memory.Span);
            }

            hulls[model] = hull;
        }

        return hulls;
    }

    /// <summary>A studio model's movement hull, from its header.</summary>
    /// <param name="mdl">The model file's bytes (its header suffices).</param>
    /// <returns>The hull, or null when the bytes are not a studio header or the hull is empty.</returns>
    public static (Vec3 Mins, Vec3 Maxs)? Hull(ReadOnlySpan<byte> mdl)
    {
        if (mdl.Length < HullOffset + 24 || !mdl[..4].SequenceEqual(Ident))
        {
            return null;
        }

        Vec3 mins = new(
            BinaryPrimitives.ReadSingleLittleEndian(mdl[HullOffset..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 8)..]));
        Vec3 maxs = new(
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 12)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 16)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 20)..]));
        return mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z ? (mins, maxs) : null;
    }
}
