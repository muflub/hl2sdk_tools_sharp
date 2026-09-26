//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.IO.Compression;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The session calls vbsp makes beyond a single convex, through <see cref="ManagedCollisionSession"/>,
/// against the earlier and later reference collision cooker builds (session.*.txt.gz,
/// cut by the reference dumper's C/Y/M commands): brush models with drag axis areas
/// and an outer hull (ConvertConvexToCollideParams), displacement triangle soups
/// (the -novirtualmesh road), and virtual-mesh packed hulls (PHYSDISP).
/// </summary>
public class ManagedSessionParityTests
{
    /// <summary>What a session job cooks.</summary>
    public enum Kind
    {
        /// <summary>ConvertConvexToCollideParams with drag areas.</summary>
        BrushModel,

        /// <summary>A polysoup.</summary>
        Polysoup,

        /// <summary>A virtual mesh.</summary>
        VirtualMesh,
    }

    private sealed record Job(Kind Kind, Func<ICollisionSession, byte[]?> Cook);

    private static float F(string s) => (float)CookerFixture.ParseC99(s);

    private static List<Job> Jobs(string group = "session")
    {
        var jobs = new List<Job>();
        var pending = new List<(CollisionPlane[] Planes, float Merge)>();
        string path = Path.Combine(CookerFixture.FixtureDirectory(), group + ".in.gz");
        using var r = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0)
            {
                continue;
            }

