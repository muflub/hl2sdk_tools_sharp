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

/// <summary>One agent's numbers in a level's navigation.</summary>
/// <param name="Name">The agent.</param>
/// <param name="Nodes">Octree nodes over every cell.</param>
/// <param name="Leaves">Free leaves.</param>
/// <param name="FloorLeaves">Free leaves with a floor under them.</param>
/// <param name="FreeVolume">The free space, in cubic units.</param>
/// <param name="Components">Connected components.</param>
/// <param name="LargestComponentLeaves">Leaves in the largest component.</param>
/// <param name="DoorLinks">Leaf pairs joined through doors.</param>
/// <param name="RoomsInLargestComponent">Placed rooms with a leaf in the largest component.</param>
public sealed record NavAgentStats(
    string Name, int Nodes, int Leaves, int FloorLeaves, double FreeVolume, int Components, long LargestComponentLeaves,
    int DoorLinks, int RoomsInLargestComponent);

/// <summary>What the free-leaf export draws.</summary>
public enum NavObjMode
{
    /// <summary>Every free leaf as a box.</summary>
    Boxes,

    /// <summary>The floor under every floor leaf, as a square at the leaf's bottom.</summary>
    Floor,
}

/// <summary>
/// Reports on a level's navigation: the numbers <c>ssmap nav</c> prints, and
/// an OBJ of the free leaves or the floors for viewing in any 3D tool.
/// </summary>
public static class NavInspector
{
    /// <summary>One agent's numbers.</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="agent">The agent.</param>
    /// <returns>The numbers.</returns>
    public static NavAgentStats Stats(Nav3dReader nav, int agent)
    {
        ArgumentNullException.ThrowIfNull(nav);
        int floors = 0;
        double voxels = 0;
        long[] perComponent = new long[nav.ComponentCount(agent)];
        for (int l = 0; l < nav.LeafCount(agent); l++)
        {
            Nav3dLeaf leaf = nav.Leaf(agent, l);
            floors += (leaf.Flags & Nav3dLeafFlags.Floor) != 0 ? 1 : 0;
            voxels += leaf.Voxels;
            perComponent[leaf.Component]++;
        }

        int largest = 0;
        for (int c = 1; c < perComponent.Length; c++)
        {
            if (perComponent[c] > perComponent[largest])
            {
                largest = c;
            }
        }

        HashSet<uint> rooms = [];
        for (int l = 0; l < nav.LeafCount(agent); l++)
        {
            Nav3dLeaf leaf = nav.Leaf(agent, l);
            if (leaf.Component == largest)
            {
                rooms.Add(leaf.Cell);
            }
        }

        double s = nav.VoxelSize;
        return new NavAgentStats(
            nav.AgentName(agent), nav.NodeCount(agent), nav.LeafCount(agent), floors, voxels * s * s * s,
            perComponent.Length, perComponent.Length == 0 ? 0 : perComponent[largest], nav.LinkCount(agent), rooms.Count);
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

        Line(text, $"grid {nav.Columns} x {nav.Rows} cells of {nav.CellSize:0.###} units, {placed} placed; voxel {nav.VoxelSize:0.###} ({nav.CellVoxels} per cell edge)");
        Line(text, $"level id {nav.LevelId:D}, pack id {nav.PackId:D}, codec {nav.Codec}");
        Line(text, $"doors {nav.DoorCount} ({joined} joined, {nav.DoorCount - joined} capped); points of interest {nav.PoiCount}");
        if (nav.TryGetSpawn(out Vec3 spawn, out float yaw))
        {
            Line(text, $"spawn at ({spawn.X:0.###} {spawn.Y:0.###} {spawn.Z:0.###}) facing {yaw:0.###}");
        }
        else
        {
            text.Append("spawn: none\n");
        }

        for (int a = 0; a < nav.AgentCount; a++)
        {
            NavAgentStats stats = Stats(nav, a);
            (Vec3 mins, Vec3 maxs) = nav.AgentBox(a);
            Line(text, $"agent {a} \"{stats.Name}\" box ({mins.X:0.###} {mins.Y:0.###} {mins.Z:0.###})-({maxs.X:0.###} {maxs.Y:0.###} {maxs.Z:0.###}) mask 0x{nav.AgentContentsMask(a):x}");
            Line(text, $"  {stats.Leaves} free leaves ({stats.FloorLeaves} floor), {stats.Nodes} nodes, free volume {stats.FreeVolume:0} cubic units");
            Line(text, $"  {stats.Components} components; the largest has {stats.LargestComponentLeaves} leaves in {stats.RoomsInLargestComponent} of {placed} rooms; {stats.DoorLinks} door links");
        }

        return text.ToString();
    }

    /// <summary>An OBJ of one agent's free leaves or floors, in level coordinates (Source units, z up).</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="mode">Boxes or floors.</param>
    /// <returns>The OBJ text.</returns>
    public static string Obj(Nav3dReader nav, int agent, NavObjMode mode)
    {
        ArgumentNullException.ThrowIfNull(nav);
        StringBuilder obj = new();
        string what = mode == NavObjMode.Boxes ? "free leaves" : "floors";
        Line(obj, $"# ssmap nav: agent \"{nav.AgentName(agent)}\", {what}");
        int vertex = 1;
        for (int l = 0; l < nav.LeafCount(agent); l++)
        {
            Nav3dLeaf leaf = nav.Leaf(agent, l);
            Vec3 lo = nav.LeafMins(agent, l);
            float size = nav.LeafSize(agent, l);
            if (mode == NavObjMode.Floor)
            {
                if ((leaf.Flags & Nav3dLeafFlags.Floor) == 0)
                {
                    continue;
                }

                Vertex(obj, lo.X, lo.Y, lo.Z);
                Vertex(obj, lo.X + size, lo.Y, lo.Z);
                Vertex(obj, lo.X + size, lo.Y + size, lo.Z);
                Vertex(obj, lo.X, lo.Y + size, lo.Z);
                Line(obj, $"f {vertex} {vertex + 1} {vertex + 2} {vertex + 3}");
                vertex += 4;
                continue;
            }

            for (int c = 0; c < 8; c++)
            {
                Vertex(obj, lo.X + ((c & 1) * size), lo.Y + (((c >> 1) & 1) * size), lo.Z + (((c >> 2) & 1) * size));
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
