//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.Tests.MapTools.Bsp;
using SourceSharp.Tests.MapTools.Compile;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap all --record-content</c>: the compile's game content, recorded at
/// the content seam and written as a game directory that mounts on its own.
/// </summary>
/// <remarks>
/// The compile facts run a real three-stage chain over a game mounted from a
/// <c>gameinfo.txt</c> on an in-memory disk, with one material loose and one
/// in a VPK, so that the recording, the bundle and the replay go through the
/// same mounter a user's game does.
/// </remarks>
public sealed class ContentRecordingTests
{
    private const string PackedMaterial = "unit/packed";

    private const string GameInfoText = """
        // a comment that says SearchPaths { nothing }
        "GameInfo"
        {
        	game	"Recorded"
        	Tools
        	{
        		vbsp	"-notjunc"
        	}
        	FileSystem
        	{
        		SteamAppId	243750
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        			game	|gameinfo_path|pak01.vpk
        		}
        	}
        }
        """;

    // ---- parsing ----------------------------------------------------------

    [Fact]
    public void RecordContentTakesTheZipPath()
    {
        AllArgs parsed = AllCommand.Parse(["m.vmf", AllCommand.RecordContentSwitch, "out/c.zip"]);

        Assert.False(parsed.HasErrors);
        Assert.Equal("out/c.zip", parsed.RecordContent);
        Assert.Equal("m.vmf", parsed.MapPath);
    }

    [Fact]
    public void WithoutTheSwitchNothingIsRecorded()
    {
        Assert.Null(AllCommand.Parse(["m.vmf"]).RecordContent);
    }

