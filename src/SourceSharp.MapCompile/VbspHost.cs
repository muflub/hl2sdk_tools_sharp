using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap vbsp</c>'s host half: which collision cooker the compile gets, and
/// the relaunch the native one needs.
/// </summary>
/// <remarks>
/// <para>
/// Stock loads <c>vphysics.dll</c> for every compile (<c>vbsp.cpp:1309</c>),
/// and a compile whose library does not load prints
/// <c>"!!! WARNING: Can't build collision data!"</c> and writes a map with no
/// collision lumps (<c>ivp.cpp:1510</c>). This host does the same with the
/// 64-bit Linux <c>vphysics.so</c> (plan §7). The choice is
/// <see cref="VbspOptions.Cooker"/>, parsed with every other vbsp option by
/// <see cref="StockArgs.ParseVbsp"/> (<c>-cooker native|vphysics|managed|none</c>,
/// <c>-vphysics &lt;game|path&gt;</c>): native is the default,
/// <c>managed</c> is the reimplementation that loads no library and so never
/// relaunches, and <c>none</c> is the no-collision road on purpose.
/// </para>
/// <para>
/// With no <c>-vphysics</c> the compile uses SDK Base 2013 Multiplayer's
/// library when it is installed: that is the engine this repo's maps are
/// loaded by, and two builds cook the same shape to different bytes, so the
/// default is the one whose bytes the engine was built with.
/// </para>
/// <para>
/// The native library loads only in a process STARTED with
/// <c>LD_LIBRARY_PATH</c> naming its directory (spike 0b), so the host
/// relaunches itself once, exactly as <c>ssmap phys cook</c> does
/// (<see cref="PhysCommand.RelaunchLibraryPath"/>). There is one
/// <see cref="VPhysicsCollisionCooker"/> per process; this host makes it and
/// disposes of it.
/// </para>
/// </remarks>
public static class VbspHost
{
    /// <summary>The library the compile uses when <c>-vphysics</c> is not given, if installed.</summary>
    public const string DefaultLibrary = "source-sdk-base-2013-multiplayer";

    /// <summary>
    /// The library a compile uses: the <c>-vphysics</c> selector, else
    /// <see cref="DefaultLibrary"/> when it is installed, else the only
    /// usable one.
    /// </summary>
    /// <param name="found">What discovery found.</param>
    /// <param name="selector">The <c>-vphysics</c> selector, or null.</param>
    /// <param name="chosen">The library.</param>
    /// <param name="problem">Why none could be chosen.</param>
    /// <returns>Whether one was chosen.</returns>
    public static bool TrySelectLibrary(
        IReadOnlyList<VPhysicsLibrary> found, string? selector, out VPhysicsLibrary? chosen, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(found);

        if (selector is null
            && found.Any(l => l.IsUsable && string.Equals(l.Key, DefaultLibrary, StringComparison.Ordinal)))
        {
            selector = DefaultLibrary;
        }

        return VPhysicsLocator.TrySelect(found, selector, out chosen, out problem);
    }

    /// <summary>Runs <c>ssmap vbsp</c> with its collision cooker.</summary>
    /// <param name="disk">The host file system, rooted at <c>/</c>.</param>
    /// <param name="searchRoots">Where game installs are, for library discovery.</param>
    /// <param name="args">The arguments after <c>vbsp</c>.</param>
    /// <param name="output">Where the log goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        PhysicalFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        // The one parse of -cooker / -vphysics; VbspCommand parses the same line again and
        // reports any error in it, so a malformed line reaches it with no cooker.
        VbspOptions options = StockArgs.ParseVbsp(args).Options;
        ISteamAppLocator? steam = SteamFor(disk, searchRoots);

        // Nothing to compile (usage, -listcompliance): no library needed.
        bool compiles = args.Count > 0
            && !args.Any(a => string.Equals(a, "-listcompliance", StringComparison.OrdinalIgnoreCase));

        if (options.Cooker == CollisionCookerKind.None || !compiles)
        {
            if (compiles)
            {
                await output.WriteLineAsync("-cooker none: no collision lumps will be written.").ConfigureAwait(false);
            }

            return await VbspCommand.RunAsync(disk, args, null, steam, output, cancellationToken).ConfigureAwait(false);
        }

