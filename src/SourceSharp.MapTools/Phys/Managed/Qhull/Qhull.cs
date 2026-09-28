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

/// <summary>qh_precision's longjmp to qh.restartexit.</summary>
internal sealed class QhullRestart : Exception
{
    internal QhullRestart()
        : base("qhull restart")
    {
    }
}

/// <summary>qhull.c</summary>
internal sealed partial class Qh
{
    /// <summary>qh_qhull</summary>
    internal void qh_qhull()
    {
        if (RERUN != 0 || JOGGLEmax < REALmax / 2)
            qh_build_withrestart();
        else
        {
            qh_initbuild();
            qh_buildhull();
        }
        if (STOPpoint == 0 && STOPcone == 0)
        {
            if (ZEROall_ok && !TESTvneighbors && MERGEexact)
                qh_checkzero(qh_ALL);
            if (ZEROall_ok && !TESTvneighbors && !WAScoplanar)
            {
                // all facets are clearly convex and no coplanar points
            }
            else
            {
                if (MERGEexact || (hull_dim > qh_DIMreduceBuild && PREmerge))
                    throw new NotSupportedException("qhull port: post-merging is not ported");
                else if (!POSTmerge && TESTvneighbors)
                    throw new NotSupportedException("qhull port: 'Qv' is not ported");
                if (POSTmerge)
                    throw new NotSupportedException("qhull port: post-merging ('Cn', 'An') is not ported");
                if (visible_list == facet_list)
                {
                    findbestnew = true;
                    qh_partitionvisible(!qh_ALL, out _);
                    findbestnew = false;
                    qh_deletevisible();
                    qh_resetlists(false);
                }
                if (DOcheckmax)
                    qh_check_maxout();
            }
        }
        if (KEEPnearinside && !maxoutdone)
            qh_nearcoplanar();
        QHULLfinished = true;
    }

    /// <summary>qh_addpoint</summary>
    internal bool qh_addpoint(double[] furthest, Facet facet, bool checkdist)
    {
        Vertex vertex;
        maxoutdone = false;
        if (qh_pointid(furthest) == -1)
            QSet<double[]>.Append(ref other_points, furthest, pool);
        if (checkdist)
        {
            bool isoutside = false;
            facet = qh_findbest(furthest, facet, !qh_ALL, false, !qh_NOupper,
                out double dist, true, ref isoutside, out _)!;
            if (!isoutside)
            {
                facet.notfurthest = true;
                qh_partitioncoplanar(furthest, facet, true, dist);
                return true;
            }
        }
        qh_buildtracing(furthest, facet);
        if (STOPpoint < 0 && furthest_id == -STOPpoint - 1)
        {
            facet.notfurthest = true;
            return false;
        }
        qh_findhorizon(furthest, facet, out int goodvisible, out int goodhorizon);
        if (ONLYgood)
            throw new NotSupportedException("qhull port: 'Qg' is not ported");
        _ = goodvisible;
        _ = goodhorizon;
        vertex = qh_makenewfacets(furthest);
        qh_makenewplanes();
        qh_matchnewfacets();
        qh_updatevertices();
        if (STOPcone != 0 && furthest_id == STOPcone - 1)
        {
            facet.notfurthest = true;
            return false;
        }
        if (PREmerge || MERGEexact)
        {
            qh_premerge(vertex, premerge_centrum, premerge_cos);
            if (Ztotmerge > qh_USEfindbestnew)
                findbestnew = true;
            else
            {
                for (Facet? newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
                {
                    if (!newfacet.simplicial)
                    {
                        findbestnew = true;
                        break;
                    }
                }
            }
        }
        else if (BESToutside)
            findbestnew = true;
        qh_partitionvisible(!qh_ALL, out _);
        findbestnew = false;
        findbest_notsharp = false;
        qh_deletevisible();
        NEWfacets = false;
        if (STOPpoint > 0 && furthest_id == STOPpoint - 1)
            return false;
        qh_resetlists(true);
        return true;
    }

    /// <summary>qh_build_withrestart</summary>
    internal void qh_build_withrestart()
    {
        ALLOWrestart = true;
        bool restart = false;
        while (true)
        {
            if (restart)
            {
                ERREXITcalled = false;
                STOPcone = 1; // qh STOPcone= True
            }
            if (RERUN == 0 && JOGGLEmax < REALmax / 2)
            {
                if (build_cnt > qh_JOGGLEmaxretry)
                    throw qh_errexit(qh_ERRqhull, null, null);
                if (build_cnt != 0 && !restart)
                    break;
            }
            else if (build_cnt != 0 && build_cnt >= RERUN)
                break;
            STOPcone = 0;
            qh_freebuild(true);
            build_cnt++;
            try
            {
                if (JOGGLEmax < REALmax / 2)
                    qh_joggleinput();
                qh_initbuild();
                qh_buildhull();
                if (JOGGLEmax < REALmax / 2 && !MERGING)
                    qh_checkconvex(facet_list, qh_ALGORITHMfault);
                restart = false;
            }
            catch (QhullRestart)
            {
                restart = true;
            }
        }
        ALLOWrestart = false;
    }

    /// <summary>qh_freebuild (allmem): drops the previous build (the memory is the GC's).</summary>
    internal void qh_freebuild(bool allmem)
    {
        del_vertices?.Truncate(0);
        if (allmem)
        {
            qh_clearcenters(QhCenter.qh_ASnone);
            vertex_list = newvertex_list = null;
        }
        VERTEXneighbors = false;
        GOODclosest = null;
        if (allmem)
            visible_list = newfacet_list = facet_list = null;
        hash_table = null;
        interior_point = null;
        facet_mergeset = null;
        degen_mergeset = null;
    }

    /// <summary>qh_buildhull</summary>
    internal void qh_buildhull()
    {
        Facet? facet;
        double[]? furthest;
        for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.visible || facet.newfacet)
                throw qh_errexit(qh_ERRqhull, facet, null);
        }
        for (Vertex? vertex = vertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
        {
            if (vertex.newlist)
                throw qh_errexit(qh_ERRqhull, null, null);
            int id = qh_pointid(vertex.point);
            if ((STOPpoint > 0 && id == STOPpoint - 1) ||
                (STOPpoint < 0 && id == -STOPpoint - 1) ||
                (STOPcone > 0 && id == STOPcone - 1))
            {
                return;
            }
        }
        facet_next = facet_list;
        while ((furthest = qh_nextfurthest(out facet)) != null)
        {
            num_outside--;
            if (!qh_addpoint(furthest, facet!, ONLYmax))
                break;
        }
        if (NARROWhull)
            qh_outcoplanar();
        if (num_outside != 0 && furthest == null)
            throw qh_errexit(qh_ERRqhull, null, null);
    }

