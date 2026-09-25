using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The parent side of the native tier: one fact here per native fact, each
/// answered by a CHILD <c>dotnet test</c> that loads vphysics.
/// </summary>
/// <remarks>
/// <para>
/// The child runs every native fact once (trait <c>Tier=vphysics-native</c>)
/// with <c>LD_LIBRARY_PATH</c> set, writing a TRX and a start/end journal. When
/// it dies -- a SIGSEGV inside the closed library -- the journal's last START
/// with no END is the fact that killed it: that fact FAILS here with the
/// child's exit status, and the child is run again without it and without
/// the facts that already finished, until a run completes. So a native
/// crash fails exactly one fact and never this test host.
/// </para>
/// <para>
/// Skips only when the pinned library is not installed. It is not the unit
/// tier's business whether a game is on the machine.
/// </para>
/// </remarks>
public class VPhysicsNativeTierTests
{
    private static readonly Lazy<Task<IReadOnlyDictionary<string, ChildOutcome>>> Outcomes =
        new(RunChildrenAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every native fact in this assembly, by journal name.</summary>
    public static TheoryData<string> NativeFacts()
    {
        TheoryData<string> data = [];
        foreach (string name in NativeFactNames())
        {
            data.Add(name);
        }

        return data;
    }

    [VPhysicsIsolatedTheory]
    [MemberData(nameof(NativeFacts))]
    public async Task TheNativeFactPassesInAChildProcess(string nativeFact)
    {
        IReadOnlyDictionary<string, ChildOutcome> outcomes = await Outcomes.Value;

        Assert.True(outcomes.TryGetValue(nativeFact, out ChildOutcome? outcome),
            $"the child process never reported {nativeFact}.");
        Assert.True(outcome!.Passed, outcome.Message);
    }

    [VPhysicsIsolatedFact]
    public void TheNativeTierIsNotEmpty()
    {
        // A tier that discovered nothing would make every parent fact vacuous.
        Assert.True(NativeFactNames().Count >= 30, NativeFactNames().Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static IReadOnlyList<string> NativeFactNames() =>
    [
        .. typeof(VPhysicsNativeTierTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(m => m.GetCustomAttributes().OfType<IVPhysicsNativeTest>().Any(a => a.ParentSkipReason is null))
            .Select(PinnedVPhysics.NameOf)
            .Order(StringComparer.Ordinal),
    ];

    private static async Task<IReadOnlyDictionary<string, ChildOutcome>> RunChildrenAsync()
    {
        ConcurrentDictionary<string, ChildOutcome> outcomes = new(StringComparer.Ordinal);
        IReadOnlyList<string> all = NativeFactNames();
        // Beside the test assembly, on disk: /tmp is a RAM disk on the dev box.
        string work = Path.Combine(AppContext.BaseDirectory, "vphysics-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            for (int attempt = 0; attempt < all.Count + 1; attempt++)
            {
                List<string> remaining = [.. all.Where(n => !outcomes.ContainsKey(n))];
                if (remaining.Count == 0)
                {
                    break;
                }

                string journal = Path.Combine(work, $"journal{attempt}.txt");
                string trx = Path.Combine(work, $"run{attempt}.trx");
                (int exit, string output) = await RunChildAsync(remaining, all, journal, trx);

                foreach ((string name, ChildOutcome outcome) in ReadTrx(trx))
                {
                    outcomes.TryAdd(name, outcome);
                }

                string? crashed = CrashedFact(journal);
                if (crashed is not null && !outcomes.ContainsKey(crashed))
                {
                    outcomes[crashed] = new ChildOutcome(false,
                        $"the child test host DIED while running {crashed} (dotnet test exit {exit}). "
                        + "A native fault inside vphysics.so; re-run with CORE=1 under run-capped and read "
                        + "`coredumpctl info` for the frame. Child output tail:\n" + Tail(output));
                    continue;
                }

                // No crash and nothing new learnt: stop rather than loop.
                foreach (string name in remaining.Where(n => !outcomes.ContainsKey(n)))
                {
                    outcomes[name] = new ChildOutcome(false,
                        $"the child (exit {exit}) did not report {name}. Output tail:\n" + Tail(output));
                }

                break;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return outcomes;
    }

    private static async Task<(int Exit, string Output)> RunChildAsync(
        IReadOnlyList<string> remaining, IReadOnlyList<string> all, string journal, string trx)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Environment.ProcessPath ?? "dotnet";

        // Everything still to run, and nothing already answered: the filter
        // names the tier and excludes the rest by full name.
        StringBuilder filter = new("Tier=" + PinnedVPhysics.Tier);
        foreach (string done in all.Except(remaining))
        {
            filter.Append("&FullyQualifiedName!=").Append(done);
        }

        ProcessStartInfo start = new(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(typeof(VPhysicsNativeTierTests).Assembly.Location);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add(filter.ToString());
        start.ArgumentList.Add("--logger");
        start.ArgumentList.Add("trx;LogFileName=" + trx);
        start.Environment[PinnedVPhysics.ChildVariable] = "1";
        start.Environment[PinnedVPhysics.JournalVariable] = journal;
        start.Environment["LD_LIBRARY_PATH"] = PinnedVPhysics.Directory
            + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") is { Length: > 0 } existing ? ":" + existing : string.Empty);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static IEnumerable<(string Name, ChildOutcome Outcome)> ReadTrx(string trx)
    {
        if (!File.Exists(trx))
        {
            yield break;
        }

        XDocument doc = XDocument.Load(trx);
        XNamespace ns = doc.Root!.Name.Namespace;

        // A theory reports one result per case, "Name(args)"; the method passes
        // only if every case did.
        Dictionary<string, ChildOutcome> byMethod = new(StringComparer.Ordinal);
        foreach (XElement result in doc.Descendants(ns + "UnitTestResult"))
        {
            string testName = (string?)result.Attribute("testName") ?? string.Empty;
            int paren = testName.IndexOf('(', StringComparison.Ordinal);
            string method = paren >= 0 ? testName[..paren] : testName;
            string outcome = (string?)result.Attribute("outcome") ?? "Unknown";
            string message = result.Descendants(ns + "Message").FirstOrDefault()?.Value ?? string.Empty;

            bool passed = outcome == "Passed";
            ChildOutcome next = new(passed, $"{testName}: {outcome} {message}");

            byMethod[method] = byMethod.TryGetValue(method, out ChildOutcome? previous) && !previous.Passed
                ? previous
                : next;
        }

        foreach ((string name, ChildOutcome outcome) in byMethod)
        {
            yield return (name, outcome);
        }
    }

    private static string? CrashedFact(string journal)
    {
        if (!File.Exists(journal))
        {
            return null;
        }

        HashSet<string> open = new(StringComparer.Ordinal);
        string? last = null;
        foreach (string line in File.ReadLines(journal))
        {
            if (line.StartsWith("START ", StringComparison.Ordinal))
            {
                last = line[6..];
                open.Add(last);
            }
            else if (line.StartsWith("END ", StringComparison.Ordinal))
            {
                open.Remove(line[4..]);
            }
        }

        return last is not null && open.Contains(last) ? last : open.FirstOrDefault();
    }

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];

    private sealed record ChildOutcome(bool Passed, string Message);
}

/// <summary>A parent fact of the native tier: skips when no library is installed, and inside the child.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VPhysicsIsolatedFactAttribute : FactAttribute
{
    /// <summary>Skips, saying why.</summary>
    public VPhysicsIsolatedFactAttribute() =>
        Skip = PinnedVPhysics.IsChild ? "the child runs the native tier, not its parent." : PinnedVPhysics.MissingLibraryReason;
}

/// <summary>A parent theory of the native tier.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VPhysicsIsolatedTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips, saying why.</summary>
    public VPhysicsIsolatedTheoryAttribute() =>
        Skip = PinnedVPhysics.IsChild ? "the child runs the native tier, not its parent." : PinnedVPhysics.MissingLibraryReason;
}
