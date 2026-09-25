using System.Text;

using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// Which game content a stock reference map was compiled against, read from
/// the <c>&lt;n&gt;.gameinfo</c> sidecar beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sidecar.</b> A stock BSP does not record the content stock read to
/// make it: the texdata sizes, the <c>$surfaceprop</c> indices, the cubemap
/// patches, the static and detail props all come from VMTs and models that
/// are not in the file. A gate that compiles the same VMF with this port
/// against DIFFERENT content compares two compiles of two different inputs,
/// and its failures say nothing about the port. That is exactly what
/// happened when p3g's eleven maps (compiled against their own fixture
/// content) joined <c>catmaps-all</c> and every gate mounted
/// <c>tools/mapgame</c> for all of them: 35 red facts, none of them a bug.
/// </para>
/// <para>
/// The sidecar is one line: the path of the <c>gameinfo.txt</c> stock was
/// given as <c>-game</c>, absolute, or relative to the sidecar's directory.
/// <c>~/.cache/maptools/bin/make-catmaps</c> writes it next to every map it
/// compiles.
/// </para>
/// <para>
/// <b>No sidecar is a failure, never a fallback.</b> There is deliberately no
/// default content: a missing or empty sidecar, or one naming a file that is
/// not there, throws <see cref="StockProvenanceException"/>, so a reference
/// whose provenance is unknown can never be compared against whatever content
/// happens to be mounted.
/// </para>
/// </remarks>
internal static class StockProvenance
{
    /// <summary>The sidecar's suffix: <c>&lt;n&gt;.gameinfo</c>.</summary>
    public const string SidecarSuffix = ".gameinfo";

    /// <summary>The sidecar for one map.</summary>
    /// <param name="directory">The reference directory.</param>
    /// <param name="name">The map name, without extension.</param>
    /// <returns>Its path.</returns>
    public static VPath SidecarPath(VPath directory, string name) => directory.Combine(name + SidecarSuffix);

    /// <summary>
    /// The <c>gameinfo.txt</c> a reference map was compiled against.
    /// </summary>
    /// <param name="fileSystem">Where the reference directory is.</param>
    /// <param name="directory">The reference directory.</param>
    /// <param name="name">The map name, without extension.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The path of the gameinfo, which exists.</returns>
    /// <exception cref="StockProvenanceException">
    /// The sidecar is missing, empty, holds more than one line, or names a
    /// file that is not there.
    /// </exception>
    public static async Task<VPath> GameInfoPathAsync(
        IFileSystem fileSystem,
        VPath directory,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(name);

        VPath sidecar = SidecarPath(directory, name);

        if (!await fileSystem.ExistsAsync(sidecar, cancellationToken))
        {
            throw new StockProvenanceException(
                $"/{sidecar} is missing: the reference map {name} does not say which game content "
                + "stock compiled it against, so nothing can be compared with it. Rebuild the "
                + "reference with ~/.cache/maptools/bin/make-catmaps (it writes the sidecar), or "
                + "write the -game directory's gameinfo.txt path into it from the map's vbsp log. "
                + "There is no default content, on purpose.");
        }

        string text;
        using (System.Buffers.IMemoryOwner<byte> owner = await fileSystem.ReadAllAsync(sidecar, cancellationToken))
        {
            text = Encoding.UTF8.GetString(owner.Memory.Span).Trim();
        }

        if (text.Length == 0)
        {
            throw new StockProvenanceException($"/{sidecar} is empty: it must name the gameinfo.txt stock used.");
        }

        if (text.Contains('\n', StringComparison.Ordinal))
        {
            throw new StockProvenanceException(
                $"/{sidecar} holds more than one line: a map is compiled against exactly one gameinfo.txt.");
        }

        VPath gameInfo = text.StartsWith('/')
            ? VPath.Create(text)
            : VPath.Create(directory.Value + "/" + text);

        if (!await fileSystem.ExistsAsync(gameInfo, cancellationToken))
        {
            throw new StockProvenanceException(
                $"/{sidecar} names /{gameInfo}, which is not there: the content {name} was compiled "
                + "against is gone, so the reference cannot be reproduced.");
        }

        return gameInfo;
    }

    /// <summary>
    /// Mounts the content a reference map was compiled against.
    /// </summary>
    /// <param name="fileSystem">Where the reference and the content are.</param>
    /// <param name="directory">The reference directory.</param>
    /// <param name="name">The map name, without extension.</param>
    /// <param name="steam">Resolves <c>|appid_N|</c> search paths; null when none are expected.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mounted content.</returns>
    /// <exception cref="StockProvenanceException">See <see cref="GameInfoPathAsync"/>.</exception>
    /// <remarks>
    /// Un-tokenised relative search paths are rooted at the directory ABOVE
    /// the gameinfo's, which is where an install keeps <c>hl2.exe</c> beside
    /// its mod directories (<c>FileSystem_GetBaseDir</c>).
    /// </remarks>
    public static async Task<GameContentMounter.Result> MountAsync(
        IFileSystem fileSystem,
        VPath directory,
        string name,
        ISteamAppLocator? steam = null,
        CancellationToken cancellationToken = default)
    {
        VPath gameInfoPath = await GameInfoPathAsync(fileSystem, directory, name, cancellationToken);
        GameInfo gameInfo = await GameInfo.LoadAsync(fileSystem, gameInfoPath, cancellationToken);

        return await GameContentMounter.MountAsync(
            fileSystem,
            gameInfo,
            new GameContentRoots(gameInfoPath.Directory, gameInfoPath.Directory.Directory) { Steam = steam },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// <see cref="MountAsync(IFileSystem, VPath, string, ISteamAppLocator?, CancellationToken)"/>
    /// over the host disk, read-only.
    /// </summary>
    /// <param name="hostDirectory">The reference directory, as a host path.</param>
    /// <param name="name">The map name, without extension.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mounted content.</returns>
    /// <exception cref="StockProvenanceException">See <see cref="GameInfoPathAsync"/>.</exception>
    public static Task<GameContentMounter.Result> MountHostAsync(
        string hostDirectory,
        string name,
        CancellationToken cancellationToken = default)
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        return MountAsync(new ReadOnlyFileSystem(host), host.ToVirtualPath(hostDirectory), name, null, cancellationToken);
    }
}

/// <summary>A stock reference whose content provenance is missing or broken.</summary>
internal sealed class StockProvenanceException : Exception
{
    /// <summary>Creates one.</summary>
    /// <param name="message">What is wrong, and how to fix it.</param>
    public StockProvenanceException(string message)
        : base(message)
    {
    }
}
