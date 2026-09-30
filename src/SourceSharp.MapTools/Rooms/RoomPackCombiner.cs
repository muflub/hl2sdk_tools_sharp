//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One library VMF given to a combined pack, in the order that decides the singletons.</summary>
/// <param name="Key">The namespace its rooms go under (<see cref="LevelLibraries.KeyProblem"/>).</param>
/// <param name="Source">The VMF as the pack records it (<see cref="RoomPackNamespace.Source"/>).</param>
/// <param name="Vmf">The VMF, parsed.</param>
/// <param name="VmfSha256">The SHA-256 of the VMF's bytes (<see cref="RoomPackNamespaces.Digest"/>).</param>
public sealed record RoomPackSource(string Key, string Source, VmfDocument Vmf, string VmfSha256);

/// <summary>What a combined pack records of one of its libraries once split, before its rooms compile.</summary>
/// <param name="Key">The namespace.</param>
/// <param name="Source">The VMF as the pack records it.</param>
/// <param name="VmfSha256">Its bytes' SHA-256.</param>
/// <param name="NameKeys">Its own name keys, normalised (<see cref="RoomLibraryOptions.NameKeys"/>), or null.</param>
/// <param name="Rooms">
/// Its rooms, qualified and ready to compile, in library order: the first
/// library's skybox room last among its own, as <c>ssmap room</c> packs it.
/// </param>
public sealed record RoomPackSpace(string Key, string Source, string VmfSha256, string? NameKeys, IReadOnlyList<LibraryRoom> Rooms);

/// <summary>
/// Several libraries split for one pack (the rooms design, 17.10): every
/// room under its qualified name and compiled under the first library's
/// worldspawn, and the pack's library-wide sections the level's singletons
/// (the first library's, its gaps filled from the later ones, D29).
/// </summary>
/// <param name="Spaces">Each library, in the order given.</param>
/// <param name="LibraryEntities">The pack's <c>LENT</c>: the first library's library-wide entities, then each later library's the first lacks (<see cref="LevelLibraries.Singletons"/>).</param>
/// <param name="Options">The pack's <c>LOPT</c>: the first library's settings, each it does not set taken from the earliest library that does.</param>
/// <param name="SkyboxRoom">The pack's <c>SKYB</c>: the skybox room of the first library with one, qualified (so its namespace says whose), or null.</param>
/// <param name="SingletonsSha256">What every namespace is compiled under (<see cref="RoomPackNamespaces.SingletonDigest"/>).</param>
/// <param name="Warnings">The singleton rule's lines, each a whole sentence, in the order the link prints them for separate packs.</param>
public sealed record RoomPackPlan(
    IReadOnlyList<RoomPackSpace> Spaces,
    IReadOnlyList<VmfChunk> LibraryEntities,
    RoomLibraryOptions Options,
    string? SkyboxRoom,
    string SingletonsSha256,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Every room of every library, in pack order.</summary>
    public IEnumerable<LibraryRoom> Rooms => Spaces.SelectMany(s => s.Rooms);
}

/// <summary>
/// Splits several library VMFs into one pack's rooms (the rooms design,
/// 17.10, D26 and D28): what <c>ssmap roompack</c> and
/// <c>ssmap room -namespace</c> compile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every library is split first</b>, so a broken library fails before any
/// room compiles, with the message <c>ssmap room</c> gives for it. Then the
/// libraries are held to one another as a level of them would be (the
/// cell size and the door kit, 17.5), and the singleton rule is applied
/// (17.4, D29): the first library supplies the pack's library-wide
/// entities, settings and skybox, each it lacks entirely taken from the
/// earliest later library that has it; every other copy is dropped, with
/// the lines the link would print for separate packs. The worldspawn and navigation
/// lines are not among them: they describe rooms compiled under another
/// library's worldspawn, and here no room is.
/// </para>
/// <para>
/// <b>One worldspawn and one sun (D26).</b> Every room of a later library
/// takes the first library's worldspawn keys as its own
/// (<see cref="RoomLibraryVmf.WithWorld"/>), in place of its library's, and
/// the pack lights every room under the level's sun (the first library's,
/// or with D29 the earliest library's with one when the first has none;
/// the worldspawn is not a singleton D29 fills: every library has one), so a level of
/// the pack links under the singletons its rooms were compiled with and the
/// link has nothing to warn about. A later library's own worldspawn, and its
/// navigation keys with it, are the level's no more than its sun is.
/// </para>
/// <para>
/// <b>Namespaces.</b> Each room is renamed <c>key.room</c> in its own
/// definition, so the pack's index, every message and its map name
/// (<c>materials/maps/&lt;mapbase&gt;/</c>, the name lower-cased) are
/// qualified and no two libraries' rooms of one name can collide. Each
/// room carries its library's name keys (<see cref="LibraryRoom.Namespace"/>),
/// since a library's name keys stay with its rooms. A single library given
/// alone is a pack of one namespace (<c>ssmap room -namespace</c>): its rooms
/// are renamed, and its worldspawn is its own, so nothing else changes.
/// </para>
/// <para>
/// A pure function of its arguments: nothing is kept between calls, and
/// the documents given are not changed.
/// </para>
/// </remarks>
public static class RoomPackCombiner
{
    /// <summary>Splits the libraries and applies the rules above.</summary>
    /// <param name="sources">The libraries, in the order that decides the singletons; at least one.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No library, a key that is not a key, or two keys equal ignoring case.</exception>
    /// <exception cref="RoomLibraryException">A library cannot be split into rooms; the message names what, not which (the caller knows).</exception>
    /// <exception cref="LinkException">Two libraries are not compatible (17.5's texts).</exception>
    /// <remarks>
    /// A library that fails to split is reported by its index through
    /// <see cref="RoomPackSplitException"/>, which carries the library's
    /// message, so the caller can name its file.
    /// </remarks>
    public static RoomPackPlan Plan(IReadOnlyList<RoomPackSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("a pack combines at least one library.", nameof(sources));
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (RoomPackSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));
            if (LevelLibraries.KeyProblem(source.Key) is { } problem)
            {
                throw new ArgumentException($"the key \"{source.Key}\" {problem}.", nameof(sources));
            }

