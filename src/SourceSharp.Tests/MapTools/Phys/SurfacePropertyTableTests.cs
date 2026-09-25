using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The managed surface-property database, fact by fact from
/// <c>CPhysicsSurfaceProps</c> (<c>vphysics/physics_material.cpp</c>, 2018
/// engine drop) and its tokenizer (<c>vcollide_parse.cpp:919</c>,
/// <c>public/filesystem_helpers.cpp:29</c>).
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
        // ParseKeyvalue Q_strlower's the key, vcollide_parse.cpp:933.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal("metal", table.GetPropName(1));
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        // m_strings( 0, 32, true ): a case-insensitive symbol table, physics_material.cpp:184.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(1, table.GetSurfaceIndex("METAL"));
    }

    [Fact]
    public void ANewPropertyInheritsDefault()
    {
        // physics_material.cpp:421-427: baseMaterial falls back to "default".
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0.8f, table.GetPhysicsProperties(1).Friction);
    }

    [Fact]
    public void ARepeatedPropertyOverridesInPlaceAndKeepsItsIndex()
    {
        // physics_material.cpp:434-441.
        SurfacePropertyTable table = Parsed(
            ("a.txt", Base),
            ("b.txt", "\"metal\" { \"density\" \"99\" }"));

        Assert.Equal(1, table.GetSurfaceIndex("metal"));
        Assert.Equal(99f, table.GetPhysicsProperties(1).Density);
    }

    [Fact]
    public void ARepeatedPropertyStartsFromItsOwnPreviousValues()
    {
        // GetSurfaceIndex( key ) finds the existing one first, :421.
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
        // physics_material.cpp:588-597: m_init, once, after the first parse.
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
        // GetReservedSurfaceIndex, :350.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0xF000, table.GetSurfaceIndex("$material_index_shadow"));
    }

    [Fact]
    public void TheReservedIndexFallsBackToTheShadowEntry()
    {
        // GetInternalSurface, :258: MATERIAL_INDEX_SHADOW -> m_shadowFallback.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(SurfacePropertyTable.ShadowMaterialName, table.GetPropName(0xF000));
    }

    [Fact]
    public void AFileNameSeenBeforeIsIgnored()
    {
        // AddFileToDatabase, :205.
        SurfacePropertyTable table = Parsed(("a.txt", Base));

        Assert.Equal(0, table.ParseSurfaceData("A.TXT", "\"wood\" { }"));
        Assert.Equal(-1, table.GetSurfaceIndex("wood"));
    }

    [Fact]
    public void AnUnknownIndexAnswersWithDefaultsPhysics()
    {
        // GetPhysicsProperties, :279-282.
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
        // g_BreakSetIncludingColons "{}()':" is the default set, filesystem_helpers.cpp:22.
        int? cursor = ParseFile("ab:cd"u8.ToArray(), 0, out string token);

        Assert.Equal("ab", token);
        Assert.Equal(2, cursor);
    }

    [Fact]
    public void AHighByteIsWhitespaceToTheSignedCharTest()
    {
        // while ( (c = *pFileBytes) <= ' ' ) on a signed char: 0xE9 is negative.
        ParseFile([(byte)'a', 0xE9, (byte)'b'], 0, out string token);

        Assert.Equal("a", token);
    }

    [Fact]
    public void ResolveMaterialWithoutASurfacePropIsMinusOne()
    {
        // GetSurfaceProperties, textures.cpp:347: no $surfaceprop, index stays -1.
        Assert.Equal(-1, Parsed(("a.txt", Base)).ResolveMaterial(null));
    }

    [Fact]
    public void ResolveMaterialWithAnUnknownSurfacePropIsDefault()
    {
        // textures.cpp:355-359: "Can't find surfaceprop ... using default".
        Assert.Equal(0, Parsed(("a.txt", Base)).ResolveMaterial("nonsense"));
    }
}
