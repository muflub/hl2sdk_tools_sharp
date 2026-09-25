using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// <c>ssmap phys</c>: listing the physics libraries and choosing one.
/// </summary>
/// <remarks>
/// The CLI has almost no logic by construction, and what it has is tested. The
/// output of this command is the only place a user learns that their machine
/// has two libraries that disagree, so its wording is a feature rather than
/// decoration.
/// </remarks>
public class PhysCommandTests
{
    [Fact]
    public async Task ListMarksTheUsableBuildAndShowsTheUnusableOne()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Team Fortress 2/bin/linux64/vphysics.so", Elf64());
        fs.Add("common/Team Fortress 2/bin/vphysics.so", Elf32());

        StringWriter output = new();
        int code = await PhysCommand.RunAsync(fs, [VPath.Create("common")], ["list"], output);

        string text = output.ToString();
        Assert.Equal(Program.ExitSuccess, code);
        Assert.Contains("team-fortress-2", text, StringComparison.Ordinal);
        Assert.Contains("x64", text, StringComparison.Ordinal);
        Assert.Contains("x86", text, StringComparison.Ordinal);
        Assert.Contains(" * ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListSaysSoWhenThereIsNothingToFind()
    {
        StringWriter output = new();
        int code = await PhysCommand.RunAsync(
            new FakeFileSystem(), [VPath.Create("common")], ["list"], output);

        Assert.Equal(Program.ExitSuccess, code);
        Assert.Contains("no vphysics library found", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWarnsWhenTwoUsableLibrariesDisagree()
    {
        // The message that matters most: a machine with two games installed
        // will silently compile different collision depending on which library
        // was picked, so the list has to say so unprompted.
        FakeFileSystem fs = new();
        fs.Add("common/Team Fortress 2/bin/linux64/vphysics.so", Elf64());
        fs.Add("common/Half-Life 2/bin/linux64/vphysics.so", Elf64());

        StringWriter output = new();
        await PhysCommand.RunAsync(fs, [VPath.Create("common")], ["list"], output);

        Assert.Contains("different bytes", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("-vphysics", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListExplainsWhenEveryBuildIsThirtyTwoBit()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Team Fortress 2/bin/vphysics.so", Elf32());

        StringWriter output = new();
        await PhysCommand.RunAsync(fs, [VPath.Create("common")], ["list"], output);

        Assert.Contains("bin/linux64/", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectNamesTheChosenLibraryAndItsBuildId()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Team Fortress 2/bin/linux64/vphysics.so", Elf64());

        StringWriter output = new();
        int code = await PhysCommand.RunAsync(
            fs, [VPath.Create("common")], ["select", "team"], output);

        Assert.Equal(Program.ExitSuccess, code);
        Assert.Contains("selected team-fortress-2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectFailsRatherThanGuessing()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Half-Life 2/bin/linux64/vphysics.so", Elf64());
        fs.Add("common/Half-Life 2 Deathmatch/bin/linux64/vphysics.so", Elf64());

        StringWriter output = new();
        int code = await PhysCommand.RunAsync(
            fs, [VPath.Create("common")], ["select", "half"], output);

        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("ambiguous", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSubcommandIsAUsageErrorNotAnException()
    {
        StringWriter output = new();
        int code = await PhysCommand.RunAsync(
            new FakeFileSystem(), [VPath.Create("common")], ["frobnicate"], output);

        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("unknown subcommand", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPrintsPathsAUserCouldActuallyPaste()
    {
        // A VPath is rooted at its filesystem and carries no leading separator,
        // which is right for the library and wrong for a person: rooted at "/",
        // the table would otherwise print "home/someone/.steam/..." -- which
        // looks like a path and is not one.
        FakeFileSystem fs = new();
        fs.Add("home/someone/games/Team Fortress 2/bin/linux64/vphysics.so", Elf64());

        StringWriter output = new();
        await PhysCommand.RunAsync(
            fs, [VPath.Create("home/someone/games")], ["list"], output, "/");

        Assert.Contains(
            "/home/someone/games/Team Fortress 2/bin/linux64/vphysics.so",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [InstalledVPhysicsFact]
    public async Task DiscoveryFindsTheRealLibrariesOnThisMachine()
    {
        // The fixture facts above were built from the same assumptions as the
        // walk they exercise, so on their own they show only that the code
        // agrees with itself. This one points discovery at the actual Steam
        // install, through the real PhysicalFileSystem, and requires it to find
        // exactly what an independent directory walk can see.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IReadOnlyList<VPath> roots =
            [.. Program.DefaultSteamRoots(home).Select(r => VPath.Create(r))];

        StringWriter output = new();
        int code = await PhysCommand.RunAsync(
            new PhysicalFileSystem("/"), roots, ["list"], output, "/");

        Assert.Equal(Program.ExitSuccess, code);

        string text = output.ToString();
        int expected = InstalledVPhysics.AllPaths64().Count;
        int marked = text.Split('\n').Count(l => l.StartsWith(" * ", StringComparison.Ordinal));

        Assert.True(
            marked == expected,
            $"discovery marked {marked} usable librar(y/ies) but {expected} 64-bit vphysics.so "
            + $"exist under the Steam roots. Output was:{Environment.NewLine}{text}");
    }

    [Fact]
    public void DefaultSteamRootsCoverBothLayouts()
    {
        // Steam lives in ~/.steam/steam on a normal install and in
        // ~/.local/share/Steam on others; a machine may have either.
        IReadOnlyList<string> roots = Program.DefaultSteamRoots("/home/someone");

        Assert.Contains(roots, r => r.Contains(".steam", StringComparison.Ordinal));
        Assert.Contains(roots, r => r.Contains(".local", StringComparison.Ordinal));
        Assert.All(roots, r => Assert.EndsWith("common", r, StringComparison.Ordinal));
    }

    private static byte[] Elf64()
    {
        byte[] bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        BitConverter.TryWriteBytes(bytes.AsSpan(18), (ushort)62);
        return bytes;
    }

    private static byte[] Elf32()
    {
        byte[] bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 1;
        bytes[5] = 1;
        BitConverter.TryWriteBytes(bytes.AsSpan(18), (ushort)3);
        return bytes;
    }
}

/// <summary>
/// <c>ssmap phys cook</c>'s relaunch rule: the process must be STARTED with
/// <c>LD_LIBRARY_PATH</c> naming the library's directory (spike 0b).
/// </summary>
public class PhysRelaunchTests
{
    private const string Dir = "/games/sdk/bin/linux64";

    [Fact]
    public void NoPathMeansRelaunchWithJustTheDirectory() =>
        Assert.Equal(Dir, PhysCommand.RelaunchLibraryPath(null, Dir));

    [Fact]
    public void ThePathAlreadyNamingTheDirectoryNeedsNoRelaunch() =>
        Assert.Null(PhysCommand.RelaunchLibraryPath("/usr/lib:" + Dir + "/", Dir));

    [Fact]
    public void TheDirectoryIsPrependedSoItWins() =>
        Assert.Equal(Dir + ":/other/linux64", PhysCommand.RelaunchLibraryPath("/other/linux64", Dir));

    [Fact]
    public void APrefixOfTheDirectoryIsNotTheDirectory() =>
        Assert.NotNull(PhysCommand.RelaunchLibraryPath("/games/sdk/bin", Dir));
}
