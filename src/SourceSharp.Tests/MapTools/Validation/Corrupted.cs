using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Validation;
using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// Takes the golden map and breaks one field of it, in memory.
/// </summary>
/// <remarks>
/// <para>
/// Every corruption fact starts from the same committed file and changes one
/// thing, so "this rule fired" can only be because of that one thing: the
/// control fact asserts the untouched map produces no findings at all.
/// </para>
/// <para>
/// Nothing here writes to disk. <c>dm_lockdown.bsp</c> is a tracked file and a
/// test that edited it would leave a broken map behind for every other lane.
/// </para>
/// </remarks>
internal static class Corrupted
{
    /// <summary>The golden map, freshly parsed.</summary>
    /// <returns>A container no other fact shares.</returns>
    public static async Task<BspData> GoldenAsync()
    {
        await using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        return await BspFile.LoadAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The golden map's bytes, for corrupting the header itself.</summary>
    /// <returns>A fresh array.</returns>
    public static byte[] GoldenBytes() => File.ReadAllBytes(GoldenBsp.Lockdown());

    /// <summary>
    /// A writable view of one lump, after replacing its bytes with a private
    /// copy so that writes through the view cannot reach any other fact.
    /// </summary>
    /// <typeparam name="T">The lump's element struct.</typeparam>
    /// <param name="bsp">The container to edit.</param>
    /// <param name="lump">Which lump.</param>
    /// <returns>A span over the copy the container now holds.</returns>
    public static Span<T> Edit<T>(BspData bsp, BspLump lump)
        where T : unmanaged
    {
        byte[] copy = EditBytes(bsp, lump);
        return MemoryMarshal.Cast<byte, T>(copy.AsSpan());
    }

    /// <summary>
    /// A writable copy of one lump's raw bytes, installed in the container.
    /// </summary>
    /// <param name="bsp">The container to edit.</param>
    /// <param name="lump">Which lump.</param>
    /// <returns>The array the container now holds.</returns>
    public static byte[] EditBytes(BspData bsp, BspLump lump)
    {
        BspLumpData data = bsp[lump];
        byte[] copy = data.Data.ToArray();
        bsp[lump] = data with { Data = copy };
        return copy;
    }

    /// <summary>Replaces a lump's bytes and version outright.</summary>
    /// <param name="bsp">The container to edit.</param>
    /// <param name="lump">Which lump.</param>
    /// <param name="data">The new payload.</param>
    /// <param name="version">The lump version to record.</param>
    public static void Replace(BspData bsp, BspLump lump, byte[] data, int version) =>
        bsp[lump] = new BspLumpData(data, version, 0);

    /// <summary>Runs the whole rule set over a container.</summary>
    /// <param name="bsp">The container to check.</param>
    /// <returns>The report.</returns>
    public static Task<ValidationReport> CheckAsync(BspData bsp) =>
        BspValidator.CheckAsync(bsp, CancellationToken.None);

    /// <summary>
    /// Asserts a report carries at least one finding under <paramref name="code"/>.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="code">The code the corruption should have produced.</param>
    public static void Fires(ValidationReport report, string code)
    {
        Assert.False(
            report.ForCode(code).IsEmpty,
            $"expected {code}; got {Describe(report)}");
    }

    /// <summary>
    /// Asserts a report carries findings under <paramref name="code"/> and
    /// under no other code.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="code">The only code the corruption should have produced.</param>
    public static void OnlyFires(ValidationReport report, string code)
    {
        Fires(report, code);

        string[] others = report.Diagnostics
            .Select(d => d.Code)
            .Where(c => c != code)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            others.Length == 0,
            $"corrupting one field for {code} also produced {string.Join(", ", others)}: "
            + Describe(report));
    }

    private static string Describe(ValidationReport report) =>
        report.Diagnostics.IsEmpty
            ? "no findings at all"
            : Environment.NewLine
              + string.Join(
                  Environment.NewLine,
                  report.Diagnostics.Select(d => $"  {d.Code} {d.Severity}: {d.Message}"));
}

/// <summary>
/// A <see cref="FactAttribute"/> that records which validation rule the fact
/// proves can fire.
/// </summary>
/// <remarks>
/// This is what makes "every rule has a mutation proof" checkable rather than
/// asserted: <c>RuleCoverageTests</c> reflects over these and requires the set
/// of codes they name to be exactly <c>BspRuleCatalog.All</c>. A rule added
/// without a fact fails that test, and so does a fact naming a code that does
/// not exist.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CorruptsAttribute : FactAttribute
{
    /// <summary>Records the rule this fact makes fire.</summary>
    /// <param name="code">A code from <see cref="BspRuleCodes"/>.</param>
    public CorruptsAttribute(string code) => Code = code;

    /// <summary>The rule this fact makes fire.</summary>
    public string Code { get; }
}

/// <summary>
/// Severity assertions shared by the rule facts.
/// </summary>
internal static class Severity
{
    /// <summary>Asserts every finding under a code has the severity given.</summary>
    /// <param name="report">The report.</param>
    /// <param name="code">The code.</param>
    /// <param name="severity">The severity the catalogue promises.</param>
    public static void Is(ValidationReport report, string code, DiagnosticSeverity severity)
    {
        foreach (CompileDiagnostic diagnostic in report.ForCode(code))
        {
            Assert.Equal(severity, diagnostic.Severity);
        }
    }
}
