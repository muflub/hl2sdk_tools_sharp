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
/// An agent preset: a box standing on its origin, and the clip brushes it
/// collides with, named for the runtime's convenience.
/// </summary>
/// <param name="Name">The preset's name: letters, digits and <c>_</c>.</param>
/// <param name="Width">The box's width and depth: agents are square seen from above, so a quarter turn leaves them unchanged.</param>
/// <param name="Height">The box's height, from the origin (the feet) up.</param>
/// <param name="ContentsMask">The <c>CONTENTS_*</c> bits the agent collides with: <see cref="Nav3dFormat.PlayerSolidMask"/> or <see cref="Nav3dFormat.NpcSolidMask"/>.</param>
/// <remarks>
/// <para>
/// The origin is at the bottom centre of the box, as a Source player's and
/// NPC's is, so a standing agent's origin is where its feet are.
/// </para>
/// <para>
/// <b>Presets do not shape the grid.</b> The clearance grid answers every
/// box size exactly (<see cref="Nav3dClearance"/>), so a preset is only a
/// name for a size: the file records the list, a reader derives each
/// preset's connected components at load, and a point of interest names the
/// presets it applies to. Room compiles use them for one thing more: a point
/// of interest must stand where the presets it applies to fit.
/// </para>
/// </remarks>
public sealed record NavAgentSpec(string Name, float Width, float Height, int ContentsMask)
{
    /// <summary>The box's low corner, relative to the origin.</summary>
    public Vec3 Mins => new(-Width / 2f, -Width / 2f, 0f);

    /// <summary>The box's high corner.</summary>
    public Vec3 Maxs => new(Width / 2f, Width / 2f, Height);

    /// <summary>Which of the grid's two clip classes the preset lives in.</summary>
    public Nav3dClipClass ClipClass => ContentsMask == Nav3dFormat.NpcSolidMask ? Nav3dClipClass.Npc : Nav3dClipClass.Player;
}

