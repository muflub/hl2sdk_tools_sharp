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

using System.Globalization;
using static SourceSharp.MapTools.Phys.Managed.Qhull.QhConst;

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>
/// qhT, qhstat and the qh_rand seed as one instance: one context per hull build, so
/// builds are independent, deterministic and thread-safe by construction.
/// This file holds global.c (initialisation and option parsing), user.c (qh_new_qhull,
/// qh_errexit) and the random number generator (geom2.c qh_rand/qh_srand).
/// </summary>
/// <remarks>
/// Points are <c>double[]</c> of length hull_dim + 1: coordinates, then the point id
/// (qh_pointid's pointer arithmetic becomes that slot). Reference identity replaces
/// pointer identity.
/// </remarks>
internal sealed partial class Qh
{
    // ---- qh constants (flags) ----
    internal bool ALLpoints;
    internal bool ANGLEmerge;
    internal bool APPROXhull;
    internal double MINoutside;
    internal bool AVOIDold;
    internal bool BESToutside;
    internal bool CHECKfrequently;
    internal double premerge_cos;
    internal double postmerge_cos;
    internal bool DELAUNAY;
    internal bool FORCEoutput;
    internal int GOODpoint;
    internal double[]? GOODpointp;
    internal int GOODvertex;
    internal double[]? GOODvertexp;
    internal bool HALFspace;
    internal int IStracing;
    internal bool KEEPcoplanar;
    internal bool KEEPinside;
    internal double MAXcoplanar;
    internal bool MERGEexact;
    internal bool MERGEindependent;
    internal bool MERGING;
    internal double premerge_centrum;
    internal double postmerge_centrum;
    internal bool MERGEvertices;
    internal double MINvisible;
    internal bool NOnearinside;
    internal bool NOpremerge;
    internal bool ONLYgood;
    internal bool ONLYmax;
    internal bool PICKfurthest;
    internal bool POSTmerge;
    internal bool PREmerge;
    internal bool PRINTprecision;
    internal bool RANDOMdist;
    internal double RANDOMfactor;
    internal double RANDOMa;
    internal double RANDOMb;
    internal bool RANDOMoutside;
    internal int RERUN;
    internal int ROTATErandom;
    internal bool SCALElast;
    internal bool SETroundoff;
    internal bool SKIPcheckmax;
    internal bool SKIPconvex;
    internal int STOPcone;
    internal int STOPpoint;
    internal bool TESTvneighbors;
    internal int TRACElevel;
    internal int TRACEpoint;
    internal double TRACEdist;
    internal bool VERIFYoutput;
    internal bool VIRTUALmemory;
    internal bool VORONOI;

    // ---- input constants ----
    internal double AREAfactor;
    internal bool DOcheckmax;
    internal bool KEEPnearinside;
    internal int hull_dim;
    internal int input_dim;
    internal int num_points;
    /// <summary>qh.first_point: the (possibly joggled) points, one array each.</summary>
    internal double[][] first_point = [];
    /// <summary>qh.input_points: original points once joggled.</summary>
    internal double[][]? input_points;
    internal bool VERTEXneighbors;
    internal bool ZEROcentrum;
    internal double[] upper_threshold = [];
    internal double[] lower_threshold = [];

    // ---- precision constants ----
    internal double ANGLEround;
    internal double centrum_radius;
    internal double cos_max;
    internal double DISTround;
    internal double MAXabs_coord;
    internal double MAXlastcoord;
    internal double MAXsumcoord;
    internal double MAXwidth;
    internal double MINdenom_1;
    internal double MINdenom;
    internal double MINdenom_1_2;
    internal double MINdenom_2;
    internal double MINlastcoord;
    internal bool NARROWhull;
    internal double[] NEARzero = [];
    internal double NEARinside;
    internal double ONEmerge;
    internal double WIDEfacet;

    // ---- internal constants ----
    internal double[]? interior_point;
    internal int TEMPsize;

