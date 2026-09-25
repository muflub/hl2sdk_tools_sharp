using System.Buffers;
using System.Collections.Immutable;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// Finding the physics libraries installed on this machine, and choosing
/// between them.
/// </summary>
/// <remarks>
/// The reason any of this exists: spike 0b cooked one 32-unit cube through two
/// different builds of <c>vphysics.so</c> and got different bytes. So the
/// physics lump depends on which game's library was loaded, and a compile that
/// cannot name its choice is not reproducible.
/// </remarks>
public class VPhysicsDiscoveryTests
{
    [Fact]
    public async Task RejectsAFileThatIsNotAnElfImage()
    {
        using MemoryStream stream = new("this is not a library, it is a sentence."u8.ToArray());
        ElfIdentity identity = await ElfIdentityReader.ReadAsync(stream);

        Assert.Equal(ElfArchitecture.Unknown, identity.Architecture);
        Assert.Null(identity.BuildId);
    }

    [Fact]
    public async Task RejectsAFileTooShortToHoldAHeader()
    {
        using MemoryStream stream = new([0x7F, (byte)'E', (byte)'L', (byte)'F']);
        Assert.Equal(ElfIdentity.Unknown, await ElfIdentityReader.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadsThirtyTwoBitAsUnloadable()
    {
        // A synthetic ELF32 header: the magic, EI_CLASS 1, EI_DATA 1, and
        // EM_386 at offset 18. Enough to be classified without being a real
        // library, which is the point -- discovery must DESCRIBE a 32-bit
        // library rather than fail on it, because "the one you were about to
        // use is 32-bit" is the useful message.
        byte[] bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 1;
        bytes[5] = 1;
        BitConverter.TryWriteBytes(bytes.AsSpan(18), (ushort)3);

        using MemoryStream stream = new(bytes);
        ElfIdentity identity = await ElfIdentityReader.ReadAsync(stream);

        Assert.Equal(ElfArchitecture.X86, identity.Architecture);
        Assert.False(identity.IsLoadableHere);
    }

    [InstalledVPhysicsFact]
    public async Task ReadsTheRealLibrarysArchitectureAndBuildId()
    {
        string path = InstalledVPhysics.Path64()!;

        await using FileStream file = File.OpenRead(path);
        ElfIdentity identity = await ElfIdentityReader.ReadAsync(file);

        Assert.Equal(ElfArchitecture.X64, identity.Architecture);
        Assert.True(identity.IsLoadableHere);

        // A build id is 40 hex characters for SHA-1, which is what GNU ld
        // emits by default and what `readelf -n` prints for this library.
        Assert.NotNull(identity.BuildId);
        Assert.Matches("^[0-9a-f]{32,}$", identity.BuildId);
    }

    [ReadelfFact]
    public async Task BuildIdAgreesWithReadelf()
    {
        // A parser that agrees only with itself proves nothing. readelf is an
        // independent implementation of the same spec, so this is the fact that
        // says the bytes were really understood rather than plausibly decoded.
        string path = InstalledVPhysics.Path64()!;

        await using FileStream file = File.OpenRead(path);
        ElfIdentity identity = await ElfIdentityReader.ReadAsync(file);

        Assert.Equal(await Readelf.BuildIdAsync(path), identity.BuildId);
    }

    [TwoInstalledVPhysicsFact]
    public async Task TwoGamesShipPhysicsLibrariesWithDifferentBuildIds()
    {
        // The measurement this whole feature exists for. If every installed
        // game shipped the same build, selecting between them would not matter
        // -- and spike 0b found they cook the same cube to different bytes.
        List<string> ids = [];
        foreach (string candidate in InstalledVPhysics.AllPaths64())
        {
            await using FileStream file = File.OpenRead(candidate);
            ElfIdentity identity = await ElfIdentityReader.ReadAsync(file);
            if (identity.BuildId is not null)
            {
                ids.Add(identity.BuildId);
            }
        }

        Assert.True(
            ids.Distinct(StringComparer.Ordinal).Count() > 1,
            "every installed vphysics reported the same build id, so this machine cannot "
            + $"demonstrate the divergence that makes selection necessary: {string.Join(", ", ids)}");
    }

    [InstalledVPhysicsFact]
    public async Task TheSameLibraryReadsTheSameIdentityTwice()
    {
        string path = InstalledVPhysics.Path64()!;

        await using FileStream first = File.OpenRead(path);
        ElfIdentity a = await ElfIdentityReader.ReadAsync(first);
        await using FileStream second = File.OpenRead(path);
        ElfIdentity b = await ElfIdentityReader.ReadAsync(second);

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task DiscoveryReportsBothTheUsableAndTheUnusableBuild()
    {
        // A game install as Steam lays it out: a 64-bit library in bin/linux64
        // and a 32-bit one in bin/. Both must be reported, so a user who picked
        // the wrong one is told why rather than told nothing was found.
        FakeFileSystem fs = new();
        fs.Add("common/Team Fortress 2/bin/linux64/vphysics.so", Elf64());
        fs.Add("common/Team Fortress 2/bin/vphysics.so", Elf32());

        ImmutableArray<VPhysicsLibrary> found =
            await VPhysicsLocator.DiscoverAsync(fs, [VPath.Create("common")]);

        Assert.Equal(2, found.Length);
        Assert.Single(found, l => l.IsUsable);
        Assert.Single(found, l => !l.IsUsable);
        Assert.All(found, l => Assert.Equal("Team Fortress 2", l.Game));
    }

    [Fact]
    public async Task DiscoveryNamesTheGameFromItsInstallDirectory()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Source SDK Base 2013 Multiplayer/bin/linux64/vphysics.so", Elf64());

        ImmutableArray<VPhysicsLibrary> found =
            await VPhysicsLocator.DiscoverAsync(fs, [VPath.Create("common")]);

        VPhysicsLibrary only = Assert.Single(found);
        Assert.Equal("Source SDK Base 2013 Multiplayer", only.Game);
        Assert.Equal("source-sdk-base-2013-multiplayer", only.Key);
    }

    [Fact]
    public async Task DiscoveryFindsNothingWhenThereIsNothingAndThatIsNotAnError()
    {
        FakeFileSystem fs = new();
        fs.Add("common/Some Game/bin/linux64/something-else.so", Elf64());

        Assert.Empty(await VPhysicsLocator.DiscoverAsync(fs, [VPath.Create("common")]));
    }

    [Fact]
    public void SelectionTakesTheOnlyUsableLibraryWithoutBeingAsked()
    {
        VPhysicsLibrary[] libraries = [Usable("Team Fortress 2"), Unusable("Team Fortress 2")];

        Assert.True(VPhysicsLocator.TrySelect(libraries, null, out VPhysicsLibrary? chosen, out _));
        Assert.Equal("team-fortress-2", chosen!.Key);
    }

    [Fact]
    public void SelectionRefusesToGuessBetweenTwoUsableLibraries()
    {
        // THE point of the whole type. Two builds cook the same shape to
        // different bytes, so picking one silently produces a compile that
        // succeeds and a lump that differs.
        VPhysicsLibrary[] libraries = [Usable("Team Fortress 2"), Usable("Half-Life 2")];

        Assert.False(VPhysicsLocator.TrySelect(libraries, null, out VPhysicsLibrary? chosen, out string? problem));
        Assert.Null(chosen);
        Assert.Contains("different bytes", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionMatchesAGameKeyCaseInsensitively()
    {
        VPhysicsLibrary[] libraries = [Usable("Team Fortress 2"), Usable("Half-Life 2")];

        Assert.True(VPhysicsLocator.TrySelect(libraries, "TEAM-Fortress-2", out VPhysicsLibrary? chosen, out _));
        Assert.Equal("Team Fortress 2", chosen!.Game);
    }

    [Fact]
    public void SelectionAcceptsAUniquePrefix()
    {
        VPhysicsLibrary[] libraries = [Usable("Team Fortress 2"), Usable("Half-Life 2")];

        Assert.True(VPhysicsLocator.TrySelect(libraries, "team", out VPhysicsLibrary? chosen, out _));
        Assert.Equal("Team Fortress 2", chosen!.Game);
    }

    [Fact]
    public void SelectionRefusesAnAmbiguousPrefix()
    {
        VPhysicsLibrary[] libraries = [Usable("Half-Life 2"), Usable("Half-Life 2 Deathmatch")];

        Assert.False(VPhysicsLocator.TrySelect(libraries, "half", out _, out string? problem));
        Assert.Contains("ambiguous", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionMatchesAnExactPath()
    {
        VPhysicsLibrary a = Usable("Team Fortress 2");
        VPhysicsLibrary[] libraries = [a, Usable("Half-Life 2")];

        Assert.True(VPhysicsLocator.TrySelect(libraries, a.Path.Value, out VPhysicsLibrary? chosen, out _));
        Assert.Equal(a, chosen);
    }

    [Fact]
    public void SelectionExplainsWhenEveryCandidateIsThirtyTwoBit()
    {
        VPhysicsLibrary[] libraries = [Unusable("Team Fortress 2")];

        Assert.False(VPhysicsLocator.TrySelect(libraries, null, out _, out string? problem));
        Assert.Contains("bin/linux64", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionExplainsWhenNothingWasFoundAtAll()
    {
        Assert.False(VPhysicsLocator.TrySelect([], null, out _, out string? problem));
        Assert.Contains("no vphysics library was found", problem, StringComparison.Ordinal);
    }

    private static VPhysicsLibrary Usable(string game) => new(
        game,
        VPath.Create($"common/{game}/bin/linux64/vphysics.so"),
        new ElfIdentity(ElfArchitecture.X64, "0123456789abcdef0123456789abcdef01234567"),
        1_666_976);

    private static VPhysicsLibrary Unusable(string game) => new(
        game,
        VPath.Create($"common/{game}/bin/vphysics.so"),
        new ElfIdentity(ElfArchitecture.X86, null),
        1_200_000);

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