    [Fact]
    public void TheVvisSectionTakesTheFastFlowAndTheChainDoesNot()
    {
        // -fastflow is vvis's (StockArgs parses it), so it goes in the
        // --vvis section; before the first section it is not a chain option.
        AllArgs section = AllCommand.Parse(["m.vmf", AllCommand.VvisSection, "-fastflow"]);
        Assert.True(section.Vvis.FastFlow);
        Assert.DoesNotContain(section.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.False(AllCommand.Parse(["m.vmf"]).Vvis.FastFlow);

        AllArgs chain = AllCommand.Parse(["m.vmf", "-fastflow"]);
        Assert.Contains(chain.Diagnostics, d => d.Code == AllCommand.ChainArgument);
    }

    [Fact]
    public void RecordContentWithNoPathIsAnError()
    {
        AllArgs parsed = AllCommand.Parse(["m.vmf", AllCommand.RecordContentSwitch]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Diagnostics, d => d.Message.Contains("needs a value", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordContentTwiceIsAnError()
    {
        AllArgs parsed = AllCommand.Parse(
            ["m.vmf", AllCommand.RecordContentSwitch, "a.zip", AllCommand.RecordContentSwitch, "b.zip"]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Diagnostics, d => d.Message.Contains("give it once", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommandLineQuotesArgumentsWithSpacesAndEmptyOnes()
    {
        Assert.Equal(
            "ssmap all \"my map.vmf\" \"\" --vrad -both",
            AllCommand.CommandLine(["my map.vmf", string.Empty, "--vrad", "-both"]));
    }

    [Fact]
    public void LoosePathIsTheFileNameUnderLoose()
    {
        Assert.Equal("loose/room.rad", ContentRecording.LoosePath("/home/x/maps/room.rad"));
        Assert.Equal("loose/l.rad", ContentRecording.LoosePath(@"C:\maps\l.rad"));
    }

    [Fact]
    public void TheSummaryLineNamesFilesBytesAndMisses()
    {
        Assert.Equal(
            "record-content: /x.zip: 3 files, 1234 bytes, 2 misses",
            new ContentBundleSummary(3, 1234, 2).Line("/x.zip"));
    }

    // ---- the bundle's gameinfo.txt ---------------------------------------

    [Fact]
    public void TheBundlesGameInfoMountsOnlyItsOwnDirectoryAndKeepsTheRest()
    {
        string text = ContentRecording.BundleGameInfo(GameInfoText);
        GameInfo parsed = GameInfo.Parse(text);

        GameInfoSearchPath only = Assert.Single(parsed.SearchPaths);
        Assert.Equal(["game", "mod"], only.Kinds);
        Assert.Equal("|gameinfo_path|.", only.Location);

        // What the format pipeline reads survives.
        Assert.Equal(243750, parsed.SteamAppId);
        Assert.Equal("-notjunc", parsed.ToolArguments["vbsp"]);
        Assert.Equal("Recorded", parsed.Game);
        Assert.Contains("// a comment that says SearchPaths { nothing }", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileSystemWithoutSearchPathsGetsOne()
    {
        GameInfo parsed = GameInfo.Parse(ContentRecording.BundleGameInfo(
            "\"GameInfo\" { game \"g\" FileSystem { SteamAppId 440 } }"));

        Assert.Equal("|gameinfo_path|.", Assert.Single(parsed.SearchPaths).Location);
        Assert.Equal(440, parsed.SteamAppId);
    }

    [Fact]
    public void AGameInfoWithoutFileSystemGetsOne()
    {
        GameInfo parsed = GameInfo.Parse(ContentRecording.BundleGameInfo("\"GameInfo\" { game \"g\" }"));

        Assert.Equal("|gameinfo_path|.", Assert.Single(parsed.SearchPaths).Location);
        Assert.Equal("g", parsed.Game);
    }

    [Fact]
    public void ASearchPathsBlockDeeperThanTheLoaderReadsIsNotTheOneReplaced()
    {
        // The loader reads FileSystem's DIRECT SearchPaths child; a quoted key
        // of the same name in a nested section is not it.
        string text = ContentRecording.BundleGameInfo(
            "\"GameInfo\" { FileSystem { Other { \"SearchPaths\" { game x } } SearchPaths { game |gameinfo_path|y } } }");

        Assert.Contains("game x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("|gameinfo_path|y", text, StringComparison.Ordinal);
        Assert.Equal("|gameinfo_path|.", Assert.Single(GameInfo.Parse(text).SearchPaths).Location);
    }

    [Fact]
    public void NoOriginalMakesAMinimalGameInfo()
    {
        GameInfo parsed = GameInfo.Parse(ContentRecording.BundleGameInfo(null));

        Assert.Equal("|gameinfo_path|.", Assert.Single(parsed.SearchPaths).Location);
        Assert.Equal(0, parsed.SteamAppId);
    }

    [Fact]
    public void AnOriginalWithNoSectionIsReplacedByTheMinimalOne()
    {
        Assert.Equal(ContentRecording.BundleGameInfo(null), ContentRecording.BundleGameInfo("// nothing here"));
    }

    [Fact]
    public void AnUnclosedSectionIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ContentRecording.BundleGameInfo("\"GameInfo\" { FileSystem { "));
    }

    // ---- writing the bundle -----------------------------------------------

    [Fact]
    public async Task ReadAndResolvedFilesAreBundledAndMissesAreOnlyListed()
    {
        MutableContent game = new MutableContent()
            .With("materials/a.vmt", "read me")
            .With("materials/b.vtf", "only resolved")
            .With("materials/unused.vmt", "never touched");
        ContentRecording recording = new(game, new InMemoryFileSystem());

        using (await recording.Content.ReadAsync(VPath.Create("MATERIALS/A.VMT"))) { }
        _ = await recording.Content.ResolveAsync(VPath.Create("materials/b.vtf"));
        _ = await recording.Content.ReadAsync(VPath.Create("materials/Absent.vmt"));

        InMemoryFileSystem target = new();
        ContentBundleSummary summary = await recording.WriteAsync(
            target, VPath.Create("out/c.zip"), GameInfoText, "ssmap all m.vmf");

        Dictionary<string, byte[]> zip = Unzip(target, "out/c.zip");
        Assert.Equal(
            ["content-manifest.txt", "gameinfo.txt", "materials/a.vmt", "materials/b.vtf"],
            zip.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("read me", Encoding.UTF8.GetString(zip["materials/a.vmt"]));
        Assert.Equal("only resolved", Encoding.UTF8.GetString(zip["materials/b.vtf"]));
        Assert.Equal(new ContentBundleSummary(2, 20, 1), summary);

        string[] manifest = Encoding.UTF8.GetString(zip["content-manifest.txt"]).Split('\n');
        Assert.Contains("# command: ssmap all m.vmf", manifest);
        Assert.Contains($"read\tmaterials/a.vmt\t{DependencyRecorder.Hash("read me"u8)}\t7\ttest-mount", manifest);
        Assert.Contains($"resolved\tmaterials/b.vtf\t{DependencyRecorder.Hash("only resolved"u8)}\t13\ttest-mount", manifest);
        Assert.Contains("missing\tmaterials/absent.vmt\t-\t-\t-", manifest);
        Assert.Equal(243750, GameInfo.Parse(Encoding.UTF8.GetString(zip["gameinfo.txt"])).SteamAppId);
    }

    [Fact]
    public async Task AResolvedFileGoneByWriteTimeIsListedNotBundled()
    {
        MutableContent game = new MutableContent().With("materials/b.vtf", "x");
        ContentRecording recording = new(game, new InMemoryFileSystem());
        _ = await recording.Content.ResolveAsync(VPath.Create("materials/b.vtf"));
        game.Remove("materials/b.vtf");

        InMemoryFileSystem target = new();
        ContentBundleSummary summary = await recording.WriteAsync(target, VPath.Create("c.zip"), null, "cmd");

        Dictionary<string, byte[]> zip = Unzip(target, "c.zip");
        Assert.False(zip.ContainsKey("materials/b.vtf"));
        Assert.Contains("resolved\tmaterials/b.vtf\t-\t-\t(gone)", Encoding.UTF8.GetString(zip["content-manifest.txt"]).Split('\n'));
        Assert.Equal(new ContentBundleSummary(0, 0, 0), summary);
    }

    [Fact]
    public async Task AReadFileThatChangedIsRefusedAndNoZipAppears()
    {
        MutableContent game = new MutableContent().With("materials/a.vmt", "before");
        ContentRecording recording = new(game, new InMemoryFileSystem());
        using (await recording.Content.ReadAsync(VPath.Create("materials/a.vmt"))) { }
        game.With("materials/a.vmt", "after");

        InMemoryFileSystem target = new();
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => recording.WriteAsync(target, VPath.Create("c.zip"), null, "cmd"));

        Assert.Contains("different bytes", error.Message, StringComparison.Ordinal);
        Assert.False(await target.ExistsAsync(VPath.Create("c.zip")));
    }

    [Fact]
    public async Task AReadFileThatIsGoneIsRefused()
    {
        MutableContent game = new MutableContent().With("materials/a.vmt", "before");
        ContentRecording recording = new(game, new InMemoryFileSystem());
        using (await recording.Content.ReadAsync(VPath.Create("materials/a.vmt"))) { }
        game.Remove("materials/a.vmt");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => recording.WriteAsync(new InMemoryFileSystem(), VPath.Create("c.zip"), null, "cmd"));

        Assert.Contains("is gone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LooseFilesThatExistAreBundledAndAbsentOnesAreNot()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem()
            .AddText("maps/room.rad", "map rad")
            .AddText("steam/bin/lights.rad", "fallback lights");
        MutableContent game = new MutableContent().With("materials/a.vmt", "a");
        ContentRecording recording = new(game, disk);

        // The game's own lights.rad lookup missed, which is why the fallback was used.
        _ = await recording.Content.ResolveAsync(VPath.Create("lights.rad"));
        recording.AddLoose("loose/room.rad", VPath.Create("maps/room.rad"), "map");
        recording.AddLoose("loose/extra.rad", VPath.Create("maps/extra.rad"), "lights");
        recording.AddLoose("lights.rad", VPath.Create("steam/bin/lights.rad"), "fallback");
        Assert.Equal(3, recording.Loose.Count);

        InMemoryFileSystem target = new();
        ContentBundleSummary summary = await recording.WriteAsync(target, VPath.Create("c.zip"), null, "cmd");

        Dictionary<string, byte[]> zip = Unzip(target, "c.zip");
        Assert.Equal("map rad", Encoding.UTF8.GetString(zip["loose/room.rad"]));
        Assert.Equal("fallback lights", Encoding.UTF8.GetString(zip["lights.rad"]));
        Assert.False(zip.ContainsKey("loose/extra.rad"));

        string[] manifest = Encoding.UTF8.GetString(zip["content-manifest.txt"]).Split('\n');
        Assert.Contains(manifest, l => l.StartsWith("loose\tlights.rad\t", StringComparison.Ordinal) && l.EndsWith("\tfallback: steam/bin/lights.rad", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest, l => l.StartsWith("missing\tlights.rad", StringComparison.Ordinal));
        Assert.Equal(new ContentBundleSummary(2, 22, 0), summary);
    }

    [Fact]
    public async Task ALooseFileNeverDisplacesAGameFileTheCompileFound()
    {
        InMemoryFileSystem disk = new InMemoryFileSystem().AddText("bin/lights.rad", "fallback");
        MutableContent game = new MutableContent().With("lights.rad", "the game's");
        ContentRecording recording = new(game, disk);
        using (await recording.Content.ReadAsync(VPath.Create("lights.rad"))) { }
        recording.AddLoose("lights.rad", VPath.Create("bin/lights.rad"), "fallback");

        InMemoryFileSystem target = new();
        _ = await recording.WriteAsync(target, VPath.Create("c.zip"), null, "cmd");

        Assert.Equal("the game's", Encoding.UTF8.GetString(Unzip(target, "c.zip")["lights.rad"]));
    }

    [Fact]
    public async Task TheSameSetMakesTheSameZipBytes()
    {
        MutableContent game = new MutableContent().With("materials/a.vmt", "a").With("models/b.mdl", "b");
        ContentRecording recording = new(game, new InMemoryFileSystem());
        using (await recording.Content.ReadAsync(VPath.Create("models/b.mdl"))) { }
        using (await recording.Content.ReadAsync(VPath.Create("materials/a.vmt"))) { }

        InMemoryFileSystem target = new();
        _ = await recording.WriteAsync(target, VPath.Create("one.zip"), GameInfoText, "cmd");
        _ = await recording.WriteAsync(target, VPath.Create("two.zip"), GameInfoText, "cmd");

        Assert.Equal(target.GetBytes(VPath.Create("one.zip")), target.GetBytes(VPath.Create("two.zip")));
    }

    [Fact]
    public async Task ACancelledWriteLeavesNoZip()
    {
        MutableContent game = new MutableContent().With("materials/a.vmt", "a");
        ContentRecording recording = new(game, new InMemoryFileSystem());
        using (await recording.Content.ReadAsync(VPath.Create("materials/a.vmt"))) { }
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        InMemoryFileSystem target = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recording.WriteAsync(target, VPath.Create("c.zip"), null, "cmd", cancelled.Token));

        Assert.False(await target.ExistsAsync(VPath.Create("c.zip")));
    }

    // ---- the chain ----------------------------------------------------------

    [Fact]
    public async Task ARecordedCompileBundlesExactlyWhatItTouchedAndTheBundleReplaysIt()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");
        ContentRecording recording = new(mounted.Content, disk);
        StringWriter output = new();

        ChainOutcome outcome = await AllCommand.CompileRecordingAsync(
            Request(disk, recording.Content), Target(recording, disk), output);

        Assert.Equal(Program.ExitSuccess, outcome.ExitCode);
        Assert.Contains("record-content: /out/c.zip: ", output.ToString(), StringComparison.Ordinal);

        // Exactly the read and resolved set, at its content paths, byte for byte.
        IReadOnlyList<FileDependency> recorded = recording.Recorder.Snapshot();
        Dictionary<string, byte[]> zip = Unzip(disk, "out/c.zip");
        string[] expected = [.. recorded.Where(d => d.Kind != DependencyKind.Missing).Select(d => d.Path.Value)];
        Assert.Contains($"materials/{UnitMap.Plain}.vmt", expected);
        Assert.Contains($"materials/{PackedMaterial}.vmt", expected);
        Assert.Equal(
            new[] { ContentRecording.GameInfoName, ContentRecording.ManifestName }.Concat(expected).Order(StringComparer.Ordinal),
            zip.Keys.Order(StringComparer.Ordinal));
        foreach (string path in expected)
        {
            using IMemoryOwner<byte>? source = await mounted.Content.ReadAsync(VPath.Create(path));
            Assert.Equal(source!.Memory.ToArray(), zip[path]);
        }

        // The misses are in the manifest, and the VPK is named as a file's mount.
        string manifest = Encoding.UTF8.GetString(zip[ContentRecording.ManifestName]);
        FileDependency miss = Assert.Single(recorded, d => d.Path.Value == "lights.rad");
        Assert.Equal(DependencyKind.Missing, miss.Kind);
        Assert.Contains("missing\tlights.rad\t-\t-\t-\n", manifest, StringComparison.Ordinal);
        Assert.Contains("pak01", manifest.Split('\n').Single(l => l.Contains($"{PackedMaterial}.vmt", StringComparison.Ordinal)), StringComparison.Ordinal);

        // The bundle mounts on its own and compiles the same map.
        InMemoryFileSystem replay = Extract(zip, "bundle");
        replay.AddText("maps/room.vmf", Encoding.UTF8.GetString(disk.GetBytes(VPath.Create("maps/room.vmf"))!));
        GameContentMounter.Result bundle = await MountAsync(replay, "bundle/gameinfo.txt");
        Assert.Empty(bundle.Skipped);
        Assert.Equal(243750, bundle.GameInfo!.SteamAppId);
        foreach (string path in expected)
        {
            Assert.Equal(VPath.Create(path), (await bundle.Content.ResolveAsync(VPath.Create(path)))?.Path);
        }

        CompileResult again = await MapCompiler.CompileAsync(Request(replay, bundle.Content), null);
        Assert.Equal(await MapCompilerTests.BytesAsync(outcome.Result!.Bsp!), await MapCompilerTests.BytesAsync(again.Bsp!));
    }

    [Fact]
    public async Task AFailedCompileStillWritesWhatItReadAndExitsFailed()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");

        // vrad asks for lights.rad after vbsp has read every material.
        ThrowingContent failing = new(mounted.Content, "lights.rad", () => new MapCompileException("vrad fell over"));
        ContentRecording recording = new(failing, disk);
        StringWriter output = new();

        ChainOutcome outcome = await AllCommand.CompileRecordingAsync(
            Request(disk, recording.Content), Target(recording, disk), output);

        Assert.Equal(VbspCommand.ExitFailed, outcome.ExitCode);
        Assert.Null(outcome.Result);
        Assert.Contains("Error: vrad fell over", output.ToString(), StringComparison.Ordinal);

        Dictionary<string, byte[]> zip = Unzip(disk, "out/c.zip");
        Assert.True(zip.ContainsKey($"materials/{UnitMap.Plain}.vmt"));
        Assert.True(zip.ContainsKey($"materials/{PackedMaterial}.vmt"));
    }

    [Fact]
    public async Task AnyOtherFailureWritesTheBundleAndIsRethrown()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");
        ThrowingContent failing = new(mounted.Content, "lights.rad", () => new InvalidOperationException("boom"));
        ContentRecording recording = new(failing, disk);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AllCommand.CompileRecordingAsync(
            Request(disk, recording.Content), Target(recording, disk), new StringWriter()));

        Assert.True(Unzip(disk, "out/c.zip").ContainsKey($"materials/{UnitMap.Plain}.vmt"));
    }

    [Fact]
    public async Task ACancelledCompileWritesNoBundle()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");
        ThrowingContent failing = new(mounted.Content, "lights.rad", () => new OperationCanceledException());
        ContentRecording recording = new(failing, disk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AllCommand.CompileRecordingAsync(
            Request(disk, recording.Content), Target(recording, disk), new StringWriter()));

        Assert.False(await disk.ExistsAsync(VPath.Create("out/c.zip")));
    }

    [Fact]
    public async Task WithoutARecordingAFailedCompileExitsFailedAndWritesNothing()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");
        ThrowingContent failing = new(mounted.Content, "lights.rad", () => new MapCompileException("no"));

        ChainOutcome outcome = await AllCommand.CompileRecordingAsync(Request(disk, failing), null, new StringWriter());

        Assert.Equal(VbspCommand.ExitFailed, outcome.ExitCode);
        Assert.False(await disk.ExistsAsync(VPath.Create("out/c.zip")));
    }

    [Fact]
    public async Task WithoutARecordingAnyOtherFailureIsRethrownAsBefore()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");
        ThrowingContent failing = new(mounted.Content, "lights.rad", () => new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AllCommand.CompileRecordingAsync(Request(disk, failing), null, new StringWriter()));
    }

    [Fact]
    public async Task ABundleThatCannotBeWrittenFailsASuccessfulCompile()
    {
        InMemoryFileSystem disk = await GameDiskAsync();
        GameContentMounter.Result mounted = await MountAsync(disk, "game/gameinfo.txt");

        // The content "changes" between the compile's read and the bundle's.
        ChangingContent changing = new(mounted.Content, $"materials/{UnitMap.Plain}.vmt");
        ContentRecording recording = new(changing, disk);
        StringWriter output = new();

        ChainOutcome outcome = await AllCommand.CompileRecordingAsync(
            Request(disk, recording.Content), Target(recording, disk), output);

        Assert.NotNull(outcome.Result);
        Assert.Equal(VbspCommand.ExitFailed, outcome.ExitCode);
        Assert.Contains($"{AllCommand.RecordContentSwitch} failed:", output.ToString(), StringComparison.Ordinal);
        Assert.False(await disk.ExistsAsync(VPath.Create("out/c.zip")));
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>
    /// A game directory with a gameinfo.txt, the plain material loose and the
    /// packed one in a VPK, and a room whose pillar uses the packed one.
    /// </summary>
    private static async Task<InMemoryFileSystem> GameDiskAsync()
    {
        VmfDocument document = MapCompilerTests.Room();
        document.Chunks[0].Children.Add(UnitMap.Box(PackedMaterial, (32, 32, 0), (64, 64, 64), 900));
        (InMemoryFileSystem files, _) = await MapCompilerTests.DiskAsync(document);

        InMemoryFileSystem disk = new();
        disk.AddText("maps/room.vmf", Encoding.UTF8.GetString(files.GetBytes(VPath.Create("maps/room.vmf"))!));
        disk.AddFile($"game/materials/{UnitMap.Plain}.vmt", files.GetBytes(VPath.Create($"materials/{UnitMap.Plain}.vmt")));
        disk.AddText("game/gameinfo.txt", GameInfoText);
        _ = new VpkFixture()
            .AddText($"materials/{PackedMaterial}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/packed\"\n}\n")
            .Write(disk, "game/pak01");
        return disk;
    }

    private static async Task<GameContentMounter.Result> MountAsync(InMemoryFileSystem disk, string gameInfo) =>
        await GameContentMounter.MountAsync(disk, VPath.Create(gameInfo), VPath.Empty);

    private static CompileRequest Request(InMemoryFileSystem disk, IContentFileSystem content) => new()
    {
        Source = MapSource.FromVmf(disk, VPath.Create("maps/room.vmf")),
        Content = content,
        Vrad = VradOptions.Default with { Bounces = 0 },
        Parallel = new CompileParallelism { MaxDegree = 2 },
        Output = CompileOutput.InMemory,
    };

    private static ContentRecordTarget Target(ContentRecording recording, InMemoryFileSystem disk) =>
        new(recording, disk, VPath.Create("out/c.zip"), GameInfoText, "ssmap all maps/room.vmf");

    private static Dictionary<string, byte[]> Unzip(InMemoryFileSystem files, string path)
    {
        using MemoryStream stream = new(files.GetBytes(VPath.Create(path)) ?? throw new FileNotFoundException(path));
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            entries[entry.FullName] = copy.ToArray();
        }

        return entries;
    }

    private static InMemoryFileSystem Extract(Dictionary<string, byte[]> zip, string directory)
    {
        InMemoryFileSystem files = new();
        foreach ((string name, byte[] bytes) in zip)
        {
            files.AddFile($"{directory}/{name}", bytes);
        }

        return files;
    }

    /// <summary>Content in a dictionary the fact can change between the compile and the bundle.</summary>
    private sealed class MutableContent : IContentFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public MutableContent With(string path, string text)
        {
            _files[path] = Encoding.UTF8.GetBytes(text);
            return this;
        }

        public void Remove(string path) => _files.Remove(path);

        public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ContentSource?>(
                _files.Keys.FirstOrDefault(k => string.Equals(k, path.Value, StringComparison.OrdinalIgnoreCase)) is { } key
                    ? new ContentSource(VPath.Create(key), "test-mount")
                    : null);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMemoryOwner<byte>?>(
                _files.TryGetValue(path.Value, out byte[]? bytes) ? new Owner(bytes) : null);
        }

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath path, long offset, int length, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _files.TryGetValue(path.Value, out byte[]? bytes) ? FileRange.Copy(bytes, offset, length) : null);
        }

        public async IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory,
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Throws the given exception whenever one path is looked up.</summary>
    private sealed class ThrowingContent(IContentFileSystem inner, string path, Func<Exception> error) : IContentFileSystem
    {
        public ValueTask<ContentSource?> ResolveAsync(VPath lookup, CancellationToken cancellationToken = default) =>
            Is(lookup) ? throw error() : inner.ResolveAsync(lookup, cancellationToken);

        public ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath lookup, CancellationToken cancellationToken = default) =>
            Is(lookup) ? throw error() : inner.ReadAsync(lookup, cancellationToken);

        public ValueTask<FileRange?> ReadRangeAsync(
            VPath lookup, long offset, int length, CancellationToken cancellationToken = default) =>
            Is(lookup) ? throw error() : inner.ReadRangeAsync(lookup, offset, length, cancellationToken);

        public IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory, string searchPattern = "*", CancellationToken cancellationToken = default) =>
            inner.EnumerateAsync(directory, searchPattern, cancellationToken);

        private bool Is(VPath lookup) => string.Equals(lookup.Value, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Serves one path's bytes as read, then different bytes on every later read.</summary>
    private sealed class ChangingContent(IContentFileSystem inner, string path) : IContentFileSystem
    {
        private int _reads;

        public ValueTask<ContentSource?> ResolveAsync(VPath lookup, CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(lookup, cancellationToken);

        public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath lookup, CancellationToken cancellationToken = default)
        {
            IMemoryOwner<byte>? owner = await inner.ReadAsync(lookup, cancellationToken);
            if (owner is null
                || !string.Equals(lookup.Value, path, StringComparison.OrdinalIgnoreCase)
                || Interlocked.Increment(ref _reads) == 1)
            {
                return owner;
            }

            byte[] changed = [.. owner.Memory.ToArray(), (byte)' '];
            owner.Dispose();
            return new Owner(changed);
        }

        // Through ReadAsync, so a range counts as a read and sees the changed
        // bytes the same way a whole read does.
        public async ValueTask<FileRange?> ReadRangeAsync(
            VPath lookup, long offset, int length, CancellationToken cancellationToken = default)
        {
            using IMemoryOwner<byte>? owner = await ReadAsync(lookup, cancellationToken);
            return owner is null ? null : FileRange.Copy(owner.Memory.Span, offset, length);
        }

        public IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory, string searchPattern = "*", CancellationToken cancellationToken = default) =>
            inner.EnumerateAsync(directory, searchPattern, cancellationToken);
    }

    private sealed class Owner(byte[] bytes) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => bytes;

        public void Dispose()
        {
        }
    }
}
