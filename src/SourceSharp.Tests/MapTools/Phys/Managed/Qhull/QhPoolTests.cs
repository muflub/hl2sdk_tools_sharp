//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Reflection;
using System.Text;
using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed.Qhull;

/// <summary>
/// The qhull storage a <see cref="QhullSession"/> reuses between hulls: a reused object is a
/// fresh one in every field that matters, a build that failed or was abandoned part-way leaves
/// nothing behind, sessions on different threads never meet, and the storage stays bounded.
/// </summary>
public class QhPoolTests
{
    // ---- helpers ----

    /// <summary>A seeded cloud of float-exact points, as a prop's hull would be.</summary>
    private static double[] Cloud(int seed, int points)
    {
        var rng = new Random(seed);
        var xyz = new double[3 * points];
        for (int i = 0; i < xyz.Length; i++)
            xyz[i] = (float)(rng.NextDouble() * 64 - 32);
        return xyz;
    }

    /// <summary>A flat grid: every point on z = 0, which the plain build rejects as singular.</summary>
    private static double[] FlatGrid()
    {
        var xyz = new List<double>();
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
                xyz.AddRange([x, y, 0]);
        return [.. xyz];
    }

    /// <summary>A 4x4x4 lattice: coplanar faces that 'C-0' merges, so the build makes ridges and merges.</summary>
    private static double[] Lattice()
    {
        var xyz = new List<double>();
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
                for (int z = 0; z < 4; z++)
                    xyz.AddRange([x * 3, y * 2, z]);
        return [.. xyz];
    }

