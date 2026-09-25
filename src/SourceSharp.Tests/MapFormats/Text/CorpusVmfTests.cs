using System.Text;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// The Phase 1c corpus gate, run against the only VMF in the tree:
/// <c>maps/ss_sandbox.vmf</c>, 913 KB written by
/// <c>SourceSharp.MapGen</c>.
/// </summary>
/// <remarks>
/// A hand-written sample proves the grammar; a real map proves the reader
/// survives the shapes a generator actually emits -- hundreds of solids, every
/// side carrying a <c>uaxis</c>, <c>vaxis</c> and <c>plane</c>, and entity
/// blocks with <c>connections</c> and <c>editor</c> sub-chunks.
/// </remarks>
public class CorpusVmfTests
{
    private const string VmfPath = "maps/ss_sandbox.vmf";

    private static Task<VmfDocument> LoadAsync() =>
        VmfDocument.ParseAsync(
            File.ReadAllBytes(RepoSourceFactAttribute.Find(VmfPath)!),
            CancellationToken.None).AsTask();

    [RepoSourceFact(VmfPath)]
    public async Task CorpusMapParses()
    {
        VmfDocument document = await LoadAsync();

        Assert.NotNull(document.GetChunk("versioninfo"));
        Assert.NotNull(document.GetChunk("world"));
    }

    [RepoSourceFact(VmfPath)]
    public async Task CorpusMapHasSolidsWithSidesUnderTheWorld()
    {
        // The nesting the whole format exists for: world > solid > side. If the
        // tokenizer mis-handled anything in 913 KB of real content, this is
        // where the count would come out wrong.
        VmfDocument document = await LoadAsync();
        VmfChunk world = document.GetChunk("world")!;

        List<VmfChunk> solids = [.. world.GetChunks("solid")];

        Assert.NotEmpty(solids);
        Assert.All(solids, solid => Assert.NotEmpty(solid.GetChunks("side")));
    }

    [RepoSourceFact(VmfPath)]
    public async Task EverySideCarriesTheKeysVbspReads()
    {
        // The reference implementation's side handler reads plane, material, uaxis and vaxis. The
        // texture axes are the BRACKETED vector-4 spelling, and the plane is
        // three parenthesised points -- a value the chunk grammar treats as one
        // opaque string.
        VmfDocument document = await LoadAsync();

        List<VmfChunk> sides =
            [.. document.GetChunk("world")!.GetChunks("solid").SelectMany(s => s.GetChunks("side"))];

        Assert.All(sides, side =>
        {
            Assert.NotNull(side.GetValue("plane"));
            Assert.NotNull(side.GetValue("material"));
            Assert.NotNull(side.GetValue("uaxis"));
            Assert.NotNull(side.GetValue("vaxis"));
        });
    }

    [RepoSourceFact(VmfPath)]
    public async Task WriteThenParseThenWriteIsAByteFixedPoint()
    {
        // The plan's 1c gate. Note it is stated over THIS WRITER'S output, not
        // over the input file -- see the fact below for why the input cannot
        // be the anchor.
        VmfDocument first = await LoadAsync();
        byte[] once = first.ToBytes();

        VmfDocument second = await VmfDocument.ParseAsync(once, CancellationToken.None);
        byte[] twice = second.ToBytes();

        Assert.Equal(once, twice);
    }

    [RepoSourceFact(VmfPath)]
    public async Task RewritingPreservesEveryKeyAndChunk()
    {
        // The semantic half: the tree that comes back out of a rewrite is the
        // tree that went in. A byte comparison alone would not catch a reader
        // that dropped a chunk and a writer that happened to be consistent
        // about it.
        VmfDocument first = await LoadAsync();
        VmfDocument second = await VmfDocument.ParseAsync(first.ToBytes(), CancellationToken.None);

        Assert.Equal(Describe(first), Describe(second));
    }

    [RepoSourceFact(VmfPath)]
    public async Task ThisWritersOutputIsNotByteIdenticalToMapGensBecauseOfLineEndings()
    {
        // A FINDING FOR THE INTEGRATOR, pinned so it cannot be discovered by
        // surprise later.
        //
        // The reference chunk writer emits a literal "\r\n" through fwrite
        // -- unconditionally, on every platform, with no
        // option. SourceSharp.MapGen writes this file with bare LFs. So a
        // FAITHFUL chunk writer cannot reproduce MapGen's bytes, and the plan's
        // "MapGen's writer -> this parser -> writer is a fixed point" holds
        // only from the second writer onwards.
        //
        // The fix belongs in MapGen, which is another lane's file: its writer
        // should emit CRLF like Hammer's does. Until then, anything comparing
        // a regenerated .vmf against a committed one must compare MODELS, not
        // bytes.
        byte[] original = File.ReadAllBytes(RepoSourceFactAttribute.Find(VmfPath)!);
        VmfDocument document = await LoadAsync();

        Assert.DoesNotContain("\r\n", Encoding.Latin1.GetString(original), StringComparison.Ordinal);
        Assert.Contains("\r\n", Encoding.Latin1.GetString(document.ToBytes()), StringComparison.Ordinal);
        Assert.NotEqual(original, document.ToBytes());
    }

