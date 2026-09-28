//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// The portal flow's might-see extent, end to end: intersecting only the
/// words that can hold a bit must leave every frame's vector the exact,
/// whole intersection it was before.
/// </summary>
/// <remarks>
/// The extent is kept in blocks of <see cref="BitVector.WordsPerBlock"/>
/// words -- 512 portals -- so a map small enough for the other flow facts
/// has one block and the extent can never exclude anything. This grid has
/// more than 512 memory portals, so frames' extents really do differ, and a
/// frame slab that kept a wider frame's words would show here as a vector
/// with bits the intersection does not have.
/// </remarks>
public class VisPortalFlowExtentTests
{
    private const int Width = 22;
    private const int Height = 8;
    private const float Cell = 64f;

    private static readonly WorkerContext Worker = new(0, 1 << 20, CancellationToken.None);

    [Fact]
    public void EveryFrameSeesExactlyItsParentsVectorAndItsPortalsFlood()
    {
        PortalSet portals = PortalSet.FromPortalFile(Grid());
        Assert.True(portals.Count > BitVector.WordsPerBlock * 64, $"{portals.Count} portals fit in one block");

        VisPortalState state = new(portals.Count);
        VisBaseFlow baseFlow = new(portals, state, useRadius: false, radiusSquared: 0.0);
        VisFloodScratch scratch = new(portals.Count);
        for (int p = 0; p < portals.Count; p++)
        {
            baseFlow.Run(p, scratch, Worker);
        }

        VisPortalFlow flow = new(portals, state, BitVectorPath.Auto);
        List<ulong[]> vectors = [[]];
        List<int> clusters = [-1];
        List<(int Lo, int Hi)> extents = [(0, 0)];
        int frames = 0;
        int narrowed = 0;
        int moved = 0;
        int basePortal = 0;

        flow.FrameEntered = (depth, cluster, _, _, mightSee) =>
        {
            frames++;
            ulong[] expected;
            if (depth == 1)
            {
                expected = state.Flood(basePortal).ToArray();
            }
            else
            {
                // Some portal of the parent's cluster that leads here and was
                // in the parent's vector: this frame's vector is the parent's
                // AND that portal's flood, word for word, all of it.
                ulong[] parent = vectors[depth - 1];
                expected = [];
                foreach (int p in portals.ClusterPortals(clusters[depth - 1]))
                {
                    if (portals.Leaf(p) != cluster || !BitVectorOps.GetBit(parent, p))
                    {
                        continue;
                    }

                    ulong[] candidate = And(parent, state.Flood(p));
                    if (candidate.AsSpan().SequenceEqual(mightSee))
                    {
                        expected = candidate;
                        break;
                    }
                }
            }

            Assert.Equal(expected, mightSee.ToArray());

            (int Lo, int Hi) extent = VisFrameStack.Extent(mightSee);
            narrowed += extent != (0, mightSee.Length) ? 1 : 0;
            while (vectors.Count <= depth)
            {
                vectors.Add([]);
                clusters.Add(-1);
                extents.Add((0, 0));
            }

            (int Lo, int Hi) before = extents[depth];
            moved += before.Lo < before.Hi && (before.Lo < extent.Lo || before.Hi > extent.Hi) ? 1 : 0;
            vectors[depth] = mightSee.ToArray();
            clusters[depth] = cluster;
            extents[depth] = extent;
        };

        for (basePortal = 0; basePortal < portals.Count; basePortal += 7)
        {
            flow.Run(basePortal, Worker);
        }

        // The path under test was actually taken: frames narrower than the
        // vector, and frames at one depth whose extent left words a previous
        // frame there had written.
        Assert.True(frames > 1000, $"{frames} frames");
        Assert.True(narrowed > 100, $"{narrowed} narrowed frames");
        Assert.True(moved > 10, $"{moved} frames moved their extent");
    }

    private static ulong[] And(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        ulong[] result = new ulong[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = a[i] & b[i];
        }

        return result;
    }

    /// <summary>
    /// A grid of 64-unit rooms, every shared wall with one window at an
    /// irregular offset, as the tightening facts use -- wider, so it has more
    /// than one block of portals.
    /// </summary>
    private static PortalFile Grid()
    {
        List<FilePortal> list = [];
        for (int j = 0; j < Height; j++)
        {
            for (int i = 0; i + 1 < Width; i++)
            {
                (float from, float to) = Window(i, j, 0);
                list.Add(VisFixture.WindowAtX(
                    (j * Width) + i, (j * Width) + i + 1, (i + 1) * Cell, (j * Cell) + from, (j * Cell) + to));
            }
        }

        for (int j = 0; j + 1 < Height; j++)
        {
            for (int i = 0; i < Width; i++)
            {
                (float from, float to) = Window(i, j, 1);
                list.Add(VisFixture.WindowAtY(
                    (j * Width) + i, ((j + 1) * Width) + i, (j + 1) * Cell, (i * Cell) + from, (i * Cell) + to));
            }
        }

        return VisFixture.Portals(Width * Height, [.. list]);
    }

    private static (float From, float To) Window(int i, int j, int axis)
    {
        int seed = (i * 7) + (j * 13) + (axis * 5);
        float from = 4f + ((seed * 11) % 10 * 3.6f);
        float width = 16f + ((seed * 3) % 7 * 4f);
        return (from, Math.Min(from + width, Cell - 4f));
    }
}
