//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// One arrangement, both ways: linked from the compiled rooms, and compiled
/// whole from the VMF the level flattens to.
/// </summary>
/// <param name="Case">The arrangement and why it is tested.</param>
/// <param name="Level">The level file the link and the flatten both read, as parsed.</param>
/// <param name="Layout">The layout the linker linked, derived from the level.</param>
/// <param name="Flattened">The level flattened into one VMF (<see cref="LevelFlattener"/>).</param>
/// <param name="Linked">The link.</param>
/// <param name="Monolithic">The vbsp compile of the flattened VMF.</param>
/// <param name="MonolithicVis">The monolithic map's real vvis.</param>
internal sealed record Rooms3x3Pair(
    Rooms3x3Case Case,
    LevelGrid Level,
    LevelLayout Layout,
    VmfDocument Flattened,
    LinkedLevel Linked,
    VbspResult Monolithic,
    VisResult MonolithicVis)
{
    public LevelProbe LinkedProbe { get; } = new(Linked.Bsp);

    public LevelProbe MonolithicProbe { get; } = new(Monolithic.Bsp!);
}

/// <summary>
/// The 3x3 sample's room library, compiled once for the class, and each
/// arrangement's pair of maps, built once and shared by every criterion.
/// </summary>
/// <remarks>
/// Everything is built from the generator's own file bytes
/// (<see cref="Rooms3x3Sample.Build"/>): the room library VMF, split into
/// rooms as <c>ssmap room</c> splits it, and the materials, on an in-memory
/// disk. Each case's level goes through the level file's text, as
/// <c>ssmap link</c> reads it, and the reference is the same level file
/// flattened, as <c>ssmap link --flatten</c> writes it. The rooms and the
/// monolithic maps are cooked with the managed cooker, which is what
/// <c>ssmap room</c> and <c>ssmap vbsp</c> use by default, so world
/// collision is compared too.
/// </remarks>
public sealed class Rooms3x3Fixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Rooms3x3Pair>>> _pairs = new(StringComparer.Ordinal);
    private ContentFileSystem? _content;
    private ManagedCollisionCooker? _cooker;
    private RoomLibrary? _library;

    /// <summary>Every valid arrangement, in enumeration order.</summary>
    public static IReadOnlyList<Rooms3x3Arrangement> All => AllArrangements.Value;

    /// <summary>The default subset.</summary>
    public static IReadOnlyList<Rooms3x3Case> Cases => DefaultCases.Value;

    private static readonly Lazy<IReadOnlyList<Rooms3x3Arrangement>> AllArrangements = new(Rooms3x3Permutations.All);

    private static readonly Lazy<IReadOnlyList<Rooms3x3Case>> DefaultCases =
        new(Rooms3x3Permutations.DefaultCases);

    /// <summary>The case names, for a theory's member data.</summary>
    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c.Name)];

    internal RoomLibrary Library => _library ?? throw new InvalidOperationException("the fixture is not initialised");

    /// <summary>The sample's room library VMF, as parsed.</summary>
    internal VmfDocument LibraryVmf => _libraryVmf ?? throw new InvalidOperationException("the fixture is not initialised");

    private VmfDocument? _libraryVmf;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        IReadOnlyDictionary<string, byte[]> files = Rooms3x3Sample.Build();
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in files)
        {
            if (path.StartsWith("materials/", StringComparison.Ordinal))
            {
                disk.AddFile(path, bytes);
            }
        }

        _content = new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        _cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        _library = new RoomLibrary(
            new SocketKit(Rooms3x3Kit.DoorWidth, Rooms3x3Kit.DoorHeight, Rooms3x3Kit.Wall), Rooms3x3Kit.CellSize);
        _libraryVmf = await VmfDocument.ParseAsync(files[Rooms3x3Kit.LibraryFile]);

        // The library's settings, as ssmap link reads them from the pack:
        // among them the library's mapversion, which the rooms do not carry
        // and the link writes into the linked worldspawn.
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(_libraryVmf);
        _library.Options = split.Options;
        foreach (LibraryRoom room in split.Rooms)
        {
            _library.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, Context(room.Definition.Name)));
        }
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_cooker is not null)
        {
            await _cooker.DisposeAsync();
        }

        if (_content is not null)
        {
            await _content.DisposeAsync();
        }
    }

    /// <summary>A compile context over the sample's materials, cooking with the managed cooker.</summary>
    internal VbspContext Context(string mapBase) =>
        new(VbspOptions.Default, _content ?? throw new InvalidOperationException("the fixture is not initialised"))
        {
            MapBase = mapBase,
            CollisionCooker = _cooker,
        };

    /// <summary>A case's pair of maps, built on first use.</summary>
    internal Task<Rooms3x3Pair> PairAsync(string caseName) =>
        _pairs.GetOrAdd(caseName, name => new Lazy<Task<Rooms3x3Pair>>(
            () => BuildAsync(Cases.Single(c => c.Name == name)))).Value;

    /// <summary>
    /// Any arrangement's pair of maps, not kept: the level file's text read
    /// back, the link from the library, the flattened level's vbsp compile,
    /// and the monolithic map's vvis.
    /// </summary>
    internal async Task<Rooms3x3Pair> BuildAsync(Rooms3x3Case found)
    {
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(found.Name, Rooms3x3Sample.LibraryFromLevels), found.Name);
        LevelLayout layout = level.ToLayout(name => Library.Find(name)?.Definition, Library.CellSize, Library.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, Library, Context(found.Name));

        VmfDocument flattened = LevelFlattener.Flatten(level, LibraryVmf);
        VbspResult monolithic = await CompileAsync(flattened, found.Name);
        VisResult vis = await RoomHarness.VisAsync(monolithic.Bsp!, PortalSet.FromPortalFile(monolithic.Portals!));
        return new Rooms3x3Pair(found, level, layout, flattened, linked, monolithic, vis);
    }

    /// <summary>
    /// A level of the sample's rooms generated larger than the sample's 3 x 3,
    /// both ways: linked from the library (at the given thread count), and
    /// flattened, compiled whole and vvis'd, as <see cref="BuildAsync"/> does
    /// for an arrangement.
    /// </summary>
    internal async Task<(LevelLayout Layout, LinkedLevel Linked, VbspResult Monolithic, VisResult MonolithicVis)> GeneratedAsync(
        int rows, int columns, ulong seed, double empty, int degree)
    {
        string name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gen_{rows}x{columns}_{seed}");
        LevelGrid generated = LevelGenerator.Generate(
            [.. Rooms3x3Kit.Kinds.Select(Rooms3x3Kit.Definition)],
            new LevelGeneratorOptions(rows, columns, seed, empty),
            name,
            Rooms3x3Sample.LibraryFromLevels);
        LevelGrid level = LevelYaml.Parse(LevelYaml.Write(generated), name);
        LevelLayout layout = level.ToLayout(n => Library.Find(n)?.Definition, Library.CellSize, Library.Kit);
        VbspContext context = Context(name);
        context.Parallelism = new SourceSharp.MapTools.Parallel.CompileParallelism { MaxDegree = degree };
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, Library, context);

        VbspResult monolithic = await CompileAsync(LevelFlattener.Flatten(level, LibraryVmf), name);
        VisResult vis = await RoomHarness.VisAsync(monolithic.Bsp!, PortalSet.FromPortalFile(monolithic.Portals!));
        return (layout, linked, monolithic, vis);
    }

    /// <summary>An arrangement's flattened level through the ordinary vbsp path; it must not leak.</summary>
    internal async Task<VbspResult> MonolithicAsync(Rooms3x3Arrangement arrangement, string mapBase)
    {
        LevelGrid level = LevelYaml.Parse(arrangement.LevelYaml(mapBase, Rooms3x3Sample.LibraryFromLevels), mapBase);
        return await CompileAsync(LevelFlattener.Flatten(level, LibraryVmf), mapBase);
    }

    private async Task<VbspResult> CompileAsync(VmfDocument whole, string mapBase)
    {
        VbspResult monolithic = await RoomHarness.CompileAsync(whole, Context(mapBase));
        Assert.True(monolithic.Bsp is not null && monolithic.Portals is not null, $"{mapBase}: the monolithic map leaked");
        return monolithic;
    }
}
