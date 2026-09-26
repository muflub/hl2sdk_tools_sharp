//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The output-format object: which version fields the writer stamps, which it
/// leaves alone, and the L4D2 lump-directory re-layout.
/// </summary>
/// <remarks>
/// <para>
/// The regression net for the whole feature is the first fact: the default
/// path must keep producing the exact bytes this branch produced before any
/// of it existed. The hash below was captured from the pre-refactor writer
/// over the committed golden map; every version knob added here has to leave
/// it untouched.
/// </para>
/// <para>
/// The v21/L4D2 fixture is SYNTHETIC until a reference-produced map of
/// that version is committed: it is built by this writer, not by the
/// reference compiler, so it proves the writer and reader agree with each
/// other and with the recovered re-layout recipe, not that they agree with
/// the reference binary. The recipe itself is the reference writer's
/// 32-byte-to-shifted-16-byte loop: the same sixteen bytes per
/// entry, fields shifted one dword right.
/// </para>
/// </remarks>
public class BspWriteFormatTests
{
    /// <summary>
    /// SHA-256 of the canonical write of the golden map, taken from the
    /// pre-refactor writer. See the class remarks.
    /// </summary>
    private const string GoldenCanonicalSha256 =
        "1DF311F55AD72922A7EBA2780BDBB01A81955978624FFA19CF15FB6D06302FF7";

    [Fact]
    public async Task DefaultFormatWritesExactlyWhatTipWroteBeforeTheFeature()
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        using MemoryStream legacy = new();
        await BspFile.SaveAsync(bsp, legacy, BspWriteMode.Canonical);
        Assert.Equal(GoldenCanonicalSha256, Sha256(legacy.ToArray()));

        // The Default object is what the compiler path passes, where the data
        // carries FileVersion 20. It is also authoritative for the header
        // version, so on THIS loaded v19 map it deliberately differs from the
        // legacy write in the four header-version bytes alone; compared
        // against a v20 copy of the data it must match byte for byte.
        using MemoryStream compilerData = new(original);
        BspData built = await BspFile.LoadAsync(compilerData);
        built.FileVersion = BspData.Version;
        using MemoryStream asCompiler = new();
        await BspFile.SaveAsync(built, asCompiler, BspWriteMode.Canonical);

