//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Reflection;
using System.Text;

using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room cache key: every input that changes a room's pack sections
/// changes its key, and edits the room's compile never sees do not.
/// </summary>
/// <remarks>
/// Each sensitivity fact changes exactly one input, so dropping that input
/// from the key turns exactly that fact red. The insensitivity facts are
/// the other half: a key that folded the whole library, or the text as
/// written, would pass every sensitivity fact and recompile every room on
/// every edit.
/// </remarks>
public sealed class RoomCacheKeyTests
{
    private static readonly RoomDefinition[] Definitions =
    [
        RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
    ];

    private static readonly RoomCacheInputs Inputs = new(VbspOptions.Default);

    // ---- the room's own content ------------------------------------------------

    /// <summary>Two splits of the same library give every room the same key: the key is a function of its inputs.</summary>
    [Fact]
    public void TheSameLibraryGivesTheSameKeys()
    {
        Assert.Equal(Keys(Library()), Keys(Library()));
        Assert.Equal(64, Keys(Library())[0].Length);
    }

    /// <summary>Editing a room's brush changes that room's key and no other.</summary>
    [Fact]
    public void AnEditToOneRoomChangesOnlyItsKey()
    {
        VmfDocument edited = Library();
        VmfChunk side = RoomSolids(edited, 1).First().GetChunks("side").First();
        side.Children.OfType<VmfKey>().Single(k => k.Name == "material").Value = RoomHarness.PlayerClip;
        string[] before = Keys(Library());
        string[] after = Keys(edited);
        Assert.Equal(before[0], after[0]);
        Assert.NotEqual(before[1], after[1]);
    }

    /// <summary>A key of one of the room's own entities is an input.</summary>
    [Fact]
    public void AnEntityKeyOfTheRoomChangesItsKey()
    {
        VmfDocument edited = Library();
        Target(edited, 0).AddKey("spawnflags", "1");
        Assert.NotEqual(Keys(Library())[0], Keys(edited)[0]);
    }

    /// <summary>
    /// The order of the room's own entities is an input: the entity lump
    /// keeps it, so the compile is not order-insensitive there.
    /// </summary>
    [Fact]
    public void TheOrderOfTheRoomsOwnEntitiesChangesItsKey()
    {
        VmfDocument edited = Library();
        VmfChunk target = Target(edited, 0);
        edited.Chunks.Remove(target);
        int start = edited.Chunks.IndexOf(edited.Chunks.First(c => c.Name == "entity" && c.GetValue("classname") == "info_player_start"));
        edited.Chunks.Insert(start, target);
        Assert.NotEqual(Keys(Library())[0], Keys(edited)[0]);
        Assert.Equal(Keys(Library())[1], Keys(edited)[1]);
    }

    /// <summary>
    /// The library's <c>mapversion</c> (the editor's save counter, in its
    /// worldspawn and its <c>versioninfo</c>) is not an input: every room is
    /// split with a fixed one, so a save that bumps it leaves every key.
    /// </summary>
    [Fact]
    public void TheMapVersionChangesNoKey()
    {
        Assert.Equal(Keys(WithVersion(Library(), "1")), Keys(WithVersion(Library(), "2")));
        foreach (LibraryRoom room in Split(WithVersion(Library(), "7")))
        {
            Assert.Equal("0", room.Document.GetChunk("world")!.GetValue("mapversion"));
            Assert.Equal("0", room.Document.GetChunk("versioninfo")!.GetValue("mapversion"));
        }
    }

    /// <summary>A worldspawn key the rooms keep (not a library-only one) is an input of every room.</summary>
    [Fact]
    public void ARoomWorldspawnKeyChangesEveryKey()
    {
        VmfDocument edited = Library();
        edited.GetChunk("world")!.AddKey("skyname", "sky_day01_01");
        string[] before = Keys(Library());
        string[] after = Keys(edited);
        Assert.NotEqual(before[0], after[0]);
        Assert.NotEqual(before[1], after[1]);
    }

    /// <summary>The room's name is an input (it is the map's base name and its pack entry).</summary>
    [Fact]
    public void TheRoomsNameChangesItsKey()
    {
        LibraryRoom room = Split(Library())[0];
        Assert.NotEqual(Key(room), Key(room with { Definition = room.Definition with { Name = "hub2" } }));
    }