    /// <summary>qh_buildtracing (only its side effects: tracing and reports are not ported)</summary>
    internal void qh_buildtracing(double[]? furthest, Facet? facet)
    {
        old_randomdist = RANDOMdist;
        RANDOMdist = false;
        if (furthest == null)
            return; // C returns here without restoring RANDOMdist
        int furthestid = qh_pointid(furthest);
        if (visit_id > int.MaxValue)
        {
            visit_id = 0;
            for (Facet? f = facet_list; f != null && f.next != null; f = f.next)
                f.visitid = visit_id;
        }
        if (vertex_visit > int.MaxValue)
        {
            vertex_visit = 0;
            for (Vertex? v = vertex_list; v != null && v.next != null; v = v.next)
                v.visitid = vertex_visit;
        }
        furthest_id = furthestid;
        RANDOMdist = old_randomdist;
        _ = facet;
    }

    /// <summary>qh_findhorizon</summary>
    internal void qh_findhorizon(double[] point, Facet facet, out int goodvisible, out int goodhorizon)
    {
        Facet? neighbor, visible;
        int numhorizon = 0;
        double dist;
        goodvisible = goodhorizon = 0;
        qh_removefacet(facet);
        qh_appendfacet(facet);
        num_visible = 1;
        if (facet.good)
            goodvisible++;
        visible_list = facet;
        facet.visible = true;
        facet.f = null; // f.replace
        visit_id++;
        for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
        {
            visible.visitid = visit_id;
            QSet<Facet> vn = visible.neighbors!;
            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
            {
                if (neighbor.visitid == visit_id)
                    continue;
                neighbor.visitid = visit_id;
                qh_distplane(point, neighbor, out dist);
                if (dist > MINvisible)
                {
                    qh_removefacet(neighbor);
                    qh_appendfacet(neighbor);
                    neighbor.visible = true;
                    neighbor.f = null; // f.replace
                    num_visible++;
                    if (neighbor.good)
                        goodvisible++;
                }
                else
                {
                    if (dist > -MAXcoplanar)
                    {
                        neighbor.coplanar = true;
                        qh_precision("coplanar horizon");
                        if (MERGING)
                        {
                            if (dist > 0)
                            {
                                if (max_outside < dist)
                                    max_outside = dist;
                                if (max_vertex < dist)
                                    max_vertex = dist;
                                if (neighbor.maxoutside < dist)
                                    neighbor.maxoutside = dist;
                            }
                            else if (min_vertex > dist)
                                min_vertex = dist;
                        }
                    }
                    else
                        neighbor.coplanar = false;
                    numhorizon++;
                    if (neighbor.good)
                        goodhorizon++;
                }
            }
        }
        if (numhorizon == 0)
        {
            qh_precision("empty horizon");
            throw qh_errexit(qh_ERRprec, null, null);
        }
    }

