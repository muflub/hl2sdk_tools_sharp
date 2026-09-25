using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The rules that make the <see cref="IFileSystem"/> seam worth having, checked
/// over the BUILT assembly rather than by convention.
/// </summary>
/// <remarks>
/// <para>
/// A seam every byte passes through is what lets a compile run in memory, lets
/// the dependency recorder be impossible to bypass, and lets fault injection be
/// a decorator. All three of those rest on one rule — nothing outside
/// <see cref="PhysicalFileSystem"/> touches <c>System.IO</c> directly — and a
/// rule that is only written in a comment is one that a reader added in a hurry
/// breaks without anyone noticing, quietly reading a file the cache never hears
/// about and handing back a stale product on the next compile.
/// </para>
/// <para>
/// So it is checked in the IL. The scan walks every method body in
/// <c>SourceSharp.MapTools</c>, resolves the target of every call and
/// construction, and names the offender when it finds one.
/// </para>
/// </remarks>
public class FileSystemSeamTests
{
    /// <summary>
    /// The types only <see cref="PhysicalFileSystem"/> may touch. Everything
    /// here is a way of reaching a real disk without going through the seam.
    /// </summary>
    private static readonly ImmutableHashSet<string> ForbiddenTypes =
    [
        "System.IO.File",
        "System.IO.FileInfo",
        "System.IO.Directory",
        "System.IO.DirectoryInfo",
        "System.IO.FileSystemInfo",
        "System.IO.FileStream",
    ];

    /// <summary>
    /// Individual members that are a way to a disk on a type that is otherwise
    /// fine to use.
    /// </summary>
    private static readonly ImmutableHashSet<string> ForbiddenMembers =
    [
        "System.IO.Path.GetTempPath",
        "System.IO.Path.GetTempFileName",
        "System.Environment.get_CurrentDirectory",
        "System.Environment.set_CurrentDirectory",
    ];

    /// <summary>
    /// The one type allowed to break the rule, plus the memory-mapping helper
    /// it delegates its large-file path to. Nothing else is exempt, and adding
    /// to this list is the decision this fact exists to force someone to make
    /// on purpose.
    /// </summary>
    private static readonly ImmutableHashSet<string> Exempt =
    [
        "SourceSharp.MapTools.Io.PhysicalFileSystem",
        "SourceSharp.MapTools.Io.MappedMemoryOwner",
    ];