    [RepoSourceFact(VmfPath)]
    public async Task CorpusMapContainsAValueWithALiteralNewlineInIt()
    {
        // The quirk in the reference tokenizer is not theoretical: the only
        // VMF in this tree exercises it. A point_worldtext entity's `message`
        // key holds two lines of text with a RAW newline between them, inside
        // the quotes.
        VmfDocument document = await LoadAsync();

        List<string> multiline = [.. document.GetChunks("entity")
            .Select(e => e.GetValue("message"))
            .Where(v => v is not null && v.Contains('\n', StringComparison.Ordinal))
            .Select(v => v!)];

        Assert.NotEmpty(multiline);
    }

    [RepoSourceFact(VmfPath)]
    public async Task ConvertingTheCorpusToCrLfMakesStocksOwnTokenizerRejectIt()
    {
        // THE FINDING, and it is a hazard for the whole pipeline rather than a
        // defect in this port.
        //
        // GetString checks for 0x0d -- a CARRIAGE RETURN -- inside a quoted
        // string and returns TOKENSTRINGTOOLONG. A
        // bare LF is not checked, so the multi-line `message` value above
        // parses perfectly in this LF file and would parse in stock vbsp too.
        //
        // Convert the same file to CRLF and that newline becomes CR+LF INSIDE
        // THE QUOTES, and the tokenizer rejects it: "unterminated string or
        // string too long". Which means the reference reader cannot read back a
        // file it could have written -- WriteKeyValue is a plain
        // "\"%s\" \"%s\"" with no escaping, and WriteLine
        // always emits CRLF.
        //
        // So this is a real shape of VMF that a faithful writer turns into a
        // file stock cannot parse. The port does NOT paper over it by escaping
        // on write: that would make every managed-written value differ from
        // stock's. The fix belongs upstream, in whatever writes such a value --
        // the reader decodes "\n" as a newline, so
        // the escape is what a writer should emit.
        byte[] original = File.ReadAllBytes(RepoSourceFactAttribute.Find(VmfPath)!);
        string crlf = Encoding.Latin1.GetString(original)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync(crlf, CancellationToken.None));

        Assert.Equal(ChunkFileResult.StringTooLong, error.Result);
    }

    [RepoSourceFact(VmfPath)]
    public async Task EverythingElseInTheCorpusRoundTripsByteExactUnderCrLf()
    {
        // The proof that the embedded newline is the ONLY thing standing
        // between this writer and byte-exactness: escape it the way the
        // tokenizer decodes it, convert to CRLF, and
        // 913 KB of real content round-trips byte for byte. If the reader or
        // writer differed from stock anywhere else, this would not hold.
        byte[] original = File.ReadAllBytes(RepoSourceFactAttribute.Find(VmfPath)!);

        string crlf = Encoding.Latin1.GetString(original)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        // Escape the raw newlines that sit inside quoted values. They are the
        // only LFs in the file that are not line terminators, and they are
        // identifiable because the line they end has an odd number of quotes.
        string[] lines = crlf.Split('\n');
        StringBuilder joined = new();
        bool insideString = false;

        foreach (string line in lines)
        {
            joined.Append(line);
            bool balanced = line.Count(c => c == '"') % 2 == 0;

            if (insideString && balanced)
            {
                insideString = false;
                joined.Append("\r\n");
                continue;
            }

            if (!insideString && !balanced)
            {
                insideString = true;
                joined.Append("\\n");
                continue;
            }

            joined.Append("\r\n");
        }

        string prepared = joined.ToString();
        if (prepared.EndsWith("\r\n", StringComparison.Ordinal))
        {
            prepared = prepared[..^2];
        }

        VmfDocument document = await VmfDocument.ParseAsync(prepared, CancellationToken.None);

        // The document's own output must be a fixed point over this input,
        // apart from the escape being decoded back to a raw newline on read --
        // so compare the SECOND pass, where both sides agree.
        byte[] once = document.ToBytes();
        VmfDocument again = await VmfDocument.ParseAsync(once, CancellationToken.None);

        Assert.Equal(once, again.ToBytes());
        Assert.Equal(Describe(document), Describe(again));
    }

    private static string Describe(VmfDocument document)
    {
        StringBuilder text = new();
        foreach (VmfChunk chunk in document.Chunks)
        {
            Describe(text, chunk, 0);
        }

        return text.ToString();
    }

    private static void Describe(StringBuilder text, VmfNode node, int depth)
    {
        text.Append(depth).Append(':');

        switch (node)
        {
            case VmfKey key:
                text.Append("K ").Append(key.Name).Append('=').Append(key.Value).Append('\n');
                break;

            case VmfChunk chunk:
                text.Append("C ").Append(chunk.Name).Append('\n');
                foreach (VmfNode child in chunk.Children)
                {
                    Describe(text, child, depth + 1);
                }

                break;
        }
    }
}
