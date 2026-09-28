//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.MaterialPatch;

/// <summary>
/// The patcher's per-compile cache of parsed game files. The cubemap pass asks
/// the same questions of the same few hundred VMTs thousands of times; each
/// file is now read and parsed once. What must not change: the answers, and
/// that nothing a caller is handed can edit what the next caller sees.
/// </summary>
public class MaterialPatcherFileCacheTests
{
    private const string Specular =
        "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"Metal/metalwall058a\"\n\t\"$envmap\" \"env_cubemap\"\n" +
        "\t\"$envmaptint\" \"[ .5 .5 .5]\"\n}\n";

    private const string Patch =
        "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$envmaptint\" \"[.25 .25 .25]\"\n\t}\n}\n";

    [Fact]
    public async Task RepeatedQuestionsReadTheFileOnce()
    {
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular));
        MaterialPatcher patcher = new(content, new MapPakFile());

        Assert.True(await patcher.HasKeyAsync("metal/specular", "$envmap"));
        Assert.True(await patcher.HasKeyValuePairAsync("metal/specular", "$envmap", "env_cubemap"));
        Assert.Equal("Metal/metalwall058a", await patcher.GetValueAsync("metal/specular", "$basetexture"));

        Assert.Equal(1, content.ReadsOf("materials/metal/specular.vmt"));
    }

    [Fact]
    public async Task AMissingFileIsAskedForOnceAndStaysMissing()
    {
        CountingContent content = await CountingContent.CreateAsync();
        MaterialPatcher patcher = new(content, new MapPakFile());

        Assert.False(await patcher.HasKeyAsync("m/none", "$envmap"));
        Assert.Null(await patcher.GetValueAsync("m/none", "$envmap"));
        Assert.Null(await patcher.LoadFromFileAsync("materials/m/none.vmt"));

        Assert.Equal(1, content.ReadsOf("materials/m/none.vmt"));
    }

    [Fact]
    public async Task AFileWithNoMaterialInItIsReadOnceAndStaysEmpty()
    {
        // Present but holding no root: LoadFromFile's null, like an absent
        // file, and remembered the same way.
        CountingContent content = await CountingContent.CreateAsync(("m/bad", " \n// nothing here\n"));
        MaterialPatcher patcher = new(content, new MapPakFile());

        Assert.Null(await patcher.LoadFromFileAsync("materials/m/bad.vmt"));
        Assert.False(await patcher.HasKeyAsync("m/bad", "$envmap"));

        Assert.Equal(1, content.ReadsOf("materials/m/bad.vmt"));
    }

    [Fact]
    public async Task LoadFromFileHandsOutACopyEachTime()
    {
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular));
        MaterialPatcher patcher = new(content, new MapPakFile());

        KeyValuesNode first = (await patcher.LoadFromFileAsync("materials/metal/specular.vmt"))!;
        StockKeyValues.SetString(first, "$envmap", "edited");
        first.Children.Add(new KeyValuesNode("$added") { Value = "1" });

        KeyValuesNode second = (await patcher.LoadFromFileAsync("materials/metal/specular.vmt"))!;

        Assert.NotSame(first, second);
        Assert.Equal("env_cubemap", StockKeyValues.GetString(second, "$envmap"));
        Assert.Null(second.Find("$added"));
        Assert.Equal("env_cubemap", await patcher.GetValueAsync("metal/specular", "$envmap"));
        Assert.Equal(1, content.ReadsOf("materials/metal/specular.vmt"));
    }

    [Fact]
    public async Task ExpandingAPatchDoesNotLeakIntoItsInclude()
    {
        // Expanding "p/patched" inserts its $envmaptint into a copy of the
        // include. The include asked about directly afterwards must still
        // have its own value, and asking about the patch again must give
        // the same answer as the first time.
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular), ("p/patched", Patch));
        MaterialPatcher patcher = new(content, new MapPakFile());

        string? patched = await patcher.GetValueAsync("p/patched", "$envmaptint");
        string? include = await patcher.GetValueAsync("metal/specular", "$envmaptint");
        string? patchedAgain = await patcher.GetValueAsync("p/patched", "$envmaptint");

        Assert.Equal("[.25 .25 .25]", patched);
        Assert.Equal("[ .5 .5 .5]", include);
        Assert.Equal(patched, patchedAgain);
    }

    [Fact]
    public async Task AReplacePatchWalksTheSharedOriginalWithoutChangingIt()
    {
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular));
        MaterialPatcher patcher = new(content, new MapPakFile());

        Assert.True(await patcher.CreatePatchAsync(
            "metal/specular",
            "maps/m/metal/specular_1_2_3",
            [new MaterialPatchInfo("$envmap", "maps/m/c1_2_3", "env_cubemap")],
            MaterialPatchType.Replace));
        Assert.True(await patcher.CreatePatchAsync(
            "metal/specular",
            "maps/m/metal/specular_4_5_6",
            [new MaterialPatchInfo("$envmap", "maps/m/c4_5_6", "env_cubemap")],
            MaterialPatchType.Replace));

        Assert.Equal("env_cubemap", await patcher.GetValueAsync("metal/specular", "$envmap"));
        Assert.Equal(2, patcher.Pak.Entries.Count);
        Assert.Equal(1, content.ReadsOf("materials/metal/specular.vmt"));
    }

    [Fact]
    public async Task ACancelledReadIsNotRemembered()
    {
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular));
        MaterialPatcher patcher = new(content, new MapPakFile());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => patcher.HasKeyAsync("metal/specular", "$envmap", new CancellationToken(true)).AsTask());

        Assert.True(await patcher.HasKeyAsync("metal/specular", "$envmap"));
        Assert.Equal(2, content.ReadsOf("materials/metal/specular.vmt"));
    }

    [Fact]
    public async Task AnUnusablePathIsNullWithoutARead()
    {
        CountingContent content = await CountingContent.CreateAsync();
        MaterialPatcher patcher = new(content, new MapPakFile());

        Assert.Null(await patcher.LoadFromFileAsync(string.Empty));
        Assert.Null(await patcher.LoadFromFileAsync("../outside.vmt"));
        Assert.Equal(0, content.TotalReads);
    }

    [Fact]
    public async Task TwoPatchersDoNotShareWhatTheyRead()
    {
        // The cache belongs to one patcher -- one compile -- and a second
        // compile reads for itself.
        CountingContent content = await CountingContent.CreateAsync(("metal/specular", Specular));

        Assert.True(await new MaterialPatcher(content, new MapPakFile()).HasKeyAsync("metal/specular", "$envmap"));
        Assert.True(await new MaterialPatcher(content, new MapPakFile()).HasKeyAsync("metal/specular", "$envmap"));

        Assert.Equal(2, content.ReadsOf("materials/metal/specular.vmt"));
    }

    // Content over an in-memory tree that counts reads per path and honours
    // a cancelled token the way a real read does.
    private sealed class CountingContent : IContentFileSystem
    {
        private readonly IContentFileSystem _inner;
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        private CountingContent(IContentFileSystem inner) => _inner = inner;

        public int TotalReads => _reads.Values.Sum();

        public static async Task<CountingContent> CreateAsync(params (string Name, string Text)[] materials) =>
            new(await MaterialPatcherTests.ContentAsync(materials));

        public int ReadsOf(string path) => _reads.GetValueOrDefault(path);

        public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
            _inner.ResolveAsync(path, cancellationToken);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
        {
            _reads[path.Value] = ReadsOf(path.Value) + 1;
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.ReadAsync(path, cancellationToken);
        }

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath path, long offset, int length, CancellationToken cancellationToken = default)
        {
            _reads[path.Value] = ReadsOf(path.Value) + 1;
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.ReadRangeAsync(path, offset, length, cancellationToken);
        }

        public IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory,
            string searchPattern = "*",
            CancellationToken cancellationToken = default) =>
            _inner.EnumerateAsync(directory, searchPattern, cancellationToken);
    }
}
