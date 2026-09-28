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

using static SourceSharp.MapTools.Phys.Managed.Qhull.QhConst;

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>geom.c. Every expression keeps C's left-to-right evaluation order; no FMA.</summary>
internal sealed partial class Qh
{
    /// <summary>
    /// fabs_ macro. Codegen fact: vphysics.so (GCC 10.3 -ffast-math) emits it as andpd, which
    /// clears the sign of -0.0 (the source's ((a) &lt; 0) ? -(a) : (a) keeps it); e.g. aa5ac
    /// qh_gausselim, ac2dc qh_setfacetplane, 87fef qh_maxsimplex.
    /// </summary>
    internal static double fabs_(double a) => Math.Abs(a);

    /// <summary>fmax_ macro: (a) &lt; (b) ? (b) : (a)</summary>
    internal static double fmax_(double a, double b) => a < b ? b : a;

    /// <summary>fmin_ macro: (a) &gt; (b) ? (b) : (a)</summary>
    internal static double fmin_(double a, double b) => a > b ? b : a;

    /// <summary>qh_backnormal</summary>
    internal void qh_backnormal(double[]?[] rows, int numrow, int numcol, bool sign,
        double[] normal, ref bool nearzero)
    {
        int zerocol = -1;
        int normalp = numcol - 1;
        normal[normalp--] = sign ? -1.0 : 1.0;
        for (int i = numrow; i-- > 0;)
        {
            double[] rowi = rows[i]!;
            normal[normalp] = 0.0;
            int ai = i + 1;
            int ak = normalp + 1;
            for (int j = i + 1; j < numcol; j++)
                normal[normalp] -= rowi[ai++] * normal[ak++];
            double diagonal = rowi[i];
            if (fabs_(diagonal) > MINdenom_2)
                normal[normalp--] /= diagonal;
            else
            {
                bool waszero = false;
                normal[normalp] = qh_divzero(normal[normalp], diagonal, MINdenom_1_2, ref waszero);
                if (waszero)
                {
                    zerocol = i;
                    normal[normalp--] = sign ? -1.0 : 1.0;
                    for (int normal_tail = normalp + 2; normal_tail < numcol; normal_tail++)
                        normal[normal_tail] = 0.0;
                }
                else
                    normalp--;
            }
        }
        if (zerocol != -1)
        {
            nearzero = true;
            qh_precision("zero diagonal on back substitution");
        }
    }

    /// <summary>qh_distplane</summary>
    internal void qh_distplane(double[] point, Facet facet, out double dist)
    {
        double[] normal = facet.normal!;
        switch (hull_dim)
        {
            case 2:
                dist = facet.offset + point[0] * normal[0] + point[1] * normal[1];
                break;
            case 3:
                // reference order: (x*nx + y*ny) + (offset + z*nz)
                dist = (point[0] * normal[0] + point[1] * normal[1]) + (facet.offset + point[2] * normal[2]);
                break;
            case 4:
                dist = facet.offset + point[0] * normal[0] + point[1] * normal[1] + point[2] * normal[2] + point[3] * normal[3];
                break;
            default:
                dist = facet.offset;
                for (int k = 0; k < hull_dim; k++)
                    dist += point[k] * normal[k];
                break;
        }
        // RANDOMdist ('Rn') and tracing are not ported
    }

