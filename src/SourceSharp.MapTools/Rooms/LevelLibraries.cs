//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A level of several libraries, combined for the link: every placed room
/// under its qualified name in one <see cref="RoomLibrary"/> that carries
/// the level's singletons (the first library's, its gaps filled from the
/// later ones), and what combining them warned of.
/// </summary>
/// <param name="Rooms">The combined library the linker takes.</param>
/// <param name="Warnings">The warnings, each a whole sentence, in the order the link prints them.</param>
public sealed record LevelLibrarySet(RoomLibrary Rooms, IReadOnlyList<string> Warnings);

/// <summary>
/// A level's rooms taken from several library VMFs (the rooms design,
/// section 17.2 to 17.5): the key and alias rules, the resolution of a cell
/// to one library's room, the compatibility check and the singleton rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names.</b> A level file that names its libraries with
/// <c>libraries:</c> places every room by its <b>qualified</b> name,
/// <c>key.room</c>: whatever a cell wrote (the qualified name, an alias, or
/// a bare name one library alone has), <see cref="Resolve"/> turns it into
/// that, so the link, the flatten and every message after it see one
/// spelling per room, and two libraries' <c>corner</c> rooms are two rooms
/// of the level. A level that names one library with <c>library:</c> keeps
/// bare names and links exactly as it always did; only its aliases, if it
/// has any, are replaced by what they name.
/// </para>
/// <para>
/// <b>Why qualified names are read first.</b> A room name may hold a dot,
/// so <c>v2.hall</c> could be a room of that name. Rather than pick, a level
/// is refused when one of its libraries has a room whose name starts with
/// another library's key and a dot (the dotted-name guard), whether or not
/// a cell uses it: an edit elsewhere in the file then never changes what a
/// cell means, and the cure, renaming the key, is the level author's.
/// </para>
/// <para>
/// <b>The first library supplies the singletons</b> (D20, D24, D29): its
/// library entities, options and skybox are the level's; another library's
/// copy of one it has is dropped with a warning, and equal copies are summed
/// into one line per library. A singleton the first library lacks entirely
/// is taken from the earliest later library that has it, without a line
/// (D29, the owner's answer to O25; <see cref="Singletons"/>).
/// <b>Compatibility</b> (D21, D25): the libraries the level
/// places rooms of must agree on the cell size, the door kit and, when both
/// build navigation, the navigation grid; the room height is not compared.
/// The other navigation settings and the worldspawn only warn. The link and
/// the flatten run the same check (<see cref="Check"/>) on what each can
/// read, the compiled rooms or the library VMFs, and print the same lines;
/// the one line only the link prints is the sun's (D26), since only the
/// link carries baked lighting.
/// </para>
/// <para>
/// Everything here is a pure function of its arguments: nothing is kept
/// between levels.
/// </para>
/// </remarks>
public static class LevelLibraries
{
    /// <summary>What separates a library key from a room name in a qualified name.</summary>
    public const char Separator = '.';

