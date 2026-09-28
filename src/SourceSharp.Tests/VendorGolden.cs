//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// run is what shows the port reproduces stock given its estimate; a
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
    /// the delta is rewritten from <paramref name="actual"/> and the call throws.
    /// </summary>
    /// <exception cref="VendorGoldenCapturedException">
    /// This run is capturing and wrote the delta: the fact fails on purpose.
    /// </exception>
    public static IReadOnlyList<string> Expected(
        string key, IReadOnlyList<string> stock, IReadOnlyList<string> actual)
    {
        string? vendor = ReferenceRsqrt.CpuVendor();
        return Expected(key, stock, actual, vendor, vendor is null ? null : Directory(vendor), Capturing);
    }

    /// <summary>
    /// <see cref="Expected(string, IReadOnlyList{string}, IReadOnlyList{string})"/>
    /// with the CPU vendor, its capture directory and the capture switch given
    /// rather than read from the machine, so both sides of capturing can be
    /// tested on any host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A CAPTURE NEVER PASSES.</b> After writing the delta, a capturing call
    /// throws <see cref="VendorGoldenCapturedException"/>, so every fact that
    /// captured fails and names the file it wrote. If capturing returned the
    /// values it had just written, each fact would compare its output with
    /// itself and pass, and a capture run would look exactly like a green run
    /// while pinning whatever the port produced, regressions included. The
    /// failure is the prompt to review the delta (its size is how far the
    /// vendor moved) and commit it; a second run without the variable is the
    /// one that checks them.
    /// </para>
    /// <para>
    /// The reference vendor has nothing to capture (its expected values are
    /// stock's own), so it neither writes nor fails.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> Expected(
        string key,
        IReadOnlyList<string> stock,
        IReadOnlyList<string> actual,
        string? vendor,
        string? directory,
        bool capturing)
    {
        if (vendor == ReferenceRsqrt.ReferenceVendor)
        {
            return stock;
        }

        string dir = (vendor is null ? null : directory)
            ?? throw new InvalidOperationException(
                "no vendor capture directory: this CPU is neither x86 nor arm64, or the test is not in a checkout");
        string path = Path.Combine(dir, key + ".txt");

        if (capturing)
        {
            string delta = Delta(stock, actual);
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(path, delta);
            int moved = delta.Count(c => c == '\n') - 1;
            throw new VendorGoldenCapturedException(
                $"captured {vendor} delta for '{key}' ({moved} of {actual.Count} lines differ from stock) "
                + $"to {path}: review and commit it. A capture run always fails; rerun without "
                + $"{CaptureVariable} to check the committed values.");
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

/// <summary>
/// Thrown by <see cref="VendorGolden.Expected(string, IReadOnlyList{string}, IReadOnlyList{string})"/>
/// after a capture run wrote a delta, so the fact fails with the file's path
/// instead of passing against the values it just wrote.
/// </summary>
internal sealed class VendorGoldenCapturedException(string message) : Exception(message);
