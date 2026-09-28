//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bench;

/// <summary>
/// <c>ssmap bench</c>'s exit code says whether every run of the series
/// compiled.
/// </summary>
/// <remarks>
/// A driver such as <c>tools/compile_perf.py</c> primes a warm-cache cell with
/// one untimed bench run and judges it by the exit code. When bench exited 0
/// whatever its runs did, a prime whose compile failed left the store empty
/// and the "warm" cell timed a cold compile, recorded as ok. The ledger still
/// holds every run either way; the exit code is what a caller that reads no
/// ledger sees.
/// </remarks>
public sealed class BenchExitCodeTests
{
    [Fact]
    public async Task ASeriesWhoseRunsFailExitsWithFailure()
    {
        using TempDir dir = new();
        string ledger = Path.Combine(dir.Path, "bench.jsonl");

        int rc = await Bench(dir, Path.Combine(dir.Path, "missing.vmf"), ledger);

        Assert.Equal(Program.ExitFailure, rc);
        BenchSample sample = BenchSample.FromJsonLine(Assert.Single(File.ReadAllLines(ledger)));
        Assert.False(sample.Ok);
    }

    [Fact]
    public async Task ASeriesWhoseRunsAllCompileExitsWithSuccess()
    {
        using TempDir dir = new();
        // vbsp mounts a game, so the room goes in the maps folder of a mod
        // whose only search path is itself: no Steam content, and nothing
        // outside the temp folder.
        string mod = Path.Combine(dir.Path, "mod");
        Directory.CreateDirectory(Path.Combine(mod, "maps"));
        File.WriteAllText(Path.Combine(mod, "gameinfo.txt"), GameInfoText);
        string vmf = Path.Combine(mod, "maps", "room.vmf");
        File.WriteAllText(vmf, TestMapCatalog.SealedRoom().Write());
        string ledger = Path.Combine(dir.Path, "bench.jsonl");

        int rc = await Bench(dir, vmf, ledger, mod);

        Assert.Equal(Program.ExitSuccess, rc);
        BenchSample sample = BenchSample.FromJsonLine(Assert.Single(File.ReadAllLines(ledger)));
        Assert.True(sample.Ok, sample.Failure);
    }

    private const string GameInfoText = """
        "GameInfo"
        {
        	game	"Bench"
        	FileSystem
        	{
        		SteamAppId	243750
        		SearchPaths
        		{
        			game	|gameinfo_path|.
        		}
        	}
        }
        """;

    private static Task<int> Bench(TempDir dir, string map, string ledger, string? game = null) =>
        BenchCommand.RunAsync(
            new PhysicalFileSystem("/"), [],
            [
                "--map", map, "--stages", "vbsp", "--threads", "1", "--runs", "1", "--warmups", "0",
                "--workdir", dir.Path, "--out", ledger, .. game is null ? Array.Empty<string>() : ["--game", game],
            ],
            new StringWriter());

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "bench-exit-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
