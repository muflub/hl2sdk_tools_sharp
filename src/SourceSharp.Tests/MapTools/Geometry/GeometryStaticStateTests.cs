using System.Reflection;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Geometry;

/// <summary>
/// No mutable static state in the geometry kernel or the scheduler.
/// </summary>
/// <remarks>
/// <para>
/// Stock is built on it and cannot escape it: alone has
/// <c>winding_pool</c>, <c>c_active_windings</c>, <c>c_peak_windings</c>.
/// <c>c_winding_allocs</c>, <c>c_winding_points</c> and <c>c_removed</c>
/// (lines 22-25, 34, 88), and its own comment on line 20 admits the counters
/// are "an awefull coherence problem" and so are only maintained when running
/// single threaded. adds <c>dispatch</c>, <c>workcount</c>.
/// <c>numthreads</c>, <c>crit</c>, <c>workfunction</c> and
/// <c>g_RunThreadsData</c>.
/// </para>
/// <para>
/// That is why stock cannot compile two maps in one process, cannot be a
/// library, and cannot be tested a piece at a time. This fact is what stops the
/// port from growing the same thing back one convenience field at a time.
/// </para>
/// </remarks>
public class GeometryStaticStateTests
{
    private static readonly string[] Namespaces =
    [
        "SourceSharp.MapFormats.Geometry",
        "SourceSharp.MapTools.Geometry",
        "SourceSharp.MapTools.Parallel",
    ];

    [Fact]
    public void NoTypeInTheseNamespacesHasAMutableStaticField()
    {
        List<string> offenders = [];

        foreach (Type type in Types())
        {
            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly))
            {
                // const is IsLiteral, `static readonly` is IsInitOnly, and a
                // get-only static auto-property compiles to the latter. Anything
                // else is a static someone can assign to.
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    offenders.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoTypeInTheseNamespacesHasAStaticSetter()
    {
        // A readonly backing field is no protection if there is a static
        // property or method writing through it by another route; a settable
        // static property is the usual way that happens.
        List<string> offenders = [];

        foreach (Type type in Types())
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod is not null)
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheScanActuallyFoundTheseTypes()
    {
        // A reflection fact that silently matched nothing would pass forever.
        Assert.Contains(typeof(Plane), Types());
        Assert.Contains(typeof(WindingArena), Types());
        Assert.Contains(typeof(BitVectorOps), Types());
        Assert.Contains(typeof(WorkQueue), Types());
    }

    [Fact]
    public void TheScanWouldNoticeAMutableStaticIfThereWereOne()
    {
        // The mutation proof for the fact above, in the same file: a type
        // carrying exactly the shape being banned, checked with exactly the
        // same predicate. If the predicate were wrong, this would be empty and
        // the fact above would be vacuous.
        FieldInfo[] fields = typeof(DeliberatelyMutable).GetFields(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);

        Assert.Contains(fields, f => !f.IsLiteral && !f.IsInitOnly);
    }

    private static List<Type> Types()
    {
        List<Type> types = [];
        foreach (Assembly assembly in new[]
        {
            typeof(Plane).Assembly,
            typeof(WindingArena).Assembly,
        }.Distinct())
        {
            foreach (Type type in assembly.GetTypes())
            {
                // Compiler-generated display and lambda-cache classes are
                // full of mutable statics -- `<>c.<>9__1_0` is the cached
                // delegate for a static lambda -- and they are artefacts of the
                // C# compiler rather than declarations anyone wrote. The rule
                // is about source, so they are skipped.
                if (type.Namespace is { } ns
                    && Namespaces.Contains(ns)
                    && !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    private static class DeliberatelyMutable
    {
#pragma warning disable CA2211, IDE0044
        public static int Counter = 1;
#pragma warning restore CA2211, IDE0044
    }
}
