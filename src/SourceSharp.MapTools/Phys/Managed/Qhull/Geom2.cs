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

/// <summary>geom2.c (the parts the 3-d convex hull with merging and joggle reaches).</summary>
internal sealed partial class Qh
{
    /// <summary>qh_determinant (2-d and 3-d closed forms; Gaussian elimination above)</summary>
    internal double qh_determinant(double[]?[] rows, int dim, out bool nearzero)
    {
        double det = 0;
        bool sign = false;
        nearzero = false;
        if (dim < 2)
            throw qh_errexit(qh_ERRqhull, null, null);
        else if (dim == 2)
        {
            double[] r0 = rows[0]!, r1 = rows[1]!;
            det = det2_(r0[0], r0[1],
                        r1[0], r1[1]);
            if (fabs_(det) < NEARzero[1])
                nearzero = true;
        }
        else if (dim == 3)
        {
            double[] r0 = rows[0]!, r1 = rows[1]!, r2 = rows[2]!;
            // reference order: (a1*(b2c3 - b3c2) + b1*(c2a3 - c3a2)) + c1*(a2b3 - b2a3)
            det = (r0[0] * (r1[1] * r2[2] - r1[2] * r2[1])
                   + r1[0] * (r2[1] * r0[2] - r2[2] * r0[1]))
                  + r2[0] * (r0[1] * r1[2] - r1[1] * r0[2]);
            if (fabs_(det) < NEARzero[2])
                nearzero = true;
        }
        else
        {
            qh_gausselim(rows, dim, dim, ref sign, out nearzero);
            det = 1.0;
            for (int i = dim; i-- > 0;)
                det *= rows[i]![i];
            if (sign)
                det = -det;
        }
        return det;
    }

    /// <summary>qh_detjoggle ('QJ' without a value)</summary>
    internal double qh_detjoggle(double[][] points, int numpoints, int dimension)
    {
        double abscoord, distround, joggle, maxcoord, mincoord;
        double maxabs = -REALmax;
        double sumabs = 0;
        double maxwidth = 0;
        for (int k = 0; k < dimension; k++)
        {
            if (SCALElast && k == dimension - 1)
                abscoord = maxwidth;
            else if (DELAUNAY && k == dimension - 1)
                abscoord = 2 * maxabs * maxabs;
            else
            {
                maxcoord = -REALmax;
                mincoord = REALmax;
                for (int i = 0; i < numpoints; i++)
                {
                    double[] point = points[i];
                    if (maxcoord < point[k])
                        maxcoord = point[k];
                    if (mincoord > point[k])
                        mincoord = point[k];
                }
                if (maxwidth < maxcoord - mincoord)
                    maxwidth = maxcoord - mincoord;
                abscoord = fmax_(maxcoord, -mincoord);
            }
            sumabs += abscoord;
            if (maxabs < abscoord)
                maxabs = abscoord;
        }
        distround = qh_distround(hull_dim, maxabs, sumabs);
        joggle = distround * qh_JOGGLEdefault;
        if (joggle < REALepsilon * qh_JOGGLEdefault)
            joggle = REALepsilon * qh_JOGGLEdefault;
        return joggle;
    }