    /// <summary>qh_nextfurthest</summary>
    internal double[]? qh_nextfurthest(out Facet? visible)
    {
        Facet facet;
        int size;
        double dist;
        visible = null;
        while ((facet = facet_next!) != facet_tail)
        {
            if (facet.outsideset == null)
            {
                facet_next = facet.next;
                continue;
            }
            size = facet.outsideset.n;
            if (size == 0)
            {
                facet.outsideset = null;
                facet_next = facet.next;
                continue;
            }
            if (NARROWhull)
            {
                if (facet.notfurthest)
                    qh_furthestout(facet);
                dist = facet.furthestdist;
                if (dist < MINoutside)
                {
                    facet_next = facet.next;
                    continue;
                }
            }
            if (!RANDOMoutside && !VIRTUALmemory)
            {
                if (PICKfurthest)
                    throw new NotSupportedException("qhull port: 'Q9' is not ported");
                visible = facet;
                return QSet<double[]>.DelLast(facet.outsideset);
            }
            throw new NotSupportedException("qhull port: 'Qr'/'Q7' are not ported");
        }
        return null;
    }

    /// <summary>qh_partitionall</summary>
    internal void qh_partitionall(QSet<Vertex> vertices, double[][] points, int numpoints)
    {
        double[]? point, bestpoint;
        Vertex? vertex;
        int size, point_i, point_n, point_end, remaining, id;
        double bestdist = -REALmax, dist, distoutside;
        var pointset = QSet<double[]>.New(numpoints, pool);
        num_outside = 0;
        for (int i = 0; i < numpoints; i++)
            pointset.e[i] = points[i];
        pointset.Truncate(numpoints);
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
        {
            if ((id = qh_pointid(vertex.point)) >= 0)
                pointset.e[id] = null;
        }
        // GOODpointp / GOODvertexp are not ported (they are NULL)
        if (!BESToutside)
        {
            if (MERGING)
                distoutside = qh_DISToutside();
            else
                distoutside = MINoutside;
            remaining = num_facets;
            point_end = numpoints;
            for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
            {
                size = point_end / (remaining--) + 100;
                facet.outsideset = QSet<double[]>.New(size, pool);
                bestpoint = null;
                point_end = 0;
                point_n = pointset.n;
                for (point_i = 0; point_i < point_n; point_i++)
                {
                    point = pointset.e[point_i];
                    if (point != null)
                    {
                        qh_distplane(point, facet, out dist);
                        if (dist < distoutside)
                            pointset.e[point_end++] = point;
                        else
                        {
                            num_outside++;
                            if (bestpoint == null)
                            {
                                bestpoint = point;
                                bestdist = dist;
                            }
                            else if (dist > bestdist)
                            {
                                QSet<double[]>.Append(ref facet.outsideset, bestpoint, pool);
                                bestpoint = point;
                                bestdist = dist;
                            }
                            else
                                QSet<double[]>.Append(ref facet.outsideset, point, pool);
                        }
                    }
                }
                if (bestpoint != null)
                {
                    QSet<double[]>.Append(ref facet.outsideset, bestpoint, pool);
                    facet.furthestdist = bestdist;
                }
                else
                    facet.outsideset = null;
                pointset.Truncate(point_end);
            }
        }
        if (BESToutside || MERGING || KEEPcoplanar || KEEPinside)
        {
            findbestnew = true;
            point_n = pointset.n;
            for (point_i = 0; point_i < point_n; point_i++)
            {
                point = pointset.e[point_i];
                if (point != null)
                    qh_partitionpoint(point, facet_list!);
            }
            findbestnew = false;
        }
    }

    /// <summary>qh_partitioncoplanar (hasdist false: C's dist == NULL)</summary>
    internal void qh_partitioncoplanar(double[] point, Facet facet, bool hasdist, double distin)
    {
        Facet bestfacet;
        double[]? oldfurthest;
        double bestdist, dist2 = 0;
        WAScoplanar = true;
        if (!hasdist)
        {
            bool isoutside = false;
            if (findbestnew)
                bestfacet = qh_findbestnew(point, facet, out bestdist, false, ref isoutside, out _);
            else
                bestfacet = qh_findbest(point, facet, qh_ALL, false, !qh_NOupper,
                    out bestdist, true, ref isoutside, out _)!;
            if (!KEEPinside)
            {
                if (KEEPnearinside)
                {
                    if (bestdist < -NEARinside)
                        return;
                }
                else if (bestdist < -MAXcoplanar)
                    return;
            }
        }
        else
        {
            bestfacet = facet;
            bestdist = distin;
        }
        if (KEEPcoplanar || KEEPinside || KEEPnearinside)
        {
            oldfurthest = QSet<double[]>.Last(bestfacet.coplanarset);
            if (oldfurthest != null)
                qh_distplane(oldfurthest, bestfacet, out dist2);
            if (oldfurthest == null || dist2 < bestdist)
            {
                QSet<double[]>.Append(ref bestfacet.coplanarset, point, pool);
                if (bestdist > max_outside)
                    max_outside = bestdist;
            }
            else
                QSet<double[]>.Append2ndLast(ref bestfacet.coplanarset, point, pool);
        }
        else
        {
            if (bestdist > max_outside)
                max_outside = bestdist;
        }
    }

