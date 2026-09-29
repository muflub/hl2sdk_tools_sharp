//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <see cref="MachineConfig"/>: where <c>ssmap</c> looks, what the file may
/// say, what it adds to a command line, and that a flag always beats it.
/// </summary>
/// <remarks>
/// Every read goes through an <see cref="InMemoryFileSystem"/> and every
/// environment through a lambda, so no fact sees the machine's own config.
/// </remarks>
public sealed class MachineConfigTests
{
    private const string File = "cfg.txt";

    // ---------------------------------------------------------------- location

    [Fact]
    public void OnLinuxAndMacTheXdgConfigHomeIsUsedWhenItIsAbsolute()
    {
        Assert.Equal(
            Path.Join("/xdg", "ssmap", "config"),
            MachineConfig.DefaultPath(Env(("XDG_CONFIG_HOME", "/xdg")), windows: false, home: "/home/u"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/dir")]
    public void OtherwiseItIsDotConfigUnderHome(string? xdg)
    {
        Assert.Equal(
            Path.Join("/home/u", ".config", "ssmap", "config"),
            MachineConfig.DefaultPath(Env(("XDG_CONFIG_HOME", xdg)), windows: false, home: "/home/u"));
        Assert.Null(MachineConfig.DefaultPath(Env(("XDG_CONFIG_HOME", xdg)), windows: false, home: null));
        Assert.Null(MachineConfig.DefaultPath(Env(("XDG_CONFIG_HOME", xdg)), windows: false, home: ""));
    }

    [Fact]
    public void OnWindowsItIsUnderAppData()
    {
        Assert.Equal(
            Path.Join(@"C:\Users\u\AppData\Roaming", "ssmap", "config"),
            MachineConfig.DefaultPath(
                Env(("APPDATA", @"C:\Users\u\AppData\Roaming"), ("XDG_CONFIG_HOME", "/xdg")), windows: true, home: @"C:\Users\u"));
        Assert.Null(MachineConfig.DefaultPath(Env(), windows: true, home: @"C:\Users\u"));
    }

    // ---------------------------------------------------------------- format

    [Fact]
    public void SectionsCommentsAndBlankLinesAreRead()
    {
        MachineConfig config = MachineConfig.Parse(
            "\uFEFF# a machine\r\n"
            + "threads = 16\r\n"
            + "\r\n"
            + "  ; also a comment\n"
            + "[VVIS]\n"
            + "  Separator=512  \n"
            + "threads = 8\n"
            + "[vrad]\n"
            + "gpu = NVIDIA #2\n"
            + "gpu_depth = 3\n"
            + "[all]\n"
            + "threads = 32\n",
            File);

        Assert.Equal(File, config.Source);
        Assert.Equal("512", config.Get("vvis", "separator"));
        Assert.Equal("8", config.Get("vvis", "threads"));
        Assert.Equal("16", config.Get("vbsp", "threads"));
        Assert.Equal("16", config.Get("vrad", "threads"));
        Assert.Equal("NVIDIA #2", config.Get("vrad", "gpu"));
        Assert.Equal("3", config.Get("vrad", "gpu_depth"));
        Assert.Equal("32", config.Get("all", "threads"));

        // Keys a verb does not take are nothing to it.
        Assert.Null(config.Get("vbsp", "separator"));
        Assert.Null(config.Get("vvis", "gpu"));
        Assert.Null(config.Get("vvis", "colour"));
    }

    [Fact]
    public void AllFallsBackToTheStagesSectionAndThenToTheTopButNotForThreads()
    {
        MachineConfig config = MachineConfig.Parse(
            "threads = 12\nseparator = 256\n[vvis]\nthreads = 3\nseparator = 512\n[vrad]\ngpu = auto\n", File);

        Assert.Equal("512", config.Get("all", "separator"));
        Assert.Equal("auto", config.Get("all", "gpu"));
        Assert.Equal("12", config.Get("all", "threads"));
        Assert.Null(config.Get("all", "gpu_depth"));

        MachineConfig own = MachineConfig.Parse("[vvis]\nseparator = 512\n[all]\nseparator = 256\n", File);
        Assert.Equal("256", own.Get("all", "separator"));
        Assert.Equal("256", MachineConfig.Parse("separator = 256\n", File).Get("all", "separator"));
    }

    [Theory]
    [InlineData("threads 8", 1, "expected key = value")]
    [InlineData("[vvis\nseparator = 512", 1, "section header")]
    [InlineData("\n[vmex]", 2, "unknown section [vmex]")]
    [InlineData("[vvis]\nseperator = 512", 2, "unknown key \"seperator\"")]
    [InlineData("[vbsp]\nseparator = 512", 2, "not a vbsp setting")]
    [InlineData("[vvis]\ngpu = auto", 2, "not a vvis setting")]
    [InlineData("threads =", 1, "has no value")]
    [InlineData("threads = 0", 1, "at least 1")]
    [InlineData("threads = many", 1, "at least 1")]
    [InlineData("threads = -4", 1, "at least 1")]
    [InlineData("separator = 128", 1, "auto, 256 or 512")]
    [InlineData("gpu_depth = 0", 1, "gpu_depth is a whole number")]
    [InlineData("gpu_depth = 999", 1, "gpu_depth is a whole number")]
    [InlineData("[vvis]\nthreads = 2\n# fine\nTHREADS = 3", 4, "given twice in [vvis]")]
    [InlineData("threads = 2\nthreads = 3", 2, "given twice before the first section")]
    public void AMalformedLineIsAnErrorNamingTheFileAndLine(string text, int line, string says)
    {
        MachineConfigException error = Assert.Throws<MachineConfigException>(() => MachineConfig.Parse(text, File));

        Assert.Equal(line, error.Line);
        Assert.Equal(File, error.ConfigFile);
        Assert.StartsWith($"{File}:{line}: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(says, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameKeyInTwoSectionsIsNotADuplicate()
    {
        MachineConfig config = MachineConfig.Parse("threads = 1\n[vbsp]\nthreads = 2\n[vvis]\nthreads = 3\n", File);
        Assert.Equal("2", config.Get("vbsp", "threads"));
        Assert.Equal("3", config.Get("vvis", "threads"));
    }

    [Fact]
    public void EveryKeyIsAFlagEveryVerbItNamesTakes()
    {
        // The table is what the file may say; each row's flag must be one the
        // verbs it names parse, or a config would feed them an unknown option.
        foreach (MachineConfigKey key in MachineConfig.Keys)
        {
            Assert.StartsWith("-", key.Flag, StringComparison.Ordinal);
            Assert.All(key.Verbs, v => Assert.Contains(v, MachineConfig.Verbs));
        }

        Assert.Empty(StockArgs.ParseVvis(["-threads", "2", "-separator", "512", "m"]).Diagnostics);
        Assert.DoesNotContain(
            StockArgs.ParseVbsp(["-threads", "2", "m"]).Diagnostics, d => d.Message.Contains("threads", StringComparison.Ordinal));
        Assert.Empty(StockArgs.ParseVrad(["-threads", "2", "-gpu", "auto", "-gpu_depth", "3", "m"]).Diagnostics);
        Assert.False(AllCommand.Parse(["m.vmf", "-threads", "2", "-gpu", "auto", "-gpu_depth", "3", "--vvis", "-separator", "512"]).HasErrors);
    }

    // ---------------------------------------------------------------- applying

    [Fact]
    public void AStageGetsItsDefaultsFirstSoAnythingTypedComesAfter()
    {
        MachineConfig config = MachineConfig.Parse("threads = 6\n[vvis]\nseparator = 512\n", File);

        (string[] args, string[] added) = config.Apply("vvis", ["-fast", "map.bsp"]);

        Assert.Equal(["-threads", "6", "-separator", "512", "-fast", "map.bsp"], args);
        Assert.Equal(["-threads", "6", "-separator", "512"], added);
    }

    [Theory]
    [InlineData("-separator")]
    [InlineData("-SEPARATOR")]
    public void AFlagOnTheCommandLineKeepsItsDefaultOut(string spelling)
    {
        MachineConfig config = MachineConfig.Parse("threads = 6\n[vvis]\nseparator = 512\n", File);

        (string[] args, string[] added) = config.Apply("vvis", [spelling, "256", "map.bsp"]);

        Assert.Equal(["-threads", "6", spelling, "256", "map.bsp"], args);
        Assert.Equal(["-threads", "6"], added);
    }

    [Fact]
    public void AllGetsChainOptionsFirstAndAStagesAfterItsSectionSwitch()
    {
        MachineConfig config = MachineConfig.Parse(
            "threads = 6\n[vvis]\nseparator = 512\n[vrad]\ngpu = auto\ngpu_depth = 2\n", File);

        (string[] args, string[] added) = config.Apply("all", ["map.vmf", "--vrad", "-bounce", "2"]);

        Assert.Equal(
            ["-threads", "6", "-gpu", "auto", "-gpu_depth", "2", "map.vmf", "--vrad", "-bounce", "2", "--vvis", "-separator", "512"],
            args);
        Assert.Equal(["-threads", "6", "-gpu", "auto", "-gpu_depth", "2", "--vvis", "-separator", "512"], added);
    }

    [Fact]
    public void NothingIsAddedWhenEveryFlagIsTypedOrTheVerbTakesNone()
    {
        MachineConfig config = MachineConfig.Parse("[vvis]\nseparator = 512\n", File);

        Assert.Empty(config.Apply("vvis", ["-separator", "auto", "m"]).Added);
        (string[] args, string[] added) = config.Apply("vbsp", ["m.vmf"]);
        Assert.Equal(["m.vmf"], args);
        Assert.Empty(added);
    }

    // ---------------------------------------------------------------- precedence

    [Fact]
    public void FlagThenConfigThenAutoForVvis()
    {
        MachineConfig config = MachineConfig.Parse("[vvis]\nseparator = 512\nthreads = 3\n", File);

        // Config over the built-in default.
        StockArgsResult<VvisOptions> fromConfig = StockArgs.ParseVvis(config.Apply("vvis", ["m.bsp"]).Args);
        Assert.Equal(VisSeparatorPath.Vector512, fromConfig.Options.SeparatorPath);
        Assert.Equal(3, fromConfig.Threads);

        // The flag over the config, even when it names the default.
        StockArgsResult<VvisOptions> fromFlag = StockArgs.ParseVvis(
            config.Apply("vvis", ["-separator", "auto", "-threads", "5", "m.bsp"]).Args);
        Assert.Equal(VisSeparatorPath.Auto, fromFlag.Options.SeparatorPath);
        Assert.Equal(5, fromFlag.Threads);

        // No config: the built-in default.
        Assert.Equal(VisSeparatorPath.Auto, StockArgs.ParseVvis(["m.bsp"]).Options.SeparatorPath);
    }

    [Fact]
    public void FlagThenConfigForAllWithoutTheChainsOneDegreeRuleEverSeeingBoth()
    {
        MachineConfig config = MachineConfig.Parse("threads = 6\n[vvis]\nseparator = 512\n", File);

        AllArgs fromConfig = AllCommand.Parse(config.Apply("all", ["m.vmf"]).Args);
        Assert.False(fromConfig.HasErrors);
        Assert.Equal(6, fromConfig.Threads);
        Assert.Equal(VisSeparatorPath.Vector512, fromConfig.Vvis.SeparatorPath);

        // A section's -threads is the chain's one degree: the config's is not
        // added beside it, so there is no "give it once" conflict.
        AllArgs fromFlag = AllCommand.Parse(
            config.Apply("all", ["m.vmf", "--vvis", "-threads", "4", "-separator", "256"]).Args);
        Assert.False(fromFlag.HasErrors);
        Assert.Equal(4, fromFlag.Threads);
        Assert.Equal(VisSeparatorPath.Vector256, fromFlag.Vvis.SeparatorPath);
    }

    // ---------------------------------------------------------------- loading

    [Fact]
    public async Task TheDefaultFileIsReadWhenItExistsAndQuietlySkippedWhenNot()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "threads = 2\n");

        (MachineConfig? config, string[] args) = await MachineConfig.LoadAsync(disk, ["vvis", "m"], path);
        Assert.NotNull(config);
        Assert.Equal(path, config.Source);
        Assert.Equal(["vvis", "m"], args);

        (MachineConfig? none, string[] same) = await MachineConfig.LoadAsync(disk, ["vvis", "m"], Full("/nowhere/config"));
        Assert.Null(none);
        Assert.Equal(["vvis", "m"], same);

        Assert.Null((await MachineConfig.LoadAsync(disk, ["vvis"], defaultPath: null)).Config);
    }

    [Fact]
    public async Task ConfigNamesAnotherFileAnywhereOnTheLine()
    {
        (InMemoryFileSystem disk, string other) = Disk("/etc/ssmap.conf", "[vvis]\nseparator = 512\n");

        (MachineConfig? config, string[] args) = await MachineConfig.LoadAsync(
            disk, ["vvis", "--config", other, "m"], Full("/nowhere/config"));
        Assert.Equal(other, config!.Source);
        Assert.Equal("512", config.Get("vvis", "separator"));
        Assert.Equal(["vvis", "m"], args);
    }

    [Fact]
    public async Task NoConfigReadsNothingEvenWhereTheDefaultExists()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "threads = 2\n");

        (MachineConfig? config, string[] args) = await MachineConfig.LoadAsync(disk, ["--no-config", "vvis", "m"], path);
        Assert.Null(config);
        Assert.Equal(["vvis", "m"], args);
    }

    [Theory]
    [InlineData(new[] { "vvis", "--config" }, "needs a file")]
    [InlineData(new[] { "vvis", "--config", "" }, "needs a file")]
    [InlineData(new[] { "--config", "/a", "--config", "/b", "vvis" }, "given twice")]
    [InlineData(new[] { "--config", "/a", "--no-config", "vvis" }, "not both")]
    [InlineData(new[] { "--config", "/missing/config", "vvis" }, "no such file")]
    public async Task TheSwitchesAreRefusedWhenMisused(string[] line, string says)
    {
        MachineConfigException error = await Assert.ThrowsAsync<MachineConfigException>(
            () => MachineConfig.LoadAsync(new InMemoryFileSystem(), line, defaultPath: null));
        Assert.Contains(says, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, error.Line);
        Assert.Null(error.ConfigFile);
    }

    // ---------------------------------------------------------------- ssmap

    [Fact]
    public async Task SsmapSaysWhichConfigItReadAndWhatItAdded()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "[vvis]\nseparator = 512\n");
        StringWriter output = new();