            if (!keys.Add(source.Key))
            {
                throw new ArgumentException($"the key {source.Key} is given twice.", nameof(sources));
            }
        }

        RoomLibrarySplit[] splits = new RoomLibrarySplit[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            try
            {
                splits[i] = RoomLibraryVmf.SplitLibrary(sources[i].Vmf);
            }
            catch (RoomLibraryException exception)
            {
                throw new RoomPackSplitException(i, exception);
            }
        }

        // Compatibility and the singleton rule, over every library: all of
        // them go into the pack. No worldspawn is passed, so neither the
        // worldspawn nor the navigation line is given (the remarks say why).
        List<LevelLibraries.LibraryFacts> facts = [.. sources.Select((s, i) => new LevelLibraries.LibraryFacts(
            s.Key,
            s.Source,
            splits[i].Rooms.Count > 0 ? splits[i].Rooms[0].Definition : splits[i].Skybox?.Definition,
            splits[i].LibraryEntities,
            splits[i].Options,
            splits[i].Skybox?.Definition.Name,
            null))];
        List<string> warnings = LevelLibraries.Check(facts);

        // The pack's singletons are the level's by D29: the first library's,
        // each it lacks entirely taken from the earliest library that has
        // it. When the first has every one, these are its own list and
        // options record, and its skybox, so the pack is what it was.
        LevelLibraries.LevelSingletonChoice level = LevelLibraries.Singletons(facts);
        int? skyboxSource = level.SkyboxSource;

        VmfChunk world = sources[0].Vmf.GetChunk(MapFileLoader.WorldChunk)
            ?? throw new RoomPackSplitException(0, new RoomLibraryException("the library has no world chunk."));
        IReadOnlyList<KeyValuePair<string, string>> firstWorld = RoomLibraryVmf.RoomWorldKeys(world);
        List<RoomPackSpace> spaces = [];
        for (int i = 0; i < sources.Count; i++)
        {
            RoomPackSource source = sources[i];
            RoomNamespace space = new(source.Key, splits[i].Options.NameKeySet);

            // The level's skybox (the first library's, or the earliest
            // library's with one, D29) is packed with its library's rooms,
            // after them, as ssmap room packs it, so it stays in its own
            // namespace; every other library's is dropped with the
            // singletons (its line is among the warnings).
            IEnumerable<LibraryRoom> own = i == skyboxSource && splits[i].Skybox is { } skybox ? [.. splits[i].Rooms, skybox] : splits[i].Rooms;
            List<LibraryRoom> rooms = [];
            foreach (LibraryRoom room in own)
            {
                LibraryRoom rehomed = i == 0 ? room : RoomLibraryVmf.WithWorld(room, firstWorld);
                rooms.Add(rehomed with
                {
                    Definition = room.Definition with { Name = LevelLibraries.Qualified(source.Key, room.Definition.Name) },
                    Namespace = space,
                });
            }

            spaces.Add(new RoomPackSpace(source.Key, source.Source, source.VmfSha256, splits[i].Options.NameKeys, rooms));
        }

        string? skyboxRoom = skyboxSource is int k ? LevelLibraries.Qualified(sources[k].Key, splits[k].Skybox!.Definition.Name) : null;
        return new RoomPackPlan(
            spaces,
            level.Entities,
            level.Options,
            skyboxRoom,
            RoomPackNamespaces.SingletonDigest(firstWorld, level.Entities, skyboxSource is > 0 ? skyboxRoom : null),
            warnings);
    }

    /// <summary>
    /// The first library's navigation settings, which every room of the pack
    /// is built with, since every room carries its worldspawn (where the
    /// settings are keys); or null when it builds none.
    /// </summary>
    /// <param name="sources">The pack's libraries.</param>
    /// <returns>The settings, or null.</returns>
    public static NavSettings? NavOf(IReadOnlyList<RoomPackSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.Count == 0 ? null : NavSettings.FromLibrary(sources[0].Vmf);
    }
}

/// <summary>A library of a combined pack that cannot be split into rooms, with which one it was.</summary>
public sealed class RoomPackSplitException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="library">The library's place among those given, from zero.</param>
    /// <param name="inner">Why it cannot be split.</param>
    public RoomPackSplitException(int library, RoomLibraryException inner)
        : base(inner?.Message, inner)
    {
        Library = library;
    }

    /// <summary>The library's place among those given, from zero.</summary>
    public int Library { get; }
}
