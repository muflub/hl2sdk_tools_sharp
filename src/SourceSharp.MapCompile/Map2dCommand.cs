//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap map2d &lt;map.bsp&gt; [-level &lt;level.yaml&gt;] [-out &lt;file.map2d&gt;] [-svg]</c>:
/// the level map overlay of any compiled map (the rooms design, 18.3).
/// </summary>
/// <remarks>
/// <para>
/// The same face rule over the map's world model and its player-solid brush
/// entities as <c>ssmap room</c> applies to each room
/// (<see cref="LevelMapBuilder"/>). With a level file the faces are cut into
/// the level's cells, each placement's unioned in its room's own frame, and
/// the doors and markers read from the level's libraries, so the flattened
/// level's compile gives the file <c>ssmap link</c> writes for the level,
/// but for the checksum that binds each to its own <c>.bsp</c>. Without one
/// a hand-built map gets its floors, its <c>info_player_start</c>s as spawn
/// markers and any entity with a <c>map_marker</c>.
/// </para>
/// <para>
/// The file goes beside the map (<c>&lt;map&gt;.map2d</c>) unless
/// <c>-out</c> names it; <c>-svg</c> also writes the preview beside it.
/// </para>
/// </remarks>
public static class Map2dCommand
{
    private const string Usage = "usage: ssmap map2d <map.bsp> [-level <level.yaml>] [-out <file.map2d>] [-svg]";

    /// <summary>Runs <c>ssmap map2d</c>.</summary>
    /// <param name="disk">Where the map, the level, its libraries and the outputs live.</param>
    /// <param name="args">The arguments after <c>map2d</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the reads and writes.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem disk, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? target = null;
        string? levelText = null;
        string? outText = null;
        bool svg = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Count;
            if (Is(arg, "level") && hasValue && levelText is null)
            {
                levelText = args[++i];
            }
            else if (Is(arg, "out") && hasValue && outText is null)
            {
                outText = args[++i];
            }
            else if (Is(arg, "svg"))
            {
                svg = true;
            }
            else if (target is null && !arg.StartsWith('-'))
            {
                target = arg;
            }
            else
            {
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return Program.ExitUsage;
            }
        }

        if (target is null
            || !VPath.TryCreate(Path.GetFullPath(target), out VPath mapPath)
            || (outText is not null && !VPath.TryCreate(Path.GetFullPath(outText), out _))
            || (levelText is not null && !VPath.TryCreate(Path.GetFullPath(levelText), out _)))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string display = HostPaths.Display(mapPath);
        Map2dLevel map;
        try
        {
            byte[] bytes = await ReadAsync(disk, mapPath, cancellationToken).ConfigureAwait(false);
            uint checksum = BspMapChecksum.Compute(bytes);
            BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false), cancellationToken).ConfigureAwait(false);
            if (levelText is null)
            {
                map = LevelMapBuilder.FromCompile(bsp, checksum);
            }
            else
            {
                string levelPath = Path.GetFullPath(levelText);
                try
                {
                    (LevelGrid level, IReadOnlyList<VmfDocument> libraries) = await ReadLevelAsync(disk, levelPath, cancellationToken).ConfigureAwait(false);
                    map = LevelMapBuilder.FromCompile(bsp, checksum, level, libraries);
                }
                catch (Exception exception) when (exception is LevelFileException or RoomLibraryException or RoomLintException or LinkException
                    or ArgumentException or ChunkFileException or IOException or UnauthorizedAccessException)
                {
                    await output.WriteLineAsync($"ssmap map2d: {levelPath}: {exception.Message}").ConfigureAwait(false);
                    return RoomCommands.ExitFailed;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidBspException)
        {
            await output.WriteLineAsync($"ssmap map2d: {display}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        VPath written = outText is null ? mapPath : VPath.Create(Path.GetFullPath(outText));
        try
        {
            await RoomCommands.WriteLevelMapAsync(disk, written, map, svg, "ssmap map2d", output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"ssmap map2d: cannot write {HostPaths.Display(RoomCommands.Map2dPathOf(written))}: {exception.Message}")
                .ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        return Program.ExitSuccess;
    }

    /// <summary>The level file and its libraries, each resolved against the level file's folder.</summary>
    private static async Task<(LevelGrid Level, IReadOnlyList<VmfDocument> Libraries)> ReadLevelAsync(
        IFileSystem disk, string levelPath, CancellationToken cancellationToken)
    {
        byte[] levelBytes = await ReadAsync(disk, VPath.Create(levelPath), cancellationToken).ConfigureAwait(false);
        LevelGrid level = LevelYaml.Parse(
            await new StreamReader(new MemoryStream(levelBytes), Encoding.UTF8).ReadToEndAsync(cancellationToken).ConfigureAwait(false),
            Path.GetFileNameWithoutExtension(levelPath));
        string folder = Path.GetDirectoryName(levelPath)!;
        IEnumerable<string> paths = level.Libraries is { } several ? several.Select(l => l.Path) : [level.Library];
        List<VmfDocument> libraries = [];
        foreach (string library in paths)
        {
            await using Stream stream = await disk.OpenReadAsync(VPath.Create(Path.GetFullPath(Path.Combine(folder, library))), cancellationToken)
                .ConfigureAwait(false);
            libraries.Add(await VmfDocument.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return (level, libraries);
    }

    private static async Task<byte[]> ReadAsync(IFileSystem disk, VPath path, CancellationToken cancellationToken)
    {
        await using Stream stream = await disk.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes.ToArray();
    }

    private static bool Is(string arg, string name) =>
        string.Equals(arg, "-" + name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);
}
