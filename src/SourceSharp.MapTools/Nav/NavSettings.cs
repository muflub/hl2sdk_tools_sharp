//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// One agent size the navigation is built for: a box standing on its
/// origin, and the contents it collides with.
/// </summary>
/// <param name="Name">The agent's name: letters, digits and <c>_</c>.</param>
/// <param name="Width">The box's width and depth: agents are square seen from above, so a quarter turn leaves them unchanged.</param>
/// <param name="Height">The box's height, from the origin (the feet) up.</param>
/// <param name="ContentsMask">The <c>CONTENTS_*</c> bits the agent collides with.</param>
/// <remarks>
/// The origin is at the bottom centre of the box, as a Source player's and
/// NPC's is, so a floor leaf is where the agent's feet stand: a leaf is free
/// when the box, placed with its origin anywhere in the leaf, overlaps no
/// solid. Square boxes are required, not assumed: a quarter-turned room
/// must give the same free space as the room turned, and only a box that a
/// quarter turn maps onto itself does.
/// </remarks>
public sealed record NavAgentSpec(string Name, float Width, float Height, int ContentsMask)
{
    /// <summary>The box's low corner, relative to the origin.</summary>
    public Vec3 Mins => new(-Width / 2f, -Width / 2f, 0f);

    /// <summary>The box's high corner.</summary>
    public Vec3 Maxs => new(Width / 2f, Width / 2f, Height);
}

/// <summary>
/// How a room library's navigation is built: the voxel, the walkable slope
/// and the agent sizes, read from the library's worldspawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why worldspawn keys.</b> The settings belong to the whole library:
/// every room of it is voxelised on one grid for one set of agents, or the
/// link could not stitch them. Of the places a library could say so, the
/// worldspawn is the one there is exactly one of, which every room's own
/// VMF already carries (<see cref="RoomLibraryVmf.Split"/> copies its keys),
/// and which Hammer edits in Map Properties. An <c>info_room</c> key would
/// have to be repeated, and agree, in every room; a separate settings entity
/// could be missing, doubled, or placed inside a room's cell.
/// </para>
/// <list type="table">
/// <listheader><term>key</term><description>meaning</description></listheader>
/// <item><term><c>nav</c></term><description><c>0</c> builds no navigation for the library; anything else, or no key, builds it.</description></item>
/// <item><term><c>nav_voxel_size</c></term><description>The leaf voxel's edge in units, default 16. It must divide the cell size, with at most <see cref="MaxCellVoxels"/> voxels along a cell's edge.</description></item>
/// <item><term><c>nav_max_slope</c></term><description>The steepest walkable floor, in degrees from level, above 0 and below 90. Without it a floor is a surface whose normal z is at least 0.7, the threshold the game's movement code uses (about 45.6 degrees).</description></item>
/// <item><term><c>nav_agents</c></term><description>
/// The agents, separated by <c>;</c>: each <c>name width height [mask]</c>,
/// the mask <c>player</c>, <c>npc</c> or a number. Default:
/// <c>standing 32 72 player; flyer 32 32 npc</c>, the player's standing hull
/// and a small flyer.
/// </description></item>
/// </list>
/// <para>
/// <b>The default voxel, 16 units</b>, is the largest that keeps the sample
/// kit exact: the wall depth is 16, the player's half-width is 16, and a
/// door 96 wide leaves 64 units of centre line, four voxels. A 32-unit
/// voxel would put the lowest free voxel 16 units above the floor (the
/// floor's top is at 16) and lose the floor contact; an 8-unit voxel gives
/// the same answer on this kit with eight times the voxels. The measurements
/// are in <c>docs/nav3d-format.md</c>.
/// </para>
/// </remarks>
public sealed record NavSettings
{
    /// <summary>The worldspawn key that turns navigation off with <c>0</c>.</summary>
    public const string EnabledKey = "nav";

    /// <summary>The worldspawn key of the voxel's edge.</summary>
    public const string VoxelKey = "nav_voxel_size";

    /// <summary>The worldspawn key of the steepest walkable slope, in degrees.</summary>
    public const string SlopeKey = "nav_max_slope";

    /// <summary>The worldspawn key of the agent list.</summary>
    public const string AgentsKey = "nav_agents";

    /// <summary>The default voxel edge.</summary>
    public const float DefaultVoxelSize = 16f;

    /// <summary>The default floor threshold: a floor's normal z is at least this.</summary>
    public const float DefaultFloorNormalZ = 0.7f;

    /// <summary>The most voxels along a cell's edge: a cell is at most 128³ voxels, 2 MiB of scratch per agent.</summary>
    public const int MaxCellVoxels = 128;

    /// <summary>The default agents, as the <see cref="AgentsKey"/> key spells them.</summary>
    public const string DefaultAgents = "standing 32 72 player; flyer 32 32 npc";

    /// <summary>The library's defaults: 16-unit voxels, the game's floor threshold, and the two default agents.</summary>
    /// <remarks>A new instance each time, so no caller shares a list another could change.</remarks>
    public static NavSettings Default => new()
    {
        VoxelSize = DefaultVoxelSize,
        FloorNormalZ = DefaultFloorNormalZ,
        Agents = ParseAgents(DefaultAgents),
    };

    /// <summary>The voxel's edge in units.</summary>
    public float VoxelSize { get; init; } = DefaultVoxelSize;

    /// <summary>The least normal z a walkable floor has.</summary>
    public float FloorNormalZ { get; init; } = DefaultFloorNormalZ;

    /// <summary>The agents, in the order the file lists them.</summary>
    public IReadOnlyList<NavAgentSpec> Agents { get; init; } = [];