        CookerSetup setup = await OpenCookerAsync(
            disk, searchRoots, options, ["vbsp", .. args], "ssmap vbsp", output, cancellationToken)
            .ConfigureAwait(false);
        if (setup.Exit is int exit)
        {
            return exit;
        }

        await using ICollisionCooker? cooker = setup.Cooker;

        return await VbspCommand.RunAsync(disk, args, cooker, steam, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What <see cref="OpenCookerAsync"/> decided: a process exit code (the
    /// compile ran in a relaunched child, or relaunching cannot help), or a
    /// cooker, or neither (<c>-cooker none</c>, or no library loads: stock's
    /// warning, no collision lumps).
    /// </summary>
    /// <param name="Exit">The exit code to return without compiling here, or null to compile.</param>
    /// <param name="Cooker">The cooker, owned (and disposed) by the caller, or null for none.</param>
    public sealed record CookerSetup(int? Exit, ICollisionCooker? Cooker);

    /// <summary>
    /// Makes the cooker <see cref="VbspOptions.Cooker"/> asks for. Shared by
    /// <c>ssmap vbsp</c> and <c>ssmap all</c>.
    /// </summary>
    /// <param name="disk">
    /// The host file system. Only <c>native</c> reads it, and it needs the
    /// real disk (<see cref="PhysicalFileSystem"/>) to load a library; any
    /// other file system gives stock's "Can't build collision data" road.
    /// </param>
    /// <param name="searchRoots">Where game installs are.</param>
    /// <param name="options">vbsp's options: the cooker kind, the <c>-vphysics</c> selector, the compliance.</param>
    /// <param name="relaunchArgs">The whole command line, command included, for a relaunched child.</param>
    /// <param name="label">The command's name, for messages.</param>
    /// <param name="output">Where messages go.</param>
    /// <param name="cancellationToken">Cancels the discovery or the child.</param>
    /// <returns>The decision.</returns>
    /// <remarks>
    /// <c>none</c> makes nothing; <c>managed</c> makes a
    /// <see cref="ManagedCollisionCooker"/>, which loads no library and so
    /// touches no file and never relaunches (Phase 5: discovery and the
    /// relaunch are about 0.45 s of a small map's compile); <c>native</c>
    /// finds <c>vphysics.so</c> and, when this process was not started with
    /// <c>LD_LIBRARY_PATH</c> naming its directory, relaunches with
    /// <paramref name="relaunchArgs"/> (spike 0b).
    /// </remarks>
    public static async Task<CookerSetup> OpenCookerAsync(
        IFileSystem disk,
        IReadOnlyList<VPath> searchRoots,
        VbspOptions options,
        IReadOnlyList<string> relaunchArgs,
        string label,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Cooker == CollisionCookerKind.None)
        {
            await output.WriteLineAsync("-cooker none: no collision lumps will be written.").ConfigureAwait(false);
            return new CookerSetup(null, null);
        }

        if (options.Cooker == CollisionCookerKind.Managed)
        {
            // No library, no relaunch: the managed cooker is plain managed code.
            ManagedCollisionCooker managed = ManagedCollisionCooker.Create(options.Compliance);
            await output.WriteLineAsync($"collision: managed ({managed.CookerIdentity})").ConfigureAwait(false);
            return new CookerSetup(null, managed);
        }

        // A relaunched child takes the library its parent chose a moment ago
        // instead of walking every Steam library again (Phase 5: the walk is
        // about 0.19 s, and it ran in both processes).
        VPhysicsLibrary? chosen = Environment.GetEnvironmentVariable(PhysCommand.RelaunchedVariable) == "1"
            ? await HandedOverAsync(disk, Environment.GetEnvironmentVariable(ChosenLibraryVariable), cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (chosen is null)
        {
            IReadOnlyList<VPhysicsLibrary> found =
                await VPhysicsLocator.DiscoverAsync(disk, searchRoots, cancellationToken).ConfigureAwait(false);

            if (!TrySelectLibrary(found, options.VPhysicsLibrary, out chosen, out string? problem))
            {
                // Stock's road when vphysics does not load (ivp.cpp:1510).
                await output.WriteLineAsync($"!!! WARNING: Can't build collision data! ({problem})").ConfigureAwait(false);
                return new CookerSetup(null, null);
            }
        }

        if (disk is not PhysicalFileSystem host)
        {
            await output.WriteLineAsync(
                "!!! WARNING: Can't build collision data! (-cooker native loads its library from the host disk)")
                .ConfigureAwait(false);
            return new CookerSetup(null, null);
        }

        string libraryFile = host.ToHostPath(chosen!.Path);
        string directory = libraryFile[..libraryFile.LastIndexOf('/')];
        string? relaunch = PhysCommand.RelaunchLibraryPath(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"), directory);

        if (relaunch is not null)
        {
            if (Environment.GetEnvironmentVariable(PhysCommand.RelaunchedVariable) == "1")
            {
                await output.WriteLineAsync(
                    $"{label}: relaunched with LD_LIBRARY_PATH naming {directory} and it still does not.")
                    .ConfigureAwait(false);
                return new CookerSetup(Program.ExitFailure, null);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            // The child inherits this process's environment.
            Environment.SetEnvironmentVariable(ChosenLibraryVariable, FormatHandOver(chosen));
            return new CookerSetup(
                await PhysCommand.RelaunchAsync(relaunch, relaunchArgs, cancellationToken).ConfigureAwait(false), null);
        }

        VPhysicsCollisionCooker cooker =
            await VPhysicsCollisionCooker.CreateAsync(host, chosen.Path, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"collision: {chosen.Key} ({cooker.CookerIdentity})").ConfigureAwait(false);
        return new CookerSetup(null, cooker);
    }

    /// <summary>
    /// The environment variable a relaunching parent names its chosen library
    /// in, so the child does not discover it again.
    /// </summary>
    public const string ChosenLibraryVariable = "SSMAP_VPHYSICS_CHOSEN";

    /// <summary>The hand-over text for a chosen library: its game, a tab, its path.</summary>
    /// <param name="chosen">The library.</param>
    /// <returns>The text.</returns>
    public static string FormatHandOver(VPhysicsLibrary chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        return chosen.Game + "\t" + chosen.Path.Value;
    }

    /// <summary>Reads <see cref="FormatHandOver"/>'s text back.</summary>
    /// <param name="value">The text, or null.</param>
    /// <param name="game">The library's game directory name.</param>
    /// <param name="path">The library's path.</param>
    /// <returns>Whether the text named a game and a path.</returns>
    public static bool TryParseHandOver(string? value, out string game, out VPath path)
    {
        game = string.Empty;
        path = default;
        int tab = value?.IndexOf('\t', StringComparison.Ordinal) ?? -1;
        if (tab <= 0 || tab == value!.Length - 1)
        {
            return false;
        }

        game = value[..tab];
        return VPath.TryCreate(value[(tab + 1)..], out path);
    }

    // The parent's choice, re-read from disk (its identity decides whether it
    // loads here); null sends the child back to discovery.
    private static async Task<VPhysicsLibrary?> HandedOverAsync(
        IFileSystem disk, string? handOver, CancellationToken cancellationToken)
    {
        if (!TryParseHandOver(handOver, out string game, out VPath path))
        {
            return null;
        }

        FileInfoSnapshot? info = await disk.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        ElfIdentity identity;
        await using (Stream stream = await disk.OpenReadAsync(path, cancellationToken).ConfigureAwait(false))
        {
            identity = await ElfIdentityReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        VPhysicsLibrary library = new(game, path, identity, info.Value.Length);
        return library.IsUsable ? library : null;
    }

    /// <summary>
    /// The Steam install a gameinfo's <c>|appid_N|</c> lines resolve against:
    /// the first search root's, read only if a gameinfo asks.
    /// </summary>
    /// <param name="disk">The disk the roots are on.</param>
    /// <param name="searchRoots">The <c>steamapps/common</c> directories, most likely first.</param>
    /// <returns>The locator, or null when there is no root.</returns>
    internal static ISteamAppLocator? SteamFor(IFileSystem disk, IReadOnlyList<VPath> searchRoots) =>
        searchRoots.Count == 0 ? null : SteamLibraryFolders.Deferred(disk, searchRoots[0].Directory.Directory);
}