        // No map: the command stops at its own usage error, after the line.
        int exit = await Program.RunAsync(["vvis"], output, new MachineConfigLocation(disk, path));

        Assert.Equal(Program.ExitUsage, exit);
        string[] lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal($"ssmap: config {path}: -separator 512", lines[0]);
    }

    [Fact]
    public async Task SsmapSaysNothingWithoutAConfigAndSaysSoWhenItAddsNothing()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "[vvis]\nseparator = 512\n");

        StringWriter quiet = new();
        await Program.RunAsync(["--no-config", "vvis"], quiet, new MachineConfigLocation(disk, path));
        Assert.DoesNotContain("config", quiet.ToString(), StringComparison.Ordinal);

        StringWriter none = new();
        await Program.RunAsync(["vvis"], none, new MachineConfigLocation(disk, Full("/nowhere/config")));
        Assert.DoesNotContain("config", none.ToString(), StringComparison.Ordinal);

        StringWriter typed = new();
        await Program.RunAsync(["vvis", "-separator", "256"], typed, new MachineConfigLocation(disk, path));
        Assert.StartsWith($"ssmap: config {path} (nothing to add)", typed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedConfigStopsTheCommandWithTheFileAndLine()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "[vvis]\nthreads = none\n");
        StringWriter output = new();

        int exit = await Program.RunAsync(["vvis", "m.bsp"], output, new MachineConfigLocation(disk, path));

        Assert.Equal(Program.ExitUsage, exit);
        Assert.StartsWith($"ssmap: config: {path}:2: threads = none", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reading", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerbWithoutDefaultsNeverReadsTheConfig()
    {
        (InMemoryFileSystem disk, string path) = Disk("/home/u/.config/ssmap/config", "this is not a config\n");
        StringWriter output = new();

        int exit = await Program.RunAsync(["compliance", "vvis"], output, new MachineConfigLocation(disk, path));

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.DoesNotContain("config", output.ToString().Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverloadWithoutALocationReadsOnlyANamedConfig()
    {
        // The facts' own entry point must never pick up the machine's file,
        // but --config still works through it.
        StringWriter output = new();
        int exit = await Program.RunAsync(["--config", Full("/definitely/not/here/config"), "vvis"], output);
        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("no such file", output.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static Func<string, string?> Env(params (string Name, string? Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    private static string Full(string path) => Path.GetFullPath(path);

    private static (InMemoryFileSystem Disk, string Path) Disk(string path, string text)
    {
        string full = Full(path);
        Assert.True(VPath.TryCreate(full, out VPath file));
        InMemoryFileSystem disk = new();
        disk.AddFile(file, Encoding.UTF8.GetBytes(text));
        return (disk, full);
    }
}
