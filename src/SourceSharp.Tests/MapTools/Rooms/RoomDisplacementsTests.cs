//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The displacement transforms and the displacement section on their own
/// (the rooms design, 4.5 and 15.2's displacements row): the start and
/// vector turns, the record rebase, the lumps' checks, the section's
/// refusals, and the geometry the pack's rules read.
/// </summary>
public sealed class RoomDisplacementsTests
{
    // ---- the transforms ----------------------------------------------------------------------

    /// <summary>
    /// A room's displacements at each quarter turn: every start position and
    /// vertex vector turned about +z, exactly (a negative zero kept, as the
    /// turn gives it); turn 0 is the compile's own; a room without
    /// displacements has none.
    /// </summary>
    [Fact]
    public void StartsAndVectorsTurnWithTheRoom()
    {
        BspData bsp = Bsp(2);
        RoomDisplacements built = RoomDisplacements.Build("r", bsp)!;
        Assert.Equal(2, built.Count);
        Assert.Equal(50, built.VertexCount);
        Assert.Equal(4, built.TurnCount);
        Assert.Null(RoomDisplacements.Build("r", new BspData()));

        Assert.Equal(new Vec3(16, 8, 24), built.Starts(0)[0]);
        Assert.Equal(new Vec3(-8, 16, 24), built.Starts(1)[0]);
        Assert.Equal(new Vec3(-16, -8, 24), built.Starts(2)[0]);
        Assert.Equal(new Vec3(8, -16, 24), built.Starts(3)[0]);
        Assert.Equal(new Vec3(0.6f, 0, 0.8f), built.Vectors(0)[1]);
        Assert.Equal(new Vec3(-0f, 0.6f, 0.8f), built.Vectors(1)[1]);
        Assert.True(float.IsNegative(built.Vectors(1)[1].X));
        Assert.Equal(new Vec3(-0.6f, -0f, 0.8f), built.Vectors(2)[1]);
        Assert.Equal(new Vec3(0, -0.6f, 0.8f), built.Vectors(3)[1]);
    }

    /// <summary>
    /// A record as a placement links it: its start the turned start plus the
    /// placement's translation, a negative zero unsigned; its runs, sample
    /// and alpha starts and face rebased; every edge neighbour it has and
    /// every corner neighbour up to its count rebased, a missing edge
    /// neighbour and a corner's unused slots left; the rest as it was.
    /// </summary>
    [Fact]
    public void ARecordIsRebasedAndItsStartMoved()
    {
        DispInfo record = Record(0, 3);
        record.EdgeNeighbors[1].SubNeighbors[0].Neighbor = 1;
        record.EdgeNeighbors[1].SubNeighbors[0].NeighborOrientation = 2;
        record.CornerNeighbors[2].NumNeighbors = 1;
        record.CornerNeighbors[2].Neighbors[0] = 1;
        record.CornerNeighbors[2].Neighbors[1] = 7;
        record.AllowedVerts[3] = 0x1234;

        DispInfo linked = LevelLinker.LinkDisplacement(
            record, new Vec3(-0f, 8.5f, 24), new Vec3(0, 256, 0), new LevelLinker.DisplacementBases(10, 100, 200, 3, 400), f => f + 1000);

        Assert.Equal(new Vec3(0, 264.5f, 24), linked.StartPosition);
        Assert.False(float.IsNegative(linked.StartPosition.X));
        Assert.Equal(100, linked.DispVertStart);
        Assert.Equal(200, linked.DispTriStart);
        Assert.Equal(3, linked.LightmapAlphaStart);
        Assert.Equal(400 + record.LightmapSamplePositionStart, linked.LightmapSamplePositionStart);
        Assert.Equal(1003, linked.MapFace);
        Assert.Equal(11, linked.EdgeNeighbors[1].SubNeighbors[0].Neighbor);
        Assert.Equal(2, linked.EdgeNeighbors[1].SubNeighbors[0].NeighborOrientation);
        Assert.Equal(DispSubNeighbor.NoNeighbor, linked.EdgeNeighbors[0].SubNeighbors[0].Neighbor);
        Assert.Equal(DispSubNeighbor.NoNeighbor, linked.EdgeNeighbors[1].SubNeighbors[1].Neighbor);
        Assert.Equal(11, linked.CornerNeighbors[2].Neighbors[0]);
        Assert.Equal(7, linked.CornerNeighbors[2].Neighbors[1]);
        Assert.Equal(0x1234u, linked.AllowedVerts[3]);
        Assert.Equal(record.Power, linked.Power);
        Assert.Equal(record.MinTess, linked.MinTess);
        Assert.Equal(record.Contents, linked.Contents);
    }

