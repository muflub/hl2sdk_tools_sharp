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
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

public sealed class Q3Diag2Tests(ITestOutputHelper output)
{
    [Fact]
    public async Task WallRoom()
    {
        RoomDefinition def = RoomHarness.Room("wall", RoomFacing.NegativeX, RoomFacing.PositiveX);
        VmfDocument doc = RoomHarness.BuildRoomModel(def);
        VmfChunk world = doc.Chunks.First(c => c.Name == MapFileLoader.WorldChunk);
        world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(112, 16, 16), new Vec3(144, 200, 240), 500));
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler.CompileAsync(doc, def, context);
        RoomLinkShared shared = LevelLinker.ComputeShared(room);
        RoomDoorVisibility dv = RoomDoorVisibility.Compute(room, shared);
        output.WriteLine($"clusters {room.ClusterCount}");
        for (int c = 0; c < room.ClusterCount; c++)
        {
            string row = string.Concat(Enumerable.Range(0, room.ClusterCount).Select(d => room.Vis.CanSee(c, d) ? "1" : "0"));
            output.WriteLine($"cluster {c} box {dv.ClusterBoxes[c].Mins} {dv.ClusterBoxes[c].Maxs} row {row} seesW {dv.SeesDoor(0, c)} seesE {dv.SeesDoor(1, c)}");
        }

        for (int s = 0; s < 2; s++) output.WriteLine($"socket {def.Sockets[s].Name} facing {string.Join(",", shared.Sockets[s].Facing)}");
        output.WriteLine($"through W->E {dv.IsThrough(0, 1)}");
    }
}