    /// <summary>
    /// Points scattered over the faces of a box, each nudged off its face by a hair: nearly
    /// coplanar facets that the premerge ('C-0') merges, so the build makes merge records.
    /// </summary>
    private static double[] NoisyBox(int seed)
    {
        var rng = new Random(seed);
        var xyz = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            int axis = i % 3;
            double side = (i / 3 % 2 == 0) ? -8 : 8;
            double[] p = [rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8];
            p[axis] = side + ((rng.NextDouble() - 0.5) * 1e-9);
            xyz.AddRange(p);
        }
        return [.. xyz];
    }

    /// <summary>Everything a result says, bit-exact: commands, exit codes, and every facet.</summary>
    private static string Dump(QhullResult r)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < r.Commands.Count; k++)
            sb.Append(r.Commands[k]).Append('=').Append(r.ExitCodes[k].ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append("exit ").Append(r.ExitCode.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (QhullFacet f in r.Facets)
        {
            sb.Append(f.Id.ToString(CultureInfo.InvariantCulture)).Append(f.TopOrient ? " t" : " f").Append(f.Simplicial ? "s " : "n ")
              .Append(BitConverter.DoubleToInt64Bits(f.NormalX).ToString("x16", CultureInfo.InvariantCulture)).Append(' ')
              .Append(BitConverter.DoubleToInt64Bits(f.NormalY).ToString("x16", CultureInfo.InvariantCulture)).Append(' ')
              .Append(BitConverter.DoubleToInt64Bits(f.NormalZ).ToString("x16", CultureInfo.InvariantCulture)).Append(' ')
              .Append(BitConverter.DoubleToInt64Bits(f.Offset).ToString("x16", CultureInfo.InvariantCulture)).Append(" :");
            foreach (int id in f.PointIds)
                sb.Append(' ').Append(id.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A varied workload: clouds of several sizes, a cube, and a set that needs the joggle retry.</summary>
    private static List<double[]> Workload()
    {
        var sets = new List<double[]>();
        for (int i = 0; i < 24; i++)
            sets.Add(Cloud(1000 + i, 4 + (i * 7 % 90)));
        sets.Add([0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1]);
        sets.Add(FlatGrid());
        sets.Add(Lattice());
        sets.Add(NoisyBox(5));
        return sets;
    }

    private static string Fresh(double[] xyz) => Dump(QhullBuilder.BuildIvp(xyz));

    /// <summary>Every instance field of an object, by name, for comparing a reused object with a new one.</summary>
    private static Dictionary<string, object?> Fields(object o, params string[] skip)
    {
        var d = new Dictionary<string, object?>();
        for (Type? t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!skip.Contains(f.Name))
                    d[f.Name] = f.GetValue(o);
            }
        }
        return d;
    }

    private static void AssertFresh<T>(T reused, params string[] skip)
        where T : class, new()
    {
        Dictionary<string, object?> fresh = Fields(new T(), skip);
        Dictionary<string, object?> got = Fields(reused, skip);
        Assert.Equal(fresh.Keys.OrderBy(k => k), got.Keys.OrderBy(k => k));
        foreach ((string name, object? value) in fresh)
            Assert.True(Equals(value, got[name]), typeof(T).Name + "." + name + " was not reset: " + got[name]);
    }

    private static void AssertEmptySet<T>(QSet<T> set, int asked)
        where T : class
    {
        Assert.Equal(0, set.n);
        Assert.True(set.e.Length >= Math.Max(asked, 1) + 1, "capacity below the request");
        Assert.All(set.e, x => Assert.Null(x));
    }

    // ---- a reused object is a fresh one ----

    [Fact]
    public void ReusedFacetsVerticesRidgesAndMergesAreIndistinguishableFromNewOnes()
    {
        // The workload (clouds, merged coplanar faces, a joggle retry) uses every kind of object
        // and dirties the pool; then every object it kept is taken again and compared field by
        // field with a new one.
        var session = new QhullSession();
        foreach (double[] xyz in Workload())
            _ = session.BuildIvp(xyz);
        QhPool pool = session.Pool;
        (int facets, int vertices, int ridges, int merges, _, _) = pool.Retained;
        Assert.True(facets > 0 && vertices > 0 && ridges > 0 && merges > 0, "the hull did not use every kind of object: " + pool.Retained);

        pool.Begin();
        for (int i = 0; i < facets; i++)
        {
            // The spare normal is the one field carried over on purpose: qh_setfacetplane writes
            // every coordinate of it before anything reads it.
            AssertFresh(pool.NewFacet(), "spareNormal");
        }
        for (int i = 0; i < vertices; i++)
            AssertFresh(pool.NewVertex());
        for (int i = 0; i < ridges; i++)
            AssertFresh(pool.NewRidge());
        for (int i = 0; i < merges; i++)
            AssertFresh(pool.NewMerge());
    }

    [Fact]
    public void ReusedSetsAreEmptyWithEverySlotNull()
    {
        var session = new QhullSession();
        Assert.Equal(0, session.BuildIvp(Cloud(8, 150)).ExitCode);
        QhPool pool = session.Pool;
        int retainedSets = pool.Retained.Sets;
        Assert.True(retainedSets > 0);

        // Take back every retained set of every kind, at the small sizes most requests are.
        pool.Begin();
        for (int i = 0; i < retainedSets; i++)
        {
            AssertEmptySet(pool.Sets<Facet>().Take(3), 3);
            AssertEmptySet(pool.Sets<Vertex>().Take(0), 0);
            AssertEmptySet(pool.Sets<Ridge>().Take(4), 4);
            AssertEmptySet(pool.Sets<MergeT>().Take(8), 8);
            AssertEmptySet(pool.Sets<double[]>().Take(2), 2);
        }
    }

    [Fact]
    public void ReuseClearsSlotsWrittenPastTheSize()
    {
        // qh_makenew_simplicial fills a neighbor set past its size before truncating it; the
        // slots past the terminator must be null again when the set is reused.
        var set = new QSet<Facet>(3);
        var f = new Facet();
        for (int i = 0; i < set.e.Length; i++)
            set.e[i] = f;
        set.Truncate(1);
        Assert.Same(f, set.e[2]);
        set.Reuse(3, QhPool.SetSlackLimit);
        AssertEmptySet(set, 3);
    }

    [Fact]
    public void ReuseKeepsAnArrayThatFitsAndReplacesOneTooSmallOrFarTooBig()
    {
        var set = new QSet<Vertex>(10);
        Vertex?[] kept = set.e;
        kept[3] = new Vertex();

        // fits (11 slots for a request of 6): kept and cleared
        set.Reuse(6, QhPool.SetSlackLimit);
        Assert.Same(kept, set.e);
        AssertEmptySet(set, 6);

        // too small: replaced by one exactly the fresh size
        set.Reuse(40, QhPool.SetSlackLimit);
        Assert.NotSame(kept, set.e);
        Assert.Equal(41, set.e.Length);
        AssertEmptySet(set, 40);

        // far bigger than asked for and above the slack limit: replaced, so clearing costs no
        // more than a fresh array would
        set.Reuse(QhPool.SetSlackLimit * 4, QhPool.SetSlackLimit);
        Vertex?[] big = set.e;
        set.Reuse(2, QhPool.SetSlackLimit);
        Assert.NotSame(big, set.e);
        Assert.Equal(3, set.e.Length);

        // bigger than asked for but within the slack limit: kept
        set.Reuse(QhPool.SetSlackLimit - 1, QhPool.SetSlackLimit);
        Vertex?[] slack = set.e;
        set.Reuse(1, QhPool.SetSlackLimit);
        Assert.Same(slack, set.e);

        // a zero-size request is a one-slot set, as qh_setnew makes it
        set.Reuse(0, QhPool.SetSlackLimit);
        AssertEmptySet(set, 1);
    }

    [Fact]
    public void SetsOfAnUnportedElementTypeAreRefused()
    {
        var pool = new QhPool();
        Assert.Throws<NotSupportedException>(() => pool.Sets<string>());
    }

    [Fact]
    public void WithoutAPoolASetIsAllocatedFresh()
    {
        QSet<Facet> a = QSet<Facet>.New(5, null);
        QSet<Facet> b = QSet<Facet>.New(5, null);
        Assert.NotSame(a, b);
        AssertEmptySet(a, 5);
    }

    // ---- reuse happens, and is bounded ----

    [Fact]
    public void RebuildingTheSameHullAllocatesNothingNewAndGivesTheSameFacets()
    {
        double[] xyz = Cloud(9, 200);
        var session = new QhullSession();
        string first = Dump(session.BuildIvp(xyz));
        var retained = session.Pool.Retained;
        string second = Dump(session.BuildIvp(xyz));
        Assert.Equal(first, second);
        Assert.Equal(Fresh(xyz), second);
        Assert.Equal(retained, session.Pool.Retained);
    }

    [Fact]
    public void AnObjectIsNeverHandedOutTwiceInOneBuild()
    {
        var pool = new QhPool();
        pool.Begin();
        var facets = new HashSet<Facet>(ReferenceEqualityComparer.Instance);
        var sets = new HashSet<QSet<Vertex>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 50; i++)
        {
            Assert.True(facets.Add(pool.NewFacet()));
            Assert.True(sets.Add(pool.Sets<Vertex>().Take(3)));
        }

        // The next build gets the same objects back, in the same order.
        pool.Begin();
        for (int i = 0; i < 50; i++)
        {
            Assert.Contains(pool.NewFacet(), facets);
            Assert.Contains(pool.Sets<Vertex>().Take(3), sets);
        }
    }

    [Fact]
    public void APoolKeepsNoMoreThanItsRetainLimit()
    {
        var pool = new QhPool();
        pool.Begin();
        int over = QhPool.RetainLimit + 10;
        var facets = new Facet[over];
        for (int i = 0; i < over; i++)
        {
            facets[i] = pool.NewFacet();
            _ = pool.NewVertex();
            _ = pool.NewRidge();
            _ = pool.NewMerge();
            _ = pool.NewPoint(4);
            _ = pool.Sets<Ridge>().Take(3);
        }

        // Beyond the limit the build still got distinct, fresh objects; the pool kept only the limit.
        Assert.Equal(over, facets.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal((QhPool.RetainLimit, QhPool.RetainLimit, QhPool.RetainLimit, QhPool.RetainLimit, QhPool.RetainLimit, QhPool.RetainLimit),
            pool.Retained);

        // The kept ones come back first next time.
        pool.Begin();
        Assert.Same(facets[0], pool.NewFacet());
    }

    [Fact]
    public void PointBuffersAreReusedAtOneLengthAndDroppedWhenTheLengthChanges()
    {
        var pool = new QhPool();
        pool.Begin();
        double[] a = pool.NewPoint(4);
        pool.Begin();
        Assert.Same(a, pool.NewPoint(4));
        double[] b = pool.NewPoint(5);
        Assert.Equal(5, b.Length);
        Assert.Equal(1, pool.Retained.Points);
        pool.Begin();
        Assert.Same(b, pool.NewPoint(5));
    }

    // ---- failed and abandoned builds leave nothing behind ----

    [Fact]
    public void HullsBuiltAfterAFailedHullMatchFreshBuilds()
    {
        // A flat grid fails the plain build with a qhull error (thrown and caught part-way through
        // the build) before the joggle retry succeeds; the plain command alone just fails.
        var session = new QhullSession();
        QhullResult failed = session.Build(FlatGrid(), QhullBuilder.IvpOptions);
        Assert.NotEqual(0, failed.ExitCode);
        foreach (double[] xyz in Workload())
        {
            Assert.Equal(Fresh(xyz), Dump(session.BuildIvp(xyz)));
            Assert.NotEqual(0, session.Build(FlatGrid(), QhullBuilder.IvpOptions).ExitCode);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(60)]
    public void HullsBuiltAfterAnAbandonedHullMatchFreshBuilds(int abortAtFacet)
    {
        // A build abandoned part-way by an exception (a cancelled or crashed cook) leaves its
        // objects half-linked in the pool; the next builds must not see any of it.
        var session = new QhullSession();
        _ = session.BuildIvp(Cloud(11, 150)); // fill the pool first, so the abort dirties reused objects
        session.Pool.FacetTaken = n =>
        {
            if (n == abortAtFacet)
                throw new OperationCanceledException("abandoned at facet " + n);
        };
        Assert.Throws<OperationCanceledException>(() => session.BuildIvp(Cloud(12, 150)));
        session.Pool.FacetTaken = null;

        foreach (double[] xyz in Workload())
            Assert.Equal(Fresh(xyz), Dump(session.BuildIvp(xyz)));
    }

    // ---- sessions on different threads are independent ----

    [Fact]
    public async Task ConcurrentSessionsMatchFreshBuilds()
    {
        List<double[]> sets = Workload();
        string[] expected = [.. sets.Select(Fresh)];
        Task<string[]>[] runs = [.. Enumerable.Range(0, 6).Select(t => Task.Run(() =>
        {
            // each thread its own session, walking the sets in its own order
            var session = new QhullSession();
            var got = new string[sets.Count];
            for (int k = 0; k < sets.Count; k++)
            {
                int i = (k + (t * 5)) % sets.Count;
                got[i] = Dump(session.BuildIvp(sets[i]));
            }
            return got;
        }))];
        foreach (string[] got in await Task.WhenAll(runs))
            Assert.Equal(expected, got);
    }
}
