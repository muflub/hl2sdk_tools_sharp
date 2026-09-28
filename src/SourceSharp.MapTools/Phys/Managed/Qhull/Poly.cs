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

/// <summary>poly.c</summary>
internal sealed partial class Qh
{
    /// <summary>qh_appendfacet</summary>
    internal void qh_appendfacet(Facet facet)
    {
        Facet tail = facet_tail!;
        if (tail == newfacet_list)
            newfacet_list = facet;
        if (tail == facet_next)
            facet_next = facet;
        facet.previous = tail.previous;
        facet.next = tail;
        if (tail.previous != null)
            tail.previous.next = facet;
        else
            facet_list = facet;
        tail.previous = facet;
        num_facets++;
    }

    /// <summary>qh_appendvertex</summary>
    internal void qh_appendvertex(Vertex vertex)
    {
        Vertex tail = vertex_tail!;
        if (tail == newvertex_list)
            newvertex_list = vertex;
        vertex.newlist = true;
        vertex.previous = tail.previous;
        vertex.next = tail;
        if (tail.previous != null)
            tail.previous.next = vertex;
        else
            vertex_list = vertex;
        tail.previous = vertex;
        num_vertices++;
    }

    /// <summary>qh_checkflipped</summary>
    internal bool qh_checkflipped(Facet facet, bool wantdist, out double distp, bool allerror)
    {
        distp = 0;
        if (facet.flipped && !wantdist)
            return false;
        qh_distplane(interior_point!, facet, out double dist);
        if (wantdist)
            distp = dist;
        if ((allerror && dist > -DISTround) || (!allerror && dist >= 0.0))
        {
            facet.flipped = true;
            qh_precision("flipped facet");
            return false;
        }
        return true;
    }

    /// <summary>qh_delfacet</summary>
    internal void qh_delfacet(Facet facet)
    {
        if (facet == GOODclosest)
            GOODclosest = null;
        qh_removefacet(facet);
        // the memory is freed in C; here the object is dropped and left for the GC
    }

    /// <summary>qh_deletevisible</summary>
    internal void qh_deletevisible()
    {
        Facet? visible, nextfacet;
        int numvisible = 0;
        for (visible = visible_list; visible != null && visible.visible;
             visible = nextfacet)
        {
            nextfacet = visible.next;
            numvisible++;
            qh_delfacet(visible);
        }
        if (numvisible != num_visible)
            throw qh_errexit(qh_ERRqhull, null, null);
        num_visible = 0;
        Vertex? vertex;
        QSet<Vertex> dv = del_vertices!;
        for (int i = 0; (vertex = dv.e[i]) != null; i++)
            qh_delvertex(vertex);
        dv.Truncate(0);
    }

    /// <summary>qh_facetintersect</summary>
    internal QSet<Vertex> qh_facetintersect(Facet facetA, Facet facetB,
        out int skipA, out int skipB, int prepend)
    {
        int dim = hull_dim, i, j;
        Facet?[] neighborsA = facetA.neighbors!.e;
        Facet?[] neighborsB = facetB.neighbors!.e;
        i = j = 0;
        skipA = skipB = 0;
        if (facetB == neighborsA[0])
            skipA = 0;
        else if (facetB == neighborsA[1])
            skipA = 1;
        else if (facetB == neighborsA[2])
            skipA = 2;
        else
        {
            for (i = 3; i < dim; i++)
            {
                if (facetB == neighborsA[i])
                {
                    skipA = i;
                    break;
                }
            }
        }
        if (facetA == neighborsB[0])
            skipB = 0;
        else if (facetA == neighborsB[1])
            skipB = 1;
        else if (facetA == neighborsB[2])
            skipB = 2;
        else
        {
            for (j = 3; j < dim; j++)
            {
                if (facetA == neighborsB[j])
                {
                    skipB = j;
                    break;
                }
            }
        }
        if (i >= dim || j >= dim)
            throw qh_errexit2(qh_ERRqhull, facetA, facetB);
        return facetA.vertices!.NewDelNthSorted(hull_dim, skipA, prepend, pool);
    }

