//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Reflection;

using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// The gate on the gate: every rule in the catalogue has a fact that makes it
/// fire, and no fact claims a rule that does not exist.
/// </summary>
/// <remarks>
/// A validator whose rules cannot fire is worth nothing, and "I wrote a test
/// for it" is not evidence that the test could ever go red. Reflecting over
/// <see cref="CorruptsAttribute"/> makes the correspondence a build-time fact
/// rather than a claim in a commit message: adding a rule without a mutation
/// proof fails here, and so does deleting one and leaving its fact behind.
/// </remarks>
public class RuleCoverageTests
{
    private static HashSet<string> CoveredCodes()
    {
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach (Type type in typeof(RuleCoverageTests).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                CorruptsAttribute? attribute = method.GetCustomAttribute<CorruptsAttribute>();
                if (attribute is not null)
                {
                    covered.Add(attribute.Code);
                }
            }
        }

        return covered;
    }

    [Fact]
    public void EveryRuleHasACorruptionFact()
    {
        HashSet<string> covered = CoveredCodes();
        string[] uncovered = BspRuleCatalog.All
            .Select(r => r.Code)
            .Where(c => !covered.Contains(c))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            "these rules have no fact that makes them fire, so nothing proves they can: "
            + string.Join(", ", uncovered));
    }

    [Fact]
    public void EveryCorruptionFactNamesARuleThatExists()
    {
        HashSet<string> declared = BspRuleCatalog.All
            .Select(r => r.Code)
            .ToHashSet(StringComparer.Ordinal);

        string[] unknown = CoveredCodes()
            .Where(c => !declared.Contains(c))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "these facts claim to prove rules that are not in the catalogue: "
            + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryCodeConstantHasACatalogueRow()
    {
        string[] constants = typeof(BspRuleCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        HashSet<string> declared = BspRuleCatalog.All
            .Select(r => r.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(constants, c => Assert.Contains(c, declared));
        Assert.Equal(constants.Length, BspRuleCatalog.All.Length);
    }

    [Fact]
    public void CodesAreUniqueAndInOrder()
    {
        string[] codes = BspRuleCatalog.All.Select(r => r.Code).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(codes.Order(StringComparer.Ordinal).ToArray(), codes);
    }

    [Fact]
    public void EveryRuleCarriesAUniqueCodeAndOneLineTitle()
    {
        // The catalogue's contract with its host: a stable code and a title
        // worth printing. A rule with an empty title is a placeholder.
        Assert.All(BspRuleCatalog.All, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Title));
            Assert.False(string.IsNullOrWhiteSpace(rule.Code));
        });
    }

    [Fact]
    public void ByCodeRejectsACodeThatIsNotARule()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BspRuleCatalog.ByCode("BSP9999"));
    }
}
