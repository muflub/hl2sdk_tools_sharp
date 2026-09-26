//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using SourceSharp.Tests.MapTools.Rad.Light;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Phys.Managed.Qhull;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>Which arithmetic a managed cook uses.</summary>
public enum CookMode
{
    /// <summary>The earlier reference build's arithmetic: float, rsqrt estimate, every defect reproduced.</summary>
    Stock,

    /// <summary>The later reference build's: double, every defect reproduced (the parity mode against that build).</summary>
    Tf2,

    /// <summary>The later reference build's double arithmetic with the defects fixed (the default).</summary>
    Correct,
}

/// <summary>
/// The cooker goldens: job files and the reference collision cooker's answers for them, committed
/// under <c>Fixtures/p8a-cooker</c>.
/// </summary>
/// <remarks>
/// Every answer was produced by the reference collision cooker itself — the earlier build in
/// float and the later build in double — on an AMD Ryzen 9 9950X, so the
/// unit tier needs no native code at all.
/// </remarks>
internal static class CookerFixture
{
    /// <summary>The job groups.</summary>
    public static readonly string[] Groups = ["shapes", "brushes", "clouds", "multi"];

    /// <summary>One convex in a job.</summary>
    public sealed record Convex(char Kind, float Merge, float[] Data);

    /// <summary>One cook: its convexes and whether to build the outer hull.</summary>
    public sealed record Job(Convex[] Convexes, bool Outer, int GameData);

    /// <summary>A reference answer: the whole blob, or its hash and length, or null.</summary>
    public sealed record Answer(byte[]? Blob, string? Sha256, int Length, bool IsNull);

    /// <summary>The fixture directory of the tree this test binary was built from.</summary>
    /// <returns>The directory.</returns>
    public static string FixtureDirectory()
    {
        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("no checkout root above the test binary");
        return Path.Combine(root, "src", "SourceSharp.Tests", "Fixtures", "p8a-cooker");
    }

    /// <summary>Loads a group's jobs.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The jobs.</returns>
    public static List<Job> Jobs(string group)
    {
        var jobs = new List<Job>();
        var pending = new List<Convex>();
        using var reader = OpenGz(Path.Combine(FixtureDirectory(), group + ".in.gz"));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "P":
                case "V":
                {
                    int n = int.Parse(t[1], CultureInfo.InvariantCulture);
                    float merge = t[0] == "P" ? (float)ParseC99(t[2]) : 0f;
                    int w = t[0] == "P" ? 4 : 3;
                    float[] data = new float[n * w];
                    for (int i = 0; i < n; i++)
                    {
                        string[] v = reader.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int k = 0; k < w; k++)
                        {
                            data[(i * w) + k] = (float)ParseC99(v[k]);
                        }
                    }

                    pending.Add(new Convex(t[0][0], merge, data));
                    break;
                }