        using MemoryStream explicitDefault = new();
        await BspFile.SaveAsync(
            bsp, explicitDefault, BspWriteMode.Canonical, BspWriteFormat.Default);
        Assert.Equal(asCompiler.ToArray(), explicitDefault.ToArray());
    }

    [Fact]
    public async Task LegacyCanonicalWritePreservesTheLoadedFileVersion()
    {
        // The golden map is a version 19 map, and the legacy path keeps it one
        // -- the round-trip contract that predates the format object.
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        Assert.Equal(BspData.MinVersion, HeaderVersion(original));

        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);
        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.Canonical);

        Assert.Equal(BspData.MinVersion, HeaderVersion(output.ToArray()));
    }

    [Theory]
    [InlineData(19, 0)]
    [InlineData(20, 0)]
    [InlineData(21, 0)]
    [InlineData(19, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 1)]
    public async Task CanonicalWriteWithAFormatStampsTheFormatsHeaderVersion(
        int version, int worldLightVersion)
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        BspWriteFormat format = new(
            version, worldLightVersion, StaticPropsFormat: null, L4d2LumpDirLayout: false);
        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.Canonical, format);

        // The golden's own FileVersion is 19; a format saying 20 or 21 has to
        // win, and a format saying 19 on a v20 map would have to win too.
        Assert.Equal(version, HeaderVersion(output.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BothWorldLightLumpsTakeTheFormatsWorldLightVersion(int worldLightVersion)
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        BspWriteFormat format = new(
            BspData.Version, worldLightVersion, StaticPropsFormat: null, L4d2LumpDirLayout: false);
        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.Canonical, format);

        byte[] written = output.ToArray();
        Assert.Equal(worldLightVersion, LumpVersion(written, BspLump.WorldLights));
        Assert.Equal(worldLightVersion, LumpVersion(written, BspLump.WorldLightsHdr));

        // Every other stamped lump is untouched: faces keep the write order's
        // 1, so the knob moves two entries, not the table.
        Assert.Equal(1, LumpVersion(written, BspLump.Faces));
    }

    [Fact]
    public async Task StaticPropsFormatIsStampedOnSprpAlone()
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        GameLumpEntry sourceSprp = bsp.GameLumps.Single(e => e.IdString() == "sprp");
        GameLumpEntry sourceDprp = bsp.GameLumps.Single(e => e.IdString() == "dprp");
        Assert.NotEqual(12, sourceSprp.Version);

        BspWriteFormat format = new(
            BspData.Version, 0, BspStaticPropsFormat.V12, L4d2LumpDirLayout: false);
        using MemoryStream output = new();
        await BspFile.SaveAsync(bsp, output, BspWriteMode.Canonical, format);

        using MemoryStream reread = new(output.ToArray());
        BspData saved = await BspFile.LoadAsync(reread);
        Assert.Equal(12, saved.GameLumps.Single(e => e.IdString() == "sprp").Version);
        Assert.Equal(sourceDprp.Version, saved.GameLumps.Single(e => e.IdString() == "dprp").Version);
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData("7", 7)]
    [InlineData("8", 8)]
    [InlineData("9", 9)]
    [InlineData("10_TF2", 10)]
    [InlineData("10", 10)]
    [InlineData("11", 11)]
    [InlineData("12", 12)]
    [InlineData("13", 13)]
    [InlineData("14", 14)]
    public void PropFormatTokensMapOntoTheSixToFourteenWireVersions(
        string token, int wireVersion)
    {
        BspStaticPropsFormat format = BspStaticPropsFormatInfo.Parse(token);
        Assert.Equal(wireVersion, BspStaticPropsFormatInfo.GameLumpVersion(format));
        Assert.Equal(token, BspStaticPropsFormatInfo.Token(format));
        Assert.InRange(wireVersion, 6, 14);
    }

    [Fact]
    public void PropFormatParsingIsExactCaseAndShape()
    {
        Assert.False(BspStaticPropsFormatInfo.TryParse("10_tf2", out _));
        Assert.False(BspStaticPropsFormatInfo.TryParse("15", out _));
        Assert.False(BspStaticPropsFormatInfo.TryParse("5", out _));
        Assert.Equal("10_TF2", BspStaticPropsFormatInfo.DefaultToken);
        FormatException error = Assert.Throws<FormatException>(
            () => BspStaticPropsFormatInfo.Parse("nonsense"));
        Assert.Contains("Unrecognized prop format", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L4d2RelayoutWriteIsDetectableReadableAndRoundTripsByteExact()
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        BspWriteFormat l4d2 = new(21, 1, StaticPropsFormat: null, L4d2LumpDirLayout: true);
        using MemoryStream first = new();
        await BspFile.SaveAsync(bsp, first, BspWriteMode.Canonical, l4d2);
        byte[] relaid = first.ToArray();

        // The reference reader's detection: version 21 and
        // the first lump entry's first dword zero -- under the re-layout that
        // dword is the planes version, and planes is written at 0, so every
        // canonical re-layout file this writer emits is detectable.
        Assert.Equal(21, HeaderVersion(relaid));
        Assert.Equal(0, BitConverter.ToInt32(relaid, 8));
        // The same format WITHOUT the re-layout: a pure field rotation must
        // leave the logical directory untouched, so the two files have to read
        // back lump-for-lump identical in payload and version.
        using MemoryStream standard = new();
        await BspFile.SaveAsync(
            bsp, standard, BspWriteMode.Canonical,
            new BspWriteFormat(21, 1, StaticPropsFormat: null, L4d2LumpDirLayout: false));

        using MemoryStream reread = new(relaid);
        using MemoryStream rereadStandard = new(standard.ToArray());
        BspData loaded = await BspFile.LoadAsync(reread);
        BspData loadedStandard = await BspFile.LoadAsync(rereadStandard);
        Assert.True(loaded.SourceLumpsUseL4d2Layout);
        Assert.False(loadedStandard.SourceLumpsUseL4d2Layout);
        Assert.Equal(21, loaded.FileVersion);
        foreach (BspLump lump in Enum.GetValues<BspLump>())
        {
            BspLumpData there = loaded[lump];
            BspLumpData mirror = loadedStandard[lump];
            Assert.True(
                mirror.Data.Span.SequenceEqual(there.Data.Span), $"{lump} payload changed");
            Assert.Equal(mirror.Version, there.Version);
            if (lump is BspLump.WorldLights or BspLump.WorldLightsHdr)
            {
                // These two are the format's own knobs, and both sides say 1.
                Assert.Equal(1, there.Version);
            }
        }

        // Preserve mode of the re-laid-out file is byte-exact -- the contract
        // the plan sets for a real reference-produced L4D2 map.
        using MemoryStream again = new();
        await BspFile.SaveAsync(loaded, again, BspWriteMode.PreserveSourceLayout);
        Assert.Equal(relaid, again.ToArray());

        // And a second canonical write of the same input reproduces the bytes.
        using MemoryStream second = new();
        await BspFile.SaveAsync(bsp, second, BspWriteMode.Canonical, l4d2);
        Assert.Equal(relaid, second.ToArray());
    }

    [Fact]
    public async Task RelayoutFileKeepsUnwrittenSlotsAsAllZeroEntries()
    {
        // A canonical+relayout write of a fresh compile: lumps the plan skips
        // keep all-zero entries in BOTH positions, which is what makes them
        // read back as unwritten under either layout, and what the detection
        // parity check leans on.
        BspData fresh = new();
        BspWriteFormat l4d2 = new(21, 1, StaticPropsFormat: null, L4d2LumpDirLayout: true);
        using MemoryStream output = new();
        await BspFile.SaveAsync(fresh, output, BspWriteMode.Canonical, l4d2);
        byte[] written = output.ToArray();

        using MemoryStream reread = new(written);
        BspData loaded = await BspFile.LoadAsync(reread);
        Assert.True(loaded.SourceLumpsUseL4d2Layout);

        for (int slot = 0; slot < BspData.HeaderLumps; slot++)
        {
            if (!loaded.SourceLayout![slot].Written)
            {
                Assert.True(
                    written.AsSpan(8 + (slot * 16), 16).IndexOfAnyExcept((byte)0) == -1,
                    $"unwritten slot {slot} is not an all-zero entry");
            }
        }
    }

    [Fact]
    public async Task PreserveModeIgnoresTheFormatObject()
    {
        // Documented decision: preserve mode exists to reproduce the source
        // file, so an explicit format must not move its versions or layout.
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        using MemoryStream plain = new();
        await BspFile.SaveAsync(bsp, plain, BspWriteMode.PreserveSourceLayout);

        BspWriteFormat l4d2 = new(21, 1, BspStaticPropsFormat.V6, L4d2LumpDirLayout: true);
        using MemoryStream styled = new();
        await BspFile.SaveAsync(bsp, styled, BspWriteMode.PreserveSourceLayout, l4d2);

        Assert.Equal(plain.ToArray(), styled.ToArray());
        Assert.Equal(original, plain.ToArray());
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public async Task ReaderAcceptsEveryVersionTheEngineLoads(int version)
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        BitConverter.TryWriteBytes(original.AsSpan(4), version);

        using MemoryStream input = new(original);
        BspData bsp = await BspFile.LoadAsync(input);

        // The golden's planes entry starts at 1036, nonzero, so the parity
        // check must classify this as a standard-layout file at any version.
        Assert.Equal(version, bsp.FileVersion);
        Assert.False(bsp.SourceLumpsUseL4d2Layout);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(22)]
    [InlineData(-1)]
    public async Task ReaderRejectsAVersionOutsideNineteenToTwentyOne(int version)
    {
        byte[] original = await File.ReadAllBytesAsync(GoldenBsp.Lockdown());
        BitConverter.TryWriteBytes(original.AsSpan(4), version);

        using MemoryStream input = new(original);
        InvalidBspException error =
            await Assert.ThrowsAsync<InvalidBspException>(() => BspFile.LoadAsync(input));
        Assert.Contains($"{BspData.MinVersion}..{BspData.MaxVersion}", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FormatConstructorEnforcesTheVersionDomains()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BspWriteFormat(18, 0, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BspWriteFormat(22, 0, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BspWriteFormat(20, 2, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BspWriteFormat(20, -1, null, false));

        // The re-layout is detectable at 21 only -- asking for it elsewhere is
        // a file nothing reads back.
        Assert.Throws<ArgumentException>(
            () => new BspWriteFormat(20, 0, null, L4d2LumpDirLayout: true));
        Assert.Throws<ArgumentException>(
            () => new BspWriteFormat(19, 0, null, L4d2LumpDirLayout: true));
        _ = new BspWriteFormat(21, 0, null, L4d2LumpDirLayout: true);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static int HeaderVersion(byte[] file) => BitConverter.ToInt32(file, 4);

    private static int LumpVersion(byte[] file, BspLump lump) =>
        BitConverter.ToInt32(file, 8 + ((int)lump * 16) + 8);
}
