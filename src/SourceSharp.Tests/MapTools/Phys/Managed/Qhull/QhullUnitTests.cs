//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port to C# for a managed
// collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed.Qhull;

/// <summary>Behaviours of the port that do not need the corpus.</summary>
public class QhullUnitTests
{
    [Fact]
    public void RandFromSeedOneIsParkMiller()
    {
        // qh_rand is Park-Miller: 16807 * seed mod 2^31-1 (Schrage); qh_initqhull_start seeds 1
        var qh = new Qh();
        qh.qh_srand(1);
        Assert.Equal(16807, qh.qh_rand());
        Assert.Equal(282475249, qh.qh_rand());
        Assert.Equal(1622650073, qh.qh_rand());
    }

    [Fact]
    public void SrandClampsTheSeed()
    {
        // geom2.c qh_srand: seed < 1 becomes 1
        var qh = new Qh();
        qh.qh_srand(0);
        Assert.Equal(16807, qh.qh_rand());
    }

    [Fact]
    public void CompareAngleSortMatchesGlibcQsortWithTiesAndNaN()
    {
        // merge.c qh_compareangle never returns 0; glibc qsort gave 3 7 8 0 1 2 4 5 6
        // for these angles.
        double[] angles = { 0.5, double.NaN, 0.5, -1, 0.5, double.NaN, 2, -1, 0.25 };
        var merges = new MergeT?[angles.Length + 1];
        for (int i = 0; i < angles.Length; i++)
            merges[i] = new MergeT { angle = angles[i], type = (MergeType)i };
        MergeT?[]? scratch = null;
        GlibcMsort.Sort(merges, angles.Length, new CompareAngle(), ref scratch);
        var order = merges.Take(angles.Length).Select(m => (int)m!.type).ToArray();
        Assert.Equal(new[] { 3, 7, 8, 0, 1, 2, 4, 5, 6 }, order);
    }

    [Fact]
    public void FormatGUsesExponentFormBelowTenToTheMinusFour()
    {
        // C printf "%G": exponent form when X < -4, trailing zeros removed
        Assert.Equal("1E-12", QhullBuilder.FormatG(QhullBuilder.IvpJoggleStart));
        Assert.Equal("2.4E-12", QhullBuilder.FormatG((QhullBuilder.IvpJoggleAdd + QhullBuilder.IvpJoggleStart) * QhullBuilder.IvpJoggleMul));
    }

    [Fact]
    public void FormatGUsesFixedFormFromTenToTheMinusFour()
    {
        // C printf "%G": fixed form with 6 significant digits when -4 <= X < 6
        Assert.Equal("0.000335491", QhullBuilder.FormatG(0.00033549123));
        Assert.Equal("0.0185212", QhullBuilder.FormatG(0.018521234));
        Assert.Equal("123457", QhullBuilder.FormatG(123456.7));
        Assert.Equal("1.23457E+06", QhullBuilder.FormatG(1234567.0));
    }

    [Fact]
    public void GethashIn3dIsTheOtherRidgeVertexId()
    {
        // poly.c qh_gethash case 2: v1 + v2 - skip (vertex ids in this port, pointers in C)
        var v = new[] { new Vertex { id = 9 }, new Vertex { id = 7 }, new Vertex { id = 4 } };
        QSet<Vertex>? s = new QSet<Vertex>(3);
        foreach (var x in v)
            QSet<Vertex>.Append(ref s, x);
        Assert.Equal(4u % 11, Qh.qh_gethash(11, s!, 3, 1, v[1]));
        Assert.Equal(7u % 11, Qh.qh_gethash(11, s!, 3, 1, v[2]));
    }

    [Fact]
    public void MatchVerticesReportsTheSkippedIndexAndParity()
    {
        // poly.c qh_matchvertices: A = {apex, a, b} skip 1, B = {apex2, b, c}: b matches at 1, skipB = 2
        var apex = new Vertex { id = 10 };
        var apex2 = new Vertex { id = 11 };
        var a = new Vertex { id = 5 };
        var b = new Vertex { id = 3 };
        var c = new Vertex { id = 1 };
        QSet<Vertex>? A = new QSet<Vertex>(3), B = new QSet<Vertex>(3);
        foreach (var x in new[] { apex, a, b })
            QSet<Vertex>.Append(ref A, x);
        foreach (var x in new[] { apex2, b, c })
            QSet<Vertex>.Append(ref B, x);
        Assert.True(Qh.qh_matchvertices(1, A!, 1, B!, out int skipB, out bool same));
        Assert.Equal(2, skipB);
        Assert.False(same);
    }

    [Fact]
    public void CubeHasSixMergedFacets()
    {
        // 'C-0' merges the coplanar triangles of each square: 6 non-simplicial facets
        double[] cube = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1 };
        QhullResult r = QhullBuilder.BuildIvp(cube);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(6, r.Facets.Count);
        Assert.All(r.Facets, f => Assert.False(f.Simplicial));
        Assert.All(r.Facets, f => Assert.Equal(4, f.PointIds.Length));
    }

    [Fact]
    public void TooFewPointsFailEveryAttempt()
    {
        // global.c qh_initqhull_globals: fewer than hull_dim+1 points is qh_ERRinput (1);
        // IVP retries until the joggle reaches 0.02f: 1 + 120 attempts
        QhullResult r = QhullBuilder.BuildIvp(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 });
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(121, r.Commands.Count);
        Assert.All(r.ExitCodes, c => Assert.Equal(1, c));
        Assert.Equal("qhull Qs QJ0.0185212 C-0 Pp W1e-14 E1.0e-18", r.Commands[120]);
    }

    [Fact]
    public void FlatInputSucceedsOnTheFirstJoggle()
    {
        // user.c: a planar set is singular (qh_ERRsingular, 2) and the first QJ attempt builds it
        QhullResult r = QhullBuilder.BuildIvp(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 });
        Assert.Equal(new[] { 2, 0 }, r.ExitCodes.ToArray());
        Assert.Equal("qhull Qs QJ1E-12 C-0 Pp W1e-14 E1.0e-18", r.Commands[1]);
    }

    [Fact]
    public void UnportedOptionsAreRejected()
    {
        // only the options IVP passes are ported
        Assert.Throws<NotSupportedException>(() => QhullBuilder.Build(new double[12], "qhull d"));
    }

    [Fact]
    public void ParallelBuildsMatchSerialBuilds()
    {
        // one context per build: no shared state (qh_rand's seed is per context)
        var rng = new Random(3);
        var sets = Enumerable.Range(0, 64).Select(_ => Enumerable.Range(0, 3 * 20).Select(__ => (double)(float)rng.NextDouble()).ToArray()).ToArray();
        string[] serial = sets.Select(Dump).ToArray();
        var parallel = new string[sets.Length];
        System.Threading.Tasks.Parallel.For(0, sets.Length, i => parallel[i] = Dump(sets[i]));
        Assert.Equal(serial, parallel);

        static string Dump(double[] xyz)
        {
            var r = QhullBuilder.BuildIvp(xyz);
            return string.Join(";", r.Facets.Select(f => f.Id + ":" + f.NormalX.ToString("R") + ":" + string.Join(",", f.PointIds)));
        }
    }
}
