//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room container is a function of the room: the same VMF and definition
/// write the same bytes whatever the machine's schedule did, at any
/// <c>-threads</c>, run after run.
/// </summary>
/// <remarks>
/// <para>
/// The room pipeline's cache promise ("same keys, same room bytes") and any
/// content-addressed store of room files rest on this. It was broken: a
/// 1024-room library compiled twice differed in about 800 files, and the
/// 3x3 sample's rooms differed between two runs at <c>-threads 1</c>. The
/// PVS and PAS rows and every BSP lump were identical; the differing bytes
/// were the vvis work counters and the deepest-flow statistic, which the
/// container persisted. Under the tightened flow at more than one worker
/// those are properties of the SCHEDULE -- a run that reads a neighbour
/// still being flowed is walked again, and the chains it walks depend on how
/// far that neighbour had got -- while the rows are a property of the map
/// (see <see cref="VisTightening"/>). And <c>-threads 1</c> did not reach
/// vvis at all: the room compiler gave it a default context, every core.
/// </para>
/// <para>
/// The facts force the schedule instead of hoping for one. The tightening's
/// claim and settle probes let one run through at a time and hold one
/// portal, claimed and unflowed, until every other portal's run has settled:
/// each of those that reads it read a neighbour that had not finished,
/// speculated, and is judged, and walked again if the read hid something,
/// once it has. With nothing running beside anything else until the held
/// portal is released, it is the same schedule on every run.
/// </para>
/// <para>
/// The rooms are the 3x3 sample's, built from the generator's bytes on an
/// in-memory disk: <c>corner</c> and <c>end</c> have sixteen memory portals
/// and a flow deep enough for the schedule to show in the work, and they are
/// the rooms the sample compile was seen to vary on.
/// </para>
/// </remarks>
public sealed class RoomReproducibilityTests
{
    /// <summary>
    /// The red-first fact for the bug: one room compiled at one thread and
    /// again under a forced speculative schedule writes byte-identical
    /// containers, though the two compiles did different work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The schedule, and why it is the same on every run.</b> Two workers,
    /// and a baton: every run takes it at its claim and gives it back at its
    /// settlement, so runs happen one at a time, lowest rank first -- except
    /// the portal at <paramref name="held"/>, whose first claim takes no baton
    /// and is held until every other portal's first run has settled. Each of
    /// those runs that reads the held portal therefore reads it empty, every
    /// time; nothing runs beside anything else until the held portal is
    /// released, so which reads were speculative, what they recorded, and
    /// which runs its final vector proves inexact are fixed by the map.
    /// </para>
    /// <para>
    /// <b>Why these ranks.</b> Holding rank 0 -- what this fact did first --
    /// does not work: rank 0 has the fewest candidates, and its final
    /// <c>portalvis</c> adds nothing the other walks would use, so every read
    /// of it empty is judged exact and nothing is walked again. The only work
    /// it changed was one chain: the idle second worker takes a split-off
    /// frame of rank 0's walk, and re-entering that frame counts it again.
    /// Whether the worker had gone idle by then was a race, lost about half
    /// the time on a loaded machine, and then the forced compile did exactly
    /// the one-thread work and the premise failed. Rank 7 of <c>corner</c>
    /// and of <c>end</c> is a portal whose empty vector DOES hide what later
    /// portals would have pruned with, so runs are judged inexact and walked
    /// again -- a difference of more than ten chains and candidates, not one.
    /// </para>
    /// <para>
    /// <b>The premises</b>, so the fact cannot pass on a schedule that never
    /// raced: the hold happened; at least one run read a neighbour that had
    /// not finished (the settle probe's flag, not an inference from the
    /// counters); at least one run was judged inexact and claimed again; and
    /// the work differs from the one-thread compile's -- the difference
    /// container version 2 wrote into the file.
    /// </para>
    /// </remarks>
    /// <param name="kind">A sample room kind.</param>
    /// <param name="held">The rank whose claim is held.</param>
    [Theory]
    [InlineData("corner", 7)]
    [InlineData("end", 7)]
    public async Task ARoomWritesTheSameBytesAtOneThreadAndUnderAForcedSpeculativeSchedule(string kind, int held)
    {
        (VmfDocument vmf, RoomDefinition definition, ContentFileSystem content) = await SampleRoomAsync(kind);
        await using (content)
        {
            RoomObject single = await RoomCompiler.CompileAsync(
                vmf, definition, Context(content, kind, new CompileParallelism { MaxDegree = 1 }));

            int portals = single.Vis.PortalCount;

            // The held rank has portals ranked above it to speculate, and all
            // of them are inside the tightening's claim window.
            Assert.InRange(portals, held + 2, 128);

            TimeSpan patience = TimeSpan.FromSeconds(30);
            using CompilePool pool = new(2);
            using SemaphoreSlim baton = new(1);
            int heldClaims = 0;
            int heldSettles = 0;
            int othersSettled = 0;
            int speculated = 0;
            bool heldInTime = false;
            bool batonInTime = true;
            ConcurrentDictionary<int, int> claims = new();
            RoomObject forced = await RoomCompiler.CompileAsync(
                vmf,
                definition,
                Context(content, kind, new CompileParallelism { MaxDegree = 2, Pool = pool }),
                tighteningClaimProbe: rank =>
                {
                    claims.AddOrUpdate(rank, 1, (_, count) => count + 1);
                    if (rank == held && Interlocked.Increment(ref heldClaims) == 1)
                    {
                        // Only this worker is outside the baton; the other
                        // flows every other portal, one at a time.
                        heldInTime = SpinWait.SpinUntil(
                            () => Volatile.Read(ref othersSettled) >= portals - 1, patience);
                        return;
                    }

                    if (!baton.Wait(patience))
                    {
                        batonInTime = false;
                    }
                },
                tighteningSettleProbe: (rank, speculative) =>
                {
                    if (speculative)
                    {
                        Interlocked.Increment(ref speculated);
                    }

                    if (rank == held && Interlocked.Increment(ref heldSettles) == 1)
                    {
                        // The held run never took the baton. It is settled
                        // once: every rank below it was done when it flowed,
                        // so it cannot speculate and is never walked again.
                        return;
                    }

                    Interlocked.Increment(ref othersSettled);
                    baton.Release();
                },
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(heldInTime, $"rank {held} was released before every other portal had settled");
            Assert.True(batonInTime, "a run waited too long for the baton");
            Assert.True(speculated > 0, "no run read a neighbour that had not finished");
            Assert.True(
                claims.Any(c => c.Key != held && c.Value > 1),
                $"no run was judged inexact and walked again (claims {string.Join(",", claims.OrderBy(c => c.Key))})");
            Assert.NotEqual(single.Vis.Work, forced.Vis.Work);

            // The answer never moved: the rows are the map's.
            Assert.Equal(single.Vis.PvsBytes.ToArray(), forced.Vis.PvsBytes.ToArray());
            Assert.Equal(single.Vis.PasBytes.ToArray(), forced.Vis.PasBytes.ToArray());

            // And neither may the file.
            Assert.Equal(await SaveAsync(single), await SaveAsync(forced));
        }
    }

    /// <summary>
    /// The settle probe's flag, on the schedule where it must be false: at one
    /// thread every neighbour a run reads has finished before it starts, so
    /// every portal settles exactly once and none of them speculated.
    /// </summary>
    [Fact]
    public async Task AtOneThreadEveryRunSettlesOnceWithoutSpeculating()
    {
        (VmfDocument vmf, RoomDefinition definition, ContentFileSystem content) = await SampleRoomAsync("corner");
        await using (content)
        {
            System.Collections.Concurrent.ConcurrentBag<(int Rank, bool Speculated)> settled = [];
            RoomObject room = await RoomCompiler.CompileAsync(
                vmf,
                definition,
                Context(content, "corner", new CompileParallelism { MaxDegree = 1 }),
                tighteningClaimProbe: null,
                tighteningSettleProbe: (rank, speculative) => settled.Add((rank, speculative)),
                CancellationToken.None);

            Assert.Equal(Enumerable.Range(0, room.Vis.PortalCount), settled.Select(s => s.Rank).Order());
            Assert.DoesNotContain(settled, s => s.Speculated);
        }
    }

    /// <summary>
    /// The room's vvis runs on the compile's parallelism: at one thread no
    /// second portal is claimed while the first is still held. It used to run
    /// on a default context -- every core -- whatever <c>-threads</c> said.
    /// </summary>
    [Fact]
    public async Task TheVisHalfOfARoomRunsOnTheCompilesParallelism()
    {
        (VmfDocument vmf, RoomDefinition definition, ContentFileSystem content) = await SampleRoomAsync("corner");
        await using (content)
        {
            int holding = 0;
            int concurrent = 0;
            int claims = 0;
            await RoomCompiler.CompileAsync(
                vmf,
                definition,
                Context(content, "corner", new CompileParallelism { MaxDegree = 1 }),
                tighteningClaimProbe: rank =>
                {
                    Interlocked.Increment(ref claims);
                    if (Volatile.Read(ref holding) != 0)
                    {
                        Interlocked.Increment(ref concurrent);
                    }

                    if (rank != 0)
                    {
                        return;
                    }

                    // Hold the first claim long enough for any other worker to
                    // claim beside it: the flow starts every worker it has at
                    // once, so a second one would be there within the wait.
                    Volatile.Write(ref holding, 1);
                    _ = SpinWait.SpinUntil(() => Volatile.Read(ref concurrent) > 0, TimeSpan.FromSeconds(2));
                    Volatile.Write(ref holding, 0);
                },
                tighteningSettleProbe: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(claims > 1, "the fixture room must have more than one portal to claim");
            Assert.Equal(0, concurrent);
        }
    }

    /// <summary>
    /// The same room compiled several times at the default parallelism -- the
    /// schedule the machine picks, unforced -- writes one set of bytes.
    /// </summary>
    [Fact]
    public async Task ARoomWritesTheSameBytesRunAfterRunAtTheDefaultParallelism()
    {
        (VmfDocument vmf, RoomDefinition definition, ContentFileSystem content) = await SampleRoomAsync("end");
        await using (content)
        {
            VbspContext context = Context(content, "end", CompileParallelism.Default);
            byte[] first = await SaveAsync(await RoomCompiler.CompileAsync(vmf, definition, context));
            for (int run = 0; run < 3; run++)
            {
                byte[] again = await SaveAsync(await RoomCompiler.CompileAsync(vmf, definition, context));
                Assert.Equal(first, again);
            }
        }
    }

    /// <summary>One of the 3x3 sample library's rooms: its VMF, its definition, and its materials mounted.</summary>
    private static async Task<(VmfDocument Vmf, RoomDefinition Definition, ContentFileSystem Content)> SampleRoomAsync(
        string kind)
    {
        IReadOnlyDictionary<string, byte[]> files = Rooms3x3Sample.Build();
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in files)
        {
            if (path.StartsWith("materials/", StringComparison.Ordinal))
            {
                disk.AddFile(path, bytes);
            }
        }

        // The room as `ssmap room` compiles it: split out of the library VMF.
        VmfDocument library = await VmfDocument.ParseAsync(files[Rooms3x3Kit.LibraryFile]);
        LibraryRoom room = RoomLibraryVmf.Split(library).Single(r => r.Definition.Name == kind);
        ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        return (room.Document, room.Definition, content);
    }

    private static VbspContext Context(ContentFileSystem content, string mapBase, CompileParallelism parallelism) =>
        new(VbspOptions.Default, content) { MapBase = mapBase, Parallelism = parallelism };

    private static async Task<byte[]> SaveAsync(RoomObject room)
    {
        using MemoryStream file = new();
        await RoomObjectStore.SaveAsync(room, file);
        return file.ToArray();
    }
}
