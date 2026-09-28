//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapGen.Catalog;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// A large room library for stress and performance runs of the room
/// pipeline: many distinct rooms on the 3x3 sample's kit, grid and
/// materials.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator and not a file.</b> A thousand rooms make a library
/// VMF of tens of megabytes, which does not belong in the repository. The
/// generator is deterministic (fixed order, invariant formatting, LF line
/// ends), so the documented command rebuilds the same bytes anywhere.
/// </para>
/// <para>
/// <b>The rooms.</b> Room <c>i</c> is kind <c>i mod 5</c> of
/// <see cref="Rooms3x3Kit.Kinds"/> (so every prefix of the library holds
/// every kind, and a layout drawn from it has rooms of every socket set),
/// and variant <c>v = i div 5</c> of that kind. The variant index picks,
/// in mixed radix, which inside corner the kind's feature stands in (4),
/// its width (3) and height (3), how many extra blocks stand in the other
/// corners (0 to 3, world and <c>func_detail</c> in turn) and how many
/// lights hang in the centre column (1 to 3). That is 432 combinations per
/// kind, more than the 205 a 1024-room library needs, so every room is a
/// different map.
/// </para>
/// <para>
/// <b>Why the rooms stay valid.</b> Everything added stands in one of the
/// four inside corner squares, <c>[32, 80]</c> or <c>[176, 224]</c> on
/// each horizontal axis: clear of every doorway's strip (the kit's opening
/// spans 80 to 176 on its face) and of the centre column, where the lights
/// and the player start are. So a room is sealed, its sockets are the
/// kind's, and a player walks from any door to any other, whatever the
/// variant.
/// </para>
/// <para>
/// The cells stand on a square grid of <see cref="Columns"/> columns, one
/// cell and <see cref="Rooms3x3Kit.LibraryGap"/> apart, so a 1024-room
/// library spans 12,288 units, inside the engine's coordinate range, where
/// a line of 1024 cells would not be.
/// </para>
/// </remarks>
public static class RoomsStressLibrary
{
    /// <summary>The default room count: the stress run's 1024.</summary>
    public const int DefaultCount = 1024;

    /// <summary>The library grid's width in cells.</summary>
    public const int Columns = 32;

    /// <summary>How many distinct variants each kind has.</summary>
    public const int VariantsPerKind = Corners * Widths * Heights * ExtraCounts * LightCounts;

    /// <summary>The largest library the generator writes: every variant of every kind.</summary>
    public static int MaxCount => VariantsPerKind * Rooms3x3Kit.Kinds.Count;

    private const int Corners = 4;
    private const int Widths = 3;
    private const int Heights = 3;
    private const int ExtraCounts = 4;
    private const int LightCounts = 3;

    /// <summary>The name of room <paramref name="index"/>: its kind, then its variant.</summary>
    /// <param name="index">The room's position in the library.</param>
    public static string Name(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        RoomKind kind = Rooms3x3Kit.Kinds[index % Rooms3x3Kit.Kinds.Count];
        return string.Create(CultureInfo.InvariantCulture, $"{kind.Name}_{index / Rooms3x3Kit.Kinds.Count:D3}");
    }

    /// <summary>Room <paramref name="index"/> of the library: a kind, varied.</summary>
    /// <param name="index">The room's position in the library, from 0 below <see cref="MaxCount"/>.</param>
    public static RoomKind Room(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxCount);

        RoomKind kind = Rooms3x3Kit.Kinds[index % Rooms3x3Kit.Kinds.Count];
        int v = index / Rooms3x3Kit.Kinds.Count;
        int corner = v % Corners;
        int width = 16 * (1 + ((v / Corners) % Widths));
        int height = new[] { 32, 128, 224 }[(v / (Corners * Widths)) % Heights];
        int extras = (v / (Corners * Widths * Heights)) % ExtraCounts;
        int lights = 1 + ((v / (Corners * Widths * Heights * ExtraCounts)) % LightCounts);

