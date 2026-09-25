using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The nested game lumps: <c>sprp</c>, <c>dprp</c>, <c>dplt</c> and
/// <c>dplh</c>.
/// </summary>
public class GameLumpTests : IClassFixture<LockdownFixture>
{
    private readonly LockdownFixture _fixture;

    /// <summary>Takes the shared golden map.</summary>
    /// <param name="fixture">The fixture xUnit constructs once.</param>
    public GameLumpTests(LockdownFixture fixture) => _fixture = fixture;

    [Fact]
    public void StaticPropDictionaryEntryIsOneHundredTwentyEightBytes()
    {
        // The reference implementation, STATIC_PROP_NAME_LENGTH.
        Assert.Equal(128, Unsafe.SizeOf<StaticPropDictLump>());
    }

    [Fact]
    public void StaticPropLeafEntryIsTwoBytes()
    {
        Assert.Equal(2, Unsafe.SizeOf<StaticPropLeafLump>());
    }

    [Fact]
    public void StaticPropVersionFourIsFiftySixBytes()
    {
        Assert.Equal(56, Unsafe.SizeOf<StaticPropLumpV4>());
    }

    [Fact]
    public void StaticPropVersionFiveAddsFourBytesForTheForcedFadeScale()
    {
        Assert.Equal(60, Unsafe.SizeOf<StaticPropLumpV5>());
    }

    [Fact]
    public void StaticPropVersionSixAddsFourMoreForTheDirectXRange()
    {
        Assert.Equal(64, Unsafe.SizeOf<StaticPropLumpV6>());
    }

    [Fact]
    public void StaticPropVersionTenIsSeventyTwoBytes()
    {
        Assert.Equal(72, Unsafe.SizeOf<StaticPropLumpV10>());
    }

    [Fact]
    public void StaticPropVersionTenHasNoByteFlagsWhereVersionSixDid()
    {
        // The trap. Versions 4 to 6 put unsigned char m_Flags immediately
        // after m_Solid; version 10 removed it and widened the flags to a
        // uint near the end. So byte 31 of a version 10 prop is PADDING, and
        // reading it as flags reports whatever the compiler left there.
        StaticPropLumpV6 six = default;
        StaticPropLumpV10 ten = default;

        int sixFlags = (int)Unsafe.ByteOffset(
            ref Unsafe.As<StaticPropLumpV6, byte>(ref six), ref six.Flags);
        int tenPadding = (int)Unsafe.ByteOffset(
            ref Unsafe.As<StaticPropLumpV10, byte>(ref ten), ref ten.Padding);
        int tenFlags = (int)Unsafe.ByteOffset(
            ref Unsafe.As<StaticPropLumpV10, byte>(ref ten),
            ref Unsafe.As<uint, byte>(ref ten.Flags));

        Assert.Equal(31, sixFlags);
        Assert.Equal(31, tenPadding);
        Assert.Equal(64, tenFlags);
    }

    [Fact]
    public void StaticPropLightstyleSampleIsFourBytes()
    {
        // A bare ColorRGBExp32.
        Assert.Equal(4, Unsafe.SizeOf<StaticPropLightstylesLump>());
    }

    [Fact]
    public void DetailObjectIsFiftyTwoBytes()
    {
        Assert.Equal(52, Unsafe.SizeOf<DetailObjectLump>());
    }

    [Fact]
    public void DetailSpriteDictionaryEntryIsThirtyTwoBytes()
    {
        // Layout: four Vector2D.
        Assert.Equal(32, Unsafe.SizeOf<DetailSpriteDictLump>());
    }

    [Fact]
    public void DetailPropLightstyleSampleIsFiveBytesWithNoPadding()
    {
        // Every member is a byte, so the struct's
        // alignment is one and there is no trailing pad. Round it up to 8 and
        // every sample after the first is misread.
        Assert.Equal(5, Unsafe.SizeOf<DetailPropLightstylesLump>());
    }

