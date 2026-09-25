using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// Finds where a Steam app is installed: what <c>|appid_N|</c> in a
/// <c>gameinfo.txt</c> search path stands for.
/// </summary>
/// <remarks>
/// The engine asks the Steam client (<c>SteamApps()-&gt;GetAppInstallDir</c>,
/// <c>public/filesystem_init.cpp:721</c>). A compile has no Steam client, so
/// the implementation reads what the client itself writes
/// (<see cref="SteamLibraryFolders"/>); a test gives an in-memory one.
/// </remarks>
public interface ISteamAppLocator
{
    /// <summary>The app's install directory, or null when it is not installed.</summary>
    /// <param name="appId">The Steam application id.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The directory, or null.</returns>
    ValueTask<VPath?> FindInstallDirectoryAsync(int appId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Steam libraries of one Steam install, read from its
/// <c>steamapps/libraryfolders.vdf</c>, and the apps installed in them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where an app lives.</b> <c>libraryfolders.vdf</c> lists the libraries,
/// each with a <c>path</c> and an <c>apps</c> block naming the app ids
/// installed there. The app's own <c>steamapps/appmanifest_&lt;id&gt;.acf</c>
/// in that library holds <c>AppState/installdir</c>, and the install is
/// <c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;</c>. That is the same
/// directory the client's <c>GetAppInstallDir</c> answers.
/// </para>
/// <para>
/// The older file format (before 2021) writes each extra library as a plain
/// value, <c>"1" "D:\\SteamLibrary"</c>, with no <c>apps</c> block, and
/// never lists the Steam install itself. Both are read: a library with no
/// <c>apps</c> block is asked for the manifest directly, and the Steam
/// install is always a library, first.
/// </para>
/// <para>
/// Every read goes through <see cref="IFileSystem"/>, so paths in the file
/// are taken as paths in that file system: a Windows drive letter is
/// dropped and backslashes become slashes, the same treatment
/// <see cref="GameInfo.ExpandTokens"/> gives a <c>gameinfo.txt</c>.
/// </para>
/// </remarks>
public sealed class SteamLibraryFolders : ISteamAppLocator
{
    /// <summary>Where the file lives under a Steam install.</summary>
    public const string RelativePath = "steamapps/libraryfolders.vdf";

    private readonly IFileSystem _fileSystem;

    private SteamLibraryFolders(IFileSystem fileSystem, IReadOnlyList<SteamLibraryFolder> libraries)
    {
        _fileSystem = fileSystem;
        Libraries = libraries;
    }

    /// <summary>The libraries, the Steam install first, then in file order.</summary>
    public IReadOnlyList<SteamLibraryFolder> Libraries { get; }

    /// <summary>Reads a Steam install's library list.</summary>
    /// <param name="fileSystem">Where the Steam install is.</param>
    /// <param name="steamRoot">The Steam install: the directory holding <c>steamapps/</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The libraries. A Steam install with no <c>libraryfolders.vdf</c> has one: itself.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> is null.</exception>
    public static async ValueTask<SteamLibraryFolders> LoadAsync(
        IFileSystem fileSystem,
        VPath steamRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        List<SteamLibraryFolder> libraries = [new SteamLibraryFolder(steamRoot, null)];
        VPath file = steamRoot.Combine(RelativePath);

        if (!await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
        {
            return new SteamLibraryFolders(fileSystem, libraries);
        }

        KeyValuesDocument document;
        using (System.Buffers.IMemoryOwner<byte> owner = await fileSystem
            .ReadAllAsync(file, cancellationToken).ConfigureAwait(false))
        {
            document = await KeyValuesDocument.ParseAsync(owner.Memory, null, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (KeyValuesNode entry in document.Root?.Children ?? [])
        {
            if (!int.TryParse(entry.Name, out _))
            {
                // "contentstatsid" and the like, beside the numbered entries.
                continue;
            }

            string? path = entry.IsSection ? entry.GetString("path") : entry.Value;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            HashSet<int>? apps = null;
            if (entry.Find("apps") is { } block)
            {
                apps = [];
                foreach (KeyValuesNode app in block.Children)
                {
                    if (int.TryParse(app.Name, out int id))
                    {
                        apps.Add(id);
                    }
                }
            }

            VPath library = VPath.Create(ToFileSystemPath(path));
            int existing = libraries.FindIndex(l => l.Path == library);

            if (existing >= 0)
            {
                // The Steam install listing itself, with its apps.
                libraries[existing] = new SteamLibraryFolder(library, apps);
            }
            else
            {
                libraries.Add(new SteamLibraryFolder(library, apps));
            }
        }

        return new SteamLibraryFolders(fileSystem, libraries);
    }

    /// <summary>
    /// A locator that reads the library list only when a gameinfo first asks
    /// for an app, so a host can always pass one and a gameinfo with no
    /// <c>|appid_N|</c> line never touches Steam's files.
    /// </summary>
    /// <param name="fileSystem">Where the Steam install is.</param>
    /// <param name="steamRoot">The Steam install.</param>
    /// <returns>The locator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> is null.</exception>
    public static ISteamAppLocator Deferred(IFileSystem fileSystem, VPath steamRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new DeferredLocator(fileSystem, steamRoot);
    }

    /// <inheritdoc/>
    public async ValueTask<VPath?> FindInstallDirectoryAsync(int appId, CancellationToken cancellationToken = default)
    {
        foreach (SteamLibraryFolder library in Libraries)
        {
            if (library.Apps is { } apps && !apps.Contains(appId))
            {
                continue;
            }

            VPath manifest = library.Path.Combine($"steamapps/appmanifest_{appId}.acf");
            if (!await _fileSystem.ExistsAsync(manifest, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            KeyValuesDocument document;
            using (System.Buffers.IMemoryOwner<byte> owner = await _fileSystem
                .ReadAllAsync(manifest, cancellationToken).ConfigureAwait(false))
            {
                document = await KeyValuesDocument.ParseAsync(owner.Memory, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            string? installDir = document.Root?.GetString("installdir");
            if (string.IsNullOrEmpty(installDir))
            {
                continue;
            }

            return library.Path.Combine("steamapps/common/" + ToFileSystemPath(installDir));
        }

        return null;
    }

    private sealed class DeferredLocator(IFileSystem fileSystem, VPath steamRoot) : ISteamAppLocator
    {
        private SteamLibraryFolders? _loaded;

        public async ValueTask<VPath?> FindInstallDirectoryAsync(int appId, CancellationToken cancellationToken = default)
        {
            _loaded ??= await LoadAsync(fileSystem, steamRoot, cancellationToken).ConfigureAwait(false);
            return await _loaded.FindInstallDirectoryAsync(appId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ToFileSystemPath(string path)
    {
        string slashed = path.Replace("\\\\", "/", StringComparison.Ordinal).Replace('\\', '/');
        return slashed.Length >= 2 && slashed[1] == ':' && char.IsAsciiLetter(slashed[0]) ? slashed[2..] : slashed;
    }
}

/// <summary>One Steam library.</summary>
/// <param name="Path">The library: the directory holding its <c>steamapps/</c>.</param>
/// <param name="Apps">
/// The app ids the file says are installed there, or null when the file does
/// not say (the older format, or the Steam install when it does not list itself).
/// </param>
public readonly record struct SteamLibraryFolder(VPath Path, IReadOnlySet<int>? Apps);