    /// <summary>qh_detroundoff</summary>
    internal void qh_detroundoff()
    {
        if (!SETroundoff)
        {
            DISTround = qh_distround(hull_dim, MAXabs_coord, MAXsumcoord);
            // RANDOMdist is not ported
        }
        MINdenom_1 = fmax_(1.0 / REALmax, REALmin);
        MINdenom = MINdenom_1 * MAXabs_coord;
        MINdenom_1_2 = Math.Sqrt(MINdenom_1 * hull_dim);
        MINdenom_2 = MINdenom_1_2 * MAXabs_coord;
        ANGLEround = hull_dim * (1.01 * REALepsilon); // reference order: folded constant
        if (premerge_cos < REALmax / 2)
        {
            premerge_cos -= ANGLEround;
        }
        if (postmerge_cos < REALmax / 2)
        {
            postmerge_cos -= ANGLEround;
        }
        premerge_centrum += 2 * DISTround;
        postmerge_centrum += 2 * DISTround;
        {
            double maxangle = 1.0, maxrho;
            if (maxangle > premerge_cos)
                maxangle = premerge_cos;
            if (maxangle > postmerge_cos)
                maxangle = postmerge_cos;
            // reference order: sqrt(dim)*sqrt(x) became sqrt(x*dim)
            ONEmerge = Math.Sqrt((1.0 - maxangle * maxangle) * hull_dim) * MAXwidth + DISTround;
            maxrho = hull_dim * premerge_centrum + DISTround;
            if (ONEmerge < maxrho)
                ONEmerge = maxrho;
            maxrho = hull_dim * postmerge_centrum + DISTround;
            if (ONEmerge < maxrho)
                ONEmerge = maxrho;
        }
        NEARinside = ONEmerge * qh_RATIOnearinside;
        if (JOGGLEmax < REALmax / 2 && (KEEPcoplanar || KEEPinside))
        {
            double maxdist;
            KEEPnearinside = true;
            maxdist = Math.Sqrt(hull_dim) * JOGGLEmax + DISTround;
            maxdist = 2 * maxdist;
            if (NEARinside < maxdist)
                NEARinside = maxdist;
        }
        if (JOGGLEmax < DISTround)
            throw qh_errexit(qh_ERRinput, null, null);
        if (MINvisible > REALmax / 2)
        {
            if (!MERGING)
                MINvisible = DISTround;
            else if (hull_dim <= 3)
                MINvisible = premerge_centrum;
            else
                MINvisible = qh_COPLANARratio * premerge_centrum;
            if (APPROXhull && MINvisible > MINoutside)
                MINvisible = MINoutside;
        }
        if (MAXcoplanar > REALmax / 2)
        {
            MAXcoplanar = MINvisible;
        }
        if (!APPROXhull)
        {
            MINoutside = 2 * MINvisible;
            if (premerge_cos < REALmax / 2)
            {
                double t = (1 - premerge_cos) * MAXabs_coord;
                if (MINoutside < t)
                    MINoutside = t;
            }
        }
        WIDEfacet = MINoutside;
        {
            double t = qh_WIDEcoplanar * MAXcoplanar;
            if (WIDEfacet < t)
                WIDEfacet = t;
            t = qh_WIDEcoplanar * MINvisible;
            if (WIDEfacet < t)
                WIDEfacet = t;
        }
        max_vertex = DISTround;
        min_vertex = -DISTround;
    }

    /// <summary>qh_detsimplex</summary>
    internal double qh_detsimplex(double[] apex, QSet<double[]> points, int dim, out bool nearzero)
    {
        int i = 0;
        double[]? point;
        double[]?[] rows = gm_row;
        for (int pi = 0; (point = points.e[pi]) != null; pi++)
        {
            if (i == dim)
                break;
            double[] row = gm_matrix[i];
            rows[i++] = row;
            for (int k = 0; k < dim; k++)
                row[k] = point[k] - apex[k];
        }
        if (i < dim)
            throw qh_errexit(qh_ERRqhull, null, null);
        return qh_determinant(rows, dim, out nearzero);
    }

    /// <summary>qh_distround</summary>
    internal static double qh_distround(int dimension, double maxabs, double maxsumabs)
    {
        double maxdistsum, maxround;
        maxdistsum = Math.Sqrt(dimension) * maxabs;
        if (maxdistsum > maxsumabs)
            maxdistsum = maxsumabs;
        maxround = REALepsilon * (dimension * maxdistsum * 1.01 + maxabs);
        return maxround;
    }

    /// <summary>qh_divzero</summary>
    internal static double qh_divzero(double numer, double denom, double mindenom1, ref bool zerodiv)
    {
        double temp, numerx, denomx;
        if (numer < mindenom1 && numer > -mindenom1)
        {
            numerx = fabs_(numer);
            denomx = fabs_(denom);
            if (numerx < denomx)
            {
                zerodiv = false;
                return numer / denom;
            }
            else
            {
                zerodiv = true;
                return 0.0;
            }
        }
        temp = denom / numer;
        if (temp > mindenom1 || temp < -mindenom1)
        {
            zerodiv = false;
            return numer / denom;
        }
        else
        {
            zerodiv = true;
            return 0.0;
        }
    }