    [Fact]
    public void StaticPropIdSpellsSprp()
    {
        Assert.Equal("sprp", GameLumpId.CodeOf(GameLumpId.MakeId(GameLumpId.StaticProps)));
    }

    [Fact]
    public void TheGoldenMapsGameLumpDirectoryHoldsSprpWithTheFirstCharacterHigh()
    {
        // The arbiter. The reference implementation writes the id as the C multi-character
        // constant 'sprp', which both compilers evaluate with 's' in the HIGH
        // byte, so the directory's int is 0x73707270. Nothing but a real file
        // can settle this, and dm_lockdown.bsp is a real file.
        int sprp = _fixture.Bsp.GameLumps[0].Id;

        Assert.Equal(('s' << 24) | ('p' << 16) | ('r' << 8) | 'p', sprp);
        Assert.Equal(GameLumpId.MakeId(GameLumpId.StaticProps), sprp);
    }

    [Fact]
    public void TheContainerAndTheStructsAgreeOnThePacking()
    {
        // This fact used to assert the opposite: it pinned a real disagreement,
        // because BspData.cs's GameLumpEntry.MakeId reversed the code and the
        // lane that found it could not edit that file. The container is fixed
        // now, so the fact is inverted rather than deleted -- two
        // implementations of one packing is exactly the shape that drifts
        // apart silently, and this is what notices.
        Assert.Equal(
            GameLumpId.MakeId(GameLumpId.StaticProps),
            GameLumpEntry.MakeId(GameLumpId.StaticProps));
        Assert.Equal("sprp", GameLumpId.CodeOf(GameLumpEntry.MakeId(GameLumpId.StaticProps)));
        Assert.Equal(
            "sprp",
            new GameLumpEntry(GameLumpEntry.MakeId("sprp"), 0, 10, default).IdString());
    }

    private static GameLumpEntry BuildSprp<TProp>(
        ushort version,
        IReadOnlyList<string> names,
        IReadOnlyList<ushort> leaves,
        ReadOnlySpan<TProp> props)
        where TProp : unmanaged
    {
        List<byte> bytes = [];
        bytes.AddRange(BitConverter.GetBytes(names.Count));
        foreach (string name in names)
        {
            byte[] field = new byte[128];
            System.Text.Encoding.Latin1.GetBytes(name).CopyTo(field, 0);
            bytes.AddRange(field);
        }

        bytes.AddRange(BitConverter.GetBytes(leaves.Count));
        foreach (ushort leaf in leaves)
        {
            bytes.AddRange(BitConverter.GetBytes(leaf));
        }

        bytes.AddRange(BitConverter.GetBytes(props.Length));
        bytes.AddRange(MemoryMarshal.AsBytes(props).ToArray());

        return new GameLumpEntry(
            GameLumpId.MakeId(GameLumpId.StaticProps), 0, version, bytes.ToArray());
    }

    private static StaticPropLumpV4 SampleV4() => new()
    {
        Origin = new Vec3(1, 2, 3),
        Angles = new Vec3(0, 90, 0),
        PropType = 0,
        FirstLeaf = 0,
        LeafCount = 1,
        Solid = 6,
        Flags = (byte)StaticPropFlags.NoShadow,
        Skin = 2,
        FadeMinDist = 100,
        FadeMaxDist = 500,
        LightingOrigin = new Vec3(4, 5, 6),
    };

