//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;
using SourceSharp.MapTools.Cache.Sqlite;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Io;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap room -incremental</c> on the 3x3 sample, against the SQLite
/// store on a real disk: after no change, an edited room, an added room, a
/// removed room and a changed option, the pack is byte for byte the pack a
/// clean run writes, and so is every map <c>ssmap link</c> makes from it;
/// only the rooms that changed compile. A cancelled run leaves no pack and
/// no rows, concurrent runs on one store agree, and a store that fails is a
/// line in the log, never a failed compile.
/// </summary>
public sealed class RoomIncrementalCommandsTests
{
    private const string LibraryPath = "/sample/rooms.vmf";

    // ---- byte identity with a clean compile ----------------------------------

    /// <summary>
    /// The five edits the incremental run must survive, in the order an
    /// author makes them, each checked against a clean compile of the same
    /// library: the store is primed without one room; the room is added
    /// (only it compiles); nothing changes (nothing compiles); one room is
    /// edited (only it compiles); another is removed (nothing compiles);
    /// an option changes (every room compiles). The maps and navigation
    /// <c>ssmap link</c> writes from each pack agree too.
    /// </summary>
    [Fact]
    public async Task EveryIncrementalPackIsTheCleanPack()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Sample();
        VmfDocument full = await LibraryAsync(fs);
        string added = Rooms3x3Kit.Kinds[^1].Name;
        string removed = Rooms3x3Kit.Kinds[1].Name;
        string edited = Rooms3x3Kit.Kinds[2].Name;
        int count = Rooms3x3Kit.Kinds.Count;

        await SetLibraryAsync(fs, WithoutRoom(full, added));
        await AssertSameAsClean(fs, tree, [], $"{count - 1} compiled, 0 reused", link: false);

        await SetLibraryAsync(fs, full);
        await AssertSameAsClean(fs, tree, [], $"1 compiled, {count - 1} reused", link: true);

        await AssertSameAsClean(fs, tree, [], $"0 compiled, {count} reused", link: true);

        await SetLibraryAsync(fs, WithTarget(full, edited));
        await AssertSameAsClean(fs, tree, [], $"1 compiled, {count - 1} reused", link: true);
        Assert.Contains($"ssmap room: compiled {edited} ", LastLog(fs), StringComparison.Ordinal);

        await SetLibraryAsync(fs, WithoutRoom(WithTarget(full, edited), removed));
        await AssertSameAsClean(fs, tree, [], $"0 compiled, {count - 1} reused", link: false);

