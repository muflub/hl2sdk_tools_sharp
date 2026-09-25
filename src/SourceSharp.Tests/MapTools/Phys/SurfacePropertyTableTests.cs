using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The managed surface-property database, fact by fact against the reference implementation's
/// surface-property database and its tokenizer.
/// </summary>
public class SurfacePropertyTableTests
{
    private const string Base = """
        "default"
        {
            "density"   "2000"
            "thickness" "0"
            "friction"  "0.8"
            "elasticity" "0.25"
        }
        "Metal"
        {
            "density"  "2700"
        }
        """;

    /// <summary>The internal tokenizer, reached by reflection (no InternalsVisibleTo here).</summary>
    private static int? ParseFile(byte[] text, int? cursor, out string token)
    {
        object?[] args = [text, cursor, null];
        object? result = typeof(SurfacePropertyTable)
            .GetMethod("ParseFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, args);
        token = (string)args[2]!;
        return (int?)result;
    }

    private static SurfacePropertyTable Parsed(params (string File, string Text)[] files)
    {
        SurfacePropertyTable table = new();
        foreach ((string file, string text) in files)
        {
            table.ParseSurfaceData(file, text);
        }

        return table;
    }

    [Fact]
    public void NamesAreStoredLowerCase()
    {
        // The parser lowercases the key before storing it.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal("metal", table.GetPropName(1));
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        // Names live in a case-insensitive symbol table.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(1, table.GetSurfaceIndex("METAL"));
    }

    [Fact]
    public void ANewPropertyInheritsDefault()
    {
        // A new property's base material falls back to "default".
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0.8f, table.GetPhysicsProperties(1).Friction);
    }

    [Fact]
    public void ARepeatedPropertyOverridesInPlaceAndKeepsItsIndex()
    {
        // Re-parsing a property overwrites the existing entry in place.
        SurfacePropertyTable table = Parsed(
            ("a.txt", Base),
            ("b.txt", "\"metal\" { \"density\" \"99\" }"));

        Assert.Equal(1, table.GetSurfaceIndex("metal"));
        Assert.Equal(99f, table.GetPhysicsProperties(1).Density);
    }

    [Fact]
    public void ARepeatedPropertyStartsFromItsOwnPreviousValues()
    {
        // The lookup finds the existing entry before creating a new one.
        SurfacePropertyTable table = Parsed(
            ("a.txt", Base),
            ("b.txt", "\"metal\" { \"friction\" \"0.1\" }"));

        Assert.Equal(2700f, table.GetPhysicsProperties(1).Density);
    }

    [Fact]
    public void BaseReInheritsFromTheNamedProperty()
    {
        SurfacePropertyTable table = Parsed(
            ("a.txt", Base),
            ("b.txt", "\"rebar\" { \"base\" \"metal\" }"));

        Assert.Equal(2700f, table.GetPhysicsProperties(table.GetSurfaceIndex("rebar")).Density);
    }

    [Fact]
    public void TheShadowMaterialIsAppendedAfterTheFirstFile()
    {
        // The reserved shadow material is appended once, after the first parse.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(SurfacePropertyTable.ShadowMaterialName, table.GetPropName(2));
    }

    [Fact]
    public void ASecondFilesPropertiesComeAfterTheShadowMaterial()
    {
        SurfacePropertyTable table = Parsed(("a.txt", Base), ("b.txt", "\"wood\" { }"));

        Assert.Equal(3, table.GetSurfaceIndex("wood"));
    }

    [Fact]
    public void TheShadowNameResolvesToTheReservedIndex()
    {
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0xF000, table.GetSurfaceIndex("$material_index_shadow"));
    }

    [Fact]
    public void TheReservedIndexFallsBackToTheShadowEntry()
    {
        // The reserved shadow index resolves to the shadow fallback entry.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(SurfacePropertyTable.ShadowMaterialName, table.GetPropName(0xF000));
    }

    [Fact]
    public void AFileNameSeenBeforeIsIgnored()
    {
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0, table.ParseSurfaceData("A.TXT", "\"wood\" { }"));
        Assert.Equal(-1, table.GetSurfaceIndex("wood"));
    }

    [Fact]
    public void AnUnknownIndexAnswersWithDefaultsPhysics()
    {
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(2000f, table.GetPhysicsProperties(77).Density);
    }

    [Fact]
    public void CommentsOfBothKindsAreSkipped()
    {
        SurfacePropertyTable table = Parsed(("a.txt", "// x\n/* \"no\" { } */ \"yes\" { }"));

        Assert.Equal(-1, table.GetSurfaceIndex("no"));
        Assert.Equal(0, table.GetSurfaceIndex("yes"));
    }

    [Fact]
    public void AColonBreaksAnUnquotedToken()
    {
        // The tokenizer's default break set is "{}()':".
        int? cursor = ParseFile("ab:cd"u8.ToArray(), 0, out string token);

        Assert.Equal("ab", token);
        Assert.Equal(2, cursor);
    }

    [Fact]
    public void AHighByteIsWhitespaceToTheSignedCharTest()
    {
        // The whitespace test compares against a signed char, so 0xE9 counts as negative.
        ParseFile([(byte)'a', 0xE9, (byte)'b'], 0, out string token);

        Assert.Equal("a", token);
    }

    [Fact]
    public void ResolveMaterialWithoutASurfacePropIsMinusOne()
    {
        // With no $surfaceprop the resolved index stays -1.
        Assert.Equal(-1, Parsed(("a.txt", Base)).ResolveMaterial(null));
    }

    [Fact]
    public void ResolveMaterialWithAnUnknownSurfacePropIsDefault()
    {
        // An unknown surfaceprop falls back to the default entry.
        Assert.Equal(0, Parsed(("a.txt", Base)).ResolveMaterial("nonsense"));
    }
}
