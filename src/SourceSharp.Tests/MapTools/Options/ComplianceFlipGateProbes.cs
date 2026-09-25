using System.Collections.Immutable;

using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The probe registry, split by stage family: <c>ProbesA</c> is vbsp's tree and
/// collision content, <c>ProbesB</c> the surface passes (cubemaps, material
/// patches, overlays, static props) and displacement lump, <c>ProbesC</c> the
/// light/bounce side of vrad, <c>ProbesD</c> its ambient, final-light, prop,
/// phys and tracing side. Each family file registers its own probes and is the
/// only file that names its quirks, so the fleet census (every quirk exactly
/// once across A–D) is what the completeness fact checks.
/// </summary>
/// <remarks>
/// A probe's <c>Fingerprint</c> is the call sequence the quirk's existing
/// per-quirk effect fact already makes, turned into a function of the policy.
/// The gate then runs it four times — the two baselines and the two records
/// <c>-compliance correct,+Q</c> / <c>-compliance stock,-Q</c> build on the
/// quirk's owning verb's real parser — so the row's evidence is CLI evidence.
/// </remarks>
internal static partial class ComplianceFlipGateProbes
{
    /// <summary>Every registered probe, keyed by quirk.</summary>
    /// <returns>The merged registry.</returns>
    /// <remarks>Cheap and stateless: each family dictionary is built fresh.</remarks>
    internal static Dictionary<StockQuirk, FlipGateProbe> Probes()
    {
        Dictionary<StockQuirk, FlipGateProbe> all = [];
        Dictionary<StockQuirk, FlipGateProbe>[] families =
            [ProbesA(), ProbesB(), ProbesC(), ProbesD(), ProbesE()];
        foreach (Dictionary<StockQuirk, FlipGateProbe> family in families)
        {
            foreach ((StockQuirk quirk, FlipGateProbe probe) in family)
            {
                all[quirk] = probe;
            }
        }

        return all;
    }

    /// <summary>Quirks no managed-mode fixture reaches, with their proof.</summary>
    internal static Dictionary<StockQuirk, FlipGateRefused> Refused { get; } = [];

    /// <summary>Registers a refusal. Called once per refused quirk, by the family that owns it.</summary>
    /// <param name="quirk">The quirk.</param>
    /// <param name="reason">Why no managed fixture reaches the site.</param>
    /// <param name="stockSite">The stock C++ <c>path:line</c> the defect lives at.</param>
    internal static void Refuse(StockQuirk quirk, string reason, string stockSite) =>
        Refused[quirk] = new FlipGateRefused(quirk, reason, stockSite);

    // Each family lives in its own file (ComplianceFlipGateProbes.<Family>.cs)
    // and implements its method there; the fleet census — every quirk exactly
    // once across A-D — is what ComplianceFlipGateTests enforces.

    /// <summary>The vbsp tree/collision/tracing family.</summary>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesA();

    /// <summary>The surface/displacement/cooker family.</summary>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesB();

    /// <summary>The vrad light/bounce family.</summary>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesC();

    /// <summary>The vrad ambient/final/disp-sample family.</summary>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesD();

    /// <summary>The gate's dedicated GAP-cell fixture family (one deliberate
    /// fixture build per §11a's completeness clause, not a reused effect fact).</summary>
    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesE();
}

/// <summary>
/// Deterministic byte fingerprints for the gate: every probe reduces its
/// observable to bytes so the four policy runs compare with
/// <see cref="Enumerable.SequenceEqual{T}(IEnumerable{T}, IEnumerable{T})"/> and
/// a failure can name which run differed.
/// </summary>
internal static class Fp
{
    /// <summary>An unmanaged struct array as bytes.</summary>
    /// <typeparam name="T">The struct.</typeparam>
    /// <param name="values">The values.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Bytes<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values).ToArray();

    /// <summary>Text as UTF-8 bytes.</summary>
    /// <param name="text">The text (null folds to an empty run).</param>
    /// <returns>The bytes.</returns>
    public static byte[] Text(string? text) =>
        text is null ? [255] : System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A length-prefixed join of scalars, texts and struct arrays — the shape
    /// for observables that are not one struct array (a leak report, a chooser's
    /// winner, a bool's decision).
    /// </summary>
    /// <param name="parts">The parts: <see cref="byte[]"/> or <see cref="string"/>;
    /// scalars go through <see cref="Bool"/>, <see cref="Real"/>, <see cref="Int"/>.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Join(params object[] parts)
    {
        List<byte> bytes = [];
        foreach (object part in parts)
        {
            byte[] run = part switch
            {
                byte[] b => b,
                string t => Text(t),
                _ => throw new ArgumentException($"fingerprint part {part.GetType().Name} is not byte[]/string", nameof(parts)),
            };
            bytes.AddRange(BitConverter.GetBytes(run.Length));
            bytes.AddRange(run);
        }

        return [.. bytes];
    }

    /// <summary>A decision as one byte.</summary>
    /// <param name="value">The decision.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Bool(bool value) => [value ? (byte)1 : (byte)0];

    /// <summary>A number as its bits — text formatting would hide last-ulp flips.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Real(double value) => BitConverter.GetBytes(value);

    /// <summary>An integer as its bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Int(int value) => BitConverter.GetBytes(value);
}
