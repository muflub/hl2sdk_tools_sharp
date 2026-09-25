using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap phys</c>: which physics libraries this machine has, and which one a
/// compile will use.
/// </summary>
/// <remarks>
/// This is a user-facing command rather than an internal detail because the
/// choice changes the output. Spike 0b cooked one cube through two installed
/// builds of <c>vphysics.so</c> and got different bytes, so "which library did
/// this map's collision come from" is a question a user has to be able to ask
/// and answer.
/// </remarks>
public static class PhysCommand
{
    /// <summary>
    /// Runs <c>ssmap phys list</c> or <c>ssmap phys select</c>.
    /// </summary>
    /// <param name="fileSystem">Where to look.</param>
    /// <param name="searchRoots">
    /// Directories whose immediate children are game installs. Supplied by the
    /// caller rather than discovered from the environment, because where Steam
    /// keeps its libraries is the host's knowledge and not the library's.
    /// </param>
    /// <param name="args">The arguments after <c>phys</c>.</param>
    /// <param name="output">Where the table goes.</param>
    /// <param name="displayRoot">
    /// What the filesystem's root is called on this machine, prepended to every
    /// path in the output.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        string displayRoot = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(displayRoot);

        string subcommand = args.Count > 0 ? args[0] : "list";
        string? selector = args.Count > 1 ? args[1] : null;

        if (subcommand is not ("list" or "select" or "cook"))
        {
            await output.WriteLineAsync($"ssmap phys: unknown subcommand \"{subcommand}\"")
                .ConfigureAwait(false);
            await output.WriteLineAsync("usage: ssmap phys list | ssmap phys select <game|path> | ssmap phys cook <game|path>")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        ImmutableArray<VPhysicsLibrary> found =
            await VPhysicsLocator.DiscoverAsync(fileSystem, searchRoots, cancellationToken)
                .ConfigureAwait(false);

        await WriteTableAsync(found, output, displayRoot).ConfigureAwait(false);

        if (subcommand == "list")
        {
            return Program.ExitSuccess;
        }

        if (VPhysicsLocator.TrySelect(found, selector, out VPhysicsLibrary? chosen, out string? problem))
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                $"selected {chosen!.Key}  build-id {chosen.Identity.BuildId ?? "(none)"}")
                .ConfigureAwait(false);
            await output.WriteLineAsync($"  {Display(chosen.Path, displayRoot)}")
                .ConfigureAwait(false);

