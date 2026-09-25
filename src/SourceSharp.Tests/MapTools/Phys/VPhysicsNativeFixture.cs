using System.Reflection;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Phys;

using Xunit;
using Xunit.Sdk;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// Where the pinned 64-bit <c>vphysics.so</c> is, and whether THIS test
/// process is the child that may load it.
/// </summary>
/// <remarks>
/// <para>
/// A NATIVE FAULT MUST FAIL ONE FACT, NEVER THE TEST HOST. So no fact of the
/// ordinary suite ever loads the library. The facts that do are the "native
/// tier" (<see cref="VPhysicsNativeFactAttribute"/>, trait
/// <c>Tier=vphysics-native</c>); they skip in the ordinary suite and run only
/// in a CHILD <c>dotnet test</c> that <see cref="VPhysicsNativeTierTests"/>
/// launches, one parent fact per native fact, so a SIGSEGV inside the closed
/// library is reported as that fact's failure (with the child's exit) and the
/// remaining facts are re-run without it.
/// </para>
/// <para>
/// The child is also how <c>LD_LIBRARY_PATH</c> gets set: the library can only
/// be loaded by a process STARTED with it naming its directory (spike 0b:
/// <c>libtier0.so</c> has no <c>DT_SONAME</c>, and glibc reads the variable once
/// at start-up), and a parent can start a child that way.
/// </para>
/// <para>
/// PINNED to SDK Base 2013 Multiplayer's copy, the build the repo targets
/// (md5 <c>95eb3dfb50e53c25d2c06047243faa03</c>, spike 0b): TF2's cooks the
/// same cube to different bytes.
/// </para>
/// </remarks>
internal static class PinnedVPhysics
{
    /// <summary>The md5 the byte-exact facts are pinned to.</summary>
    public const string PinnedMd5 = "95eb3dfb50e53c25d2c06047243faa03";

    /// <summary>Set to 1 in the child process that runs the native tier.</summary>
    public const string ChildVariable = "SOURCESHARP_VPHYSICS_CHILD";

    /// <summary>Where the child journals each native fact's start and end.</summary>
    public const string JournalVariable = "SOURCESHARP_VPHYSICS_JOURNAL";

    /// <summary>The trait value of the native tier.</summary>
    public const string Tier = "vphysics-native";

    /// <summary>The library's directory, whether or not it exists.</summary>
    public static string Directory =>
        Path.Combine(
            Environment.GetEnvironmentVariable("SDKBASE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".steam", "steam", "steamapps", "common", "Source SDK Base 2013 Multiplayer"),
            "bin", "linux64");

    /// <summary>The library itself.</summary>
    public static string LibraryPath => Path.Combine(Directory, "vphysics.so");

    /// <summary>Whether this process is the native-tier child.</summary>
    public static bool IsChild => Environment.GetEnvironmentVariable(ChildVariable) == "1";

    /// <summary>Why no native work can happen on this machine at all, or null.</summary>
    public static string? MissingLibraryReason =>
        File.Exists(LibraryPath)
            ? null
            : $"no pinned vphysics.so at {LibraryPath} (SDK Base 2013 Multiplayer not installed).";

    /// <summary>Why a native-tier fact cannot run in THIS process, or null.</summary>
    public static string? SkipReason
    {
        get
        {
            if (MissingLibraryReason is { } missing)
            {
                return missing;
            }

            if (!IsChild)
            {
                return "native tier: runs in a child process through VPhysicsNativeTierTests (or "
                    + "tools/maptools-phys-tests), so a fault in the closed library fails one fact "
                    + "and not this test host.";
            }

            string[] entries = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries);

            string wanted = Path.GetFullPath(Directory).TrimEnd('/');
            if (!entries.Any(e => string.Equals(Path.GetFullPath(e).TrimEnd('/'), wanted, StringComparison.Ordinal)))
            {
                return "the child was not launched with LD_LIBRARY_PATH=" + Directory
                    + ", so vphysics.so cannot be loaded (glibc reads it once, at start-up).";
            }

            return null;
        }
    }