        KitBrush feature = kind.Features[0];
        List<KitBrush> features = [new(CornerBox(corner, width, height), feature.Material, feature.Role)];
        for (int j = 0; j < extras; j++)
        {
            features.Add(new KitBrush(
                CornerBox((corner + 1 + j) % Corners, 16, 48 + (16 * j)),
                Rooms3x3Kit.BlockMaterial,
                j % 2 == 0 ? KitBrushRole.World : KitBrushRole.Detail));
        }

        List<KitEntity> entities = [];
        int[] lightHeights = [208, 96, 152];
        for (int l = 0; l < lights; l++)
        {
            int brightness = 120 + ((v * 7) % 120) + (l * 20);
            entities.Add(new KitEntity(
                "light",
                new Point(128, 128, lightHeights[l]),
                null,
                [new("_light", string.Create(CultureInfo.InvariantCulture, $"255 {200 + (l * 20)} {180 + ((v * 3) % 60)} {brightness}"))]));
        }

        // The end room keeps its player start, moved to the centre column so
        // no variant's corner block can stand on it.
        foreach (KitEntity entity in kind.Entities)
        {
            if (entity.ClassName != "light")
            {
                entities.Add(entity with { Origin = new Point(128, 128, 17) });
            }
        }

        return new RoomKind(Name(index), kind.Sockets, features, entities);
    }

    /// <summary>
    /// A box standing on the floor in one inside corner square: corner 0 is
    /// south-west, then south-east, north-east and north-west.
    /// </summary>
    private static Bounds CornerBox(int corner, int width, int height)
    {
        float x0 = corner is 0 or 3 ? 32 : 176;
        float y0 = corner is 0 or 1 ? 32 : 176;
        float z0 = Rooms3x3Kit.Wall;
        return new Bounds(new Point(x0, y0, z0), new Point(x0 + width, y0 + width, z0 + height));
    }

    /// <summary>Where room <paramref name="index"/>'s cell starts in the library.</summary>
    /// <param name="index">The room's position in the library.</param>
    public static Point Corner(int index)
    {
        const float pitch = Rooms3x3Kit.CellSize + Rooms3x3Kit.LibraryGap;
        return new Point((index % Columns) * pitch, (index / Columns) * pitch, 0);
    }

    /// <summary>The library VMF of the first <paramref name="count"/> rooms.</summary>
    /// <param name="count">How many rooms, from 1 to <see cref="MaxCount"/>.</param>
    public static string LibraryVmf(int count = DefaultCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);

        VmfMap map = new();
        for (int i = 0; i < count; i++)
        {
            Point corner = Corner(i);
            RoomKind room = Room(i);
            Rooms3x3Kit.Place(map, room, Rooms3x3Placement.Identity, open: null, offset: corner);
            VmfEntity marker = new() { ClassName = "info_room" };
            marker.Set("origin", corner.ToString());
            marker.Set("name", room.Name);
            marker.Set("cell_size", Number(Rooms3x3Kit.CellSize));
            marker.Set("door_width", Number(Rooms3x3Kit.DoorWidth));
            marker.Set("door_height", Number(Rooms3x3Kit.DoorHeight));
            marker.Set("wall_depth", Number(Rooms3x3Kit.Wall));
            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>
    /// Every file of a stress game folder: the sample's <c>gameinfo.txt</c>
    /// and materials, and the library as <c>rooms.vmf</c>, in ordinal path order.
    /// </summary>
    /// <param name="count">How many rooms the library holds.</param>
    public static IReadOnlyDictionary<string, byte[]> Build(int count = DefaultCount)
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(Rooms3x3Kit.GameInfo),
            [Rooms3x3Kit.LibraryFile] = Encoding.UTF8.GetBytes(LibraryVmf(count)),
        };

        foreach ((string path, string vmt) in Rooms3x3Kit.Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        return files;
    }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