    /// <summary>
    /// The worldspawn keys the worldspawn comparison leaves out: those that
    /// describe one room or one compile rather than the map (the extent, the
    /// editor's ids, the ids the link stamps), the save counter the library
    /// options carry (<see cref="RoomLibraryOptions.MapVersionKey"/>), and
    /// the navigation keys, which the navigation line reports.
    /// </summary>
    private static readonly ImmutableHashSet<string> NotCompared = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "id", "hammerid", "world_mins", "world_maxs", RoomLibraryOptions.MapVersionKey, RoomCompileIds.LevelIdKey, RoomCompileIds.PackIdKey);

    /// <summary>Why a string cannot be a library key, or null when it can.</summary>
    /// <param name="key">The candidate.</param>
    /// <returns>The problem, or null.</returns>
    /// <remarks>
    /// A key starts with a letter and holds only letters, digits, <c>_</c>
    /// and <c>-</c>: no dot, since the dot separates the key from the room
    /// in a cell. Letters and digits are any script's, as a room name's are
    /// (<see cref="RoomNames"/>).
    /// </remarks>
    public static string? KeyProblem(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || !char.IsLetter(key[0]))
        {
            return "does not start with a letter";
        }

        return key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') ? null : "holds a character other than a letter, a digit, '_' or '-'";
    }

    /// <summary>Why a string cannot be an alias, or null when it can.</summary>
    /// <param name="alias">The candidate.</param>
    /// <returns>The problem, or null.</returns>
    /// <remarks>
    /// An alias is a name by the room-name rule without a dot: it starts with
    /// a letter, a digit or <c>_</c> and holds only letters, digits, <c>_</c>
    /// and <c>-</c>. Without a dot it can never read as a qualified name.
    /// </remarks>
    public static string? AliasProblem(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (alias.Length == 0 || !(char.IsLetterOrDigit(alias[0]) || alias[0] == '_'))
        {
            return "does not start with a letter, a digit or '_'";
        }

        return alias.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') ? null : "holds a character other than a letter, a digit, '_' or '-'";
    }

    /// <summary>A room's qualified name: its library's key, a dot, and its own name.</summary>
    /// <param name="key">The library's key.</param>
    /// <param name="room">The room's name in its library.</param>
    /// <returns><c>key.room</c>.</returns>
    public static string Qualified(string key, string room) => string.Concat(key, ".", room);

    /// <summary>Splits a qualified name at its first dot, which a key never holds.</summary>
    /// <param name="qualified">The name.</param>
    /// <param name="key">The key before the dot.</param>
    /// <param name="room">The room after it.</param>
    /// <returns>False when the name holds no dot.</returns>
    public static bool TrySplit(string qualified, out string key, out string room)
    {
        ArgumentNullException.ThrowIfNull(qualified);
        int dot = qualified.IndexOf(Separator, StringComparison.Ordinal);
        key = dot < 0 ? string.Empty : qualified[..dot];
        room = dot < 0 ? qualified : qualified[(dot + 1)..];
        return dot >= 0;
    }

    /// <summary>
    /// Refuses a level whose libraries name one file twice, after resolving
    /// each against the level file's folder.
    /// </summary>
    /// <param name="grid">The level, as read.</param>
    /// <param name="resolve">A path as the level wrote it, resolved as the caller opens it.</param>
    /// <exception cref="LevelFileException">Two keys name the same file, at the second.</exception>
    /// <remarks>Paths are compared exactly (ordinal), as the host resolved them.</remarks>
    public static void CheckFiles(LevelGrid grid, Func<string, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(resolve);
        Dictionary<string, LevelLibrary> seen = new(StringComparer.Ordinal);
        foreach (LevelLibrary library in grid.Libraries ?? [])
        {
            string path = resolve(library.Path);
            if (seen.TryGetValue(path, out LevelLibrary? first))
            {
                throw new LevelFileException(library.Line, library.Column, $"libraries {first.Key} and {library.Key} name the same file, {path}.");
            }

            seen[path] = library;
        }
    }

    /// <summary>
    /// Resolves every cell (and alias) of a level to the room it names: for
    /// a level of several libraries, the room's qualified name; for a level
    /// of one, an alias replaced by the bare name it stands for.
    /// </summary>
    /// <param name="grid">The level, as read.</param>
    /// <param name="rooms">
    /// Each library's room names, in level order (one list for a
    /// <c>library:</c> level); its skybox room among them, which the link then
    /// refuses to place by name.
    /// </param>
    /// <returns>The level with every cell's room resolved, where it was written.</returns>
    /// <exception cref="LevelFileException">A rule of 17.2 is broken, at the key, alias or cell at fault.</exception>
    /// <remarks>
    /// <para>
    /// The order, as 17.2 gives it: a token holding a dot whose part before
    /// the first dot is a library key is <b>qualified</b>, and that library
    /// must have the rest; else an <b>alias</b> is replaced by what it names;
    /// else the token is a <b>bare</b> room name, which exactly one library
    /// must have. Lookup is exact, as the pack index's is.
    /// </para>
    /// <para>
    /// An alias's own value is resolved as a qualified or bare name, never
    /// as another alias: a chain of aliases would let the order they are
    /// written in matter, and an alias that names another fails as the bare
    /// room name it then is. Every alias is resolved, and every alias and
    /// every library checked, whether or not a cell uses it, so that an
    /// edit elsewhere in the file never changes whether it is accepted.
    /// </para>
    /// </remarks>
    public static LevelGrid Resolve(LevelGrid grid, IReadOnlyList<IReadOnlyList<string>> rooms)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(rooms);
        IReadOnlyList<LevelLibrary>? libraries = grid.Libraries;
        int count = libraries?.Count ?? 1;
        if (rooms.Count != count)
        {
            throw new ArgumentException($"the level has {count} librar{(count == 1 ? "y" : "ies")}; {rooms.Count} room lists were given", nameof(rooms));
        }

        HashSet<string>[] sets = [.. rooms.Select(r => r.ToHashSet(StringComparer.Ordinal))];
        if (libraries is not null)
        {
            DottedNameGuard(libraries, rooms);
        }

        // Aliases: none may hide a room, and each resolves on its own.
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (LevelAlias alias in grid.Aliases)
        {
            for (int i = 0; i < count; i++)
            {
                if (sets[i].Contains(alias.Name))
                {
                    string room = libraries is null ? alias.Name : Qualified(libraries[i].Key, alias.Name);
                    throw new LevelFileException(alias.Line, alias.Column, $"the alias \"{alias.Name}\" is also the name of room {room}; an alias may not hide a room.");
                }
            }

            aliases[alias.Name] = libraries is null
                ? alias.Value
                : Name(alias.Value, libraries, sets, alias.Line, alias.Column) ?? Bare(alias.Value, libraries, sets, alias.Line, alias.Column);
        }

        LevelCell?[] cells = new LevelCell?[grid.Cells.Count];
        for (int c = 0; c < cells.Length; c++)
        {
            if (grid.Cells[c] is not { } cell)
            {
                continue;
            }

            string resolved;
            if (libraries is null)
            {
                resolved = aliases.GetValueOrDefault(cell.Room) ?? cell.Room;
            }
            else
            {
                resolved = Name(cell.Room, libraries, sets, cell.Line, cell.Column)
                    ?? aliases.GetValueOrDefault(cell.Room)
                    ?? Bare(cell.Room, libraries, sets, cell.Line, cell.Column);
            }

            cells[c] = ReferenceEquals(resolved, cell.Room) || resolved == cell.Room ? cell : cell with { Room = resolved };
        }

        return grid.WithCells(cells);
    }

    /// <summary>
    /// A resolved level's cells written in their shortest spelling: a
    /// room's bare name when one library alone has it, else its qualified
    /// name. What <see cref="LevelYaml.Write"/> writes for a level of several
    /// libraries that a program made.
    /// </summary>
    /// <param name="resolved">The level, its cells qualified (<see cref="Resolve"/>).</param>
    /// <param name="rooms">Each library's room names, in level order.</param>
    /// <returns>The level with its cells respelt; a <c>library:</c> level unchanged.</returns>
    public static LevelGrid Shorten(LevelGrid resolved, IReadOnlyList<IReadOnlyList<string>> rooms)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(rooms);
        if (resolved.Libraries is not { } libraries)
        {
            return resolved;
        }

        HashSet<string>[] sets = [.. rooms.Select(r => r.ToHashSet(StringComparer.Ordinal))];
        HashSet<string> keys = new(libraries.Select(l => l.Key), StringComparer.Ordinal);
        LevelCell?[] cells = [.. resolved.Cells.Select(cell =>
        {
            if (cell is null || !TrySplit(cell.Room, out string key, out string room))
            {
                return cell;
            }

            // Bare only when one library has it and it would not read as a
            // qualified name (a room named after its own library's key and a
            // dot; the dotted-name guard refuses every other library's). An
            // alias never hides a room, so a bare name is never an alias.
            bool unique = sets.Count(s => s.Contains(room)) == 1;
            bool readsQualified = TrySplit(room, out string prefix, out _) && keys.Contains(prefix);
            return unique && !readsQualified ? cell with { Room = room } : cell;
        })];
        return resolved.WithCells(cells);
    }

    /// <summary>
    /// Combines a level's libraries for the link: checks them (<see cref="Check"/>),
    /// then puts every room each holds into one library under its qualified
    /// name, with the level's singletons (<see cref="Singletons"/>).
    /// </summary>
    /// <param name="resolved">The level, its cells qualified (<see cref="Resolve"/>) and its <see cref="LevelGrid.Libraries"/> set.</param>
    /// <param name="libraries">
    /// Each library as its pack gave it, in level order: the rooms the level
    /// places (and the level's skybox room, <see cref="SkyboxSource"/>)
    /// under their own names, with the library's options, entities and
    /// skybox name.
    /// </param>
    /// <returns>The combined library and the warnings.</returns>
    /// <exception cref="LinkException">The libraries are not compatible, or the level places a library's skybox room.</exception>
    public static LevelLibrarySet Combine(LevelGrid resolved, IReadOnlyList<RoomLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(libraries);
        IReadOnlyList<LevelLibrary> keys = resolved.Libraries
            ?? throw new ArgumentException("the level names one library; only a level of several is combined", nameof(resolved));
        if (libraries.Count != keys.Count)
        {
            throw new ArgumentException($"the level has {keys.Count} libraries; {libraries.Count} were given", nameof(libraries));
        }

        // The first placed room of each library, in link order, and whether
        // the level places a library's skybox room (the link's to place).
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            index[keys[i].Key] = i;
        }

        string?[] firstPlaced = new string?[keys.Count];
        foreach ((int x, int y, LevelCell cell) in resolved.Placed)
        {
            int i = SourceIndex(index, cell.Room);
            string room = RoomOf(cell.Room);
            if (libraries[i].SkyboxRoom is { } skybox && skybox == room)
            {
                throw new LinkException(
                    $"level {resolved.Name} places the skybox room {cell.Room} at cell ({x}, {y}); the link places the skybox below the grid itself.");
            }

            firstPlaced[i] ??= room;
        }

        List<LibraryFacts> facts = [];
        for (int i = 0; i < keys.Count; i++)
        {
            RoomObject? placed = firstPlaced[i] is { } name ? libraries[i].Get(name) : null;
            facts.Add(new LibraryFacts(
                keys[i].Key,
                keys[i].Path,
                placed?.Definition,
                libraries[i].LibraryEntities,
                libraries[i].Options,
                libraries[i].SkyboxRoom,
                placed is null ? null : LevelLinker.WorldspawnOf(placed)));
        }

        List<string> warnings = Check(facts);
        LevelSingletonChoice level = Singletons(facts);

        // D26: a sunlit room baked under a sun the level drops, refused
        // naming the first such room in link order (SunLine). The level's
        // sun is the first library's, or the earliest library's that has
        // one when the first has none (D29): that library's rooms were
        // baked under it and are never held to it, every other library with
        // a sun is.
        int sun = level.SunSource;
        for (int b = 0; b < keys.Count; b++)
        {
            if (b == sun || SunDifference(facts[sun].Entities, facts[b].Entities) is null)
            {
                continue;
            }

            foreach ((_, _, LevelCell cell) in resolved.Placed)
            {
                if (SourceIndex(index, cell.Room) == b
                    && libraries[b].Get(RoomOf(cell.Room)).LightingOfCompile is { RotationCount: 4 })
                {
                    warnings.Add(SunLine(RefusesDroppedSun, keys[b].Key, cell.Room, keys[sun].Key));
                    break;
                }
            }
        }

        // The combined library: the grid of the first placed library (every
        // placed library agrees with it, Check), the level's singletons (the
        // first listed library's, its gaps filled from the later ones, D29),
        // every room under its qualified name.
        int grid = Array.FindIndex(firstPlaced, p => p is not null);
        RoomObject? sample = grid >= 0 ? libraries[grid].Get(firstPlaced[grid]!) : libraries.SelectMany(l => l.Rooms).FirstOrDefault();
        if (sample is null)
        {
            throw new LinkException($"level {resolved.Name} places no room.");
        }

        RoomLibrary combined = new(sample.Definition.Kit, sample.Definition.CellSize)
        {
            Options = level.Options,
            LibraryEntities = level.Entities,
            SkyboxRoom = level.SkyboxSource is int sky ? Qualified(keys[sky].Key, libraries[sky].SkyboxRoom!) : null,
            SunSource = sun,
        };
        combined.SetSources([.. libraries.Select(l => l.Options)]);
        for (int i = 0; i < libraries.Count; i++)
        {
            // By the name each library holds a room under, which is its
            // compile name for a plain pack and its name within the library
            // for a namespace of a combined pack (compiled as key.room).
            foreach (string name in libraries[i].Names)
            {
                combined.Add(Qualified(keys[i].Key, name), libraries[i].Get(name), i);
            }
        }

        return new LevelLibrarySet(combined, warnings);
    }

    /// <summary>
    /// The compatibility check and the singleton rule over a level's library
    /// VMFs, as the flatten runs them: what <c>ssmap rooms</c> over a level
    /// prints.
    /// </summary>
    /// <param name="level">The level, as read.</param>
    /// <param name="libraries">Its library VMFs, in level order.</param>
    /// <returns>The warnings, in the order the link and the flatten print them; none for a <c>library:</c> level.</returns>
    /// <exception cref="RoomLibraryException">A library cannot be split into rooms.</exception>
    /// <exception cref="LevelFileException">A cell or alias does not resolve.</exception>
    /// <exception cref="LinkException">The libraries the level places rooms of are not compatible.</exception>
    public static IReadOnlyList<string> CheckLibraries(LevelGrid level, IReadOnlyList<VmfDocument> libraries)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(libraries);
        if (level.Libraries is not { } keys)
        {
            return [];
        }

        if (libraries.Count != keys.Count)
        {
            throw new ArgumentException($"the level has {keys.Count} libraries; {libraries.Count} VMFs were given", nameof(libraries));
        }

        RoomLibrarySplit[] splits = [.. libraries.Select(RoomLibraryVmf.SplitLibrary)];
        LevelGrid resolved = Resolve(level, [.. splits.Select(s => (IReadOnlyList<string>)[
            .. s.Rooms.Select(r => r.Definition.Name), .. s.Skybox is { } sky ? [sky.Definition.Name] : Array.Empty<string>()])]);
        RoomDefinition?[] placed = new RoomDefinition?[keys.Count];
        foreach ((_, _, LevelCell cell) in resolved.Placed)
        {
            TrySplit(cell.Room, out string key, out string room);
            int i = keys.Select((k, n) => (k, n)).First(x => x.k.Key == key).n;
            placed[i] ??= splits[i].Rooms.FirstOrDefault(r => r.Definition.Name == room)?.Definition;
        }

        return Check([.. keys.Select((k, i) => FactsOf(k, libraries[i], splits[i], placed[i]))]);
    }

    /// <summary>
    /// Whether a sunlit room baked under a sun the level drops is refused
    /// rather than warned of (the rooms design's D26). The owner's rule is to
    /// refuse it once the door light (PR 10) lands, since the door terms then
    /// carry that wrong sun into the room's neighbours too; before it the
    /// link warned and linked the room as baked. The door light has landed,
    /// so this is on; the warning's text stays with <see cref="SunLine"/>
    /// for a build that turns it back off.
    /// </summary>
    internal static bool RefusesDroppedSun => true;

    /// <summary>
    /// D26's line for a library whose sunlit room was baked under its own sun
    /// where the level takes the first library's: the warning, or with
    /// <paramref name="refuse"/> the refusal.
    /// </summary>
    /// <param name="refuse">Whether the case is refused (<see cref="RefusesDroppedSun"/>).</param>
    /// <param name="library">The later library's key.</param>
    /// <param name="room">Its first such room in link order, qualified.</param>
    /// <param name="first">The first library's key.</param>
    /// <returns>The warning.</returns>
    /// <exception cref="LinkException">When <paramref name="refuse"/> is set.</exception>
    internal static string SunLine(bool refuse, string library, string room, string first) =>
        refuse
            ? throw new LinkException(
                $"library {library}: room {room} was baked under library {library}'s sun, but the level takes library {first}'s;"
                + " a sunlit room links only under the sun it was baked with. Build the libraries with one sun.")
            : $"library {library}: room {room} was baked under library {library}'s sun, and the level takes library {first}'s;"
                + " it links as baked. Build the libraries with one sun.";

    /// <summary>
    /// What the compatibility check and the singleton rule read of one
    /// library: from its pack and a room it places at link, from its VMF in
    /// the flatten.
    /// </summary>
    /// <param name="Key">The library's key.</param>
    /// <param name="Path">The library VMF as the level wrote it.</param>
    /// <param name="Placed">The definition of the first room the level places of it (its grid and kit), or null when it places none.</param>
    /// <param name="Entities">Its library-wide entities, in library order.</param>
    /// <param name="Options">Its settings.</param>
    /// <param name="Skybox">Its skybox room's name, or null.</param>
    /// <param name="World">Its worldspawn's keys (a placed room's compiled worldspawn, or the VMF's), or null when the level places none of its rooms.</param>
    internal sealed record LibraryFacts(
        string Key,
        string Path,
        RoomDefinition? Placed,
        IReadOnlyList<VmfChunk> Entities,
        RoomLibraryOptions Options,
        string? Skybox,
        IReadOnlyList<KeyValuePair<string, string>>? World);

    /// <summary>
    /// The compatibility check and the singleton rule over a level's
    /// libraries, in level order: refusals first, then the warnings.
    /// </summary>
    /// <param name="facts">What each library holds, in level order.</param>
    /// <returns>The warnings, each a whole sentence: per library after the first, its singletons, options, skybox, navigation and worldspawn lines.</returns>
    /// <exception cref="LinkException">Two placed libraries differ in their grid, their door kit or their navigation grid.</exception>
    internal static List<string> Check(IReadOnlyList<LibraryFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Compatibility, over the libraries the level places rooms of, each
        // against the earliest of them.
        List<LibraryFacts> placed = [.. facts.Where(f => f.Placed is not null)];
        LibraryFacts? level = placed.FirstOrDefault();
        foreach (LibraryFacts b in placed.Skip(1))
        {
            RoomDefinition x = level!.Placed!, y = b.Placed!;
            if (x.CellSize != y.CellSize)
            {
                throw new LinkException(
                    $"libraries {level.Key} ({level.Path}) and {b.Key} ({b.Path}) are built for different grids: {RoomLibraryVmf.CellSizeKey} {Format(x.CellSize)} against {Format(y.CellSize)};"
                    + " the rooms of a level share one cell size.");
            }

            if (KitDifference(x.Kit, y.Kit) is { } kit)
            {
                throw new LinkException(
                    $"libraries {level.Key} ({level.Path}) and {b.Key} ({b.Path}) have different door kits: {kit.Key} {Format(kit.X)} against {Format(kit.Y)};"
                    + " the rooms of a level join through one kit.");
            }
        }

        foreach (LibraryFacts b in placed.Skip(1))
        {
            if (NavGrid(level!) is float x && NavGrid(b) is float y && x != y)
            {
                throw new LinkException(
                    $"libraries {level!.Key} and {b.Key} build navigation on different grids: {Format(x)} against {Format(y)} voxels per cell;"
                    + " a level's navigation is one grid.");
            }
        }

        List<string> warnings = [];
        int? skyboxSource = SkyboxSource([.. facts.Select(f => f.Skybox)]);
        for (int i = 1; i < facts.Count; i++)
        {
            LibraryFacts b = facts[i];
            EntityLines(facts, i, warnings);
            OptionLines(facts, i, warnings);
            if (b.Skybox is { } skybox && skyboxSource != i)
            {
                LibraryFacts a = facts[skyboxSource!.Value];
                warnings.Add($"library {b.Key}: its skybox room \"{skybox}\" is dropped; the level's skybox is library {a.Key}'s, \"{a.Skybox}\".");
            }

            if (level is not null && b.World is not null && !ReferenceEquals(b, level))
            {
                if (NavLine(level, b) is { } nav)
                {
                    warnings.Add(nav);
                }

                if (WorldDifference(b.World, level.World!) is { } world)
                {
                    warnings.Add(
                        $"library {b.Key}: its rooms were compiled with worldspawn {world.Key} \"{world.X}\"; the level's is \"{world.Y}\" (library {level.Key})."
                        + " They link as compiled; build the libraries into one pack with ssmap roompack to compile them with the level's.");
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// The facts the flatten reads of one library from its VMF (<see cref="Check"/>):
    /// the grid and kit of the first room the level places of it, its
    /// split's entities and skybox, and its worldspawn's keys.
    /// </summary>
    internal static LibraryFacts FactsOf(LevelLibrary library, VmfDocument vmf, RoomLibrarySplit split, RoomDefinition? placed)
    {
        VmfChunk world = vmf.GetChunk(MapFileLoader.WorldChunk)!;
        return new LibraryFacts(
            library.Key,
            library.Path,
            placed,
            split.LibraryEntities,
            RoomLibraryOptions.FromWorld(world),
            split.Skybox?.Definition.Name,
            placed is null ? null : [.. world.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value))]);
    }

    /// <summary>
    /// The level's singletons (the rooms design, D20 and D29): what the link
    /// and the flatten write once for the whole level, from the libraries in
    /// level order.
    /// </summary>
    /// <param name="Entities">
    /// The level's library-wide entities: the first library's, as it wrote
    /// them, then each later library's that no earlier library has (by
    /// <see cref="RoomLibraryEntities.IdentityOf"/>), in library order and
    /// each library's own order. The first library's own list when no later
    /// library adds one, so a level whose first library has every singleton
    /// writes exactly what it wrote before D29.
    /// </param>
    /// <param name="Options">
    /// The level's options: the first library's, each of the entity
    /// reserve, the fold and the door portals it does not set taken from the
    /// earliest later library that sets it. The first library's own record
    /// when none is taken. The name keys stay per library and the save
    /// counter stays the first library's (17.4).
    /// </param>
    /// <param name="SkyboxSource">The library whose skybox room is the level's (<see cref="LevelLibraries.SkyboxSource"/>), or null when none has one.</param>
    /// <param name="SunSource">The library whose sun is the level's: the earliest with a <c>light_environment</c>, or 0 when none has one.</param>
    internal sealed record LevelSingletonChoice(IReadOnlyList<VmfChunk> Entities, RoomLibraryOptions Options, int? SkyboxSource, int SunSource);

    /// <summary>
    /// The library whose skybox room is the level's: the first library when
    /// it has one, else the earliest listed library that has one (the rooms
    /// design's D29), or null when none has.
    /// </summary>
    /// <param name="skyboxes">Each library's skybox room name or null, in level order.</param>
    /// <returns>The library's index, or null.</returns>
    /// <remarks>
    /// Public because whoever loads the rooms for the link (the CLI reads
    /// each pack for the rooms the level places) must load this library's
    /// skybox room too: the link places it below the grid, and
    /// <see cref="Combine"/> names it as the level's.
    /// </remarks>
    public static int? SkyboxSource(IReadOnlyList<string?> skyboxes)
    {
        ArgumentNullException.ThrowIfNull(skyboxes);
        for (int i = 0; i < skyboxes.Count; i++)
        {
            if (skyboxes[i] is not null)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The level's singletons from its libraries' facts, in level order: the
    /// first library supplies every singleton it has, and a singleton it
    /// lacks entirely comes from the earliest later library that has it
    /// (D29, the owner's answer to O25: a singleton only a later library has
    /// is used, not dropped).
    /// </summary>
    /// <param name="facts">What each library holds, in level order.</param>
    /// <returns>The level's entities, options, skybox and sun source.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why fill a gap but never override.</b> Where the first library has
    /// a singleton, or sets an option, the level has an answer and it is
    /// the first library's (D20). Where it has none, dropping a later
    /// library's copy leaves the level without a sun or a fog its rooms
    /// were built to have, which helps nobody; the earliest library's is
    /// taken, so the answer still depends only on the level file's order,
    /// never on which rooms a level happens to place.
    /// </para>
    /// <para>
    /// <b>Options.</b> An option a library does not write is a gap: the
    /// section records it as unset (<see cref="RoomLibraryOptions"/>'s null),
    /// not as its default, so the same rule reads it: the level takes the
    /// earliest library that sets it. An option the first library sets,
    /// even to the default, is an explicit value and stands.
    /// </para>
    /// </remarks>
    internal static LevelSingletonChoice Singletons(IReadOnlyList<LibraryFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentOutOfRangeException.ThrowIfZero(facts.Count);

        IReadOnlyList<VmfChunk> entities = facts[0].Entities;
        List<VmfChunk>? filled = null;
        HashSet<string> taken = new(StringComparer.Ordinal);
        for (int i = 1; i < facts.Count; i++)
        {
            foreach (VmfChunk entity in facts[i].Entities)
            {
                string identity = IdentityOf(RoomLibraryEntities.PairsOf(entity));
                if (EntitySource(facts, identity) == i && taken.Add(identity))
                {
                    (filled ??= [.. entities]).Add(entity);
                }
            }
        }

        RoomLibraryOptions first = facts[0].Options;
        int? reserve = OptionSource(facts, o => o.EntityReserve) is int r ? facts[r].Options.EntityReserve : null;
        bool? fold = OptionSource(facts, o => o.FoldLogic) is int f ? facts[f].Options.FoldLogic : null;
        bool? doors = OptionSource(facts, o => o.DoorPortals) is int d ? facts[d].Options.DoorPortals : null;
        RoomLibraryOptions options = reserve == first.EntityReserve && fold == first.FoldLogic && doors == first.DoorPortals
            ? first
            : first with { EntityReserve = reserve, FoldLogic = fold, DoorPortals = doors };

        return new LevelSingletonChoice(
            filled ?? entities,
            options,
            SkyboxSource([.. facts.Select(f => f.Skybox)]),
            EntitySource(facts, RoomLibraryEntities.SunClass) ?? 0);
    }

    /// <summary>The earliest library with a singleton of this identity, or null when none has one.</summary>
    private static int? EntitySource(IReadOnlyList<LibraryFacts> facts, string identity)
    {
        for (int i = 0; i < facts.Count; i++)
        {
            if (facts[i].Entities.Any(e => IdentityOf(RoomLibraryEntities.PairsOf(e)) == identity))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The earliest library that sets an option, or null when none does.</summary>
    private static int? OptionSource<T>(IReadOnlyList<LibraryFacts> facts, Func<RoomLibraryOptions, T?> value)
        where T : struct
    {
        for (int i = 0; i < facts.Count; i++)
        {
            if (value(facts[i].Options) is not null)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// One later library's singleton lines against the level's: each that
    /// differs from the copy the level keeps, then one line for the equal
    /// ones (D24). A singleton the library supplies itself, because no
    /// earlier library has one, is the level's and has no line (D29).
    /// </summary>
    private static void EntityLines(IReadOnlyList<LibraryFacts> facts, int index, List<string> warnings)
    {
        LibraryFacts b = facts[index];
        List<(string Label, int Source)> equal = [];
        foreach (VmfChunk entity in b.Entities)
        {
            List<KeyValuePair<string, string>> pairs = RoomLibraryEntities.PairsOf(entity);
            string identity = IdentityOf(pairs);
            int source = EntitySource(facts, identity)!.Value;
            if (source == index)
            {
                continue;
            }

            LibraryFacts a = facts[source];
            List<KeyValuePair<string, string>> kept = RoomLibraryEntities.PairsOf(a.Entities.First(e => IdentityOf(RoomLibraryEntities.PairsOf(e)) == identity));
            string classname = RoomLibraryEntities.LastValue(pairs, "classname") ?? string.Empty;
            string label = RoomLibraryEntities.NameOf(pairs) is { } name ? $"{classname} \"{name}\"" : classname;
            if (RoomLibraryEntities.Difference(pairs, kept) is { } difference)
            {
                // The first library's copy is "the first listed"; a copy that
                // filled its gap is the earliest listed library's with one.
                string which = source == 0 ? "the first listed" : "the earliest listed with one";
                warnings.Add(
                    $"library {b.Key}: its {label} differs from library {a.Key}'s ({difference.Key}: \"{difference.Value}\" against \"{difference.Other}\");"
                    + $" the level takes library {a.Key}'s, {which}, and drops it.");
            }
            else
            {
                equal.Add((label, source));
            }
        }

        if (equal.Count == 0)
        {
            return;
        }

        // One line per library (D24), naming the library the level's copies
        // came from; when they came from several (a gap of the first filled
        // by a later library), each copy names its own.
        warnings.Add(equal.All(e => e.Source == equal[0].Source)
            ? $"library {b.Key}: {equal.Count} singleton(s) equal to library {facts[equal[0].Source].Key}'s dropped ({string.Join(", ", equal.Select(e => e.Label))})."
            : $"library {b.Key}: {equal.Count} singleton(s) equal to the level's dropped ({string.Join(", ", equal.Select(e => $"{e.Label} of library {facts[e.Source].Key}"))}).");
    }

    /// <summary>
    /// One later library's option lines: each option it sets to other than
    /// the level's value, which is the first library's or, where the first
    /// does not set it, the earliest library's that does (D29); an option
    /// the library supplies itself has no line. The save counter
    /// (<c>mapversion</c>) is not reported: it differs on every save of
    /// either library, so a line for it would be printed on every link and
    /// say nothing.
    /// </summary>
    private static void OptionLines(IReadOnlyList<LibraryFacts> facts, int index, List<string> warnings)
    {
        LibraryFacts b = facts[index];
        void Line<T>(string key, Func<RoomLibraryOptions, T?> value, Func<T, string> text)
            where T : struct
        {
            int source = OptionSource(facts, value) ?? index;
            if (value(b.Options) is T theirs && source != index)
            {
                LibraryFacts a = facts[source];
                string ours = text(value(a.Options)!.Value);
                if (text(theirs) != ours)
                {
                    warnings.Add($"library {b.Key}: {key} {text(theirs)} is ignored; the level takes library {a.Key}'s, {ours}.");
                }
            }
        }

        Line(RoomLibraryOptions.EntityReserveKey, o => o.EntityReserve, v => v.ToString(CultureInfo.InvariantCulture));
        Line(RoomLibraryOptions.FoldLogicKey, o => o.FoldLogic, Switch);
        Line(RoomLibraryOptions.DoorPortalsKey, o => o.DoorPortals, Switch);
    }

    /// <summary>
    /// The navigation line of one placed library against the level's: the
    /// first setting, other than the grid, that its rooms were built with
    /// differently, or null when they agree or either builds no navigation.
    /// </summary>
    private static string? NavLine(LibraryFacts level, LibraryFacts b)
    {
        if (NavOf(level.World!) is not { } ours || NavOf(b.World!) is not { } theirs)
        {
            return null;
        }

        (string Key, bool Same, string Default)[] settings =
        [
            (NavSettings.SlopeKey, ours.FloorNormalZ == theirs.FloorNormalZ, "45.57"),
            (NavSettings.StepKey, ours.StepHeight == theirs.StepHeight, Format(NavSettings.DefaultStepHeight)),
            (NavSettings.JumpHeightKey, ours.JumpHeight == theirs.JumpHeight, Format(NavSettings.DefaultJumpHeight)),
            (NavSettings.JumpDistanceKey, ours.JumpDistance == theirs.JumpDistance, Format(NavSettings.DefaultJumpDistance)),
            (NavSettings.WaterCostKey, ours.WaterCost == theirs.WaterCost, Format(NavSettings.DefaultWaterCost)),
            (NavSettings.LadderCostKey, ours.LadderCost == theirs.LadderCost, Format(NavSettings.DefaultLadderCost)),
            (NavSettings.AgentsKey, ours.Agents.SequenceEqual(theirs.Agents), NavSettings.DefaultAgents),
        ];
        foreach ((string key, bool same, string fallback) in settings)
        {
            if (!same)
            {
                string x = RoomLibraryEntities.LastValue(b.World!, key) ?? fallback;
                string y = RoomLibraryEntities.LastValue(level.World!, key) ?? fallback;
                return $"library {b.Key}: its rooms' navigation was built with {key} {x}; the level's is {y} (library {level.Key}).";
            }
        }

        return null;
    }

    /// <summary>A library's navigation settings from its worldspawn's keys, or null when it builds none.</summary>
    private static NavSettings? NavOf(IReadOnlyList<KeyValuePair<string, string>> world)
    {
        VmfChunk chunk = new(MapFileLoader.WorldChunk);
        foreach (KeyValuePair<string, string> pair in world)
        {
            chunk.AddKey(pair.Key, pair.Value);
        }

        VmfDocument document = new();
        document.Chunks.Add(chunk);
        return NavSettings.FromLibrary(document);
    }

    /// <summary>The navigation voxels along a placed library's cell edge, or null when it builds no navigation.</summary>
    private static float? NavGrid(LibraryFacts facts) =>
        facts.World is { } world && NavOf(world) is { } nav ? facts.Placed!.CellSize / nav.VoxelSize : null;

    /// <summary>
    /// The first worldspawn key two libraries' rooms disagree on, in the
    /// other library's key order and then the level's (a missing key read
    /// as empty, a repeated key's last value), leaving out
    /// <see cref="NotCompared"/>, the library keys and the navigation keys.
    /// </summary>
    private static (string Key, string X, string Y)? WorldDifference(
        IReadOnlyList<KeyValuePair<string, string>> theirs, IReadOnlyList<KeyValuePair<string, string>> ours)
    {
        static bool Compared(string key) =>
            !NotCompared.Contains(key) && !RoomLibraryOptions.IsLibraryKey(key)
            && !key.Equals(NavSettings.EnabledKey, StringComparison.OrdinalIgnoreCase)
            && !key.StartsWith("nav_", StringComparison.OrdinalIgnoreCase);

        foreach (string key in theirs.Select(p => p.Key).Concat(ours.Select(p => p.Key)).Where(Compared).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string x = RoomLibraryEntities.LastValue(theirs, key) ?? string.Empty;
            string y = RoomLibraryEntities.LastValue(ours, key) ?? string.Empty;
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                return (key, x, y);
            }
        }

        return null;
    }

    /// <summary>
    /// How a later library's sun differs from the first's, or null when they
    /// are equal or either has none (a level of rooms lit with a sun and
    /// without one is refused by the lighting rule itself).
    /// </summary>
    private static (string Key, string Value, string Other)? SunDifference(IReadOnlyList<VmfChunk> first, IReadOnlyList<VmfChunk> other)
    {
        static List<KeyValuePair<string, string>>? Sun(IReadOnlyList<VmfChunk> entities) =>
            entities.Select(RoomLibraryEntities.PairsOf)
                .FirstOrDefault(p => RoomLibraryEntities.LastValue(p, "classname") == RoomLibraryEntities.SunClass);

        return Sun(first) is { } ours && Sun(other) is { } theirs ? RoomLibraryEntities.Difference(theirs, ours) : null;
    }

    /// <summary>The first of the kit's keys two kits differ in, in the order the library writes them.</summary>
    private static (string Key, float X, float Y)? KitDifference(SocketKit x, SocketKit y) =>
        x.Width != y.Width ? (RoomLibraryVmf.DoorWidthKey, x.Width, y.Width)
        : x.Height != y.Height ? (RoomLibraryVmf.DoorHeightKey, x.Height, y.Height)
        : x.Depth != y.Depth ? (RoomLibraryVmf.WallDepthKey, x.Depth, y.Depth)
        : null;

    private static string IdentityOf(List<KeyValuePair<string, string>> pairs) =>
        RoomLibraryEntities.IdentityOf(RoomLibraryEntities.LastValue(pairs, "classname") ?? string.Empty, RoomLibraryEntities.NameOf(pairs));

    private static string Switch(bool on) => on ? "1" : "0";

    private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The library index a qualified name belongs to.</summary>
    private static int SourceIndex(Dictionary<string, int> index, string qualified) =>
        TrySplit(qualified, out string key, out _) && index.TryGetValue(key, out int i)
            ? i
            : throw new ArgumentException($"\"{qualified}\" is not a qualified name of one of the level's libraries", nameof(qualified));

    private static string RoomOf(string qualified) => TrySplit(qualified, out _, out string room) ? room : qualified;

    /// <summary>
    /// A token read as a qualified name: when it holds a dot and the part
    /// before the first one is a library key, the room of that library it
    /// names (refused when the library has no such room); else null.
    /// </summary>
    private static string? Name(string token, IReadOnlyList<LevelLibrary> libraries, HashSet<string>[] sets, int line, int column)
    {
        if (!TrySplit(token, out string key, out string room))
        {
            return null;
        }

        for (int i = 0; i < libraries.Count; i++)
        {
            if (libraries[i].Key == key)
            {
                return sets[i].Contains(room)
                    ? token
                    : throw new LevelFileException(line, column, $"library {key} ({libraries[i].Path}) has no room \"{room}\".");
            }
        }

        return null;
    }

    /// <summary>A token read as a bare room name: the one library that has it, qualified; refused when none or several do.</summary>
    private static string Bare(string token, IReadOnlyList<LevelLibrary> libraries, HashSet<string>[] sets, int line, int column)
    {
        List<string> keys = [.. Enumerable.Range(0, libraries.Count).Where(i => sets[i].Contains(token)).Select(i => libraries[i].Key)];
        return keys.Count switch
        {
            1 => Qualified(keys[0], token),
            0 => throw new LevelFileException(
                line, column, $"no library of the level has a room \"{token}\"; its libraries are {And([.. libraries.Select(l => l.Key)])}."),
            _ => throw new LevelFileException(
                line,
                column,
                $"the room \"{token}\" is in libraries {And(keys)}; write {Or([.. keys.Select(k => Qualified(k, token))])}, or name one with an alias."),
        };
    }

    /// <summary>
    /// Refuses a level one of whose libraries has a room whose name reads as
    /// another library's qualified name (<c>v2.hall</c> beside a library
    /// keyed <c>v2</c>), at that other library's key.
    /// </summary>
    private static void DottedNameGuard(IReadOnlyList<LevelLibrary> libraries, IReadOnlyList<IReadOnlyList<string>> rooms)
    {
        for (int i = 0; i < libraries.Count; i++)
        {
            foreach (string room in rooms[i])
            {
                if (!TrySplit(room, out string prefix, out string rest))
                {
                    continue;
                }

                foreach (LevelLibrary other in libraries)
                {
                    if (other.Key == prefix && !ReferenceEquals(other, libraries[i]))
                    {
                        throw new LevelFileException(
                            other.Line,
                            other.Column,
                            $"room \"{room}\" of library {libraries[i].Key} reads as room \"{rest}\" of library {prefix}; rename the library key {prefix}.");
                    }
                }
            }
        }
    }

    /// <summary><c>a</c>, <c>a and b</c>, <c>a, b and c</c>.</summary>
    internal static string And(IReadOnlyList<string> items) => Join(items, "and");

    /// <summary><c>a</c>, <c>a or b</c>, <c>a, b or c</c>.</summary>
    internal static string Or(IReadOnlyList<string> items) => Join(items, "or");

    private static string Join(IReadOnlyList<string> items, string last) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} {last} {items[^1]}";
}
