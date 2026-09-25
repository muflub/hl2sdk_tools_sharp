using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The empirical basis of the whole room design: a plug brush made of a
/// <c>%compileTrigger</c> material is solid to the compile — it stops vbsp's
/// flood fill (so a shell whose only closure at the socket is the plug
/// compiles sealed) and it stops vvis (so the room's own PVS is computed
/// with the door shut, and the linked map needs the door graph at all).
/// </summary>
/// <remarks>
/// If either half of this is false the design changes: a plug that does not
/// stop the flood means the linker must do BSP surgery; a plug that vis sees
/// through means the room's PVS already includes its neighbours. Stock
/// makes a trigger side solid at <c>map.cpp:2747</c> (a trigger material sets
/// surface flags, no contents, and the no-visible-contents rule then adds
/// <c>CONTENTS_SOLID</c>); the facts here pin that the port does the same.
/// </remarks>
public class TriggerPlugProbeTests
{
    [Fact]
    public async Task ATriggerPlugSealsTheShellAndBlocksVis()
    {
        // A room with one face open, closed only by its trigger plug.
        RoomDefinition room = RoomHarness.Room("plug", RoomFacing.PositiveX);
        VbspContext context = await RoomHarness.ContextAsync();
        VbspResult result = await RoomHarness.CompileAsync(RoomHarness.BuildRoomModel(room), context);

        // Half one: no leak — the plug is what closed the shell.
        Assert.Null(result.Leak);
        Assert.NotNull(result.Bsp);
        Assert.NotNull(result.Portals);

        // Half two: vis sees a single interior cluster, and nothing inside the
        // plug box has a cluster at all — the plug is solid to vvis, so the
        // room's PVS is the room-with-the-door-shut.
        VisResult vis = await RoomHarness.VisAsync(result.Bsp!, PortalSet.FromPortalFile(result.Portals!));
        Assert.Equal(1, vis.ClusterCount);
        List<short> clusters = RoomHarness.Clusters(result.Bsp!);
        Assert.Contains((short)0, clusters);

        Box plug = RoomCompiler.SealBoxes(room)[0];
        int index = 0;
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(result.Bsp![BspLump.Leafs]))
        {
            Vec3 center = new(
                (leaf.Mins[0] + leaf.Maxs[0]) / 2f,
                (leaf.Mins[1] + leaf.Maxs[1]) / 2f,
                (leaf.Mins[2] + leaf.Maxs[2]) / 2f);
            bool insidePlug =
                center.X >= plug.Mins.X - 2 && center.X <= plug.Maxs.X + 2
                && center.Y >= plug.Mins.Y - 2 && center.Y <= plug.Maxs.Y + 2
                && center.Z >= plug.Mins.Z - 2 && center.Z <= plug.Maxs.Z + 2;
            if (insidePlug)
            {
                Assert.True(leaf.Cluster < 0, $"leaf {index} at {center} escaped the plug");
            }

            index++;
        }
    }

    [Fact]
    public async Task TheSameShellWithoutItsPlugLeaks()
    {
        // The control for probe A: the identical shell minus its plug brushes
        // leaks, so the seal in probe A is the plug's doing and not the
        // shell's own closure.
        RoomDefinition room = RoomHarness.Room("plug", RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(room);

        // Remove the plug brushes from the world chunks only (the plug is the
        // only trigger-material solid); entity chunks, including the player
        // start the leak reporter walks from, stay.
        foreach (VmfChunk world in document.Chunks.Where(c => c.Name == MapFileLoader.WorldChunk))
        {
            List<VmfChunk> keep = [];
            foreach (VmfChunk solid in world.Chunks)
            {
                if (!UsesMaterial(solid, RoomHarness.Trigger))
                {
                    keep.Add(solid);
                }
            }

            world.Children.Clear();
            foreach (VmfChunk solid in keep)
            {
                world.Children.Add(solid);
            }
        }

        VbspContext context = await RoomHarness.ContextAsync();
        VbspResult result = await RoomHarness.CompileAsync(document, context);

        Assert.NotNull(result.Leak);
        Assert.Null(result.Portals);
    }

    private static bool UsesMaterial(VmfChunk chunk, string material)
    {
        if (string.Equals(chunk.GetValue("material"), material, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (VmfChunk child in chunk.Chunks)
        {
            if (UsesMaterial(child, material))
            {
                return true;
            }
        }

        return false;
    }
}
