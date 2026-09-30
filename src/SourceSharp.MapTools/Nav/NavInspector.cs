//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>One preset's numbers in a level's navigation, derived from the shared grid.</summary>
/// <param name="Name">The preset.</param>
/// <param name="Leaves">Leaves it fits somewhere in.</param>
/// <param name="StandingLeaves">Leaves it stands in (at their bottom voxel).</param>
/// <param name="FreeVolume">The space it fits in, in cubic units: the voxels of every leaf up to its fit.</param>
/// <param name="Components">Connected components.</param>
/// <param name="LargestComponentLeaves">Leaves in the largest component.</param>
/// <param name="RoomsInLargestComponent">Placed rooms with a leaf in the largest component.</param>
public sealed record NavAgentStats(
    string Name, int Leaves, int StandingLeaves, double FreeVolume, int Components, long LargestComponentLeaves, int RoomsInLargestComponent);

/// <summary>What the leaf export draws.</summary>
public enum NavObjMode
{
    /// <summary>Every leaf (or, for a preset, the part of it the preset fits in) as a box.</summary>
    Boxes,

    /// <summary>The walkable floor under every grounded leaf (or every leaf a preset stands in), as a square at the floor's height.</summary>
    Floor,
}

/// <summary>
/// Reports on a level's navigation: the numbers <c>ssmap nav</c> prints, and
/// an OBJ of the leaves or the floors for viewing in any 3D tool.
/// </summary>
public static class NavInspector
{
    /// <summary>One preset's numbers.</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="preset">The preset.</param>
    /// <returns>The numbers.</returns>
    public static NavAgentStats Stats(Nav3dReader nav, int preset)
    {
        ArgumentNullException.ThrowIfNull(nav);
        Nav3dPreset p = nav.Preset(preset);
        int leaves = 0;
        int standing = 0;
        long voxels = 0;
        long[] perComponent = new long[nav.ComponentCount(preset)];
        for (int l = 0; l < nav.LeafCount; l++)
        {
            int top = nav.FitTop(l, p.Width, p.Height, p.ClipClass);
            if (top < 0)
            {
                continue;
            }

            leaves++;
            voxels += top - nav.Leaf(l).ZLo + 1;
            standing += nav.Standable(l, nav.Leaf(l).ZLo, p.Width, p.Height, p.ClipClass) ? 1 : 0;
            perComponent[nav.Component(preset, l)]++;
        }

        int largest = 0;
        for (int c = 1; c < perComponent.Length; c++)
        {
            if (perComponent[c] > perComponent[largest])
            {
                largest = c;
            }
        }

        HashSet<int> rooms = [];
        for (int l = 0; l < nav.LeafCount; l++)
        {
            if (nav.Component(preset, l) == largest)
            {
                rooms.Add(nav.LeafColumn(l).Cell);
            }
        }

        double s = nav.VoxelSize;
        return new NavAgentStats(
            p.Name, leaves, standing, voxels * s * s * s, perComponent.Length, perComponent.Length == 0 ? 0 : perComponent[largest],
            perComponent.Length == 0 ? 0 : rooms.Count);
    }

