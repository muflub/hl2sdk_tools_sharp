using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools;

/// <summary>
/// No sync-over-async anywhere in the shipping assemblies, measured in the IL
/// rather than by grepping the source.
/// </summary>
/// <remarks>
/// <para>
/// Plan_maptools.md 1a makes this a gate because blocking on a task is how an
/// async library kills a host. Inside a compile it is worse than a stall: the
/// scheduler runs on its own dedicated threads, and a worker that blocks on
/// another worker's task takes one of those threads out of circulation for the
/// duration. Enough of them and the run cannot finish at all.
/// </para>
/// <para>
/// The scan reads IL because the source-level shapes are open-ended.
/// <c>.Result</c>, <c>.Wait()</c>, <c>GetAwaiter().GetResult()</c>,
/// <c>Task.WaitAll</c> and <c>ValueTask.Result</c> all compile down to calls
/// this walker sees by name, including the ones hidden behind a helper or a
/// generic.
/// </para>
/// <para>
/// TEST code is deliberately NOT scanned. A fact may block: it has no host to
/// starve, and forcing every assertion through an async chain would obscure
/// what is being asserted. The rule is about what ships.
/// </para>
/// </remarks>
public class SyncOverAsyncTests
{
    /// <summary>
    /// The blocking members. Names as they appear in the IL: a property read is
    /// <c>get_X</c>, and the awaiter types are where <c>GetResult</c> lives.
    /// </summary>
    private static readonly ImmutableHashSet<string> AlwaysBlocking =
    [
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.WaitAll",
        "System.Threading.Tasks.Task.WaitAny",
        "System.Threading.Tasks.Task.get_Result",
        "System.Threading.Tasks.Task`1.Wait",
        "System.Threading.Tasks.Task`1.get_Result",
        "System.Threading.Tasks.ValueTask.get_Result",
        "System.Threading.Tasks.ValueTask`1.get_Result",
    ];

    /// <summary>
    /// <c>GetResult</c> on an awaiter, which is blocking in hand-written code
    /// and is simply what <c>await</c> compiles to inside a state machine.
    /// </summary>
    /// <remarks>
    /// This distinction is the whole difficulty of the scan, and getting it
    /// wrong in either direction is silent. Every `await` emits
    /// <c>awaiter.GetResult()</c> inside a compiler-generated
    /// <c>MoveNext</c> — the first version of this fact flagged all of them and
    /// reported the entire library as broken. Exempting <c>MoveNext</c>
    /// wholesale would be the opposite error, because `.Result` written inside
    /// an async method also lands there; so only the AWAITER members are
    /// contextual, and the list above stays forbidden everywhere.
    /// </remarks>
    private static readonly ImmutableHashSet<string> AwaiterResults =
    [
        "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult",
        "System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1.ConfiguredTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1.ConfiguredValueTaskAwaiter.GetResult",
    ];

    /// <summary>
    /// Nothing is exempt. The list exists so that adding to it is a visible
    /// decision rather than an edit buried in a method.
    /// </summary>
    private static readonly ImmutableHashSet<string> Exempt = [];

    public static TheoryData<string> ShippingAssemblies =>
        ["SourceSharp.MapFormats", "SourceSharp.MapTools", "SourceSharp.MapCompile"];