    [Fact]
    public void OnlyPhysicalFileSystemTouchesSystemIo()
    {
        IReadOnlyList<string> offenders = ScanForForbiddenCalls();

        Assert.True(
            offenders.Count == 0,
            "these must go through IFileSystem instead:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScanCanActuallyFindAViolation()
    {
        // The check that cannot fail is worth nothing. This drives the same
        // walker over the TEST assembly, which touches System.IO freely on
        // purpose (TempTree), and requires that it says so -- so a scan that
        // silently decoded no method bodies, or resolved no tokens, fails here
        // rather than reporting a clean library it never read.
        IReadOnlyList<string> offenders = Scan(typeof(TempTree).Assembly, ImmutableHashSet<string>.Empty);

        Assert.NotEmpty(offenders);
    }

    [Fact]
    public void TheScanNamesTheTypeItFound()
    {
        IReadOnlyList<string> offenders = Scan(typeof(TempTree).Assembly, ImmutableHashSet<string>.Empty);

        Assert.Contains(offenders, static o => o.Contains("TempTree", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLibraryHasNoMutableStaticFields()
    {
        // A mutable static is shared between two compiles in one host process,
        // which is exactly what the library form of these tools is for.
        List<string> mutable = [];

        foreach (Type type in typeof(IFileSystem).Assembly.GetTypes())
        {
            // A coverage collector welds its own tracker type, with its own
            // mutable statics, into every instrumented assembly. Skipping it
            // is what lets this gate and the section 11a coverage run both
            // pass in the same invocation.
            if (FileSystemSeamTests.IsInjectedInstrumentation(type.FullName ?? type.Name))
            {
                continue;
            }

            // The compiler's own closure classes are excluded, and they are
            // the reason this fact needs an exclusion at all: a `static`
            // lambda is CACHED in a mutable static field the compiler
            // generates, so writing the allocation-free form of a comparison
            // would otherwise fail the no-statics rule. They hold a delegate
            // to a static method and no state.
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    mutable.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        Assert.True(mutable.Count == 0, string.Join(", ", mutable));
    }

    private static IReadOnlyList<string> ScanForForbiddenCalls() =>
        Scan(typeof(IFileSystem).Assembly, Exempt);

    private static IReadOnlyList<string> Scan(Assembly assembly, ImmutableHashSet<string> exempt) =>
        ScanCalls(assembly, exempt, (m, h, _, _) => IsForbiddenFileSystemCall(m, h));

    /// <summary>
    /// Walks every method body in an assembly and reports the calls a rule
    /// rejects.
    /// </summary>
    /// <param name="assembly">The built assembly to read.</param>
    /// <param name="exempt">
    /// Outermost type names allowed to break the rule.
    /// </param>
    /// <param name="rule">
    /// Given the metadata, a call target, and the name of the type and method
    /// containing the call, returns a description of the violation or null.
    /// The containing names matter for rules that must tell a compiler-generated
    /// construct from hand-written code.
    /// </param>
    /// <returns>One line per violation.</returns>
    /// <remarks>
    /// Generalised out of the filesystem rule so there is ONE IL walker rather
    /// than one per rule. The opcode table below is the fiddly part and the
    /// part most likely to be got subtly wrong in a second copy: an operand
    /// size read wrong makes the walker desynchronise and silently stop seeing
    /// calls, which would turn any rule using it into a check that cannot fail.
    /// </remarks>
    internal static IReadOnlyList<string> ScanCalls(
        Assembly assembly,
        ImmutableHashSet<string> exempt,
        Func<MetadataReader, EntityHandle, string, string, string?> rule)
    {
        List<string> offenders = [];

        using FileStream file = File.OpenRead(assembly.Location);
        using PEReader pe = new(file);
        MetadataReader metadata = pe.GetMetadataReader();

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            string owner = OutermostTypeName(metadata, method.GetDeclaringType());
            if (exempt.Contains(owner) || IsInjectedInstrumentation(owner))
            {
                continue;
            }

            // The declaring type's OWN name, not the outermost one: a compiler
            // generated async state machine is a nested type called
            // <Something>d__7, and a rule that must tell `await` from a
            // blocking call needs to see that rather than the tidy outer name.
            string declaringName = metadata.GetString(
                metadata.GetTypeDefinition(method.GetDeclaringType()).Name);
            string methodName = metadata.GetString(method.Name);

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);

            byte[]? il = body.GetILBytes();
            if (il is null)
            {
                continue;
            }

            foreach (EntityHandle target in CallTargets(il))
            {
                if (rule(metadata, target, declaringName, methodName) is string what)
                {
                    offenders.Add($"{owner}.{methodName} calls {what}");
                }
            }
        }

        return offenders;
    }

    private static string? IsForbiddenFileSystemCall(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind is not HandleKind.MemberReference)
        {
            // A call within this assembly, or a generic instantiation of one.
            // Neither can reach a disk without some method in this assembly
            // making a MemberReference of its own, which this scan sees.
            return null;
        }

        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind is not HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        string typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        string memberName = metadata.GetString(member.Name);

        if (ForbiddenTypes.Contains(typeName))
        {
            return typeName + "." + memberName;
        }

        return ForbiddenMembers.Contains(typeName + "." + memberName) ? typeName + "." + memberName : null;
    }

    internal static string OutermostTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);

        // A compiler-generated state machine or closure is nested inside the
        // type whose method it came from, so the exemption has to be checked
        // against the OUTERMOST type or every async method in
        // PhysicalFileSystem would read as a violation.
        while (type.IsNested)
        {
            type = metadata.GetTypeDefinition(type.GetDeclaringType());
        }

        return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
    }

    /// <summary>
    /// The targets of every <c>call</c>, <c>callvirt</c> and <c>newobj</c> in a
    /// method body.
    /// </summary>
    /// <param name="il">The method's IL.</param>
    /// <returns>One handle per call site.</returns>
    /// <remarks>
    /// The operand sizes come from <see cref="OpCodes"/> itself rather than
    /// from a table written out here, because a table written out here is a
    /// table that is wrong in one entry and silently desynchronises the walker
    /// from the instruction stream for the rest of the method.
    /// </remarks>
    internal static IEnumerable<EntityHandle> CallTargets(byte[] il)
    {
        List<EntityHandle> targets = [];
        int at = 0;

        while (at < il.Length)
        {
            short value = il[at] == 0xFE && at + 1 < il.Length
                ? (short)(0xFE00 | il[at + 1])
                : il[at];

            at += value > 0xFF ? 2 : 1;

            if (!OperandSizes.TryGetValue(value, out OperandType operand))
            {
                // An opcode this walker does not know would desynchronise the
                // stream, so stop rather than report nonsense from here on.
                break;
            }

            if (value is (short)ILOpCode.Call or (short)ILOpCode.Callvirt or (short)ILOpCode.Newobj
                && at + 4 <= il.Length)
            {
                int token = il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24);
                targets.Add(MetadataTokens.EntityHandle(token));
            }

            at += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                    or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * ReadInt32(il, at)),
                _ => 4,
            };
        }

        return targets;
    }

    private static int ReadInt32(byte[] il, int at) =>
        at + 4 > il.Length ? 0 : il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24);

    private static readonly Dictionary<short, OperandType> OperandSizes = BuildOperandSizes();

    private static Dictionary<short, OperandType> BuildOperandSizes()
    {
        Dictionary<short, OperandType> sizes = [];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                sizes[opCode.Value] = opCode.OperandType;
            }
        }

        return sizes;
    }

    /// <summary>
    /// Whether a type was welded into the assembly by a coverage collector
    /// rather than written by anyone.
    /// </summary>
    /// <param name="typeName">The type's full name.</param>
    /// <returns>True for injected instrumentation.</returns>
    /// <remarks>
    /// <para>
    /// Running <c>dotnet test --collect "Code Coverage"</c> rewrites the IL of
    /// every assembly under test and welds in a
    /// <c>Microsoft.CodeCoverage.Instrumentation.Static.Tracker</c> type. That
    /// tracker holds mutable statics and calls <c>System.IO.File</c> and
    /// <c>Directory</c> directly -- so without this, the assembly-rule gates
    /// all fail, and they fail ONLY when coverage is being measured.
    /// </para>
    /// <para>
    /// That matters because the design rules need both: they make a
    /// coverage run part of every phase exit, and make these gates
    /// part of the same exit. They collided the first time both were run
    /// together, which is a thing nobody discovers until they do it.
    /// </para>
    /// <para>
    /// Narrow on purpose: it matches the collector's own namespace and nothing
    /// else, so it cannot be used to excuse a type of ours.
    /// </para>
    /// </remarks>
    internal static bool IsInjectedInstrumentation(string typeName) =>
        typeName.StartsWith("Microsoft.CodeCoverage.", StringComparison.Ordinal);
}
