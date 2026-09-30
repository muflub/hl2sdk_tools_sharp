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

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.RoomDisplacementHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Displacements through the link (section 4.5 of the rooms design): every
/// placed room's displacements are carried, their starts moved and their
/// vectors turned with the room at every quarter turn, their runs, base
/// faces and neighbours rebased, their collision the room's, and the
/// level's surfaces agree with the flattened level's vbsp compile.
/// </summary>
public sealed class LevelLinkerDisplacementTests(ITestOutputHelper output)
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A level of a hub with two neighbouring patches beside another room
    /// with one, at every quarter turn: the linked map holds the three in
    /// link order, each record's runs, face and neighbours its own, and it
    /// agrees with the flattened level's compile on everything that does not
    /// depend on where a compile put a displacement (power, flags,
    /// neighbours, allowed vertices, distances, alphas, tags, sample
    /// positions, material and lightmap size, collision hull size), bit for
    /// bit; the start positions bit for bit; the surfaces' vertices within a
    /// thousandth of a unit and their normals within a hundred-thousandth;
    /// and <c>ssmap check</c> finds no error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsItsRoomsDisplacementsAtEveryRotation(int rotation)
    {
        VmfDocument library = Library(Patches);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        DispInfo[] infos = Infos(linked.Bsp);
        Assert.Equal(3, infos.Length);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.Equal(Infos(flat).Select(d => d.StartPosition), infos.Select(d => d.StartPosition));

        float vertexGap = VertexGap(linked.Bsp, flat);
        float normalGap = NormalGap(linked.Bsp, flat);
        output.WriteLine($"turn {rotation}: vertex gap {vertexGap:G9}, normal gap {normalGap:G9}");
        Assert.True(vertexGap < 1e-3f, $"vertices {vertexGap} apart");
        Assert.True(normalGap < 1e-5f, $"normals {normalGap} apart");

        // Every record names its own face, which names it back.
        DFace[] faces = BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray();
        for (int i = 0; i < infos.Length; i++)
        {
            Assert.Equal(i, faces[infos[i].MapFace].DispInfo);
        }

        // The collision hulls are each room's own (a placement's hull is its
        // room's bytes, which name vertices by index); the flattened compile
        // cooks them anew and cuts the same convex hull into its own
        // triangles where the move rounds differently, so Observed compares
        // their corners.
        IReadOnlyList<byte[]?> hulls = RoomDisplacements.CollisionBlobs(linked.Bsp);
        Assert.Equal(
            [.. RoomDisplacements.CollisionBlobs(rooms.Get("hub").Bsp), .. RoomDisplacements.CollisionBlobs(rooms.Get("other").Bsp)],
            hulls);

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }
}
