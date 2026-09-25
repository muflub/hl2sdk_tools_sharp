using System.Reflection;
using System.Reflection.Emit;

using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// <see cref="ComplianceCatalogue"/> is complete, cites real C++, and names
/// exactly the managed methods that decide each quirk.
/// </summary>
/// <remarks>
/// Three different ways for the catalogue to go stale, and one fact for each.
/// A new enum member without an entry. A stock citation that has drifted off
/// the code it names. A switch site that was added, moved or deleted without
/// the catalogue following it. The third fact reads the BUILT assembly's IL,
/// so it sees what the compiler emitted and not what a grep of the source
/// would guess.
/// </remarks>
public class ComplianceCatalogueTests
{
    public static TheoryData<StockQuirk> Quirks => [.. Enum.GetValues<StockQuirk>()];

    [Theory]
    [MemberData(nameof(Quirks))]
    public void EveryQuirkHasACatalogueEntry(StockQuirk quirk)
    {
        ComplianceQuirkInfo info = ComplianceCatalogue.Describe(quirk);

        Assert.Equal(quirk, info.Quirk);
        Assert.False(string.IsNullOrWhiteSpace(info.Summary));
        Assert.False(string.IsNullOrWhiteSpace(info.StockToken));
        Assert.NotEqual(CompileTools.None, info.Tools);
        Assert.NotEmpty(info.ManagedSites);
    }

    [Fact]
    public void AllListsEveryQuirkOnceInEnumOrder()
    {
        Assert.Equal(
            Enum.GetValues<StockQuirk>(),
            ComplianceCatalogue.All.Select(q => q.Quirk));
    }

    [Fact]
    public void AnUncataloguedValueIsRefusedRatherThanDescribedAsSomethingElse()
    {
        StockQuirk bogus = (StockQuirk)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(() => ComplianceCatalogue.Describe(bogus));
    }

