//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms of their own heights for the heights facts (the rooms design,
/// 17.6): the prop harness's four-socket hub, a cube; <c>tall</c>, the same
/// room 512 units tall; and <c>mid</c>, 384 tall; all on the walkable kit,
/// so their doors are the hub's, on the floor.
/// </summary>
internal static class RoomHeightHarness
{
    /// <summary>The tall room's height: two cells.</summary>
    public const float Tall = 512;

    /// <summary>The middle room's height: a cell and a half.</summary>
    public const float Mid = 384;

    /// <summary>The four-socket cube room.</summary>
    public static RoomDefinition Hub => RoomPropHarness.Hub;

    /// <summary>A four-socket room of the given height.</summary>
    public static RoomDefinition Shaped(string name, float height) => RoomHarness.WalkableRoom(
        name, RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY) with { Height = height };

    /// <summary>The library of hub, tall and mid, in that order, each with the given extra entities at room-local origins.</summary>
    public static VmfDocument Library(params (int Room, VmfChunk Entity)[] extra)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, Shaped("tall", Tall), Shaped("mid", Mid));
        foreach ((int room, VmfChunk entity) in extra)
        {
            Vec3 corner = new(room * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
            library.Chunks.Add(VmfPlacement.MoveEntity(entity, QuarterTurn.Translation(corner)));
        }

        return library;
    }

    /// <summary>A level of the given rows of the harness library.</summary>
    public static LevelGrid Level(params string[] rows) => LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "heights");

    /// <summary>The level flattened from the library and compiled whole.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomLightHarness.ContextAsync("flat"));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>
    /// A lattice over every cell of a level, up to <paramref name="top"/>:
    /// sixteen units apart across, and at heights inside a cube room, inside
    /// a tall room only, and above every room.
    /// </summary>
    public static IEnumerable<Vec3> Samples(LevelGrid level, float top = Tall + 64)
    {
        float[] heights = [40, 130, 250, 300, 380, 450, 505, 540];
        for (int row = 0; row < level.Rows; row++)
        {
            for (int column = 0; column < level.Columns; column++)
            {
                for (int i = 0; i < 16; i++)
                {
                    for (int j = 0; j < 16; j++)
                    {
                        foreach (float z in heights.Where(h => h <= top))
                        {
                            yield return new Vec3((column * RoomHarness.Cell) + 6 + (16 * i), (row * RoomHarness.Cell) + 6 + (16 * j), z);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Holds a linked and a flattened map to one answer at every sample: the
    /// same solid or open space, and (open) the same leaf contents.
    /// </summary>
    /// <returns>How many samples were open.</returns>
    public static int SameSpace(BspData linked, BspData flat, LevelGrid level)
    {
        int open = 0;
        foreach (Vec3 point in Samples(level))
        {
            DLeaf a = RoomHarness.LeafAt(linked, point);
            DLeaf b = RoomHarness.LeafAt(flat, point);
            bool solidA = (a.Contents & (int)BrushContents.Solid) != 0;
            bool solidB = (b.Contents & (int)BrushContents.Solid) != 0;
            Assert.True(solidA == solidB, $"at {point} the linked leaf is {(solidA ? "solid" : "open")}, the flattened one {(solidB ? "solid" : "open")}");
            open += solidA ? 0 : 1;
        }

        return open;
    }

    /// <summary>
    /// The engine's culling promise, over model 0's tree: every node's bounds
    /// hold every open leaf below it; the first node that does not, as text,
    /// or null.
    /// </summary>
    /// <remarks>
    /// The renderer and the engine's box walks skip a node whose bounds are
    /// out of view or out of the box, so a node bounded short of an open leaf
    /// below it drops that leaf from view whenever only its part past the
    /// bounds is on screen. Solid leaves are left out (the shared solid leaf
    /// is bounded by the whole grid and hangs under every top-tree node), and
    /// so are nodes' own bounds below a room's root, which vbsp pads.
    /// </remarks>
    public static string? BoundsProblem(BspData bsp)
    {
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        DLeaf[] leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        DModel world = BspStructView.As<DModel>(bsp[BspLump.Models])[0];
        string? problem = null;
        _ = Open(world.HeadNode);
        return problem;

        // The open leaves' bounds under a child, checking each node on the way back up.
        (int[] Mins, int[] Maxs)? Open(int child)
        {
            if (child < 0)
            {
                DLeaf leaf = leaves[-(child + 1)];
                return (leaf.Contents & (int)BrushContents.Solid) != 0
                    ? null
                    : ([leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]], [leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]]);
            }

            DNode node = nodes[child];
            (int[] Mins, int[] Maxs)? a = Open(node.Children[0]);
            (int[] Mins, int[] Maxs)? b = Open(node.Children[1]);
            (int[] Mins, int[] Maxs)? both = a is null ? b : b is null ? a
                : ([.. a.Value.Mins.Zip(b.Value.Mins, Math.Min)], [.. a.Value.Maxs.Zip(b.Value.Maxs, Math.Max)]);
            if (both is { } box && problem is null)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    if (box.Mins[axis] < node.Mins[axis] || box.Maxs[axis] > node.Maxs[axis])
                    {
                        problem = $"node {child} ({node.Mins[0]} {node.Mins[1]} {node.Mins[2]} .. {node.Maxs[0]} {node.Maxs[1]} {node.Maxs[2]})"
                            + $" does not hold its open leaves ({string.Join(' ', box.Mins)} .. {string.Join(' ', box.Maxs)})";
                        break;
                    }
                }
            }

            return both;
        }
    }
}
