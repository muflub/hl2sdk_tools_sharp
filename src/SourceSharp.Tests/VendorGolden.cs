using System.Globalization;
using System.Text;

using SourceSharp.MapGen;

namespace SourceSharp.Tests;

/// <summary>
/// Per-vendor expected values for the facts that compare a stock-normalised
/// result against a golden. See <see cref="ReferenceRsqrt"/> for why they differ
/// by vendor at all.
///
/// <para>
/// <b>WHERE THE VALUES COME FROM.</b> On <see cref="ReferenceRsqrt.ReferenceVendor"/>
/// the expected values are the committed stock goldens, cut from the reference
/// tools. On any other vendor they are the stock goldens with that vendor's
/// DELTA applied: the lines where this port's own output on that vendor differs
/// from them, captured with <see cref="CaptureVariable"/> set. Those captures are
/// SELF-CAPTURED regression values, not reference output. The reference-vendor
/// run is what shows the port reproduces stock given an <c>rsqrtss</c>; a
/// capture only pins what the port produces on the other vendor, so a later
/// change to it is caught. Replacing a capture with the reference tools' own
/// output on that vendor turns it into a parity check without touching a fact.
/// </para>
///
/// <para>
/// A delta file is plain text under
/// <c>src/SourceSharp.Tests/Fixtures/rsqrt-vendor/&lt;vendor&gt;/&lt;key&gt;.txt</c>:
/// a <c>count N</c> line, then one <c>index value</c> line per line that
/// differs from stock. Its size is therefore how far the vendor moves the
/// result, readable in a diff.
/// </para>
/// </summary>
internal static class VendorGolden
{
    /// <summary>Set to 1 to (re)write this vendor's deltas from this run's output.</summary>
    public const string CaptureVariable = "SS_CAPTURE_VENDOR_GOLDENS";

    /// <summary>Whether this run writes captures rather than reading them.</summary>
    public static bool Capturing =>
        Environment.GetEnvironmentVariable(CaptureVariable) == "1";

    /// <summary>The capture directory for a vendor, whether or not it exists; null outside a checkout.</summary>
    public static string? Directory(string vendor)
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        return root is null
            ? null
            : Path.Combine(root, "src", "SourceSharp.Tests", "Fixtures", "rsqrt-vendor", vendor);
    }

    /// <summary>Whether a vendor has a capture directory in this checkout.</summary>
    public static bool HasCaptures(string vendor) =>
        Directory(vendor) is { } dir && System.IO.Directory.Exists(dir);

    /// <summary>
    /// The expected lines on this CPU: <paramref name="stock"/> on the reference
    /// vendor, stock with this vendor's delta applied otherwise. When capturing,
    /// the delta is rewritten from <paramref name="actual"/> first.
    /// </summary>
    public static IReadOnlyList<string> Expected(
        string key, IReadOnlyList<string> stock, IReadOnlyList<string> actual)
    {
        string? vendor = ReferenceRsqrt.CpuVendor();
        if (vendor == ReferenceRsqrt.ReferenceVendor)
        {
            return stock;
        }

        string dir = (vendor is null ? null : Directory(vendor))
            ?? throw new InvalidOperationException(
                "no vendor capture directory: this CPU is not x86 or the test is not in a checkout");
        string path = Path.Combine(dir, key + ".txt");

        if (Capturing)
        {
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(path, Delta(stock, actual));
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"no {vendor} capture for '{key}'. Run the suite on a {vendor} host with "
                + $"{CaptureVariable}=1 to record one, and commit it.");
        }

        return Apply(stock, File.ReadAllText(path));
    }

    /// <summary>The single-value form of <see cref="Expected"/>, for a float fact.</summary>
    public static float Expected(string key, float stock, float actual) =>
        FromBits(Expected(key, [Bits(stock)], [Bits(actual)])[0]);

    /// <summary>A float as its exact bit pattern, the unit these facts compare in.</summary>
    public static string Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The float a <see cref="Bits"/> string names.</summary>
    public static float FromBits(string bits) =>
        BitConverter.UInt32BitsToSingle(uint.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>The lines of <paramref name="actual"/> that differ from <paramref name="stock"/>, as a delta file.</summary>
    public static string Delta(IReadOnlyList<string> stock, IReadOnlyList<string> actual)
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"count {actual.Count}\n");
        for (int i = 0; i < actual.Count; i++)
        {
            if (i >= stock.Count || !string.Equals(stock[i], actual[i], StringComparison.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"{i} {actual[i]}\n");
            }
        }

        return text.ToString();
    }

    /// <summary><paramref name="stock"/> with a delta file applied.</summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> stock, string delta)
    {
        string[] lines = delta.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("count ", StringComparison.Ordinal))
        {
            throw new FormatException("a vendor delta starts with a 'count N' line");
        }

        int count = int.Parse(lines[0].AsSpan(6), CultureInfo.InvariantCulture);
        string?[] result = new string?[count];
        for (int i = 0; i < count && i < stock.Count; i++)
        {
            result[i] = stock[i];
        }

        foreach (string line in lines.AsSpan(1))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                throw new FormatException($"a vendor delta line is 'index value', not '{line}'");
            }

            result[int.Parse(line.AsSpan(0, space), CultureInfo.InvariantCulture)] = line[(space + 1)..];
        }

        int missing = Array.IndexOf(result, null);
        if (missing >= 0)
        {
            throw new FormatException(
                $"the delta names {count} lines but stock has {stock.Count} and line {missing} is in neither");
        }

        return result!;
    }
}