    [Fact]
    public void ReadsAVersionFourStaticPropLump()
    {
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["models/props/crate.mdl"], [7], [SampleV4()]));

        Assert.Single(lump.Props);
    }

    [Fact]
    public void ReadsTheModelDictionaryAsStrings()
    {
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["models/props/crate.mdl"], [7], [SampleV4()]));

        Assert.Equal("models/props/crate.mdl", lump.ModelNames[0]);
    }

    [Fact]
    public void ReadsTheLeafListWhichComesBeforeThePropsNotAfter()
    {
        // Writes dict, then LEAVES, then props. Swapping
        // the last two still parses a plausible-looking lump.
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["models/props/crate.mdl"], [7, 9], [SampleV4()]));

        Assert.Equal<ushort[]>([7, 9], [.. lump.LeafEntries]);
    }

    [Fact]
    public void VersionFourPropsGetAForcedFadeScaleOfOne()
    {
        // The reference implementation. A zero here would stop the prop fading at all.
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["m.mdl"], [0], [SampleV4()]));

        Assert.Equal(1.0f, lump.Props[0].ForcedFadeScale);
    }

    [Fact]
    public void VersionFourPropsGainNoPerTexelLighting()
    {
        // "Older versions don't want this."
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["m.mdl"], [0], [SampleV4()]));

        Assert.True(lump.Props[0].Flags.HasFlag(StaticPropFlags.NoPerTexelLighting));
    }

    [Fact]
    public void VersionFourPropsKeepTheirOwnFlagsToo()
    {
        StaticPropLump lump = StaticPropLump.Read(
            BuildSprp(4, ["m.mdl"], [0], [SampleV4()]));

        Assert.True(lump.Props[0].Flags.HasFlag(StaticPropFlags.NoShadow));
    }

    [Fact]
    public void VersionFivePropsKeepTheirOwnForcedFadeScale()
    {
        // The V4 path sets 1.0f and the V5 path then
        // overwrites it, so a reader that stops at the V4 path silently
        // discards the field version 5 was created to add.
        StaticPropLumpV5 prop = new()
        {
            Origin = new Vec3(1, 2, 3),
            Solid = 6,
            ForcedFadeScale = 0.25f,
        };

        StaticPropLump lump = StaticPropLump.Read(BuildSprp(5, ["m.mdl"], [0], [prop]));

        Assert.Equal(0.25f, lump.Props[0].ForcedFadeScale);
    }

    [Fact]
    public void VersionSixPropsKeepTheirDirectXRange()
    {
        StaticPropLumpV6 prop = new()
        {
            Solid = 6,
            ForcedFadeScale = 1.0f,
            MinDxLevel = 80,
            MaxDxLevel = 95,
        };

        StaticPropLump lump = StaticPropLump.Read(BuildSprp(6, ["m.mdl"], [0], [prop]));

        Assert.Equal(80, lump.Props[0].MinDxLevel);
        Assert.Equal(95, lump.Props[0].MaxDxLevel);
    }

    [Fact]
    public void VersionSixPropsAlsoGainNoPerTexelLighting()
    {
        // The V6 path runs through V5 which runs through V4, so the flag is
        // added for every version below 10.
        StaticPropLumpV6 prop = new() { Solid = 6 };

        StaticPropLump lump = StaticPropLump.Read(BuildSprp(6, ["m.mdl"], [0], [prop]));

        Assert.True(lump.Props[0].Flags.HasFlag(StaticPropFlags.NoPerTexelLighting));
    }

    [Fact]
    public void VersionTenPropsDoNotGainNoPerTexelLighting()
    {
        // Version 10 IS the per-texel lighting version, so the compatibility
        // flag must not be forced on.
        StaticPropLumpV10 prop = new() { Solid = 6, Flags = (uint)StaticPropFlags.NoShadow };

        StaticPropLump lump = StaticPropLump.Read(BuildSprp(10, ["m.mdl"], [0], [prop]));

        Assert.False(lump.Props[0].Flags.HasFlag(StaticPropFlags.NoPerTexelLighting));
    }

    [Fact]
    public void VersionTenPropsKeepTheirLightmapResolution()
    {
        StaticPropLumpV10 prop = new()
        {
            Solid = 6,
            LightmapResolutionX = 32,
            LightmapResolutionY = 64,
        };

        StaticPropLump lump = StaticPropLump.Read(BuildSprp(10, ["m.mdl"], [0], [prop]));

        Assert.Equal(32, lump.Props[0].LightmapResolutionX);
        Assert.Equal(64, lump.Props[0].LightmapResolutionY);
    }

    [Fact]
    public void TheSourceVersionIsRecorded()
    {
        StaticPropLump lump = StaticPropLump.Read(BuildSprp(4, ["m.mdl"], [0], [SampleV4()]));

        Assert.Equal(4, lump.Props[0].SourceVersion);
    }

    [Fact]
    public void RejectsAVersionWithNoStructInThisTree()
    {
        // There is no version 7, 8 or 9 struct anywhere in this tree, and
        // guessing at one would be inventing another game's format.
        Assert.Throws<InvalidBspException>(() =>
            StaticPropLump.Read(BuildSprp(7, ["m.mdl"], [0], [SampleV4()])));
    }

    [Fact]
    public void RejectsAGameLumpThatIsNotSprp()
    {
        GameLumpEntry wrong = new(GameLumpId.MakeId("dprp"), 0, 4, new byte[12]);

        Assert.Throws<InvalidBspException>(() => StaticPropLump.Read(wrong));
    }

    [Fact]
    public void RejectsALumpThatRunsShortOfItsDeclaredCount()
    {
        // A declared prop count with no bytes behind it. Truncating silently
        // is how a corrupt map becomes a map with fewer props.
        byte[] bytes = [.. BitConverter.GetBytes(0), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(99)];
        GameLumpEntry entry = new(GameLumpId.MakeId(GameLumpId.StaticProps), 0, 10, bytes);

        Assert.Throws<InvalidBspException>(() => StaticPropLump.Read(entry));
    }

    [Fact]
    public void WritesVersionTenWhateverItRead()
    {
        // And the reference implementation -- the branch writes 10.
        StaticPropLump lump = StaticPropLump.Read(BuildSprp(4, ["m.mdl"], [0], [SampleV4()]));

        Assert.Equal(10, lump.Write().Version);
    }

    [Fact]
    public void AVersionTenLumpRoundTripsByteForByte()
    {
        // The only version where a byte-exact golden is meaningful, because it
        // is the only one this port writes.
        StaticPropLumpV10 prop = new()
        {
            Origin = new Vec3(1, 2, 3),
            Angles = new Vec3(0, 90, 0),
            PropType = 0,
            FirstLeaf = 0,
            LeafCount = 2,
            Solid = 6,
            Skin = 1,
            FadeMinDist = 10,
            FadeMaxDist = 20,
            LightingOrigin = new Vec3(4, 5, 6),
            ForcedFadeScale = 0.5f,
            MinDxLevel = 80,
            MaxDxLevel = 95,
            Flags = (uint)StaticPropFlags.NoShadow,
            LightmapResolutionX = 32,
            LightmapResolutionY = 32,
        };

        GameLumpEntry original = BuildSprp(10, ["models/props/crate.mdl"], [3, 4], [prop]);
        GameLumpEntry written = StaticPropLump.Read(original).Write();

        Assert.True(original.Data.Span.SequenceEqual(written.Data.Span));
    }

    [Fact]
    public void AModelNameTooLongForItsFieldIsRejectedRatherThanTruncated()
    {
        StaticPropLump lump = new();
        lump.ModelNames.Add(new string('x', 128));

        Assert.Throws<ArgumentException>(() => lump.Write());
    }

    [Fact]
    public void DetailPropLightingIsAPlainArrayWithNoCountPrefix()
    {
        DetailPropLightstylesLump[] samples =
        [
            new() { Lighting = new ColorRgbExp32 { R = 1, G = 2, B = 3, Exponent = -4 }, Style = 5 },
            new() { Lighting = new ColorRgbExp32 { R = 6, G = 7, B = 8, Exponent = 9 }, Style = 10 },
        ];

        GameLumpEntry entry = DetailPropLightingLump.Write(samples, hdr: false);

        Assert.Equal(10, entry.Data.Length);
    }

    [Fact]
    public void DetailPropLightingRoundTrips()
    {
        DetailPropLightstylesLump[] samples =
        [
            new() { Lighting = new ColorRgbExp32 { R = 1, G = 2, B = 3, Exponent = -4 }, Style = 5 },
        ];

        ReadOnlySpan<DetailPropLightstylesLump> read =
            DetailPropLightingLump.Read(DetailPropLightingLump.Write(samples, hdr: false));

        Assert.Equal((sbyte)-4, read[0].Lighting.Exponent);
        Assert.Equal(5, read[0].Style);
    }

    [Fact]
    public void DetailPropLightingHdrUsesTheDplhCode()
    {
        GameLumpEntry entry = DetailPropLightingLump.Write([], hdr: true);

        Assert.Equal("dplh", GameLumpId.CodeOf(entry.Id));
    }

    [Fact]
    public void DetailPropLumpRejectsAnyVersionButFour()
    {
        // Defines only version 4.
        GameLumpEntry entry = new(GameLumpId.MakeId(GameLumpId.DetailProps), 0, 3, new byte[12]);

        Assert.Throws<InvalidBspException>(() => DetailPropLump.Read(entry));
    }

    [Fact]
    public void DetailPropLumpRoundTripsByteForByte()
    {
        DetailPropLump lump = new();
        lump.ModelNames.Add("models/props/grass.mdl");
        lump.Sprites.Add(default);
        lump.Props.Add(new DetailObjectLump
        {
            Origin = new Vec3(1, 2, 3),
            DetailModel = 0,
            Leaf = 4,
            Type = (byte)DetailPropType.Sprite,
            Scale = 1.5f,
        });

        GameLumpEntry once = lump.Write();
        GameLumpEntry twice = DetailPropLump.Read(once).Write();

        Assert.True(once.Data.Span.SequenceEqual(twice.Data.Span));
    }

    [Fact]
    public void TheGoldenMapCarriesAStaticPropGameLump()
    {
        Assert.Contains(
            _fixture.Bsp.GameLumps,
            g => GameLumpId.CodeOf(g.Id) == GameLumpId.StaticProps);
    }

    [Fact]
    public void TheGoldenMapsStaticPropsParse()
    {
        GameLumpEntry sprp = _fixture.Bsp.GameLumps.First(
            g => GameLumpId.CodeOf(g.Id) == GameLumpId.StaticProps);
        StaticPropLump lump = StaticPropLump.Read(sprp);

        Assert.NotEmpty(lump.Props);
    }

    [Fact]
    public void TheGoldenMapsStaticPropsAllNameARealModel()
    {
        // The sharpest available check that the prop struct matches the lump's
        // version: a wrong stride makes PropType read as a coordinate byte.
        GameLumpEntry sprp = _fixture.Bsp.GameLumps.First(
            g => GameLumpId.CodeOf(g.Id) == GameLumpId.StaticProps);
        StaticPropLump lump = StaticPropLump.Read(sprp);

        Assert.All(lump.Props, p => Assert.InRange(p.PropType, 0, lump.ModelNames.Count - 1));
    }

    [Fact]
    public void TheGoldenMapsStaticPropLeafRunsLieInsideItsLeafList()
    {
        GameLumpEntry sprp = _fixture.Bsp.GameLumps.First(
            g => GameLumpId.CodeOf(g.Id) == GameLumpId.StaticProps);
        StaticPropLump lump = StaticPropLump.Read(sprp);

        Assert.All(lump.Props, p =>
            Assert.InRange(p.FirstLeaf + p.LeafCount, 0, lump.LeafEntries.Count));
    }

    [Fact]
    public void TheGoldenMapsStaticPropModelNamesAllStartWithModels()
    {
        GameLumpEntry sprp = _fixture.Bsp.GameLumps.First(
            g => GameLumpId.CodeOf(g.Id) == GameLumpId.StaticProps);
        StaticPropLump lump = StaticPropLump.Read(sprp);

        Assert.All(lump.ModelNames, n =>
            Assert.StartsWith("models", n, StringComparison.OrdinalIgnoreCase));
    }
}