        await SetLibraryAsync(fs, full);
        await AssertSameAsClean(fs, tree, ["-nav-codec", "deflate"], $"{count} compiled, 0 reused", link: true);
        await AssertSameAsClean(fs, tree, ["-micro", "2"], $"{count} compiled, 0 reused", link: false);
        await AssertSameAsClean(fs, tree, ["-nav-codec", "deflate"], $"0 compiled, {count} reused", link: false);
    }

    /// <summary>
    /// A library-only setting (the entity reserve) rewrites the pack's
    /// settings section and reuses every room: the pack is still the clean
    /// one.
    /// </summary>
    [Fact]
    public async Task ALibraryOnlySettingReusesEveryRoom()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Sample();
        int count = Rooms3x3Kit.Kinds.Count;
        await AssertSameAsClean(fs, tree, [], $"{count} compiled, 0 reused", link: false);
        VmfDocument library = await LibraryAsync(fs);
        library.GetChunk("world")!.AddKey(RoomLibraryOptions.EntityReserveKey, "300");
        await SetLibraryAsync(fs, library);
        await AssertSameAsClean(fs, tree, [], $"0 compiled, {count} reused", link: false);
    }

    // ---- the flags ------------------------------------------------------------

    /// <summary>
    /// Without <c>-incremental</c> no store is opened and the log is the one
    /// the verb always wrote; <c>-nocache</c> turns <c>-incremental</c> off
    /// again, so every room compiles and nothing is opened.
    /// </summary>
    [Fact]
    public async Task TheCacheIsOffUnlessAskedAndNoCacheTurnsItOff()
    {
        InMemoryFileSystem fs = Sample();
        int opened = 0;
        Task<ICacheStore?> Open(string path, CancellationToken token)
        {
            opened++;
            return Task.FromResult<ICacheStore?>(null);
        }

        using StringWriter plain = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample"], plain, Open));
        Assert.DoesNotContain(" reused", plain.ToString(), StringComparison.Ordinal);

        using StringWriter off = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-nocache"], off, Open));
        Assert.Contains($"ssmap room: {Rooms3x3Kit.Kinds.Count} compiled, 0 reused", off.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, opened);
    }

    /// <summary>
    /// The store goes beside the library as <c>&lt;library&gt;.sscache.db</c>,
    /// or into <c>-cache-dir</c>; and the default opener makes the SQLite
    /// store there, which the next run reuses.
    /// </summary>
    [Fact]
    public async Task TheStoreGoesBesideTheLibraryOrIntoCacheDir()
    {
        InMemoryFileSystem fs = Sample();
        List<string> paths = [];
        Task<ICacheStore?> Open(string path, CancellationToken token)
        {
            paths.Add(path);
            return Task.FromResult<ICacheStore?>(null);
        }

        using StringWriter output = new();
        await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental"], output, Open);
        await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-cache-dir", "/caches"], output, Open);
        Assert.Equal([Path.GetFullPath("/sample/rooms.sscache.db"), Path.GetFullPath("/caches/rooms.sscache.db")], paths);

        using TempTree tree = new();
        using StringWriter first = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-cache-dir", tree.Root], first));
        Assert.True(File.Exists(Path.Combine(tree.Root, "rooms.sscache.db")), first.ToString());
        using StringWriter second = new();
        await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-cache-dir", tree.Root], second);
        Assert.Contains($"ssmap room: 0 compiled, {Rooms3x3Kit.Kinds.Count} reused", second.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A store that will not open is said once, and the run compiles every room into the clean pack.</summary>
    [Fact]
    public async Task AStoreThatWillNotOpenIsSaidAndEveryRoomCompiles()
    {
        InMemoryFileSystem fs = Sample();
        using StringWriter output = new();
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(
                fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-out", "/inc.roompack"], output,
                (_, _) => Task.FromResult<ICacheStore?>(null)));
        Assert.Contains("ssmap room: cache: -incremental opened no store (", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("every room compiles this run", output.ToString(), StringComparison.Ordinal);
        await CleanAsync(fs, []);
        Assert.Equal(Bytes(fs, "/clean.roompack"), Bytes(fs, "/inc.roompack"));
    }

    /// <summary>A commit the store fails, or a collection that fails, is a line in the log; the pack stands and the run succeeds.</summary>
    [Fact]
    public async Task AStoreFailureAfterThePackIsALine()
    {
        InMemoryFileSystem fs = Sample();
        InMemoryCacheStore inner = await RoomCacheHarness.StoreAsync();

        using StringWriter commit = new();
        HookedCacheStore failing = new(inner) { BeforeCommit = _ => throw new IOException("disk full") };
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(
                fs, [], [LibraryPath, "-game", "/sample", "-incremental"], commit, (_, _) => Task.FromResult<ICacheStore?>(failing)));
        Assert.Contains("ssmap room: cache: commit failed (disk full); this run's rooms were not stored", commit.ToString(), StringComparison.Ordinal);
        Assert.NotNull(fs.GetBytes(VPath.Create(Rooted("/sample/rooms.roompack"))));
        Assert.Empty(await inner.KeysAsync(CancellationToken.None));

        using StringWriter gc = new();
        HookedCacheStore stats = new(inner) { FailStats = new InvalidOperationException("no stats") };
        Assert.Equal(
            Program.ExitSuccess,
            await RoomCommands.RunRoomAsync(
                fs, [], [LibraryPath, "-game", "/sample", "-incremental"], gc, (_, _) => Task.FromResult<ICacheStore?>(stats)));
        Assert.Contains("ssmap room: cache: gc failed (no stats); the store was left as it was", gc.ToString(), StringComparison.Ordinal);
        Assert.Equal(Rooms3x3Kit.Kinds.Count, (await inner.KeysAsync(CancellationToken.None)).Count);
    }

    // ---- cancellation and concurrency ------------------------------------------

    /// <summary>
    /// A run cancelled as its first room is reported writes no pack and
    /// leaves no row in the store; the next run compiles every room into the
    /// clean pack.
    /// </summary>
    [Fact]
    public async Task ACancelledRunLeavesNoPackAndNoRows()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Sample();
        using CancellationTokenSource cancel = new();
        using CancellingWriter output = new("ssmap room: compiled", cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoomCommands.RunRoomAsync(
            fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-out", "/inc.roompack"], output, Opener(tree), cancel.Token));
        Assert.Null(fs.GetBytes(VPath.Create(Rooted("/inc.roompack"))));
        await using (SqliteCacheStore store = new())
        {
            await store.OpenAsync(Path.Combine(tree.Root, "rooms.sscache.db"), CancellationToken.None);
            Assert.Empty(await store.KeysAsync(CancellationToken.None));
        }

        await AssertSameAsClean(fs, tree, [], $"{Rooms3x3Kit.Kinds.Count} compiled, 0 reused", link: false);
    }

    /// <summary>
    /// Two runs at once on one store file, each with its own store object as
    /// two processes would have, both write the clean pack; the store they
    /// leave serves every room to the next run.
    /// </summary>
    [Fact]
    public async Task ConcurrentRunsOnOneStoreAgree()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Sample();
        await CleanAsync(fs, []);
        using StringWriter a = new();
        using StringWriter b = new();
        int[] exits = await Task.WhenAll(
            RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-out", "/a.roompack"], a, Opener(tree)),
            RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-out", "/b.roompack"], b, Opener(tree)));
        Assert.True(exits.All(e => e == Program.ExitSuccess), a + "\n" + b);
        Assert.DoesNotContain("cache:", a + b.ToString(), StringComparison.Ordinal);
        Assert.Equal(Bytes(fs, "/clean.roompack"), Bytes(fs, "/a.roompack"));
        Assert.Equal(Bytes(fs, "/clean.roompack"), Bytes(fs, "/b.roompack"));
        await AssertSameAsClean(fs, tree, [], $"0 compiled, {Rooms3x3Kit.Kinds.Count} reused", link: false);
    }

    // ---- the section table --------------------------------------------------------

    /// <summary>
    /// <c>ssmap rooms -rooms</c> prints the pack's section table, the same
    /// for an incremental pack as for the clean one; a pack that is not
    /// there, or is not a pack, fails with its name.
    /// </summary>
    [Fact]
    public async Task TheSectionTableIsTheSameForAReusedPack()
    {
        using TempTree tree = new();
        InMemoryFileSystem fs = Sample();
        await AssertSameAsClean(fs, tree, [], $"{Rooms3x3Kit.Kinds.Count} compiled, 0 reused", link: false);
        await AssertSameAsClean(fs, tree, [], $"0 compiled, {Rooms3x3Kit.Kinds.Count} reused", link: false);

        using StringWriter clean = new();
        using StringWriter reused = new();
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["-rooms", "/clean.roompack"], clean));
        Assert.Equal(Program.ExitSuccess, await RoomCommands.RunRoomsAsync(fs, ["-rooms", "/inc.roompack"], reused));
        Assert.Equal(clean.ToString(), reused.ToString());
        string[] lines = clean.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("owner tag offset length codec decoded revision sha256", lines[0]);
        Assert.StartsWith("(library) CMPL ", lines[1], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith($"{Rooms3x3Kit.Kinds[0].Name} ECNT ", StringComparison.Ordinal) && l.Contains(" none ", StringComparison.Ordinal));

        using StringWriter missing = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomsAsync(fs, ["-rooms", "/none.roompack"], missing));
        fs.AddFile(Rooted("/junk.roompack"), Encoding.ASCII.GetBytes("not a pack at all, not at all"));
        using StringWriter junk = new();
        Assert.Equal(RoomCommands.ExitFailed, await RoomCommands.RunRoomsAsync(fs, ["-rooms", "/junk.roompack"], junk));
        Assert.Contains("junk.roompack", junk.ToString(), StringComparison.Ordinal);
        using StringWriter bad = new();
        Assert.Equal(Program.ExitUsage, await RoomCommands.RunRoomsAsync(fs, ["-rooms", "bad\0path"], bad));
    }

    // ---- helpers ----------------------------------------------------------------

    /// <summary>
    /// Runs a clean compile and an incremental one of the current library
    /// with the extra arguments, holds the incremental run's summary line to
    /// <paramref name="summary"/> and its pack to the clean pack, byte for
    /// byte; with <paramref name="link"/>, links the sample's level from
    /// each pack and holds the maps and navigation to each other too.
    /// </summary>
    private static async Task AssertSameAsClean(InMemoryFileSystem fs, TempTree tree, string[] extra, string summary, bool link)
    {
        await CleanAsync(fs, extra);
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(
            fs, [], [LibraryPath, "-game", "/sample", "-incremental", "-out", "/inc.roompack", .. extra], output, Opener(tree));
        Assert.True(exit == Program.ExitSuccess, output.ToString());
        Assert.Contains("ssmap room: " + summary + "\n", output.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.True(Bytes(fs, "/clean.roompack").AsSpan().SequenceEqual(Bytes(fs, "/inc.roompack")), $"{summary}: the packs differ");
        fs.AddFile(Rooted("/last.log"), Encoding.UTF8.GetBytes(output.ToString()));

        if (link)
        {
            foreach (string pack in (string[])["clean", "inc"])
            {
                using StringWriter linkOutput = new();
                exit = await RoomCommands.RunLinkAsync(
                    fs, ["/sample/levels/rooms3x3.yaml", "-rooms", $"/{pack}.roompack", "-out", $"/out/{pack}.bsp"], linkOutput);
                Assert.True(exit == Program.ExitSuccess, linkOutput.ToString());
            }

            Assert.Equal(Bytes(fs, "/out/clean.bsp"), Bytes(fs, "/out/inc.bsp"));
            Assert.Equal(Bytes(fs, "/out/clean.nav3d"), Bytes(fs, "/out/inc.nav3d"));
        }
    }

    /// <summary>The log of the last incremental run <see cref="AssertSameAsClean"/> made.</summary>
    private static string LastLog(InMemoryFileSystem fs) => Encoding.UTF8.GetString(Bytes(fs, "/last.log"));

    private static async Task CleanAsync(InMemoryFileSystem fs, string[] extra)
    {
        using StringWriter output = new();
        int exit = await RoomCommands.RunRoomAsync(fs, [], [LibraryPath, "-game", "/sample", "-out", "/clean.roompack", .. extra], output);
        Assert.True(exit == Program.ExitSuccess, output.ToString());
    }

    /// <summary>A fresh SQLite store object over the tree's one file, as a separate process would open it.</summary>
    private static Func<string, CancellationToken, Task<ICacheStore?>> Opener(TempTree tree) => async (_, token) =>
    {
        SqliteCacheStore store = new();
        await store.OpenAsync(Path.Combine(tree.Root, "rooms.sscache.db"), token);
        return store;
    };

    private static async Task<VmfDocument> LibraryAsync(InMemoryFileSystem fs) =>
        await VmfDocument.ParseAsync(Bytes(fs, LibraryPath));

    private static Task SetLibraryAsync(InMemoryFileSystem fs, VmfDocument library)
    {
        fs.AddFile(Rooted(LibraryPath), library.ToBytes());
        return Task.CompletedTask;
    }

    /// <summary>The library with an <c>info_target</c> added inside a room's cell: an edit of that room alone.</summary>
    private static VmfDocument WithTarget(VmfDocument library, string room)
    {
        VmfDocument copy = Copy(library);
        (Vec3 corner, float cell) = Cell(copy, room);
        VmfChunk target = new("entity");
        target.AddKey("id", "990001");
        target.AddKey("classname", "info_target");
        target.AddKey("targetname", "edited");
        target.AddKey("origin", VmfPlacement.Format(corner + new Vec3(cell / 2f, cell / 2f, 64f)));
        copy.Chunks.Add(target);
        return copy;
    }

    /// <summary>The library without one room: its marker, its brushes and its entities.</summary>
    private static VmfDocument WithoutRoom(VmfDocument library, string room)
    {
        VmfDocument copy = Copy(library);
        (Vec3 corner, float cell) = Cell(copy, room);
        bool Inside(Vec3 p) =>
            p.X >= corner.X - 1 && p.Y >= corner.Y - 1 && p.Z >= corner.Z - 1
            && p.X <= corner.X + cell + 1 && p.Y <= corner.Y + cell + 1 && p.Z <= corner.Z + cell + 1;
        VmfChunk world = copy.GetChunk("world")!;
        foreach (VmfChunk solid in world.GetChunks("solid").ToList())
        {
            if (Inside(VmfPlacement.Bounds(solid).Mins) && Inside(VmfPlacement.Bounds(solid).Maxs))
            {
                world.Children.Remove(solid);
            }
        }

        foreach (VmfChunk entity in copy.GetChunks("entity").ToList())
        {
            bool owned = entity.GetChunks("solid").Any()
                ? entity.GetChunks("solid").All(s => Inside(VmfPlacement.Bounds(s).Mins))
                : VmfPlacement.Origin(entity) is { } origin && Inside(origin);
            if (owned)
            {
                copy.Chunks.Remove(entity);
            }
        }

        Assert.DoesNotContain(RoomLibraryVmf.Split(copy), r => r.Definition.Name == room);
        return copy;
    }

    private static (Vec3 Corner, float Cell) Cell(VmfDocument library, string room)
    {
        VmfChunk marker = library.GetChunks("entity").Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity && e.GetValue("name") == room);
        return (VmfPlacement.Origin(marker)!.Value, float.Parse(marker.GetValue("cell_size")!, CultureInfo.InvariantCulture));
    }

    private static VmfDocument Copy(VmfDocument library)
    {
        VmfDocument copy = new();
        foreach (VmfChunk chunk in library.Chunks)
        {
            copy.Chunks.Add(VmfPlacement.Clone(chunk));
        }

        return copy;
    }

    private static byte[] Bytes(InMemoryFileSystem fs, string path) =>
        fs.GetBytes(VPath.Create(Rooted(path))) ?? throw new InvalidOperationException($"{path} was not written");

    private static InMemoryFileSystem Sample()
    {
        InMemoryFileSystem fs = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            fs.AddFile(Rooted("/sample/" + path), bytes);
        }

        return fs;
    }

    private static string Rooted(string path) => VPath.Create(Path.GetFullPath(path)).Value;

    /// <summary>A log that cancels the run when it is handed a line starting with a given text.</summary>
    private sealed class CancellingWriter(string trigger, CancellationTokenSource cancel) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            if (value?.StartsWith(trigger, StringComparison.Ordinal) == true)
            {
                cancel.Cancel();
            }

            return base.WriteLineAsync(value);
        }
    }
}
