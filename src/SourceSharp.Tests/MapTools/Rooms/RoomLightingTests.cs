//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The base bake at pack time (<see cref="RoomLighting"/>): the frame it
/// lights a room in, its once-or-per-turn storage (the rooms design, 1.1 and
/// 15.9), its linear values, its section, and what binds it to its room.
/// </summary>
public sealed class RoomLightingTests(LitRoomsFixture fixture) : IClassFixture<LitRoomsFixture>
{
    // ---- the frame -----------------------------------------------------------------------------

    /// <summary>
    /// A world direction in a turned room's frame is the placement's turn
    /// undone, exactly: turning it back with the placement's turn gives the
    /// same floats, at every turn; turn 0 is the identity, and a table turns
    /// element by element.
    /// </summary>
    [Fact]
    public void TheBakeFrameIsThePlacementsTurnUndone()
    {
        Vec3[] directions = [new(1, 0, 0), new(0.6f, -0.8f, 0), new(0.1f, 0.2f, -0.97f), new(-0.3f, 0.5f, 0.81f)];
        for (int turns = 0; turns < 4; turns++)
        {
            foreach (Vec3 d in directions)
            {
                Vec3 room = BakeFrame.ToRoom(d, turns);
                Vec3 back = RoomTransform.Rotate(room, turns);
                Assert.Equal(d.X, back.X);
                Assert.Equal(d.Y, back.Y);
                Assert.Equal(d.Z, back.Z);
            }

            Assert.Equal(directions.Select(d => BakeFrame.ToRoom(d, turns)), BakeFrame.ToRoom(directions, turns));
        }

        Assert.Equal(directions[1], BakeFrame.ToRoom(directions[1], 0));
        Assert.Equal(BakeFrame.ToRoom(directions[2], 1), BakeFrame.ToRoom(directions[2], 5));
    }

    // ---- linear values -------------------------------------------------------------------------

    /// <summary>
    /// The stored values give back vrad's bytes (the rooms design, 9.3):
    /// every <c>ColorRGBExp32</c> vrad's encode writes (its largest channel's
    /// mantissa 128 to 255, the others anything up to it, exponents -24 to 8,
    /// and black) decodes to half floats that encode to the same four bytes.
    /// </summary>
    [Fact]
    public void EveryEncodedColourRoundTripsThroughHalfFloats()
    {
        List<ColorRgbExp32> colours = [new ColorRgbExp32()];
        for (int exponent = -24; exponent <= 8; exponent++)
        {
            for (int mantissa = 128; mantissa <= 255; mantissa++)
            {
                foreach (int other in (int[])[0, 1, 7, 64, 127, mantissa / 2, mantissa])
                {
                    colours.Add(new ColorRgbExp32 { R = (byte)mantissa, G = (byte)Math.Min(other, mantissa), B = (byte)(mantissa - Math.Min(other, mantissa)), Exponent = (sbyte)exponent });
                    colours.Add(new ColorRgbExp32 { R = (byte)Math.Min(other, mantissa), G = (byte)(mantissa - Math.Min(other, mantissa)), B = (byte)mantissa, Exponent = (sbyte)exponent });
                }
            }
        }

        // Only the canonical encodings: what vrad writes.
        colours = [.. colours.Where(c => StockLightColor.Encode(c.ToLinear()) is var e && LitCompare.Same(e, c))];
        Assert.True(colours.Count > 20000);
        ColorRgbExp32[] back = new ColorRgbExp32[colours.Count];
        RoomLighting.EncodeColors(RoomLighting.DecodeColors([.. colours]), back);
        Assert.Equal(colours, back);
    }

    /// <summary>Every luxel, cube face and vertex colour the fixture's bakes stored gives back the encoded bytes vrad wrote.</summary>
    [Fact]
    public void EveryStoredValueOfARealBakeRoundTrips()
    {
        foreach (RoomObject room in fixture.Lit.Rooms)
        {
            foreach (RoomLightingPayload payload in room.Lighting!.Payloads)
            {
                RoomLightRange range = payload.Ldr!;
                ColorRgbExp32[] encoded = new ColorRgbExp32[range.Luxels.Length / 3];
                RoomLighting.EncodeColors(range.Luxels, encoded);
                Assert.Equal(range.Luxels, RoomLighting.DecodeColors(encoded));
            }
        }
    }

