//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap layout</c> over several libraries (the rooms design, 17.9): the
/// <c>key=path</c> operands and the entity budget over every library.
/// </summary>
public static partial class RoomCommands
{
    /// <summary>One library the layout draws from: its key, the pack its counts come from, and its rooms in library order.</summary>
    /// <param name="Key">The key: the level's, or a library VMF's stem for a <c>library:</c> level.</param>
    /// <param name="Pack">Where its pack is looked for.</param>
    /// <param name="PlainRefused">Whether a plain pack is refused there (one <c>-rooms</c> for every key of several).</param>
    /// <param name="Rooms">Its rooms, as the split gives them.</param>
    private sealed record LayoutSource(string Key, VPath Pack, bool PlainRefused, IReadOnlyList<LibraryRoom> Rooms);

    /// <summary>
    /// An operand's key and path when it is written <c>key=path</c> with a
    /// valid key, or null: a path holding <c>=</c> after something that is
    /// not a key stays a path, as <c>ssmap roompack</c> reads it.
    /// </summary>
    private static (string Key, string Path)? KeyedOperand(string operand)
    {
        int equals = operand.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 && LevelLibraries.KeyProblem(operand[..equals]) is null ? (operand[..equals], operand[(equals + 1)..]) : null;
    }

    /// <summary>An operand's key and path: <c>key=path</c>, or a bare path keyed by its file's stem.</summary>
    private static (string Key, string Path) LibraryOperand(string operand) =>
        KeyedOperand(operand) ?? (Path.GetFileNameWithoutExtension(operand), operand);

    /// <summary>
    /// The libraries of a layout of several: each operand's key and path as
    /// the level will write it (relative to <paramref name="from"/>, the
    /// folder the level is written to), in operand order, and each full
    /// path; or null, with the refusal printed, for a stem that is not a
    /// key, a key given twice (ignoring case, since keys become namespaces)
    /// or two keys naming one file.
    /// </summary>
    private static async Task<(LevelLibrary[]? Keys, string[] Paths, int Exit)> LayoutLibrariesAsync(
        IReadOnlyList<string> operands, string from, TextWriter output)
    {
        List<LevelLibrary> keys = [];
        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string operand in operands)
        {
            (string key, string path) = LibraryOperand(operand);
            if (KeyedOperand(operand) is null && LevelLibraries.KeyProblem(key) is not null)
            {
                await output.WriteLineAsync($"ssmap layout: {operand} gives no key ({key} is not a key); write key={operand}.").ConfigureAwait(false);
                return (null, [], Program.ExitUsage);
            }

            if (!seen.Add(key))
            {
                await output.WriteLineAsync($"ssmap layout: the key {key} is given twice.").ConfigureAwait(false);
                return (null, [], Program.ExitUsage);
            }

            string full = Path.GetFullPath(path);
            if (files.TryGetValue(full, out string? first))
            {
                await output.WriteLineAsync($"ssmap layout: libraries {first} and {key} name the same file, {full}.").ConfigureAwait(false);
                return (null, [], Program.ExitUsage);
            }

            files[full] = key;
            keys.Add(new LevelLibrary(key, Path.GetRelativePath(from, full).Replace('\\', '/')));
            paths.Add(full);
        }