    [Theory]
    [MemberData(nameof(ShippingAssemblies))]
    public void NothingBlocksOnATask(string assemblyName)
    {
        IReadOnlyList<string> offenders =
            FileSystemSeamTests.ScanCalls(Resolve(assemblyName), Exempt, IsBlockingCall);

        Assert.True(
            offenders.Count == 0,
            $"{assemblyName} blocks on a task. Await it instead:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScanCanActuallyFindAViolation()
    {
        // The check that cannot fail is worth nothing, and this one has a real
        // way of silently passing: if the IL walker desynchronises on an
        // operand size it stops seeing calls altogether and every assembly
        // looks clean.
        //
        // So drive the same walker over the TEST assembly, which blocks on
        // tasks deliberately and in several places, and require it to find
        // them. A walker that decodes nothing fails here.
        IReadOnlyList<string> offenders =
            FileSystemSeamTests.ScanCalls(typeof(SyncOverAsyncTests).Assembly, Exempt, IsBlockingCall);

        Assert.True(
            offenders.Count > 0,
            "the scan found no blocking call in the TEST assembly, which is known to contain "
            + "several. The walker is not decoding method bodies, so its clean verdict on the "
            + "shipping assemblies means nothing.");
    }

    [Fact]
    public void TheScanNamesWhatItFound()
    {
        // A gate whose message does not say where to look gets ignored.
        IReadOnlyList<string> offenders =
            FileSystemSeamTests.ScanCalls(typeof(SyncOverAsyncTests).Assembly, Exempt, IsBlockingCall);

        Assert.Contains(offenders, o => o.Contains("GetResult", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a call is a compiler-generated async state machine's own
    /// <c>MoveNext</c>, where an awaiter's <c>GetResult</c> IS the await.
    /// </summary>
    /// <param name="declaringType">The containing type's own name.</param>
    /// <param name="method">The containing method's name.</param>
    /// <returns>True when the call site is generated rather than written.</returns>
    /// <remarks>
    /// <para>
    /// Two shapes, both emitted by the compiler and neither written by anyone.
    /// </para>
    /// <para>
    /// The first is the async state machine: C# names it after the method that
    /// produced it, in angle brackets — <c>&lt;LoadAsync&gt;d__12</c> — and its
    /// <c>MoveNext</c> is where every <c>await</c> becomes
    /// <c>awaiter.GetResult()</c>. Those brackets are not legal in a C#
    /// identifier, so a hand-written type cannot collide with the pattern.
    /// </para>
    /// <para>
    /// The second is the entry-point stub for an <c>async Task&lt;int&gt;
    /// Main</c>. A process entry point cannot itself be asynchronous, so the
    /// compiler emits a synchronous <c>&lt;Main&gt;</c> that blocks on the real
    /// one. That block is required by the runtime and is the single place in
    /// the program where there is nothing to await into — which is why it is
    /// named here rather than put on the exemption list, where it would read as
    /// a decision someone made about our code.
    /// </para>
    /// </remarks>
    private static bool IsGeneratedStateMachine(string declaringType, string method) =>
        (method == "MoveNext" && declaringType.StartsWith('<'))
        || method == "<Main>";

    private static string? IsBlockingCall(
        MetadataReader metadata,
        EntityHandle handle,
        string declaringType,
        string method)
    {
        if (handle.Kind is not HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        string memberName = metadata.GetString(member.Name);

        // A call on a GENERIC type -- TaskAwaiter<T>.GetResult(), which is
        // most of what this rule is looking for -- has a TypeSpecification
        // parent, not a TypeReference: the parent is the INSTANTIATED type and
        // lives in a signature blob.
        //
        // The first version of this scan returned null here and consequently
        // found nothing at all, including in test code that blocks on purpose.
        // It reported every assembly clean. The positive control below is what
        // caught it, which is exactly why a rule like this needs one.
        if (member.Parent.Kind is HandleKind.TypeSpecification)
        {
            string decoded = metadata
                .GetTypeSpecification((TypeSpecificationHandle)member.Parent)
                .DecodeSignature(new TypeNameDecoder(), null);

            // Drop the instantiation so TaskAwaiter`1<Foo> matches TaskAwaiter`1.
            int angle = decoded.IndexOf('<', StringComparison.Ordinal);
            string open = angle < 0 ? decoded : decoded[..angle];

            string generic = open + "." + memberName;
            if (AlwaysBlocking.Contains(generic))
            {
                return generic;
            }

            return AwaiterResults.Contains(generic) && !IsGeneratedStateMachine(declaringType, method)
                ? generic
                : null;
        }

        if (member.Parent.Kind is not HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        string typeName = metadata.GetString(type.Namespace);
        string shortName = metadata.GetString(type.Name);

        // A nested type such as ConfiguredTaskAwaitable.ConfiguredTaskAwaiter
        // carries its declaring type in ResolutionScope rather than in
        // Namespace, so it is rebuilt here to match the names above.
        if (type.ResolutionScope.Kind is HandleKind.TypeReference)
        {
            TypeReference outer =
                metadata.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            typeName = metadata.GetString(outer.Namespace) + "." + metadata.GetString(outer.Name);
        }

        string full = typeName + "." + shortName + "." + memberName;

        if (AlwaysBlocking.Contains(full))
        {
            return full;
        }

        return AwaiterResults.Contains(full) && !IsGeneratedStateMachine(declaringType, method)
            ? full
            : null;
    }

    private static Assembly Resolve(string name) => name switch
    {
        "SourceSharp.MapFormats" => typeof(BspData).Assembly,
        "SourceSharp.MapTools" => typeof(VPath).Assembly,
        "SourceSharp.MapCompile" => typeof(Program).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a shipping assembly"),
    };
}

/// <summary>
/// Turns a signature blob into a plain type name, which is all the
/// sync-over-async rule needs from it.
/// </summary>
/// <remarks>
/// <see cref="System.Reflection.Metadata"/> decodes signatures through a
/// provider rather than handing back strings, because most callers want real
/// types. This one wants the name and nothing else, so every member that cannot
/// appear in the position being examined returns a placeholder rather than
/// pretending to be complete.
/// </remarks>
internal sealed class TypeNameDecoder : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(definition.Namespace);
        string name = reader.GetString(definition.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);

        // A nested type carries its declaring type in ResolutionScope, which is
        // how ConfiguredTaskAwaitable`1.ConfiguredTaskAwaiter is spelled.
        if (reference.ResolutionScope.Kind is HandleKind.TypeReference)
        {
            return GetTypeFromReference(
                reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "." + name;
        }

        string ns = reader.GetString(reference.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromSpecification(
        MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
