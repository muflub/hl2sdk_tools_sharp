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
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A <c>.room</c> file is a function of the room: the same VMF and definition
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
/// claim probe holds the first-ranked portal, claimed and unflowed, until the
/// other worker has claimed every other portal: each of those runs then reads
/// a neighbour that has not finished, speculates, and is judged and possibly
/// walked again once it has. That is the busiest schedule the flow can have,
/// and the one furthest from a single thread's.
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
    /// again under the forced speculative schedule writes byte-identical
    /// <c>.room</c> files. Its premise is checked too: the two compiles must
    /// really have done different work, or the fact would pass on a schedule
    /// that never raced.
    /// </summary>
    /// <param name="kind">A sample room kind.</param>
    [Theory]
    [InlineData("corner")]
    [InlineData("end")]
    public async Task ARoomWritesTheSameBytesAtOneThreadAndUnderAForcedSpeculativeSchedule(string kind)
    {
        (VmfDocument vmf, RoomDefinition definition, ContentFileSystem content) = await SampleRoomAsync(kind);
        await using (content)
        {
            RoomObject single = await RoomCompiler.CompileAsync(
                vmf, definition, Context(content, kind, new CompileParallelism { MaxDegree = 1 }));

            int portals = single.Vis.PortalCount;

            // At least two runs to hold back, and all of them inside the
            // tightening's claim window, so the other worker CAN claim them all.
            Assert.InRange(portals, 3, 128);

            using CompilePool pool = new(2);
            int others = 0;
            bool held = false;
            RoomObject forced = await RoomCompiler.CompileAsync(
                vmf,
                definition,
                Context(content, kind, new CompileParallelism { MaxDegree = 2, Pool = pool }),
                rank =>
                {
                    if (rank != 0)
                    {
                        Interlocked.Increment(ref others);
                        return;
                    }

                    // Rank 0 is always the first claim, and it is claimed once:
                    // nothing ranks below it, so it never speculates and is
                    // never walked again.
                    held = SpinWait.SpinUntil(
                        () => Volatile.Read(ref others) >= portals - 1, TimeSpan.FromSeconds(30));
                },
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(held, "the other worker never claimed every other portal while rank 0 was held");
            Assert.NotEqual(single.Vis.Work, forced.Vis.Work);

            // The answer never moved: the rows are the map's.
            Assert.Equal(single.Vis.PvsBytes.ToArray(), forced.Vis.PvsBytes.ToArray());
            Assert.Equal(single.Vis.PasBytes.ToArray(), forced.Vis.PasBytes.ToArray());

            // And neither may the file.
            Assert.Equal(await SaveAsync(single), await SaveAsync(forced));
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
                rank =>
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

    /// <summary>One of the 3x3 sample's rooms: its VMF, its definition, and its materials mounted.</summary>
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

        VmfDocument vmf = await VmfDocument.ParseAsync(files[$"maps/{kind}.vmf"]);
        RoomDefinition definition = RoomDefinitionJson.Parse(
            Encoding.UTF8.GetString(files[$"maps/{kind}.vmf.roomdef.json"]));
        ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        return (vmf, definition, content);
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
