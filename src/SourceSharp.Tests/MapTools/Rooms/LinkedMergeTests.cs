//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The per-map lumps the linker merges rather than relocates: the entity
/// list and the world collision's keydata, material table and ledges.
/// </summary>
public sealed class LinkedMergeTests
{
    // ---- entities -----------------------------------------------------------

    /// <summary>
    /// A moved entity's origin goes through the placement, its angles' yaw
    /// and its <c>angle</c> turn with it, and the -1 / -2 "up" / "down"
    /// codes of <c>angle</c> are not angles and stay.
    /// </summary>
    [Fact]
    public void AMovedEntityTurnsItsYawAndMovesItsOrigin()
    {
        RoomTransform turned = new(new RoomPlacement("r", 1, 0, 1), 256);
        BspEntity light = Entity(("classname", "light"), ("origin", "10 20 30"), ("angles", "-45 300 0"), ("angle", "90"), ("_light", "1 2 3"));
        BspEntity moved = LevelLinker.MoveEntity(light, turned, "r");

        Assert.Equal("492 10 30", moved.Get("origin"));
        Assert.Equal("-45 30 0", moved.Get("angles"));
        Assert.Equal("180", moved.Get("angle"));
        Assert.Equal("1 2 3", moved.Get("_light"));

        Assert.Equal("-1", LevelLinker.MoveEntity(Entity(("angle", "-1")), turned, "r").Get("angle"));
        Assert.Equal("-2", LevelLinker.MoveEntity(Entity(("angle", "-2")), turned, "r").Get("angle"));
    }

    /// <summary>An unturned placement keeps the angle text as written.</summary>
    [Fact]
    public void AnUnturnedEntityKeepsItsAnglesVerbatim()
    {
        RoomTransform moved = new(new RoomPlacement("r", 2, 3, 0), 256);
        BspEntity e = LevelLinker.MoveEntity(Entity(("origin", "1 2 3"), ("angles", "0 90.000 0"), ("angle", "45.0")), moved, "r");

        Assert.Equal("513 770 3", e.Get("origin"));
        Assert.Equal("0 90.000 0", e.Get("angles"));
        Assert.Equal("45.0", e.Get("angle"));
    }

