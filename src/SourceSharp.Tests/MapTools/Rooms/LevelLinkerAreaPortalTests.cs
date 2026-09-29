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

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomAreaPortalHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Area portals and areas through the link (section 4.11 of the rooms
/// design): a room's own areas are joined to its neighbours' at every
/// joint, its portals are listed for the level with their numbers rebased,
/// and the level's areas and portals agree with the flattened level's vbsp
/// compile at every quarter turn.
/// </summary>
public sealed class LevelLinkerAreaPortalTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A line of three rooms, the split room between two hubs, along the
    /// split room's sockets at its turn: a row for 0 and 180 degrees, a
    /// column for 90 and 270.
    /// </summary>
    private static LevelGrid Line(int rotation) => rotation % 180 == 0
        ? RoomPropHarness.Level($"hub, split@{rotation}, hub")
        : RoomPropHarness.Level("hub", $"split@{rotation}", "hub");

    // ---- placement, turned -------------------------------------------------------------------

    /// <summary>
    /// The split room between two hubs, at every quarter turn: the level
    /// has two areas (each hub joined to the half of the split room its
    /// doorway opens onto) and the one portal between them, listed from
    /// both sides with its key 1; the linked and the flattened maps make the
    /// same partition of the open space into areas, list the same portals
    /// (outline and normal) between the same areas, each map's portal on a
    /// face of the portal's brush (which face follows each compile's flood
    /// order), and number the portal's entity alike; and every node the
    /// link says is in one area holds only that area's leaves.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsItsRoomsAreasAndPortalsAtEveryRotation(int rotation)
    {
        VmfDocument library = Library();
        LevelGrid level = Line(rotation);
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(3, Areas(linked.Bsp).Length);
        Assert.Equal(3, Listings(linked.Bsp).Length);
        Assert.Equal(Areas(flat).Length, Areas(linked.Bsp).Length);
        Dictionary<int, int> names = SamePartition(linked.Bsp, flat, level);
        Assert.Equal(2, names.Count);
        Assert.Equal(Portals(flat), Portals(linked.Bsp, names));
        Box[] brushes = [.. linked.Plan.Layout.Rooms.Where(r => r.Placement.Room == "split").Select(r => Placed(Doorway, r.Placement))];
        OnPortalFace(linked.Bsp, brushes);
        OnPortalFace(flat, brushes);
        Assert.Equal([1], PortalNumbers(linked.Bsp));
        Assert.Equal(PortalNumbers(flat), PortalNumbers(linked.Bsp));
        NodeAreasHold(linked.Bsp);
        Assert.Empty(linked.AreaWarnings);
    }
}
