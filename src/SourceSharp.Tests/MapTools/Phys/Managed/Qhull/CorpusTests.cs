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

using SourceSharp.MapGen;
using System.IO.Compression;
using System.Text;
using SourceSharp.MapTools.Phys.Managed.Qhull;
using SourceSharp.Tests.MapTools.Rad.Light;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed.Qhull;

/// <summary>
/// The differential gate against the golden corpus: every set's facet sequence, exit codes,
/// facet ids, flags, normals, offsets (bit-exact) and vertex order must equal the recorded
/// reference dump (the reference collision cooker's own qhull-compatible build,
/// with its floating-point grouping and vertex-id hash), both through fresh builds
/// and through one reused <see cref="QhullSession"/>. Goldens are gzipped.
/// </summary>
public class CorpusTests
{
    /// <summary>Where the goldens live in the SourceSharp checkout.</summary>
    private const string FixtureDir = "src/SourceSharp.Tests/Fixtures/p8a-qhull";

    /// <summary>The golden directory under the checkout this test binary was built from.</summary>
    private static string CorpusDir()
    {
        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("no checkout root above " + AppContext.BaseDirectory);
        string f = Path.Combine(root, FixtureDir);
        Assert.True(File.Exists(Path.Combine(f, "p8aq-shapes.pts.gz")), "golden corpus missing at " + f);
        return f;
    }

    private static string ReadGz(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        return r.ReadToEnd();
    }

    private static void AssertCorpus(string name, int expectedSets)
    {
        string dir = CorpusDir();
        List<PointSet> sets;
        using (var r = new StringReader(ReadGz(Path.Combine(dir, name + ".pts.gz"))))
            sets = HullText.ReadSets(r);
        var expected = HullText.SplitBlocks(ReadGz(Path.Combine(dir, name + ".expected.gz")));
        Assert.Equal(expectedSets, sets.Count);
        Assert.Equal(sets.Count, expected.Count);
        var failures = new List<string>();
        var session = new QhullSession();
        for (int i = 0; i < sets.Count; i++)
        {
            var sb = new StringBuilder();
            HullText.WriteResult(sb, sets[i], QhullBuilder.BuildIvp(sets[i].Xyz));
            if (sb.ToString() != expected[i].Text)
                failures.Add(sets[i].Name);
            var sb2 = new StringBuilder();
            HullText.WriteResult(sb2, sets[i], session.BuildIvp(sets[i].Xyz));
            if (sb2.ToString() != expected[i].Text)
                failures.Add(sets[i].Name + "(session)");
        }
        Assert.True(failures.Count == 0, failures.Count + " sets differ from the reference: " + string.Join(" ", failures.Take(10)));
    }

    [Fact]
    public void ShapesMatchTheReference() => AssertCorpus("p8aq-shapes", 370);

    [Fact]
    public void BrushPolytopesMatchTheReference() => AssertCorpus("p8aq-brushes", 450);

    [Fact]
    public void DuplicatedPointsMatchTheReference() => AssertCorpus("p8aq-dups", 480);

    [Fact]
    public void CoplanarGridsMatchTheReference() => AssertCorpus("p8aq-coplanar", 120);

    [Fact]
    public void PointCloudsMatchTheReference() => AssertCorpus("p8aq-clouds", 200);

    [Fact]
    public void DegenerateSetsAndTheJoggleRetryMatchTheReference() => AssertCorpus("p8aq-degenerate", 204);

    [Fact]
    public void LatticeSetsMatchTheReference() => AssertCorpus("p8aq-lattice", 950);

    [Fact]
    public void SearchedRarePathSetsMatchTheReference() => AssertCorpus("p8aq-hard", 27);

    [Fact]
    public void SeededRandomSetsMatchTheReference() => AssertCorpus("p8aq-random", 3000);
}
