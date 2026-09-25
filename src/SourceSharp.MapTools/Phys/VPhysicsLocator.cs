using System.Collections.Immutable;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// One <c>vphysics</c> library found on this machine.
/// </summary>
/// <param name="Game">
/// The game it belongs to, taken from the install directory's name, for example
/// <c>Team Fortress 2</c> or <c>Source SDK Base 2013 Multiplayer</c>.
/// </param>
/// <param name="Path">Where it is.</param>
/// <param name="Identity">Its architecture and build id.</param>
/// <param name="Length">Its size in bytes.</param>
public sealed record VPhysicsLibrary(
    string Game,
    VPath Path,
    ElfIdentity Identity,
    long Length)
{
    /// <summary>
    /// Whether this library can actually be loaded by this process.
    /// </summary>
    /// <remarks>
    /// A game's <c>bin/</c> directory usually holds a 32-bit
    /// <c>vphysics.so</c> beside the 64-bit one in <c>bin/linux64/</c>. Both
    /// are reported by discovery, because "the one you were about to use is the
    /// 32-bit one" is a far more useful message than an empty list.
    /// </remarks>
    public bool IsUsable => Identity.IsLoadableHere;

    /// <summary>
    /// A short stable name a user can select this library by.
    /// </summary>
    /// <remarks>
    /// The game name lower-cased with spaces collapsed to dashes, so
    /// <c>Team Fortress 2</c> selects as <c>team-fortress-2</c>. Matching is
    /// case-insensitive and also accepts a unique prefix.
    /// </remarks>
    public string Key => string.Join('-', Game.ToLowerInvariant()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>
/// Finds the <c>vphysics</c> libraries installed on this machine, so one can be
/// chosen deliberately.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a measurement, not for tidiness. Spike 0b cooked the
/// same 32-unit cube through two different builds of <c>vphysics.so</c> and got
/// different bytes: <c>mass_center</c> 0 against 1.6e-11, and
/// <c>rotation_inertia</c> off by one ULP. So the physics lump of a compiled
/// map depends on WHICH GAME'S library happened to be installed, and a compile
/// that does not record its choice is not reproducible.
/// </para>
/// <para>
/// Discovery takes its search roots as a parameter and reads nothing from the
/// environment. Where Steam keeps its libraries is a host's knowledge; a
/// library that went looking for <c>$HOME</c> on its own would be untestable
/// and would behave differently inside a service.
/// </para>
/// </remarks>
public static class VPhysicsLocator
{
    /// <summary>
    /// The places a Source game keeps its physics library, relative to the
    /// game's install directory.
    /// </summary>
    /// <remarks>
    /// <c>bin/linux64</c> first, because that is the one a 64-bit host can
    /// load. <c>bin</c> is searched too so that a 32-bit-only install is
    /// REPORTED as unusable rather than silently missing.
    /// </remarks>
    public static ImmutableArray<string> RelativeLocations { get; } =
        ["bin/linux64/vphysics.so", "bin/vphysics.so"];

    /// <summary>
    /// Finds every <c>vphysics</c> library beneath the given roots.
    /// </summary>
    /// <param name="fileSystem">The filesystem to look through.</param>
    /// <param name="searchRoots">
    /// Directories each of whose immediate children is a game install, for
    /// example a Steam <c>steamapps/common</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// What was found, ordered by game name, then usable before unusable. Never
    /// null; an empty result means nothing was found, which is not an error.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<ImmutableArray<VPhysicsLibrary>> DiscoverAsync(
        IFileSystem fileSystem,
        IEnumerable<VPath> searchRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(searchRoots);

        List<VPhysicsLibrary> found = [];
        HashSet<string> seen = [];

        foreach (VPath root in searchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (VPath candidate in EnumerateGameDirectoriesAsync(
                fileSystem, root, cancellationToken).ConfigureAwait(false))
            {
                string game = candidate.FileName;

                foreach (string relative in RelativeLocations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    VPath path = candidate.Combine(relative);
                    if (!seen.Add(path.Value))
                    {
                        continue;
                    }

                    FileInfoSnapshot? info =
                        await fileSystem.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);

                    if (info is null)
                    {
                        continue;
                    }

                    ElfIdentity identity;
                    await using (Stream stream =
                        await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        identity = await ElfIdentityReader.ReadAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    found.Add(new VPhysicsLibrary(game, path, identity, info.Value.Length));
                }
            }
        }

        return
        [
            .. found
                .OrderBy(l => l.Game, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.IsUsable ? 0 : 1)
                .ThenBy(l => l.Path.Value, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Picks the library a user asked for out of what discovery found.
    /// </summary>
    /// <param name="libraries">What discovery found.</param>
    /// <param name="selector">
    /// A game <see cref="VPhysicsLibrary.Key"/>, a unique prefix of one, or an
    /// exact path. Null selects the default.
    /// </param>
    /// <param name="chosen">The selected library, when exactly one matched.</param>
    /// <param name="problem">Why nothing was selected, when nothing was.</param>
    /// <returns>True when exactly one usable library matched.</returns>
    /// <remarks>
    /// An ambiguous selector is refused rather than resolved by taking the
    /// first match. Picking one of two physics libraries silently is precisely
    /// the failure this type exists to prevent -- the compile would succeed and
    /// the lump would differ.
    /// </remarks>
    public static bool TrySelect(
        IReadOnlyList<VPhysicsLibrary> libraries,
        string? selector,
        out VPhysicsLibrary? chosen,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        chosen = null;
        problem = null;

        List<VPhysicsLibrary> usable = [.. libraries.Where(l => l.IsUsable)];

        if (usable.Count == 0)
        {
            problem = libraries.Count == 0
                ? "no vphysics library was found. Pass a search root that contains a Source game "
                  + "install, or name a library explicitly."
                : $"{libraries.Count} vphysics librar(y/ies) were found but none is 64-bit, so none "
                  + "can be loaded by this process. A game's bin/ holds the 32-bit build; the "
                  + "loadable one is in bin/linux64/.";
            return false;
        }

        if (selector is null)
        {
            // No default is invented when there is a real choice to make,
            // because the choice changes the output bytes.
            if (usable.Count > 1)
            {
                problem = "several usable vphysics libraries were found and they do NOT agree: two "
                    + "builds cook the same shape to different bytes. Choose one explicitly. "
                    + $"Candidates: {string.Join(", ", usable.Select(l => l.Key))}";
                return false;
            }

            chosen = usable[0];
            return true;
        }

        List<VPhysicsLibrary> exact =
        [
            .. usable.Where(l =>
                l.Key.Equals(selector, StringComparison.OrdinalIgnoreCase)
                || l.Path.Value.Equals(selector, StringComparison.Ordinal)),
        ];

        if (exact.Count == 1)
        {
            chosen = exact[0];
            return true;
        }

        List<VPhysicsLibrary> prefixed =
        [
            .. usable.Where(l => l.Key.StartsWith(selector, StringComparison.OrdinalIgnoreCase)),
        ];

        if (prefixed.Count == 1)
        {
            chosen = prefixed[0];
            return true;
        }

        problem = prefixed.Count == 0
            ? $"no usable vphysics library matches \"{selector}\". "
              + $"Candidates: {string.Join(", ", usable.Select(l => l.Key))}"
            : $"\"{selector}\" is ambiguous between {string.Join(", ", prefixed.Select(l => l.Key))}. "
              + "Refusing to guess: the choice changes the bytes of the physics lump.";

        return false;
    }

    private static async IAsyncEnumerable<VPath> EnumerateGameDirectoriesAsync(
        IFileSystem fileSystem,
        VPath root,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        // Enumeration is over FILES, because that is what IFileSystem exposes:
        // a directory is not a thing that exists in a pak or a VPK, so the seam
        // has no concept of one. The game directory is recovered from each
        // candidate's path instead.
        foreach (string relative in RelativeLocations)
        {
            HashSet<string> games = [];

            await foreach (VPath file in fileSystem
                .EnumerateAsync(root, "vphysics.so", recursive: true, cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string suffix = "/" + relative;
                if (!file.Value.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string gameDirectory = file.Value[..^suffix.Length];
                if (games.Add(gameDirectory))
                {
                    yield return VPath.Create(gameDirectory);
                }
            }
        }
    }
}
