//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port (Claude, lane p8a)
// to C# for a managed collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

using System.Globalization;
using System.Text;

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>One facet of a hull built by <see cref="QhullBuilder"/>, in qhull's facet_list order.</summary>
/// <remarks>
/// A value, not an object, and its point ids a slice of one array shared by the whole result:
/// a result is what a build hands its caller to keep, so it cannot come from the build's reused
/// storage the way the facets being built do, and as a class with an array each it was one
/// allocation per facet plus one per facet's ids, for every hull a cook builds. As a struct
/// over a shared id array a result is three allocations whatever its size.
/// </remarks>
internal readonly struct QhullFacet
{
    private readonly int[] ids;
    private readonly int idStart;
    private readonly int idCount;

    internal QhullFacet(uint id, bool topOrient, bool simplicial, double nx, double ny, double nz, double offset, int[] ids, int idStart, int idCount)
    {
        Id = id;
        TopOrient = topOrient;
        Simplicial = simplicial;
        NormalX = nx;
        NormalY = ny;
        NormalZ = nz;
        Offset = offset;
        this.ids = ids;
        this.idStart = idStart;
        this.idCount = idCount;
    }

    /// <summary>qhull's facet id (facet->id).</summary>
    public uint Id { get; }

    /// <summary>facet->toporient.</summary>
    public bool TopOrient { get; }

    /// <summary>facet->simplicial.</summary>
    public bool Simplicial { get; }

    /// <summary>facet->normal[0] (outward unit normal).</summary>
    public double NormalX { get; }

    /// <summary>facet->normal[1].</summary>
    public double NormalY { get; }

    /// <summary>facet->normal[2].</summary>
    public double NormalZ { get; }

    /// <summary>facet->offset: the plane is normal . p + offset = 0.</summary>
    public double Offset { get; }

    /// <summary>
    /// Input point indices of the facet's vertices in qh_facet3vertex order
    /// (qh_pointid of each vertex's point).
    /// </summary>
    public ReadOnlySpan<int> PointIds => new(ids, idStart, idCount);
}

/// <summary>The result of one qh_new_qhull call, or of IVP's retry loop.</summary>
internal sealed class QhullResult
{
    internal QhullResult(int exitCode, QhullFacet[] facets, string[] commands, int[] exitCodes)
    {
        ExitCode = exitCode;
        Facets = facets;
        Commands = commands;
        ExitCodes = exitCodes;
    }

    /// <summary>
    /// qhull's exit code of the successful (or last) attempt: 0 success, 1 input error,
    /// 2 singular input, 3 precision error, 5 internal error.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>The hull's facets in facet_list order; empty unless <see cref="ExitCode"/> is 0.</summary>
    public IReadOnlyList<QhullFacet> Facets { get; }

    /// <summary>The qhull command of every attempt, in order.</summary>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>The exit code of every attempt, in order.</summary>
    public IReadOnlyList<int> ExitCodes { get; }
}

/// <summary>
/// Managed port of the 3-d subset of Qhull 2.6 that IVP's convex-hull builder
/// (IVP_SurfaceBuilder_Pointsoup) uses. Deterministic and thread-safe: every call
/// builds with its own context.
/// </summary>
internal static class QhullBuilder
{
    /// <summary>IVP's first attempt: "qhull Qs Pp C-0 W1e-14 E1.0e-6".</summary>
    public const string IvpOptions = "qhull Qs Pp C-0 W1e-14 E1.0e-6";

    /// <summary>IVP's joggle retry command; {0} is the joggle printed with C's "%G".</summary>
    public const string IvpRetryFormat = "qhull Qs QJ{0} C-0 Pp W1e-14 E1.0e-18";

    /// <summary>IVP's first joggle (1e-12f widened to double).</summary>
    public const double IvpJoggleStart = 9.999999960041972e-13;

    /// <summary>Added to the joggle before each growth step (1e-12f).</summary>
    public const double IvpJoggleAdd = 9.999999960041972e-13;

    /// <summary>Joggle growth factor (1.2f).</summary>
    public const double IvpJoggleMul = 1.2000000476837158;

    /// <summary>IVP gives up once the joggle reaches this (0.02f).</summary>
    public const double IvpJoggleMax = 0.019999999552965164;

    /// <summary>
    /// Builds the 3-d convex hull of <paramref name="xyz"/> (x, y, z per point) exactly as
    /// qh_new_qhull (3, n, xyz, False, <paramref name="options"/>, NULL, NULL) does.
    /// </summary>
    /// <param name="xyz">Point coordinates, three per point.</param>
    /// <param name="options">A qhull command starting with "qhull "; the port supports Qs, QJn, C-n, En, Wn and Pp.</param>
    /// <returns>The exit code and, on success, the facets.</returns>
    public static QhullResult Build(ReadOnlySpan<double> xyz, string options) => BuildCore(xyz, options, null);