    /// <summary>The cell size is an input.</summary>
    [Fact]
    public void TheCellSizeChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        Assert.NotEqual(Key(room), Key(room with { Definition = room.Definition with { CellSize = 512f } }));
    }

    /// <summary>Each of the door kit's three sizes is an input.</summary>
    [Fact]
    public void EachDoorKitSizeChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        SocketKit kit = room.Definition.Kit;
        string key = Key(room);
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Kit = kit with { Width = kit.Width + 1 } } }));
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Kit = kit with { Height = kit.Height + 1 } } }));
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Kit = kit with { Depth = kit.Depth + 1 } } }));
    }

    /// <summary>A socket's name and the socket set are inputs.</summary>
    [Fact]
    public void TheSocketsChangeTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        List<RoomSocket> sockets = [.. room.Definition.Sockets];
        string key = Key(room);
        List<RoomSocket> renamed = [.. sockets];
        renamed[0] = renamed[0] with { Name = "front" };
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Sockets = renamed } }));
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Sockets = sockets[1..] } }));
        List<RoomSocket> turned = [.. sockets];
        turned[0] = turned[0] with { Facing = turned[1].Facing };
        Assert.NotEqual(key, Key(room with { Definition = room.Definition with { Sockets = turned } }));
    }

    /// <summary>The room's transition role is an input (its navigation reads it).</summary>
    [Fact]
    public void TheRoleChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        Assert.NotEqual(Key(room), Key(room with { Role = RoomRole.Up }));
    }

    // ---- edits the room never sees -------------------------------------------

    /// <summary>
    /// Whitespace, indentation and the editor's own chunks (view settings,
    /// cameras) are not inputs: the key reads the parsed room, written back.
    /// </summary>
    [Fact]
    public async Task WhitespaceAndEditorChunksDoNotChangeTheKeys()
    {
        string text = Encoding.Latin1.GetString(Library().ToBytes());
        string loose = text.Replace("\n", "\n\n", StringComparison.Ordinal).Replace("\t", "      ", StringComparison.Ordinal)
            + "viewsettings\n{\n\t\"bSnapToGrid\" \"1\"\n}\ncameras\n{\n\t\"activecamera\" \"-1\"\n}\n";
        VmfDocument reparsed = await VmfDocument.ParseAsync(Encoding.Latin1.GetBytes(loose));
        Assert.Equal(Keys(Library()), Keys(reparsed));
    }

    /// <summary>
    /// Moving another room's entity in front of this room's entities (an
    /// interleave the library's order allows) leaves this room's key alone.
    /// </summary>
    [Fact]
    public void AnotherRoomsEntityOrderDoesNotChangeTheKey()
    {
        // The second room's first entity (its player start) moves in front of
        // the first room's: both rooms keep their own entities' order.
        VmfDocument edited = Library();
        VmfChunk other = edited.Chunks.Where(c => c.GetValue("classname") == "info_player_start").ElementAt(1);
        edited.Chunks.Remove(other);
        edited.Chunks.Insert(1, other);
        Assert.Equal("info_player_start", edited.Chunks[1].GetValue("classname"));
        Assert.Equal(Keys(Library()), Keys(edited));
    }

    /// <summary>The order of the rooms in the library changes the pack's order, not any room's key.</summary>
    [Fact]
    public void TheRoomsOrderDoesNotChangeTheirKeys()
    {
        VmfDocument edited = Library();
        List<VmfChunk> markers = [.. edited.Chunks.Where(c => c.GetValue("classname") == RoomLibraryVmf.RoomEntity)];
        foreach (VmfChunk marker in markers)
        {
            edited.Chunks.Remove(marker);
        }

        edited.Chunks.Add(markers[1]);
        edited.Chunks.Add(markers[0]);
        IReadOnlyList<LibraryRoom> rooms = Split(edited);
        Assert.Equal(["hall", "hub"], rooms.Select(r => r.Definition.Name));
        Assert.Equal(Keys(Library()), new[] { Key(rooms[1]), Key(rooms[0]) });
    }

    /// <summary>
    /// The library-only settings (<c>rooms_entity_reserve</c>, logic folding)
    /// are link inputs kept in <c>LOPT</c>; no room compile reads them, so
    /// they change no room's key.
    /// </summary>
    [Fact]
    public void LibraryOnlySettingsDoNotChangeTheKeys()
    {
        VmfDocument edited = Library();
        edited.GetChunk("world")!.AddKey(RoomLibraryOptions.EntityReserveKey, "300");
        edited.GetChunk("world")!.AddKey(RoomLibraryOptions.FoldLogicKey, "0");
        Assert.Equal(Keys(Library()), Keys(edited));
    }

    // ---- the library-wide inputs ---------------------------------------------

    /// <summary>
    /// Every vbsp option but the two logging switches is an input. The fact
    /// walks the option record, so an option added later is covered, and one
    /// whose type the fact cannot vary fails it until someone decides.
    /// </summary>
    [Fact]
    public void EveryVbspOptionButLoggingChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        string key = Key(room);
        HashSet<string> logging = [nameof(VbspOptions.Verbose), nameof(VbspOptions.VerboseEntities)];
        int varied = 0;
        foreach (PropertyInfo property in typeof(VbspOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            VbspOptions changed = VbspOptions.Default with { };
            property.SetValue(changed, Vary(property.PropertyType, property.GetValue(changed)));
            string changedKey = Key(room, Inputs with { Options = changed });
            if (logging.Contains(property.Name))
            {
                Assert.True(key == changedKey, $"{property.Name} changes the key");
            }
            else
            {
                Assert.True(key != changedKey, $"{property.Name} does not change the key");
                varied++;
            }
        }

        Assert.True(varied > 30, $"only {varied} options varied");
    }

    /// <summary>A compliance quirk flipped on its own is an input.</summary>
    [Fact]
    public void ASingleComplianceQuirkChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        StockQuirk quirk = Enum.GetValues<StockQuirk>()[0];
        VbspOptions flipped = VbspOptions.Default with { Compliance = ComplianceOptions.Correct.Flipping(quirk) };
        Assert.NotEqual(Key(room), Key(room, Inputs with { Options = flipped }));
    }

    /// <summary>The navigation settings are inputs: on or off, and each of their values.</summary>
    [Fact]
    public void TheNavigationSettingsChangeTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        RoomCacheInputs nav = Inputs with { Nav = NavSettings.Default };
        string off = Key(room);
        string on = Key(room, nav);
        Assert.NotEqual(off, on);
        Assert.NotEqual(on, Key(room, nav with { Nav = NavSettings.Default with { VoxelSize = 8f } }));
        Assert.NotEqual(on, Key(room, nav with { Nav = NavSettings.Default with { FloorNormalZ = 0.5f } }));
        Assert.NotEqual(on, Key(room, nav with { Nav = NavSettings.Default with { Agents = NavSettings.Default.Agents.Take(1).ToList() } }));
        NavAgentSpec first = NavSettings.Default.Agents[0];
        Assert.NotEqual(on, Key(room, nav with
        {
            Nav = NavSettings.Default with { Agents = [first with { Height = first.Height + 1 }, .. NavSettings.Default.Agents.Skip(1)] },
        }));
    }

    /// <summary><c>-nav-turn0</c> and <c>-nav-codec</c> change what the pack stores, so they are inputs.</summary>
    [Fact]
    public void TheNavigationStorageChangesTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        string key = Key(room);
        Assert.NotEqual(key, Key(room, Inputs with { PackOptions = new RoomNavPackOptions { StoreAllTurns = false } }));
        Assert.True(NavCompression.TryParse("deflate", out NavCompression deflate));
        Assert.True(NavCompression.TryParse("deflate:3", out NavCompression deflate3));
        string deflated = Key(room, Inputs with { PackOptions = new RoomNavPackOptions { Compression = deflate } });
        Assert.NotEqual(key, deflated);
        Assert.NotEqual(deflated, Key(room, Inputs with { PackOptions = new RoomNavPackOptions { Compression = deflate3 } }));
    }

    /// <summary>The library's name keys are inputs, as a set: their order is not.</summary>
    [Fact]
    public void TheNameKeysChangeTheKeyAsASet()
    {
        LibraryRoom room = Split(Library())[0];
        string none = Key(room);
        string ab = Key(room, Inputs with { NameKeys = new HashSet<string> { "a", "b" } });
        Assert.NotEqual(none, ab);
        Assert.NotEqual(none, Key(room, Inputs with { NameKeys = new HashSet<string>() }));
        Assert.NotEqual(ab, Key(room, Inputs with { NameKeys = new HashSet<string> { "a", "c" } }));
        Assert.Equal(ab, Key(room, Inputs with { NameKeys = new SortedSet<string>(StringComparer.Ordinal) { "b", "a" } }));
    }

    /// <summary>The host's context tags (the cooker's identity, the preset) are inputs.</summary>
    [Fact]
    public void TheContextTagsChangeTheKey()
    {
        LibraryRoom room = Split(Library())[0];
        string managed = Key(room, Inputs with { ContextTags = ["preset=(default)", "cooker=managed"] });
        Assert.NotEqual(managed, Key(room, Inputs with { ContextTags = ["preset=(default)", "cooker=native"] }));
        Assert.NotEqual(managed, Key(room));
    }

    /// <summary>
    /// The build is an input: the key carries <see cref="ToolIdentity.Current"/>
    /// among its parts, under the room stage's name, so another build's key
    /// differs even for the same room.
    /// </summary>
    [Fact]
    public void TheKeyCarriesTheBuildAndTheStage()
    {
        LibraryRoom room = Split(Library())[0];
        CacheKey key = RoomCacheKey.Of(room, Inputs);
        Assert.Equal(RoomCacheKey.StageName, key.Stage);
        Assert.Equal(ToolIdentity.Current, key.ToolId);
        Assert.Contains(("tool", ToolIdentity.Current), key.Parts);
        Assert.NotEqual(key.Digest, (key with { ToolId = key.ToolId + " other" }).Digest);
    }

    /// <summary>The arguments are checked.</summary>
    [Fact]
    public void NullArgumentsAreRefused()
    {
        LibraryRoom room = Split(Library())[0];
        Assert.Throws<ArgumentNullException>(() => RoomCacheKey.Of(null!, Inputs));
        Assert.Throws<ArgumentNullException>(() => RoomCacheKey.Of(room, null!));
        Assert.Throws<ArgumentNullException>(() => RoomCacheKey.RoomDigest(null!));
        Assert.Throws<ArgumentNullException>(() => RoomCacheKey.OptionsDigestOf(null!));
    }

    // ---- helpers ----------------------------------------------------------------

    /// <summary>A different value of an option's type, or a failed fact for a type it does not know.</summary>
    private static object? Vary(Type type, object? value) => value switch
    {
        bool b => !b,
        float f => f + 1f,
        int i => i + 1,
        BspBlockGrid => BspBlockGrid.Single(1, 1),
        ComplianceOptions => ComplianceOptions.Stock,
        FormatOptions format => format with { BspVersion = format.BspVersion + 1 },
        Enum e => Enum.GetValues(type).Cast<object>().First(v => !v.Equals(e)),
        null when type == typeof(string) => "x",
        string s => s + "x",
        _ => throw new InvalidOperationException($"the fact does not know how to vary a {type.Name}; add it"),
    };

    private static string Key(LibraryRoom room, RoomCacheInputs? inputs = null) => RoomCacheKey.Of(room, inputs ?? Inputs).Digest;

    private static string[] Keys(VmfDocument library) => [.. Split(library).Select(r => Key(r))];

    private static IReadOnlyList<LibraryRoom> Split(VmfDocument library) => RoomLibraryVmf.Split(library);

    /// <summary>The test library: the two rooms, each with an <c>info_target</c> of its own.</summary>
    internal static VmfDocument Library()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Definitions);
        for (int i = 0; i < Definitions.Length; i++)
        {
            float x = (i * (RoomHarness.Cell + RoomHarness.LibraryGap)) + 64f;
            VmfChunk target = new(MapFileLoader.EntityChunk);
            target.AddKey("id", (700000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.AddKey("classname", "info_target");
            target.AddKey("targetname", "target" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.AddKey("origin", VmfPlacement.Format(new SourceSharp.MapFormats.Geometry.Vec3(x, 64f, 64f)));
            int marker = library.Chunks.IndexOf(library.Chunks.First(c => c.GetValue(RoomLibraryVmf.NameKey) == Definitions[i].Name));
            library.Chunks.Insert(marker, target);
        }

        return library;
    }

    private static VmfChunk Target(VmfDocument library, int room) =>
        library.Chunks.Single(c => c.GetValue("targetname") == "target" + room.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static IEnumerable<VmfChunk> RoomSolids(VmfDocument library, int room)
    {
        float low = room * (RoomHarness.Cell + RoomHarness.LibraryGap);
        return library.GetChunk("world")!.GetChunks("solid").Where(s => VmfPlacement.Bounds(s).Mins.X >= low - 1f && VmfPlacement.Bounds(s).Maxs.X <= low + RoomHarness.Cell + 1f);
    }

    private static VmfDocument WithVersion(VmfDocument library, string version)
    {
        VmfChunk info = new("versioninfo");
        info.AddKey("editorversion", "400");
        info.AddKey("mapversion", version);
        library.Chunks.Insert(0, info);
        library.GetChunk("world")!.AddKey("mapversion", version);
        return library;
    }
}
