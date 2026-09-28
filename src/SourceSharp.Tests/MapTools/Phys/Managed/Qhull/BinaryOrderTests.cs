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

/// <summary>
/// The port follows the reference collision cooker's floating-point grouping, not
/// qhull 2.6's own. Each fact picks inputs where the two groupings round differently.
/// </summary>
public class BinaryOrderTests
{
    private static double H(string hex) => HullText.ParseDouble(hex);

    private static Qh Qh3()
    {
        var qh = new Qh();
        qh.hull_dim = 3;
        return qh;
    }

    [Fact]
    public void DistplaneGroupsAsTheBinary()
    {
        // The reference cooker groups distplane as (x*nx + y*ny) + (offset + z*nz); qhull's
        // qh_distplane groups it as ((offset + x*nx) + y*ny) + z*nz.
        var qh = Qh3();
        double[] p = { H("-0x1.179e18d58c670p+0"), H("0x1.1b923ce04acecp+2"), H("-0x1.5b2f32016a5b0p+2"), 0 };
        var f = new Facet { normal = new[] { H("0x1.c7f50a8d15c78p-1"), H("0x1.9b0fcca8a188cp-1"), H("-0x1.e0ad04fd248cap-1") }, offset = H("-0x1.2fb6f33c094b9p+3") };
        qh.qh_distplane(p, f, out double dist);
        Assert.Equal(H("-0x1.d049950320334p+0"), dist);
        Assert.NotEqual(H("-0x1.d049950320330p+0"), dist); // the source grouping
    }

    [Fact]
    public void GetcenterMultipliesByTheReciprocalOfTheCount()
    {
        // The reference cooker's getcenter is sum * (1.0/count); qhull's qh_getcenter is sum / count.
        var qh = Qh3();
        double s = H("-0x1.301dfda4533e4p+1");
        QSet<Vertex>? vs = new QSet<Vertex>(3);
        foreach (double x in new[] { s, 0.0, 0.0 })
            QSet<Vertex>.Append(ref vs, new Vertex { point = new[] { x, 0.0, 0.0, -1 } }, null);
        double[] c = qh.qh_getcenter(vs!);
        Assert.Equal(H("-0x1.957d52306efdap-1"), c[0]);
        Assert.NotEqual(s / 3, c[0]); // the source's division
    }

    [Fact]
    public void FabsClearsTheSignOfNegativeZero()
    {
        // The reference cooker compiles fabs_ to andpd, which clears -0.0; the macro
        // ((a) < 0) ? -(a) : (a) keeps -0.0.
        Assert.False(double.IsNegative(Qh.fabs_(-0.0)));
    }

    [Fact]
    public void JoggleGroupsAsTheBinaryWhereTheFormsDiffer()
    {
        // The reference cooker's joggle is (in - J) + r*(J*(2/RANDOMmax)); qhull's
        // qh_joggleinput is in + (r*(2*J/RANDOMmax) + -J). 'QJ1.8146E-05' is one of IVP's
        // joggles where they differ.
        double joggle = 1.8146E-05;
        double input = H("0x1.44a6780000000p-10");
        var qh = Qh3();
        qh.num_points = 1;
        qh.first_point = new[] { new[] { input, input, input, 0.0 } };
        qh.JOGGLEmax = joggle;
        qh.build_cnt = 1;
        qh.qh_srand(1);
        qh.qh_joggleinput();

        var rand = Qh3();
        rand.qh_srand(1);
        _ = rand.qh_rand(); // the joggle seed draw
        double r0 = rand.qh_rand();
        double binary = (input - joggle) + r0 * (joggle * (2.0 / QhConst.qh_RANDOMmax));
        double source = input + (r0 * (2.0 * joggle / QhConst.qh_RANDOMmax) + -joggle);
        Assert.NotEqual(source, binary);
        Assert.Equal(binary, qh.first_point[0][0]);
    }

    [Fact]
    public void JoggleScalesByTheFoldedConstantWhereItDiffers()
    {
        // The reference cooker's random draw is randa = J * (2/RANDOMmax) rather than
        // 2*J/RANDOMmax; for this input the two randa give different joggled coordinates
        // under the reference grouping.
        double joggle = 1.8146E-05;
        double input = H("0x1.eb2c120000000p-14");
        var qh = Qh3();
        qh.num_points = 1;
        qh.first_point = new[] { new[] { input, input, input, 0.0 } };
        qh.JOGGLEmax = joggle;
        qh.build_cnt = 1;
        qh.qh_srand(1);
        qh.qh_joggleinput();
        Assert.Equal(H("0x1.b315bf3e7e502p-14"), qh.first_point[0][0]);
        Assert.NotEqual(H("0x1.b315bf3e7e503p-14"), qh.first_point[0][0]); // with randa = 2*J/RANDOMmax
    }

    [Fact]
    public void JoggleUsesTheFoldedTwoOverRandomMax()
    {
        // The reference cooker folds 2.0/qh_RANDOMmax: JOGGLEmax * 0x1.0000000400000p-30.
        double folded = 2.0 / QhConst.qh_RANDOMmax;
        Assert.Equal(0x3E10000000400000L, BitConverter.DoubleToInt64Bits(folded));
    }
}
