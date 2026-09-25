using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// Where an installed Source SDK Base 2013 Multiplayer is, if there is one.
/// </summary>
/// <remarks>
/// The default matches the Makefile's <c>SDKBASE</c>, and the same
/// <c>SDKBASE</c> environment variable overrides it, so a machine that installs
/// the game somewhere else does not need a second setting.
/// </remarks>
internal static class InstalledGameContent
{
    /// <summary>The archive the real-VPK facts read.</summary>
    /// <remarks>
    /// <c>hl2_misc_dir.vpk</c> rather than a textures archive: it is the one
    /// every SDK Base install has, it holds <c>scripts/</c> and
    /// <c>materials/</c> both, and it is multi-part, which is the case a
    /// single-file fixture cannot exercise.
    /// </remarks>
    public const string ProbeArchive = "hl2/hl2_misc_dir.vpk";

    /// <summary>The install directory, whether or not it exists.</summary>
    public static string BaseDirectory =>
        Environment.GetEnvironmentVariable("SDKBASE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam",
            "steam",
            "steamapps",
            "common",
            "Source SDK Base 2013 Multiplayer");

    /// <summary>The host path of <see cref="ProbeArchive"/>.</summary>
    public static string ProbeArchivePath =>
        Path.Combine(BaseDirectory, ProbeArchive.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Why the real-content facts cannot run, or null when they can.
    /// </summary>
    public static string? SkipReason =>
        File.Exists(ProbeArchivePath)
            ? null
            : $"no installed game content: {ProbeArchivePath} is not there (set SDKBASE, or run `make game`)";
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with the reason in the
/// runner's own output — when this machine has no installed Source SDK Base
/// 2013 Multiplayer to read.
/// </summary>
/// <remarks>
/// The same shape, and for the same reason, as
/// <see cref="SandboxMapFactAttribute"/>: the alternative is an early return
/// that reports a PASS, and this repo has already been bitten once by a missing
/// input reading as a green test. A real skip prints the reason and moves the
/// count into the summary's Skipped column, so "the game is not installed here"
/// is legible without opening this file.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class InstalledGameFactAttribute : FactAttribute
{
    /// <summary>Skips unless the installed content is there.</summary>
    public InstalledGameFactAttribute() => Skip = InstalledGameContent.SkipReason;
}