    /// <summary>
    /// qh_gethash. PORT CHANGE: qhull 2.6 sums the vertex POINTERS; this port sums the
    /// vertex ids (the oracle is patched the same way, -DP8AQ_IDHASH). In 3-d the sum
    /// v1 + v2 - skip is the other ridge vertex, so only the table slot changes.
    /// </summary>
    internal static uint qh_gethash(int hashsize, QSet<Vertex> set, int size, int firstindex, Vertex? skipelem)
    {
        Vertex?[] e = set.e;
        int p = firstindex;
        ulong hash = 0, elem;
        ulong skip = K(skipelem);
        switch (size - firstindex)
        {
            case 1:
                hash = K(e[p]) - skip;
                break;
            case 2:
                hash = K(e[p]) + K(e[p + 1]) - skip;
                break;
            case 3:
                hash = K(e[p]) + K(e[p + 1]) + K(e[p + 2])
                       - skip;
                break;
            case 4:
                hash = K(e[p]) + K(e[p + 1]) + K(e[p + 2])
                       + K(e[p + 3]) - skip;
                break;
            case 5:
                hash = K(e[p]) + K(e[p + 1]) + K(e[p + 2])
                       + K(e[p + 3]) + K(e[p + 4]) - skip;
                break;
            case 6:
                hash = K(e[p]) + K(e[p + 1]) + K(e[p + 2])
                       + K(e[p + 3]) + K(e[p + 4]) + K(e[p + 5])
                       - skip;
                break;
            default:
                hash = 0;
                int i = 3;
                do
                {
                    elem = K(e[p]);
                    p++;
                    if (elem != skip)
                    {
                        hash ^= (elem << i) + (elem >> (32 - i));
                        i += 3;
                        if (i >= 32)
                            i -= 32;
                    }
                }
                while (e[p] != null);
                break;
        }
        hash %= (ulong)hashsize;
        return (uint)hash;

        static ulong K(Vertex? v) => v == null ? 0UL : v.id;
    }

    /// <summary>qh_makenewfacet</summary>
    internal Facet qh_makenewfacet(QSet<Vertex> vertices, bool toporient, Facet horizon)
    {
        Vertex? vertex;
        for (int i = 0; (vertex = vertices.e[i]) != null; i++)
        {
            if (!vertex.newlist)
            {
                qh_removevertex(vertex);
                qh_appendvertex(vertex);
            }
        }
        Facet newfacet = qh_newfacet();
        newfacet.vertices = vertices;
        newfacet.toporient = toporient;
        newfacet.neighbors!.Append(horizon);
        qh_appendfacet(newfacet);
        return newfacet;
    }

    /// <summary>qh_makenewplanes</summary>
    internal void qh_makenewplanes()
    {
        for (Facet? newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            if (!newfacet.mergehorizon)
                qh_setfacetplane(newfacet);
        }
        if (JOGGLEmax < REALmax / 2)
        {
            if (min_vertex > -Wnewvertexmax)
                min_vertex = -Wnewvertexmax;
        }
    }

