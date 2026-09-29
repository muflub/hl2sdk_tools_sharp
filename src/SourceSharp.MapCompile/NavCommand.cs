//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap nav &lt;map.nav3d | level.yaml&gt; [-rooms &lt;pack&gt;] [--obj &lt;out.obj&gt;] [--floor] [--agent N]</c>:
/// prints a level navigation's numbers and optionally exports it as OBJ.
/// </summary>
/// <remarks>
/// Thin on purpose: reading, stitching and reporting are the library's
/// (<see cref="Nav3dReader"/>, <see cref="LevelNavFromPack"/>,
/// <see cref="NavInspector"/>). Given a level file, the navigation is
/// stitched from the room pack in memory exactly as <c>ssmap link</c> would
/// write it, and nothing is written but the OBJ.
/// </remarks>
public static class NavCommand
{
    private const string Usage =
        "usage: ssmap nav <map.nav3d | level.yaml> [-rooms <pack.roompack>] [--obj <out.obj>] [--floor] [--agent <index|name>]";

    /// <summary>Runs <c>ssmap nav</c>.</summary>
    /// <param name="disk">Where the files are.</param>
    /// <param name="args">The arguments after <c>nav</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IFileSystem disk, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? target = null;
        string? obj = null;
        string? rooms = null;
        string? agentText = null;
        bool floor = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Count;
            if (Is(arg, "obj") && hasValue)
            {
                obj = args[++i];
            }
            else if (Is(arg, "rooms") && hasValue)
            {
                rooms = args[++i];
            }
            else if (Is(arg, "agent") && hasValue)
            {
                agentText = args[++i];
            }
            else if (Is(arg, "floor"))
            {
                floor = true;
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

        if (target is null || !VPath.TryCreate(Path.GetFullPath(target), out VPath path))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return Program.ExitUsage;
        }

        Nav3dReader nav;
        try
        {
            if (string.Equals(Path.GetExtension(target), Nav3dFormat.Extension, StringComparison.OrdinalIgnoreCase))
            {
                nav = Nav3dReader.Open(await ReadAsync(disk, path, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                (Nav3dReader? stitched, string? problem) = await StitchAsync(disk, path, rooms, cancellationToken).ConfigureAwait(false);
                if (stitched is null)
                {
                    await output.WriteLineAsync($"ssmap nav: {target}: {problem}").ConfigureAwait(false);
                    return RoomCommands.ExitFailed;
                }

                nav = stitched;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LinkException
            or LevelFileException or ArgumentException)
        {
            await output.WriteLineAsync($"ssmap nav: {target}: {exception.Message}").ConfigureAwait(false);
            return RoomCommands.ExitFailed;
        }

        await output.WriteAsync(NavInspector.Describe(nav)).ConfigureAwait(false);
        if (obj is null)
        {
            return Program.ExitSuccess;
        }

        int agent = agentText is null ? -1
            : int.TryParse(agentText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index
            : nav.FindPreset(agentText);
        if ((agentText is not null && (agent < 0 || agent >= nav.PresetCount)) || !VPath.TryCreate(Path.GetFullPath(obj), out VPath objPath))
        {
            await output.WriteLineAsync($"ssmap nav: no agent \"{agentText}\", or --obj \"{obj}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        byte[] text = Encoding.UTF8.GetBytes(NavInspector.Obj(nav, agent, floor ? NavObjMode.Floor : NavObjMode.Boxes));
        await disk.ReplaceAsync(
            objPath, async (stream, token) => await stream.WriteAsync(text, token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync($"ssmap nav: wrote {HostPaths.Display(objPath)}").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>A level file's navigation, stitched from its room pack as <c>ssmap link</c> would.</summary>
    private static async Task<(Nav3dReader? Nav, string? Problem)> StitchAsync(
        IFileSystem disk, VPath levelPath, string? roomsPack, CancellationToken cancellationToken)
    {
        byte[] levelBytes = await ReadAsync(disk, levelPath, cancellationToken).ConfigureAwait(false);
        string levelFile = levelPath.ToString();
        LevelGrid level = LevelYaml.Parse(
            await new StreamReader(new MemoryStream(levelBytes), Encoding.UTF8).ReadToEndAsync(cancellationToken).ConfigureAwait(false),
            Path.GetFileNameWithoutExtension(levelFile));
        string library = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(levelFile)!, level.Library));
        VPath pack = VPath.Create(Path.GetFullPath(roomsPack ?? Path.ChangeExtension(library, RoomPack.Extension)));
        await using Stream stream = await disk.OpenReadAsync(pack, cancellationToken).ConfigureAwait(false);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
        Dictionary<string, HashSet<int>> turns = new(StringComparer.Ordinal);
        foreach ((_, _, LevelCell cell) in level.Placed)
        {
            if (!turns.TryGetValue(cell.Room, out HashSet<int>? placed))
            {
                turns[cell.Room] = placed = [];
            }

            placed.Add(cell.Rotation);
        }

        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            stream, index, [.. turns.Select(t => new RoomPackRequest(t.Key, t.Value) { Navigation = true })], cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, RoomObject> byName = loaded.ToDictionary(r => r.Definition.Name, StringComparer.Ordinal);
        RoomDefinition first = loaded[0].Definition;
        LevelLayout layout = level.ToLayout(name => byName.GetValueOrDefault(name)?.Definition, first.CellSize, first.Kit);
        Guid? packId = await RoomNavPack.ReadPackIdAsync(stream, index, cancellationToken).ConfigureAwait(false);
        LevelNavPlan plan = LevelNavFromPack.Plan(
            layout, level.Columns, level.Rows, name => byName[name], packId, levelBytes,
            LevelNavFromPack.IdOptions(true, LevelNavFromPack.DefaultCompression), includeNavigation: true);
        if (!plan.WritesNavigation)
        {
            return (null, plan.Warning);
        }

        Nav3dLevel stitched = await plan.BuildAsync(cancellationToken).ConfigureAwait(false);
        return (Nav3dReader.Open(Nav3dWriter.Write(stitched, LevelNavFromPack.DefaultCompression)), null);
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