                case "C":
                {
                    int k = int.Parse(t[1], CultureInfo.InvariantCulture);
                    jobs.Add(new Job([.. pending.GetRange(pending.Count - k, k)], t[2] != "0", 0));
                    pending.Clear();
                    break;
                }
            }
        }

        return jobs;
    }

    /// <summary>Loads a group's reference answers.</summary>
    /// <param name="group">The group.</param>
    /// <param name="build">"sdk" or "tf2".</param>
    /// <returns>One answer per job.</returns>
    public static List<Answer> Answers(string group, string build)
    {
        var answers = new List<Answer>();
        using var reader = OpenGz(Path.Combine(FixtureDirectory(), group + "." + build + ".txt.gz"));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("hex ", StringComparison.Ordinal))
            {
                byte[] b = Convert.FromHexString(line.AsSpan(4).Trim());
                answers.Add(new Answer(b, null, b.Length, false));
            }
            else if (line.StartsWith("sha ", StringComparison.Ordinal))
            {
                string[] t = line.Split(' ');
                answers.Add(new Answer(null, t[1], int.Parse(t[2], CultureInfo.InvariantCulture), false));
            }
            else if (line.StartsWith("collide null", StringComparison.Ordinal))
            {
                answers.Add(new Answer(null, null, 0, true));
            }
        }

        return answers;
    }

    /// <summary>A fresh cook context for a mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The context.</returns>
    public static IvpCookContext Context(CookMode mode) =>
        new(new QhullRunner()) { SkipZeroLengthInertiaEdges = mode == CookMode.Correct };

    /// <summary>Cooks one job the way the reference collision cooker does, returning the VPHY blob or null.</summary>
    /// <param name="job">The job.</param>
    /// <param name="mode">The arithmetic.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The blob.</returns>
    public static byte[]? Cook(Job job, CookMode mode, IvpCookContext context)
    {
        bool single = mode == CookMode.Stock;
        var ledges = new List<IvpCompactLedge?>(job.Convexes.Length);
        foreach (Convex c in job.Convexes)
        {
            if (c.Kind == 'P')
            {
                var planes = new (float, float, float, float)[c.Data.Length / 4];
                for (int i = 0; i < planes.Length; i++)
                {
                    planes[i] = (c.Data[4 * i], c.Data[(4 * i) + 1], c.Data[(4 * i) + 2], c.Data[(4 * i) + 3]);
                }

                ledges.Add(single
                    ? IvpCooker<float, StockPrecision>.ConvexFromPlanes(planes, c.Merge, context)
                    : IvpCooker<double, CorrectPrecision>.ConvexFromPlanes(planes, c.Merge, context));
            }
            else
            {
                var points = new (float, float, float)[c.Data.Length / 3];
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = (c.Data[3 * i], c.Data[(3 * i) + 1], c.Data[(3 * i) + 2]);
                }

                ledges.Add(single
                    ? IvpCooker<float, StockPrecision>.ConvexFromVerts(points, context)
                    : IvpCooker<double, CorrectPrecision>.ConvexFromVerts(points, context));
            }
        }

        return single
            ? IvpCooker<float, StockPrecision>.ConvertConvexToCollide(ledges, job.Outer, (1f, 1f, 1f), context)
            : IvpCooker<double, CorrectPrecision>.ConvertConvexToCollide(ledges, job.Outer, (1f, 1f, 1f), context);
    }

    /// <summary>Whether a managed blob is the reference answer, byte for byte.</summary>
    /// <param name="answer">The reference answer.</param>
    /// <param name="managed">The managed blob.</param>
    /// <returns>True when identical.</returns>
    public static bool Matches(Answer answer, byte[]? managed)
    {
        if (answer.IsNull || managed is null)
        {
            return answer.IsNull && managed is null;
        }

        if (answer.Blob is not null)
        {
            return answer.Blob.AsSpan().SequenceEqual(managed);
        }

        return managed.Length == answer.Length
            && string.Equals(Convert.ToHexStringLower(SHA256.HashData(managed)), answer.Sha256, StringComparison.Ordinal);
    }

    /// <summary>A reference answer as one line: <c>null</c>, or the blob's SHA-256 and length.</summary>
    /// <param name="answer">The answer.</param>
    /// <returns>The line a vendor delta records.</returns>
    public static string Line(Answer answer) =>
        answer.IsNull
            ? "null"
            : answer.Blob is not null
                ? Line(answer.Blob)
                : $"{answer.Sha256} {answer.Length}";

    /// <summary>A managed blob as the same line <see cref="Line(Answer)"/> gives an answer.</summary>
    /// <param name="managed">The blob, or null for no collide.</param>
    /// <returns>The line.</returns>
    public static string Line(byte[]? managed) =>
        managed is null
            ? "null"
            : $"{Convert.ToHexStringLower(SHA256.HashData(managed))} {managed.Length}";

    private static StreamReader OpenGz(string path) =>
        new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));

    /// <summary>Parses a C99 hex float (<c>%a</c>) or a decimal.</summary>
    /// <param name="s">The text.</param>
    /// <returns>The value.</returns>
    public static double ParseC99(string s)
    {
        if (!s.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        bool negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }

        s = s[2..];
        int p = s.IndexOfAny(['p', 'P']);
        string mantissa = p < 0 ? s : s[..p];
        int exponent = p < 0 ? 0 : int.Parse(s[(p + 1)..], CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.');
        string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;
        double v = 0;
        foreach (char c in digits)
        {
            v = (v * 16) + Convert.ToInt32(c.ToString(), 16);
        }

        v = Math.ScaleB(v, exponent - (4 * fraction));
        return negative ? -v : v;
    }
}