    // ---- lists ----
    internal Facet? facet_list;
    internal Facet? facet_tail;
    internal Facet? facet_next;
    internal Facet? newfacet_list;
    internal Facet? visible_list;
    internal int num_visible;
    internal Vertex? vertex_list;
    internal Vertex? vertex_tail;
    internal Vertex? newvertex_list;
    internal int num_facets;
    internal int num_vertices;
    internal int num_outside;
    internal uint facet_id;
    internal uint ridge_id;
    internal uint vertex_id;

    // ---- global variables ----
    internal bool ALLOWrestart;
    internal int build_cnt;
    internal QhCenter CENTERtype;
    internal int furthest_id;
    internal Facet? GOODclosest;
    internal double JOGGLEmax;
    internal bool maxoutdone;
    internal double max_outside;
    internal double max_vertex;
    internal double min_vertex;
    internal bool NEWfacets;
    internal bool findbestnew;
    internal bool findbest_notsharp;
    internal bool NOerrexit;
    internal bool POSTmerging;
    internal bool QHULLfinished;
    internal uint visit_id;
    internal uint vertex_visit;
    internal bool ZEROall_ok;
    internal bool WAScoplanar;

    // ---- global sets ----
    internal QSet<MergeT>? facet_mergeset;
    internal QSet<MergeT>? degen_mergeset;
    internal QSet<Facet>? hash_table;
    internal QSet<Ridge>? ridge_hash_table;
    internal QSet<double[]>? other_points;
    internal QSet<Vertex>? del_vertices;

    // ---- global buffers ----
    internal double[][] gm_matrix = [];
    internal double[]?[] gm_row = [];

    // ---- static variables ----
    internal bool ERREXITcalled;
    internal double last_low;
    internal double last_high;
    internal double last_newhigh;
    internal bool old_randomdist;
    internal QSet<Facet>? searchset;

    // ---- qhstat: only the statistics that steer the algorithm ----
    internal int Ztotmerge;      // qh_addpoint (findbestnew), qh_premerge
    internal int Zsetplane;      // qh_maxsimplex error code
    internal double Wnewvertexmax; // qh_setfacetplane / qh_makenewplanes when joggling

    /// <summary>Storage reused across builds (QhullSession), or null to allocate.</summary>
    internal QhPool? pool;

    // ---- qh_rand (geom2.c, a global in C) ----
    internal int qh_rand_seed = 1;

    /// <summary>The qh_DUPLICATEridge sentinel ((facetT *)1).</summary>
    internal readonly Facet qh_DUPLICATEridge = new() { sentinel = true, id = 0xFFFFFFFE };

    /// <summary>The qh_MERGEridge sentinel ((facetT *)2).</summary>
    internal readonly Facet qh_MERGEridge = new() { sentinel = true, id = 0xFFFFFFFD };

    // ================= user.c =================

    /// <summary>
    /// qh_new_qhull (dim, numpoints, points, ismalloc=False, qhull_cmd, outfile=NULL, errfile)
    /// on a fresh context. Returns the exit code.
    /// </summary>
    internal int NewQhull(int dim, int numpoints, ReadOnlySpan<double> coords, string qhull_cmd)
    {
        if (!qhull_cmd.StartsWith("qhull ", StringComparison.Ordinal))
            throw new ArgumentException("qh_new_qhull: start qhull_cmd argument with \"qhull \"", nameof(qhull_cmd));
        qh_initqhull_start();
        int exitcode = 0;
        try
        {
            NOerrexit = false;
            qh_initflags(qhull_cmd);
            if (DELAUNAY || HALFspace)
                throw new NotSupportedException("qhull port: 'd', 'v' and 'H' are not ported");
            var points = new double[numpoints][];
            for (int i = 0; i < numpoints; i++)
            {
                double[] p = pool != null ? pool.NewPoint(dim + 1) : new double[dim + 1];
                for (int k = 0; k < dim; k++)
                    p[k] = coords[i * dim + k];
                p[dim] = i;
                points[i] = p;
            }
            qh_init_B(points, numpoints, dim);
            qh_qhull();
            qh_check_output();
        }
        catch (QhullExit ex)
        {
            exitcode = ex.ExitCode;
        }
        NOerrexit = true;
        return exitcode;
    }