    /// <summary>qh_findbestsharp</summary>
    internal bool qh_findbestsharp(double[] point, ref Facet bestfacet, ref double bestdist, ref int numpart)
    {
        bool issharp = false;
        Span<bool> quadrant = stackalloc bool[hull_dim];
        for (Facet? facet = newfacet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet == newfacet_list)
            {
                for (int k = hull_dim; k-- > 0;)
                    quadrant[k] = facet.normal![k] > 0;
            }
            else if (!issharp)
            {
                for (int k = hull_dim; k-- > 0;)
                {
                    if (quadrant[k] != (facet.normal![k] > 0))
                    {
                        issharp = true;
                        break;
                    }
                }
            }
            if (facet.visitid != visit_id)
            {
                qh_distplane(point, facet, out double dist);
                numpart++;
                if (dist > bestdist)
                {
                    if (!facet.upperdelaunay || dist > MINoutside)
                    {
                        bestdist = dist;
                        bestfacet = facet;
                    }
                }
            }
        }
        return issharp;
    }

    /// <summary>qh_joggleinput</summary>
    internal void qh_joggleinput()
    {
        double randr, randa, randb;
        if (input_points == null)
        {
            input_points = first_point;
            var np = new double[num_points][];
            for (int i = 0; i < num_points; i++)
                np[i] = NewPoint(i);
            first_point = np;
            if (JOGGLEmax == 0.0)
            {
                JOGGLEmax = qh_detjoggle(input_points, num_points, hull_dim);
            }
        }
        else
        {
            if (RERUN == 0 && build_cnt > qh_JOGGLEretry)
            {
                if (((build_cnt - qh_JOGGLEretry - 1) % qh_JOGGLEagain) == 0)
                {
                    double maxjoggle = MAXwidth * qh_JOGGLEmaxincrease;
                    if (JOGGLEmax < maxjoggle)
                    {
                        JOGGLEmax *= qh_JOGGLEincrease;
                        if (JOGGLEmax > maxjoggle)
                            JOGGLEmax = maxjoggle;
                    }
                }
            }
        }
        if (build_cnt > 1 && JOGGLEmax > fmax_(MAXwidth / 4, 0.1))
            throw qh_errexit(qh_ERRqhull, null, null);
        _ = qh_rand(); // seed= qh_RANDOMint; only reported by qh_option
        randa = JOGGLEmax * (2.0 / qh_RANDOMmax); // evaluation order pinned: the reference folds 2/qh_RANDOMmax to 0x1.00000p-30
        randb = -JOGGLEmax;
        for (int i = 0; i < num_points; i++)
        {
            double[] inp = input_points[i];
            double[] outp = first_point[i];
            for (int k = 0; k < hull_dim; k++)
            {
                randr = qh_rand();
                outp[k] = (inp[k] - JOGGLEmax) + randr * randa; // reference order.89961
                _ = randb;
            }
        }
    }

    /// <summary>qh_maxabsval (index of the entry with the largest absolute value, -1 if none)</summary>
    internal static int qh_maxabsval(double[] normal, int dim)
    {
        double maxval = -REALmax;
        int maxp = -1;
        for (int k = 0; k < dim; k++)
        {
            double absval = fabs_(normal[k]);
            if (absval > maxval)
            {
                maxval = absval;
                maxp = k;
            }
        }
        return maxp;
    }

    /// <summary>qh_maxmin</summary>
    internal QSet<double[]> qh_maxmin(double[][] points, int numpoints, int dimension)
    {
        double maxcoord, temp;
        double[] minimum, maximum;
        max_outside = 0.0;
        MAXabs_coord = 0.0;
        MAXwidth = -REALmax;
        MAXsumcoord = 0.0;
        min_vertex = 0.0;
        WAScoplanar = false;
        if (ZEROcentrum)
            ZEROall_ok = true;
        QSet<double[]>? set = new QSet<double[]>(2 * dimension);
        for (int k = 0; k < dimension; k++)
        {
            // GOODpointp is not ported ('QGn')
            minimum = maximum = points[0];
            for (int i = 0; i < numpoints; i++)
            {
                double[] point = points[i];
                if (maximum[k] < point[k])
                    maximum = point;
                else if (minimum[k] > point[k])
                    minimum = point;
            }
            if (k == dimension - 1)
            {
                MINlastcoord = minimum[k];
                MAXlastcoord = maximum[k];
            }
            if (SCALElast && k == dimension - 1)
                maxcoord = MAXwidth;
            else
            {
                maxcoord = fmax_(maximum[k], -minimum[k]);
                temp = maximum[k] - minimum[k];
                if (MAXwidth < temp)
                    MAXwidth = temp;
            }
            if (MAXabs_coord < maxcoord)
                MAXabs_coord = maxcoord;
            MAXsumcoord += maxcoord;
            QSet<double[]>.Append(ref set, maximum);
            QSet<double[]>.Append(ref set, minimum);
            NEARzero[k] = (80 * REALepsilon) * MAXsumcoord; // reference order: folded constant
        }
        return set!;
    }

    /// <summary>qh_maxouter</summary>
    internal double qh_maxouter()
    {
        double dist = fmax_(max_outside, DISTround);
        dist += DISTround;
        return dist;
    }

    /// <summary>qh_maxsimplex</summary>
    internal void qh_maxsimplex(int dim, QSet<double[]>? maxpoints, double[][] points, int numpoints, ref QSet<double[]>? simplex)
    {
        double[]? point, maxpoint, minx = null, maxx = null;
        bool nearzero, maxnearzero = false;
        int k, sizinit;
        double maxdet, det, mincoord = REALmax, maxcoord = -REALmax;
        sizinit = QSet<double[]>.Size(simplex);
        if (sizinit < 2)
        {
            if (QSet<double[]>.Size(maxpoints) >= 2)
            {
                for (int pi = 0; (point = maxpoints!.e[pi]) != null; pi++)
                {
                    if (maxcoord < point[0])
                    {
                        maxcoord = point[0];
                        maxx = point;
                    }
                    if (mincoord > point[0])
                    {
                        mincoord = point[0];
                        minx = point;
                    }
                }
            }
            else
            {
                for (int i = 0; i < numpoints; i++)
                {
                    point = points[i];
                    if (maxcoord < point[0])
                    {
                        maxcoord = point[0];
                        maxx = point;
                    }
                    if (mincoord > point[0])
                    {
                        mincoord = point[0];
                        minx = point;
                    }
                }
            }
            QSet<double[]>.Unique(ref simplex, minx!);
            if (QSet<double[]>.Size(simplex) < 2)
                QSet<double[]>.Unique(ref simplex, maxx!);
            sizinit = QSet<double[]>.Size(simplex);
            if (sizinit < 2)
            {
                qh_precision("input has same x coordinate");
                if (Zsetplane > hull_dim + 1)
                    throw qh_errexit(qh_ERRprec, null, null);
                else
                    throw qh_errexit(qh_ERRinput, null, null);
            }
        }
        for (k = sizinit; k < dim + 1; k++)
        {
            maxpoint = null;
            maxdet = -REALmax;
            if (maxpoints != null)
            {
                for (int pi = 0; (point = maxpoints.e[pi]) != null; pi++)
                {
                    if (!QSet<double[]>.In(simplex, point))
                    {
                        det = qh_detsimplex(point, simplex!, k, out nearzero);
                        if ((det = fabs_(det)) > maxdet)
                        {
                            maxdet = det;
                            maxpoint = point;
                            maxnearzero = nearzero;
                        }
                    }
                }
            }
            if (maxpoint == null || maxnearzero)
            {
                for (int i = 0; i < numpoints; i++)
                {
                    point = points[i];
                    if (!QSet<double[]>.In(simplex, point))
                    {
                        det = qh_detsimplex(point, simplex!, k, out nearzero);
                        if ((det = fabs_(det)) > maxdet)
                        {
                            maxdet = det;
                            maxpoint = point;
                            maxnearzero = nearzero;
                        }
                    }
                }
            }
            if (maxpoint == null)
                throw qh_errexit(qh_ERRqhull, null, null);
            QSet<double[]>.Append(ref simplex, maxpoint);
        }
    }

    /// <summary>qh_orientoutside</summary>
    internal bool qh_orientoutside(Facet facet)
    {
        qh_distplane(interior_point!, facet, out double dist);
        if (dist > 0)
        {
            double[] normal = facet.normal!;
            for (int k = hull_dim; k-- > 0;)
                normal[k] = -normal[k];
            facet.offset = -facet.offset;
            return true;
        }
        return false;
    }
}
