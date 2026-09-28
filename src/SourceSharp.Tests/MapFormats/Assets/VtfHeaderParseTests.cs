//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// <see cref="VtfFile.ParseHeader"/>, the header parse from a file's first
/// bytes, against <see cref="VtfFile.Parse"/> of the whole file.
/// </summary>
/// <remarks>
/// <para>
/// The header parse is only worth having if it is indistinguishable from the
/// whole-file parse on every input: the same header for a good file, and the
/// same exception with the same message for a bad one. So every fact here is
/// a comparison, over every VTF this tree has -- the committed TF2 logo, the
/// synthetic files the unit facts build, the skybox faces the surface facts
/// mount, the default cubemap vbsp writes, and MapGen's writer -- and over
/// truncations and corruptions of each.
/// </para>
/// <para>
/// A comparison rather than expected values, because the point is not what
/// either parse says about a mangled file; it is that they say the same thing.
/// </para>
/// </remarks>
public class VtfHeaderParseTests
{
    /// <summary>Every VTF the tree has or builds, by name.</summary>
    public static TheoryData<string> Fixtures
    {
        get
        {
            TheoryData<string> names = new();
            foreach (string name in AllFixtures().Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    private static Dictionary<string, byte[]> AllFixtures() => new()
    {
        ["tf2-logo (7.4, resources, thumbnail, DXT5)"] = File.ReadAllBytes(GoldenAssets.TfLogoVtf()),
        ["synthetic 7.4 one resource"] = VtfTests.Synthetic(ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false),
        ["synthetic 7.2 fixed layout"] = VtfTests.Synthetic(
            ImageFormat.Bgra8888, 4, 4, mips: 1, envMap: false, minor: 2, headerSize: 80),
        ["synthetic 7.3 one resource"] = VtfTests.Synthetic(
            ImageFormat.Dxt1, 64, 32, mips: 7, envMap: false, minor: 3, headerSize: 88),
        ["synthetic cubemap"] = VtfTests.Synthetic(ImageFormat.Bgr888, 8, 8, mips: 4, envMap: true),
        ["synthetic large DXT5"] = VtfTests.Synthetic(ImageFormat.Dxt5, 1024, 1024, mips: 11, envMap: false),
        ["skybox face (7.4, no resources)"] = SurfaceUnit.Vtf(512, 512, (int)ImageFormat.Bgr888, 0x034c),
        ["default cubemap LDR"] = DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0x434c, 1),
        ["default cubemap HDR two frames"] = DefaultCubemapBuilder.Serialize(ImageFormat.Rgba16161616F, 0x6000, 2),
        ["mapgen writer BGR888"] = VtfWriter.Write(64, 32, ImageFormat.Bgr888, 0x4, (_, _) => (10, 20, 30, 255)),
        ["mapgen writer skybox BGRA8888"] = VtfWriter.Write(
            16, 16, ImageFormat.Bgra8888, VtfWriter.SkyboxFlags, (x, y) => ((byte)x, (byte)y, 0, 128)),
    };

    private static byte[] Fixture(string name) => AllFixtures()[name];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheHeaderParseGivesTheWholeParsesHeader(string name)
    {
        byte[] file = Fixture(name);

        VtfHeader whole = VtfFile.Parse(file).Header;
        VtfHeader header = VtfFile.ParseHeader(Prefix(file), file.Length);

        Assert.Equal(Bytes(whole), Bytes(header));
        Assert.Equal(VtfFile.Parse(file).Width, header.Width);
        Assert.Equal(VtfFile.Parse(file).Reflectivity, header.Reflectivity);
    }

    /// <summary>
    /// Every truncation that matters: through the base header, the full
    /// header, the resource table, the prefix a header read takes, and the
    /// last byte before the image data and before the end. A truncated file is
    /// accepted or rejected, with the same message, exactly as the whole parse
    /// does it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ATruncatedFileIsJudgedAsTheWholeParseJudgesIt(string name)
    {
        byte[] file = Fixture(name);
        int imageOffset = VtfFile.Parse(file).ImageDataOffset;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));

        int[] lengths =
        [
            0, 1, 3, 4, 8, 15, 16, 17, 63, 79, 80, 81, 87, 88, 89, 95, 96, 97,
            VtfFile.HeaderReadLength - 1, VtfFile.HeaderReadLength, VtfFile.HeaderReadLength + 1,
            headerSize - 1, headerSize, imageOffset - 1, imageOffset, imageOffset + 1, file.Length - 1,
        ];

        foreach (int length in lengths.Where(l => l >= 0 && l <= file.Length).Distinct())
        {
            AssertJudgedAlike(file.AsSpan(0, length).ToArray(), $"{name} cut to {length}");
        }
    }

