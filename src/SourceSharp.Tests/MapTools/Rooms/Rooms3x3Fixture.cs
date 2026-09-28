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
/// whole from its monolithic VMF.
/// </summary>
/// <param name="Case">The arrangement and why it is tested.</param>
/// <param name="Layout">The layout the linker read, parsed from the generator's JSON.</param>
/// <param name="Linked">The link.</param>
/// <param name="Monolithic">The monolithic vbsp compile.</param>
/// <param name="MonolithicVis">The monolithic map's real vvis.</param>
internal sealed record Rooms3x3Pair(
    Rooms3x3Case Case,
    LevelLayout Layout,
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
/// (<see cref="Rooms3x3Sample.Build"/>): the room VMFs, their sidecars and
/// the materials, on an in-memory disk. The rooms and the monolithic maps are
/// cooked with the managed cooker, which is what <c>ssmap room</c> and
/// <c>ssmap vbsp</c> use by default, so world collision is compared too.
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
        new(() => Rooms3x3Permutations.DefaultCases(All));

    /// <summary>The case names, for a theory's member data.</summary>
    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c.Name)];

    internal RoomLibrary Library => _library ?? throw new InvalidOperationException("the fixture is not initialised");

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
        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            VmfDocument vmf = await VmfDocument.ParseAsync(files[$"maps/{kind.Name}.vmf"]);
            RoomDefinition definition = RoomDefinitionJson.Parse(
                Encoding.UTF8.GetString(files[$"maps/{kind.Name}.vmf.roomdef.json"]));
            _library.Add(await RoomCompiler.CompileAsync(vmf, definition, Context(kind.Name)));
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
    /// Any arrangement's pair of maps, not kept: the link from the library,
    /// the monolithic compile, and the monolithic map's vvis.
    /// </summary>
    internal async Task<Rooms3x3Pair> BuildAsync(Rooms3x3Case found)
    {
        LevelLayout layout = LevelLayoutJson.Parse(found.Arrangement.LayoutJson(found.Name));
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, Library, Context(found.Name));

        VbspResult monolithic = await MonolithicAsync(found.Arrangement, found.Name);
        VisResult vis = await RoomHarness.VisAsync(monolithic.Bsp!, PortalSet.FromPortalFile(monolithic.Portals!));
        return new Rooms3x3Pair(found, layout, linked, monolithic, vis);
    }

    /// <summary>An arrangement's monolithic VMF through the ordinary vbsp path; it must not leak.</summary>
    internal async Task<VbspResult> MonolithicAsync(Rooms3x3Arrangement arrangement, string mapBase)
    {
        VmfDocument whole = await VmfDocument.ParseAsync(arrangement.MonolithicVmf());
        VbspResult monolithic = await RoomHarness.CompileAsync(whole, Context(mapBase));
        Assert.True(monolithic.Bsp is not null && monolithic.Portals is not null, $"{mapBase}: the monolithic map leaked");
        return monolithic;
    }
}
