// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port (Claude, lane p8a)
// to C# for a managed collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

using static SourceSharp.MapTools.Phys.Managed.Qhull.QhConst;

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>poly2.c</summary>
internal sealed partial class Qh
{
    /// <summary>qh_addhash</summary>
    internal static void qh_addhash(Facet newelem, QSet<Facet> hashtable, int hashsize, uint hash)
    {
        int scan;
        Facet? elem;
        for (scan = (int)hash; (elem = hashtable.e[scan]) != null;
             scan = (++scan >= hashsize ? 0 : scan))
        {
            if (elem == newelem)
                break;
        }
        if (elem == null)
            hashtable.e[scan] = newelem;
    }

    /// <summary>qh_check_maxout</summary>
    internal void qh_check_maxout()
    {
        Facet? facet, bestfacet, neighbor;
        double dist, maxoutside, minvertex;
        double[]? point;
        Vertex? vertex;
        maxoutside = minvertex = 0;
        if (VERTEXneighbors)
        {
            // && (PRINTsummary || KEEPinside || KEEPcoplanar || TRACElevel || PRINTstatistics
            //     || PRINTout[0] == qh_PRINTsummary || PRINTout[0] == qh_PRINTnone): PRINTout[0] is none
            _ = qh_pointvertex();
            for (vertex = vertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
            {
                QSet<Facet>? vn = vertex.neighbors;
                if (vn == null)
                    continue;
                for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                {
                    qh_distplane(vertex.point!, neighbor, out dist);
                    if (minvertex > dist)
                        minvertex = dist;
                }
            }
            min_vertex = minvertex;
        }
        QSet<Facet> facets = qh_pointfacet();
        int facet_n = facets.n;
        for (int facet_i = 0; facet_i < facet_n; facet_i++)
        {
            facet = facets.e[facet_i];
            if (facet != null)
            {
                point = qh_point(facet_i);
                if (point == GOODpointp)
                    continue;
                bool dummy = false;
                bestfacet = qh_findbest(point!, facet, qh_ALL, false, !qh_NOupper,
                    out dist, false, ref dummy, out _);
                if (bestfacet != null && dist > maxoutside)
                {
                    if (ONLYgood && !bestfacet.good)
                        throw new NotSupportedException("qhull port: 'Qg' is not ported");
                    else
                        maxoutside = dist;
                }
            }
        }
        max_outside = maxoutside;
        qh_nearcoplanar();
        maxoutdone = true;
    }

    /// <summary>qh_check_output: with merging and without 'Tv'/'Tc'/tracing it checks nothing.</summary>
    internal void qh_check_output()
    {
        if (STOPcone != 0)
            return;
        if (VERIFYoutput || IStracing != 0 || CHECKfrequently)
            throw new NotSupportedException("qhull port: 'Tv'/'Tc' are not ported");
        else if (!MERGING)
            throw new NotSupportedException("qhull port: qh_check_output without merging is not ported");
    }

    /// <summary>qh_checkconvex</summary>
    internal void qh_checkconvex(Facet? facetlist, int fault)
    {
        Facet? facet, neighbor, errfacet1 = null, errfacet2 = null;
        Vertex vertex;
        double dist;
        double[] centrum;
        bool waserror = false, tempcentrum = false, allsimplicial;
        int neighbor_i;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.flipped)
            {
                qh_precision("flipped facet");
                errfacet1 = facet;
                waserror = true;
                continue;
            }
            if (MERGING && (!ZEROcentrum || !facet.simplicial))
                allsimplicial = false;
            else
            {
                allsimplicial = true;
                neighbor_i = 0;
                QSet<Facet> fn = facet.neighbors!;
                for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                {
                    vertex = facet.vertices!.e[neighbor_i++]!;
                    if (!neighbor.simplicial)
                    {
                        allsimplicial = false;
                        continue;
                    }
                    qh_distplane(vertex.point!, neighbor, out dist);
                    if (dist > -DISTround)
                    {
                        if (fault == qh_DATAfault)
                        {
                            qh_precision("coplanar or concave ridge");
                            throw qh_errexit(qh_ERRsingular, null, null);
                        }
                        if (dist > DISTround)
                        {
                            qh_precision("concave ridge");
                            errfacet1 = facet;
                            errfacet2 = neighbor;
                            waserror = true;
                        }
                        else if (ZEROcentrum)
                        {
                            if (dist > 0)
                            {
                                qh_precision("coplanar ridge");
                                errfacet1 = facet;
                                errfacet2 = neighbor;
                                waserror = true;
                            }
                        }
                        else
                        {
                            qh_precision("coplanar ridge");
                        }
                    }
                }
            }
            if (!allsimplicial)
            {
                if (CENTERtype == QhCenter.qh_AScentrum)
                {
                    facet.center ??= qh_getcentrum(facet);
                    centrum = facet.center;
                }
                else
                {
                    centrum = qh_getcentrum(facet);
                    tempcentrum = true;
                }
                QSet<Facet> fn = facet.neighbors!;
                for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                {
                    if (ZEROcentrum && facet.simplicial && neighbor.simplicial)
                        continue;
                    qh_distplane(centrum, neighbor, out dist);
                    if (dist > DISTround)
                    {
                        qh_precision("concave ridge");
                        errfacet1 = facet;
                        errfacet2 = neighbor;
                        waserror = true;
                    }
                    else if (dist >= 0.0)
                    {
                        qh_precision("coplanar ridge");
                        errfacet1 = facet;
                        errfacet2 = neighbor;
                        waserror = true;
                    }
                }
                _ = tempcentrum;
            }
        }
        if (waserror && !FORCEoutput)
            throw qh_errexit2(qh_ERRprec, errfacet1, errfacet2);
    }

    /// <summary>
    /// qh_checkpolygon: the consistency checks are not ported (they report internal
    /// errors only); the side effects on vertex seen/visitid and qh vertex_visit are.
    /// </summary>
    internal void qh_checkpolygon(Facet facetlist)
    {
        Facet? facet;
        Vertex? vertex, vertexlist;
        int numfacets = 0;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (!facet.visible)
                numfacets++;
        }
        if (facetlist == facet_list)
            vertexlist = vertex_list;
        else if (facetlist == newfacet_list)
            vertexlist = newvertex_list;
        else
            vertexlist = null;
        for (vertex = vertexlist; vertex != null && vertex.next != null; vertex = vertex.next)
        {
            vertex.seen = false;
            vertex.visitid = 0;
        }
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.visible)
                continue;
            QSet<Vertex> fv = facet.vertices!;
            for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            {
                vertex.visitid++;
                if (!vertex.seen)
                    vertex.seen = true;
            }
        }
        vertex_visit += (uint)numfacets;
        if (facetlist == facet_list)
            vertex_visit++;
    }

    /// <summary>qh_clearcenters</summary>
    internal void qh_clearcenters(QhCenter type)
    {
        if (CENTERtype != type)
        {
            for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
                facet.center = null;
            CENTERtype = type;
        }
    }

    /// <summary>qh_createsimplex</summary>
    internal void qh_createsimplex(QSet<Vertex> vertices)
    {
        Facet? facet, newfacet;
        bool toporient = true;
        int nth;
        facet_list = newfacet_list = facet_tail = qh_newfacet();
        num_facets = num_vertices = 0;
        vertex_list = newvertex_list = vertex_tail = qh_newvertex(null);
        int vertex_n = vertices.n;
        for (int vertex_i = 0; vertex_i < vertex_n; vertex_i++)
        {
            Vertex vertex = vertices.e[vertex_i]!;
            newfacet = qh_newfacet();
            newfacet.vertices = vertices.NewDelNthSorted(vertex_n, vertex_i, 0);
            newfacet.toporient = toporient;
            qh_appendfacet(newfacet);
            newfacet.newfacet = true;
            qh_appendvertex(vertex);
            toporient ^= true;
        }
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            nth = 0;
            QSet<Facet> nb = newfacet.neighbors!;
            for (facet = newfacet_list; facet != null && facet.next != null; facet = facet.next)
            {
                if (facet != newfacet)
                {
                    if (nth + 1 >= nb.e.Length)
                        Array.Resize(ref nb.e, nth + 2);
                    nb.e[nth++] = facet;
                }
            }
            nb.Truncate(hull_dim);
        }
    }

    /// <summary>qh_delridge</summary>
    internal static void qh_delridge(Ridge ridge)
    {
        QSet<Ridge>.Del(ridge.top!.ridges, ridge);
        QSet<Ridge>.Del(ridge.bottom!.ridges, ridge);
        ridge.vertices = null;
    }

    /// <summary>qh_delvertex</summary>
    internal void qh_delvertex(Vertex vertex)
    {
        qh_removevertex(vertex);
        vertex.neighbors = null;
    }

    /// <summary>qh_facet3vertex: the facet's vertices in qh_ORIENTclock order.</summary>
    internal QSet<Vertex> qh_facet3vertex(Facet facet)
    {
        Ridge? ridge, firstridge;
        int cntvertices, cntprojected = 0;
        cntvertices = QSet<Vertex>.Size(facet.vertices);
        var vertices = new QSet<Vertex>(cntvertices);
        if (facet.simplicial)
        {
            if (cntvertices != 3)
                throw qh_errexit(qh_ERRqhull, facet, null);
            QSet<Vertex> fv = facet.vertices!;
            vertices.Append(fv.e[0]);
            if (facet.toporient ^ (qh_ORIENTclock != 0))
                vertices.Append(fv.e[1]);
            else
            {
                QSet<Vertex>? v = vertices;
                QSet<Vertex>.AddNth(ref v, 0, fv.e[1]!);
            }
            vertices.Append(fv.e[2]);
        }
        else
        {
            ridge = firstridge = facet.ridges!.e[0];
            while ((ridge = qh_nextridge3d(ridge!, facet, out Vertex? vertex)) != null)
            {
                vertices.Append(vertex);
                if (++cntprojected > cntvertices || ridge == firstridge)
                    break;
            }
            if (ridge == null || cntprojected != cntvertices)
                throw qh_errexit(qh_ERRqhull, facet, ridge);
        }
        return vertices;
    }

    /// <summary>qh_furthestnext</summary>
    internal void qh_furthestnext()
    {
        Facet? bestfacet = null;
        double dist, bestdist = -REALmax;
        for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.outsideset != null)
            {
                dist = facet.furthestdist;
                if (dist > bestdist)
                {
                    bestfacet = facet;
                    bestdist = dist;
                }
            }
        }
        if (bestfacet != null)
        {
            qh_removefacet(bestfacet);
            qh_prependfacet(bestfacet, ref facet_next);
        }
    }

    /// <summary>qh_furthestout</summary>
    internal void qh_furthestout(Facet facet)
    {
        double[]? point, bestpoint = null;
        double bestdist = -REALmax;
        QSet<double[]>? os = facet.outsideset;
        if (os != null)
        {
            for (int pi = 0; (point = os.e[pi]) != null; pi++)
            {
                qh_distplane(point, facet, out double dist);
                if (dist > bestdist)
                {
                    bestpoint = point;
                    bestdist = dist;
                }
            }
        }
        if (bestpoint != null)
        {
            // C: qh_setdel (facet->outsideset, point) with point == NULL at loop exit.
            // qh_setdel (set, NULL) finds the terminator and deletes nothing; then the
            // NULL qh_setappend is ignored. Reproduced as written.
            point = null;
            QSet<double[]>.Del(facet.outsideset, point);
            QSet<double[]>.Append(ref facet.outsideset, point);
            facet.furthestdist = bestdist;
        }
        facet.notfurthest = false;
    }

    /// <summary>qh_initbuild</summary>
    internal void qh_initbuild()
    {
        furthest_id = -1;
        facet_id = vertex_id = ridge_id = 0;
        visit_id = vertex_visit = 0;
        maxoutdone = false;
        if (GOODpoint != 0 || GOODvertex != 0)
            throw new NotSupportedException("qhull port: 'QGn'/'QVn' are not ported");
        QSet<double[]> maxpoints = qh_maxmin(first_point, num_points, hull_dim);
        if (SCALElast)
            throw new NotSupportedException("qhull port: 'Qbb' is not ported");
        qh_detroundoff();
        QSet<Vertex> vertices = qh_initialvertices(hull_dim, maxpoints, first_point, num_points);
        qh_initialhull(vertices);
        qh_partitionall(vertices, first_point, num_points);
        qh_resetlists(false);
        facet_next = facet_list;
        qh_furthestnext();
        if (PREmerge)
        {
            cos_max = premerge_cos;
            centrum_radius = premerge_centrum;
        }
        if (ONLYgood)
            throw new NotSupportedException("qhull port: 'Qg' is not ported");
    }

    /// <summary>qh_initialhull</summary>
    internal void qh_initialhull(QSet<Vertex> vertices)
    {
        Facet? facet, firstfacet, neighbor;
        double dist, angle, minangle = REALmax;
        qh_createsimplex(vertices);
        qh_resetlists(false);
        facet_next = facet_list;
        interior_point = qh_getcenter(vertices);
        firstfacet = facet_list!;
        qh_setfacetplane(firstfacet);
        qh_distplane(interior_point, firstfacet, out dist);
        if (dist > 0)
        {
            for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
                facet.toporient ^= true;
        }
        for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
            qh_setfacetplane(facet);
        for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (!qh_checkflipped(facet, false, out _, qh_ALL))
            {
                facet.flipped = false;
                for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
                {
                    facet.toporient ^= true;
                    qh_orientoutside(facet);
                }
                break;
            }
        }
        for (facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (!qh_checkflipped(facet, false, out _, !qh_ALL))
            {
                qh_precision("initial facet is coplanar with interior point");
                throw qh_errexit(qh_ERRsingular, facet, null);
            }
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
            {
                angle = qh_getangle(facet.normal!, neighbor.normal!);
                if (minangle > angle)
                    minangle = angle;
            }
        }
        if (minangle < qh_MAXnarrow)
        {
            NARROWhull = true;
        }
        qh_checkpolygon(facet_list!);
        qh_checkconvex(facet_list, qh_DATAfault);
    }

    /// <summary>qh_initialvertices ('Qs' searches all points)</summary>
    internal QSet<Vertex> qh_initialvertices(int dim, QSet<double[]> maxpoints, double[][] points, int numpoints)
    {
        QSet<Vertex>? vertices = new QSet<Vertex>(dim + 1);
        QSet<double[]>? simplex = new QSet<double[]>(dim + 1);
        if (ALLpoints)
            qh_maxsimplex(dim, null, points, numpoints, ref simplex);
        else if (RANDOMoutside)
            throw new NotSupportedException("qhull port: 'Qr' is not ported");
        else if (hull_dim >= qh_INITIALmax)
            throw new NotSupportedException("qhull port: dimension >= 8 is not ported");
        else
            qh_maxsimplex(dim, maxpoints, points, numpoints, ref simplex);
        double[]? point;
        for (int pi = 0; (point = simplex!.e[pi]) != null; pi++)
            QSet<Vertex>.AddNth(ref vertices, 0, qh_newvertex(point));
        return vertices!;
    }

    /// <summary>qh_makenewfacets</summary>
    internal Vertex qh_makenewfacets(double[] point)
    {
        Facet? visible, newfacet = null, newfacet2 = null, neighbor;
        Vertex apex;
        int numnew = 0;
        newfacet_list = facet_tail;
        newvertex_list = vertex_tail;
        apex = qh_newvertex(point);
        qh_appendvertex(apex);
        visit_id++;
        if (!ONLYgood)
            NEWfacets = true;
        for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
        {
            QSet<Facet> vn = visible.neighbors!;
            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                neighbor.seen = false;
            if (visible.ridges != null)
            {
                visible.visitid = visit_id;
                newfacet2 = qh_makenew_nonsimplicial(visible, apex, ref numnew);
            }
            if (visible.simplicial)
                newfacet = qh_makenew_simplicial(visible, apex, ref numnew);
            if (!ONLYgood)
            {
                if (newfacet2 != null)
                    newfacet = newfacet2;
                if (newfacet != null)
                    visible.f = newfacet; // f.replace
                vn.e[0] = null; // SETfirst_(visible->neighbors)= NULL
            }
        }
        return apex;
    }

    /// <summary>qh_matchduplicates</summary>
    internal void qh_matchduplicates(Facet atfacet, int atskip, int hashsize, ref int hashcount)
    {
        bool same, ismatch;
        int hash, scan;
        Facet? facet, newfacet, maxmatch = null, maxmatch2 = null, nextfacet;
        int skip, newskip, nextskip = 0, maxskip = 0, maxskip2 = 0, makematch;
        double maxdist = -REALmax, mindist, dist2;
        QSet<Facet> table = hash_table!;
        hash = (int)qh_gethash(hashsize, atfacet.vertices!, hull_dim, 1,
            atfacet.vertices!.e[atskip]);
        for (makematch = 0; makematch < 2; makematch++)
        {
            visit_id++;
            for (newfacet = atfacet, newskip = atskip; newfacet != null; newfacet = nextfacet, newskip = nextskip)
            {
                nextfacet = null;
                newfacet.visitid = visit_id;
                for (scan = hash; (facet = table.e[scan]) != null;
                     scan = (++scan >= hashsize ? 0 : scan))
                {
                    if (!facet.dupridge || facet.visitid == visit_id)
                        continue;
                    if (qh_matchvertices(1, newfacet.vertices!, newskip, facet.vertices!, out skip, out same))
                    {
                        ismatch = same == (newfacet.toporient ^ facet.toporient);
                        if (facet.neighbors!.e[skip] != qh_DUPLICATEridge)
                        {
                            if (makematch == 0)
                                throw qh_errexit2(qh_ERRqhull, facet, newfacet);
                        }
                        else if (ismatch && makematch != 0)
                        {
                            if (newfacet.neighbors!.e[newskip] == qh_DUPLICATEridge)
                            {
                                facet.neighbors.e[skip] = newfacet;
                                newfacet.neighbors.e[newskip] = qh_MERGEridge;
                                hashcount -= 2;
                            }
                        }
                        else if (ismatch)
                        {
                            mindist = qh_getdistance(facet, newfacet, out _, out _);
                            dist2 = qh_getdistance(newfacet, facet, out _, out _);
                            if (mindist > dist2)
                                mindist = dist2;
                            if (mindist > maxdist)
                            {
                                maxdist = mindist;
                                maxmatch = facet;
                                maxskip = skip;
                                maxmatch2 = newfacet;
                                maxskip2 = newskip;
                            }
                        }
                        else
                        {
                            nextfacet = facet;
                            nextskip = skip;
                        }
                    }
                }
            }
            if (makematch == 0)
            {
                if (maxmatch == null)
                    throw qh_errexit(qh_ERRqhull, atfacet, null);
                maxmatch.neighbors!.e[maxskip] = maxmatch2;
                maxmatch2!.neighbors!.e[maxskip2] = maxmatch;
                hashcount -= 2;
                qh_precision("ridge with multiple neighbors");
            }
        }
    }

    /// <summary>qh_nearcoplanar (without 'Qc'/'Qi': drop all coplanar sets)</summary>
    internal void qh_nearcoplanar()
    {
        if (!KEEPcoplanar && !KEEPinside)
        {
            for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
            {
                if (facet.coplanarset != null)
                    facet.coplanarset = null;
            }
        }
        else
            throw new NotSupportedException("qhull port: 'Qc'/'Qi' are not ported");
    }

    /// <summary>qh_newhashtable</summary>
    internal int qh_newhashtable(int newsize)
    {
        int size = ((newsize + 1) * qh_HASHfactor) | 0x1;
        while (true)
        {
            if ((size % 3) != 0 && (size % 5) != 0)
                break;
            size += 2;
        }
        hash_table = new QSet<Facet>(size);
        hash_table.Zero(0, size);
        return size;
    }

    /// <summary>qh_newvertex</summary>
    internal Vertex qh_newvertex(double[]? point)
    {
        Vertex vertex = pool != null ? pool.NewVertex() : new Vertex();
        if (vertex_id == 0xFFFFFF)
            throw qh_errexit(qh_ERRinput, null, null);
        vertex.id = vertex_id++;
        vertex.point = point;
        return vertex;
    }

    /// <summary>qh_nextridge3d</summary>
    internal static Ridge? qh_nextridge3d(Ridge atridge, Facet facet, out Vertex? vertexp)
    {
        Vertex? atvertex, vertex, othervertex;
        Ridge? ridge;
        vertexp = null;
        if ((atridge.top == facet) ^ (qh_ORIENTclock != 0))
            atvertex = atridge.vertices!.e[1];
        else
            atvertex = atridge.vertices!.e[0];
        QSet<Ridge> fr = facet.ridges!;
        for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
        {
            if (ridge == atridge)
                continue;
            if ((ridge.top == facet) ^ (qh_ORIENTclock != 0))
            {
                othervertex = ridge.vertices!.e[1];
                vertex = ridge.vertices.e[0];
            }
            else
            {
                vertex = ridge.vertices!.e[1];
                othervertex = ridge.vertices.e[0];
            }
            if (vertex == atvertex)
            {
                vertexp = othervertex;
                return ridge;
            }
        }
        return null;
    }

    /// <summary>qh_outcoplanar</summary>
    internal void qh_outcoplanar()
    {
        double[]? point;
        for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            QSet<double[]>? os = facet.outsideset;
            if (os != null)
            {
                for (int pi = 0; (point = os.e[pi]) != null; pi++)
                {
                    num_outside--;
                    if (KEEPcoplanar || KEEPnearinside)
                    {
                        qh_distplane(point, facet, out double dist);
                        qh_partitioncoplanar(point, facet, true, dist);
                    }
                }
            }
            facet.outsideset = null;
        }
    }

    /// <summary>qh_point</summary>
    internal double[]? qh_point(int id)
    {
        if (id < 0)
            return null;
        if (id < num_points)
            return first_point[id];
        id -= num_points;
        if (id < QSet<double[]>.Size(other_points))
            return other_points!.e[id];
        return null;
    }

    /// <summary>qh_point_add</summary>
    internal void qh_point_add<T>(QSet<T> set, double[] point, T elem) where T : class
    {
        int id, size;
        size = set.n;
        if ((id = qh_pointid(point)) < 0)
        {
            // "qhull internal warning (point_add): unknown point"
        }
        else if (id >= size)
            throw qh_errexit(qh_ERRqhull, null, null);
        else
            set.e[id] = elem;
    }

    /// <summary>qh_pointfacet</summary>
    internal QSet<Facet> qh_pointfacet()
    {
        int numpoints = num_points + QSet<double[]>.Size(other_points);
        var facets = new QSet<Facet>(numpoints);
        facets.Zero(0, numpoints);
        vertex_visit++;
        Vertex? vertex;
        double[]? point;
        for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            QSet<Vertex> fv = facet.vertices!;
            for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            {
                if (vertex.visitid != vertex_visit)
                {
                    vertex.visitid = vertex_visit;
                    qh_point_add(facets, vertex.point!, facet);
                }
            }
            QSet<double[]>? cs = facet.coplanarset;
            if (cs != null)
            {
                for (int pi = 0; (point = cs.e[pi]) != null; pi++)
                    qh_point_add(facets, point, facet);
            }
            QSet<double[]>? os = facet.outsideset;
            if (os != null)
            {
                for (int pi = 0; (point = os.e[pi]) != null; pi++)
                    qh_point_add(facets, point, facet);
            }
        }
        return facets;
    }

    /// <summary>qh_pointvertex</summary>
    internal QSet<Vertex> qh_pointvertex()
    {
        int numpoints = num_points + QSet<double[]>.Size(other_points);
        var vertices = new QSet<Vertex>(numpoints);
        vertices.Zero(0, numpoints);
        for (Vertex? vertex = vertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
            qh_point_add(vertices, vertex.point!, vertex);
        return vertices;
    }

    /// <summary>qh_prependfacet</summary>
    internal void qh_prependfacet(Facet facet, ref Facet? facetlist)
    {
        // facetlist aliases a qh field (facet_next, visible_list) exactly as the C pointer does
        Facet? prevfacet;
        Facet list = facetlist!;
        prevfacet = list.previous;
        facet.previous = prevfacet;
        if (prevfacet != null)
            prevfacet.next = facet;
        list.previous = facet;
        facet.next = facetlist;
        if (facet_list == list)
            facet_list = facet;
        if (facet_next == list)
            facet_next = facet;
        facetlist = facet;
        num_facets++;
    }

    /// <summary>qh_resetlists</summary>
    internal void qh_resetlists(bool stats)
    {
        Vertex? vertex;
        Facet? newfacet, visible;
        for (vertex = newvertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
            vertex.newlist = false;
        newvertex_list = null;
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
            newfacet.newfacet = false;
        newfacet_list = null;
        for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
        {
            visible.f = null; // f.replace
            visible.visible = false;
        }
        visible_list = null;
        num_visible = 0;
        NEWfacets = false;
    }

    /// <summary>qh_vertexintersect</summary>
    internal void qh_vertexintersect(ref QSet<Vertex> vertexsetA, QSet<Vertex> vertexsetB)
    {
        vertexsetA = qh_vertexintersect_new(vertexsetA, vertexsetB);
    }

    /// <summary>qh_vertexintersect_new</summary>
    internal QSet<Vertex> qh_vertexintersect_new(QSet<Vertex> vertexsetA, QSet<Vertex> vertexsetB)
    {
        var intersection = new QSet<Vertex>(hull_dim - 1);
        Vertex?[] A = vertexsetA.e, B = vertexsetB.e;
        int a = 0, b = 0;
        while (A[a] != null && B[b] != null)
        {
            if (A[a] == B[b])
            {
                intersection.Append(A[a]);
                a++;
                b++;
            }
            else
            {
                if (A[a]!.id > B[b]!.id)
                    a++;
                else
                    b++;
            }
        }
        return intersection;
    }

    /// <summary>qh_vertexneighbors</summary>
    internal void qh_vertexneighbors()
    {
        Vertex? vertex;
        if (VERTEXneighbors)
            return;
        vertex_visit++;
        for (Facet? facet = facet_list; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.visible)
                continue;
            QSet<Vertex> fv = facet.vertices!;
            for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            {
                if (vertex.visitid != vertex_visit)
                {
                    vertex.visitid = vertex_visit;
                    vertex.neighbors = new QSet<Facet>(hull_dim);
                }
                QSet<Facet>.Append(ref vertex.neighbors, facet);
            }
        }
        VERTEXneighbors = true;
    }
}