            switch (t[0])
            {
                case "P":
                {
                    int n = int.Parse(t[1], CultureInfo.InvariantCulture);
                    var planes = new CollisionPlane[n];
                    for (int i = 0; i < n; i++)
                    {
                        string[] v = r.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        planes[i] = new CollisionPlane(new Vec3(F(v[0]), F(v[1]), F(v[2])), F(v[3]));
                    }

                    pending.Add((planes, F(t[2])));
                    break;
                }

                case "C":
                {
                    var convexes = pending.ToArray();
                    pending.Clear();
                    var p = new ConvertConvexParams(t[2] != "0", t[3] != "0", false, F(t[4]));
                    jobs.Add(new Job(Kind.BrushModel, s =>
                    {
                        ConvexHandle[] handles = [.. convexes.Select(c => s.ConvexFromPlanes(c.Planes, c.Merge))];
                        return Write(s, s.ConvertConvexToCollideParams(handles, p));
                    }));
                    break;
                }

                case "Y":
                {
                    int n = int.Parse(t[1], CultureInfo.InvariantCulture);
                    var tris = new (Vec3 A, Vec3 B, Vec3 C, int Material)[n];
                    for (int i = 0; i < n; i++)
                    {
                        string[] v = r.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        tris[i] = (new Vec3(F(v[0]), F(v[1]), F(v[2])), new Vec3(F(v[3]), F(v[4]), F(v[5])),
                            new Vec3(F(v[6]), F(v[7]), F(v[8])), int.Parse(v[9], CultureInfo.InvariantCulture));
                    }

                    jobs.Add(new Job(Kind.Polysoup, s =>
                    {
                        PolysoupHandle soup = s.PolysoupCreate();
                        foreach ((Vec3 a, Vec3 b, Vec3 c, int m) in tris)
                        {
                            s.PolysoupAddTriangle(soup, a, b, c, m);
                        }

                        CollideHandle collide = s.ConvertPolysoupToCollide(soup, false);
                        s.PolysoupDestroy(soup);
                        return Write(s, collide);
                    }));
                    break;
                }

                case "M":
                {
                    int nv = int.Parse(t[1], CultureInfo.InvariantCulture);
                    int nt = int.Parse(t[2], CultureInfo.InvariantCulture);
                    bool hull = t[3] != "0";
                    var verts = new Vec3[nv];
                    for (int i = 0; i < nv; i++)
                    {
                        string[] v = r.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        verts[i] = new Vec3(F(v[0]), F(v[1]), F(v[2]));
                    }

                    var indices = new ushort[nt * 3];
                    for (int i = 0; i < nt; i++)
                    {
                        string[] v = r.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int k = 0; k < 3; k++)
                        {
                            indices[(i * 3) + k] = ushort.Parse(v[k], CultureInfo.InvariantCulture);
                        }
                    }

                    jobs.Add(new Job(Kind.VirtualMesh, s => Write(s, s.CreateVirtualMesh(new VirtualMeshSource(verts, indices), hull))));
                    break;
                }
            }
        }

        return jobs;
    }

    private static byte[]? Write(ICollisionSession s, CollideHandle collide)
    {
        if (collide.IsNull)
        {
            return null;
        }

        byte[] blob = s.CollideWrite(collide);
        s.DestroyCollide(collide);
        return blob;
    }

    private static ManagedCollisionSession Session(bool stock)
    {
        var context = new IvpCookContext(new QhullRunner());
        IIvpBuild build = stock ? new IvpBuild<float, StockPrecision>(context) : new IvpBuild<double, CorrectPrecision>(context);
        return new ManagedCollisionSession(build, new LockedSurfaceProps(new SurfacePropertyTable()));
    }

    private static void AssertExact(Kind kind, bool stock, string group = "session")
    {
        List<Job> jobs = Jobs(group);
        List<CookerFixture.Answer> answers = CookerFixture.Answers(group, stock ? "sdk" : "tf2");
        Assert.Equal(jobs.Count, answers.Count);
        ManagedCollisionSession session = Session(stock);
        int checkedCount = 0;
        var failed = new List<int>();

        if (stock)
        {
            // Stock precision normalises with rsqrtss, so its answers are per vendor.
            int[] mine = [.. Enumerable.Range(0, jobs.Count).Where(i => jobs[i].Kind == kind)];
            string[] ours = [.. mine.Select(i => CookerFixture.Line(jobs[i].Cook(session)))];
            IReadOnlyList<string> expected = VendorGolden.Expected(
                $"session.{group}.{kind}", [.. mine.Select(i => CookerFixture.Line(answers[i]))], ours);
            Assert.Equal(expected.Count, ours.Length);
            checkedCount = ours.Length;
            failed.AddRange(Enumerable.Range(0, ours.Length).Where(k => ours[k] != expected[k]).Select(k => mine[k]));
        }
        else
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Kind != kind)
                {
                    continue;
                }

                checkedCount++;
                if (!CookerFixture.Matches(answers[i], jobs[i].Cook(session)))
                {
                    failed.Add(i);
                }
            }
        }

        Assert.True(checkedCount > 0);
        Assert.True(failed.Count == 0, $"{failed.Count} of {checkedCount} differ: {string.Join(' ', failed.Take(10))}");
    }

    public static TheoryData<Kind> Kinds => [Kind.BrushModel, Kind.Polysoup, Kind.VirtualMesh];

    [ReferenceRsqrtTheory]
    [MemberData(nameof(Kinds))]
    public void StockIsByteExactAgainstTheEarlierReferenceBuild(Kind kind) => AssertExact(kind, stock: true);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Tf2ParityIsByteExactAgainstTf2(Kind kind) => AssertExact(kind, stock: false);

    [Fact]
    public void TheGroupHoldsEveryKind()
    {
        List<Job> jobs = Jobs();
        Assert.Equal(120, jobs.Count(j => j.Kind == Kind.BrushModel));
        Assert.Equal(120, jobs.Count(j => j.Kind == Kind.Polysoup));
        Assert.Equal(40, jobs.Count(j => j.Kind == Kind.VirtualMesh));
    }

    [Fact]
    public void StockDragRaysUseThePointHullAndTwoSidedTriangles()
    {
        // drag.*: two random multi-convex brush models. Job 0 has rays that graze a ledge point
        // lying just outside its triangles' planes (the collision library traces them); job 1 has a flat
        // two-sided triangle ledge (whose two faces bound no edges).
        AssertExact(Kind.BrushModel, stock: true, group: "drag");
    }

    [Fact]
    public void Tf2DragRaysUseThePointHullAndTwoSidedTriangles() => AssertExact(Kind.BrushModel, stock: false, group: "drag");
}
