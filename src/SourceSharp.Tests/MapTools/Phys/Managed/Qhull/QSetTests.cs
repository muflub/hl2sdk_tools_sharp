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

using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed.Qhull;

/// <summary>qset.c semantics the port relies on for facet and merge order.</summary>
public class QSetTests
{
    private sealed class E(int v)
    {
        public int V { get; } = v;
    }

    private static (QSet<E> Set, E[] Items) Make(int n)
    {
        var items = new E[n];
        QSet<E>? s = new QSet<E>(2);
        for (int i = 0; i < n; i++)
        {
            items[i] = new E(i);
            QSet<E>.Append(ref s, items[i]);
        }
        return (s!, items);
    }

    private static int[] Values(QSet<E> s)
    {
        var r = new List<int>();
        E? e;
        for (int i = 0; (e = s.e[i]) != null; i++)
            r.Add(e.V);
        return r.ToArray();
    }

    [Fact]
    public void DelNthMovesTheLastElementIntoTheHole()
    {
        // qset.c qh_setdelnth: "*elemp= *lastp"
        var (s, _) = Make(5);
        s.DelNth(1);
        Assert.Equal(new[] { 0, 4, 2, 3 }, Values(s));
    }

    [Fact]
    public void DelMovesTheLastElementIntoTheHole()
    {
        // qset.c qh_setdel: unsorted delete
        var (s, items) = Make(5);
        QSet<E>.Del(s, items[0]);
        Assert.Equal(new[] { 4, 1, 2, 3 }, Values(s));
    }

    [Fact]
    public void DelNthSortedShiftsTheTailDown()
    {
        // qset.c qh_setdelnthsorted
        var (s, _) = Make(5);
        s.DelNthSorted(1);
        Assert.Equal(new[] { 0, 2, 3, 4 }, Values(s));
    }

    [Fact]
    public void DelOfNullDeletesNothing()
    {
        // qh_setdel stops at the NULL terminator: qh_furthestout passes
        // the loop variable, NULL after the FOREACH, so the furthest point never moves
        var (s, _) = Make(3);
        Assert.Null(QSet<E>.Del(s, null));
        Assert.Equal(new[] { 0, 1, 2 }, Values(s));
    }

    [Fact]
    public void Append2ndLastInsertsBeforeTheLastElement()
    {
        // qset.c qh_setappend2ndlast: the last element stays the furthest point
        var (s, _) = Make(3);
        QSet<E>? r = s;
        QSet<E>.Append2ndLast(ref r, new E(9));
        Assert.Equal(new[] { 0, 1, 9, 2 }, Values(s));
    }

    [Fact]
    public void AddNthShiftsTheTailUp()
    {
        // qset.c qh_setaddnth
        var (s, _) = Make(3);
        QSet<E>? r = s;
        QSet<E>.AddNth(ref r, 0, new E(7));
        Assert.Equal(new[] { 7, 0, 1, 2 }, Values(s));
    }

    [Fact]
    public void CompactDropsNullHolesInOrder()
    {
        // qset.c qh_setcompact
        var (s, _) = Make(5);
        s.e[1] = null;
        s.e[3] = null;
        QSet<E>.Compact(s);
        Assert.Equal(new[] { 0, 2, 4 }, Values(s));
        Assert.Equal(3, s.n);
    }

    [Fact]
    public void ForeachStopsAtTheFirstNullOfAnIndexedSet()
    {
        // FOREACHsetelement_ in the reference implementation ends at the first NULL; qh_setsize still counts holes
        var (s, _) = Make(4);
        s.e[2] = null;
        Assert.Equal(new[] { 0, 1 }, Values(s));
        Assert.Equal(4, QSet<E>.Size(s));
    }

    [Fact]
    public void NewDelNthSortedLeavesThePrependSlotsEmpty()
    {
        // qset.c qh_setnew_delnthsorted, as qh_facetintersect uses it (prepend 1)
        var (s, _) = Make(3);
        QSet<E> n = s.NewDelNthSorted(3, 1, 1);
        Assert.Equal(3, n.n);
        Assert.Null(n.e[0]);
        Assert.Equal(0, n.e[1]!.V);
        Assert.Equal(2, n.e[2]!.V);
    }

    [Fact]
    public void EqualExceptMatchesWithOneSkipEach()
    {
        // qset.c qh_setequal_except: {a,b,x} vs {a,b,y} skipping x and y
        var (s, items) = Make(3);
        QSet<E>? t = new QSet<E>(3);
        var y = new E(8);
        QSet<E>.Append(ref t, items[0]);
        QSet<E>.Append(ref t, items[1]);
        QSet<E>.Append(ref t, y);
        Assert.True(QSet<E>.EqualExcept(s, items[2], t!, y));
        Assert.False(QSet<E>.EqualExcept(s, items[1], t!, y));
    }

    [Fact]
    public void LastOfAnEmptySetIsNull()
    {
        // qset.c qh_setlast
        var s = new QSet<E>(3);
        Assert.Null(QSet<E>.Last(s));
        Assert.Null(QSet<E>.Last(null));
    }
}