        return ([.. keys], [.. paths], Program.ExitSuccess);
    }

    /// <summary>
    /// The entity budget <c>ssmap layout</c> generates within: the given one,
    /// else the link's <c>cap − reserve</c> with the level's reserve, from
    /// the rooms' counts in each library's pack, each with what the linker
    /// may write for it in the emission mode asked for (<c>-mod-entities</c>).
    /// </summary>
    /// <param name="disk">Where the packs are.</param>
    /// <param name="sources">Each library, in level order.</param>
    /// <param name="several">Whether the level names its libraries by key, which the messages then name.</param>
    /// <param name="explicitBudget">The budget given, or null for the link's.</param>
    /// <param name="modEntities">Whether the level is linked with <c>-mod-entities</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The budget; or null when none was given and a pack is missing or
    /// lacks a room's counts, since there is then nothing to count with.
    /// </returns>
    /// <exception cref="LinkException">
    /// A budget was given and a pack is missing, or lacks a room's counts;
    /// or a pack cannot be read.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The level's own entities</b> are what the link writes once
    /// whatever rooms the level places: the library entities and options by
    /// the singleton rule over every library (D20 and D29,
    /// <see cref="LevelLibraries.LevelSingletonsOf"/>), and the skybox room
    /// of the library that supplies it. For one library that is its own
    /// entities, options and skybox, as it always was, so a one-library
    /// budget does not move.
    /// </para>
    /// <para>
    /// The door portals option is the level's too, so a room of a library
    /// that does not ask for door portals pays for its share of them when
    /// the level has them from another.
    /// </para>
    /// </remarks>
    private static async Task<LayoutEntityBudget?> LayoutBudgetAsync(
        IFileSystem disk, IReadOnlyList<LayoutSource> sources, bool several, int? explicitBudget, bool modEntities, CancellationToken cancellationToken)
    {
        List<PackCounts> all = [];
        foreach (LayoutSource source in sources)
        {
            string pack = HostPaths.Display(source.Pack);
            PackCounts? counts = await ReadPackCountsAsync(disk, source.Pack, source.Key, source.PlainRefused, cancellationToken).ConfigureAwait(false);
            if (counts is null)
            {
                return explicitBudget is null
                    ? null
                    : throw new LinkException(several
                        ? $"-entity-budget counts the rooms' entities, and there is no room pack {pack} for library {source.Key};"
                            + $" compile the library with ssmap room, or point -rooms {source.Key}= at its pack."
                        : $"-entity-budget counts the rooms' entities, and there is no room pack {pack};"
                            + " compile the library with ssmap room, or point -rooms at its pack.");
            }

            all.Add(counts);
        }

        LevelLibraries.LevelSingletonChoice level = LevelLibraries.LevelSingletonsOf(
            [.. all.Select(c => new LevelLibraries.LibrarySingletons(c.LibraryEntities, c.Options, c.Skybox))]);
        List<int> edicts = new(sources.Sum(s => s.Rooms.Count));
        for (int i = 0; i < sources.Count; i++)
        {
            PackCounts counts = all[i];
            string pack = HostPaths.Display(sources[i].Pack);
            foreach (LibraryRoom room in sources[i].Rooms)
            {
                string name = room.Definition.Name;
                if (counts.Counts.GetValueOrDefault(name) is not { } found)
                {
                    string named = several ? LevelLibraries.Qualified(sources[i].Key, name) : name;
                    return explicitBudget is null
                        ? null
                        : throw new LinkException(
                            $"-entity-budget counts the rooms' entities, and the room pack {pack} has no counts for room \"{named}\";"
                            + " recompile the library with ssmap room.");
                }

                // A room pays for the entities the linker writes for it too (its
                // flags, and without -mod-entities its hub's stock fallback): at
                // most what its names say, so the layout never under-counts.
                int written = counts.Names.GetValueOrDefault(name)?.WrittenEdictsBound(modEntities) ?? 0;
                // And, when the level asks for door portals, its share of its
                // joints' portals: half its sockets, rounded up.
                int doors = level.Options.HasDoorPortals ? LevelDoorPortals.EdictsBound(room.Definition) : 0;
                edicts.Add(found.Tally(EntityClassTable.Default).Edicts + written + TransitionEdictsBound(room, modEntities) + doors);
            }
        }

        int budget = explicitBudget
            ?? EntityClassTable.EdictCap - LevelEntityBudget.ReserveFor(LevelLinkOptions.Default, level.Options);

        // The level's own entities are the level's whatever it places, as
        // the link counts them, and so are its skybox room's, which every
        // level carries once below its grid.
        int skybox = level.SkyboxSource is int from
            && all[from].Skybox is { } sky
            && all[from].Counts.GetValueOrDefault(sky) is { } skyCounts
            ? skyCounts.Tally(EntityClassTable.Default).Edicts
            : 0;
        return new LayoutEntityBudget(budget, edicts)
        {
            LevelEdicts = RoomLibraryEntities.Count(level.Entities).Tally(EntityClassTable.Default).Edicts + skybox,
        };
    }
}
