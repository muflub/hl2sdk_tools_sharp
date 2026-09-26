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

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>
/// qset.c: qhull's set of pointers. Element order is qhull's exactly (it decides
/// facet order and merge order), including qh_setdelnth moving the last element
/// into the hole and FOREACH stopping at the first NULL element of an indexed set.
/// </summary>
/// <remarks>
/// qhull stores the actual size in the slot after the last element and a NULL
/// terminator after the elements; here <see cref="n"/> is the actual size (which,
/// as in qhull, counts NULL holes of an indexed set) and <c>e[n]</c> is always
/// null. The capacity (qhull's maxsize, which depends on qhmem's size classes) never
/// changes element order, so it is not reproduced: the array simply grows.
/// </remarks>
internal sealed class QSet<T> where T : class
{
    /// <summary>Elements; e[n] is null; e.Length - 1 is the capacity.</summary>
    internal T?[] e;

    /// <summary>Actual size (qhull: e[maxsize].i - 1).</summary>
    internal int n;

    /// <summary>qh_setnew</summary>
    internal QSet(int setsize)
    {
        if (setsize == 0)
            setsize++;
        e = new T?[setsize + 1];
        n = 0;
    }

    private void Reserve(int size)
    {
        if (size + 1 > e.Length)
        {
            int cap = e.Length - 1;
            int newcap = cap * 2 > size ? cap * 2 : size;
            Array.Resize(ref e, newcap + 1);
        }
    }

    /// <summary>qh_setsize (a null set has size 0)</summary>
    internal static int Size(QSet<T>? set) => set == null ? 0 : set.n;

    /// <summary>SETfirst_ (null for an empty set)</summary>
    internal T? First => e[0];

    /// <summary>SETsecond_ (reads the slot as qhull does, even past the terminator of a 0-size set)</summary>
    internal T? Second => e[1];

    /// <summary>SETempty_</summary>
    internal static bool Empty(QSet<T>? set) => set == null || set.e[0] == null;

    /// <summary>qh_setappend: appends elem (a NULL elem is ignored).</summary>
    internal static void Append(ref QSet<T>? setp, T? newelem)
    {
        if (newelem == null)
            return;
        setp ??= new QSet<T>(3);
        QSet<T> s = setp;
        s.Reserve(s.n + 1);
        s.e[s.n++] = newelem;
        s.e[s.n] = null;
    }

    /// <summary>qh_setappend on a set known to exist.</summary>
    internal void Append(T? newelem)
    {
        if (newelem == null)
            return;
        Reserve(n + 1);
        e[n++] = newelem;
        e[n] = null;
    }

    /// <summary>qh_setappend_set: appends all elements of setA (including NULL holes).</summary>
    internal static void AppendSet(ref QSet<T>? setp, QSet<T>? setA)
    {
        if (setA == null)
            return;
        int sizeA = setA.n;
        setp ??= new QSet<T>(sizeA);
        QSet<T> s = setp;
        int size = s.n;
        s.Reserve(size + sizeA);
        if (sizeA > 0)
            Array.Copy(setA.e, 0, s.e, size, sizeA + 1);
        s.n = size + sizeA;
        s.e[s.n] = null;
    }

    /// <summary>qh_setappend2ndlast: inserts newelem before the last element.</summary>
    internal static void Append2ndLast(ref QSet<T>? setp, T newelem)
    {
        setp ??= new QSet<T>(3);
        QSet<T> s = setp;
        if (s.n == 0)
            throw new InvalidOperationException("qh_setappend2ndlast on an empty set");
        s.Reserve(s.n + 1);
        s.e[s.n] = s.e[s.n - 1];
        s.e[s.n - 1] = newelem;
        s.n++;
        s.e[s.n] = null;
    }

    /// <summary>qh_setaddnth: inserts newelem at index nth, shifting the tail up.</summary>
    internal static void AddNth(ref QSet<T>? setp, int nth, T newelem)
    {
        setp ??= new QSet<T>(3);
        QSet<T> s = setp;
        int oldsize = s.n;
        if (nth < 0 || nth > oldsize)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        s.Reserve(oldsize + 1);
        for (int i = oldsize; i >= nth; i--)
            s.e[i + 1] = s.e[i];
        s.e[nth] = newelem;
        s.n = oldsize + 1;
    }

    /// <summary>qh_setcompact: removes NULL elements, keeping order.</summary>
    internal static void Compact(QSet<T>? set)
    {
        if (set == null)
            return;
        int size = set.n;
        int dest = 0;
        for (int i = 0; i < size; i++)
        {
            T? x = set.e[i];
            if (x != null)
                set.e[dest++] = x;
        }
        set.Truncate(dest);
    }

    /// <summary>qh_setcopy</summary>
    internal QSet<T> Copy(int extra)
    {
        if (extra < 0)
            extra = 0;
        var newset = new QSet<T>(n + extra);
        Array.Copy(e, 0, newset.e, 0, n + 1);
        newset.n = n;
        return newset;
    }

    /// <summary>qh_setdel: deletes oldelem (unsorted; the last element moves into its place).</summary>
    internal static T? Del(QSet<T>? set, T? oldelem)
    {
        if (set == null)
            return null;
        int i = 0;
        while (set.e[i] != oldelem && set.e[i] != null)
            i++;
        if (set.e[i] != null)
        {
            set.n--;
            int last = set.n;
            set.e[i] = set.e[last];
            set.e[last] = null;
            return oldelem;
        }
        return null;
    }

    /// <summary>qh_setdellast</summary>
    internal static T? DelLast(QSet<T>? set)
    {
        if (set == null || set.e[0] == null)
            return null;
        set.n--;
        T? ret = set.e[set.n];
        set.e[set.n] = null;
        return ret;
    }

