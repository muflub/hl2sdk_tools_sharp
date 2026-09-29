//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A library's settings (<see cref="RoomLibraryOptions"/>): the entity
/// reserve read from its worldspawn, the <c>LOPT</c> library section that
/// carries it to the link, and the key kept out of every room and out of
/// the flattened level.
/// </summary>
public sealed class RoomLibraryOptionsTests
{
    /// <summary>A library without the key sets nothing and writes no section.</summary>
    [Fact]
    public void ALibraryWithoutTheKeySetsNothing()
    {
        RoomLibraryOptions options = RoomLibraryOptions.FromWorld(World());
        Assert.Null(options.EntityReserve);
        Assert.Equal(RoomLibraryOptions.None, options);
        Assert.Null(options.ToSection());
    }

    /// <summary>The key is read ignoring its case, as entity keys are; the first of two wins.</summary>
    [Theory]
    [InlineData("rooms_entity_reserve", "0", 0)]
    [InlineData("ROOMS_ENTITY_RESERVE", "700", 700)]
    [InlineData("rooms_entity_reserve", "2048", 2048)]
    public void TheReserveIsReadFromTheWorldspawn(string key, string value, int expected)
    {
        Assert.Equal(expected, RoomLibraryOptions.FromWorld(World((key, value), (key, "5"))).EntityReserve);
    }

    /// <summary>A reserve that is not a whole number of edicts from 0 to the cap is refused, naming the key and the value.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("2049")]
    [InlineData("1.5")]
    [InlineData(" 12")]
    [InlineData("many")]
    [InlineData("")]
    public void AReserveOutOfRangeIsRefused(string value)
    {
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(
            () => RoomLibraryOptions.FromWorld(World((RoomLibraryOptions.EntityReserveKey, value))));
        Assert.Equal(
            $"the library's rooms_entity_reserve \"{value}\" is not a whole number of edicts from 0 to 2048.",
            refused.Message);
        Assert.False(RoomLibraryOptions.TryParseReserve(value, out int reserve));
        Assert.Equal(0, reserve);
    }

    /// <summary>Only the one key is a library key.</summary>
    [Fact]
    public void OnlyTheReserveKeyIsALibraryKey()
    {
        Assert.True(RoomLibraryOptions.IsLibraryKey("rooms_entity_reserve"));
        Assert.True(RoomLibraryOptions.IsLibraryKey("Rooms_Entity_Reserve"));
        Assert.False(RoomLibraryOptions.IsLibraryKey("skyname"));
        Assert.False(RoomLibraryOptions.IsLibraryKey("rooms_entity"));
        Assert.Throws<ArgumentNullException>(() => RoomLibraryOptions.FromWorld(null!));
    }

    /// <summary>The section's bytes, pinned: codec 0, the payload's length, the revision, one key and its value.</summary>
    [Fact]
    public void TheSectionsBytesArePinned()
    {
        RoomPackSectionData section = new RoomLibraryOptions(700).ToSection()!.Value;
        Assert.Equal(RoomLibraryOptions.SectionTag, section.Tag);
        string key = Convert.ToHexString(Encoding.UTF8.GetBytes("rooms_entity_reserve"));
        Assert.Equal(
            Convert.FromHexString("00" + "0000000000000027" + "00000001" + "00000001" + "00000014" + key + "00000003" + "373030"),
            section.Bytes.ToArray());
        Assert.Equal(new RoomLibraryOptions(700), RoomLibraryOptions.Read(section.Bytes.Span));
    }

    /// <summary>
    /// A key this build does not know is skipped (so a later build can add
    /// keys), and a section of another revision reads as no settings.
    /// </summary>
    [Fact]
    public void UnknownKeysAreSkippedAndUnknownRevisionsReadAsNone()
    {
        Assert.Equal(
            new RoomLibraryOptions(9),
            RoomLibraryOptions.Read(Section(1, ("rooms_fold_relays", "0"), ("rooms_entity_reserve", "9"))));
        Assert.Equal(RoomLibraryOptions.None, RoomLibraryOptions.Read(Section(1, ("rooms_fold_relays", "0"))));
        Assert.Equal(RoomLibraryOptions.None, RoomLibraryOptions.Read(Section(2, ("rooms_entity_reserve", "9"))));
    }

