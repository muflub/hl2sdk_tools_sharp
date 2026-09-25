using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// Portal 2's sibling content mount (<c>GameContentMounter</c>, as the
/// reference's Portal 2 mount performs it):
/// appid 620 mounts <c>&lt;gamedir&gt;/update</c> and every contiguous
/// <c>&lt;gamedir&gt;/portal2_dlcN</c> ahead of the gameinfo's own search paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are structural facts on <see cref="InMemoryFileSystem"/>, not oracle
/// facts.</b> Real Portal 2 content cannot be mounted on this machine (the T0
/// ruling: no Portal 2 install in the catalogue, so no stock dump exists to
/// compare a real mount against here). What these facts pin is the observable
/// mount list and priority — which directories and archives joined, in what
/// order, gated on what — against the reference mount's loop structure. The bytes inside a
/// real Portal 2 pak stay unpinned.
/// </para>
/// <para>
/// How the reference mount works: the caller fires once
/// <c>SteamAppId == 620</c> — unconditionally on the appid, before and
/// independently of the <c>SearchPaths</c> walk, and independently of any preset
/// flag. The loops probe <c>dlc1</c>, <c>dlc2</c>, … while the directory exists,
/// stop at the first miss, never past 99, then mount each found DLC's
/// <c>pak01_dir.vpk</c> before its directory, highest-numbered DLC first; the
/// update directory gates its own vpk-then-directory pair.
/// </para>
/// </remarks>
public class Portal2MountTests
{
    private const string GameDir = "portal2";

    private static GameInfo P2() => Appid(620);

    private static GameInfo Appid(int appid) =>
        GameInfo.Parse(
            "\"GameInfo\"\n{\n"
            + "    game\t\"portal2\"\n"
            + "    FileSystem\n    {\n        SteamAppId\t" + appid
            + "\n        SearchPaths\n        {\n            game+mod\tbase.vpk\n        }\n    }\n}\n");

    private static GameContentRoots Roots() => new(VPath.Create(GameDir), VPath.Create(GameDir));

    /// <summary>
    /// Writes a DLC's <c>pak01_dir.vpk</c> holding one marked file. In
    /// <see cref="InMemoryFileSystem"/> a directory exists exactly when it
    /// holds a file, so this is also what makes the DLC probeable at all.
    /// which is why a fixture that wants a DLC ABSENT writes nothing there.
    /// </summary>
    private static void AddDlc(InMemoryFileSystem fs, int n, string file = "materials/marker.vmt") =>
        new VpkFixture().AddText(file, $"from dlc{n}").Write(fs, $"{GameDir}/portal2_dlc{n}/pak01");

    private static async Task<GameContentMounter.Result> MountAsync(
        InMemoryFileSystem fs, GameInfo info) =>
        await GameContentMounter.MountAsync(fs, info, Roots()).ConfigureAwait(false);

    private static string[] Names(GameContentMounter.Result r) =>
        [.. r.Content.Mounts.Select(m => m.Name)];