    /// <summary>
    /// A stored ambient sample turned to a placement: the cube face for each
    /// world direction is the stored face for the room direction the turn
    /// takes to it (worked out here from the frame, independently), the
    /// vertical faces stay, and each position byte is the stored byte along
    /// the room axis the turn takes to that world axis, flipped where the axis
    /// is negated, so the byte names the same point of the turned box.
    /// </summary>
    [Fact]
    public void AnAmbientSampleTurnsItsFacesAndItsPosition()
    {
        Half[] cube = new Half[RoomLighting.AmbientHalves];
        for (int side = 0; side < 6; side++)
        {
            cube[side * 3] = (Half)(1 << side);
            cube[(side * 3) + 1] = (Half)(side + 1);
            cube[(side * 3) + 2] = (Half)0.5f;
        }

        RoomLightRange range = new([], [], [], [], [], cube, [10, 200, 30, 0], []);
        Vec3[] axes = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            DLeafAmbientLighting turned = LevelLinker.TurnSample(range, 0, rotation);
            for (int world = 0; world < 6; world++)
            {
                int room = Array.IndexOf(axes, BakeFrame.ToRoom(axes[world], rotation) is var d ? new Vec3(d.X + 0f, d.Y + 0f, d.Z + 0f) : default);
                Assert.Equal((float)(1 << room), turned.Cube.Color[world].ToLinear().X);
                Assert.Equal(room + 1f, turned.Cube.Color[world].ToLinear().Y);
            }

            // The point at the stored fractions, turned about the box's centre.
            Vec3 point = RoomTransform.Rotate(new Vec3((10 / 255f) - 0.5f, (200 / 255f) - 0.5f, 0), rotation);
            Assert.Equal((int)MathF.Round((point.X + 0.5f) * 255), turned.X);
            Assert.Equal((int)MathF.Round((point.Y + 0.5f) * 255), turned.Y);
            Assert.Equal(30, turned.Z);
        }
    }

    // ---- once or per turn (1.1, 15.9) ----------------------------------------------------------

    /// <summary>
    /// A room no sun or sky reaches stores one payload, one with a sky face
    /// in a library with a sun stores four; pass one's sky leaves are the sky
    /// room's leaves with a sky face, and the hub has none.
    /// </summary>
    [Fact]
    public void ASunlessRoomStoresOnePayloadAndASunlitOneFour()
    {
        RoomObject hub = fixture.Lit.Get("hub");
        RoomObject other = fixture.Lit.Get("other");
        Assert.False(RoomLighting.HasSkyFace(hub.Bsp));
        Assert.True(RoomLighting.HasSkyFace(other.Bsp));
        Assert.Equal(1, hub.Lighting!.RotationCount);
        Assert.Equal(4, other.Lighting!.RotationCount);
        Assert.Empty(RoomLighting.PassOne(hub.Bsp));
        Assert.Equal(RoomLighting.PassOne(other.Bsp), other.Lighting.SkyLeaves);
        Assert.NotEmpty(other.Lighting.SkyLeaves);
        Assert.All(other.Lighting.SkyLeaves, l => Assert.Equal(LeafFlags.Sky, l.Flags));
        Assert.True(hub.Lighting.HasSun);
        Assert.NotNull(hub.Lighting.SkyLdr);
        Assert.Equal(2, hub.Lighting.SkyLdr!.Length);
        Assert.Null(hub.Lighting.SkyHdr);
    }

    /// <summary>
    /// 15.9: the four bakes of a room no sun or sky reaches, computed, are
    /// the same bytes wherever the turn could enter: every face's styles,
    /// offsets and luxels, the world lights, the vertex normals; so one
    /// stored payload is the room at every turn. (Its leaf ambient and prop
    /// colours are drawn along vrad's world-fixed sampling directions, so a
    /// turned room's differ in the last bits; the capped-room facts hold
    /// the one stored copy, turned at link, to the turned room's own.)
    /// </summary>
    [Fact]
    public async Task TheFourBakesOfASunlessRoomAreTheSameBytes()
    {
        RoomObject hub = fixture.Lit.Get("hub");
        RoomLightingSettings settings = new(LitRoomsFixture.Options) { Sun = RoomLightingSettings.SunOf(fixture.Lit.LibraryEntities) };
        var context = await RoomLightHarness.ContextAsync("hub");
        RoomLightingTurn[] turns = new RoomLightingTurn[4];
        for (int turn = 0; turn < 4; turn++)
        {
            turns[turn] = await RoomLighting.BakeTurnAsync(hub, settings, context.Content!, new CompileParallelism { MaxDegree = 1 }, turn, CancellationToken.None);
        }

        foreach (RoomLightingTurn turn in turns[1..])
        {
            Assert.Equal(turns[0].Payload.Ldr!.Styles, turn.Payload.Ldr!.Styles);
            Assert.Equal(turns[0].Payload.Ldr!.LightOffsets, turn.Payload.Ldr!.LightOffsets);
            Assert.Equal(turns[0].Payload.Ldr!.Luxels, turn.Payload.Ldr!.Luxels);
            Assert.Equal(turns[0].Payload.Ldr!.Lights, turn.Payload.Ldr!.Lights);
            Assert.Equal(turns[0].VertNormals, turn.VertNormals);
            Assert.Equal(turns[0].VertNormalIndices, turn.VertNormalIndices);
        }
    }

    /// <summary>A bake lights copies: the room's own compile is the same bytes after it.</summary>
    [Fact]
    public async Task ABakeLeavesTheRoomsCompileAsItWas()
    {
        RoomObject other = fixture.Unlit.Get("other");
        string before = Digest(other.Bsp);
        RoomLightingSettings settings = new(RoomLightHarness.Options) { Sun = RoomLightingSettings.SunOf(fixture.Unlit.LibraryEntities) };
        var context = await RoomLightHarness.ContextAsync("other");
        RoomLighting lighting = await RoomLighting.BakeAsync(other, settings, context.Content!, new CompileParallelism { MaxDegree = 1 }, CancellationToken.None);
        Assert.Equal(before, Digest(other.Bsp));
        Assert.True(lighting.IsFor(other));
        Assert.False(lighting.IsFor(fixture.Lit.Get("other")));
        Assert.Null(other.LightingOfCompile);
        Assert.Same(lighting, (other with { Lighting = lighting }).LightingOfCompile);
    }

    private static string Digest(BspData bsp) =>
        Convert.ToHexString(SHA256.HashData([.. Enumerable.Range(0, BspData.HeaderLumps).SelectMany(i => bsp[i].Data.ToArray())]));

    // ---- settings --------------------------------------------------------------------------

    /// <summary>
    /// The settings refuse a luxel density below one (it rewrites the room's
    /// texture axes, which only its compile may), take the library's first
    /// sun, and describe themselves as a function of what shapes a bake.
    /// </summary>
    [Fact]
    public void SettingsRefuseADensityBelowOneAndDescribeWhatShapesTheBake()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new RoomLightingSettings(VradOptions.Default with { LuxelDensity = 0.5f }));
        Assert.StartsWith("-luxeldensity below 1 rewrites texture axes and face extents", refused.Message, StringComparison.Ordinal);

        VmfChunk sun = RoomLightHarness.Sun();
        VmfChunk fog = RoomPropHarness.Entity("env_fog_controller", 5, Vec3.Zero);
        Assert.Same(sun, RoomLightingSettings.SunOf([fog, sun, RoomLightHarness.Sun("0 90 0")]));
        Assert.Null(RoomLightingSettings.SunOf([fog]));

        string plain = new RoomLightingSettings(VradOptions.Default).Describe();
        Assert.Equal(plain, new RoomLightingSettings(VradOptions.Default).Describe());
        Assert.NotEqual(plain, new RoomLightingSettings(VradOptions.Default with { Bounces = 3 }).Describe());
        Assert.NotEqual(plain, new RoomLightingSettings(VradOptions.Default) { Sun = sun }.Describe());
        Assert.NotEqual(
            new RoomLightingSettings(VradOptions.Default) { Sun = sun }.Describe(),
            new RoomLightingSettings(VradOptions.Default) { Sun = RoomLightHarness.Sun("0 90 0") }.Describe());
    }

    /// <summary>
    /// A room's cache key changes with how it is lit, and an unlit library's
    /// keys are the ones it had before the bake existed (the lighting is
    /// folded only when set).
    /// </summary>
    [Fact]
    public void TheCacheKeyCarriesTheLighting()
    {
        RoomCacheInputs unlit = new(VbspOptions.Default);
        RoomCacheInputs lit = unlit with { Lighting = new RoomLightingSettings(VradOptions.Default) };
        RoomCacheInputs litOther = unlit with { Lighting = new RoomLightingSettings(VradOptions.Default with { Range = VradLightingRange.Both }) };
        string before = RoomCacheKey.OptionsDigestOf(unlit);
        Assert.Equal(before, RoomCacheKey.OptionsDigestOf(new RoomCacheInputs(VbspOptions.Default)));
        Assert.NotEqual(before, RoomCacheKey.OptionsDigestOf(lit));
        Assert.NotEqual(RoomCacheKey.OptionsDigestOf(lit), RoomCacheKey.OptionsDigestOf(litOther));
        Assert.Equal(RoomCacheKey.OptionsDigestOf(lit), RoomCacheKey.OptionsDigestOf(unlit with { Lighting = new RoomLightingSettings(VradOptions.Default) }));
    }

    // ---- the section -----------------------------------------------------------------------------

    /// <summary>
    /// The section reads back to the same lighting (written again, the same
    /// bytes), bound to the compile it is read with; a section of a revision
    /// this build does not read leaves the room unlit.
    /// </summary>
    [Theory]
    [InlineData("hub")]
    [InlineData("other")]
    public void TheSectionRoundTrips(string name)
    {
        RoomObject room = fixture.Lit.Get(name);
        byte[] section = room.Lighting!.ToSection().Bytes.ToArray();
        Assert.Equal((byte)0, section[0]);
        Assert.Equal(section.Length - 9, BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)));
        Assert.Equal(RoomLighting.Revision, BinaryPrimitives.ReadInt32BigEndian(section.AsSpan(9)));
        Assert.Equal(room.Lighting.RotationCount, BinaryPrimitives.ReadInt32BigEndian(section.AsSpan(13)));

        RoomLighting read = RoomLighting.Read(section, room.Definition, room.Bsp)!;
        Assert.Equal(section, read.ToSection().Bytes.ToArray());
        Assert.True(read.IsFor(room));
        Assert.Equal(room.Lighting.RotationCount, RoomLighting.ReadRotationCount(section, name));

        byte[] later = (byte[])section.Clone();
        BinaryPrimitives.WriteInt32BigEndian(later.AsSpan(9), RoomLighting.Revision + 1);
        Assert.Null(RoomLighting.Read(later, room.Definition, room.Bsp));
        Assert.Null(RoomLighting.ReadRotationCount(later, name));
        Assert.Null(RoomLighting.Read(null, room.Definition, room.Bsp));
    }

    /// <summary>
    /// A damaged section is refused naming the room and the section (15.9):
    /// an unknown codec, a decoded length other than the recorded one, a
    /// truncated body, a rotation count other than 1 or 4, and a section
    /// for another compile's faces.
    /// </summary>
    [Fact]
    public void ADamagedSectionIsRefusedByName()
    {
        RoomObject hub = fixture.Lit.Get("hub");
        byte[] section = hub.Lighting!.ToSection().Bytes.ToArray();

        byte[] codec = (byte[])section.Clone();
        codec[0] = 7;
        Assert.Equal("room hub: section LITE uses codec 7, which this build does not read.", Refused(codec));

        byte[] length = (byte[])section.Clone();
        BinaryPrimitives.WriteInt64BigEndian(length.AsSpan(1), section.Length);
        Assert.Equal($"room hub: section LITE decodes to {section.Length - 9} bytes, not the {section.Length} it records.", Refused(length));

        byte[] cut = section[..(section.Length - 10)];
        BinaryPrimitives.WriteInt64BigEndian(cut.AsSpan(1), cut.Length - 9);
        Assert.Equal("room pack entry \"hub\": its \"LITE\" section is truncated.", Refused(cut));

        byte[] turns = (byte[])section.Clone();
        BinaryPrimitives.WriteInt32BigEndian(turns.AsSpan(13), 3);
        Assert.Equal("room pack entry \"hub\": its \"LITE\" section holds a rotation count of 3; a section stores 1 or 4.", Refused(turns));

        RoomObject other = fixture.Lit.Get("other");
        LinkException elsewhere = Assert.Throws<LinkException>(() => RoomLighting.Read(section, hub.Definition, other.Bsp));
        Assert.Contains("faces; the room has", elsewhere.Message, StringComparison.Ordinal);

        string Refused(byte[] bytes) => Assert.Throws<LinkException>(() => RoomLighting.Read(bytes, hub.Definition, hub.Bsp)).Message;
    }

    /// <summary>
    /// A lit room's pack entry holds its lighting after its other whole-room
    /// sections, the pack's reader attaches it to the room it loads (bound to
    /// the container's compile), and the listing reads each room's stored
    /// turns; an unlit room's entry has none.
    /// </summary>
    [Fact]
    public async Task ThePackCarriesTheLightingSection()
    {
        RoomPackItem hub = await RoomPackItem.CreateAsync(fixture.Lit.Get("hub"));
        RoomPackItem other = await RoomPackItem.CreateAsync(fixture.Lit.Get("other"));
        RoomPackItem unlit = await RoomPackItem.CreateAsync(fixture.Unlit.Get("hub"));
        Assert.Equal(["ECNT", "PROP", "BMOD", "LITE", "MAPV", "LNKA"], hub.Extra.Take(6).Select(s => s.Tag));
        Assert.DoesNotContain(unlit.Extra, s => s.Tag == RoomLighting.SectionTag);

        using MemoryStream pack = new();
        await RoomPack.SaveAsync([hub, other], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, ["other", "hub"]);
        Assert.Equal(4, loaded[0].LightingOfCompile!.RotationCount);
        Assert.Equal(fixture.Lit.Get("other").Lighting!.ToSection().Bytes.ToArray(), loaded[0].LightingOfCompile!.ToSection().Bytes.ToArray());
        Assert.Equal(1, loaded[1].LightingOfCompile!.RotationCount);
        Assert.Equal(new Dictionary<string, int> { ["hub"] = 1, ["other"] = 4 }, await RoomPack.ReadLightingTurnsAsync(pack, index));
    }
}
