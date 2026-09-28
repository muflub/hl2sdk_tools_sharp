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

/// <summary> / / / constants used by the 3-d subset.</summary>
internal static class QhConst
{
    internal const int qh_ERRnone = 0;
    internal const int qh_ERRinput = 1;
    internal const int qh_ERRsingular = 2;
    internal const int qh_ERRprec = 3;
    internal const int qh_ERRmem = 4;
    internal const int qh_ERRqhull = 5;
    internal const int qhmem_ERRqhull = 5;

    internal const double REALmax = double.MaxValue;
    internal const double REALmin = 2.2250738585072014e-308; // DBL_MIN
    internal const double REALepsilon = 2.220446049250313e-16; // DBL_EPSILON

    internal const double qh_RANDOMmax = 2147483646.0; // (realT)2147483646UL, qh_RANDOMtype 5
    internal const int qh_HASHfactor = 2;
    internal const int qh_INITIALsearch = 6;
    internal const int qh_INITIALmax = 8;
    internal const double qh_JOGGLEdefault = 30000.0;
    internal const double qh_JOGGLEincrease = 10.0;
    internal const int qh_JOGGLEretry = 2;
    internal const int qh_JOGGLEagain = 1;
    internal const double qh_JOGGLEmaxincrease = 1e-2;
    internal const int qh_JOGGLEmaxretry = 100;
    internal const int qh_DIMmergeVertex = 6;
    internal const int qh_DIMreduceBuild = 5;
    internal const int qh_BESTcentrum = 20;
    internal const int qh_BESTcentrum2 = 2;
    internal const int qh_BESTnonconvex = 15;
    internal const int qh_MAXnewmerges = 2;
    internal const int qh_MAXnewcentrum = 5;
    internal const double qh_COPLANARratio = 3;
    internal const int qh_RATIOnearinside = 5;
    internal const int qh_USEfindbestnew = 50;
    internal const double qh_WIDEcoplanar = 6;
    internal const double qh_MAXnarrow = -0.98;
    internal const double qh_WARNnarrow = -0.9999;
    internal const int qh_MAXnummerge = 511;
    internal const double qh_ANGLEredundant = 6.0;
    internal const double qh_ANGLEdegen = 5.0;
    internal const double qh_ANGLEconcave = 1.5;
    internal const int qh_ORIENTclock = 0;

    internal const bool qh_ALL = true;
    internal const bool qh_NOupper = true;
    internal const bool qh_MERGEapex = true;
    internal const int qh_ALGORITHMfault = 0;
    internal const int qh_DATAfault = 1;
}

/// <summary>qh_CENTER</summary>
internal enum QhCenter
{
    qh_ASnone = 0,
    qh_ASvoronoi,
    qh_AScentrum,
}

/// <summary>mergeType; the order matters (comparisons with &lt; and &lt;=).</summary>
internal enum MergeType
{
    MRGnone = 0,
    MRGcoplanar,
    MRGanglecoplanar,
    MRGconcave,
    MRGflip,
    MRGridge,
    MRGdegen,
    MRGredundant,
    ENDmrg,
}