    private static int IndexOf(string[] names, string suffix) =>
        Array.FindIndex(names, n => n.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task<string> ReadAsync(GameContentMounter.Result r, string path)
    {
        using System.Buffers.IMemoryOwner<byte>? bytes = await r.Content
            .ReadAsync(VPath.Create(path)).ConfigureAwait(false);
        Assert.NotNull(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes.Memory.Span);
    }

    // ---- the gate is the appid, and only the appid ---.

    [Fact]
    public async Task SiblingContentIsInvisibleToAnotherAppid()
    {
        // Same bytes on disk, appid 630 instead of 620: the reference caller
        // checks the appid and nothing else, so Alien Swarm mounts none of it.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        AddDlc(fs, 1);
        new VpkFixture().AddText("materials/update.vmt", "update").Write(fs, GameDir + "/update/pak01");

        string[] names = Names(await MountAsync(fs, Appid(630)));

        Assert.DoesNotContain(names, n => n.Contains("dlc", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstallWithNoUpdateAndNoDlcPaysNothingForTheCheck()
    {
        // Nothing under update/ or portal2_dlcN*: the probes answer "absent"
        // and the mount list is just the gameinfo's own path.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");

        GameContentMounter.Result r = await MountAsync(fs, P2());

        Assert.Equal(["base_dir.vpk"], Names(r));
    }

    // ---- priority: the sibling content outranks the gameinfo's own paths ---.

    [Fact]
    public async Task DlcContentOutranksTheGamesOwnVpk()
    {
        // The reference mounter mounts the sibling content BEFORE walking SearchPaths, so
        // first-match-wins answers from dlc1 for a file the base game has too.
        // Proven by CONTENT, not by mount name: every pak01_dir.vpk reports
        // the same bare archive name, so only the bytes say who answered.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/same.vmt", "from base").Write(fs, GameDir + "/base");
        AddDlc(fs, 1, "materials/same.vmt");

        GameContentMounter.Result r = await MountAsync(fs, P2());
        Assert.Equal("from dlc1", await ReadAsync(r, "materials/same.vmt"));
    }

    [Fact]
    public async Task TheDlcMountsComeHighestFirstWithVpkBeforeDirectory()
    {
        // The reference mounter walks back DOWN from the highest found DLC, and within each
        // DLC mounts pak01_dir.vpk before the directory itself.
        //
        // The ordering is proven by CONTENT bytes, not by mount Name: every
        // archive mount reports the same bare "pak01_dir.vpk", so names cannot
        // tell dlc3's archive from dlc1's. Directories do carry distinct names
        // ("<gamedir>/portal2_dlcN"), so those are checked by name; every
        // archive-to-archive and archive-to-directory edge is a read whose
        // winner can only be the mount the reference order says is first.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        for (int n = 1; n <= 3; n++)
        {
            // same.vmt sits in all three archives with distinct bytes: the
            // answer names the first archive. uN.vmt sits in DLC n's archive
            // AND its own directory (nothing earlier holds it): the answer
            // says whether that archive outranks its own directory. vN.vmt
            // sits in DLC n's archive and DLC n+1's DIRECTORY: the answer
            // pins the interleaving (archive n before directory n+1).
            new VpkFixture()
                .AddText("materials/same.vmt", $"from vpk {n}")
                .AddText($"materials/u{n}.vmt", $"vpk {n}")
                .AddText($"materials/v{n}.vmt", $"vpk {n}")
                .Write(fs, $"{GameDir}/portal2_dlc{n}/pak01");
            fs.AddText($"{GameDir}/portal2_dlc{n}/materials/u{n}.vmt", $"dir {n}");
            if (n < 3)
            {
                fs.AddText($"{GameDir}/portal2_dlc{n + 1}/materials/v{n}.vmt", $"dir {n + 1}");
            }
        }

        GameContentMounter.Result r = await MountAsync(fs, P2());
        string[] names = Names(r);
        string all = string.Join(",", names);

        // Presence: three bare-named archive mounts, three named directories.
        Assert.Equal(3, names.Count(n => n == "pak01_dir.vpk"));
        int dir1 = IndexOf(names, "/portal2_dlc1");
        int dir2 = IndexOf(names, "/portal2_dlc2");
        int dir3 = IndexOf(names, "/portal2_dlc3");
        Assert.True(dir3 >= 0 && dir2 >= 0 && dir1 >= 0, "all three directory mounts present: " + all);
        Assert.True(dir3 < dir2 && dir2 < dir1, "highest directory first: " + all);

        Assert.Equal("from vpk 3", await ReadAsync(r, "materials/same.vmt"));
        Assert.Equal("vpk 1", await ReadAsync(r, "materials/u1.vmt"));
        Assert.Equal("vpk 2", await ReadAsync(r, "materials/u2.vmt"));
        Assert.Equal("vpk 3", await ReadAsync(r, "materials/u3.vmt"));
        // vN is in archive N and directory N+1: directory N+1 mounts earlier
        // (the walk descends), so it must answer.
        Assert.Equal("dir 2", await ReadAsync(r, "materials/v1.vmt"));
        Assert.Equal("dir 3", await ReadAsync(r, "materials/v2.vmt"));
    }

    [Fact]
    public async Task TheUpdateVpkPrecedesTheUpdateDirectory()
    {
        // Same content-bytes proof as the DLC fact: the update archive mounts
        // under the bare name "pak01_dir.vpk", indistinguishable from the DLC
        // archives by Name, so the shared marker's bytes decide who answered.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        new VpkFixture().AddText("materials/same.vmt", "from update vpk").Write(fs, GameDir + "/update/pak01");
        fs.AddText(GameDir + "/update/materials/same.vmt", "from update dir");

        GameContentMounter.Result r = await MountAsync(fs, P2());
        string[] names = Names(r);
        string all = string.Join(",", names);

        Assert.Contains("pak01_dir.vpk", names);
        int dir = IndexOf(names, "/update");
        Assert.True(dir >= 0, "update directory mounted: " + all);
        Assert.Equal("from update vpk", await ReadAsync(r, "materials/same.vmt"));
    }

    [Fact]
    public async Task AnUnreadableUpdateVpkSkipsItselfButNotItsDirectory()
    {
        // The gate is the directory's existence, which the file system answers
        // from its contents; a stray non-VPK file where pak01_dir.vpk should be
        // is skipped by the archive open, while the directory's loose files
        // still mount. A broken sibling never fails the mount.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        fs.AddText(GameDir + "/update/pak01_dir.vpk", "not a vpk");
        fs.AddText(GameDir + "/update/loose.vmt", "loose");

        GameContentMounter.Result r = await MountAsync(fs, P2());
        string[] names = Names(r);

        Assert.Contains(names, n => n.EndsWith("/update", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Contains("update/pak01_dir.vpk", StringComparison.Ordinal));
        Assert.NotNull(await r.Content.ResolveAsync(VPath.Create("loose.vmt")));
    }

    // ---- contiguity, and the cap ---.

    [Fact]
    public async Task DlcNumberingIsContiguousAndStopsAtTheFirstMiss()
    {
        // dlc1 and dlc2 mount; dlc3 is missing; dlc4 exists but is never
        // reached — the reference loop breaks on the first miss.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        AddDlc(fs, 1);
        AddDlc(fs, 2);
        AddDlc(fs, 4);

        string[] names = Names(await MountAsync(fs, P2()));

        Assert.Contains(names, n => n.EndsWith("/portal2_dlc2", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("portal2_dlc4", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("portal2_dlc3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThereIsNoDlcMountWhenDlc1IsMissing()
    {
        // Contiguous from 1: dlc5 alone is nothing.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        AddDlc(fs, 5);

        Assert.DoesNotContain(
            Names(await MountAsync(fs, P2())),
            n => n.Contains("dlc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheProbeIsCappedAtDlc99()
    {
        // Every directory through dlc100 exists; the reference counter never
        // reaches 100, so dlc99 is the floor and dlc100 stays unmounted.
        InMemoryFileSystem fs = new();
        new VpkFixture().AddText("materials/base.vmt", "base").Write(fs, GameDir + "/base");
        for (int n = 1; n <= 100; n++)
        {
            AddDlc(fs, n);
        }

        string[] names = Names(await MountAsync(fs, P2()));

        Assert.Contains(names, n => n.EndsWith("/portal2_dlc99", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("portal2_dlc100", StringComparison.Ordinal));
        Assert.Equal(99 * 2, names.Length - 1); // base + 99 vpk/dir pairs
    }

    // ---- the parsed gameinfo travels with the mount ----

    [Fact]
    public async Task TheMountResultCarriesTheGameinfoItMountedFrom()
    {
        // The format host reads the appid and the Tools key off THIS, never off
        // a re-read of whatever is on disk later.
        GameContentMounter.Result r = await MountAsync(new(), P2());

        Assert.NotNull(r.GameInfo);
        Assert.Equal(620, r.GameInfo.SteamAppId);
    }
}