    /// <summary>A damaged section is refused, naming the section and what is wrong.</summary>
    [Theory]
    [InlineData("short", "is 3 bytes, shorter than its 9-byte header.")]
    [InlineData("codec", "has codec 1; this build reads codec 0 (none).")]
    [InlineData("length", "says 99 bytes of settings but holds 8.")]
    [InlineData("count", "claims -1 settings.")]
    [InlineData("cut", "is cut short.")]
    [InlineData("text", "is cut short.")]
    [InlineData("trailing", "has 1 bytes after its last setting.")]
    [InlineData("value", "sets rooms_entity_reserve to \"4000\", not a whole number from 0 to 2048.")]
    public void ADamagedSectionIsRefused(string fault, string expected)
    {
        byte[] bytes = fault switch
        {
            "short" => [0, 0, 0],
            "codec" => Patch(Section(1), b => b[0] = 1),
            "length" => Patch(Section(1), b => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(1), 99)),
            "count" => Raw(Int(1), Int(-1)),
            "cut" => Raw(Int(1)),
            "text" => Raw(Int(1), Int(1), Int(50)),
            "trailing" => Raw(Int(1), Int(0), [7]),
            "value" => Section(1, ("rooms_entity_reserve", "4000")),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        LinkException refused = Assert.Throws<LinkException>(() => RoomLibraryOptions.Read(bytes));
        Assert.Equal($"the room pack's LOPT section {expected}", refused.Message);
    }

    /// <summary>
    /// The split reads the setting and keeps the key out of every room's
    /// worldspawn, leaving the other keys; the flatten keeps it out of the
    /// flattened level's worldspawn the same way. A library without it
    /// splits and flattens with every key, as before.
    /// </summary>
    [Fact]
    public void TheKeyStaysOutOfTheRoomsAndTheFlattenedLevel()
    {
        RoomDefinition hub = Hub;
        VmfDocument library = RoomHarness.LibraryVmf(hub);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        world.AddKey("skyname", "sky_day01_01");
        world.AddKey(RoomLibraryOptions.EntityReserveKey, "640");

        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        Assert.Equal(640, split.Options.EntityReserve);
        VmfChunk roomWorld = split.Rooms[0].Document.GetChunk(MapFileLoader.WorldChunk)!;
        Assert.DoesNotContain(roomWorld.Keys, k => RoomLibraryOptions.IsLibraryKey(k.Name));
        Assert.Contains(roomWorld.Keys, k => k.Name == "skyname");

        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub"), "one");
        VmfChunk flatWorld = LevelFlattener.Flatten(level, library).GetChunk(MapFileLoader.WorldChunk)!;
        Assert.DoesNotContain(flatWorld.Keys, k => RoomLibraryOptions.IsLibraryKey(k.Name));
        Assert.Contains(flatWorld.Keys, k => k.Name == "skyname");

        VmfDocument plain = RoomHarness.LibraryVmf(hub);
        Assert.Equal(RoomLibraryOptions.None, RoomLibraryVmf.SplitLibrary(plain).Options);
    }

    /// <summary>A library whose reserve is out of range does not split.</summary>
    [Fact]
    public void ALibraryWithABadReserveDoesNotSplit()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        library.GetChunk(MapFileLoader.WorldChunk)!.AddKey(RoomLibraryOptions.EntityReserveKey, "all");
        Assert.Equal(
            "the library's rooms_entity_reserve \"all\" is not a whole number of edicts from 0 to 2048.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);
    }

    /// <summary>
    /// The pack reader finds the settings among the library sections, after
    /// the library-wide entities, on a stream that seeks and on one that
    /// only reads forward; a pack without them reads as no settings.
    /// </summary>
    [Fact]
    public async Task ThePackReaderFindsTheSettings()
    {
        RoomPackSectionData entities = RoomLibraryEntities.ToSection([]);
        RoomPackSectionData options = new RoomLibraryOptions(333).ToSection()!.Value;
        byte[] with = await PackAsync([entities, options]);
        byte[] without = await PackAsync([entities]);

        foreach (Func<byte[], Stream> open in new Func<byte[], Stream>[] { b => new MemoryStream(b), b => new ForwardOnly(b) })
        {
            await using (Stream stream = open(with))
            {
                RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
                Assert.Equal(new RoomLibraryOptions(333), await RoomPack.ReadLibraryOptionsAsync(stream, index));
            }

            await using (Stream stream = open(without))
            {
                RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
                Assert.Equal(RoomLibraryOptions.None, await RoomPack.ReadLibraryOptionsAsync(stream, index));
            }
        }
    }

