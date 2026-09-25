// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port (Claude, lane p8a)
// to C# for a managed collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>
/// Storage reused across the builds of one <see cref="QhullSession"/> (the role qhull's
/// mem.c freelists play across qh_new_qhull calls). Objects are handed out in order and
/// never reused within a build, so a build sees exactly what fresh allocation would give;
/// <see cref="Begin"/> makes the previous build's objects available again, which is safe
/// because nothing outlives a build except the copied-out <see cref="QhullResult"/>.
/// </summary>
internal sealed class QhPool
{
    private readonly List<Facet> facets = new();
    private readonly List<Vertex> vertices = new();
    private readonly List<Ridge> ridges = new();
    private readonly List<MergeT> merges = new();
    private readonly List<double[]> points = new();
    private int facetCur, vertexCur, ridgeCur, mergeCur, pointCur;
    private int pointLen = -1;

    /// <summary>qsort's temporary storage for merges, kept between builds.</summary>
    internal MergeT?[]? SortMergeScratch;

    /// <summary>qsort's temporary storage for vertices, kept between builds.</summary>
    internal Vertex?[]? SortVertexScratch;

    /// <summary>Starts a build: everything handed out before may be reused.</summary>
    internal void Begin()
    {
        facetCur = vertexCur = ridgeCur = mergeCur = pointCur = 0;
    }

    /// <summary>A zeroed facet (qh_memalloc + memset); keeps its spare normal and neighbor set.</summary>
    internal Facet NewFacet()
    {
        if (facetCur < facets.Count)
        {
            Facet f = facets[facetCur++];
            f.Reset();
            return f;
        }
        var nf = new Facet();
        facets.Add(nf);
        facetCur++;
        return nf;
    }

    /// <summary>A zeroed vertex.</summary>
    internal Vertex NewVertex()
    {
        if (vertexCur < vertices.Count)
        {
            Vertex v = vertices[vertexCur++];
            v.Reset();
            return v;
        }
        var nv = new Vertex();
        vertices.Add(nv);
        vertexCur++;
        return nv;
    }

    /// <summary>A zeroed ridge.</summary>
    internal Ridge NewRidge()
    {
        if (ridgeCur < ridges.Count)
        {
            Ridge r = ridges[ridgeCur++];
            r.Reset();
            return r;
        }
        var nr = new Ridge();
        ridges.Add(nr);
        ridgeCur++;
        return nr;
    }

    /// <summary>A merge record.</summary>
    internal MergeT NewMerge()
    {
        if (mergeCur < merges.Count)
        {
            MergeT m = merges[mergeCur++];
            m.angle = 0;
            m.facet1 = null!;
            m.facet2 = null!;
            m.type = MergeType.MRGnone;
            return m;
        }
        var nm = new MergeT();
        merges.Add(nm);
        mergeCur++;
        return nm;
    }

    /// <summary>A point-sized array of length <paramref name="len"/> (contents unspecified).</summary>
    internal double[] NewPoint(int len)
    {
        if (len != pointLen)
        {
            points.Clear();
            pointCur = 0;
            pointLen = len;
        }
        if (pointCur < points.Count)
            return points[pointCur++];
        var p = new double[len];
        points.Add(p);
        pointCur++;
        return p;
    }
}

/// <summary>
/// A reusable hull builder: the same results as <see cref="QhullBuilder"/>, with the
/// facets, vertices, ridges, merges and point buffers of earlier builds reused. Not
/// thread-safe: use one session per thread.
/// </summary>
internal sealed class QhullSession
{
    private readonly QhPool pool = new();

    /// <summary>As <see cref="QhullBuilder.Build"/>, reusing this session's storage.</summary>
    /// <param name="xyz">Point coordinates, three per point.</param>
    /// <param name="options">A qhull command starting with "qhull ".</param>
    /// <returns>The exit code and, on success, the facets.</returns>
    public QhullResult Build(ReadOnlySpan<double> xyz, string options) => QhullBuilder.BuildCore(xyz, options, pool);

    /// <summary>As <see cref="QhullBuilder.BuildIvp"/>, reusing this session's storage.</summary>
    /// <param name="xyz">Point coordinates, three per point.</param>
    /// <returns>Every attempt's command and exit code, and the facets of the successful attempt.</returns>
    public QhullResult BuildIvp(ReadOnlySpan<double> xyz) => QhullBuilder.BuildIvpCore(xyz, pool);
}