    /// <summary>qh_findbest</summary>
    internal Facet? qh_findbest(double[] point, Facet startfacet,
        bool bestoutside, bool newfacets, bool noupper,
        out double dist, bool wantisoutside, ref bool isoutside, out int numpart)
    {
        double bestdist = -REALmax / 2, searchdist;
        double cutoff, mincutoff;
        Facet? facet, neighbor, bestfacet = null;
        int searchsize = 0;
        bool newbest;
        bool ischeckmax = bestoutside && !newfacets && !wantisoutside;
        bool ispartition = newfacets && wantisoutside;
        bool isfindfacet = !newfacets && wantisoutside;
        bool testhorizon = ispartition && (bestoutside || APPROXhull || MERGING);
        numpart = 0;
        dist = 0;
        if (!ischeckmax && !ispartition && !isfindfacet)
            throw qh_errexit(qh_ERRqhull, startfacet, null);
        if (wantisoutside)
            isoutside = true;
        if (!startfacet.flipped)
        {
            numpart = 1;
            qh_distplane(point, startfacet, out dist);
            if (!startfacet.upperdelaunay || (!noupper && dist >= MINoutside))
            {
                bestdist = dist;
                bestfacet = startfacet;
                if (!bestoutside && dist >= MINoutside)
                    goto LABELreturn_best;
            }
            if (ischeckmax && (!ONLYgood || startfacet.good) && dist > startfacet.maxoutside)
                startfacet.maxoutside = dist;
        }
        if (ispartition)
            searchdist = 2 * DISTround;
        else
            searchdist = (2 * DISTround + fmax_(MINvisible, MAXcoplanar)) + max_outside; // reference order
        cutoff = bestdist - searchdist;
        mincutoff = 0;
        if (ischeckmax)
        {
            mincutoff = fmax_(MINvisible, MAXcoplanar) - DISTround; // reference order
            if (cutoff > mincutoff)
                cutoff = mincutoff;
        }
        startfacet.visitid = ++visit_id;
        facet = startfacet;
        do
        {
        LABELrestart:
            newbest = false;
            QSet<Facet> neighbors = facet!.neighbors!;
            for (int ni = 0; (neighbor = neighbors.e[ni]) != null; ni++)
            {
                if (ispartition && !neighbor.newfacet)
                    continue;
                if (!neighbor.flipped)
                {
                    if (neighbor.visitid == visit_id)
                        continue;
                    neighbor.visitid = visit_id;
                    numpart++;
                    qh_distplane(point, neighbor, out dist);
                    if (!bestoutside && dist >= MINoutside
                        && (!noupper || !facet.upperdelaunay))
                    {
                        bestfacet = neighbor;
                        goto LABELreturn_best;
                    }
                    if (ischeckmax)
                    {
                        if ((!ONLYgood || neighbor.good)
                            && dist > neighbor.maxoutside)
                            neighbor.maxoutside = dist;
                        else if (bestfacet != null && dist < cutoff)
                            continue;
                    }
                    else if (bestfacet != null && dist < cutoff)
                        continue;
                    if (dist > bestdist)
                    {
                        if (!neighbor.upperdelaunay
                            || (bestoutside && !noupper && dist >= MINoutside))
                        {
                            if (ischeckmax)
                            {
                                bestdist = dist;
                                bestfacet = neighbor;
                                cutoff = bestdist - searchdist;
                                if (cutoff > mincutoff)
                                    cutoff = mincutoff;
                            }
                            else if (dist > bestdist + searchdist)
                            {
                                bestdist = dist;
                                bestfacet = neighbor;
                                cutoff = bestdist - searchdist;
                                searchsize = 0;
                                facet = neighbor;
                                if (newbest)
                                    facet.visitid = ++visit_id;
                                goto LABELrestart;
                            }
                            else
                            {
                                bestdist = dist;
                                bestfacet = neighbor;
                                cutoff = bestdist - searchdist;
                            }
                            newbest = true;
                        }
                    }
                }
                if (searchsize++ == 0)
                {
                    searchset!.e[0] = neighbor;
                    searchset.Truncate(1);
                }
                else
                    QSet<Facet>.Append(ref searchset, neighbor, pool);
            }
        }
        while (searchsize != 0 && (facet = QSet<Facet>.DelLast(searchset)) != null);
        if (!ischeckmax)
        {
            if (bestfacet == null)
            {
                FORCEoutput = true;
                throw qh_errexit(qh_ERRqhull, startfacet, null);
            }
            if (ispartition && !findbest_notsharp && bestdist < -DISTround)
            {
                if (qh_findbestsharp(point, ref bestfacet, ref bestdist, ref numpart))
                    findbestnew = true;
                else
                    findbest_notsharp = true;
            }
            if (testhorizon)
            {
                facet = bestfacet.neighbors!.e[0]!;
                numpart++;
                qh_distplane(point, facet, out dist);
                if (dist > bestdist
                    && (!facet.upperdelaunay || (!noupper && dist >= MINoutside)))
                {
                    bestdist = dist;
                    bestfacet = facet;
                }
            }
        }
        dist = bestdist;
        if (wantisoutside && bestdist < MINoutside)
            isoutside = false;
    LABELreturn_best:
        return bestfacet;
    }