    /// <summary>The report <c>ssmap nav</c> prints.</summary>
    /// <param name="nav">The navigation.</param>
    /// <returns>Lines of text.</returns>
    public static string Describe(Nav3dReader nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        StringBuilder text = new();
        int placed = 0;
        int joined = 0;
        for (int c = 0; c < nav.CellCount; c++)
        {
            placed += nav.Cell(c).Room is null ? 0 : 1;
        }

        for (int d = 0; d < nav.DoorCount; d++)
        {
            joined += nav.Door(d).Joined ? 1 : 0;
        }

        int grounded = 0;
        int water = 0;
        int ladder = 0;
        for (int l = 0; l < nav.LeafCount; l++)
        {
            Nav3dLeaf leaf = nav.Leaf(l);
            grounded += leaf.IsGrounded(Nav3dClipClass.Player) ? 1 : 0;
            water += (leaf.Flags & Nav3dLeafFlags.Water) != 0 ? 1 : 0;
            ladder += (leaf.Flags & Nav3dLeafFlags.Ladder) != 0 ? 1 : 0;
        }

        Line(text, $"grid {nav.Columns} x {nav.Rows} cells of {nav.CellSize:0.###} units, {placed} placed; voxel {nav.VoxelSize:0.###} ({nav.CellVoxels} per cell edge)");
        if (nav.Version != Nav3dFormat.CubeVersion)
        {
            // A level of rooms of their own heights (version 3): how tall
            // the columns run, which a file of cubes does not need to say.
            int lowest = Enumerable.Range(0, nav.CellCount).Select(nav.CellHeight).Where(h => h > 0).DefaultIfEmpty(0).Min();
            Line(text, $"cells of their own heights: {lowest} to {nav.TallestCell} voxels up");
        }

        Line(text, $"level id {nav.LevelId:D}, pack id {nav.PackId:D}, codec {nav.Codec}");
        Line(text, $"doors {nav.DoorCount} ({joined} joined, {nav.DoorCount - joined} capped); points of interest {nav.PoiCount}");
        Line(text, $"leaves {nav.LeafCount} ({grounded} on a player floor, {water} water, {ladder} ladder); jump links {nav.JumpCount}; dynamic obstacles {nav.ObstacleCount}");
        Line(text, $"step {nav.StepHeight:0.###}, jump {nav.JumpHeight:0.###} up and {nav.JumpDistance:0.###} across");
        if (nav.TryGetSpawn(out Vec3 spawn, out float yaw))
        {
            Line(text, $"spawn at ({spawn.X:0.###} {spawn.Y:0.###} {spawn.Z:0.###}) facing {yaw:0.###}");
        }
        else
        {
            text.Append("spawn: none\n");
        }

        for (int a = 0; a < nav.PresetCount; a++)
        {
            NavAgentStats stats = Stats(nav, a);
            Nav3dPreset preset = nav.Preset(a);
            Line(text, $"agent {a} \"{stats.Name}\" {preset.Width:0.###} x {preset.Height:0.###} {preset.ClipClass.ToString().ToLowerInvariant()}");
            Line(text, $"  fits in {stats.Leaves} leaves (stands in {stats.StandingLeaves}), free volume {stats.FreeVolume:0} cubic units");
            Line(text, $"  {stats.Components} components; the largest has {stats.LargestComponentLeaves} leaves in {stats.RoomsInLargestComponent} of {placed} rooms");
        }

        return text.ToString();
    }

    /// <summary>An OBJ of the leaves or floors, in level coordinates (Source units, z up).</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="preset">A preset to draw the leaves it fits in and the floors it stands on; -1 for every leaf and every walkable player floor.</param>
    /// <param name="mode">Boxes or floors.</param>
    /// <returns>The OBJ text.</returns>
    public static string Obj(Nav3dReader nav, int preset, NavObjMode mode)
    {
        ArgumentNullException.ThrowIfNull(nav);
        StringBuilder obj = new();
        string who = preset < 0 ? "every leaf" : $"agent \"{nav.Preset(preset).Name}\"";
        string what = mode == NavObjMode.Boxes ? "leaves" : "floors";
        Line(obj, $"# ssmap nav: {who}, {what}");
        int vertex = 1;
        double s = nav.VoxelSize;
        for (int l = 0; l < nav.LeafCount; l++)
        {
            Nav3dLeaf leaf = nav.Leaf(l);
            (Vec3 lo, Vec3 hi) = nav.LeafBounds(l);
            if (mode == NavObjMode.Floor)
            {
                bool draws = preset < 0
                    ? leaf.IsWalkable(Nav3dClipClass.Player)
                    : nav.Standable(l, leaf.ZLo, nav.Preset(preset).Width, nav.Preset(preset).Height, nav.Preset(preset).ClipClass);
                if (!draws)
                {
                    continue;
                }

                Nav3dClipClass clipClass = preset < 0 ? Nav3dClipClass.Player : nav.Preset(preset).ClipClass;
                float z = leaf.IsGrounded(clipClass) ? leaf.FloorZ(clipClass) : lo.Z;
                Vertex(obj, lo.X, lo.Y, z);
                Vertex(obj, hi.X, lo.Y, z);
                Vertex(obj, hi.X, hi.Y, z);
                Vertex(obj, lo.X, hi.Y, z);
                Line(obj, $"f {vertex} {vertex + 1} {vertex + 2} {vertex + 3}");
                vertex += 4;
                continue;
            }

            if (preset >= 0)
            {
                Nav3dPreset p = nav.Preset(preset);
                int top = nav.FitTop(l, p.Width, p.Height, p.ClipClass);
                if (top < 0)
                {
                    continue;
                }

                hi = new Vec3(hi.X, hi.Y, (float)(nav.Origin.Z + ((top + 1) * s)));
            }

            for (int c = 0; c < 8; c++)
            {
                Vertex(obj, (c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
            }

            ReadOnlySpan<int> faces = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5];
            for (int f = 0; f < 24; f += 4)
            {
                Line(obj, $"f {vertex + faces[f]} {vertex + faces[f + 1]} {vertex + faces[f + 2]} {vertex + faces[f + 3]}");
            }

            vertex += 8;
        }

        return obj.ToString();
    }

    private static void Vertex(StringBuilder obj, float x, float y, float z) =>
        Line(obj, $"v {x:0.###} {y:0.###} {z:0.###}");

    private static void Line(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');
}