    /// <summary>qh_setdelnth: deletes the nth element; the last element moves into the hole.</summary>
    internal T? DelNth(int nth)
    {
        n--;
        if (nth < 0 || nth > n)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        T? elem = e[nth];
        e[nth] = e[n];
        e[n] = null;
        return elem;
    }

    /// <summary>qh_setdelnthsorted: deletes the nth element, shifting the tail down (copies up to the first NULL).</summary>
    internal T? DelNthSorted(int nth)
    {
        if (nth < 0 || nth >= n)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        T? elem = e[nth];
        int newp = nth, oldp = nth + 1;
        while ((e[newp++] = e[oldp++]) != null)
        {
        }
        n--;
        return elem;
    }

    /// <summary>qh_setdelsorted: deletes oldelem, shifting the tail down.</summary>
    internal static T? DelSorted(QSet<T>? set, T? oldelem)
    {
        if (set == null)
            return null;
        int newp = 0;
        while (set.e[newp] != oldelem && set.e[newp] != null)
            newp++;
        if (set.e[newp] != null)
        {
            int oldp = newp + 1;
            while ((set.e[newp++] = set.e[oldp++]) != null)
            {
            }
            set.n--;
            return oldelem;
        }
        return null;
    }

    /// <summary>qh_setequal</summary>
    internal static bool Equal(QSet<T> setA, QSet<T> setB)
    {
        int sizeA = setA.n, sizeB = setB.n;
        if (sizeA != sizeB)
            return false;
        for (int i = 0; i < sizeA; i++)
        {
            if (setA.e[i] != setB.e[i])
                return false;
        }
        return true;
    }

    /// <summary>qh_setequal_except</summary>
    internal static bool EqualExcept(QSet<T> setA, T? skipelemA, QSet<T> setB, T? skipelemB)
    {
        int a = 0, b = 0;
        int skip = 0;
        while (true)
        {
            if (setA.e[a] == skipelemA)
            {
                skip++;
                a++;
            }
            if (skipelemB != null)
            {
                if (setB.e[b] == skipelemB)
                {
                    skip++;
                    b++;
                }
            }
            else if (setA.e[a] != setB.e[b])
            {
                skip++;
                if ((skipelemB = setB.e[b++]) == null)
                    return false;
            }
            if (setA.e[a] == null)
                break;
            if (setA.e[a++] != setB.e[b++])
                return false;
        }
        if (skip != 2 || setB.e[b] != null)
            return false;
        return true;
    }

    /// <summary>qh_setequal_skip</summary>
    internal static bool EqualSkip(QSet<T> setA, int skipA, QSet<T> setB, int skipB)
    {
        int a = 0, b = 0;
        while (true)
        {
            if (a == skipA)
                a++;
            if (b == skipB)
                b++;
            if (setA.e[a] == null)
                break;
            if (setA.e[a++] != setB.e[b++])
                return false;
        }
        if (setB.e[b] != null)
            return false;
        return true;
    }

    /// <summary>qh_setin (stops at the first NULL)</summary>
    internal static bool In(QSet<T>? set, T? setelem)
    {
        if (set == null)
            return false;
        T? elem;
        for (int i = 0; (elem = set.e[i]) != null; i++)
        {
            if (elem == setelem)
                return true;
        }
        return false;
    }

    /// <summary>qh_setindex (scans all size slots, including NULL holes)</summary>
    internal static int Index(QSet<T>? set, T? atelem)
    {
        if (set == null)
            return -1;
        for (int i = 0; i < set.n; i++)
        {
            if (set.e[i] == atelem)
                return i;
        }
        return -1;
    }

    /// <summary>qh_setlast</summary>
    internal static T? Last(QSet<T>? set)
    {
        if (set != null && set.n > 0)
            return set.e[set.n - 1];
        return null;
    }

    /// <summary>qh_setnew_delnthsorted: new set of the first size elements without the nth, prepend slots left empty.</summary>
    internal QSet<T> NewDelNthSorted(int size, int nth, int prepend)
    {
        int tailsize = size - nth - 1;
        if (tailsize < 0)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        int newsize = size - 1 + prepend;
        var newset = new QSet<T>(newsize);
        newset.n = newsize;
        int newp = prepend;
        int oldp = 0;
        for (int i = 0; i < nth; i++)
            newset.e[newp++] = e[oldp++];
        oldp++;
        for (int i = 0; i < tailsize; i++)
            newset.e[newp++] = e[oldp++];
        newset.e[newp] = null;
        return newset;
    }

    /// <summary>qh_setreplace</summary>
    internal static void Replace(QSet<T> set, T? oldelem, T? newelem)
    {
        int i = 0;
        while (set.e[i] != oldelem && set.e[i] != null)
            i++;
        if (set.e[i] != null)
            set.e[i] = newelem;
        else
            throw new QhullExit(QhConst.qhmem_ERRqhull);
    }

    /// <summary>qh_settruncate</summary>
    internal void Truncate(int size)
    {
        if (size < 0 || size > e.Length - 1)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        n = size;
        e[size] = null;
    }

    /// <summary>qh_setunique</summary>
    internal static bool Unique(ref QSet<T>? set, T elem)
    {
        if (!In(set, elem))
        {
            Append(ref set, elem);
            return true;
        }
        return false;
    }

    /// <summary>qh_setzero: sets the size to size and zeroes elements index..size.</summary>
    internal void Zero(int index, int size)
    {
        if (index < 0 || index >= size)
            throw new QhullExit(QhConst.qhmem_ERRqhull);
        Reserve(size);
        n = size;
        for (int i = index; i <= size; i++)
            e[i] = null;
    }
}
