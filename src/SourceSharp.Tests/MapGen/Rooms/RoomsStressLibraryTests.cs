//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapGen.Rooms;

/// <summary>
/// The stress library generator: many distinct rooms on the 3x3 sample's
/// kit, each still a valid room of a library.
/// </summary>
public sealed class RoomsStressLibraryTests
{
    /// <summary>
    /// Every room of the largest library is its own map: no two rooms of a
    /// kind share their features and entities, and every name is unique and
    /// starts with the kind.
    /// </summary>
    [Fact]
    public void EveryRoomIsDistinct()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> shapes = new(StringComparer.Ordinal);
        for (int i = 0; i < RoomsStressLibrary.MaxCount; i++)
        {
            RoomKind room = RoomsStressLibrary.Room(i);
            RoomKind kind = Rooms3x3Kit.Kinds[i % Rooms3x3Kit.Kinds.Count];
            Assert.True(names.Add(room.Name), room.Name);
            Assert.StartsWith(kind.Name + "_", room.Name, StringComparison.Ordinal);
            Assert.Null(RoomNames.Problem(room.Name));
            Assert.Equal(kind.Sockets, room.Sockets);

            string shape = kind.Name + ":"
                + string.Join(";", room.Features.Select(f => $"{f.Box} {f.Material} {f.Role}"))
                + "|" + string.Join(";", room.Entities.Select(e => $"{e.ClassName} {e.Origin} {string.Join(",", e.Keys)}"));
            Assert.True(shapes.Add(shape), $"{room.Name} repeats another room of its kind");
        }