    /// <summary>An origin or angle that is not numbers is refused naming the room and key.</summary>
    [Theory]
    [InlineData("origin", "1 2")]
    [InlineData("origin", "1 two 3")]
    [InlineData("angle", "east")]
    public void AnUnreadablePlacementKeyIsRefused(string key, string value)
    {
        RoomTransform turned = new(new RoomPlacement("r", 0, 0, 1), 256);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(Entity((key, value)), turned, "attic"));
        Assert.Contains($"room attic has an entity whose \"{key}\"", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two rooms whose worldspawns disagree on a map-wide key are refused:
    /// the linked map has one worldspawn and would silently keep one room's.
    /// </summary>
    [Fact]
    public async Task WorldspawnsThatDisagreeAreRefused()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub(), RoomHarness.Room("sky",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY));
        RoomObject sky = RoomHarness.WithLumps(compiled.Get("sky"), bsp =>
        {
            List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
            entities[0].Pairs.Add(new BspKeyValue("skyname", "sky_day01_01"));
            bsp[BspLump.Entities] = EntityLump.Write(entities);
        });
        RoomLibrary library = RoomHarness.Library(compiled.Get("hub"), sky);
        LevelLayout layout = RoomHarness.AutoLayout("two", library, ("hub", 0, 0, 0), ("sky", 1, 0, 0));

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync()));
        Assert.Contains("room sky's worldspawn keys disagree with room hub's", refused.Message, StringComparison.Ordinal);
    }

    // ---- collision keydata ---------------------------------------------------

    /// <summary>The world keydata's three block kinds read as the cooker writes them.</summary>
    [Fact]
    public void TheWorldKeydataReads()
    {
        (List<(int Index, int Contents)> statics, List<string> materials, bool terrain) = LevelLinker.ParseKeyData(
            "staticsolid {\n\"index\" \"0\"\n\"contents\" \"1\"\n}\nstaticsolid {\n\"index\" \"1\"\n\"contents\" \"65536\"\n}\n"
            + "virtualterrain {}\nmaterialtable {\n\"metal\" \"2\"\n\"default\" \"1\"\n}\n\0",
            "r");

        Assert.Equal([(0, 1), (1, 65536)], statics);
        Assert.Equal(["default", "metal"], materials);
        Assert.True(terrain);
    }

    /// <summary>Anything else in the keydata is refused naming the block or the fault.</summary>
    [Theory]
    [InlineData("solid {\n\"index\" \"0\"\n}\n", "has a \"solid\" block")]
    [InlineData("staticsolid\n", "\"staticsolid\" without a block")]
    [InlineData("staticsolid {\n\"index\" \"0\"\n", "block \"staticsolid\" is not closed")]
    [InlineData("staticsolid {\n\"index\" }\n", "has a key without a value")]
    [InlineData("staticsolid {\n\"index\" \"0\"\n}\n", "has no \"contents\"")]
    [InlineData("staticsolid {\n\"index\" \"zero\"\n\"contents\" \"1\"\n}\n", "\"index\" is \"zero\", not an integer")]
    [InlineData("staticsolid {\n\"index\n", "unclosed quote")]
    [InlineData("materialtable {\n\"a\" \"2\"\n}\n", "gives \"a\" slot 2 of 1")]
    [InlineData("materialtable {\n\"a\" \"1\"\n\"b\" \"1\"\n}\n", "gives \"b\" slot 1 of 2")]
    public void UnknownKeydataIsRefused(string text, string expected)
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.ParseKeyData(text, "r"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The linked material table is first-seen order, a name already in it
    /// keeps its slot, and past 126 names a material gets 0 as the cooker's
    /// own table does.
    /// </summary>
    [Fact]
    public void TheMaterialTableMergesByNameAndCapsAt126()
    {
        List<string> table = [];
        Assert.Equal(1, LevelLinker.MaterialIndex(table, "default"));
        Assert.Equal(2, LevelLinker.MaterialIndex(table, "metal"));
        Assert.Equal(1, LevelLinker.MaterialIndex(table, "default"));
        for (int i = table.Count; i < 126; i++)
        {
            LevelLinker.MaterialIndex(table, "m" + i);
        }

        Assert.Equal(0, LevelLinker.MaterialIndex(table, "one-too-many"));
        Assert.Equal(126, table.Count);
    }

    /// <summary>
    /// Two rooms whose collision tables name their materials in different
    /// orders link into one table, and each triangle's material index names
    /// the same material after the merge as before it.
    /// </summary>
    [Fact]
    public async Task TriangleMaterialsFollowTheirNamesIntoTheLinkedTable()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(true, RoomHarness.Hub(), RoomHarness.Room("other",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY));

        // The second room's table becomes { "metal", "default" }: its
        // triangles that said 1 ("default") must now say 2 in its own
        // table, and the link must map them back to the first room's 1.
        RoomObject other = RoomHarness.WithLumps(compiled.Get("other"), bsp =>
        {
            PhysCollideModel model = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span)[0];
            List<byte[]> solids = [];
            foreach (byte[] blob in model.Solids)
            {
                byte[] copy = (byte[])blob.Clone();
                SetEveryMaterial(copy, 2);
                solids.Add(copy);
            }

            string key = model.KeyText.Replace("\"default\" \"1\"", "\"metal\" \"1\"\n\"default\" \"2\"", StringComparison.Ordinal);
            byte[] keyData = [.. System.Text.Encoding.Latin1.GetBytes(key), 0];
            bsp.SetLump(BspLump.PhysCollide, PhysCollideLump.Write([new PhysCollideModel(0, solids, keyData)]));
        });
        RoomLibrary library = RoomHarness.Library(compiled.Get("hub"), other);
        LinkedLevel link = await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("two", library, ("hub", 0, 0, 0), ("other", 1, 0, 0)), library, await RoomHarness.ContextAsync());

        PhysCollideModel linked = PhysCollideLump.Read(link.Bsp[BspLump.PhysCollide].Data.Span)[0];
        Assert.Contains("\"default\" \"1\"", linked.KeyText, StringComparison.Ordinal);
        Assert.Contains("\"metal\" \"2\"", linked.KeyText, StringComparison.Ordinal);
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(linked.Solids[0])))
        {
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                Assert.Equal(1u, (ledge.TriangleWord(t) >> 24) & 0x7f);
            }
        }
    }

    /// <summary>A triangle naming a material its room's table does not have is refused.</summary>
    [Fact]
    public async Task AMaterialPastTheRoomsTableIsRefused()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(true, RoomHarness.Hub());
        RoomObject broken = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            PhysCollideModel model = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span)[0];
            byte[] blob = (byte[])model.Solids[0].Clone();
            SetEveryMaterial(blob, 9);
            bsp.SetLump(BspLump.PhysCollide, PhysCollideLump.Write([new PhysCollideModel(0, [blob], model.KeyData)]));
        });
        RoomLibrary library = RoomHarness.Library(broken);

        LinkException refused = await Assert.ThrowsAsync<LinkException>(async () => await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("one", library, ("hub", 0, 0, 0)), library, await RoomHarness.ContextAsync()));
        Assert.Contains("uses material 9, but its table names 1", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A collision lump that is not one model-0 record of compact surfaces is refused.</summary>
    [Theory]
    [InlineData("two records")]
    [InlineData("not a surface")]
    [InlineData("unnamed solid")]
    [InlineData("not framed")]
    public async Task AnUnexpectedCollisionLumpIsRefused(string fault)
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(true, RoomHarness.Hub());
        RoomObject broken = RoomHarness.WithLumps(compiled.Get("hub"), bsp =>
        {
            PhysCollideModel model = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span)[0];
            byte[] lump = fault switch
            {
                "two records" => PhysCollideLump.Write([model, model with { ModelIndex = 1 }]),
                "not a surface" => PhysCollideLump.Write([model with { Solids = [new byte[40]] }]),
                "unnamed solid" => PhysCollideLump.Write([model with { Solids = [model.Solids[0], model.Solids[0]] }]),
                _ => new byte[8],
            };
            bsp.SetLump(BspLump.PhysCollide, lump);
        });
        RoomLibrary library = RoomHarness.Library(broken);

        LinkException refused = await Assert.ThrowsAsync<LinkException>(async () => await LevelLinker.LinkAsync(
            RoomHarness.AutoLayout("one", library, ("hub", 0, 0, 0)), library, await RoomHarness.ContextAsync()));
        Assert.Contains(
            fault switch
            {
                "two records" => "has 2 records",
                "not a surface" => "solid 0 is not a compact surface",
                "unnamed solid" => "solid 1 has no staticsolid block",
                _ => "is not a collision lump",
            },
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A quarter turn moves a ledge point in IVP's axes as the map point
    /// turns: the map's (X, Y, Z) is IVP's (X, -Z, Y) in metres, so the turn
    /// acts on (x, z) and the translation lands on x and z.
    /// </summary>
    [Fact]
    public void ALedgePointMovesAsItsMapPointDoes()
    {
        IvpCompactLedge ledge = IvpCompactLedge.Create(0, 1);
        Vec3 map = new(10, 20, 30);
        ledge.SetPoint(0, map.X * 0.0254f, -map.Z * 0.0254f, map.Y * 0.0254f);
        RoomTransform transform = new(new RoomPlacement("r", 1, 2, 1), 256);

        LevelLinker.MoveLedge(ledge, transform);

        Vec3 expected = transform.Apply(map);
        (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, 0);
        Assert.Equal(expected.X, x, 2);
        Assert.Equal(expected.Y, y, 2);
        Assert.Equal(expected.Z, z, 2);
    }

    // ---- helpers -----------------------------------------------------------

    private static BspEntity Entity(params (string Key, string Value)[] pairs)
    {
        BspEntity entity = new();
        foreach ((string key, string value) in pairs)
        {
            entity.Pairs.Add(new BspKeyValue(key, value));
        }

        return entity;
    }

    private static void SetEveryMaterial(byte[] vphy, int material)
    {
        int headerSize = VphyWriter.HeaderSize;
        byte[] surface = vphy[headerSize..];
        foreach (int at in IvpCollideQueries.LeafOffsets(surface))
        {
            IvpCompactLedge ledge = IvpCollideQueries.LedgeAt(surface, at);
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                int word = headerSize + at + 16 + (16 * t);
                uint value = BitConverter.ToUInt32(vphy, word);
                BitConverter.TryWriteBytes(vphy.AsSpan(word), (value & ~(0x7fu << 24)) | ((uint)material << 24));
            }
        }
    }
}