    /// <summary>
    /// Each field the parse checks, broken on its own: the signature, both
    /// versions, the header size (too small, past the end, huge), the resource
    /// count (over the cap, absurd), a table with no image entry, and an image
    /// offset past the end.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ACorruptHeaderFieldIsReportedAsTheWholeParseReportsIt(string name)
    {
        byte[] file = Fixture(name);

        (string What, Action<byte[]> Break)[] corruptions =
        [
            ("signature", f => f[1] = (byte)'X'),
            ("major 8", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(4), 8)),
            ("minor 6", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(8), 6)),
            ("minor -1", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(8), -1)),
            ("minor 2", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(8), 2)),
            ("minor 3", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(8), 3)),
            ("header size 15", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), 15)),
            ("header size past end", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), f.Length + 1)),
            ("header size huge", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), int.MaxValue)),
            ("header size negative", f => BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), -80)),
            ("33 resources", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 33)),
            ("32 resources", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 32)),
            ("2^31 resources", f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 1u << 31)),
            ("one resource, not the image", f =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 1);
                if (f.Length >= 84)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(80), VtfResourceType.Sheet);
                }
            }),
            ("image offset past end", f =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 1);
                if (f.Length >= 88)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(80), VtfResourceType.Image);
                    BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(84), (uint)f.Length + 1);
                }
            }),
            ("no resources, thumbnail past end", f =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(68), 0);
                BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(57), (int)ImageFormat.Rgba8888);
                f[61] = 255;
                f[62] = 255;
            }),
        ];

        foreach ((string what, Action<byte[]> corrupt) in corruptions)
        {
            byte[] broken = (byte[])file.Clone();
            corrupt(broken);
            AssertJudgedAlike(broken, $"{name}: {what}");
        }
    }

    /// <summary>
    /// A seeded sweep of single-byte corruptions across every byte a header
    /// parse can read, for the combinations the named cases do not think of.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RandomHeaderCorruptionsAreJudgedAlike(string name)
    {
        byte[] file = Fixture(name);
        Random random = new(0x5f7);
        int reach = Math.Min(file.Length, VtfFile.HeaderReadLength);

        for (int i = 0; i < 400; i++)
        {
            byte[] broken = (byte[])file.Clone();
            int at = random.Next(reach);
            broken[at] = (byte)random.Next(256);
            AssertJudgedAlike(broken, $"{name}: byte {at} = {broken[at]}");
        }
    }

    [Fact]
    public void AHeaderParseRefusesAPrefixTooShortForTheFile()
    {
        byte[] file = File.ReadAllBytes(GoldenAssets.TfLogoVtf());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VtfFile.ParseHeader(file.AsSpan(0, VtfFile.HeaderReadLength - 1), file.Length));
    }

    [Fact]
    public void AHeaderParseOfAFileShorterThanThePrefixNeedsTheWholeFile()
    {
        byte[] file = SurfaceUnit.Vtf(4, 4, (int)ImageFormat.Bgr888, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => VtfFile.ParseHeader(file.AsSpan(0, 80), file.Length));
        Assert.Equal(4, VtfFile.ParseHeader(file, file.Length).Width);
    }

    [Fact]
    public void AHeaderParseRefusesAPrefixLongerThanTheFile()
    {
        byte[] file = SurfaceUnit.Vtf(4, 4, (int)ImageFormat.Bgr888, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => VtfFile.ParseHeader(file, file.Length - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => VtfFile.ParseHeader([], -1));
    }

    /// <summary>
    /// A longer prefix than the parse needs is accepted and changes nothing,
    /// so a caller that already has the whole file may pass it.
    /// </summary>
    [Fact]
    public void AHeaderParseAcceptsTheWholeFileAsItsPrefix()
    {
        byte[] file = File.ReadAllBytes(GoldenAssets.TfLogoVtf());

        Assert.Equal(Bytes(VtfFile.Parse(file).Header), Bytes(VtfFile.ParseHeader(file, file.Length)));
    }

    [Fact]
    public void TheHeaderReadLengthIsTheHeaderAndAFullResourceTable()
    {
        Assert.Equal(80 + (32 * 8), VtfFile.HeaderReadLength);
    }

    private static ReadOnlySpan<byte> Prefix(byte[] file) =>
        file.AsSpan(0, Math.Min(file.Length, VtfFile.HeaderReadLength));

    private static byte[] Bytes(VtfHeader header) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<VtfHeader>(in header)).ToArray();

    /// <summary>
    /// Parses a file both ways and asserts the same outcome: the same header,
    /// or the same exception type and message.
    /// </summary>
    private static void AssertJudgedAlike(byte[] file, string because)
    {
        (byte[]? Header, Exception? Error) whole = Outcome(() => VtfFile.Parse(file).Header);
        byte[] prefix = Prefix(file).ToArray();
        (byte[]? Header, Exception? Error) header = Outcome(() => VtfFile.ParseHeader(prefix, file.Length));

        Assert.True(
            whole.Error?.GetType() == header.Error?.GetType(),
            $"{because}: whole parse {Describe(whole.Error)}, header parse {Describe(header.Error)}");
        Assert.True(
            whole.Error?.Message == header.Error?.Message,
            $"{because}: \"{whole.Error?.Message}\" vs \"{header.Error?.Message}\"");
        Assert.True(
            whole.Header is null ? header.Header is null : header.Header is not null && whole.Header.SequenceEqual(header.Header),
            $"{because}: the headers differ");
    }

    private static (byte[]? Header, Exception? Error) Outcome(Func<VtfHeader> parse)
    {
        try
        {
            return (Bytes(parse()), null);
        }
#pragma warning disable CA1031 // Any exception is an outcome to compare, not a test failure.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return (null, exception);
        }
    }

    private static string Describe(Exception? error) =>
        error is null ? "succeeded" : $"threw {error.GetType().Name}: {error.Message}";
}
