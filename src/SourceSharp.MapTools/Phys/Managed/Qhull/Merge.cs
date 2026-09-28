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

/// <summary>merge.c (pre-merging in 3-d: 'C-n' and 'C-0')</summary>
internal sealed partial class Qh
{
    /// <summary>qsort's temporary storage for merges (reused; sorts do not nest).</summary>
    internal MergeT?[]? sortMergeScratch;

    /// <summary>qsort's temporary storage for vertices.</summary>
    internal Vertex?[]? sortVertexScratch;

    /// <summary>qh_premerge</summary>
    internal void qh_premerge(Vertex apex, double maxcentrum, double maxangle)
    {
        bool othermerge = false;
        if (ZEROcentrum && qh_checkzero(!qh_ALL))
            return;
        centrum_radius = maxcentrum;
        cos_max = maxangle;
        degen_mergeset = QSet<MergeT>.New(TEMPsize, pool);
        facet_mergeset = QSet<MergeT>.New(TEMPsize, pool);
        if (hull_dim >= 3)
        {
            qh_mark_dupridges(newfacet_list);
            qh_mergecycle_all(newfacet_list, ref othermerge);
            qh_forcedmerges(ref othermerge);
            for (Facet? newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
            {
                if (!newfacet.simplicial && !newfacet.mergeridge)
                    qh_degen_redundant_neighbors(newfacet, null);
            }
            if (qh_merge_degenredundant() != 0)
                othermerge = true;
        }
        else
            qh_mergecycle_all(newfacet_list, ref othermerge);
        qh_flippedmerges(newfacet_list, ref othermerge);
        if (!MERGEexact || Ztotmerge != 0)
        {
            POSTmerging = false;
            qh_getmergeset_initial(newfacet_list);
            qh_all_merges(othermerge, false);
        }
        facet_mergeset = null;
        degen_mergeset = null;
        _ = apex;
    }

    /// <summary>qh_all_merges</summary>
    internal void qh_all_merges(bool othermerge, bool vneighbors)
    {
        Facet facet1, facet2;
        MergeT? merge;
        bool wasmerge, isreduce;
        MergeType mergetype;
        int numnewmerges = 0;
        while (true)
        {
            wasmerge = false;
            while (QSet<MergeT>.Size(facet_mergeset) != 0)
            {
                while ((merge = QSet<MergeT>.DelLast(facet_mergeset)) != null)
                {
                    facet1 = merge.facet1;
                    facet2 = merge.facet2;
                    mergetype = merge.type;
                    if (facet1.visible || facet2.visible)
                        continue;
                    if ((facet1.newfacet && !facet1.tested)
                        || (facet2.newfacet && !facet2.tested))
                    {
                        if (MERGEindependent && mergetype <= MergeType.MRGanglecoplanar)
                            continue;
                    }
                    qh_merge_nonconvex(facet1, facet2, mergetype);
                    qh_merge_degenredundant();
                    numnewmerges++;
                    wasmerge = true;
                }
                if (POSTmerging && hull_dim <= qh_DIMreduceBuild
                    && numnewmerges > qh_MAXnewmerges)
                {
                    numnewmerges = 0;
                    qh_reducevertices();
                }
                qh_getmergeset(newfacet_list);
            }
            if (VERTEXneighbors)
            {
                isreduce = false;
                if (hull_dim >= 4 && POSTmerging)
                {
                    for (Vertex? vertex = vertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
                        vertex.delridge = true;
                    isreduce = true;
                }
                if ((wasmerge || othermerge) && (!MERGEexact || POSTmerging)
                    && hull_dim <= qh_DIMreduceBuild)
                {
                    othermerge = false;
                    isreduce = true;
                }
                if (isreduce)
                {
                    if (qh_reducevertices())
                    {
                        qh_getmergeset(newfacet_list);
                        continue;
                    }
                }
            }
            if (vneighbors)
                throw new NotSupportedException("qhull port: 'Qv' is not ported");
            break;
        }
    }

    /// <summary>qh_appendmergeset</summary>
    internal void qh_appendmergeset(Facet facet, Facet neighbor, MergeType mergetype, bool hasangle, double angle)
    {
        MergeT? lastmerge;
        if (facet.redundant)
            return;
        if (facet.degenerate && mergetype == MergeType.MRGdegen)
            return;
        MergeT merge = pool != null ? pool.NewMerge() : new MergeT();
        merge.facet1 = facet;
        merge.facet2 = neighbor;
        merge.type = mergetype;
        if (hasangle && ANGLEmerge)
            merge.angle = angle;
        if (mergetype < MergeType.MRGdegen)
            QSet<MergeT>.Append(ref facet_mergeset, merge, pool);
        else if (mergetype == MergeType.MRGdegen)
        {
            facet.degenerate = true;
            if ((lastmerge = QSet<MergeT>.Last(degen_mergeset)) == null
                || lastmerge.type == MergeType.MRGdegen)
                QSet<MergeT>.Append(ref degen_mergeset, merge, pool);
            else
                QSet<MergeT>.AddNth(ref degen_mergeset, 0, merge, pool);
        }
        else
        {
            facet.redundant = true;
            QSet<MergeT>.Append(ref degen_mergeset, merge, pool);
        }
    }

    /// <summary>qh_basevertices</summary>
    internal QSet<Vertex> qh_basevertices(Facet samecycle)
    {
        Facet? same;
        Vertex? apex, vertex;
        var vertices = QSet<Vertex>.New(TEMPsize, pool);
        apex = samecycle.vertices!.e[0]!;
        apex.visitid = ++vertex_visit;
        for (same = samecycle.f; same != null; same = (same == samecycle ? null : same.f))
        {
            if (same.mergeridge)
                continue;
            QSet<Vertex> sv = same.vertices!;
            for (int vi = 0; (vertex = sv.e[vi]) != null; vi++)
            {
                if (vertex.visitid != vertex_visit)
                {
                    vertices.Append(vertex);
                    vertex.visitid = vertex_visit;
                    vertex.seen = false;
                }
            }
        }
        return vertices;
    }

    /// <summary>qh_checkzero</summary>
    internal bool qh_checkzero(bool testall)
    {
        Facet? facet, neighbor = null;
        Facet? horizon, facetlist;
        int neighbor_i;
        Vertex? vertex;
        double dist;
        if (testall)
            facetlist = facet_list;
        else
        {
            facetlist = newfacet_list;
            for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
            {
                horizon = facet.neighbors!.e[0]!;
                if (!horizon.simplicial)
                    goto LABELproblem;
                if (facet.flipped || facet.dupridge || facet.normal == null)
                    goto LABELproblem;
            }
            if (MERGEexact && ZEROall_ok)
                return true;
        }
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            vertex_visit++;
            neighbor_i = 0;
            horizon = null;
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
            {
                if (neighbor_i == 0 && !testall)
                {
                    horizon = neighbor;
                    neighbor_i++;
                    continue;
                }
                vertex = facet.vertices!.e[neighbor_i++]!;
                vertex.visitid = vertex_visit;
                qh_distplane(vertex.point!, neighbor, out dist);
                if (dist >= -DISTround)
                {
                    ZEROall_ok = false;
                    if (!MERGEexact || testall || dist > DISTround)
                        goto LABELnonconvex;
                }
            }
            if (!testall)
            {
                QSet<Vertex> hv = horizon!.vertices!;
                for (int vi = 0; (vertex = hv.e[vi]) != null; vi++)
                {
                    if (vertex.visitid != vertex_visit)
                    {
                        qh_distplane(vertex.point!, facet, out dist);
                        if (dist >= -DISTround)
                        {
                            ZEROall_ok = false;
                            if (!MERGEexact || dist > DISTround)
                                goto LABELnonconvex;
                        }
                        break;
                    }
                }
            }
        }
        return true;
    LABELproblem:
        ZEROall_ok = false;
        return false;
    LABELnonconvex:
        return false;
    }

    /// <summary>qh_copynonconvex</summary>
    internal static void qh_copynonconvex(Ridge atridge)
    {
        Facet facet, otherfacet;
        Ridge? ridge;
        facet = atridge.top!;
        otherfacet = atridge.bottom!;
        QSet<Ridge> fr = facet.ridges!;
        for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
        {
            if (otherfacet == (ridge.top == facet ? ridge.bottom : ridge.top) && ridge != atridge)
            {
                ridge.nonconvex = true;
                break;
            }
        }
    }

    /// <summary>qh_degen_redundant_facet</summary>
    internal void qh_degen_redundant_facet(Facet facet)
    {
        Vertex? vertex;
        Facet? neighbor;
        QSet<Facet> fn = facet.neighbors!;
        for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
        {
            vertex_visit++;
            QSet<Vertex> nv = neighbor.vertices!;
            for (int vi = 0; (vertex = nv.e[vi]) != null; vi++)
                vertex.visitid = vertex_visit;
            QSet<Vertex> fv = facet.vertices!;
            for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            {
                if (vertex.visitid != vertex_visit)
                    break;
            }
            if (vertex == null)
            {
                qh_appendmergeset(facet, neighbor, MergeType.MRGredundant, false, 0);
                return;
            }
        }
        if (QSet<Facet>.Size(facet.neighbors) < hull_dim)
        {
            qh_appendmergeset(facet, facet, MergeType.MRGdegen, false, 0);
        }
    }

    /// <summary>qh_degen_redundant_neighbors</summary>
    internal void qh_degen_redundant_neighbors(Facet facet, Facet? delfacet)
    {
        Vertex? vertex;
        Facet? neighbor;
        if (QSet<Facet>.Size(facet.neighbors) < hull_dim)
        {
            qh_appendmergeset(facet, facet, MergeType.MRGdegen, false, 0);
        }
        delfacet ??= facet;
        vertex_visit++;
        QSet<Vertex> fv = facet.vertices!;
        for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            vertex.visitid = vertex_visit;
        QSet<Facet> dn = delfacet.neighbors!;
        for (int ni = 0; (neighbor = dn.e[ni]) != null; ni++)
        {
            if (neighbor == facet)
                continue;
            QSet<Vertex> nv = neighbor.vertices!;
            for (int vi = 0; (vertex = nv.e[vi]) != null; vi++)
            {
                if (vertex.visitid != vertex_visit)
                    break;
            }
            if (vertex == null)
            {
                qh_appendmergeset(neighbor, facet, MergeType.MRGredundant, false, 0);
            }
        }
        for (int ni = 0; (neighbor = dn.e[ni]) != null; ni++)
        {
            if (neighbor == facet)
                continue;
            if (QSet<Facet>.Size(neighbor.neighbors) < hull_dim)
            {
                qh_appendmergeset(neighbor, neighbor, MergeType.MRGdegen, false, 0);
            }
        }
    }

    /// <summary>qh_find_newvertex</summary>
    internal Vertex? qh_find_newvertex(Vertex oldvertex, QSet<Vertex> vertices, QSet<Ridge> ridges)
    {
        Vertex? vertex;
        Ridge? ridge;
        int hashsize;
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
            vertex.visitid = 0;
        for (int ri = 0; (ridge = ridges.e[ri]) != null; ri++)
        {
            QSet<Vertex> rv = ridge.vertices!;
            for (int vi = 0; (vertex = rv.e[vi]) != null; vi++)
                vertex.visitid++;
        }
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
        {
            if (vertex.visitid == 0)
            {
                vertices.DelNth(vi);
                vi--;
            }
        }
        vertex_visit += (uint)ridges.n;
        if (vertices.n == 0)
            return null;
        GlibcMsort.Sort(vertices.e, vertices.n, new CompareVisit(), ref sortVertexScratch);
        hashsize = qh_newhashtable_ridges(ridges.n);
        for (int ri = 0; (ridge = ridges.e[ri]) != null; ri++)
            qh_hashridge(ridge_hash_table!, hashsize, ridge, oldvertex);
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
        {
            QSet<Ridge> newridges = qh_vertexridges(vertex);
            for (int ri = 0; (ridge = newridges.e[ri]) != null; ri++)
            {
                if (qh_hashridge_find(ridge_hash_table!, hashsize, ridge, vertex, oldvertex, out _) != null)
                    break;
            }
            if (ridge == null)
                break;
        }
        ridge_hash_table = null;
        return vertex;
    }

    /// <summary>qh_newhashtable for qh_find_newvertex (the same table, holding ridges)</summary>
    internal int qh_newhashtable_ridges(int newsize)
    {
        int size = ((newsize + 1) * qh_HASHfactor) | 0x1;
        while (true)
        {
            if ((size % 3) != 0 && (size % 5) != 0)
                break;
            size += 2;
        }
        ridge_hash_table = QSet<Ridge>.New(size, pool);
        ridge_hash_table.Zero(0, size);
        return size;
    }

    /// <summary>qh_findbest_test</summary>
    internal void qh_findbest_test(bool testcentrum, Facet facet, Facet neighbor,
        ref Facet? bestfacet, ref double distp, ref double mindistp, ref double maxdistp)
    {
        double dist, mindist, maxdist;
        if (testcentrum)
        {
            qh_distplane(facet.center!, neighbor, out dist);
            dist *= hull_dim;
            if (dist < 0)
            {
                maxdist = 0;
                mindist = dist;
                dist = -dist;
            }
            else
            {
                maxdist = dist;
                mindist = 0; // uninitialised in C; only read when dist < *distp
            }
        }
        else
            dist = qh_getdistance(facet, neighbor, out mindist, out maxdist);
        if (dist < distp)
        {
            bestfacet = neighbor;
            mindistp = mindist;
            maxdistp = maxdist;
            distp = dist;
        }
    }

    /// <summary>qh_findbestneighbor</summary>
    internal Facet qh_findbestneighbor(Facet facet, out double distp, out double mindistp, out double maxdistp)
    {
        Facet? neighbor, bestfacet = null;
        Ridge? ridge;
        bool testcentrum = false;
        int size = QSet<Vertex>.Size(facet.vertices);
        distp = REALmax;
        mindistp = 0;
        maxdistp = 0;
        if (size > qh_BESTcentrum2 * hull_dim + qh_BESTcentrum)
        {
            testcentrum = true;
            facet.center ??= qh_getcentrum(facet);
        }
        if (size > hull_dim + qh_BESTnonconvex)
        {
            QSet<Ridge>? fr = facet.ridges;
            if (fr != null)
            {
                for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
                {
                    if (ridge.nonconvex)
                    {
                        neighbor = ridge.top == facet ? ridge.bottom! : ridge.top!;
                        qh_findbest_test(testcentrum, facet, neighbor,
                            ref bestfacet, ref distp, ref mindistp, ref maxdistp);
                    }
                }
            }
        }
        if (bestfacet == null)
        {
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                qh_findbest_test(testcentrum, facet, neighbor,
                    ref bestfacet, ref distp, ref mindistp, ref maxdistp);
        }
        if (bestfacet == null)
            throw qh_errexit(qh_ERRqhull, facet, null);
        if (testcentrum)
            qh_getdistance(facet, bestfacet, out mindistp, out maxdistp);
        return bestfacet;
    }

    /// <summary>qh_flippedmerges</summary>
    internal void qh_flippedmerges(Facet? facetlist, ref bool wasmerge)
    {
        Facet? facet, neighbor, facet1;
        MergeT? merge;
        int nummerge = 0;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.flipped && !facet.visible)
                qh_appendmergeset(facet, facet, MergeType.MRGflip, false, 0);
        }
        // othermerges= qh_settemppop(); qh facet_mergeset= qh_settemp(); qh_settemppush(othermerges)
        QSet<MergeT> othermerges = facet_mergeset!;
        facet_mergeset = QSet<MergeT>.New(TEMPsize, pool);
        for (int mi = 0; (merge = othermerges.e[mi]) != null; mi++)
        {
            facet1 = merge.facet1;
            if (merge.type != MergeType.MRGflip || facet1.visible)
                continue;
            neighbor = qh_findbestneighbor(facet1, out _, out double mindist, out double maxdist);
            qh_mergefacet(facet1, neighbor, true, mindist, maxdist, !qh_MERGEapex);
            nummerge++;
            qh_merge_degenredundant();
        }
        for (int mi = 0; (merge = othermerges.e[mi]) != null; mi++)
        {
            if (merge.facet1.visible || merge.facet2.visible)
            {
                // qh_memfree (merge)
            }
            else
                QSet<MergeT>.Append(ref facet_mergeset, merge, pool);
        }
        if (nummerge != 0)
            wasmerge = true;
    }

    /// <summary>qh_forcedmerges</summary>
    internal void qh_forcedmerges(ref bool wasmerge)
    {
        Facet facet1, facet2;
        MergeT? merge;
        double dist1, dist2, mindist1, mindist2, maxdist1, maxdist2;
        int nummerge = 0;
        QSet<MergeT> othermerges = facet_mergeset!;
        facet_mergeset = QSet<MergeT>.New(TEMPsize, pool);
        for (int mi = 0; (merge = othermerges.e[mi]) != null; mi++)
        {
            if (merge.type != MergeType.MRGridge)
                continue;
            facet1 = merge.facet1;
            facet2 = merge.facet2;
            while (facet1.visible)
                facet1 = facet1.f!; // f.replace
            while (facet2.visible)
                facet2 = facet2.f!;
            if (facet1 == facet2)
                continue;
            if (!QSet<Facet>.In(facet2.neighbors, facet1))
                throw qh_errexit2(qh_ERRqhull, facet1, facet2);
            dist1 = qh_getdistance(facet1, facet2, out mindist1, out maxdist1);
            dist2 = qh_getdistance(facet2, facet1, out mindist2, out maxdist2);
            if (dist1 < dist2)
                qh_mergefacet(facet1, facet2, true, mindist1, maxdist1, !qh_MERGEapex);
            else
            {
                qh_mergefacet(facet2, facet1, true, mindist2, maxdist2, !qh_MERGEapex);
                facet1 = facet2;
            }
            if (!facet1.flipped)
                nummerge++;
        }
        for (int mi = 0; (merge = othermerges.e[mi]) != null; mi++)
        {
            if (merge.type == MergeType.MRGridge)
            {
                // qh_memfree (merge)
            }
            else
                QSet<MergeT>.Append(ref facet_mergeset, merge, pool);
        }
        if (nummerge != 0)
            wasmerge = true;
    }

    /// <summary>qh_getmergeset</summary>
    internal void qh_getmergeset(Facet? facetlist)
    {
        Facet? facet, neighbor;
        Ridge? ridge;
        visit_id++;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.tested)
                continue;
            facet.visitid = visit_id;
            facet.tested = true;
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                neighbor.seen = false;
            QSet<Ridge>? fr = facet.ridges;
            if (fr != null)
            {
                for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
                {
                    if (ridge.tested && !ridge.nonconvex)
                        continue;
                    neighbor = ridge.top == facet ? ridge.bottom! : ridge.top!;
                    if (neighbor.seen)
                    {
                        ridge.tested = true;
                        ridge.nonconvex = false;
                    }
                    else if (neighbor.visitid != visit_id)
                    {
                        ridge.tested = true;
                        ridge.nonconvex = false;
                        neighbor.seen = true;
                        if (qh_test_appendmerge(facet, neighbor))
                            ridge.nonconvex = true;
                    }
                }
            }
        }
        SortMergeset();
    }

    /// <summary>The qsort at the end of qh_getmergeset[_initial] (glibc's merge sort, see GlibcMsort).</summary>
    private void SortMergeset()
    {
        QSet<MergeT>? ms = facet_mergeset;
        if (ms == null)
            return;
        if (ANGLEmerge)
            GlibcMsort.Sort(ms.e, ms.n, new CompareAngle(), ref sortMergeScratch);
        else
            GlibcMsort.Sort(ms.e, ms.n, new CompareMerge(), ref sortMergeScratch);
    }

    /// <summary>qh_getmergeset_initial</summary>
    internal void qh_getmergeset_initial(Facet? facetlist)
    {
        Facet? facet, neighbor;
        Ridge? ridge;
        visit_id++;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            facet.visitid = visit_id;
            facet.tested = true;
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
            {
                if (neighbor.visitid != visit_id)
                {
                    if (qh_test_appendmerge(facet, neighbor))
                    {
                        QSet<Ridge>? nr = neighbor.ridges;
                        if (nr != null)
                        {
                            for (int ri = 0; (ridge = nr.e[ri]) != null; ri++)
                            {
                                if (facet == (ridge.top == neighbor ? ridge.bottom : ridge.top))
                                {
                                    ridge.nonconvex = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            QSet<Ridge>? fr = facet.ridges;
            if (fr != null)
            {
                for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
                    ridge.tested = true;
            }
        }
        SortMergeset();
    }

    /// <summary>qh_hashridge</summary>
    internal void qh_hashridge(QSet<Ridge> hashtable, int hashsize, Ridge ridge, Vertex oldvertex)
    {
        int hash;
        Ridge? ridgeA;
        hash = (int)qh_gethash(hashsize, ridge.vertices!, hull_dim - 1, 0, oldvertex);
        while (true)
        {
            if ((ridgeA = hashtable.e[hash]) == null)
            {
                hashtable.e[hash] = ridge;
                break;
            }
            else if (ridgeA == ridge)
                break;
            if (++hash == hashsize)
                hash = 0;
        }
    }

    /// <summary>qh_hashridge_find</summary>
    internal Ridge? qh_hashridge_find(QSet<Ridge> hashtable, int hashsize, Ridge ridge,
        Vertex vertex, Vertex oldvertex, out int hashslot)
    {
        int hash;
        Ridge? ridgeA;
        hashslot = 0;
        hash = (int)qh_gethash(hashsize, ridge.vertices!, hull_dim - 1, 0, vertex);
        while ((ridgeA = hashtable.e[hash]) != null)
        {
            if (ridgeA == ridge)
                hashslot = -1;
            else
            {
                if (QSet<Vertex>.EqualExcept(ridge.vertices!, vertex, ridgeA.vertices!, oldvertex))
                    return ridgeA;
            }
            if (++hash == hashsize)
                hash = 0;
        }
        if (hashslot == 0)
            hashslot = hash;
        return null;
    }

    /// <summary>qh_makeridges</summary>
    internal void qh_makeridges(Facet facet)
    {
        Facet? neighbor;
        Ridge? ridge;
        bool toporient, mergeridge = false;
        if (!facet.simplicial)
            return;
        facet.simplicial = false;
        QSet<Facet> fn = facet.neighbors!;
        for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
        {
            if (neighbor == qh_MERGEridge)
                mergeridge = true;
            else
                neighbor.seen = false;
        }
        QSet<Ridge>? fr = facet.ridges;
        if (fr != null)
        {
            for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
                (ridge.top == facet ? ridge.bottom! : ridge.top!).seen = true;
        }
        int neighbor_n = fn.n;
        for (int neighbor_i = 0; neighbor_i < neighbor_n; neighbor_i++)
        {
            neighbor = fn.e[neighbor_i];
            if (neighbor == qh_MERGEridge)
                continue;
            else if (!neighbor!.seen)
            {
                ridge = qh_newridge();
                ridge.vertices = facet.vertices!.NewDelNthSorted(hull_dim, neighbor_i, 0, pool);
                toporient = facet.toporient ^ ((neighbor_i & 0x1) != 0);
                if (toporient)
                {
                    ridge.top = facet;
                    ridge.bottom = neighbor;
                }
                else
                {
                    ridge.top = neighbor;
                    ridge.bottom = facet;
                }
                QSet<Ridge>.Append(ref facet.ridges, ridge, pool);
                QSet<Ridge>.Append(ref neighbor.ridges, ridge, pool);
            }
        }
        if (mergeridge)
        {
            while (QSet<Facet>.Del(facet.neighbors, qh_MERGEridge) != null)
            {
            }
        }
    }

    /// <summary>qh_mark_dupridges</summary>
    internal void qh_mark_dupridges(Facet? facetlist)
    {
        Facet? facet, neighbor;
        int nummerge = 0;
        MergeT? merge;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.dupridge)
            {
                QSet<Facet> fn = facet.neighbors!;
                for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                {
                    if (neighbor == qh_MERGEridge)
                    {
                        facet.mergeridge = true;
                        continue;
                    }
                    if (neighbor.dupridge
                        && !QSet<Facet>.In(neighbor.neighbors, facet))
                    {
                        qh_appendmergeset(facet, neighbor, MergeType.MRGridge, false, 0);
                        facet.mergeridge2 = true;
                        facet.mergeridge = true;
                        nummerge++;
                    }
                }
            }
        }
        if (nummerge == 0)
            return;
        for (facet = facetlist; facet != null && facet.next != null; facet = facet.next)
        {
            if (facet.mergeridge && !facet.mergeridge2)
                qh_makeridges(facet);
        }
        QSet<MergeT> ms = facet_mergeset!;
        for (int mi = 0; (merge = ms.e[mi]) != null; mi++)
        {
            if (merge.type == MergeType.MRGridge)
            {
                QSet<Facet>.Append(ref merge.facet2.neighbors, merge.facet1, pool);
                qh_makeridges(merge.facet1);
            }
        }
    }

    /// <summary>qh_maydropneighbor</summary>
    internal void qh_maydropneighbor(Facet facet)
    {
        Ridge? ridge;
        double angledegen = qh_ANGLEdegen;
        Facet? neighbor;
        visit_id++;
        QSet<Ridge>? fr = facet.ridges;
        if (fr != null)
        {
            for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
            {
                ridge.top!.visitid = visit_id;
                ridge.bottom!.visitid = visit_id;
            }
        }
        QSet<Facet> fn = facet.neighbors!;
        for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
        {
            if (neighbor.visitid != visit_id)
            {
                QSet<Facet>.Del(facet.neighbors, neighbor);
                ni--;
                QSet<Facet>.Del(neighbor.neighbors, facet);
                if (QSet<Facet>.Size(neighbor.neighbors) < hull_dim)
                {
                    qh_appendmergeset(neighbor, neighbor, MergeType.MRGdegen, true, angledegen);
                }
            }
        }
        if (QSet<Facet>.Size(facet.neighbors) < hull_dim)
        {
            qh_appendmergeset(facet, facet, MergeType.MRGdegen, true, angledegen);
        }
    }

    /// <summary>qh_merge_degenredundant</summary>
    internal int qh_merge_degenredundant()
    {
        int size;
        MergeT? merge;
        Facet bestneighbor, facet1, facet2;
        Vertex? vertex;
        int nummerges = 0;
        MergeType mergetype;
        while ((merge = QSet<MergeT>.DelLast(degen_mergeset)) != null)
        {
            facet1 = merge.facet1;
            facet2 = merge.facet2;
            mergetype = merge.type;
            if (facet1.visible)
                continue;
            facet1.degenerate = false;
            facet1.redundant = false;
            if (mergetype == MergeType.MRGredundant)
            {
                while (facet2.visible)
                {
                    if (facet2.f == null)
                        throw qh_errexit2(qh_ERRqhull, facet1, facet2);
                    facet2 = facet2.f; // f.replace
                }
                if (facet1 == facet2)
                {
                    qh_degen_redundant_facet(facet1);
                    continue;
                }
                qh_mergefacet(facet1, facet2, false, 0, 0, !qh_MERGEapex);
                nummerges++;
            }
            else
            {
                if ((size = QSet<Facet>.Size(facet1.neighbors)) == 0)
                {
                    qh_willdelete(facet1, null);
                    QSet<Vertex> fv = facet1.vertices!;
                    for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
                    {
                        QSet<Facet>.Del(vertex.neighbors, facet1);
                        if (vertex.neighbors!.e[0] == null)
                        {
                            vertex.deleted = true;
                            QSet<Vertex>.Append(ref del_vertices, vertex, pool);
                        }
                    }
                    nummerges++;
                }
                else if (size < hull_dim)
                {
                    bestneighbor = qh_findbestneighbor(facet1, out _, out double mindist, out double maxdist);
                    qh_mergefacet(facet1, bestneighbor, true, mindist, maxdist, !qh_MERGEapex);
                    nummerges++;
                }
            }
        }
        return nummerges;
    }

    /// <summary>qh_merge_nonconvex</summary>
    internal void qh_merge_nonconvex(Facet facet1, Facet facet2, MergeType mergetype)
    {
        Facet bestfacet, bestneighbor, neighbor;
        double dist, dist2, mindist, mindist2, maxdist, maxdist2;
        if (!facet1.newfacet)
        {
            bestfacet = facet2;
            facet2 = facet1;
            facet1 = bestfacet;
        }
        else
            bestfacet = facet1;
        bestneighbor = qh_findbestneighbor(bestfacet, out dist, out mindist, out maxdist);
        neighbor = qh_findbestneighbor(facet2, out dist2, out mindist2, out maxdist2);
        if (dist < dist2)
        {
            qh_mergefacet(bestfacet, bestneighbor, true, mindist, maxdist, !qh_MERGEapex);
        }
        else if (AVOIDold && !facet2.newfacet
                 && ((mindist >= -MAXcoplanar && maxdist <= max_outside)
                     || dist * 1.5 < dist2))
        {
            qh_mergefacet(bestfacet, bestneighbor, true, mindist, maxdist, !qh_MERGEapex);
        }
        else
        {
            qh_mergefacet(facet2, neighbor, true, mindist2, maxdist2, !qh_MERGEapex);
        }
        _ = mergetype;
    }

    /// <summary>qh_mergecycle</summary>
    internal void qh_mergecycle(Facet samecycle, Facet newfacet)
    {
        Vertex apex;
        if (!VERTEXneighbors)
            qh_vertexneighbors();
        Ztotmerge++;
        apex = samecycle.vertices!.e[0]!;
        qh_makeridges(newfacet);
        qh_mergecycle_neighbors(samecycle, newfacet);
        qh_mergecycle_ridges(samecycle, newfacet);
        qh_mergecycle_vneighbors(samecycle, newfacet);
        if (newfacet.vertices!.e[0] != apex)
            QSet<Vertex>.AddNth(ref newfacet.vertices, 0, apex, pool);
        if (!newfacet.newfacet)
            qh_newvertices(newfacet.vertices!);
        qh_mergecycle_facets(samecycle, newfacet);
    }

    /// <summary>qh_mergecycle_all</summary>
    internal void qh_mergecycle_all(Facet? facetlist, ref bool wasmerge)
    {
        Facet? facet, same, prev, horizon;
        Facet? samecycle, nextfacet, nextsame;
        Vertex? apex, vertex;
        int cycles = 0, facets, nummerge;
        for (facet = facetlist; facet != null && (nextfacet = facet.next) != null; facet = nextfacet)
        {
            if (facet.normal != null)
                continue;
            if (!facet.mergehorizon)
                throw qh_errexit(qh_ERRqhull, facet, null);
            horizon = facet.neighbors!.e[0]!;
            if (facet.f == facet) // f.samecycle
            {
                apex = facet.vertices!.e[0]!;
                QSet<Vertex> fv = facet.vertices;
                for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
                {
                    if (vertex != apex)
                        vertex.delridge = true;
                }
                horizon.f = null; // f.newcycle
                qh_mergefacet(facet, horizon, false, 0, 0, qh_MERGEapex);
            }
            else
            {
                samecycle = facet;
                facets = 0;
                prev = facet;
                for (same = facet.f; same != null;
                     same = (same == facet ? null : nextsame))
                {
                    nextsame = same.f;
                    if (same.cycledone || same.visible)
                        throw qh_errexit(qh_ERRqhull, same, null); // qh_infiniteloop
                    same.cycledone = true;
                    if (same.normal != null)
                    {
                        prev.f = same.f;
                        same.f = null;
                    }
                    else
                    {
                        prev = same;
                        facets++;
                    }
                }
                while (nextfacet != null && nextfacet.cycledone)
                    nextfacet = nextfacet.next;
                horizon.f = null; // f.newcycle
                qh_mergecycle(samecycle, horizon);
                nummerge = horizon.nummerge + facets;
                if (nummerge > qh_MAXnummerge)
                    horizon.nummerge = qh_MAXnummerge;
                else
                    horizon.nummerge = nummerge;
            }
            cycles++;
        }
        if (cycles != 0)
            wasmerge = true;
    }

    /// <summary>qh_mergecycle_facets</summary>
    internal void qh_mergecycle_facets(Facet samecycle, Facet newfacet)
    {
        Facet? same, next;
        qh_removefacet(newfacet);
        qh_appendfacet(newfacet);
        newfacet.newfacet = true;
        newfacet.simplicial = false;
        newfacet.newmerge = true;
        for (same = samecycle.f; same != null; same = (same == samecycle ? null : next))
        {
            next = same.f;
            qh_willdelete(same, newfacet);
        }
        if (newfacet.center != null
            && QSet<Vertex>.Size(newfacet.vertices) <= hull_dim + qh_MAXnewcentrum)
        {
            newfacet.center = null;
        }
    }

    /// <summary>qh_mergecycle_neighbors</summary>
    internal void qh_mergecycle_neighbors(Facet samecycle, Facet newfacet)
    {
        Facet? same, neighbor;
        uint samevisitid;
        Ridge? ridge;
        samevisitid = ++visit_id;
        for (same = samecycle.f; same != null; same = (same == samecycle ? null : same.f))
        {
            if (same.visitid == samevisitid || same.visible)
                throw qh_errexit(qh_ERRqhull, samecycle, null); // qh_infiniteloop
            same.visitid = samevisitid;
        }
        newfacet.visitid = ++visit_id;
        QSet<Facet> nfn = newfacet.neighbors!;
        for (int ni = 0; (neighbor = nfn.e[ni]) != null; ni++)
        {
            if (neighbor.visitid == samevisitid)
            {
                nfn.e[ni] = null; // SETref_(neighbor)= NULL
            }
            else
                neighbor.visitid = visit_id;
        }
        QSet<Facet>.Compact(newfacet.neighbors);
        for (same = samecycle.f; same != null; same = (same == samecycle ? null : same.f))
        {
            QSet<Facet> sn = same.neighbors!;
            for (int ni = 0; (neighbor = sn.e[ni]) != null; ni++)
            {
                if (neighbor.visitid == samevisitid)
                    continue;
                if (neighbor.simplicial)
                {
                    if (neighbor.visitid != visit_id)
                    {
                        QSet<Facet>.Append(ref newfacet.neighbors, neighbor, pool);
                        QSet<Facet>.Replace(neighbor.neighbors!, same, newfacet);
                        neighbor.visitid = visit_id;
                        QSet<Ridge>? nr = neighbor.ridges;
                        if (nr != null)
                        {
                            for (int ri = 0; (ridge = nr.e[ri]) != null; ri++)
                            {
                                if (ridge.top == same)
                                {
                                    ridge.top = newfacet;
                                    break;
                                }
                                else if (ridge.bottom == same)
                                {
                                    ridge.bottom = newfacet;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        qh_makeridges(neighbor);
                        QSet<Facet>.Del(neighbor.neighbors, same);
                    }
                }
                else
                {
                    QSet<Facet>.Del(neighbor.neighbors, same);
                    if (neighbor.visitid != visit_id)
                    {
                        QSet<Facet>.Append(ref neighbor.neighbors, newfacet, pool);
                        QSet<Facet>.Append(ref newfacet.neighbors, neighbor, pool);
                        neighbor.visitid = visit_id;
                    }
                }
            }
        }
    }

    /// <summary>qh_mergecycle_ridges</summary>
    internal void qh_mergecycle_ridges(Facet samecycle, Facet newfacet)
    {
        Facet? same, neighbor = null;
        uint samevisitid;
        Ridge? ridge;
        bool toporient;
        samevisitid = visit_id - 1;
        QSet<Ridge>? nfr = newfacet.ridges;
        if (nfr != null)
        {
            for (int ri = 0; (ridge = nfr.e[ri]) != null; ri++)
            {
                neighbor = ridge.top == newfacet ? ridge.bottom! : ridge.top!;
                if (neighbor.visitid == samevisitid)
                    nfr.e[ri] = null; // SETref_(ridge)= NULL
            }
        }
        QSet<Ridge>.Compact(newfacet.ridges);
        for (same = samecycle.f; same != null; same = (same == samecycle ? null : same.f))
        {
            QSet<Ridge>? sr = same.ridges;
            if (sr != null)
            {
                for (int ri = 0; (ridge = sr.e[ri]) != null; ri++)
                {
                    if (ridge.top == same)
                    {
                        ridge.top = newfacet;
                        neighbor = ridge.bottom!;
                    }
                    else if (ridge.bottom == same)
                    {
                        ridge.bottom = newfacet;
                        neighbor = ridge.top!;
                    }
                    else if (ridge.top == newfacet || ridge.bottom == newfacet)
                    {
                        QSet<Ridge>.Append(ref newfacet.ridges, ridge, pool);
                        continue;
                    }
                    else
                        throw qh_errexit(qh_ERRqhull, null, ridge);
                    if (neighbor == newfacet)
                    {
                        ridge.vertices = null; // qh_setfree + qh_memfree
                    }
                    else if (neighbor.visitid == samevisitid)
                    {
                        QSet<Ridge>.Del(neighbor.ridges, ridge);
                        ridge.vertices = null;
                    }
                    else
                    {
                        QSet<Ridge>.Append(ref newfacet.ridges, ridge, pool);
                    }
                }
            }
            same.ridges?.Truncate(0);
            if (!same.simplicial)
                continue;
            QSet<Facet> sn = same.neighbors!;
            int neighbor_n = sn.n;
            for (int neighbor_i = 0; neighbor_i < neighbor_n; neighbor_i++)
            {
                neighbor = sn.e[neighbor_i]!;
                if (neighbor.visitid != samevisitid && neighbor.simplicial)
                {
                    ridge = qh_newridge();
                    ridge.vertices = same.vertices!.NewDelNthSorted(hull_dim, neighbor_i, 0, pool);
                    toporient = same.toporient ^ ((neighbor_i & 0x1) != 0);
                    if (toporient)
                    {
                        ridge.top = newfacet;
                        ridge.bottom = neighbor;
                    }
                    else
                    {
                        ridge.top = neighbor;
                        ridge.bottom = newfacet;
                    }
                    QSet<Ridge>.Append(ref newfacet.ridges, ridge, pool);
                    QSet<Ridge>.Append(ref neighbor.ridges, ridge, pool);
                }
            }
        }
    }

    /// <summary>qh_mergecycle_vneighbors</summary>
    internal void qh_mergecycle_vneighbors(Facet samecycle, Facet newfacet)
    {
        Facet? neighbor;
        uint mergeid;
        Vertex? vertex, apex;
        QSet<Vertex> vertices;
        mergeid = visit_id - 1;
        newfacet.visitid = mergeid;
        vertices = qh_basevertices(samecycle);
        apex = samecycle.vertices!.e[0]!;
        vertices.Append(apex);
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
        {
            vertex.delridge = true;
            QSet<Facet>? vn = vertex.neighbors;
            if (vn != null)
            {
                for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                {
                    if (neighbor.visitid == mergeid)
                        vn.e[ni] = null; // SETref_(neighbor)= NULL
                }
            }
            QSet<Facet>.Compact(vertex.neighbors);
            QSet<Facet>.Append(ref vertex.neighbors, newfacet, pool);
            if (vertex.neighbors!.Second == null)
            {
                QSet<Vertex>.DelSorted(newfacet.vertices, vertex);
                vertex.deleted = true;
                QSet<Vertex>.Append(ref del_vertices, vertex, pool);
            }
        }
    }

    /// <summary>qh_mergefacet (mindist/maxdist NULL in C when hasdist is false)</summary>
    internal void qh_mergefacet(Facet facet1, Facet facet2, bool hasdist, double mindist, double maxdist, bool mergeapex)
    {
        Vertex? vertex;
        int nummerge;
        Ztotmerge++;
        if (facet1 == facet2 || facet1.visible || facet2.visible)
            throw qh_errexit2(qh_ERRqhull, facet1, facet2);
        if (num_facets - num_visible <= hull_dim + 1)
            throw qh_errexit(qh_ERRinput, null, null);
        if (!VERTEXneighbors)
            qh_vertexneighbors();
        qh_makeridges(facet1);
        qh_makeridges(facet2);
        if (hasdist)
        {
            if (max_outside < maxdist)
                max_outside = maxdist;
            if (max_vertex < maxdist)
                max_vertex = maxdist;
            if (facet2.maxoutside < maxdist)
                facet2.maxoutside = maxdist;
            if (min_vertex > mindist)
                min_vertex = mindist;
            if (!facet2.keepcentrum
                && (maxdist > WIDEfacet || mindist < -WIDEfacet))
            {
                facet2.keepcentrum = true;
            }
        }
        nummerge = facet1.nummerge + facet2.nummerge + 1;
        if (nummerge >= qh_MAXnummerge)
            facet2.nummerge = qh_MAXnummerge;
        else
            facet2.nummerge = nummerge;
        facet2.newmerge = true;
        facet2.dupridge = false;
        qh_updatetested(facet1, facet2);
        if (hull_dim > 2 && QSet<Vertex>.Size(facet1.vertices) == hull_dim)
            qh_mergesimplex(facet1, facet2, mergeapex);
        else
        {
            vertex_visit++;
            QSet<Vertex> f2v = facet2.vertices!;
            for (int vi = 0; (vertex = f2v.e[vi]) != null; vi++)
                vertex.visitid = vertex_visit;
            if (hull_dim == 2)
                throw new NotSupportedException("qhull port: 2-d is not ported");
            else
            {
                qh_mergeneighbors(facet1, facet2);
                qh_mergevertices(facet1.vertices!, ref facet2.vertices);
            }
            qh_mergeridges(facet1, facet2);
            qh_mergevertex_neighbors(facet1, facet2);
            if (!facet2.newfacet)
                qh_newvertices(facet2.vertices!);
        }
        if (!mergeapex)
            qh_degen_redundant_neighbors(facet2, facet1);
        qh_willdelete(facet1, facet2);
        qh_removefacet(facet2);
        qh_appendfacet(facet2);
        facet2.newfacet = true;
        facet2.tested = false;
    }

    /// <summary>qh_mergeneighbors</summary>
    internal void qh_mergeneighbors(Facet facet1, Facet facet2)
    {
        Facet? neighbor;
        visit_id++;
        QSet<Facet> f2n = facet2.neighbors!;
        for (int ni = 0; (neighbor = f2n.e[ni]) != null; ni++)
            neighbor.visitid = visit_id;
        QSet<Facet> f1n = facet1.neighbors!;
        for (int ni = 0; (neighbor = f1n.e[ni]) != null; ni++)
        {
            if (neighbor.visitid == visit_id)
            {
                if (neighbor.simplicial)
                    qh_makeridges(neighbor);
                if (neighbor.neighbors!.e[0] != facet1)
                    QSet<Facet>.Del(neighbor.neighbors, facet1);
                else
                {
                    QSet<Facet>.Del(neighbor.neighbors, facet2);
                    QSet<Facet>.Replace(neighbor.neighbors, facet1, facet2);
                }
            }
            else if (neighbor != facet2)
            {
                QSet<Facet>.Append(ref facet2.neighbors, neighbor, pool);
                QSet<Facet>.Replace(neighbor.neighbors!, facet1, facet2);
            }
        }
        QSet<Facet>.Del(facet1.neighbors, facet2);
        QSet<Facet>.Del(facet2.neighbors, facet1);
    }

    /// <summary>qh_mergeridges</summary>
    internal void qh_mergeridges(Facet facet1, Facet facet2)
    {
        Ridge? ridge;
        Vertex? vertex;
        QSet<Ridge>? f2r = facet2.ridges;
        if (f2r != null)
        {
            for (int ri = 0; (ridge = f2r.e[ri]) != null; ri++)
            {
                if ((ridge.top == facet1) || (ridge.bottom == facet1))
                {
                    QSet<Vertex> rv = ridge.vertices!;
                    for (int vi = 0; (vertex = rv.e[vi]) != null; vi++)
                        vertex.delridge = true;
                    qh_delridge(ridge);
                    ri--;
                }
            }
        }
        QSet<Ridge>? f1r = facet1.ridges;
        if (f1r != null)
        {
            for (int ri = 0; (ridge = f1r.e[ri]) != null; ri++)
            {
                if (ridge.top == facet1)
                    ridge.top = facet2;
                else
                    ridge.bottom = facet2;
                QSet<Ridge>.Append(ref facet2.ridges, ridge, pool);
            }
        }
    }

    /// <summary>qh_mergesimplex</summary>
    internal void qh_mergesimplex(Facet facet1, Facet facet2, bool mergeapex)
    {
        Vertex? vertex, apex;
        Ridge? ridge;
        bool issubset = false;
        int vertex_i = -1, vertex_n;
        Facet? neighbor, otherfacet;
        if (mergeapex)
        {
            if (!facet2.newfacet)
                qh_newvertices(facet2.vertices!);
            apex = facet1.vertices!.e[0]!;
            if (facet2.vertices!.e[0] != apex)
                QSet<Vertex>.AddNth(ref facet2.vertices, 0, apex, pool);
            else
                issubset = true;
        }
        else
        {
            QSet<Vertex> f1v = facet1.vertices!;
            for (int vi = 0; (vertex = f1v.e[vi]) != null; vi++)
                vertex.seen = false;
            QSet<Ridge>? f1r = facet1.ridges;
            if (f1r != null)
            {
                for (int ri = 0; (ridge = f1r.e[ri]) != null; ri++)
                {
                    if ((ridge.top == facet1 ? ridge.bottom : ridge.top) == facet2)
                    {
                        QSet<Vertex> rv = ridge.vertices!;
                        for (int vi = 0; (vertex = rv.e[vi]) != null; vi++)
                        {
                            vertex.seen = true;
                            vertex.delridge = true;
                        }
                        break;
                    }
                }
            }
            vertex = null;
            for (int vi = 0; (vertex = f1v.e[vi]) != null; vi++)
            {
                if (!vertex.seen)
                    break;
            }
            apex = vertex!;
            QSet<Vertex> f2v = facet2.vertices!;
            vertex_n = f2v.n;
            for (vertex_i = 0; vertex_i < vertex_n; vertex_i++)
            {
                vertex = f2v.e[vertex_i]!;
                if (vertex.id < apex.id)
                    break;
                else if (vertex.id == apex.id)
                {
                    issubset = true;
                    break;
                }
            }
            if (!issubset)
                QSet<Vertex>.AddNth(ref facet2.vertices, vertex_i, apex, pool);
            if (!facet2.newfacet)
                qh_newvertices(facet2.vertices!);
            else if (!apex.newlist)
            {
                qh_removevertex(apex);
                qh_appendvertex(apex);
            }
        }
        QSet<Vertex> f1vs = facet1.vertices!;
        for (int vi = 0; (vertex = f1vs.e[vi]) != null; vi++)
        {
            if (vertex == apex && !issubset)
                QSet<Facet>.Replace(vertex.neighbors!, facet1, facet2);
            else
            {
                QSet<Facet>.Del(vertex.neighbors, facet1);
                if (vertex.neighbors!.Second == null)
                    qh_mergevertex_del(vertex, facet1, facet2);
            }
        }
        visit_id++;
        QSet<Facet> f2n = facet2.neighbors!;
        for (int ni = 0; (neighbor = f2n.e[ni]) != null; ni++)
            neighbor.visitid = visit_id;
        QSet<Ridge>? f1rs = facet1.ridges;
        if (f1rs != null)
        {
            for (int ri = 0; (ridge = f1rs.e[ri]) != null; ri++)
            {
                otherfacet = ridge.top == facet1 ? ridge.bottom! : ridge.top!;
                if (otherfacet == facet2)
                {
                    QSet<Ridge>.Del(facet2.ridges, ridge);
                    ridge.vertices = null; // qh_setfree + qh_memfree
                    QSet<Facet>.Del(facet2.neighbors, facet1);
                }
                else
                {
                    QSet<Ridge>.Append(ref facet2.ridges, ridge, pool);
                    if (otherfacet.visitid != visit_id)
                    {
                        QSet<Facet>.Append(ref facet2.neighbors, otherfacet, pool);
                        QSet<Facet>.Replace(otherfacet.neighbors!, facet1, facet2);
                        otherfacet.visitid = visit_id;
                    }
                    else
                    {
                        if (otherfacet.simplicial)
                            qh_makeridges(otherfacet);
                        if (otherfacet.neighbors!.e[0] != facet1)
                            QSet<Facet>.Del(otherfacet.neighbors, facet1);
                        else
                        {
                            QSet<Facet>.Del(otherfacet.neighbors, facet2);
                            QSet<Facet>.Replace(otherfacet.neighbors, facet1, facet2);
                        }
                    }
                    if (ridge.top == facet1)
                        ridge.top = facet2;
                    else
                        ridge.bottom = facet2;
                }
            }
            f1rs.e[0] = null; // SETfirst_(facet1->ridges)= NULL
        }
        else
            throw new NullReferenceException("qh_mergesimplex: facet1 has no ridges"); // SETfirst_ of NULL in C
    }

    /// <summary>qh_mergevertex_del</summary>
    internal void qh_mergevertex_del(Vertex vertex, Facet facet1, Facet facet2)
    {
        QSet<Vertex>.DelSorted(facet2.vertices, vertex);
        vertex.deleted = true;
        QSet<Vertex>.Append(ref del_vertices, vertex, pool);
        _ = facet1;
    }

    /// <summary>qh_mergevertex_neighbors</summary>
    internal void qh_mergevertex_neighbors(Facet facet1, Facet facet2)
    {
        Vertex? vertex;
        QSet<Vertex> f1v = facet1.vertices!;
        for (int vi = 0; (vertex = f1v.e[vi]) != null; vi++)
        {
            if (vertex.visitid != vertex_visit)
                QSet<Facet>.Replace(vertex.neighbors!, facet1, facet2);
            else
            {
                QSet<Facet>.Del(vertex.neighbors, facet1);
                if (vertex.neighbors!.Second == null)
                    qh_mergevertex_del(vertex, facet1, facet2);
            }
        }
    }

    /// <summary>qh_mergevertices</summary>
    internal void qh_mergevertices(QSet<Vertex> vertices1, ref QSet<Vertex>? vertices2)
    {
        int newsize = vertices1.n + QSet<Vertex>.Size(vertices2) - hull_dim + 1;
        var mergedvertices = QSet<Vertex>.New(newsize, pool);
        Vertex? vertex;
        Vertex?[] v2 = vertices2!.e;
        int vertex2 = 0;
        for (int vi = 0; (vertex = vertices1.e[vi]) != null; vi++)
        {
            if (v2[vertex2] == null || vertex.id > v2[vertex2]!.id)
                mergedvertices.Append(vertex);
            else
            {
                while (v2[vertex2] != null && v2[vertex2]!.id > vertex.id)
                    mergedvertices.Append(v2[vertex2++]);
                if (v2[vertex2] == null || v2[vertex2]!.id < vertex.id)
                    mergedvertices.Append(vertex);
                else
                    mergedvertices.Append(v2[vertex2++]);
            }
        }
        while (v2[vertex2] != null)
            mergedvertices.Append(v2[vertex2++]);
        if (newsize < mergedvertices.n)
            throw qh_errexit(qh_ERRqhull, null, null);
        vertices2 = mergedvertices;
    }

    /// <summary>qh_newvertices</summary>
    internal void qh_newvertices(QSet<Vertex> vertices)
    {
        Vertex? vertex;
        for (int vi = 0; (vertex = vertices.e[vi]) != null; vi++)
        {
            if (!vertex.newlist)
            {
                qh_removevertex(vertex);
                qh_appendvertex(vertex);
            }
        }
    }

    /// <summary>qh_reducevertices</summary>
    internal bool qh_reducevertices()
    {
        bool degenredun = false;
        Facet? newfacet;
        Vertex? vertex;
        if (hull_dim == 2)
            return false;
        if (qh_merge_degenredundant() != 0)
            degenredun = true;
        // LABELrestart: only the 4-d qh_redundant_vertex branch jumps back here
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            if (newfacet.newmerge)
            {
                if (!MERGEvertices)
                    newfacet.newmerge = false;
                qh_remove_extravertices(newfacet);
            }
        }
        if (!MERGEvertices)
            return false;
        for (newfacet = newfacet_list; newfacet != null && newfacet.next != null; newfacet = newfacet.next)
        {
            if (newfacet.newmerge)
            {
                newfacet.newmerge = false;
                QSet<Vertex> nv = newfacet.vertices!;
                for (int vi = 0; (vertex = nv.e[vi]) != null; vi++)
                {
                    if (vertex.delridge)
                    {
                        if (qh_rename_sharedvertex(vertex, newfacet) != null)
                        {
                            vi--;
                        }
                    }
                }
            }
        }
        for (vertex = newvertex_list; vertex != null && vertex.next != null; vertex = vertex.next)
        {
            if (vertex.delridge && !vertex.deleted)
            {
                vertex.delridge = false;
                if (hull_dim >= 4)
                    throw new NotSupportedException("qhull port: 4-d qh_redundant_vertex is not ported");
            }
        }
        return degenredun;
    }

    /// <summary>qh_remove_extravertices</summary>
    internal bool qh_remove_extravertices(Facet facet)
    {
        Ridge? ridge;
        Vertex? vertex;
        bool foundrem = false;
        QSet<Vertex> fv = facet.vertices!;
        for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
            vertex.seen = false;
        QSet<Ridge>? fr = facet.ridges;
        if (fr != null)
        {
            for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
            {
                QSet<Vertex> rv = ridge.vertices!;
                for (int vi = 0; (vertex = rv.e[vi]) != null; vi++)
                    vertex.seen = true;
            }
        }
        for (int vi = 0; (vertex = fv.e[vi]) != null; vi++)
        {
            if (!vertex.seen)
            {
                foundrem = true;
                QSet<Vertex>.DelSorted(facet.vertices, vertex);
                QSet<Facet>.Del(vertex.neighbors, facet);
                if (QSet<Facet>.Size(vertex.neighbors) == 0)
                {
                    vertex.deleted = true;
                    QSet<Vertex>.Append(ref del_vertices, vertex, pool);
                }
                vi--;
            }
        }
        return foundrem;
    }

    /// <summary>qh_rename_sharedvertex</summary>
    internal Vertex? qh_rename_sharedvertex(Vertex vertex, Facet facet)
    {
        Facet? neighbor, neighborA = null;
        QSet<Vertex> vertices;
        QSet<Ridge>? ridges;
        Vertex? newvertex;
        if (QSet<Facet>.Size(vertex.neighbors) == 2)
        {
            neighborA = vertex.neighbors!.e[0];
            if (neighborA == facet)
                neighborA = vertex.neighbors.e[1];
        }
        else if (hull_dim == 3)
            return null;
        else
        {
            visit_id++;
            QSet<Facet> fn = facet.neighbors!;
            for (int ni = 0; (neighbor = fn.e[ni]) != null; ni++)
                neighbor.visitid = visit_id;
            QSet<Facet> vn = vertex.neighbors!;
            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
            {
                if (neighbor.visitid == visit_id)
                {
                    if (neighborA != null)
                        return null;
                    neighborA = neighbor;
                }
            }
            if (neighborA == null)
                throw qh_errexit(qh_ERRqhull, null, null);
        }
        ridges = QSet<Ridge>.New(TEMPsize, pool);
        neighborA!.visitid = ++visit_id;
        qh_vertexridges_facet(vertex, facet, ref ridges);
        vertices = qh_vertexintersect_new(facet.vertices!, neighborA.vertices!);
        QSet<Vertex>.Del(vertices, vertex);
        if ((newvertex = qh_find_newvertex(vertex, vertices, ridges!)) != null)
            qh_renamevertex(vertex, newvertex, ridges!, facet, neighborA);
        return newvertex;
    }

    /// <summary>qh_renameridgevertex</summary>
    internal void qh_renameridgevertex(Ridge ridge, Vertex oldvertex, Vertex newvertex)
    {
        int nth = 0, oldnth;
        Facet? temp;
        Vertex? vertex;
        oldnth = QSet<Vertex>.Index(ridge.vertices, oldvertex);
        ridge.vertices!.DelNthSorted(oldnth);
        QSet<Vertex> rv = ridge.vertices;
        for (int vi = 0; (vertex = rv.e[vi]) != null; vi++)
        {
            if (vertex == newvertex)
            {
                if (ridge.nonconvex)
                    qh_copynonconvex(ridge);
                qh_delridge(ridge);
                return;
            }
            if (vertex.id < newvertex.id)
                break;
            nth++;
        }
        QSet<Vertex>.AddNth(ref ridge.vertices, nth, newvertex, pool);
        if ((Math.Abs(oldnth - nth) % 2) != 0)
        {
            temp = ridge.top;
            ridge.top = ridge.bottom;
            ridge.bottom = temp;
        }
    }

    /// <summary>qh_renamevertex</summary>
    internal void qh_renamevertex(Vertex oldvertex, Vertex newvertex, QSet<Ridge> ridges, Facet? oldfacet, Facet? neighborA)
    {
        Facet? neighbor;
        Ridge? ridge;
        for (int ri = 0; (ridge = ridges.e[ri]) != null; ri++)
            qh_renameridgevertex(ridge, oldvertex, newvertex);
        if (oldfacet == null)
        {
            QSet<Facet> vn = oldvertex.neighbors!;
            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
            {
                qh_maydropneighbor(neighbor);
                QSet<Vertex>.DelSorted(neighbor.vertices, oldvertex);
                if (qh_remove_extravertices(neighbor))
                    ni--;
            }
            if (!oldvertex.deleted)
            {
                oldvertex.deleted = true;
                QSet<Vertex>.Append(ref del_vertices, oldvertex, pool);
            }
        }
        else if (QSet<Facet>.Size(oldvertex.neighbors) == 2)
        {
            QSet<Facet> vn = oldvertex.neighbors!;
            for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
                QSet<Vertex>.DelSorted(neighbor.vertices, oldvertex);
            oldvertex.deleted = true;
            QSet<Vertex>.Append(ref del_vertices, oldvertex, pool);
        }
        else
        {
            QSet<Vertex>.DelSorted(oldfacet.vertices, oldvertex);
            QSet<Facet>.Del(oldvertex.neighbors, oldfacet);
            qh_remove_extravertices(neighborA!);
        }
    }

    /// <summary>qh_test_appendmerge</summary>
    internal bool qh_test_appendmerge(Facet facet, Facet neighbor)
    {
        double dist, dist2 = -REALmax, angle = -REALmax;
        bool isconcave = false, iscoplanar = false, okangle = false;
        if (SKIPconvex && !POSTmerging)
            return false;
        if ((!MERGEexact || POSTmerging) && cos_max < REALmax / 2)
        {
            angle = qh_getangle(facet.normal!, neighbor.normal!);
            if (angle > cos_max)
            {
                qh_appendmergeset(facet, neighbor, MergeType.MRGanglecoplanar, true, angle);
                return true;
            }
            else
                okangle = true;
        }
        facet.center ??= qh_getcentrum(facet);
        qh_distplane(facet.center, neighbor, out dist);
        if (dist > centrum_radius)
            isconcave = true;
        else
        {
            if (dist > -centrum_radius)
                iscoplanar = true;
            neighbor.center ??= qh_getcentrum(neighbor);
            qh_distplane(neighbor.center, facet, out dist2);
            if (dist2 > centrum_radius)
                isconcave = true;
            else if (!iscoplanar && dist2 > -centrum_radius)
                iscoplanar = true;
        }
        if (!isconcave && (!iscoplanar || (MERGEexact && !POSTmerging)))
            return false;
        if (!okangle && ANGLEmerge)
        {
            angle = qh_getangle(facet.normal!, neighbor.normal!);
        }
        if (isconcave)
        {
            if (ANGLEmerge)
                angle += qh_ANGLEconcave + 0.5;
            qh_appendmergeset(facet, neighbor, MergeType.MRGconcave, true, angle);
        }
        else
        {
            qh_appendmergeset(facet, neighbor, MergeType.MRGcoplanar, true, angle);
        }
        return true;
    }

    /// <summary>qh_updatetested</summary>
    internal void qh_updatetested(Facet facet1, Facet facet2)
    {
        Ridge? ridge;
        int size;
        facet2.tested = false;
        QSet<Ridge>? f1r = facet1.ridges;
        if (f1r != null)
        {
            for (int ri = 0; (ridge = f1r.e[ri]) != null; ri++)
                ridge.tested = false;
        }
        if (facet2.center == null)
            return;
        size = QSet<Vertex>.Size(facet2.vertices);
        if (!facet2.keepcentrum)
        {
            if (size > hull_dim + qh_MAXnewcentrum)
            {
                facet2.keepcentrum = true;
            }
        }
        else if (size <= hull_dim + qh_MAXnewcentrum)
        {
            if (size == hull_dim || POSTmerging)
                facet2.keepcentrum = false;
        }
        if (!facet2.keepcentrum)
        {
            facet2.center = null;
            QSet<Ridge>? f2r = facet2.ridges;
            if (f2r != null)
            {
                for (int ri = 0; (ridge = f2r.e[ri]) != null; ri++)
                    ridge.tested = false;
            }
        }
    }

    /// <summary>qh_vertexridges</summary>
    internal QSet<Ridge> qh_vertexridges(Vertex vertex)
    {
        Facet? neighbor;
        QSet<Ridge>? ridges = QSet<Ridge>.New(TEMPsize, pool);
        visit_id++;
        QSet<Facet> vn = vertex.neighbors!;
        for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
            neighbor.visitid = visit_id;
        for (int ni = 0; (neighbor = vn.e[ni]) != null; ni++)
        {
            if (vn.e[ni + 1] != null) // if (*neighborp): no new ridges in last neighbor
                qh_vertexridges_facet(vertex, neighbor, ref ridges);
        }
        return ridges!;
    }

    /// <summary>qh_vertexridges_facet</summary>
    internal void qh_vertexridges_facet(Vertex vertex, Facet facet, ref QSet<Ridge>? ridges)
    {
        Ridge? ridge;
        Facet neighbor;
        QSet<Ridge>? fr = facet.ridges;
        if (fr != null)
        {
            for (int ri = 0; (ridge = fr.e[ri]) != null; ri++)
            {
                neighbor = ridge.top == facet ? ridge.bottom! : ridge.top!;
                if (neighbor.visitid == visit_id
                    && QSet<Vertex>.In(ridge.vertices, vertex))
                    QSet<Ridge>.Append(ref ridges, ridge, pool);
            }
        }
        facet.visitid = visit_id - 1;
    }

    /// <summary>qh_willdelete</summary>
    internal void qh_willdelete(Facet facet, Facet? replace)
    {
        qh_removefacet(facet);
        qh_prependfacet(facet, ref visible_list);
        num_visible++;
        facet.visible = true;
        facet.f = replace; // f.replace
    }
}

/// <summary>A qsort comparator (a struct so the sort does not allocate).</summary>
internal interface IQsortCompare<T>
{
    int Compare(T a, T b);
}

/// <summary>qh_compareangle: never returns 0 (the result depends on the sort algorithm).</summary>
internal readonly struct CompareAngle : IQsortCompare<MergeT>
{
    public int Compare(MergeT a, MergeT b) => (a.angle > b.angle) ? 1 : -1;
}

/// <summary>qh_comparemerge</summary>
internal readonly struct CompareMerge : IQsortCompare<MergeT>
{
    public int Compare(MergeT a, MergeT b) => (int)a.type - (int)b.type;
}

/// <summary>qh_comparevisit: (a->visitid - b->visitid) as int</summary>
internal readonly struct CompareVisit : IQsortCompare<Vertex>
{
    public int Compare(Vertex a, Vertex b) => unchecked((int)(a.visitid - b.visitid));
}

/// <summary>
/// glibc's qsort for an array of pointers: a top-down merge sort (n1 = n/2, take the
/// left element when cmp &lt;= 0). Verified identical to glibc 2.43 qsort, including for
/// qh_compareangle, which never returns 0 (p8aq-qsorttest.c). That comparator makes
/// the order sort-algorithm dependent: an MSVC build of qhull can order ties differently.
/// </summary>
internal static class GlibcMsort
{
    internal static void Sort<T, TCmp>(T?[] b, int n, TCmp cmp, ref T?[]? scratch)
        where T : class
        where TCmp : struct, IQsortCompare<T>
    {
        if (n <= 1)
            return;
        if (scratch == null || scratch.Length < n)
            scratch = new T?[Math.Max(n, 16)];
        Msort(b, 0, n, scratch, cmp);
    }

    private static void Msort<T, TCmp>(T?[] b, int start, int n, T?[] tmp, TCmp cmp)
        where T : class
        where TCmp : struct, IQsortCompare<T>
    {
        if (n <= 1)
            return;
        int n1 = n / 2;
        int n2 = n - n1;
        int b1 = start, b2 = start + n1;
        Msort(b, b1, n1, tmp, cmp);
        Msort(b, b2, n2, tmp, cmp);
        int t = 0;
        while (n1 > 0 && n2 > 0)
        {
            if (cmp.Compare(b[b1]!, b[b2]!) <= 0)
            {
                tmp[t++] = b[b1++];
                --n1;
            }
            else
            {
                tmp[t++] = b[b2++];
                --n2;
            }
        }
        while (n1 > 0)
        {
            tmp[t++] = b[b1++];
            --n1;
        }
        Array.Copy(tmp, 0, b, start, t);
    }
}