    /// <summary>qh_findbestnew</summary>
    internal Facet qh_findbestnew(double[] point, Facet? startfacet,
        out double dist, bool wantisoutside, ref bool isoutside, out int numpart)
    {
        double bestdist = -REALmax, bestdist2 = -REALmax;
        Facet? neighbor, bestfacet = null, newfacet, facet;
        Facet? bestfacet2 = null;
        double distoutside;
        dist = 0;
        if (startfacet == null)
            throw qh_errexit(qh_ERRqhull, null, null);
        if (BESToutside || !wantisoutside)
            distoutside = REALmax;
        else if (MERGING)
            distoutside = qh_DISToutside();
        else
            distoutside = MINoutside;
        if (wantisoutside)
            isoutside = true;
        numpart = 0;
        facet = startfacet;
        for (int i = 0; i < 2; i++, facet = newfacet_list)
        {
            // FORALLfacet_(facet)
            if (facet != null)
            {
                for (; facet != null && facet.next != null; facet = facet.next)
                {
                    if (facet == startfacet && i != 0)
                        break;
                    qh_distplane(point, facet, out dist);
                    numpart++;
                    if (facet.upperdelaunay)
                    {
                        if (dist > bestdist2)
                        {
                            bestdist2 = dist;
                            bestfacet2 = facet;
                            if (dist >= distoutside)
                            {
                                bestfacet = facet;
                                goto LABELreturn_bestnew;
                            }
                        }
                    }
                    else if (dist > bestdist)
                    {
                        bestdist = dist;
                        bestfacet = facet;
                        if (dist >= distoutside)
                            goto LABELreturn_bestnew;
                    }
                }
            }
        }
        newfacet = bestfacet ?? bestfacet2;
        QSet<Facet> nbs = newfacet!.neighbors!;
        for (int ni = 0; (neighbor = nbs.e[ni]) != null; ni++)
        {
            if (!neighbor.newfacet)
            {
                qh_distplane(point, neighbor, out dist);
                numpart++;
                if (neighbor.upperdelaunay)
                {
                    if (dist > bestdist2)
                    {
                        bestdist2 = dist;
                        bestfacet2 = neighbor;
                    }
                }
                else if (dist > bestdist)
                {
                    bestdist = dist;
                    bestfacet = neighbor;
                }
            }
        }
        if (bestfacet == null
            || (wantisoutside && bestdist2 >= MINoutside && bestdist2 > bestdist))
        {
            dist = bestdist2;
            bestfacet = bestfacet2;
        }
        else
            dist = bestdist;
        if (wantisoutside && dist < MINoutside)
            isoutside = false;
    LABELreturn_bestnew:
        return bestfacet!;
    }

    /// <summary>qh_DISToutside: fmax_(4*qh MINoutside, 2*qh max_outside)</summary>
    internal double qh_DISToutside() => fmax_(4 * MINoutside, 2 * max_outside);