/// <summary>facetT</summary>
internal sealed class Facet : IQhPooled
{
    internal double furthestdist;
    internal double maxoutside;
    internal double offset;
    internal double[]? normal;
    /// <summary>the union f: replace (visible), samecycle (newfacet), newcycle (horizon). One storage slot as in C.</summary>
    internal Facet? f;
    internal double[]? center;
    internal Facet? previous;
    internal Facet? next;
    internal QSet<Vertex>? vertices;
    internal QSet<Ridge>? ridges;
    internal QSet<Facet>? neighbors;
    internal QSet<double[]>? outsideset;
    internal QSet<double[]>? coplanarset;
    internal uint visitid;
    internal uint id;
    internal int nummerge;     // 9-bit field, clamped to qh_MAXnummerge by the callers as in C
    internal bool newfacet;
    internal bool visible;
    internal bool toporient;
    internal bool simplicial;
    internal bool seen;
    internal bool flipped;
    internal bool upperdelaunay;
    internal bool notfurthest;
    internal bool good;
    internal bool dupridge;
    internal bool mergeridge;
    internal bool mergeridge2;
    internal bool coplanar;
    internal bool mergehorizon;
    internal bool cycledone;
    internal bool tested;
    internal bool keepcentrum;
    internal bool newmerge;
    internal bool degenerate;
    internal bool redundant;
    /// <summary>true for the qh_DUPLICATEridge / qh_MERGEridge sentinels ((facetT*)1 and 2 in C).</summary>
    internal bool sentinel;
    /// <summary>
    /// QhPool: the normal array of this object's previous life, reused by qh_setfacetplane.
    /// A facet owns its normal alone (nothing else holds or shares the array), so the array can
    /// follow the facet from build to build; qh_setfacetplane writes every coordinate before
    /// anything reads it. Sets are not kept this way: they come from the pool's own set
    /// storage, so that a set is never owned by two slots at once.
    /// </summary>
    internal double[]? spareNormal;

    /// <summary>memset (facet, 0) for a pooled facet, keeping the spare normal.</summary>
    public void Reset()
    {
        double[]? nm = normal ?? spareNormal;
        furthestdist = 0;
        maxoutside = 0;
        offset = 0;
        normal = null;
        f = null;
        center = null;
        previous = null;
        next = null;
        vertices = null;
        ridges = null;
        neighbors = null;
        outsideset = null;
        coplanarset = null;
        visitid = 0;
        id = 0;
        nummerge = 0;
        newfacet = false;
        visible = false;
        toporient = false;
        simplicial = false;
        seen = false;
        flipped = false;
        upperdelaunay = false;
        notfurthest = false;
        good = false;
        dupridge = false;
        mergeridge = false;
        mergeridge2 = false;
        coplanar = false;
        mergehorizon = false;
        cycledone = false;
        tested = false;
        keepcentrum = false;
        newmerge = false;
        degenerate = false;
        redundant = false;
        sentinel = false;
        spareNormal = nm;
    }
}

/// <summary>ridgeT</summary>
internal sealed class Ridge : IQhPooled
{
    internal QSet<Vertex>? vertices;
    internal Facet? top;
    internal Facet? bottom;
    internal uint id;          // 24-bit field in C
    internal bool tested;
    internal bool nonconvex;

    /// <summary>memset (ridge, 0) for a pooled ridge.</summary>
    public void Reset()
    {
        vertices = null;
        top = null;
        bottom = null;
        id = 0;
        tested = false;
        nonconvex = false;
    }
}

/// <summary>vertexT</summary>
internal sealed class Vertex : IQhPooled
{
    internal Vertex? next;
    internal Vertex? previous;
    internal double[]? point;
    internal QSet<Facet>? neighbors;
    internal uint visitid;
    internal uint id;          // 24-bit field in C
    internal bool seen;
    internal bool delridge;
    internal bool deleted;
    internal bool newlist;

    /// <summary>memset (vertex, 0) for a pooled vertex.</summary>
    public void Reset()
    {
        next = null;
        previous = null;
        point = null;
        neighbors = null;
        visitid = 0;
        id = 0;
        seen = false;
        delridge = false;
        deleted = false;
        newlist = false;
    }
}

/// <summary>mergeT</summary>
internal sealed class MergeT : IQhPooled
{
    internal double angle;
    internal Facet facet1 = null!;
    internal Facet facet2 = null!;
    internal MergeType type;

    /// <summary>memset (merge, 0) for a pooled merge record.</summary>
    public void Reset()
    {
        angle = 0;
        facet1 = null!;
        facet2 = null!;
        type = MergeType.MRGnone;
    }
}

/// <summary>qh_errexit's longjmp to qh.errexit, carrying the exit code.</summary>
internal sealed class QhullExit : Exception
{
    internal readonly int ExitCode;

    internal QhullExit(int exitcode)
        : base("qhull exit " + exitcode)
    {
        ExitCode = exitcode;
    }
}