    /// <summary>qh_makenew_nonsimplicial</summary>
    internal Facet? qh_makenew_nonsimplicial(Facet visible, Vertex apex, ref int numnew)
    {
        Ridge? ridge;
        Facet? neighbor, newfacet = null, samecycle;
        QSet<Vertex>? vertices;
        bool toporient;
        QSet<Ridge> vridges = visible.ridges!;
        for (int ri = 0; (ridge = vridges.e[ri]) != null; ri++)
        {
            neighbor = ridge.top == visible ? ridge.bottom! : ridge.top!;
            if (neighbor.visible)
            {
                if (!ONLYgood)
                {
                    if (neighbor.visitid == visit_id)
                    {
                        ridge.vertices = null; // qh_setfree + qh_memfree
                    }
                }
            }
            else
            {
                toporient = ridge.top == visible;
                vertices = QSet<Vertex>.New(hull_dim, pool);
                vertices.Append(apex);
                QSet<Vertex>.AppendSet(ref vertices, ridge.vertices, pool);
                newfacet = qh_makenewfacet(vertices!, toporient, neighbor);
                numnew++;
                if (neighbor.coplanar)
                {
                    newfacet.mergehorizon = true;
                    if (!neighbor.seen)
                    {
                        newfacet.f = newfacet;          // f.samecycle
                        neighbor.f = newfacet;          // f.newcycle
                    }
                    else
                    {
                        samecycle = neighbor.f!;        // f.newcycle
                        newfacet.f = samecycle.f;       // f.samecycle
                        samecycle.f = newfacet;
                    }
                }
                if (ONLYgood)
                {
                    if (!neighbor.simplicial)
                        QSet<Ridge>.Append(ref newfacet.ridges, ridge, pool);
                }
                else
                {
                    if (neighbor.seen)
                    {
                        if (neighbor.simplicial)
                            throw qh_errexit2(qh_ERRqhull, neighbor, visible);
                        QSet<Facet>.Append(ref neighbor.neighbors, newfacet, pool);
                    }
                    else
                        QSet<Facet>.Replace(neighbor.neighbors!, visible, newfacet);
                    if (neighbor.simplicial)
                    {
                        QSet<Ridge>.Del(neighbor.ridges, ridge);
                        ridge.vertices = null; // qh_setfree + qh_memfree
                    }
                    else
                    {
                        QSet<Ridge>.Append(ref newfacet.ridges, ridge, pool);
                        if (toporient)
                            ridge.top = newfacet;
                        else
                            ridge.bottom = newfacet;
                    }
                }
            }
            neighbor.seen = true;
        }
        if (!ONLYgood)
            vridges.e[0] = null; // SETfirst_(visible->ridges)= NULL
        return newfacet;
    }

    /// <summary>qh_makenew_simplicial</summary>
    internal Facet? qh_makenew_simplicial(Facet visible, Vertex apex, ref int numnew)
    {
        Facet? neighbor, newfacet = null;
        bool toporient;
        QSet<Facet> vn = visible.neighbors!;
        for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
        {
            if (!neighbor.seen && !neighbor.visible)
            {
                QSet<Vertex> vertices = qh_facetintersect(neighbor, visible, out int horizonskip, out int visibleskip, 1);
                vertices.e[0] = apex;
                if (neighbor.toporient)
                    toporient = (horizonskip & 0x1) != 0;
                else
                    toporient = ((horizonskip & 0x1) ^ 0x1) != 0;
                newfacet = qh_makenewfacet(vertices!, toporient, neighbor);
                numnew++;
                if (neighbor.coplanar && (PREmerge || MERGEexact))
                {
                    newfacet.f = newfacet; // f.samecycle
                    newfacet.mergehorizon = true;
                }
                if (!ONLYgood)
                    neighbor.neighbors!.e[horizonskip] = newfacet;
                _ = visibleskip;
            }
        }
        return newfacet;
    }

    /// <summary>qh_matchneighbor</summary>
    internal void qh_matchneighbor(Facet newfacet, int newskip, int hashsize, ref int hashcount)
    {
        bool newfound = false;
        bool same, ismatch;
        int hash, scan;
        Facet? facet, matchfacet;
        int skip, matchskip;
        QSet<Facet> table = hash_table!;
        hash = (int)qh_gethash(hashsize, newfacet.vertices!, hull_dim, 1,
            newfacet.vertices!.e[newskip]);
        for (scan = hash; (facet = table.e[scan]) != null;
             scan = (++scan >= hashsize ? 0 : scan))
        {
            if (facet == newfacet)
            {
                newfound = true;
                continue;
            }
            if (qh_matchvertices(1, newfacet.vertices!, newskip, facet.vertices!, out skip, out same))
            {
                if (newfacet.vertices!.e[newskip] == facet.vertices!.e[skip])
                {
                    qh_precision("two facets with the same vertices");
                    throw qh_errexit2(qh_ERRprec, facet, newfacet);
                }
                ismatch = same == (newfacet.toporient ^ facet.toporient);
                matchfacet = facet.neighbors!.e[skip];
                if (ismatch && matchfacet == null)
                {
                    facet.neighbors.e[skip] = newfacet;
                    newfacet.neighbors!.e[newskip] = facet;
                    hashcount--;
                    return;
                }
                if (!PREmerge && !MERGEexact)
                {
                    qh_precision("a ridge with more than two neighbors");
                    throw qh_errexit2(qh_ERRprec, facet, newfacet);
                }
                newfacet.neighbors!.e[newskip] = qh_DUPLICATEridge;
                newfacet.dupridge = true;
                if (newfacet.normal == null)
                    qh_setfacetplane(newfacet);
                qh_addhash(newfacet, table, hashsize, (uint)hash);
                hashcount++;
                if (facet.normal == null)
                    qh_setfacetplane(facet);
                if (matchfacet != qh_DUPLICATEridge)
                {
                    facet.neighbors.e[skip] = qh_DUPLICATEridge;
                    facet.dupridge = true;
                    if (facet.normal == null)
                        qh_setfacetplane(facet);
                    if (matchfacet != null)
                    {
                        matchskip = QSet<Facet>.Index(matchfacet.neighbors, facet);
                        matchfacet.neighbors!.e[matchskip] = qh_DUPLICATEridge;
                        matchfacet.dupridge = true;
                        if (matchfacet.normal == null)
                            qh_setfacetplane(matchfacet);
                        qh_addhash(matchfacet, table, hashsize, (uint)hash);
                        hashcount += 2;
                    }
                }
                return;
            }
        }
        if (!newfound)
            table.e[scan] = newfacet;
        hashcount++;
    }