/// <summary>
/// How a room library's navigation is built: the voxel, the walkable slope,
/// the traversal limits and costs, and the agent presets, read from the
/// library's worldspawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why worldspawn keys.</b> The settings belong to the whole library:
/// every room of it is voxelised on one grid with one set of limits, or the
/// link could not stitch them. Of the places a library could say so, the
/// worldspawn is the one there is exactly one of, which every room's own
/// VMF already carries (<see cref="RoomLibraryVmf.Split"/> copies its keys),
/// and which Hammer edits in Map Properties.
/// </para>
/// <list type="table">
/// <listheader><term>key</term><description>meaning</description></listheader>
/// <item><term><c>nav</c></term><description><c>0</c> builds no navigation for the library; anything else, or no key, builds it.</description></item>
/// <item><term><c>nav_voxel_size</c></term><description>The voxel's edge in units, default 16. It must divide the cell size, with at most <see cref="MaxCellVoxels"/> voxels along a cell's edge.</description></item>
/// <item><term><c>nav_max_slope</c></term><description>The steepest walkable floor, in degrees from level, above 0 and below 90. Without it a floor is walkable when its normal z is at least 0.7, the threshold the game's movement code uses (about 45.6 degrees).</description></item>
/// <item><term><c>nav_step_height</c></term><description>The highest step walked without jumping, default 18: the Source player's step.</description></item>
/// <item><term><c>nav_jump_height</c></term><description>The highest ledge a jump link climbs (and the deepest drop it stands for), default 56: what a Source player clears with a crouch jump.</description></item>
/// <item><term><c>nav_jump_distance</c></term><description>The farthest a jump link reaches between column centres, default 100: a Half-Life 2 player running at 190 units a second stays in the air about 0.53 s on its 21-unit jump under the default gravity of 600.</description></item>
/// <item><term><c>nav_cost_water</c></term><description>The cost multiplier of a water leaf, default 2: swimming and wading are slow.</description></item>
/// <item><term><c>nav_cost_ladder</c></term><description>The cost multiplier of a ladder leaf, default 1.5: climbing is slower than walking and commits the agent.</description></item>
/// <item><term><c>nav_agents</c></term><description>
/// The presets, separated by <c>;</c>: each <c>name width height [class]</c>,
/// the class <c>player</c> (the default) or <c>npc</c>, or a number equal to
/// one of their contents masks. Default:
/// <c>standing 32 72 player; flyer 32 32 npc</c>. May be empty: presets are
/// optional.
/// </description></item>
/// </list>
/// <para>
/// <b>The default voxel, 16 units</b>, is the largest that keeps the sample
/// kit's floors on voxel boundaries: the floor's top and the wall depth are
/// both 16. Clearances are exact at any voxel (they carry the real
/// distances), but a floor that falls inside a voxel leaves that voxel
/// solid, so the standing voxel is the one above.
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

    /// <summary>The worldspawn key of the step height.</summary>
    public const string StepKey = "nav_step_height";

    /// <summary>The worldspawn key of the jump height.</summary>
    public const string JumpHeightKey = "nav_jump_height";

    /// <summary>The worldspawn key of the jump distance.</summary>
    public const string JumpDistanceKey = "nav_jump_distance";

    /// <summary>The worldspawn key of the water cost multiplier.</summary>
    public const string WaterCostKey = "nav_cost_water";

    /// <summary>The worldspawn key of the ladder cost multiplier.</summary>
    public const string LadderCostKey = "nav_cost_ladder";

    /// <summary>The worldspawn key of the preset list.</summary>
    public const string AgentsKey = "nav_agents";

    /// <summary>The default voxel edge.</summary>
    public const float DefaultVoxelSize = 16f;

    /// <summary>The default floor threshold: a walkable floor's normal z is at least this.</summary>
    public const float DefaultFloorNormalZ = 0.7f;

    /// <summary>The default step: the Source player's 18 units.</summary>
    public const float DefaultStepHeight = 18f;

    /// <summary>The default jump height: the ledge a Source player clears with a crouch jump.</summary>
    public const float DefaultJumpHeight = 56f;

    /// <summary>The default jump distance between column centres.</summary>
    public const float DefaultJumpDistance = 100f;

    /// <summary>The default water cost multiplier.</summary>
    public const float DefaultWaterCost = 2f;

    /// <summary>The default ladder cost multiplier.</summary>
    public const float DefaultLadderCost = 1.5f;

    /// <summary>The most voxels along a cell's edge.</summary>
    public const int MaxCellVoxels = Nav3dFormat.MaxCellVoxels;

    /// <summary>
    /// The most voxels up one room's columns: a run's low voxel and height
    /// are bytes in the file (<see cref="Nav3dFormat.MaxColumnVoxels"/>).
    /// </summary>
    public const int MaxColumnVoxels = Nav3dFormat.MaxColumnVoxels;

    /// <summary>The default presets, as the <see cref="AgentsKey"/> key spells them.</summary>
    public const string DefaultAgents = "standing 32 72 player; flyer 32 32 npc";

    /// <summary>The library's defaults: 16-unit voxels, the game's floor threshold, the Source player's limits, and the two default presets.</summary>
    /// <remarks>A new instance each time, so no caller shares a list another could change.</remarks>
    public static NavSettings Default => new()
    {
        Agents = ParseAgents(DefaultAgents),
    };

    /// <summary>The voxel's edge in units.</summary>
    public float VoxelSize { get; init; } = DefaultVoxelSize;

    /// <summary>The least normal z a walkable floor has.</summary>
    public float FloorNormalZ { get; init; } = DefaultFloorNormalZ;

    /// <summary>The highest step walked without jumping.</summary>
    public float StepHeight { get; init; } = DefaultStepHeight;

    /// <summary>The highest ledge a jump link climbs.</summary>
    public float JumpHeight { get; init; } = DefaultJumpHeight;

    /// <summary>The farthest a jump link reaches between column centres.</summary>
    public float JumpDistance { get; init; } = DefaultJumpDistance;

    /// <summary>The cost multiplier of a water leaf.</summary>
    public float WaterCost { get; init; } = DefaultWaterCost;

    /// <summary>The cost multiplier of a ladder leaf.</summary>
    public float LadderCost { get; init; } = DefaultLadderCost;

    /// <summary>The presets, in the order the file lists them.</summary>
    public IReadOnlyList<NavAgentSpec> Agents { get; init; } = [];

    /// <summary>
    /// The preset whose box is the player's: the first of the player class.
    /// Arrival points must fit it.
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

    /// <summary>
    /// Voxels up a room's columns: its height in voxels (the rooms design,
    /// 17.6), <see cref="CellVoxels"/> for a room that is a cube.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <returns>The count.</returns>
    /// <exception cref="RoomLibraryException">
    /// A shaped room's height is not a whole number of voxels, or is more
    /// voxels than a run's 8-bit fields describe
    /// (<see cref="MaxColumnVoxels"/>), with the 17.3 texts.
    /// </exception>
    /// <remarks>
    /// A room lower than its cell has fewer voxels up its columns than along
    /// its edge; the grid stays aligned to the cell in x and y, and to the
    /// floor in z, which is all a quarter turn and the stitch need. The
    /// limit is the file's (a run's low voxel and height are bytes), not
    /// the per-edge one, which bounds the footprint.
    /// </remarks>
    public int ColumnVoxels(RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int n = CellVoxels(definition.CellSize);
        if (!definition.IsShaped)
        {
            return n;
        }

        double ratio = (double)definition.Height / VoxelSize;
        double whole = Math.Round(ratio);
        if (Math.Abs(ratio - whole) > 1e-4 || whole < 1)
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"room {definition.Name}: room_height {definition.Height:0.###} is not a whole number of navigation voxels ({VoxelSize:0.###} units each)."));
        }

        if (whole > MaxColumnVoxels)
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"room {definition.Name}: room_height {definition.Height:0.###} is taller than {MaxColumnVoxels * (double)VoxelSize:0.###}, the most navigation describes ({MaxColumnVoxels} voxels)."));
        }

        return (int)whole;
    }

    /// <summary>A leaf's cost in the file's 8.8 fixed point, from its flags.</summary>
    /// <param name="water">Whether the leaf is water.</param>
    /// <param name="ladder">Whether it is a ladder.</param>
    /// <returns>The cost: 256 for 1.0, the product of the flags' multipliers, rounded to the nearest 1/256, at least 1.</returns>
    public ushort Cost(bool water, bool ladder)
    {
        double multiplier = (water ? WaterCost : 1.0) * (ladder ? LadderCost : 1.0);
        return (ushort)Math.Clamp(Math.Round(multiplier * 256, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);
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
            VoxelSize = Number(world, VoxelKey, DefaultVoxelSize, 0, float.MaxValue, "a positive number of units", allowLow: false),
            FloorNormalZ = floor,
            StepHeight = Number(world, StepKey, DefaultStepHeight, 0, float.MaxValue, "0 or more units", allowLow: true),
            JumpHeight = Number(world, JumpHeightKey, DefaultJumpHeight, 0, float.MaxValue, "0 or more units", allowLow: true),
            JumpDistance = Number(world, JumpDistanceKey, DefaultJumpDistance, 0, float.MaxValue, "0 or more units", allowLow: true),
            WaterCost = Number(world, WaterCostKey, DefaultWaterCost, 0, 255, "a multiplier above 0 and at most 255", allowLow: false),
            LadderCost = Number(world, LadderCostKey, DefaultLadderCost, 0, 255, "a multiplier above 0 and at most 255", allowLow: false),
            Agents = ParseAgents(world?.GetValue(AgentsKey) ?? DefaultAgents),
        };
    }

    /// <summary>Parses a preset list: <c>name width height [class]</c> entries separated by <c>;</c>.</summary>
    /// <param name="text">The list; empty for no presets.</param>
    /// <returns>The presets.</returns>
    /// <exception cref="RoomLibraryException">An entry is malformed, a name repeats, or there are too many.</exception>
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
                    $"the nav agent \"{entry}\" ({AgentsKey}) is not \"name width height [class]\".");
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

        if (agents.Count > Nav3dFormat.MaxPresets)
        {
            throw new RoomLibraryException(
                $"the nav agent list \"{text}\" ({AgentsKey}) names {agents.Count} agents; it names at most {Nav3dFormat.MaxPresets}.");
        }

        return agents;
    }

    /// <summary>
    /// A number as a mask: accepted only when it is one of the two clip
    /// classes' masks, because the grid keeps exactly those two worlds apart
    /// and any other set of contents would be answered for neither.
    /// </summary>
    private static int ParseMask(string text, string entry)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int mask)
            || mask is not (Nav3dFormat.PlayerSolidMask or Nav3dFormat.NpcSolidMask))
        {
            throw new RoomLibraryException(string.Create(CultureInfo.InvariantCulture,
                $"the nav agent \"{entry}\" ({AgentsKey}) has class \"{text}\"; a class is player, npc, or a number equal to one of their contents masks (0x{Nav3dFormat.PlayerSolidMask:x}, 0x{Nav3dFormat.NpcSolidMask:x})."));
        }

        return mask;
    }

    private static float Number(VmfChunk? world, string key, float fallback, float low, float high, string what, bool allowLow)
    {
        if (world?.GetValue(key) is not { } text)
        {
            return fallback;
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)
            || value > high || (allowLow ? value < low : value <= low))
        {
            throw new RoomLibraryException($"the worldspawn's \"{key}\" is \"{text}\", not {what}.");
        }

        return value;
    }
}