            return subcommand == "cook"
                ? await CookAsync(fileSystem, chosen, args, output, cancellationToken).ConfigureAwait(false)
                : Program.ExitSuccess;
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"ssmap phys: {problem}").ConfigureAwait(false);
        return Program.ExitUsage;
    }

    /// <summary>Set in a relaunched child, so a relaunch that did not help cannot loop.</summary>
    public const string RelaunchedVariable = "SSMAP_VPHYSICS_RELAUNCHED";

    /// <summary>
    /// The <c>LD_LIBRARY_PATH</c> this process must be RE-LAUNCHED with for
    /// the library in <paramref name="libraryDirectory"/> to load, or null
    /// when the current one already names it.
    /// </summary>
    /// <param name="current">The process's <c>LD_LIBRARY_PATH</c>, or null.</param>
    /// <param name="libraryDirectory">The library's <c>bin/linux64</c>.</param>
    /// <returns>The value to relaunch with, or null.</returns>
    /// <remarks>
    /// glibc reads the variable once at start-up, so setting it in-process
    /// does nothing (spike 0b); <c>libtier0.so</c> has no <c>DT_SONAME</c>, so
    /// nothing else satisfies <c>vphysics.so</c>'s <c>DT_NEEDED</c>. The
    /// directory is PREPENDED so it wins over anything already on the path.
    /// </remarks>
    public static string? RelaunchLibraryPath(string? current, string libraryDirectory)
    {
        ArgumentNullException.ThrowIfNull(libraryDirectory);
        string wanted = libraryDirectory.TrimEnd('/');
        string[] entries = (current ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (entries.Any(e => string.Equals(e.TrimEnd('/'), wanted, StringComparison.Ordinal)))
        {
            return null;
        }

        return string.IsNullOrEmpty(current) ? wanted : wanted + ":" + current;
    }

    /// <summary>
    /// <c>ssmap phys cook</c>: load the chosen library and cook a 32-unit cube,
    /// relaunching this process with <c>LD_LIBRARY_PATH</c> first if it has to.
    /// </summary>
    private static async Task<int> CookAsync(
        IFileSystem fileSystem,
        VPhysicsLibrary chosen,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (fileSystem is not PhysicalFileSystem disk)
        {
            await output.WriteLineAsync("ssmap phys cook: the library must be on a physical disk.").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string libraryFile = disk.ToHostPath(chosen.Path);
        string directory = libraryFile[..libraryFile.LastIndexOf('/')];
        string? relaunch = RelaunchLibraryPath(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"), directory);

        if (relaunch is not null)
        {
            if (Environment.GetEnvironmentVariable(RelaunchedVariable) == "1")
            {
                await output.WriteLineAsync(
                    $"ssmap phys cook: relaunched with LD_LIBRARY_PATH naming {directory} and it still does not.")
                    .ConfigureAwait(false);
                return Program.ExitFailure;
            }

            await output.WriteLineAsync($"relaunching with LD_LIBRARY_PATH={relaunch}").ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await RelaunchAsync(relaunch, ["phys", .. args], cancellationToken).ConfigureAwait(false);
        }

        await using VPhysicsCollisionCooker cooker =
            await VPhysicsCollisionCooker.CreateAsync(disk, chosen.Path, cancellationToken).ConfigureAwait(false);

        (float X, float Y, float Z, float D)[] faces =
            [(1, 0, 0, 16), (-1, 0, 0, 16), (0, 1, 0, 16), (0, -1, 0, 16), (0, 0, 1, 16), (0, 0, -1, 16)];
        CollisionPlane[] planes = [.. faces.Select(f => new CollisionPlane(new(f.X, f.Y, f.Z), f.D))];
        CookedCollide? cube = await cooker.CookFromPlanesAsync(planes, 0f, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync(cooker.CookerIdentity).ConfigureAwait(false);
        await output.WriteLineAsync($"library flushes denormals in its calls: {cooker.LibraryFlushesDenormals}").ConfigureAwait(false);
        if (cube is not { } cooked)
        {
            await output.WriteLineAsync("the cube did not cook.").ConfigureAwait(false);
            return Program.ExitFailure;
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"32-unit cube: {cooked.Bytes.Length} bytes, sha256 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(cooked.Bytes.Span))}"))
            .ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Runs this program again with <c>LD_LIBRARY_PATH</c> set, and waits for it.
    /// </summary>
    /// <param name="libraryPath">The <c>LD_LIBRARY_PATH</c> to start it with.</param>
    /// <param name="args">Its whole command line, command name first.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The child's exit code.</returns>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000:AssemblyLocationEmpty",
        Justification = "Only the dotnet host relaunches itself with the assembly path as the child's first argument; an AOT single-file binary's Environment.ProcessPath is the native binary (so the host-name test fails and Location is never read), and the is { Length: > 0 } guard already excludes an embedded assembly whose Location reports empty.")]
    internal static async Task<int> RelaunchAsync(string libraryPath, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string host = Environment.ProcessPath ?? "dotnet";
        System.Diagnostics.ProcessStartInfo start = new(host) { UseShellExecute = false };

        // Under `dotnet ssmap.dll` the host is dotnet and the assembly is an argument.
        if (Path.GetFileNameWithoutExtension(host) == "dotnet"
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            start.ArgumentList.Add(entry);
        }

        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["LD_LIBRARY_PATH"] = libraryPath;
        start.Environment[RelaunchedVariable] = "1";

        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("could not relaunch " + host);
        await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return child.ExitCode;
    }

    /// <summary>
    /// Renders the discovered libraries as a table.
    /// </summary>
    /// <param name="libraries">What discovery found.</param>
    /// <param name="output">Where the table goes.</param>
    /// <param name="displayRoot">
    /// What the filesystem's root is called, prepended to every path shown.
    /// </param>
    /// <returns>A task that completes when the table has been written.</returns>
    /// <remarks>
    /// Separate and public so the rendering can be exercised by a fact without
    /// a disk, and so a host can reuse it.
    /// </remarks>
    public static async Task WriteTableAsync(
        IReadOnlyList<VPhysicsLibrary> libraries,
        TextWriter output,
        string displayRoot = "")
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(displayRoot);

        if (libraries.Count == 0)
        {
            await output.WriteLineAsync(
                "no vphysics library found under the search roots.").ConfigureAwait(false);
            return;
        }

        int keyWidth = Math.Max(4, libraries.Max(l => l.Key.Length));

        await output.WriteLineAsync(
            $"{"use".PadRight(3)}  {"game".PadRight(keyWidth)}  {"arch".PadRight(5)}  build-id")
            .ConfigureAwait(false);

        foreach (VPhysicsLibrary library in libraries)
        {
            // The marker is the first column because "which of these can I
            // actually use" is the first question, and a 32-bit library sitting
            // in a game's bin/ looks identical to the usable one otherwise.
            string marker = library.IsUsable ? " * " : "   ";
            string arch = library.Identity.Architecture switch
            {
                ElfArchitecture.X64 => "x64",
                ElfArchitecture.X86 => "x86",
                _ => "?",
            };

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{marker}  {library.Key.PadRight(keyWidth)}  {arch.PadRight(5)}  " +
                $"{library.Identity.BuildId ?? "(none)"}")).ConfigureAwait(false);

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"     {Display(library.Path, displayRoot)}  ({library.Length:N0} bytes)"))
                .ConfigureAwait(false);
        }

        if (!libraries.Any(l => l.IsUsable))
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                "none of these is 64-bit, so none can be loaded. The loadable build lives in "
                + "bin/linux64/, not bin/.").ConfigureAwait(false);
        }
        else if (libraries.Count(l => l.IsUsable) > 1)
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                "more than one is usable, and they do NOT agree: two builds cook the same shape "
                + "to different bytes. Choose one with -vphysics <game>.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A path as the user's shell would spell it.
    /// </summary>
    /// <param name="path">A path relative to the filesystem's root.</param>
    /// <param name="displayRoot">What that root is called on this machine.</param>
    /// <returns>The path a user could paste.</returns>
    /// <remarks>
    /// A <see cref="VPath"/> is rooted at its filesystem and carries no leading
    /// separator, which is right for the library and wrong for a person: a
    /// discovery rooted at "/" would otherwise print
    /// <c>home/someone/.steam/...</c>, which looks like a path and is not one.
    /// </remarks>
    private static string Display(VPath path, string displayRoot)
    {
        if (displayRoot.Length == 0)
        {
            return path.Value;
        }

        return displayRoot.EndsWith('/')
            ? displayRoot + path.Value
            : displayRoot + "/" + path.Value;
    }
}
