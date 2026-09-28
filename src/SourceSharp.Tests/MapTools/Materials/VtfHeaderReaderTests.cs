//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.Tests.MapFormats.Assets;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// Reading a texture's header, and only its header, out of game content --
/// and the material facts that now use it.
/// </summary>
public class VtfHeaderReaderTests
{
    private static readonly VPath TexturePath = VPath.Create("materials/a/b.vtf");

    [Fact]
    public async Task AGoodTextureGivesTheHeaderTheWholeParseGives()
    {
        byte[] logo = File.ReadAllBytes(GoldenAssets.TfLogoVtf());
        await using ContentFileSystem content = await MountAsync(new InMemoryFileSystem().AddFile(TexturePath, logo));

        VtfHeader? header = await VtfHeaderReader.TryReadAsync(content, TexturePath);

        Assert.NotNull(header);
        Assert.Equal(Bytes(VtfFile.Parse(logo).Header), Bytes(header.Value));
    }

    [Fact]
    public async Task AMissingTextureIsNull()
    {
        await using ContentFileSystem content = await MountAsync(new InMemoryFileSystem().AddText("x.txt", "x"));

        Assert.Null(await VtfHeaderReader.TryReadAsync(content, TexturePath));
    }

    [Fact]
    public async Task ACorruptTextureIsNull()
    {
        await using ContentFileSystem content = await MountAsync(
            new InMemoryFileSystem().AddText(TexturePath.Value, "this is not a texture"));

        Assert.Null(await VtfHeaderReader.TryReadAsync(content, TexturePath));
    }

    /// <summary>
    /// A texture cut short inside its header is rejected, as the whole parse
    /// rejects it; one cut short only in its pixels is accepted, as the whole
    /// parse accepts it (neither reads the pixels).
    /// </summary>
    [Fact]
    public async Task ATruncatedTextureIsJudgedAsTheWholeParseJudgesIt()
    {
        byte[] logo = File.ReadAllBytes(GoldenAssets.TfLogoVtf());
        foreach (int length in new[] { 10, 50, 90, 200, 1000, logo.Length - 1 })
        {
            byte[] cut = logo.AsSpan(0, length).ToArray();
            await using ContentFileSystem content = await MountAsync(new InMemoryFileSystem().AddFile(TexturePath, cut));

            VtfHeader? header = await VtfHeaderReader.TryReadAsync(content, TexturePath);

            byte[]? expected = WholeParse(cut);
            Assert.Equal(expected, header is { } h ? Bytes(h) : null);
        }
    }