    /// <summary>qh_partitionpoint</summary>
    internal void qh_partitionpoint(double[] point, Facet facet)
    {
        double bestdist;
        bool isoutside = false;
        Facet bestfacet;
        if (findbestnew)
            bestfacet = qh_findbestnew(point, facet, out bestdist, true, ref isoutside, out _);
        else
            bestfacet = qh_findbest(point, facet, BESToutside, true, !qh_NOupper,
                out bestdist, true, ref isoutside, out _)!;
        if (NARROWhull)
        {
            if (KEEPnearinside)
            {
                if (bestdist >= -NEARinside)
                    isoutside = true;
            }
            else if (bestdist >= -MAXcoplanar)
                isoutside = true;
        }
        if (isoutside)
        {
            if (bestfacet.outsideset == null
                || QSet<double[]>.Last(bestfacet.outsideset) == null)
            {
                QSet<double[]>.Append(ref bestfacet.outsideset, point, pool);
                if (!bestfacet.newfacet)
                {
                    qh_removefacet(bestfacet);
                    qh_appendfacet(bestfacet);
                }
                bestfacet.furthestdist = bestdist;
            }
            else
            {
                if (bestfacet.furthestdist < bestdist)
                {
                    QSet<double[]>.Append(ref bestfacet.outsideset, point, pool);
                    bestfacet.furthestdist = bestdist;
                }
                else
                    QSet<double[]>.Append2ndLast(ref bestfacet.outsideset, point, pool);
            }
            num_outside++;
        }
        else if (bestdist >= -MAXcoplanar)
        {
            if (KEEPcoplanar || KEEPnearinside || bestdist > max_outside)
                qh_partitioncoplanar(point, bestfacet, true, bestdist);
        }
        else if (KEEPnearinside && bestdist > -NEARinside)
        {
            qh_partitioncoplanar(point, bestfacet, true, bestdist);
        }
        else
        {
            if (KEEPinside)
                qh_partitioncoplanar(point, bestfacet, true, bestdist);
        }
    }

    /// <summary>qh_partitionvisible</summary>
    internal void qh_partitionvisible(bool allpoints, out int numoutside)
    {
        Facet? visible, newfacet;
        double[]? point;
        int size;
        uint count;
        Vertex? vertex;
        if (ONLYmax)
        {
            if (MINoutside < max_vertex)
                MINoutside = max_vertex;
        }
        numoutside = 0;
        for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
        {
            if (visible.outsideset == null && visible.coplanarset == null)
                continue;
            newfacet = visible.f; // f.replace
            count = 0;
            while (newfacet != null && newfacet.visible)
            {
                newfacet = newfacet.f;
                if (count++ > facet_id)
                    throw qh_errexit(qh_ERRqhull, visible, null); // qh_infiniteloop
            }
            newfacet ??= newfacet_list;
            if (visible.outsideset != null)
            {
                size = visible.outsideset.n;
                numoutside += size;
                num_outside -= size;
                QSet<double[]> os = visible.outsideset;
                for (int pi = 0; (point = os.e[pi]) != null; pi++)
                    qh_partitionpoint(point, newfacet!);
            }
            if (visible.coplanarset != null && (KEEPcoplanar || KEEPinside || KEEPnearinside))
            {
                QSet<double[]> cs = visible.coplanarset;
                for (int pi = 0; (point = cs.e[pi]) != null; pi++)
                {
                    if (allpoints)
                        qh_partitionpoint(point, newfacet!);
                    else
                        qh_partitioncoplanar(point, newfacet!, false, 0);
                }
            }
        }
        QSet<Vertex> dv = del_vertices!;
        for (int vi = 0; (vertex = dv.e[vi]) != null; vi++)
        {
            if (vertex.point != null)
            {
                if (allpoints)
                    qh_partitionpoint(vertex.point, newfacet_list!);
                else
                    qh_partitioncoplanar(vertex.point, newfacet_list!, false, 0);
            }
        }
    }

    /// <summary>qh_precision: restart ('QJ' without merging) or nothing.</summary>
    internal void qh_precision(string reason)
    {
        if (ALLOWrestart && !PREmerge && !MERGEexact)
        {
            if (JOGGLEmax < REALmax / 2)
                throw new QhullRestart();
        }
        _ = reason;
    }
}
