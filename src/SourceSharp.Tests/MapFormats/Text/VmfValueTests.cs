using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the conversions a VMF value goes through
/// (<c>src/public/chunkfile.cpp:636-940</c>), and the assembly-wide rules the
/// plan makes gates.
/// </summary>
public class VmfValueTests
{
    [Theory]

    // C's %g with precision 6: %f form while 6 > exponent >= -4, trailing
    // zeros stripped, and the decimal point stripped with them.
    [InlineData(0f, "0")]
    [InlineData(1f, "1")]
    [InlineData(-1f, "-1")]
    [InlineData(0.5f, "0.5")]
    [InlineData(64f, "64")]
    [InlineData(1.25f, "1.25")]
    [InlineData(123456f, "123456")]

    // Seven significant digits do not fit, so it switches to the %e form with
    // a lower-case 'e' and at least two exponent digits.
    [InlineData(1234567f, "1.23457e+06")]
    [InlineData(0.0001f, "0.0001")]
    [InlineData(0.00001f, "1e-05")]
    public void FloatsAreWrittenWithCsPercentG(float value, string expected)
    {
        // chunkfile.cpp:844 -- Q_snprintf(..., "\"%s\" \"%g\"", key,
        // (double)fValue). .NET's "G6" is NOT the same conversion: it spells
        // the exponent "E+06" and keeps a digit C drops.
        Assert.Equal(expected, VmfValue.FormatFloat(value));
    }

    [Fact]
    public void SixSignificantDigitsIsLossyAndThatIsTheFormatNotThePort()
    {
        // A float that needs nine digits to round-trip does not survive a
        // Hammer save, because %g's default precision is 6.
        const float value = 1234.5678f;

        Assert.Equal("1234.57", VmfValue.FormatFloat(value));
        Assert.NotEqual(value, VmfValue.ParseFloat(VmfValue.FormatFloat(value)));
    }

    [Fact]
    public void PointsArePARENTHESISED()
    {
        // chunkfile.cpp:884 writes "(%g %g %g)" and :719 reads it back.
        Assert.Equal("(0 0 64)", VmfValue.FormatPoint(new Vec3(0, 0, 64)));
    }