    /// <summary>qh_errexit: report (not ported: all output goes to a NULL errfile) and longjmp.</summary>
    internal static QhullExit qh_errexit(int exitcode, Facet? facet, Ridge? ridge)
    {
        if (exitcode == 0)
            exitcode = qh_ERRqhull;
        return new QhullExit(exitcode);
    }

    /// <summary>qh_errexit2</summary>
    internal static QhullExit qh_errexit2(int exitcode, Facet? facet, Facet? otherfacet)
        => qh_errexit(exitcode, null, null);

    // ================= global.c =================

    /// <summary>qh_initqhull_start: memset 0 plus the non-zero defaults.</summary>
    internal void qh_initqhull_start()
    {
        // a fresh instance is the memset. The options below are never set by the ported
        // qh_initflags subset; their zero values are assigned explicitly (the memset).
        AVOIDold = false;
        BESToutside = false;
        CHECKfrequently = false;
        DELAUNAY = false;
        GOODpoint = 0;
        GOODpointp = null;
        GOODvertex = 0;
        GOODvertexp = null;
        HALFspace = false;
        IStracing = 0;
        KEEPcoplanar = false;
        KEEPinside = false;
        NOnearinside = false;
        NOpremerge = false;
        ONLYgood = false;
        ONLYmax = false;
        PICKfurthest = false;
        RANDOMfactor = 0.0;
        RANDOMoutside = false;
        RERUN = 0;
        SCALElast = false;
        SKIPcheckmax = false;
        SKIPconvex = false;
        STOPpoint = 0;
        TESTvneighbors = false;
        TRACElevel = 0;
        VERIFYoutput = false;
        VIRTUALmemory = false;
        VORONOI = false;
        // the non-zero defaults
        ANGLEmerge = true;
        furthest_id = -1;
        JOGGLEmax = REALmax;
        last_low = REALmax;
        last_high = REALmax;
        last_newhigh = REALmax;
        max_outside = 0.0;
        max_vertex = 0.0;
        MAXabs_coord = 0.0;
        MAXsumcoord = 0.0;
        MAXwidth = -REALmax;
        MERGEindependent = true;
        MINdenom_1 = 0.0;
        MINoutside = 0.0;
        MINvisible = REALmax;
        MAXcoplanar = REALmax;
        premerge_centrum = 0.0;
        premerge_cos = REALmax;
        PRINTprecision = true;
        postmerge_cos = REALmax;
        postmerge_centrum = 0.0;
        ROTATErandom = int.MinValue;
        MERGEvertices = true;
        TRACEdist = REALmax;
        TRACEpoint = -1;
        // qh_initstatistics: zzdef_(wmax, Wnewvertexmax, ...) starts at -REALmax
        Ztotmerge = 0;
        Zsetplane = 0;
        Wnewvertexmax = -REALmax;
        qh_srand(1);
    }