    // ---- the lumps -----------------------------------------------------------------------------

    /// <summary>
    /// A room's displacement lumps are held to what vbsp writes, and each
    /// departure refused naming the room: runs out of order, a base face
    /// that does not name its displacement back, a power outside 2 to 4,
    /// sample positions past their lump, lumps longer than the records, a
    /// collision lump for another number of displacements, and vertices
    /// without a displacement; the collision blobs are read one per
    /// displacement.
    /// </summary>
    [Fact]
    public void LumpsVbspWouldNotWriteAreRefused()
    {
        Assert.Equal(2, RoomDisplacements.Records("r", Bsp(2)).Length);
        Assert.Equal(0, RoomDisplacements.Records("r", new BspData()).Length);

        Assert.Equal(
            "room r's displacement 1 starts its vertices at 0 and its triangles at 32; vbsp lays them out in order, at 25 and 32.",
            Refused(bsp => Edit(bsp, 1, (ref DispInfo d) => d.DispVertStart = 0)));
        Assert.Equal(
            "room r's displacement 1 names face 0, which is not its base face.",
            Refused(bsp => Edit(bsp, 1, (ref DispInfo d) => d.MapFace = 0)));
        Assert.Equal(
            "room r's displacement 1 names face 9, which is not its base face.",
            Refused(bsp => Edit(bsp, 1, (ref DispInfo d) => d.MapFace = 9)));
        Assert.Equal(
            "room r's displacement 0 has power 5; vbsp writes 2 to 4.",
            Refused(bsp => Edit(bsp, 0, (ref DispInfo d) => d.Power = 5)));
        Assert.Equal(
            "room r's displacement 1 starts its sample positions at 99, past the 20-byte lump.",
            Refused(bsp => Edit(bsp, 1, (ref DispInfo d) => d.LightmapSamplePositionStart = 99)));
        Assert.Equal(
            "room r's displacements hold 50 vertices and 64 triangles; its lumps hold 51 and 64.",
            Refused(bsp => bsp.SetLump(BspLump.DispVerts, new byte[51 * 20])));
        Assert.Equal(
            "room r has 2 displacements and collision for 1.",
            Refused(bsp => bsp.SetLump(BspLump.PhysDisp, PhysDispLump.Write([[1, 2]]))));
        Assert.Equal(
            "room r has 3 displacement vertices and 0 triangles but no displacement.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Records("r", VertsOnly())).Message);

        BspData cooked = Bsp(2);
        cooked.SetLump(BspLump.PhysDisp, PhysDispLump.Write([[1, 2, 3], null]));
        Assert.Equal([[1, 2, 3], null], RoomDisplacements.CollisionBlobs(cooked));
        Assert.Empty(RoomDisplacements.CollisionBlobs(Bsp(2)));

        static string Refused(Action<BspData> edit)
        {
            BspData bsp = Bsp(2);
            edit(bsp);
            return Assert.Throws<LinkException>(() => RoomDisplacements.Records("r", bsp)).Message;
        }

        static BspData VertsOnly()
        {
            BspData bsp = new();
            bsp.SetLump(BspLump.DispVerts, new byte[3 * 20]);
            return bsp;
        }
    }

    // ---- the section ---------------------------------------------------------------------------

    /// <summary>
    /// The section round trips at four turns and at one (the link turning
    /// turn 0, to the same values), and is refused as damaged when it does
    /// not fit the room's lumps, holds a turn count other than 1 or 4, or
    /// has bytes after its end; an absent section, or one of a revision this
    /// build does not read, is none.
    /// </summary>
    [Fact]
    public void TheSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp(2);
        RoomDisplacements built = RoomDisplacements.Build("r", bsp)!;
        RoomDisplacements read = RoomDisplacements.Read(Payload(built.ToSection()), "r", bsp)!;
        RoomDisplacements once = RoomDisplacements.Read(Payload(built.WithTurnZeroOnly().ToSection()), "r", bsp)!;
        Assert.Equal(4, read.TurnCount);
        Assert.Equal(1, once.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(built.Starts(turn), read.Starts(turn));
            Assert.Equal(built.Starts(turn), once.Starts(turn));
            Assert.Equal(built.Vectors(turn), read.Vectors(turn));
            Assert.Equal(built.Vectors(turn), once.Vectors(turn));
        }

        Assert.Null(RoomDisplacements.Read(null, "r", bsp));
        byte[] revised = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(revised.AsSpan(9), 99);
        Assert.Null(RoomDisplacements.Read(revised, "r", bsp));

        Assert.Equal(
            "room pack entry \"r\": its \"DISP\" section holds 2 displacements; the room has 1.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Read(Payload(built.ToSection()), "r", Bsp(1))).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DISP\" section holds 2 displacements; the room has 0.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Read(Payload(built.ToSection()), "r", new BspData())).Message);

        BspData moreVerts = Bsp(2);
        moreVerts.SetLump(BspLump.DispVerts, new byte[51 * 20]);
        Assert.Equal(
            "room pack entry \"r\": its \"DISP\" section holds 50 displacement vertices; the room has 51.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Read(Payload(built.ToSection()), "r", moreVerts)).Message);

        byte[] twoTurns = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(twoTurns.AsSpan(9 + 12), 2);
        Assert.Equal(
            "room pack entry \"r\": its \"DISP\" section holds 2 turns of displacements; a section holds 1 or 4.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Read(twoTurns, "r", bsp)).Message);

        byte[] section = Payload(built.ToSection()).ToArray();
        byte[] longer = [.. section, 0];
        BinaryPrimitives.WriteInt64BigEndian(longer.AsSpan(1), BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)) + 1);
        Assert.Equal(
            "room pack entry \"r\": its \"DISP\" section holds 1 bytes after its end.",
            Assert.Throws<LinkException>(() => RoomDisplacements.Read(longer, "r", bsp)).Message);
    }

    // ---- the geometry the pack's rules read ----------------------------------------------------

    /// <summary>
    /// A side's face is cut from its plane by the brush's other planes: the
    /// four corners of a box's top, whichever side is asked for; a side the
    /// brush does not hold, or a brush whose planes leave the side nothing,
    /// has none.
    /// </summary>
    [Fact]
    public void ASidesFaceIsCutFromItsPlane()
    {
        VmfChunk slab = RoomModel.Slab(RoomHarness.Plain, new Vec3(64, 32, 16), new Vec3(128, 96, 24), 7);
        Vec3[] top = RoomDisplacements.BaseFace(slab, slab.Chunks.First())!;
        Assert.Equal(4, top.Length);
        Assert.All(top, p => Assert.Equal(24, p.Z));
        Assert.Equal(
            [new Vec3(64, 32, 24), new Vec3(64, 96, 24), new Vec3(128, 32, 24), new Vec3(128, 96, 24)],
            top.OrderBy(p => p.X).ThenBy(p => p.Y));

        Vec3[] west = RoomDisplacements.BaseFace(slab, slab.Chunks.ElementAt(3))!;
        Assert.All(west, p => Assert.Equal(64, p.X));
        Assert.Equal(4, west.Length);

        Assert.Null(RoomDisplacements.BaseFace(slab, new VmfChunk("side")));

        // Two tops, the second above the first: the lower one's face is cut away entirely.
        VmfChunk capped = RoomModel.Slab(RoomHarness.Plain, new Vec3(0, 0, 0), new Vec3(16, 16, 16), 8);
        VmfChunk lid = RoomModel.Slab(RoomHarness.Plain, new Vec3(0, 0, 0), new Vec3(16, 16, 8), 9).Chunks.First();
        capped.Children.Add(lid);
        Assert.Null(RoomDisplacements.BaseFace(capped, capped.Chunks.First()));
    }

    /// <summary>
    /// A segment meets a closed box when any point of it is inside or on the
    /// box: through it, ending on its face, lying along it; one that passes
    /// by, stops short, or lies in a face's plane outside it does not.
    /// </summary>
    [Theory]
    [InlineData(0, 5, 5, 20, 5, 5, true)]
    [InlineData(-5, 5, 5, 0, 5, 5, true)]
    [InlineData(0, 10, 10, 10, 10, 10, true)]
    [InlineData(2, 2, 2, 3, 3, 3, true)]
    [InlineData(-5, 5, 5, -1, 5, 5, false)]
    [InlineData(-5, 11, 5, 20, 11, 5, false)]
    [InlineData(-5, -5, 0, 5, -1, 0, false)]
    [InlineData(-5, 5, 20, 20, 5, 11, false)]
    public void ASegmentMeetsABoxWhenAPointOfItIsInIt(float ax, float ay, float az, float bx, float by, float bz, bool meets)
    {
        Box box = new(Vec3.Zero, new Vec3(10, 10, 10));
        Assert.Equal(meets, RoomDisplacements.SegmentTouches(new Vec3(ax, ay, az), new Vec3(bx, by, bz), box));
        Assert.Equal(meets, RoomDisplacements.SegmentTouches(new Vec3(bx, by, bz), new Vec3(ax, ay, az), box));
    }

    /// <summary>
    /// The part of a plug box a displacement edge may not enter: the box
    /// widened by the tolerance on every side but its inner face, which is
    /// pulled in by the tolerance, for each facing.
    /// </summary>
    [Fact]
    public void TheDoorwayIsThePlugBoxBarItsInnerFace()
    {
        RoomDefinition room = RoomPropHarness.Hub;
        const float E = RoomLinter.CellEpsilon;
        foreach (RoomSocket socket in room.Sockets)
        {
            Box plug = RoomLinter.SealBox(room, socket, room.CellSize);
            Box doorway = RoomDisplacements.Doorway(room, socket);
            Vec3 lo = plug.Mins - new Vec3(E, E, E), hi = plug.Maxs + new Vec3(E, E, E);
            Box expected = socket.Facing switch
            {
                RoomFacing.PositiveX => new Box(new Vec3(plug.Mins.X + E, lo.Y, lo.Z), hi),
                RoomFacing.NegativeX => new Box(lo, new Vec3(plug.Maxs.X - E, hi.Y, hi.Z)),
                RoomFacing.PositiveY => new Box(new Vec3(lo.X, plug.Mins.Y + E, lo.Z), hi),
                _ => new Box(lo, new Vec3(hi.X, plug.Maxs.Y - E, hi.Z)),
            };
            Assert.Equal(expected, doorway);
        }
    }

    /// <summary>
    /// The cell rule reads the room's box (the rooms design, 17.6): a patch
    /// standing above a cube room's ceiling reaches out of a cube room, by
    /// how far its highest vertex stands past it, and not out of a room tall
    /// enough to hold it; one inside the cube is inside both.
    /// </summary>
    [Fact]
    public void TheCellRuleReadsTheRoomsHeight()
    {
        RoomDefinition cube = RoomPropHarness.Hub;
        RoomDefinition tall = cube with { Height = 512 };
        VmfDocument high = World(RoomDisplacementHarness.Patch(RoomDisplacementHarness.PatchBrush, new Box(new Vec3(64, 64, 400), new Vec3(128, 128, 404)), offsets: false));
        Assert.Equal(
            "room hub: the displacement on brush side 48000 reaches 156.50 units outside the cell; displacements stay in their cell.",
            RoomDisplacements.Problem(cube, high));
        Assert.Null(RoomDisplacements.Problem(tall, high));

        VmfDocument low = World(RoomDisplacementHarness.Patch(RoomDisplacementHarness.PatchBrush, RoomDisplacementHarness.HubWest));
        Assert.Null(RoomDisplacements.Problem(cube, low));
        Assert.Null(RoomDisplacements.Problem(tall, low));

        static VmfDocument World(VmfChunk solid)
        {
            VmfDocument document = new();
            VmfChunk world = new(SourceSharp.MapTools.Bsp.MapFileLoader.WorldChunk);
            world.Children.Add(solid);
            document.Chunks.Add(world);
            return document;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>A record of power 2 as vbsp writes the <paramref name="index"/>th of a room: runs in order, no neighbours.</summary>
    private static DispInfo Record(int index, int face)
    {
        DispInfo record = default;
        record.Power = 2;
        record.MinTess = unchecked((int)0x80000000);
        record.SmoothingAngle = 45;
        record.Contents = 1;
        record.StartPosition = new Vec3(16 + index, 8, 24);
        record.DispVertStart = index * 25;
        record.DispTriStart = index * 32;
        record.MapFace = (ushort)face;
        record.LightmapSamplePositionStart = index * 10;
        for (int e = 0; e < 4; e++)
        {
            record.EdgeNeighbors[e].SubNeighbors[0].Neighbor = DispSubNeighbor.NoNeighbor;
            record.EdgeNeighbors[e].SubNeighbors[1].Neighbor = DispSubNeighbor.NoNeighbor;
        }

        return record;
    }

    /// <summary>A room of <paramref name="count"/> displacements of power 2, on faces 1, 2, ..., each vertex 1 leaning along +x.</summary>
    private static BspData Bsp(int count)
    {
        DispInfo[] infos = [.. Enumerable.Range(0, count).Select(i => Record(i, i + 1))];
        DFace[] faces = new DFace[count + 2];
        for (int f = 0; f < faces.Length; f++)
        {
            faces[f].DispInfo = -1;
        }

        for (int i = 0; i < count; i++)
        {
            faces[i + 1].DispInfo = (short)i;
        }

        DispVert[] verts = new DispVert[count * 25];
        for (int v = 0; v < verts.Length; v++)
        {
            verts[v] = new DispVert { Vector = v % 25 == 1 ? new Vec3(0.6f, 0, 0.8f) : new Vec3(0, 0, 1), Dist = v, Alpha = 0 };
        }

        BspData bsp = new();
        bsp.SetLump(BspLump.DispInfo, MemoryMarshal.AsBytes(infos.AsSpan()).ToArray());
        bsp.SetLump(BspLump.Faces, MemoryMarshal.AsBytes(faces.AsSpan()).ToArray());
        bsp.SetLump(BspLump.DispVerts, MemoryMarshal.AsBytes(verts.AsSpan()).ToArray());
        bsp.SetLump(BspLump.DispTris, new byte[count * 32 * 2]);
        bsp.SetLump(BspLump.DispLightmapSamplePositions, new byte[count * 10]);
        return bsp;
    }

    private delegate void Change(ref DispInfo record);

    private static void Edit(BspData bsp, int index, Change change)
    {
        DispInfo[] infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]).ToArray();
        change(ref infos[index]);
        bsp.SetLump(BspLump.DispInfo, MemoryMarshal.AsBytes(infos.AsSpan()).ToArray());
    }

    private static ArraySegment<byte> Payload(RoomPackSectionData section) => section.Bytes.ToArray();
}