        Assert.Equal(432 * 5, RoomsStressLibrary.MaxCount);
    }

    /// <summary>
    /// Everything a variant adds stands in an inside corner square, clear of
    /// every doorway's strip and the centre column; the lights and the
    /// player start stand in the centre column, above the floor.
    /// </summary>
    [Fact]
    public void EveryAdditionStaysInTheCornerSquares()
    {
        static bool InSquare(float lo, float hi) => (lo >= 32 && hi <= 80) || (lo >= 176 && hi <= 224);

        for (int i = 0; i < RoomsStressLibrary.MaxCount; i++)
        {
            RoomKind room = RoomsStressLibrary.Room(i);
            foreach (KitBrush brush in room.Features)
            {
                Assert.True(InSquare(brush.Box.Mins.X, brush.Box.Maxs.X), $"{room.Name}: {brush.Box}");
                Assert.True(InSquare(brush.Box.Mins.Y, brush.Box.Maxs.Y), $"{room.Name}: {brush.Box}");
                Assert.Equal(Rooms3x3Kit.Wall, brush.Box.Mins.Z);
                Assert.True(brush.Box.Maxs.Z <= Rooms3x3Kit.CellSize - Rooms3x3Kit.Wall, $"{room.Name}: {brush.Box}");
            }

            // No two corner blocks share a corner.
            Assert.Equal(room.Features.Count, room.Features.Select(f => (f.Box.Mins.X, f.Box.Mins.Y)).Distinct().Count());
            Assert.All(room.Entities, e => Assert.Equal((128f, 128f), (e.Origin.X, e.Origin.Y)));
            Assert.All(room.Entities, e => Assert.InRange(e.Origin.Z, Rooms3x3Kit.Wall, Rooms3x3Kit.CellSize - Rooms3x3Kit.Wall));
            Assert.Equal(i % 5 == 4 ? 1 : 0, room.Entities.Count(e => e.ClassName == "info_player_start"));
        }
    }

    /// <summary>
    /// The library splits, with the room pipeline's own reader, into exactly
    /// the rooms asked for, in order, each at its grid corner with the kind's
    /// sockets.
    /// </summary>
    [Fact]
    public async Task TheLibrarySplitsIntoItsRooms()
    {
        const int count = 70; // three library rows, every kind many times
        VmfDocument library = await VmfDocument.ParseAsync(RoomsStressLibrary.Build(count)[Rooms3x3Kit.LibraryFile]);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);

        Assert.Equal(count, rooms.Count);
        for (int i = 0; i < count; i++)
        {
            RoomKind kind = RoomsStressLibrary.Room(i);
            Assert.Equal(kind.Name, rooms[i].Definition.Name);
            Assert.Equal(Rooms3x3Kit.CellSize, rooms[i].Definition.CellSize);
            Assert.Equal(new SocketKit(Rooms3x3Kit.DoorWidth, Rooms3x3Kit.DoorHeight, Rooms3x3Kit.Wall), rooms[i].Definition.Kit);
            Assert.Equal(
                kind.Sockets.Select(s => (RoomFacing)(int)s).Order(),
                rooms[i].Definition.Sockets.Select(s => s.Facing).Order());
            SourceSharp.MapGen.Point corner = RoomsStressLibrary.Corner(i);
            Assert.Equal((corner.X, corner.Y, corner.Z), (rooms[i].Corner.X, rooms[i].Corner.Y, rooms[i].Corner.Z));
        }
    }

    /// <summary>
    /// The default library's grid stays inside the engine's coordinate range
    /// (a line of 1024 cells would not), and the corners step by one cell and
    /// the library gap.
    /// </summary>
    [Fact]
    public void TheDefaultLibraryFitsTheCoordinateRange()
    {
        SourceSharp.MapGen.Point last = RoomsStressLibrary.Corner(RoomsStressLibrary.DefaultCount - 1);
        Assert.True(last.X + Rooms3x3Kit.CellSize <= 16384 && last.Y + Rooms3x3Kit.CellSize <= 16384, last.ToString());
        Assert.Equal(new SourceSharp.MapGen.Point(384, 0, 0), RoomsStressLibrary.Corner(1));
        Assert.Equal(new SourceSharp.MapGen.Point(0, 384, 0), RoomsStressLibrary.Corner(RoomsStressLibrary.Columns));
    }

    /// <summary>
    /// Rooms from across the variant space compile under the room rules: every
    /// kind, the plainest variant, and the fullest one (the widest, tallest
    /// feature, three extra blocks, three lights).
    /// </summary>
    [Fact]
    public async Task VariedRoomsCompileUnderTheRoomRules()
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, string vmt) in Rooms3x3Kit.Materials())
        {
            disk.AddText(path, vmt);
        }

        await using ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        // Variant 431 is the last of the mixed radix: corner 3, width 48,
        // height 224, three extras, three lights.
        int[] variants = [0, 431, 204];
        List<int> indices = [.. variants.SelectMany(v => Enumerable.Range(0, 5).Select(k => (5 * v) + k))];
        string vmf = RoomsStressLibrary.LibraryVmf(indices.Max() + 1);
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(await VmfDocument.ParseAsync(Encoding.UTF8.GetBytes(vmf)));

        foreach (int index in indices)
        {
            LibraryRoom room = rooms[index];
            VbspContext context = new(VbspOptions.Default, content) { MapBase = room.Definition.Name, CollisionCooker = cooker };
            RoomObject compiled = await RoomCompiler.CompileAsync(room.Document, room.Definition, context);
            Assert.True(compiled.ClusterCount > 0, room.Definition.Name);
            Assert.Equal(RoomsStressLibrary.Room(index).Sockets.Count, compiled.Definition.Sockets.Count);
        }
    }

    /// <summary>
    /// The build is deterministic and holds the sample's game folder: its
    /// gameinfo, its materials and the library.
    /// </summary>
    [Fact]
    public void TheBuildIsDeterministic()
    {
        IReadOnlyDictionary<string, byte[]> first = RoomsStressLibrary.Build(12);
        IReadOnlyDictionary<string, byte[]> second = RoomsStressLibrary.Build(12);
        Assert.Equal(first.Keys, second.Keys);
        Assert.All(first, f => Assert.True(f.Value.AsSpan().SequenceEqual(second[f.Key]), f.Key));
        Assert.Equal(2 + Rooms3x3Kit.Materials().Count, first.Count);
        Assert.Equal(Rooms3x3Kit.GameInfo, Encoding.UTF8.GetString(first["gameinfo.txt"]));
        Assert.DoesNotContain((byte)'\r', first[Rooms3x3Kit.LibraryFile]);
        Assert.Equal(12, Encoding.UTF8.GetString(first[Rooms3x3Kit.LibraryFile]).Split("\"info_room\"").Length - 1);
    }

    /// <summary>Room indices and library sizes outside the variant space are refused.</summary>
    [Fact]
    public void OutOfRangeArgumentsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomsStressLibrary.Room(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomsStressLibrary.Room(RoomsStressLibrary.MaxCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomsStressLibrary.Name(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomsStressLibrary.LibraryVmf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomsStressLibrary.LibraryVmf(RoomsStressLibrary.MaxCount + 1));
        Assert.Equal("cross_000", RoomsStressLibrary.Name(0));
        Assert.Equal("hall_204", RoomsStressLibrary.Name(1024 - 1));
    }
}