    /// <summary>
    /// qh_initflags for the options the 3-d subset ports: Qs, QJ[n], C-n, Cn, En, Wn, Pp.
    /// Any other option throws <see cref="NotSupportedException"/>.
    /// </summary>
    internal void qh_initflags(string command)
    {
        int s = 0;
        int len = command.Length;
        char At(int i) => i < len ? command[i] : '\0';
        while (s < len && !IsSpace(command[s]))
            s++;
        while (s < len)
        {
            while (s < len && IsSpace(command[s]))
                s++;
            if (At(s) == '-')
                s++;
            if (s >= len)
                break;
            int prev_s = s;
            char key = command[s++];
            switch (key)
            {
                case 'C':
                    if (!char.IsAsciiDigit(At(s)) && At(s) != '.' && At(s) != '-')
                    {
                        // "no centrum radius given for option 'Cn'.  Ignored."
                    }
                    else
                    {
                        if (At(s) == '-')
                        {
                            premerge_centrum = -qh_strtod(command, ref s);
                            PREmerge = true;
                        }
                        else
                        {
                            postmerge_centrum = qh_strtod(command, ref s);
                            POSTmerge = true;
                        }
                        MERGING = true;
                    }
                    break;
                case 'E':
                    if (At(s) == '-' || !char.IsAsciiDigit(At(s)))
                    {
                        // warning, ignored
                    }
                    else
                    {
                        DISTround = qh_strtod(command, ref s);
                        SETroundoff = true;
                    }
                    break;
                case 'W':
                    if (At(s) == '-' || !char.IsAsciiDigit(At(s)))
                    {
                        // warning, ignored
                    }
                    else
                    {
                        MINoutside = qh_strtod(command, ref s);
                        APPROXhull = true;
                    }
                    break;
                case 'P':
                    while (s < len && !IsSpace(command[s]))
                    {
                        char sub = command[s++];
                        switch (sub)
                        {
                            case 'p':
                                PRINTprecision = false;
                                break;
                            default:
                                throw new NotSupportedException("qhull port: option 'P" + sub + "' is not ported");
                        }
                    }
                    break;
                case 'Q':
                    while (s < len && !IsSpace(command[s]))
                    {
                        char sub = command[s++];
                        switch (sub)
                        {
                            case 's':
                                ALLpoints = true;
                                break;
                            case 'J':
                                if (!char.IsAsciiDigit(At(s)) && At(s) != '-')
                                    JOGGLEmax = 0.0;
                                else
                                    JOGGLEmax = qh_strtod(command, ref s);
                                break;
                            default:
                                throw new NotSupportedException("qhull port: option 'Q" + sub + "' is not ported");
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException("qhull port: option '" + key + "' is not ported");
            }
            if (s - 1 == prev_s && s < len && !IsSpace(command[s]))
            {
                // "missing space after flag"; skipped
                while (s < len && !IsSpace(command[s]))
                    s++;
            }
        }
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';

    /// <summary>qh_strtod: strtod, but a trailing '.' is not consumed.</summary>
    internal static double qh_strtod(string str, ref int s)
    {
        int start = s;
        int i = s;
        int len = str.Length;
        if (i < len && (str[i] == '+' || str[i] == '-'))
            i++;
        int digits = 0;
        while (i < len && char.IsAsciiDigit(str[i]))
        {
            i++;
            digits++;
        }
        if (i < len && str[i] == '.')
        {
            i++;
            while (i < len && char.IsAsciiDigit(str[i]))
            {
                i++;
                digits++;
            }
        }
        if (digits == 0)
            return 0.0; // strtod: no conversion, endp = s
        int mant = i;
        if (i < len && (str[i] == 'e' || str[i] == 'E'))
        {
            int j = i + 1;
            if (j < len && (str[j] == '+' || str[j] == '-'))
                j++;
            if (j < len && char.IsAsciiDigit(str[j]))
            {
                while (j < len && char.IsAsciiDigit(str[j]))
                    j++;
                i = j;
            }
        }
        _ = mant;
        double result = double.Parse(str.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        s = i;
        if (s > start && str[s - 1] == '.')
            s--;
        return result;
    }

    /// <summary>qh_init_B (no projection, scaling or rotation in the ported subset).</summary>
    internal void qh_init_B(double[][] points, int numpoints, int dim)
    {
        qh_initqhull_globals(points, numpoints, dim);
        qh_initqhull_buffers();
    }

    /// <summary>qh_initqhull_buffers</summary>
    internal void qh_initqhull_buffers()
    {
        TEMPsize = 8; // (qhmem.LASTsize - sizeof(setT))/SETelemsize is only a capacity
        other_points = QSet<double[]>.New(TEMPsize, pool);
        del_vertices = QSet<Vertex>.New(TEMPsize, pool);
        searchset = QSet<Facet>.New(TEMPsize, pool);
        NEARzero = new double[hull_dim];
        lower_threshold = new double[input_dim + 1];
        upper_threshold = new double[input_dim + 1];
        for (int k = input_dim + 1; k-- > 0;)
        {
            lower_threshold[k] = -REALmax;
            upper_threshold[k] = REALmax;
        }
        gm_matrix = new double[hull_dim + 1][];
        for (int i = 0; i < hull_dim + 1; i++)
            gm_matrix[i] = new double[hull_dim];
        gm_row = new double[]?[hull_dim + 1];
    }

    /// <summary>qh_initqhull_globals (the parts reachable without 'd', 'v', 'H', printing or tracing).</summary>
    internal void qh_initqhull_globals(double[][] points, int numpoints, int dim)
    {
        first_point = points;
        num_points = numpoints;
        hull_dim = input_dim = dim;
        if (!NOpremerge && !MERGEexact && !PREmerge && JOGGLEmax > REALmax / 2)
        {
            MERGING = true;
            if (hull_dim <= 4)
                PREmerge = true;
            else
                MERGEexact = true;
        }
        if (MERGING && !POSTmerge && premerge_cos > REALmax / 2
            && premerge_centrum == 0)
        {
            ZEROcentrum = true;
            ZEROall_ok = true;
        }
        DOcheckmax = !FORCEoutput && !SKIPcheckmax && MERGING;
        KEEPnearinside = DOcheckmax && !(KEEPinside && KEEPcoplanar)
                         && !NOnearinside;
        if (MERGING)
            CENTERtype = QhCenter.qh_AScentrum;
        else if (VORONOI)
            CENTERtype = QhCenter.qh_ASvoronoi;
        if (TESTvneighbors && !MERGING)
            throw qh_errexit(qh_ERRinput, null, null);
        if (hull_dim <= 1)
            throw qh_errexit(qh_ERRinput, null, null);
        double factorial = 1.0;
        for (int k = 2; k < hull_dim; k++)
            factorial *= k;
        AREAfactor = 1.0 / factorial;
        int pointsneeded = hull_dim + 1;
        if (hull_dim > qh_DIMmergeVertex)
            MERGEvertices = false;
        if (GOODpoint != 0)
            pointsneeded++;
        int seed = ROTATErandom;
        if (seed == int.MinValue)
            seed = 1;
        else if (seed < 0)
            seed = -seed;
        // qh_RANDOMseed_(seed); 1000 qh_RANDOMint draws only check qh_RANDOMmax; qh_RANDOMseed_(seed)
        qh_srand(seed);
        RANDOMa = 2.0 * RANDOMfactor / qh_RANDOMmax;
        RANDOMb = 1.0 - RANDOMfactor;
        if (numpoints < pointsneeded)
            throw qh_errexit(qh_ERRinput, null, null);
    }

    // ================= geom2.c: qh_rand =================

    /// <summary>qh_rand (Park and Miller minimal standard generator)</summary>
    internal int qh_rand()
    {
        const int qh_rand_a = 16807;
        const int qh_rand_m = 2147483647;
        const int qh_rand_q = 127773;
        const int qh_rand_r = 2836;
        int seed = qh_rand_seed;
        int hi = seed / qh_rand_q;
        int lo = seed % qh_rand_q;
        int test = qh_rand_a * lo - qh_rand_r * hi;
        if (test > 0)
            seed = test;
        else
            seed = test + qh_rand_m;
        qh_rand_seed = seed;
        return seed;
    }

    /// <summary>qh_srand</summary>
    internal void qh_srand(int seed)
    {
        if (seed < 1)
            qh_rand_seed = 1;
        else if (seed >= 2147483647)
            qh_rand_seed = 2147483647 - 1;
        else
            qh_rand_seed = seed;
    }
}