    /// <summary><see cref="Build"/> with optional reused storage (<see cref="QhullSession"/>).</summary>
    internal static QhullResult BuildCore(ReadOnlySpan<double> xyz, string options, QhPool? pool)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (xyz.Length % 3 != 0)
            throw new ArgumentException("xyz must hold three coordinates per point", nameof(xyz));
        var facets = RunOnce(xyz, options, pool, out int exitcode);
        return new QhullResult(exitcode, facets, [options], [exitcode]);
    }

    /// <summary>
    /// IVP's full sequence: <see cref="IvpOptions"/>, then on failure
    /// <see cref="IvpRetryFormat"/> with a growing joggle until a build succeeds or the joggle
    /// reaches <see cref="IvpJoggleMax"/>.
    /// </summary>
    /// <param name="xyz">Point coordinates, three per point (metres, as IVP passes them).</param>
    /// <returns>Every attempt's command and exit code, and the facets of the successful attempt.</returns>
    public static QhullResult BuildIvp(ReadOnlySpan<double> xyz) => BuildIvpCore(xyz, null);

    /// <summary><see cref="BuildIvp"/> with optional reused storage (<see cref="QhullSession"/>).</summary>
    internal static QhullResult BuildIvpCore(ReadOnlySpan<double> xyz, QhPool? pool)
    {
        if (xyz.Length % 3 != 0)
            throw new ArgumentException("xyz must hold three coordinates per point", nameof(xyz));
        var commands = new List<string>();
        var codes = new List<int>();
        string cmd = IvpOptions;
        QhullFacet[] facets = RunOnce(xyz, cmd, pool, out int exitcode);
        commands.Add(cmd);
        codes.Add(exitcode);
        if (exitcode != 0)
        {
            double joggle = IvpJoggleStart;
            while (true)
            {
                cmd = string.Format(CultureInfo.InvariantCulture, IvpRetryFormat, FormatG(joggle));
                facets = RunOnce(xyz, cmd, pool, out exitcode);
                commands.Add(cmd);
                codes.Add(exitcode);
                if (exitcode == 0)
                    break;
                joggle = (IvpJoggleAdd + joggle) * IvpJoggleMul;
                if (IvpJoggleMax <= joggle)
                    break;
            }
        }
        return new QhullResult(exitcode, facets, commands.ToArray(), codes.ToArray());
    }

    private static QhullFacet[] RunOnce(ReadOnlySpan<double> xyz, string options, QhPool? pool, out int exitcode)
    {
        // The context itself is not pooled: it is one object per build (under 2% of what a
        // build allocated before the pool took its facets, vertices, ridges, merges and sets),
        // and it has well over a hundred fields that qh_initqhull_start relies on starting at
        // zero. Resetting them by hand would be the one place a missed field could carry state
        // from one hull into the next, which is the failure pooling must never have.
        var qh = new Qh();
        if (pool != null)
        {
            pool.Begin();
            qh.pool = pool;
            qh.sortMergeScratch = pool.SortMergeScratch;
            qh.sortVertexScratch = pool.SortVertexScratch;
        }
        int n = xyz.Length / 3;
        exitcode = qh.NewQhull(3, n, xyz, options);
        if (pool != null)
        {
            pool.SortMergeScratch = qh.sortMergeScratch;
            pool.SortVertexScratch = qh.sortVertexScratch;
        }
        if (exitcode != 0)
            return [];
        int count = 0;
        for (Facet? facet = qh.facet_list; facet != null && facet.next != null; facet = facet.next)
            count++;
        var facets = new QhullFacet[count];
        // Every facet of a 3-d hull has at least three vertices; a merged facet has more, and
        // the id array grows (doubling) when the hull has many of those.
        int[] ids = new int[3 * count];
        int used = 0;
        int k = 0;
        for (Facet? facet = qh.facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            QSet<Vertex> vertices;
            try
            {
                vertices = qh.qh_facet3vertex(facet);
            }
            catch (QhullExit ex)
            {
                // In C, qh_errexit after qh_new_qhull returned calls exit(1).
                throw new InvalidOperationException("qh_facet3vertex failed with qhull exit " + ex.ExitCode, ex);
            }
            int nv = vertices.n;
            if (used + nv > ids.Length)
                Array.Resize(ref ids, Math.Max(2 * ids.Length, used + nv));
            for (int i = 0; i < nv; i++)
                ids[used + i] = qh.qh_pointid(vertices.e[i]!.point);
            double[] normal = facet.normal!;
            facets[k++] = new QhullFacet(facet.id, facet.toporient, facet.simplicial,
                normal[0], normal[1], normal[2], facet.offset, ids, used, nv);
            used += nv;
        }
        return facets;
    }

    /// <summary>C printf "%G" (precision 6, no '#' flag), as IVP formats the joggle.</summary>
    public static string FormatG(double value)
    {
        if (double.IsNaN(value))
            return "NAN";
        if (double.IsInfinity(value))
            return value > 0 ? "INF" : "-INF";
        const int P = 6;
        if (value == 0)
            return double.IsNegative(value) ? "-0" : "0";
        // %E with precision P-1 decides the exponent X
        string e = value.ToString("E" + (P - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int epos = e.IndexOf('E');
        int x = int.Parse(e.AsSpan(epos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string body;
        if (P > x && x >= -4)
        {
            body = value.ToString("F" + (P - 1 - x).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            body = StripZeros(body);
        }
        else
        {
            string mant = StripZeros(e.Substring(0, epos));
            var sb = new StringBuilder(mant);
            sb.Append('E');
            sb.Append(x < 0 ? '-' : '+');
            int ax = Math.Abs(x);
            if (ax < 10)
                sb.Append('0');
            sb.Append(ax.ToString(CultureInfo.InvariantCulture));
            body = sb.ToString();
        }
        return body;
    }

    private static string StripZeros(string s)
    {
        if (s.IndexOf('.') < 0)
            return s;
        s = s.TrimEnd('0');
        if (s.EndsWith('.'))
            s = s.Substring(0, s.Length - 1);
        return s;
    }
}