    /// <summary>qh_matchnewfacets</summary>
    internal void qh_matchnewfacets()
    {
        int numnew = 0, hashcount = 0, newskip;
        Facet? newfacet, neighbor;
        int dim = hull_dim, hashsize;
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            numnew++;
            QSet<Facet> neighbors = newfacet.neighbors!;
            // neighbors->e[neighbors->maxsize].i= dim+1; memset (&e[1], 0, dim)
            if (neighbors.e.Length < dim + 1)
                Array.Resize(ref neighbors.e, dim + 1);
            neighbors.n = dim;
            for (int k = 1; k <= dim; k++)
                neighbors.e[k] = null;
        }
        qh_newhashtable(numnew * (hull_dim - 1));
        hashsize = hash_table!.n;
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            for (newskip = 1; newskip < hull_dim; newskip++)
                qh_matchneighbor(newfacet, newskip, hashsize, ref hashcount);
        }
        if (hashcount != 0)
        {
            for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
            {
                if (newfacet.dupridge)
                {
                    // FOREACHneighbor_i_(newfacet)
                    QSet<Facet> nbs = newfacet.neighbors!;
                    int neighbor_n = nbs.n;
                    for (int neighbor_i = 0; neighbor_i < neighbor_n; neighbor_i++)
                    {
                        neighbor = nbs.e[neighbor_i];
                        if (neighbor == qh_DUPLICATEridge)
                            qh_matchduplicates(newfacet, neighbor_i, hashsize, ref hashcount);
                    }
                }
            }
        }
        if (hashcount != 0)
            throw qh_errexit(qh_ERRqhull, null, null);
        hash_table = null;
        if (PREmerge || MERGEexact)
        {
            for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
            {
                if (newfacet.normal != null)
                    qh_checkflipped(newfacet, false, out _, qh_ALL);
            }
        }
        else if (FORCEoutput)
            throw new NotSupportedException("qhull port: 'Po' is not ported");
    }

    /// <summary>qh_matchvertices</summary>
    internal static bool qh_matchvertices(int firstindex, QSet<Vertex> verticesA, int skipA,
        QSet<Vertex> verticesB, out int skipB, out bool same)
    {
        Vertex?[] A = verticesA.e, B = verticesB.e;
        int a = firstindex, b = firstindex, skipBp = -1;
        skipB = 0;
        same = false;
        do
        {
            if (a != skipA)
            {
                while (A[a] != B[b++])
                {
                    if (skipBp >= 0)
                        return false;
                    skipBp = b;
                }
            }
        }
        while (A[++a] != null);
        if (skipBp < 0)
            skipBp = ++b;
        skipB = skipBp - 1;
        same = ((skipA & 0x1) ^ (skipB & 0x1)) == 0;
        return true;
    }

    /// <summary>qh_newfacet</summary>
    internal Facet qh_newfacet()
    {
        Facet facet;
        facet = pool != null ? pool.NewFacet() : new Facet();
        facet.neighbors = QSet<Facet>.New(hull_dim, pool);
        facet.id = facet_id++;
        facet.furthestdist = 0.0;
        if (FORCEoutput && APPROXhull)
            facet.maxoutside = MINoutside;
        else
            facet.maxoutside = DISTround;
        facet.simplicial = true;
        facet.good = true;
        facet.newfacet = true;
        return facet;
    }

    /// <summary>qh_newridge</summary>
    internal Ridge qh_newridge()
    {
        Ridge ridge = pool != null ? pool.NewRidge() : new Ridge();
        ridge.id = ridge_id++ & 0xFFFFFF;
        return ridge;
    }

    /// <summary>qh_pointid (pointer arithmetic becomes the id slot plus a reference check)</summary>
    internal int qh_pointid(double[]? point)
    {
        if (point == null)
            return -3;
        if (point == interior_point)
            return -2;
        int id = (int)point[hull_dim];
        if (id >= 0 && id < num_points && first_point[id] == point)
            return id;
        int j = QSet<double[]>.Index(other_points, point);
        if (j != -1)
            return j + num_points;
        return -1;
    }

    /// <summary>qh_removefacet</summary>
    internal void qh_removefacet(Facet facet)
    {
        Facet? next = facet.next, previous = facet.previous;
        if (facet == newfacet_list)
            newfacet_list = next;
        if (facet == facet_next)
            facet_next = next;
        if (facet == visible_list)
            visible_list = next;
        if (previous != null)
        {
            previous.next = next;
            next!.previous = previous;
        }
        else
        {
            facet_list = next;
            facet_list!.previous = null;
        }
        num_facets--;
    }

    /// <summary>qh_removevertex</summary>
    internal void qh_removevertex(Vertex vertex)
    {
        Vertex? next = vertex.next, previous = vertex.previous;
        if (vertex == newvertex_list)
            newvertex_list = next;
        if (previous != null)
        {
            previous.next = next;
            next!.previous = previous;
        }
        else
        {
            vertex_list = vertex.next;
            vertex_list!.previous = null;
        }
        num_vertices--;
    }

    /// <summary>qh_updatevertices</summary>
    internal void qh_updatevertices()
    {
        Facet? newfacet, neighbor, visible;
        Vertex? vertex;
        if (VERTEXneighbors)
        {
            for (vertex = newvertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
            {
                QSet<Facet>? vn = vertex.neighbors;
                if (vn != null)
                {
                    for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                    {
                        if (neighbor.visible)
                            vn.e[ni] = null; // SETref_(neighbor)= NULL; the loop goes on to the next slot
                    }
                }
                QSet<Facet>.Compact(vertex.neighbors);
            }
            for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
            {
                QSet<Vertex> nv = newfacet.vertices!;
                for (int vi = 0; (vertex = nv.e[vi]) != null; vi++)
                    QSet<Facet>.Append(ref vertex.neighbors, newfacet, pool);
            }
            for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
            {
                QSet<Vertex> vv = visible.vertices!;
                for (int vi = 0; (vertex = vv.e[vi]) != null; vi++)
                {
                    if (!vertex.newlist && !vertex.deleted)
                    {
                        neighbor = null;
                        QSet<Facet>? vn = vertex.neighbors;
                        if (vn != null)
                        {
                            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                            {
                                if (!neighbor.visible)
                                    break;
                            }
                        }
                        if (neighbor != null)
                            QSet<Facet>.Del(vertex.neighbors, visible);
                        else
                        {
                            vertex.deleted = true;
                            QSet<Vertex>.Append(ref del_vertices, vertex, pool);
                        }
                    }
                }
            }
        }
        else
        {
            for (visible = visible_list; visible != null && visible.visible; visible = visible.next)
            {
                QSet<Vertex> vv = visible.vertices!;
                for (int vi = 0; (vertex = vv.e[vi]) != null; vi++)
                {
                    if (!vertex.newlist && !vertex.deleted)
                    {
                        vertex.deleted = true;
                        QSet<Vertex>.Append(ref del_vertices, vertex, pool);
                    }
                }
            }
        }
    }
}