    [Theory]
    [MemberData(nameof(Quirks))]
    public void TheStockSiteCitesALineThatHoldsItsToken(StockQuirk quirk)
    {
        ComplianceQuirkInfo info = ComplianceCatalogue.Describe(quirk);

        int colon = info.StockSite.LastIndexOf(':');
        Assert.True(colon > 0, $"{quirk}: '{info.StockSite}' is not path:line");

        string relative = info.StockSite[..colon];
        int line = int.Parse(info.StockSite[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("no checkout root above the test binary");
        string path = Path.Combine(root, relative);

        Assert.True(File.Exists(path), $"{quirk}: {path} does not exist");

        string[] lines = File.ReadAllLines(path);
        Assert.InRange(line, 1, lines.Length);
        Assert.Contains(info.StockToken, lines[line - 1], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Quirks))]
    public void TheManagedSitesAreExactlyTheMethodsThatConsultTheQuirk(StockQuirk quirk)
    {
        ComplianceQuirkInfo info = ComplianceCatalogue.Describe(quirk);

        IReadOnlyDictionary<StockQuirk, SortedSet<string>> consulted = QuirkSites.Scan();
        SortedSet<string> actual = consulted.TryGetValue(quirk, out SortedSet<string>? found)
            ? found
            : [];

        Assert.Equal(new SortedSet<string>(info.ManagedSites, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void EveryCallToEmulatesPassesAConstantQuirk()
    {
        // The site scan attributes a call by the constant loaded right before
        // it. A call that passes a variable could not be attributed, so the
        // catalogue could not be checked against it. This fact makes that a
        // failure rather than a silent gap.
        Assert.Empty(QuirkSites.UnattributedCalls());
    }

    [Fact]
    public void TheScanFindsTheWindingArenaSite()
    {
        // The scanner is the instrument behind the site fact. This is its
        // known-answer check, so a scanner that finds nothing cannot pass the
        // site fact by comparing two empty sets.
        Assert.Contains(
            "SourceSharp.MapTools.Geometry.WindingArena.BaseWindingForPlane",
            QuirkSites.Scan()[StockQuirk.BaseWindingNormalise]);
    }

    [Fact]
    public void TheVbspListingNamesEveryVbspQuirkBeforeTheOthers()
    {
        string text = ComplianceCatalogue.Format(CompileTools.Vbsp);
        int otherHeading = text.IndexOf("Other tools'", StringComparison.Ordinal);

        Assert.True(otherHeading > 0);
        foreach (ComplianceQuirkInfo q in ComplianceCatalogue.All.Where(q => (q.Tools & CompileTools.Vbsp) != 0))
        {
            int at = text.IndexOf("  " + q.Quirk + " [", StringComparison.Ordinal);
            Assert.InRange(at, 0, otherHeading);
        }
    }

    [Fact]
    public void TheVvisListingSaysNoQuirkAffectsVvis()
    {
        int total = Enum.GetValues<StockQuirk>().Length;

        Assert.StartsWith(
            $"-compliance correct|stock (default correct). 0 of {total} stock quirks affect vvis:\n  (none)\n",
            ComplianceCatalogue.Format(CompileTools.Vvis),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingCarriesAQuirksNote()
    {
        // The last DISPUTED quirk (WeldHashZeroSentinel) was retired with the
        // vertex-0 reservation; the listing still prints every note.
        Assert.Contains(
            "Note: Also disp_vbsp.cpp:346.",
            ComplianceCatalogue.Format(CompileTools.Vbsp),
            StringComparison.Ordinal);
    }
}

/// <summary>
/// Finds, in the built <c>SourceSharp.MapTools</c> assembly, every method that
/// passes a constant <see cref="StockQuirk"/> to
/// <see cref="ComplianceOptions.Emulates"/>.
/// </summary>
internal static class QuirkSites
{
    private static readonly MethodInfo Emulates =
        typeof(ComplianceOptions).GetMethod(nameof(ComplianceOptions.Emulates))!;

    private static readonly Dictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

    /// <summary>Quirk to the methods that consult it.</summary>
    /// <returns>The map.</returns>
    internal static IReadOnlyDictionary<StockQuirk, SortedSet<string>> Scan()
    {
        Dictionary<StockQuirk, SortedSet<string>> sites = [];

        foreach ((string owner, int? constant) in Calls())
        {
            if (constant is int value)
            {
                StockQuirk quirk = (StockQuirk)value;
                if (!sites.TryGetValue(quirk, out SortedSet<string>? set))
                {
                    sites[quirk] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(owner);
            }
        }

        return sites;
    }

    /// <summary>Methods calling Emulates with an argument that is not a constant.</summary>
    /// <returns>Their names.</returns>
    internal static IReadOnlyList<string> UnattributedCalls() =>
        [.. Calls().Where(c => c.Constant is null).Select(c => c.Owner).Distinct()];

    private static IEnumerable<(string Owner, int? Constant)> Calls()
    {
        Assembly assembly = typeof(ComplianceOptions).Assembly;

        foreach (Type type in assembly.GetTypes())
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            IEnumerable<MethodBase> methods =
                type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));

            foreach (MethodBase method in methods)
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                {
                    continue;
                }

                foreach (int? constant in EmulatesCalls(method.Module, il))
                {
                    yield return (OwnerName(type, method), constant);
                }
            }
        }
    }

    private static List<int?> EmulatesCalls(Module module, byte[] il)
    {
        List<int?> calls = [];
        int? lastConstant = null;
        int at = 0;

        while (at < il.Length)
        {
            short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;
            OpCode op = OpCodesByValue[value];

            int? constant = op.Value switch
            {
                >= 0x16 and <= 0x1E => op.Value - 0x16,          // ldc.i4.0 .. ldc.i4.8
                0x15 => -1,                                     // ldc.i4.m1
                0x1F => (sbyte)il[at],                          // ldc.i4.s
                0x20 => BitConverter.ToInt32(il, at),           // ldc.i4
                _ => null,
            };

            // Emulates is defined in this same module, so every call to it
            // carries its MethodDef token. Comparing tokens avoids resolving
            // the generic calls, which would need their generic context.
            if ((op == OpCodes.Call || op == OpCodes.Callvirt)
                && module == Emulates.Module
                && BitConverter.ToInt32(il, at) == Emulates.MetadataToken)
            {
                calls.Add(lastConstant);
            }

            lastConstant = constant;

            at += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
        }

        return calls;
    }

    /// <summary>
    /// <c>Namespace.Type.Method</c>, with compiler-generated state machines,
    /// closures and local functions named by the method they came from.
    /// </summary>
    private static string OwnerName(Type type, MethodBase method)
    {
        string name = method.Name;

        // <Build>b__3_0, <Build>g__Local|3_0: a lambda or local function.
        if (name.StartsWith('<'))
        {
            name = name[1..name.IndexOf('>', StringComparison.Ordinal)];
        }

        // <>c, <>c__DisplayClass3_0, <BuildAsync>d__5: generated nested types.
        while (type.Name.StartsWith('<') && type.DeclaringType is Type outer)
        {
            if (type.Name.Contains(">d__", StringComparison.Ordinal))
            {
                name = type.Name[1..type.Name.IndexOf('>', StringComparison.Ordinal)];
            }

            type = outer;
        }

        return $"{type.FullName}.{name}";
    }
}
