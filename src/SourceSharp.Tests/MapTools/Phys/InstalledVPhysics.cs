using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The 64-bit <c>vphysics.so</c> of an installed Source game, when this machine
/// has one.
/// </summary>
/// <remarks>
/// Facts that read a real library are worth having -- a synthetic ELF header
/// proves the parser handles what the test author imagined, not what GNU ld
/// actually emits -- but a game install is not something a checkout can rely
/// on. So these facts SKIP visibly rather than pass quietly, which is the
/// distinction this project learned the hard way and encoded in
/// <see cref="SandboxMapFactAttribute"/>.
/// </remarks>
internal static class InstalledVPhysics
{
    /// <summary>
    /// The first 64-bit <c>vphysics.so</c> found in a Steam install, or null.
    /// </summary>
    /// <returns>An absolute path, or null when no game is installed here.</returns>
    public static string? Path64() => AllPaths64().FirstOrDefault();

    /// <summary>
    /// Every 64-bit <c>vphysics.so</c> in a Steam install, in directory order.
    /// </summary>
    /// <returns>Absolute paths; empty when no game is installed here.</returns>
    public static IReadOnlyList<string> AllPaths64()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string common = Path.Combine(home, ".steam", "steam", "steamapps", "common");

        if (!Directory.Exists(common))
        {
            return [];
        }

        List<string> found = [];
        foreach (string game in Directory.EnumerateDirectories(common).OrderBy(d => d, StringComparer.Ordinal))
        {
            string candidate = Path.Combine(game, "bin", "linux64", "vphysics.so");
            if (File.Exists(candidate))
            {
                found.Add(candidate);
            }
        }

        return found;
    }
}

/// <summary>
/// Runs <c>readelf</c>, so a fact can check this port's ELF reader against an
/// independent implementation of the same specification.
/// </summary>
internal static class Readelf
{
    /// <summary>Whether <c>readelf</c> can be run on this machine.</summary>
    public static bool IsAvailable => ResolvePath() is not null;

    /// <summary>
    /// The GNU build id <c>readelf -n</c> reports for a file.
    /// </summary>
    /// <param name="path">The ELF image to inspect.</param>
    /// <returns>Lower-case hex, or null when readelf reported none.</returns>
    public static async Task<string?> BuildIdAsync(string path)
    {
        System.Diagnostics.ProcessStartInfo start = new(ResolvePath()!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add(path);

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        foreach (string line in output.Split('\n'))
        {
            int marker = line.IndexOf("Build ID:", StringComparison.Ordinal);
            if (marker >= 0)
            {
                return line[(marker + "Build ID:".Length)..].Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    private static string? ResolvePath()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, "readelf");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the cross-check against <c>readelf</c>,
/// skipping when either the tool or a game install is absent.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReadelfFactAttribute : FactAttribute
{
    /// <summary>Skips unless both readelf and a 64-bit library are present.</summary>
    public ReadelfFactAttribute()
    {
        if (InstalledVPhysics.Path64() is null)
        {
            Skip = "no installed 64-bit vphysics.so to cross-check against.";
        }
        else if (!Readelf.IsAvailable)
        {
            Skip = "readelf is not on PATH, so this port's ELF reader cannot be checked against an "
                + "independent implementation here. The synthetic-header facts still run.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for facts needing two or more installed
/// physics libraries to compare.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TwoInstalledVPhysicsFactAttribute : FactAttribute
{
    /// <summary>Skips unless at least two 64-bit libraries are installed.</summary>
    public TwoInstalledVPhysicsFactAttribute()
    {
        int count = InstalledVPhysics.AllPaths64().Count;
        if (count < 2)
        {
            Skip = $"only {count} game(s) with a 64-bit vphysics.so are installed here, so this "
                + "machine cannot demonstrate that two builds differ. That divergence is what "
                + "makes choosing between them necessary; it is measured in spike 0b.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips, with the reason visible in the
/// runner's output, when no Source game is installed on this machine.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class InstalledVPhysicsFactAttribute : FactAttribute
{
    /// <summary>Skips unless a 64-bit vphysics library can be found.</summary>
    public InstalledVPhysicsFactAttribute()
    {
        if (InstalledVPhysics.Path64() is null)
        {
            Skip = "NO SOURCE GAME INSTALLED HERE: no bin/linux64/vphysics.so under "
                + "~/.steam/steam/steamapps/common. This fact reads a REAL library, because a "
                + "synthetic ELF header only proves the parser handles what the test author "
                + "imagined. The synthetic-header facts beside it still run, so the parsing logic "
                + "stays covered either way.";
        }
    }
}
