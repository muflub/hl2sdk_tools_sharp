// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port to C# for a managed
// collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

using System.Globalization;
using System.Text;
using SourceSharp.MapTools.Phys.Managed.Qhull;

namespace SourceSharp.Tests.MapTools.Phys.Managed.Qhull;

/// <summary>A named point set of the differential corpus.</summary>
internal sealed class PointSet
{
    /// <summary>Creates a point set.</summary>
    public PointSet(string name, double[] xyz)
    {
        Name = name;
        Xyz = xyz;
    }

    /// <summary>Set name (no spaces).</summary>
    public string Name { get; }

    /// <summary>Coordinates, three per point.</summary>
    public double[] Xyz { get; }
}

/// <summary>
/// The corpus text formats shared with the reference dumper: point sets in
/// ("set NAME N" + N lines of three C99 hex floats), hull dumps out.
/// </summary>
internal static class HullText
{
    /// <summary>Reads a point-set file (the reference dumper's input format).</summary>
    public static List<PointSet> ReadSets(TextReader reader)
    {
        var sets = new List<PointSet>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] head = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length != 3 || head[0] != "set")
                throw new FormatException("bad set line: " + line);
            int n = int.Parse(head[2], CultureInfo.InvariantCulture);
            var xyz = new double[3 * n];
            for (int i = 0; i < n; i++)
            {
                string? pl = reader.ReadLine() ?? throw new FormatException("truncated set " + head[1]);
                string[] c = pl.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (c.Length != 3)
                    throw new FormatException("bad point line in " + head[1] + ": " + pl);
                for (int k = 0; k < 3; k++)
                    xyz[3 * i + k] = ParseDouble(c[k]);
            }
            sets.Add(new PointSet(head[1], xyz));
        }
        return sets;
    }

    /// <summary>Parses a decimal or C99 hexadecimal floating constant exactly (like strtod).</summary>
    public static double ParseDouble(string s)
    {
        int i = 0;
        bool neg = false;
        if (s[i] == '-' || s[i] == '+')
        {
            neg = s[i] == '-';
            i++;
        }
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            i += 2;
            ulong mant = 0;
            int nd = 0;
            int fracdigits = 0;
            bool frac = false;
            for (; i < s.Length && s[i] != 'p' && s[i] != 'P'; i++)
            {
                if (s[i] == '.')
                {
                    frac = true;
                    continue;
                }
                int hv = HexVal(s[i]);
                if (mant == 0 && hv == 0)
                {
                    if (frac)
                        fracdigits++;
                    continue;
                }
                if (++nd > 15)
                    throw new FormatException("hex float with more than 15 significant digits: " + s);
                mant = (mant << 4) + (ulong)hv;
                if (frac)
                    fracdigits++;
            }
            int exp = 0;
            if (i < s.Length)
                exp = int.Parse(s.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            exp -= 4 * fracdigits;
            double v = ScaleExact(mant, exp);
            return neg ? -v : v;
        }
        double d = double.Parse(s.AsSpan(i), NumberStyles.Float, CultureInfo.InvariantCulture);
        return neg ? -d : d;
    }

    private static int LeadingZeros(ulong x)
    {
        int n = 0;
        while (n < 64 && (x & (1UL << (63 - n))) == 0)
            n++;
        return n;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        throw new FormatException("bad hex digit " + c);
    }

    private static double ScaleExact(ulong mant, int exp)
    {
        if (mant == 0)
            return 0.0;
        // the corpus only holds values that are exact doubles: shift into 53 bits exactly
        int bits = 64 - LeadingZeros(mant);
        if (bits > 53)
        {
            int shift = bits - 53;
            if ((mant & ((1UL << shift) - 1)) != 0)
                throw new FormatException("hex float not exactly representable");
            mant >>= shift;
            exp += shift;
        }
        double v = Math.ScaleB((double)mant, exp);
        if (Math.ScaleB(v, -exp) != (double)mant)
            throw new FormatException("hex float not exactly representable (subnormal)");
        return v;
    }

    /// <summary>C printf "%a" as glibc prints a double.</summary>
    public static string FormatA(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0;
        int bexp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        var sb = new StringBuilder();
        if (bexp == 0x7FF)
        {
            if (frac == 0)
                return neg ? "-inf" : "inf";
            return neg ? "-nan" : "nan";
        }
        if (neg)
            sb.Append('-');
        int exp;
        char lead;
        if (bexp == 0)
        {
            if (frac == 0)
            {
                sb.Append("0x0p+0");
                return sb.ToString();
            }
            lead = '0';
            exp = -1022;
        }
        else
        {
            lead = '1';
            exp = bexp - 1023;
        }
        sb.Append("0x").Append(lead);
        string hex = frac.ToString("x13", CultureInfo.InvariantCulture).TrimEnd('0');
        if (hex.Length > 0)
            sb.Append('.').Append(hex);
        sb.Append('p').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Writes one set's result in the golden's format.</summary>
    public static void WriteResult(StringBuilder sb, PointSet set, QhullResult r)
    {
        sb.Append("set ").Append(set.Name).Append(' ').Append((set.Xyz.Length / 3).ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (int k = 0; k < r.Commands.Count; k++)
        {
            sb.Append("try ").Append(k.ToString(CultureInfo.InvariantCulture)).Append(" \"").Append(r.Commands[k])
              .Append("\" exit ").Append(r.ExitCodes[k].ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (r.ExitCodes[k] == 0)
            {
                sb.Append("ok ").Append(k.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (QhullFacet f in r.Facets)
                {
                    sb.Append("f ").Append(f.Id.ToString(CultureInfo.InvariantCulture))
                      .Append(" t").Append(f.TopOrient ? '1' : '0')
                      .Append(" s").Append(f.Simplicial ? '1' : '0')
                      .Append(' ').Append(FormatA(f.NormalX))
                      .Append(' ').Append(FormatA(f.NormalY))
                      .Append(' ').Append(FormatA(f.NormalZ))
                      .Append(' ').Append(FormatA(f.Offset))
                      .Append(" :");
                    foreach (int id in f.PointIds)
                        sb.Append(' ').Append(id.ToString(CultureInfo.InvariantCulture));
                    sb.Append('\n');
                }
            }
        }
        if (r.ExitCode != 0)
            sb.Append("fail\n");
        sb.Append("end\n");
    }

    /// <summary>Splits a golden dump into per-set blocks keyed by order.</summary>
    public static List<(string Name, string Text)> SplitBlocks(string text)
    {
        var blocks = new List<(string, string)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int end = text.IndexOf("end\n", pos, StringComparison.Ordinal);
            if (end < 0)
                throw new FormatException("unterminated block at " + pos);
            string block = text.Substring(pos, end + 4 - pos);
            int sp1 = block.IndexOf(' ');
            int sp2 = block.IndexOf(' ', sp1 + 1);
            blocks.Add((block.Substring(sp1 + 1, sp2 - sp1 - 1), block));
            pos = end + 4;
        }
        return blocks;
    }
}
