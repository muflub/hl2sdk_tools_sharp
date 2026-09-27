//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools;

/// <summary>
/// No code in the libraries reaches the platform's C math library: every
/// elementary function goes through <see cref="DetMath"/> or
/// <see cref="DetMathF"/>, so a compile's output does not depend on the OS.
/// </summary>
/// <remarks>
/// <para>
/// Checked in the IL of the built assemblies, like the file-system seam: the
/// scan resolves every call and fails on a <see cref="Math"/> or
/// <see cref="MathF"/> method that is not on the allow-list, and on a
/// transcendental reached any other way (<c>double.Sin</c>, the generic-math
/// statics on <see cref="float"/>, the vector types' <c>Sin</c>/<c>Exp</c>/...,
/// whose results depend on the instruction set).
/// </para>
/// <para>
/// The allow-list is the functions IEEE 754 defines exactly -- square root,
/// rounding, min/max, absolute value, sign manipulation, scaling by a power of
/// two -- which every platform computes to the same bits. Adding a name to it
/// is the decision this fact exists to make someone take on purpose.
/// </para>
/// </remarks>
public class DeterministicMathRuleTests
{
    /// <summary>The members of Math and MathF whose results are exact, or integer-only.</summary>
    private static readonly ImmutableHashSet<string> ExactMathMembers =
    [
        "Abs", "Min", "Max", "MinMagnitude", "MaxMagnitude", "Clamp", "Sqrt", "Floor", "Ceiling", "Round",
        "Truncate", "ScaleB", "ILogB", "CopySign", "Sign", "BitIncrement", "BitDecrement", "DivRem", "BigMul",
    ];

    /// <summary>
    /// Elementary functions by name, however they are reached: on
    /// <see cref="double"/>/<see cref="float"/> (generic math) and on the
    /// vector types.
    /// </summary>
    private static readonly ImmutableHashSet<string> Transcendentals =
    [
        "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "SinCos", "SinPi", "CosPi", "TanPi", "AsinPi",
        "AcosPi", "AtanPi", "Atan2Pi", "SinCosPi", "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh", "Exp",
        "Exp2", "Exp10", "ExpM1", "Exp2M1", "Exp10M1", "Log", "Log2", "Log10", "LogP1", "Log2P1", "Log10P1",
        "Pow", "Cbrt", "Hypot", "RootN", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "FusedMultiplyAdd",
        "IEEERemainder",
    ];

    private static readonly ImmutableHashSet<string> NumericTypes =
    [
        "System.Double", "System.Single", "System.Half",
        "System.Runtime.Intrinsics.Vector64", "System.Runtime.Intrinsics.Vector128",
        "System.Runtime.Intrinsics.Vector256", "System.Runtime.Intrinsics.Vector512",
        "System.Numerics.Vector", "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4",
        "System.Numerics.Complex",
    ];

    public static TheoryData<string> LibraryAssemblies => ["SourceSharp.MapFormats", "SourceSharp.MapTools"];

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void NoLibraryCodeCallsThePlatformMathLibrary(string assemblyName)
    {
        IReadOnlyList<string> offenders = FileSystemSeamTests.ScanCalls(
            Load(assemblyName), ImmutableHashSet<string>.Empty, (m, h, _, _) => IsPlatformMathCall(m, h));

        Assert.True(
            offenders.Count == 0,
            "use DetMath or DetMathF instead:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScanCanActuallyFindAViolation()
    {
        // This test class calls Math.Sin and MathF.Pow below; the same scan
        // over the test assembly must report them, or it is reading nothing.
        _ = Math.Sin(1.0) + MathF.Pow(2f, 0.5f) + double.Cos(1.0);
        IReadOnlyList<string> offenders = FileSystemSeamTests.ScanCalls(
            typeof(DeterministicMathRuleTests).Assembly,
            ImmutableHashSet<string>.Empty,
            (m, h, _, _) => IsPlatformMathCall(m, h));

        Assert.Contains(offenders, o => o.Contains("System.Math.Sin", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains("System.MathF.Pow", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains("System.Double.Cos", StringComparison.Ordinal));
        Assert.DoesNotContain(offenders, o => o.Contains("System.Math.Sqrt", StringComparison.Ordinal));
    }

    private static Assembly Load(string name) => name switch
    {
        "SourceSharp.MapFormats" => typeof(BspData).Assembly,
        _ => typeof(VPath).Assembly,
    };

    private static string? IsPlatformMathCall(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            // A generic instantiation, such as Vector128.Sin<float>: judge the
            // method it instantiates.
            handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

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
        string typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        string name = metadata.GetString(member.Name);
        string generic = typeName.Contains('`', StringComparison.Ordinal) ? typeName[..typeName.IndexOf('`', StringComparison.Ordinal)] : typeName;

        bool forbidden = typeName is "System.Math" or "System.MathF"
            ? !ExactMathMembers.Contains(name) && member.GetKind() == MemberReferenceKind.Method
            : NumericTypes.Contains(generic) && Transcendentals.Contains(name);

        return forbidden ? typeName + "." + name : null;
    }
}