    /// <summary>qh_gausselim</summary>
    internal void qh_gausselim(double[]?[] rows, int numrow, int numcol, ref bool sign, out bool nearzero)
    {
        double n, pivot, pivot_abs = 0.0, temp;
        int pivoti;
        nearzero = false;
        for (int k = 0; k < numrow; k++)
        {
            pivot_abs = fabs_(rows[k]![k]);
            pivoti = k;
            for (int i = k + 1; i < numrow; i++)
            {
                if ((temp = fabs_(rows[i]![k])) > pivot_abs)
                {
                    pivot_abs = temp;
                    pivoti = i;
                }
            }
            if (pivoti != k)
            {
                double[]? rowp = rows[pivoti];
                rows[pivoti] = rows[k];
                rows[k] = rowp;
                sign ^= true;
            }
            if (pivot_abs <= NEARzero[k])
            {
                nearzero = true;
                if (pivot_abs == 0.0)
                {
                    qh_precision("zero pivot for Gaussian elimination");
                    continue; // goto LABELnextcol
                }
            }
            double[] pivotrow = rows[k]!;
            int pivotp = k;
            pivot = pivotrow[pivotp++];
            double recip = 1.0 / pivot; // reference order: one reciprocal per pivot
            for (int i = k + 1; i < numrow; i++)
            {
                double[] rowi = rows[i]!;
                int ai = k;
                int ak = pivotp;
                n = rowi[ai++] * recip; // reference order
                for (int j = numcol - (k + 1); j-- > 0;)
                    rowi[ai++] -= n * pivotrow[ak++];
            }
        }
    }

    /// <summary>qh_getangle</summary>
    internal double qh_getangle(double[] vect1, double[] vect2)
    {
        double angle = 0;
        for (int k = 0; k < hull_dim; k++)
            angle += vect1[k] * vect2[k];
        return angle;
    }

    /// <summary>qh_getcenter</summary>
    internal double[] qh_getcenter(QSet<Vertex> vertices)
    {
        int count = vertices.n;
        if (count < 2)
            throw qh_errexit(qh_ERRqhull, null, null);
        var center = NewPoint(-1);
        Vertex? vertex;
        for (int k = 0; k < hull_dim; k++)
        {
            center[k] = 0.0;
            for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
                center[k] += vertex.point![k];
            center[k] = center[k] * (1.0 / count); // reference order times the reciprocal
        }
        return center;
    }

    /// <summary>A point-sized buffer (qh normal_size + the id slot of this port).</summary>
    internal double[] NewPoint(int id)
    {
        double[] p = pool != null ? pool.NewPoint(hull_dim + 1) : new double[hull_dim + 1];
        p[hull_dim] = id;
        return p;
    }

    /// <summary>qh_getcentrum</summary>
    internal double[] qh_getcentrum(Facet facet)
    {
        double[] point = qh_getcenter(facet.vertices!);
        qh_distplane(point, facet, out double dist);
        return qh_projectpoint(point, facet, dist);
    }

    /// <summary>qh_getdistance</summary>
    internal double qh_getdistance(Facet facet, Facet neighbor, out double mindist, out double maxdist)
    {
        Vertex? vertex;
        double maxd, mind;
        QSet<Vertex> fv = facet.vertices!;
        QSet<Vertex> nv = neighbor.vertices!;
        for (int i = 0; (vertex = fv.e[i]) != null; i++)
            vertex.seen = false;
        for (int i = 0; (vertex = nv.e[i]) != null; i++)
            vertex.seen = true;
        mind = 0.0;
        maxd = 0.0;
        for (int i = 0; (vertex = fv.e[i]) != null; i++)
        {
            if (!vertex.seen)
            {
                qh_distplane(vertex.point!, neighbor, out double dist);
                if (dist < mind)
                    mind = dist;
                else if (dist > maxd)
                    maxd = dist;
            }
        }
        mindist = mind;
        maxdist = maxd;
        mind = -mind;
        // reference order: maxsd (-mind > maxd) ? -mind : maxd (differs from the source only for +-0)
        if (mind > maxd)
            return mind;
        else
            return maxd;
    }