    /// <summary>
    /// The read stops at <see cref="VtfFile.HeaderReadLength"/>: a store that
    /// cancels any read reaching past that byte serves the header reader, and
    /// refuses a whole read of the same file.
    /// </summary>
    [Fact]
    public async Task OnlyTheHeaderBytesAreRead()
    {
        byte[] logo = File.ReadAllBytes(GoldenAssets.TfLogoVtf());
        FaultInjectingFileSystem guarded = new(
            new InMemoryFileSystem().AddFile(TexturePath, logo), FaultPlan.CancelReadAfter(VtfFile.HeaderReadLength));
        await using ContentFileSystem content = await MountAsync(guarded);

        Assert.NotNull(await VtfHeaderReader.TryReadAsync(content, TexturePath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await content.ReadAsync(TexturePath));
    }

    /// <summary>
    /// A read that comes back holding fewer bytes than the file's length
    /// promises is judged by the bytes it holds -- what a whole read that came
    /// up short would have handed the parser -- not reported as a caller bug.
    /// Both outcomes: a cut after the header still parses, a cut inside it
    /// does not.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(200)]
    public async Task AReadThatComesUpShortIsJudgedByWhatItHolds(int deliveredBytes)
    {
        // 96-byte header and 256 bytes of pixels: longer than the header read,
        // so a delivery of 200 bytes is short.
        byte[] file = VtfTests.Synthetic(ImageFormat.Bgra8888, 8, 8, mips: 1, envMap: false);
        Assert.True(file.Length > VtfFile.HeaderReadLength);
        FaultInjectingFileSystem truncating = new(
            new InMemoryFileSystem().AddFile(TexturePath, file), FaultPlan.ShortReadAfter(deliveredBytes));
        await using ContentFileSystem content = await MountAsync(truncating);

        VtfHeader? header = await VtfHeaderReader.TryReadAsync(content, TexturePath);

        byte[]? expected = WholeParse(file.AsSpan(0, deliveredBytes).ToArray());
        Assert.Equal(expected, header is { } h ? Bytes(h) : null);
        Assert.Equal(deliveredBytes >= 96, header is not null);
    }

    [Fact]
    public async Task ACancelledTokenCancelsTheRead()
    {
        await using ContentFileSystem content = await MountAsync(
            new InMemoryFileSystem().AddFile(TexturePath, File.ReadAllBytes(GoldenAssets.TfLogoVtf())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await VtfHeaderReader.TryReadAsync(content, TexturePath, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task NoContentIsAnArgumentError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VtfHeaderReader.TryReadAsync(null!, TexturePath));
    }

    /// <summary>
    /// The material facts take a texture's size and reflectivity from a
    /// header read: content that refuses every whole read of a VTF still
    /// yields them, and yields exactly what a whole read gave.
    /// </summary>
    [Fact]
    public async Task MaterialFactsReadOnlyTheTexturesHeader()
    {
        await using ContentFileSystem plain = await new MaterialContent()
            .AddMaterial("a/b", "LightmappedGeneric { $basetexture a/b }")
            .AddTexture("a/b", 256, 64, (0.25f, 0.5f, 0.75f))
            .MountAsync();
        NoWholeTextureReads guarded = new(plain);

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", guarded);

        Assert.True(facts.HasPreviewImage);
        Assert.Equal((256, 64), (facts.Width, facts.Height));
        Assert.Equal(new Vec3(0.25f, 0.5f, 0.75f), facts.Reflectivity);
        Assert.Equal(1, guarded.RangeReads);
    }

    /// <summary>
    /// A texture whose header is cut short falls back to the defaults exactly
    /// as a corrupt one always has: no preview image, the fallback size, and
    /// zero reflectivity.
    /// </summary>
    [Fact]
    public async Task MaterialFactsFallBackForATruncatedTexture()
    {
        byte[] logo = File.ReadAllBytes(GoldenAssets.TfLogoVtf());
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("materials/a/b.vmt", "LightmappedGeneric { $basetexture a/b }")
            .AddFile("materials/a/b.vtf", logo.AsSpan(0, 60));
        await using ContentFileSystem content = await MountAsync(disk);

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        Assert.False(facts.HasPreviewImage);
        Assert.Equal((128, 128), (facts.Width, facts.Height));
        Assert.Equal(Vec3.Zero, facts.Reflectivity);
    }

    private static async Task<ContentFileSystem> MountAsync(IFileSystem disk) =>
        new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);

    private static byte[] Bytes(VtfHeader header) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<VtfHeader>(in header)).ToArray();

    private static byte[]? WholeParse(byte[] file)
    {
        try
        {
            return Bytes(VtfFile.Parse(file).Header);
        }
        catch (InvalidVtfException)
        {
            return null;
        }
    }

    /// <summary>Content that fails any whole read of a <c>.vtf</c>, and counts range reads.</summary>
    internal sealed class NoWholeTextureReads(IContentFileSystem inner) : IContentFileSystem
    {
        private int _rangeReads;

        public int RangeReads => Volatile.Read(ref _rangeReads);

        public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(path, cancellationToken);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default) =>
            path.Value.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException($"a whole read of {path}; only its header was needed")
                : inner.ReadAsync(path, cancellationToken);

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath path, long offset, int length, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _rangeReads);
            return inner.ReadRangeAsync(path, offset, length, cancellationToken);
        }

        public async IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory,
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (VPath path in inner.EnumerateAsync(directory, searchPattern, cancellationToken))
            {
                yield return path;
            }
        }
    }
}