    /// <summary>
    /// The agent whose box is the player's: the first one colliding with
    /// <see cref="Nav3dFormat.PlayerSolidMask"/>. Arrival points must fit it,
    /// and it is the agent the level's reachability rule is checked for.
    /// </summary>
    public int PlayerAgent
    {
        get
        {
            for (int a = 0; a < Agents.Count; a++)
            {
                if (Agents[a].ContentsMask == Nav3dFormat.PlayerSolidMask)
                {
                    return a;
                }
            }

            return -1;
        }
    }

    /// <summary>Voxels along the edge of a cell of the given size.</summary>
    /// <param name="cellSize">The cell's edge.</param>
    /// <returns>The count.</returns>
    /// <exception cref="RoomLibraryException">The voxel does not divide the cell, or divides it too finely.</exception>
    public int CellVoxels(float cellSize)
    {
        double ratio = (double)cellSize / VoxelSize;
        double whole = Math.Round(ratio);
        if (Math.Abs(ratio - whole) > 1e-4 || whole < 1)
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"the nav voxel ({VoxelKey} {VoxelSize:0.###}) does not divide the {cellSize:0.###}-unit cell into whole voxels; the grid is aligned to the cell so a quarter turn is an exact permutation of voxels."));
        }

        if (whole > MaxCellVoxels)
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"the nav voxel ({VoxelKey} {VoxelSize:0.###}) gives {whole} voxels along a {cellSize:0.###}-unit cell; at most {MaxCellVoxels}."));
        }

        return (int)whole;
    }

    /// <summary>Reads a library's navigation settings from its worldspawn.</summary>
    /// <param name="library">The library VMF (or one room's VMF, which carries the same worldspawn keys).</param>
    /// <returns>The settings, or null when the library turns navigation off.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="library"/> is null.</exception>
    /// <exception cref="RoomLibraryException">A key is malformed; the message names it.</exception>
    public static NavSettings? FromLibrary(VmfDocument library)
    {
        ArgumentNullException.ThrowIfNull(library);
        VmfChunk? world = library.GetChunk(MapFileLoader.WorldChunk);
        string? enabled = world?.GetValue(EnabledKey);
        if (enabled is not null && enabled.Trim() == "0")
        {
            return null;
        }

        float voxel = DefaultVoxelSize;
        if (world?.GetValue(VoxelKey) is { } voxelText)
        {
            if (!float.TryParse(voxelText, NumberStyles.Float, CultureInfo.InvariantCulture, out voxel)
                || !float.IsFinite(voxel) || voxel <= 0)
            {
                throw new RoomLibraryException($"the worldspawn's \"{VoxelKey}\" is \"{voxelText}\", not a positive number of units.");
            }
        }

        float floor = DefaultFloorNormalZ;
        if (world?.GetValue(SlopeKey) is { } slopeText)
        {
            if (!double.TryParse(slopeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees)
                || !(degrees > 0 && degrees < 90))
            {
                throw new RoomLibraryException(
                    $"the worldspawn's \"{SlopeKey}\" is \"{slopeText}\"; the steepest walkable slope is above 0 and below 90 degrees.");
            }

            floor = (float)DetMath.Cos(degrees * (Math.PI / 180.0));
        }

        return new NavSettings
        {
            VoxelSize = voxel,
            FloorNormalZ = floor,
            Agents = ParseAgents(world?.GetValue(AgentsKey) ?? DefaultAgents),
        };
    }

    /// <summary>Parses an agent list: <c>name width height [mask]</c> entries separated by <c>;</c>.</summary>
    /// <param name="text">The list.</param>
    /// <returns>The agents.</returns>
    /// <exception cref="RoomLibraryException">An entry is malformed, a name repeats, or there are none or too many.</exception>
    public static IReadOnlyList<NavAgentSpec> ParseAgents(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<NavAgentSpec> agents = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string raw in text.Split(';'))
        {
            string entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 4)
            {
                throw new RoomLibraryException(
                    $"the nav agent \"{entry}\" ({AgentsKey}) is not \"name width height [mask]\".");
            }

            string name = parts[0];
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new RoomLibraryException($"the nav agent name \"{name}\" ({AgentsKey}) is not letters, digits and _.");
            }

            if (!names.Add(name))
            {
                throw new RoomLibraryException($"two nav agents are named \"{name}\" ({AgentsKey}).");
            }

            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float width) || !(width > 0) || !float.IsFinite(width)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !(height > 0) || !float.IsFinite(height))
            {
                throw new RoomLibraryException($"the nav agent \"{entry}\" ({AgentsKey}) needs a positive width and height.");
            }

            int mask = Nav3dFormat.PlayerSolidMask;
            if (parts.Length == 4)
            {
                mask = parts[3].ToUpperInvariant() switch
                {
                    "PLAYER" => Nav3dFormat.PlayerSolidMask,
                    "NPC" => Nav3dFormat.NpcSolidMask,
                    _ => ParseMask(parts[3], entry),
                };
            }

            agents.Add(new NavAgentSpec(name, width, height, mask));
        }

        if (agents.Count == 0 || agents.Count > Nav3dFormat.MaxAgents)
        {
            throw new RoomLibraryException(
                $"the nav agent list \"{text}\" ({AgentsKey}) names {agents.Count} agents; it names 1 to {Nav3dFormat.MaxAgents}.");
        }

        return agents;
    }

    private static int ParseMask(string text, string entry)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int mask)
            || mask == 0)
        {
            throw new RoomLibraryException(
                $"the nav agent \"{entry}\" ({AgentsKey}) has mask \"{text}\"; a mask is player, npc or a non-zero number.");
        }

        return mask;
    }
}