    [Fact]
    public void VectorsAreBRACKETED()
    {
        // chunkfile.cpp:916 writes "[%g %g %g]" and :753 reads it back. The two
        // bracketings are NOT interchangeable and only the C++ says which
        // keys use which.
        Assert.Equal("[1 0 0]", VmfValue.FormatVector3(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void PointParserRejectsTheBracketedSpelling()
    {
        Assert.False(VmfValue.TryParsePoint("[0 0 64]", out _));
    }

    [Fact]
    public void VectorParserRejectsTheParenthesisedSpelling()
    {
        Assert.False(VmfValue.TryParseVector3("(1 0 0)", out _));
    }

    [Fact]
    public void PointRoundTripsThroughItsOwnFormatting()
    {
        Vec3 point = new(-64, 128.5f, 0);

        Assert.True(VmfValue.TryParsePoint(VmfValue.FormatPoint(point), out Vec3 parsed));
        Assert.Equal(point, parsed);
    }

    [Fact]
    public void BoolIsStrictlyGreaterThanZeroSoMinusOneIsFalse()
    {
        // chunkfile.cpp:638-647 -- atoi(value) > 0. Not what a C programmer
        // expects from a flag, and it means a VMF storing -1 for "on" reads as
        // off.
        Assert.True(VmfValue.ParseBool("1"));
        Assert.True(VmfValue.ParseBool("2"));
        Assert.False(VmfValue.ParseBool("0"));
        Assert.False(VmfValue.ParseBool("-1"));
    }

    [Fact]
    public void NonNumericValuesBecomeZeroRatherThanFailing()
    {
        // chunkfile.cpp:659-676 -- atof and atoi report nothing, and the C++
        // returns true unconditionally.
        Assert.Equal(0f, VmfValue.ParseFloat("banana"));
        Assert.Equal(0, VmfValue.ParseInt("banana"));
    }

    [Fact]
    public void FloatIsParsedAtDoublePrecisionAndThenNarrowed()
    {
        // chunkfile.cpp:661 -- (float)atof(pszValue). Narrowing the correctly
        // rounded double is not always the same as parsing at single precision
        // directly.
        Assert.Equal((float)0.1d, VmfValue.ParseFloat("0.1"));
    }

    [Fact]
    public void ColourComponentsWrapRatherThanClampBecauseTheCppAssignsIntToByte()
    {
        // chunkfile.cpp:695-699 -- scanned into int, assigned to unsigned char.
        Assert.True(VmfValue.TryParseColour("300 0 0", out (byte Red, byte Green, byte Blue) colour));
        Assert.Equal(44, colour.Red);
    }

    [Fact]
    public void ScanStopsCountingAssignmentsSoAMissingCloseBracketStillParses()
    {
        // sscanf returns the number of ASSIGNMENTS. A trailing literal that
        // fails to match does not reduce it, so "[1 2 3" yields three.
        Assert.True(VmfValue.TryParseVector3("[1 2 3", out Vec3 vector));
        Assert.Equal(new Vec3(1, 2, 3), vector);
    }

    [Fact]
    public void MapFormatsHasNoMutableStaticFields()
    {
        // The plan's no-statics rule, enforced from the BUILT assembly rather
        // than from a reading of the source. A mutable static in a format
        // parser is what makes two maps unable to compile at once -- the C++
        // tokenizers are riddled with them (scriplib.cpp:44-50,
        // KeyValues.cpp:45, which needs a process-wide mutex because of it).
        List<string> offenders = [];

        foreach (Type type in typeof(VmfValue).Assembly.GetTypes())
        {
            // A coverage collector welds its own tracker type, with its own
            // mutable statics, into every instrumented assembly. Skipping it
            // is what lets this gate and the section 11a coverage run both
            // pass in the same invocation.
            if (SourceSharp.Tests.MapTools.Io.FileSystemSeamTests.IsInjectedInstrumentation(type.FullName ?? type.Name))
            {
                continue;
            }

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (FieldInfo field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly)
                {
                    continue;
                }

                offenders.Add($"{type.FullName}.{field.Name}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void MapFormatsTouchesNoFileSystemTypeAtAll()
    {
        // The plan's "readers and writers take a Stream or memory, never a
        // path" rule, enforced from the BUILT assembly's metadata rather than
        // from a reading of the source: if nothing in here can NAME
        // System.IO.File, it cannot open one.
        //
        // Checked against type references, not assembly references. A
        // referenced-assembly check would be vacuous on modern .NET, where
        // everything arrives through System.Runtime and no facade named
        // System.IO.FileSystem appears at all.
        string[] forbidden =
        [
            "File", "FileInfo", "FileStream", "Directory", "DirectoryInfo", "Path",
        ];

        using FileStream assembly = File.OpenRead(typeof(VmfValue).Assembly.Location);
        using PEReader pe = new(assembly);
        MetadataReader metadata = pe.GetMetadataReader();

        List<string> offenders = [];
        bool instrumented = false;

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            string name = metadata.GetString(metadata.GetTypeDefinition(handle).Name);
            if (name.Contains("ManagedTrackerTemplate", StringComparison.Ordinal))
            {
                instrumented = true;
                break;
            }
        }

        if (instrumented)
        {
            // A coverage collector has rewritten this assembly and welded in a
            // tracker that opens files directly, so its System.IO type
            // references are now in the table and the strong check below cannot
            // hold. Fall back to the CALL-SITE scan, which can tell the
            // injected code from ours.
            //
            // Stated rather than skipped: this is a genuinely weaker check, and
            // it runs only while coverage is being measured. plan_maptools.md
            // asks for both this gate (1a) and a coverage run (11a) at every
            // phase exit, and they collide -- which nobody discovers until they
            // are run together.
            IReadOnlyList<string> calls = SourceSharp.Tests.MapTools.Io.FileSystemSeamTests.ScanCalls(
                typeof(VmfValue).Assembly,
                [],
                (m, h, _, _) => ForbiddenSystemIoCall(m, h, forbidden));

            Assert.True(
                calls.Count == 0,
                "MapFormats calls System.IO (measured at call sites, because this assembly is "
                + "instrumented for coverage):" + Environment.NewLine
                + string.Join(Environment.NewLine, calls));
            return;
        }

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference reference = metadata.GetTypeReference(handle);
            string ns = metadata.GetString(reference.Namespace);
            string name = metadata.GetString(reference.Name);

            if (ns == "System.IO" && forbidden.Contains(name))
            {
                offenders.Add($"{ns}.{name}");
            }
        }

        Assert.Empty(offenders);
    }

    private static string? ForbiddenSystemIoCall(
        MetadataReader metadata,
        EntityHandle handle,
        string[] forbidden)
    {
        if (handle.Kind is not HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind is not HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        string ns = metadata.GetString(type.Namespace);
        string name = metadata.GetString(type.Name);

        return ns == "System.IO" && forbidden.Contains(name)
            ? ns + "." + name + "." + metadata.GetString(member.Name)
            : null;
    }

    [Fact]
    public void MapFormatsTakesNoPackageReference()
    {
        // Plan ruling Q3, checked from the BUILT assembly's references rather
        // than from the .csproj, so an indirect package arriving through a
        // project reference cannot slip past by not being written there.
        string[] allowedPrefixes = ["System.", "Microsoft.", "netstandard", "mscorlib"];

        List<string> offenders = [.. typeof(VmfValue).Assembly.GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .Where(name => !allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))];

        Assert.Empty(offenders);
    }
}