    /// <summary>The name a native fact is journalled and reported under.</summary>
    /// <param name="method">The fact.</param>
    /// <returns>Namespace-qualified class name, a dot, the method name.</returns>
    /// <remarks>
    /// The REFLECTED type, not the declaring one: a native test inherited from
    /// an abstract base (VbspPhysStockGateBase, run once per cooker by two
    /// concrete classes) is a different test per concrete class, and xUnit
    /// names it by that class. Keyed on the declaring type, both classes'
    /// runs collapsed onto one name the child never reports.
    /// </remarks>
    public static string NameOf(MethodInfo method) => method.ReflectedType!.FullName + "." + method.Name;
}

/// <summary>
/// Marks a native-tier test, and says whether the PARENT should expect the
/// child to run it (a prerequisite other than the library -- a corpus
/// directory, say -- that is missing makes the parent leave it out, rather
/// than read the child's skip as a pass).
/// </summary>
public interface IVPhysicsNativeTest
{
    /// <summary>Why the child would skip this for a reason other than being the parent, or null.</summary>
    string? ParentSkipReason { get; }
}

/// <summary>A fact of the native tier: it runs only in the child process.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class VPhysicsNativeFactAttribute : FactAttribute, IVPhysicsNativeTest
{
    /// <summary>Skips, saying why, outside the native-tier child.</summary>
    public VPhysicsNativeFactAttribute()
        : this(null)
    {
    }

    /// <summary>Skips outside the child, or when a further prerequisite is missing.</summary>
    /// <param name="extraSkipReason">Why the further prerequisite is missing, or null.</param>
    protected VPhysicsNativeFactAttribute(string? extraSkipReason)
    {
        ParentSkipReason = extraSkipReason;
        Skip = PinnedVPhysics.SkipReason ?? extraSkipReason;
    }

    /// <inheritdoc />
    public string? ParentSkipReason { get; }
}

/// <summary>A theory of the native tier: it runs only in the child process.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class VPhysicsNativeTheoryAttribute : TheoryAttribute, IVPhysicsNativeTest
{
    /// <summary>Skips, saying why, outside the native-tier child.</summary>
    public VPhysicsNativeTheoryAttribute()
        : this(null)
    {
    }

    /// <summary>Skips outside the child, or when a further prerequisite is missing.</summary>
    /// <param name="extraSkipReason">Why the further prerequisite is missing, or null.</param>
    protected VPhysicsNativeTheoryAttribute(string? extraSkipReason)
    {
        ParentSkipReason = extraSkipReason;
        Skip = PinnedVPhysics.SkipReason ?? extraSkipReason;
    }

    /// <inheritdoc />
    public string? ParentSkipReason { get; }
}

/// <summary>
/// Journals each native fact's start and end, so the parent can name the fact
/// that was running when a child died.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class VPhysicsJournalAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest) => Write("START", methodUnderTest);

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest) => Write("END", methodUnderTest);

    private static void Write(string what, MethodInfo method)
    {
        string? journal = Environment.GetEnvironmentVariable(PinnedVPhysics.JournalVariable);
        if (!string.IsNullOrEmpty(journal))
        {
            // Appended and flushed per line: the line before a crash is the evidence.
            File.AppendAllText(journal, what + " " + PinnedVPhysics.NameOf(method) + "\n");
        }
    }
}

/// <summary>
/// The ONE cooker the child process may have (a second is refused, because
/// the library is one set of globals), shared by every native-tier class.
/// </summary>
public sealed class VPhysicsCookerFixture : IAsyncLifetime
{
    private VPhysicsCollisionCooker? _cooker;

    /// <summary>The cooker; only valid in the native-tier child.</summary>
    public VPhysicsCollisionCooker Cooker =>
        _cooker ?? throw new InvalidOperationException(PinnedVPhysics.SkipReason ?? "the cooker did not start.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (PinnedVPhysics.SkipReason is not null)
        {
            return;
        }

        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        _cooker = await VPhysicsCollisionCooker.CreateAsync(host, host.ToVirtualPath(PinnedVPhysics.LibraryPath));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_cooker is not null)
        {
            await _cooker.DisposeAsync();
        }
    }
}

/// <summary>Every native-tier class shares one cooker and never runs in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VPhysicsCollection : ICollectionFixture<VPhysicsCookerFixture>
{
    /// <summary>The collection's name.</summary>
    public const string Name = "vphysics";
}
