using System.Buffers;
using System.Text;

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// The <c>&lt;n&gt;.gameinfo</c> sidecar every stock reference carries
/// (<see cref="StockProvenance"/>): a reference is compared only against the
/// content it was compiled with, and one that does not say fails.
/// </summary>
public class StockProvenanceTests
{
    private const string GameInfoText = "\"GameInfo\"\n{\n FileSystem\n {\n  SearchPaths\n  {\n   game+mod \"|gameinfo_path|content\"\n  }\n }\n}\n";

    private static InMemoryFileSystem Corpus() => new InMemoryFileSystem()
        .AddText("corpus/game/gameinfo.txt", GameInfoText)
        .AddText("corpus/game/content/materials/mine.vmt", "mine")
        .AddText("other/game/gameinfo.txt", GameInfoText)
        .AddText("other/game/content/materials/mine.vmt", "other")
        .AddText("refs/a.bsp", "bsp");

    private static readonly VPath Refs = VPath.Create("refs");

    [Fact]
    public async Task AnAbsoluteSidecarNamesItsGameInfo()
    {
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "/corpus/game/gameinfo.txt\n");

        Assert.Equal("corpus/game/gameinfo.txt", (await StockProvenance.GameInfoPathAsync(disk, Refs, "a")).Value);
    }

    [Fact]
    public async Task ARelativeSidecarIsRelativeToItsDirectory()
    {
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "../corpus/game/gameinfo.txt");

        Assert.Equal("corpus/game/gameinfo.txt", (await StockProvenance.GameInfoPathAsync(disk, Refs, "a")).Value);
    }

    [Fact]
    public async Task AMissingSidecarFailsRatherThanFallingBack()
    {
        StockProvenanceException thrown = await Assert.ThrowsAsync<StockProvenanceException>(
            () => StockProvenance.GameInfoPathAsync(Corpus(), Refs, "a"));

        Assert.Contains("refs/a.gameinfo is missing", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySidecarFails()
    {
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "  \n");

        await Assert.ThrowsAsync<StockProvenanceException>(() => StockProvenance.GameInfoPathAsync(disk, Refs, "a"));
    }

    [Fact]
    public async Task ASidecarNamingTwoGameInfosFails()
    {
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "/corpus/game/gameinfo.txt\n/other/game/gameinfo.txt\n");

        await Assert.ThrowsAsync<StockProvenanceException>(() => StockProvenance.GameInfoPathAsync(disk, Refs, "a"));
    }

    [Fact]
    public async Task ASidecarNamingAGameInfoThatIsGoneFails()
    {
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "/gone/game/gameinfo.txt");

        await Assert.ThrowsAsync<StockProvenanceException>(() => StockProvenance.GameInfoPathAsync(disk, Refs, "a"));
    }

    [Fact]
    public async Task TheSidecarsContentIsWhatIsMounted()
    {
        // Two gameinfos with the same layout and different content: the one
        // the sidecar names is the one read.
        InMemoryFileSystem disk = Corpus().AddText("refs/a.gameinfo", "/other/game/gameinfo.txt");

        await using ContentFileSystem content = (await StockProvenance.MountAsync(disk, Refs, "a")).Content;
        using IMemoryOwner<byte>? owner = await content.ReadAsync(VPath.Create("materials/mine.vmt"));

        Assert.Equal("other", Encoding.UTF8.GetString(owner!.Memory.Span));
    }

    [Fact]
    public async Task EachMapReadsItsOwnSidecar()
    {
        InMemoryFileSystem disk = Corpus()
            .AddText("refs/a.gameinfo", "/corpus/game/gameinfo.txt")
            .AddText("refs/b.gameinfo", "/other/game/gameinfo.txt");

        Assert.Equal("other/game/gameinfo.txt", (await StockProvenance.GameInfoPathAsync(disk, Refs, "b")).Value);
    }
}