    private static async Task<byte[]> PackAsync(RoomPackSectionData[] librarySections)
    {
        using MemoryStream stream = new();
        await RoomPack.SaveAsync(librarySections, [], stream, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>A stream that reads forward and cannot seek or say its length, like a pipe.</summary>
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static VmfChunk World(params (string Key, string Value)[] keys)
    {
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        foreach ((string key, string value) in keys)
        {
            world.AddKey(key, value);
        }

        return world;
    }

    private static byte[] Section(int revision, params (string Key, string Value)[] keys)
    {
        List<byte> payload = [.. Int(revision), .. Int(keys.Length)];
        foreach ((string key, string value) in keys)
        {
            payload.AddRange(Text(key));
            payload.AddRange(Text(value));
        }

        return Raw([.. payload]);
    }

    private static byte[] Raw(params byte[][] parts)
    {
        byte[] payload = [.. parts.SelectMany(p => p)];
        byte[] bytes = new byte[9 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), payload.Length);
        payload.CopyTo(bytes, 9);
        return bytes;
    }

    private static byte[] Int(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Text(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return [.. Int(bytes.Length), .. bytes];
    }

    private static byte[] Patch(byte[] bytes, Action<byte[]> patch)
    {
        patch(bytes);
        return bytes;
    }

    // ---- the naming settings -------------------------------------------------

    /// <summary>
    /// The fold switch and the library's name keys are read off the
    /// worldspawn (first of a repeated key wins; entries trimmed, empty ones
    /// dropped), are library keys, and travel in the section with the
    /// reserve, keys in ordinal order; the fold is on unless the library
    /// turns it off.
    /// </summary>
    [Fact]
    public void TheNamingSettingsAreReadWrittenAndReadBack()
    {
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        world.AddKey("ROOMS_FOLD_LOGIC", "0");
        world.AddKey(RoomLibraryOptions.FoldLogicKey, "1");
        world.AddKey(RoomLibraryOptions.NameKeysKey, " friend , ,enemy");
        world.AddKey(RoomLibraryOptions.EntityReserveKey, "300");
        RoomLibraryOptions options = RoomLibraryOptions.FromWorld(world);
        Assert.Equal(new RoomLibraryOptions(300) { FoldLogic = false, NameKeys = "friend,enemy" }, options);
        Assert.False(options.Folds);
        Assert.True(options.NameKeySet!.Contains("FRIEND"));
        Assert.True(RoomLibraryOptions.None.Folds);
        Assert.Null(RoomLibraryOptions.None.NameKeySet);
        Assert.True(RoomLibraryOptions.IsLibraryKey("Rooms_Name_Keys"));
        Assert.True(RoomLibraryOptions.IsLibraryKey(RoomLibraryOptions.FoldLogicKey));

        RoomPackSectionData section = options.ToSection()!.Value;
        Assert.Equal(options, RoomLibraryOptions.Read(section.Bytes.Span));
        string text = Encoding.UTF8.GetString(section.Bytes.Span);
        Assert.True(text.IndexOf("rooms_entity_reserve", StringComparison.Ordinal) < text.IndexOf("rooms_fold_logic", StringComparison.Ordinal));
        Assert.True(text.IndexOf("rooms_fold_logic", StringComparison.Ordinal) < text.IndexOf("rooms_name_keys", StringComparison.Ordinal));

        RoomLibraryOptions foldOnly = new() { FoldLogic = true };
        Assert.Equal(foldOnly, RoomLibraryOptions.Read(foldOnly.ToSection()!.Value.Bytes.Span));
        RoomLibraryOptions namesOnly = new() { NameKeys = "a" };
        Assert.Equal(namesOnly, RoomLibraryOptions.Read(namesOnly.ToSection()!.Value.Bytes.Span));

        world.AddKey(RoomLibraryOptions.NameKeysKey, "ignored");
        Assert.Equal("friend,enemy", RoomLibraryOptions.FromWorld(world).NameKeys);
        VmfChunk empty = new(MapFileLoader.WorldChunk);
        empty.AddKey(RoomLibraryOptions.NameKeysKey, " , ");
        Assert.Equal(RoomLibraryOptions.None, RoomLibraryOptions.FromWorld(empty));
    }

    /// <summary>A fold switch other than 0 or 1 is refused, off the worldspawn and in the section.</summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("2")]
    [InlineData("")]
    public void ABadFoldSwitchIsRefused(string value)
    {
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey(RoomLibraryOptions.FoldLogicKey, value);
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => RoomLibraryOptions.FromWorld(world));
        Assert.Equal($"the library's rooms_fold_logic \"{value}\" is not 0 or 1.", refused.Message);

        byte[] payload = [.. Int(RoomLibraryOptions.Revision), .. Int(1), .. Text(RoomLibraryOptions.FoldLogicKey), .. Text(value)];
        byte[] section = [RoomLibraryOptions.CodecNone, .. Long(payload.Length), .. payload];
        Assert.Contains($"sets rooms_fold_logic to \"{value}\", not 0 or 1", Assert.Throws<LinkException>(() => RoomLibraryOptions.Read(section)).Message, StringComparison.Ordinal);
        Assert.False(RoomLibraryOptions.TryParseFold(value, out _));
    }

    /// <summary>
    /// The door portal switch (open point O10) is read off the worldspawn
    /// (first of a repeated key wins), is a library key, and travels in the
    /// section in ordinal order; door portals are off unless the library
    /// turns them on, and a switch other than 0 or 1 is refused, off the
    /// worldspawn and in the section.
    /// </summary>
    [Fact]
    public void TheDoorPortalSwitchIsReadWrittenAndReadBack()
    {
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("Rooms_Door_Portals", "1");
        world.AddKey(RoomLibraryOptions.DoorPortalsKey, "0");
        world.AddKey(RoomLibraryOptions.EntityReserveKey, "300");
        RoomLibraryOptions options = RoomLibraryOptions.FromWorld(world);
        Assert.Equal(new RoomLibraryOptions(300) { DoorPortals = true }, options);
        Assert.True(options.HasDoorPortals);
        Assert.False(RoomLibraryOptions.None.HasDoorPortals);
        Assert.False(new RoomLibraryOptions { DoorPortals = false }.HasDoorPortals);
        Assert.True(RoomLibraryOptions.IsLibraryKey("ROOMS_DOOR_PORTALS"));

        RoomPackSectionData section = options.ToSection()!.Value;
        Assert.Equal(options, RoomLibraryOptions.Read(section.Bytes.Span));
        string text = Encoding.UTF8.GetString(section.Bytes.Span);
        Assert.True(text.IndexOf("rooms_door_portals", StringComparison.Ordinal) < text.IndexOf("rooms_entity_reserve", StringComparison.Ordinal));
        RoomLibraryOptions off = new() { DoorPortals = false };
        Assert.Equal(off, RoomLibraryOptions.Read(off.ToSection()!.Value.Bytes.Span));

        VmfChunk bad = new(MapFileLoader.WorldChunk);
        bad.AddKey(RoomLibraryOptions.DoorPortalsKey, "yes");
        Assert.Equal(
            "the library's rooms_door_portals \"yes\" is not 0 or 1.",
            Assert.Throws<RoomLibraryException>(() => RoomLibraryOptions.FromWorld(bad)).Message);
        byte[] payload = [.. Int(RoomLibraryOptions.Revision), .. Int(1), .. Text(RoomLibraryOptions.DoorPortalsKey), .. Text("yes")];
        byte[] damaged = [RoomLibraryOptions.CodecNone, .. Long(payload.Length), .. payload];
        Assert.Contains(
            "sets rooms_door_portals to \"yes\", not 0 or 1",
            Assert.Throws<LinkException>(() => RoomLibraryOptions.Read(damaged)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The library worldspawn's first <c>mapversion</c> is kept as written,
    /// in the section and back out of it; it is not a library-only key, so
    /// the flattened level keeps it; a library without one keeps none.
    /// </summary>
    [Fact]
    public void TheMapVersionIsKeptInTheSettings()
    {
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("MapVersion", "17");
        world.AddKey("mapversion", "18");
        RoomLibraryOptions options = RoomLibraryOptions.FromWorld(world);
        Assert.Equal("17", options.MapVersion);
        Assert.Equal("17", RoomLibraryOptions.Read(options.ToSection()!.Value.Bytes.Span).MapVersion);
        Assert.False(RoomLibraryOptions.IsLibraryKey(RoomLibraryOptions.MapVersionKey));
        Assert.Null(RoomLibraryOptions.FromWorld(new VmfChunk(MapFileLoader.WorldChunk)).MapVersion);
        Assert.Null(RoomLibraryOptions.Read((options with { MapVersion = null, EntityReserve = 1 }).ToSection()!.Value.Bytes.Span).MapVersion);
    }

    /// <summary>
    /// The split gives every room <c>mapversion</c> 0, in its worldspawn at
    /// the library key's own place and in its <c>versioninfo</c>, and leaves
    /// the library's document as it was.
    /// </summary>
    [Fact]
    public void EveryRoomIsSplitWithMapVersionZero()
    {
        VmfDocument library = RoomHarness.LibraryVmf(RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX));
        VmfChunk info = new("versioninfo");
        info.AddKey("mapversion", "9");
        library.Chunks.Insert(0, info);
        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        VmfChunk rebuilt = new(MapFileLoader.WorldChunk);
        rebuilt.AddKey("id", "1");
        rebuilt.AddKey("mapversion", "9");
        foreach (VmfNode node in world.Children.Skip(1))
        {
            rebuilt.Children.Add(node);
        }

        library.Chunks[library.Chunks.IndexOf(world)] = rebuilt;
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        VmfChunk roomWorld = split.Rooms[0].Document.GetChunk(MapFileLoader.WorldChunk)!;
        Assert.Equal(["id", "mapversion", "classname"], roomWorld.Keys.Select(k => k.Name));
        Assert.Equal("0", roomWorld.GetValue("mapversion"));
        Assert.Equal("0", split.Rooms[0].Document.GetChunk("versioninfo")!.GetValue("mapversion"));
        Assert.Equal("9", split.Options.MapVersion);
        Assert.Equal("9", rebuilt.GetValue("mapversion"));
        Assert.Equal("9", info.GetValue("mapversion"));
    }

    private static byte[] Long(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
