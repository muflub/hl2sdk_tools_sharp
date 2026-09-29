//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Compile;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room compiler, the library compile and build, and the level linker
/// hand their host back on a fresh thread-pool stack, not on the worker their
/// run finished on (see <see cref="HostHandoffTests"/> for why), whether the
/// run succeeds or fails.
/// </summary>
public sealed class RoomHostHandoffTests
{
    [Fact]
    public Task ARoomCompileAndALinkHandTheHostBackOffTheirOwnStacks()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            RoomDefinition hub = RoomHarness.Hub();
            VbspContext context = await RoomHarness.ContextAsync(degree: 2);

            RoomObject room = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
            HostStack.AssertOffCompileStack();

            RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
            library.Add(room);
            _ = await LevelLinker.LinkAsync(LevelLinkerScaleTests.HubGrid(library, 2), library, context);
            HostStack.AssertOffCompileStack();
        });
    }

    [Fact]
    public Task ALibraryCompileHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
            ContentFileSystem content = await RoomCacheHarness.ContentAsync();
            int delivered = 0;

            await RoomLibraryCompiler.CompileAsync(
                rooms,
                new RoomLibraryCompileSettings(VbspOptions.Default, content) { Parallelism = new CompileParallelism { MaxDegree = 2 } },
                (_, _) =>
                {
                    Interlocked.Increment(ref delivered);
                    return ValueTask.CompletedTask;
                });

            HostStack.AssertOffCompileStack();
            Assert.Equal(2, delivered);
        });
    }

    [Fact]
    public Task ALibraryBuildHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
            ContentFileSystem content = await RoomCacheHarness.ContentAsync();
            int delivered = 0;

            await RoomLibraryBuild.BuildAsync(
                rooms,
                new RoomLibraryCompileSettings(VbspOptions.Default, content) { Parallelism = new CompileParallelism { MaxDegree = 2 } },
                new RoomNavPackOptions(),
                null,
                (_, _) =>
                {
                    Interlocked.Increment(ref delivered);
                    return ValueTask.CompletedTask;
                });

            HostStack.AssertOffCompileStack();
            Assert.Equal(2, delivered);
        });
    }

    [Fact]
    public Task AFailedLibraryBuildHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            // The host's own callback fails the build on the last room, from one
            // of the build's workers: the failure reaches the host's catch on a
            // fresh stack all the same.
            IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(RoomCacheHarness.Library(2));
            ContentFileSystem content = await RoomCacheHarness.ContentAsync();
            InvalidDataException planted = new("planted failure");

            Exception? thrown = null;
            try
            {
                await RoomLibraryBuild.BuildAsync(
                    rooms,
                    new RoomLibraryCompileSettings(VbspOptions.Default, content) { Parallelism = new CompileParallelism { MaxDegree = 2 } },
                    new RoomNavPackOptions(),
                    null,
                    (outcome, _) => outcome.Index == 1 ? throw planted : ValueTask.CompletedTask);
            }
            catch (InvalidDataException ex)
            {
                thrown = ex;
                HostStack.AssertOffCompileStack();
            }

            Assert.Same(planted, thrown);
        });
    }
}