    /// <summary>qh_normalize2 (minnorm/ismin not used by the ported callers)</summary>
    internal void qh_normalize2(double[] normal, int dim, bool toporient)
    {
        double norm = 0, temp;
        bool zerodiv = false;
        if (dim == 2)
            norm = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1]);
        else if (dim == 3)
            norm = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
        else if (dim == 4)
        {
            norm = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]
                             + normal[3] * normal[3]);
        }
        else if (dim > 4)
        {
            norm = normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]
                   + normal[3] * normal[3];
            for (int k = 4; k < dim; k++)
                norm += normal[k] * normal[k];
            norm = Math.Sqrt(norm);
        }
        if (norm > MINdenom)
        {
            if (!toporient)
                norm = -norm;
            normal[0] /= norm;
            normal[1] /= norm;
            if (dim == 2)
            {
            }
            else if (dim == 3)
                normal[2] /= norm;
            else if (dim == 4)
            {
                normal[2] /= norm;
                normal[3] /= norm;
            }
            else if (dim > 4)
            {
                normal[2] /= norm;
                normal[3] /= norm;
                for (int k = 4; k < dim; k++)
                    normal[k] /= norm;
            }
        }
        else if (norm == 0.0)
        {
            temp = Math.Sqrt(1.0 / dim);
            for (int k = 0; k < dim; k++)
                normal[k] = temp;
        }
        else
        {
            if (!toporient)
                norm = -norm;
            for (int k = 0; k < dim; k++)
            {
                temp = qh_divzero(normal[k], norm, MINdenom_1, ref zerodiv);
                if (!zerodiv)
                    normal[k] = temp;
                else
                {
                    int maxp = qh_maxabsval(normal, dim);
                    temp = (normal[maxp] * norm >= 0.0) ? 1.0 : -1.0;
                    for (int kk = 0; kk < dim; kk++)
                        normal[kk] = 0.0;
                    normal[maxp] = temp;
                    return;
                }
            }
        }
    }

    /// <summary>qh_projectpoint</summary>
    internal double[] qh_projectpoint(double[] point, Facet facet, double dist)
    {
        double[] newpoint = NewPoint(-1);
        double[] normal = facet.normal!;
        for (int k = 0; k < hull_dim; k++)
            newpoint[k] = point[k] - dist * normal[k];
        return newpoint;
    }

    /// <summary>qh_setfacetplane</summary>
    internal void qh_setfacetplane(Facet facet)
    {
        QSet<Vertex> fverts = facet.vertices!;
        double[] point0 = fverts.e[0]!.point!;
        bool nearzero = false;
        Vertex? vertex;
        Zsetplane++;
        facet.normal ??= facet.spareNormal ?? new double[hull_dim]; // spareNormal: QhPool reuse
        if (hull_dim <= 4)
        {
            int i = 0;
            // RANDOMdist ('Rn') is not ported
            for (int vi = 0; (vertex = fverts.e[vi]) != null; vi++)
                gm_row[i++] = vertex.point;
            qh_sethyperplane_det(hull_dim, gm_row, point0, facet.toporient,
                facet.normal, out facet.offset, ref nearzero);
        }
        if (hull_dim > 4 || nearzero)
        {
            int i = 0;
            int gmcoord = 0;
            for (int vi = 0; (vertex = fverts.e[vi]) != null; vi++)
            {
                if (vertex.point != point0)
                {
                    double[] row = gm_matrix[gmcoord++];
                    gm_row[i++] = row;
                    double[] coord = vertex.point!;
                    for (int k = 0; k < hull_dim; k++)
                        row[k] = coord[k] - point0[k];
                }
            }
            gm_row[i] = gm_matrix[gmcoord];
            qh_sethyperplane_gauss(hull_dim, gm_row, point0, facet.toporient,
                facet.normal, out facet.offset, ref nearzero);
            if (nearzero)
            {
                qh_orientoutside(facet);
            }
        }
        facet.upperdelaunay = false;
        // DELAUNAY is not ported
        if (JOGGLEmax < REALmax)
        {
            // PRINTstatistics || IStracing || TRACElevel || JOGGLEmax < REALmax
            old_randomdist = RANDOMdist;
            RANDOMdist = false;
            for (int vi = 0; (vertex = fverts.e[vi]) != null; vi++)
            {
                if (vertex.point != point0)
                {
                    qh_distplane(vertex.point!, facet, out double dist);
                    dist = fabs_(dist);
                    if (dist > Wnewvertexmax)
                    {
                        Wnewvertexmax = dist;
                        if (dist > max_outside)
                            max_outside = dist;
                    }
                }
            }
            RANDOMdist = old_randomdist;
        }
    }

    /// <summary>qh_sethyperplane_det (dimensions 2..4)</summary>
    internal void qh_sethyperplane_det(int dim, double[]?[] rows, double[] point0,
        bool toporient, double[] normal, out double offset, ref bool nearzero)
    {
        offset = 0;
        if (dim == 2)
        {
            double[] r0 = rows[0]!, r1 = rows[1]!;
            normal[0] = r1[1] - r0[1];                // dY(1,0)
            normal[1] = r0[0] - r1[0];                // dX(0,1)
            qh_normalize2(normal, dim, toporient);
            offset = -(point0[0] * normal[0] + point0[1] * normal[1]);
            nearzero = false;
        }
        else if (dim == 3)
        {
            double[] r0 = rows[0]!, r1 = rows[1]!, r2 = rows[2]!;
            // reference order det2_(a, b, c, d) = a*d - b*c is emitted as
            // a*d + b*(the negated difference), e.g. dY20*dZ10 + dZ20*(r0y - r1y)
            normal[0] = (r2[1] - r0[1]) * (r1[2] - r0[2]) + (r2[2] - r0[2]) * (r0[1] - r1[1]);
            normal[1] = (r1[0] - r0[0]) * (r2[2] - r0[2]) + (r1[2] - r0[2]) * (r0[0] - r2[0]);
            normal[2] = (r2[0] - r0[0]) * (r1[1] - r0[1]) + (r2[1] - r0[1]) * (r0[0] - r1[0]);
            qh_normalize2(normal, dim, toporient);
            offset = -(point0[0] * normal[0] + point0[1] * normal[1]
                       + point0[2] * normal[2]);
            double maxround = DISTround;
            for (int i = dim; i-- > 0;)
            {
                double[] point = rows[i]!;
                if (point != point0)
                {
                    // reference order: (z*nz + offset) + (x*nx + y*ny)
                    double dist = (point[2] * normal[2] + offset) + (point[0] * normal[0] + point[1] * normal[1]);
                    if (dist > maxround || dist < -maxround)
                    {
                        nearzero = true;
                        break;
                    }
                }
            }
        }
        else if (dim == 4)
        {
            throw new NotSupportedException("qhull port: 4-d is not ported");
        }
    }

    /// <summary>det2_ macro: ((a1)*(b2) - (a2)*(b1))</summary>
    internal static double det2_(double a1, double a2, double b1, double b2) => a1 * b2 - a2 * b1;

    /// <summary>det3_ macro</summary>
    internal static double det3_(double a1, double a2, double a3, double b1, double b2, double b3,
        double c1, double c2, double c3)
        => a1 * det2_(b2, b3, c2, c3) - b1 * det2_(a2, a3, c2, c3) + c1 * det2_(a2, a3, b2, b3);

    /// <summary>qh_sethyperplane_gauss</summary>
    internal void qh_sethyperplane_gauss(int dim, double[]?[] rows, double[] point0,
        bool toporient, double[] normal, out double offset, ref bool nearzero)
    {
        bool sign = toporient, nearzero2 = false;
        qh_gausselim(rows, dim - 1, dim, ref sign, out nearzero);
        for (int k = dim - 1; k-- > 0;)
        {
            if (rows[k]![k] < 0)
                sign ^= true;
        }
        if (nearzero)
        {
            qh_backnormal(rows, dim - 1, dim, sign, normal, ref nearzero2);
        }
        else
        {
            qh_backnormal(rows, dim - 1, dim, sign, normal, ref nearzero2);
        }
        if (nearzero2)
            nearzero = true;
        qh_normalize2(normal, dim, true);
        offset = -(point0[0] * normal[0]);
        for (int k = 1; k < dim; k++)
            offset -= point0[k] * normal[k];
    }
}
