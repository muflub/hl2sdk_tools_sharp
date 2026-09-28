//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Reflection;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools;

/// <summary>
/// The design rules, measured on the BUILT ASSEMBLIES
/// rather than asserted in prose.
/// </summary>
/// <remarks>
/// Each of these replaces a rule that would otherwise be enforced by everyone
/// remembering it. Measured on the assembly and not by grepping the source,
/// because a grep cannot see what a project reference dragged in and cannot
/// tell a <c>const</c> from a mutable static.
/// </remarks>
public class LibraryRuleTests
{
    private static Assembly MapFormats => typeof(BspData).Assembly;

    private static Assembly MapTools => typeof(VPath).Assembly;

    public static TheoryData<string> LibraryAssemblies => ["SourceSharp.MapFormats", "SourceSharp.MapTools"];

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void HasNoMutableStaticFields(string assemblyName)
    {
        // THE rule that makes every later parallelisation legal, and that makes
        // two compiles in one process legal at all. Stock has 48 externs in
        //, 117 file-scope statics in vbsp alone, and bsplib's ~110 MB of
        // global d* arrays; that is exactly why the RPG plan needed one
        // process per compile.
        //
        // `const` and `static readonly` of an immutable type are fine: they are
        // values, not state. A `static readonly` array is NOT fine, because its
        // elements are writable.
        Assembly assembly = Load(assemblyName);

        List<string> offenders = [];
        foreach (Type type in assembly.GetTypes())
        {
            // See FileSystemSeamTests.IsInjectedInstrumentation: a coverage
            // collector rewrites the IL and welds in a tracker holding mutable
            // statics, so without this the gate fails whenever, and only when,
            // coverage is being measured.
            if (Io.FileSystemSeamTests.IsInjectedInstrumentation(type.FullName ?? type.Name))
            {
                continue;
            }

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            foreach (FieldInfo field in fields)
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                // Compiler-generated caches for lambdas, collection literals and
                // the like. They are not state this rule is about, and they are
                // not ours to remove.
                if (field.DeclaringType?.Name.StartsWith("<>", StringComparison.Ordinal) == true
                    || field.Name.StartsWith('<'))
                {
                    continue;
                }

                if (!field.IsInitOnly || IsMutableReference(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} : {field.FieldType.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{assemblyName} has mutable static state:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The packages a library may reference, by library: an explicit
    /// allow-list, so a package not named here fails.
    /// </summary>
    /// <remarks>
    /// Exactly one entry, by the owner's decision: MapTools reads room levels
    /// with YamlDotNet, a standard YAML library, rather than a hand-written
    /// parser. MapFormats stays package-free.
    /// </remarks>
    private static IReadOnlyList<string> AllowedPackages(string assemblyName) =>
        assemblyName == "SourceSharp.MapTools" ? ["YamlDotNet"] : [];

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void ReferencesNoPackages(string assemblyName)
    {
        // Plan ruling Q3: the format parsers are our own. The heavy runtime
        // packages live in the optional Cache.Sqlite and Gpu assemblies, which
        // reference these and not the reverse, so a host that wants neither
        // pulls in neither. The one exception is AllowedPackages.
        //
        // Read from the assembly's own reference table rather than from the
        // project file, so a package arriving INDIRECTLY through a project reference
        // cannot slip past by not being written in the project file.
        Assembly assembly = Load(assemblyName);
        IReadOnlyList<string> packages = AllowedPackages(assemblyName);

        List<string> offenders = [];
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? string.Empty;
            if (!IsFrameworkOrOurs(name) && !packages.Contains(name, StringComparer.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{assemblyName} references non-framework assemblies: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// An allowed package brings nothing further in: every assembly it
    /// references is the framework's, so allowing it did not also allow its
    /// dependencies by the back door. This is the transitive half of the
    /// rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void AnAllowedPackageReferencesOnlyTheFramework(string assemblyName)
    {
        foreach (string package in AllowedPackages(assemblyName))
        {
            Assembly assembly = Assembly.Load(package);
            List<string> offenders = [.. assembly.GetReferencedAssemblies()
                .Select(r => r.Name ?? string.Empty)
                .Where(n => !IsFrameworkOrOurs(n))];
            Assert.True(
                offenders.Count == 0,
                $"{package}, which {assemblyName} may reference, itself references: {string.Join(", ", offenders)}");
        }
    }

    /// <summary>The allow-list is YamlDotNet, for MapTools, and nothing else anywhere.</summary>
    [Fact]
    public void TheAllowListIsYamlDotNetInMapToolsAlone()
    {
        Assert.Equal(["YamlDotNet"], AllowedPackages("SourceSharp.MapTools"));
        Assert.Empty(AllowedPackages("SourceSharp.MapFormats"));
        Assert.Contains(Load("SourceSharp.MapTools").GetReferencedAssemblies(), r => r.Name == "YamlDotNet");
    }

    private static bool IsFrameworkOrOurs(string name)
    {
        string[] allowed =
        [
            "System", "mscorlib", "netstandard", "SourceSharp.MapFormats", "SourceSharp.MapTools",
        ];

        return allowed.Any(a =>
            name.Equals(a, StringComparison.Ordinal)
            || name.StartsWith(a + ".", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPublicAsyncMethodTakesItsTokenLast()
    {
        // Shape, by reflection, so a new blocking or untokened API cannot
        // appear unnoticed. CA1068 covers this at compile time for code in
        // these projects; this covers the same rule for anything that turns off
        // the analyser, and states it where a reader of the tests can see it.
        List<string> offenders = [];

        foreach (Assembly assembly in new[] { MapFormats, MapTools })
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!ReturnsTask(method.ReturnType) || IsFrameworkFixedSignature(method))
                    {
                        continue;
                    }

                    if (!method.Name.EndsWith("Async", StringComparison.Ordinal))
                    {
                        offenders.Add($"{type.FullName}.{method.Name} returns a task but is not named …Async");
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0
                        || parameters[^1].ParameterType != typeof(CancellationToken))
                    {
                        offenders.Add(
                            $"{type.FullName}.{method.Name} does not take a CancellationToken last");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"async shape violations:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    /// Whether the framework, rather than this project, decides the method's
    /// signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one such method today and it is worth naming rather
    /// than waving through: <see cref="IAsyncDisposable.DisposeAsync"/> is
    /// declared to take no parameters, so an implementation CANNOT accept a
    /// cancellation token. Five types in the filesystem layer implement it, and
    /// without this the gate reports all five as violations of a rule they have
    /// no way to obey.
    /// </para>
    /// <para>
    /// Deliberately narrow: it matches the no-argument <c>DisposeAsync</c> of a
    /// type that really does implement <see cref="IAsyncDisposable"/>, and
    /// nothing else. A blanket "skip anything that implements an interface"
    /// would exempt every seam in the library, which is most of the surface
    /// this fact exists to police.
    /// </para>
    /// <para>
    /// Cancelling disposal is not a gap this hides. Disposal releases what is
    /// already held; a caller who wants a bounded teardown bounds the work
    /// before it, not the release.
    /// </para>
    /// </remarks>
    private static bool IsFrameworkFixedSignature(MethodInfo method) =>
        method.Name == "DisposeAsync"
        && method.GetParameters().Length == 0
        && method.DeclaringType is not null
        && typeof(IAsyncDisposable).IsAssignableFrom(method.DeclaringType);

    private static bool ReturnsTask(Type returnType) =>
        returnType == typeof(Task)
        || returnType == typeof(ValueTask)
        || (returnType.IsGenericType
            && (returnType.GetGenericTypeDefinition() == typeof(Task<>)
                || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)));

    private static bool IsMutableReference(Type type)
    {
        // An array's elements are writable however readonly the field is, so a
        // `static readonly int[]` is exactly the shared mutable state this rule
        // exists to ban. Strings and primitives are values.
        if (type.IsArray)
        {
            return true;
        }

        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            return false;
        }

        // Immutable collections and readonly structs of immutable parts are
        // values for this purpose. Anything else that is a class is assumed
        // mutable until someone shows otherwise, which is the safe direction.
        string name = type.FullName ?? type.Name;
        if (name.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal))
        {
            return false;
        }

        return !type.IsValueType;
    }

    private static Assembly Load(string name) => name switch
    {
        "SourceSharp.MapFormats" => MapFormats,
        "SourceSharp.MapTools" => MapTools,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a map-tools library"),
    };
}
