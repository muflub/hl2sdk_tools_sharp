//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;
using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for the VMF document reader and writer, from
/// </summary>
public class VmfDocumentTests
{
    /// <summary>
    /// A minimal but real VMF: a world with a solid with a side, plus an
    /// entity. Written with CRLF and tabs, as Hammer writes one.
    /// </summary>
    private const string SampleVmf =
        "versioninfo\r\n" +
        "{\r\n" +
        "\t\"editorversion\" \"400\"\r\n" +
        "\t\"mapversion\" \"1\"\r\n" +
        "}\r\n" +
        "world\r\n" +
        "{\r\n" +
        "\t\"id\" \"1\"\r\n" +
        "\t\"classname\" \"worldspawn\"\r\n" +
        "\tsolid\r\n" +
        "\t{\r\n" +
        "\t\t\"id\" \"2\"\r\n" +
        "\t\tside\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"material\" \"BRICK/BRICKWALL001A\"\r\n" +
        "\t\t}\r\n" +
        "\t}\r\n" +
        "}\r\n" +
        "entity\r\n" +
        "{\r\n" +
        "\t\"classname\" \"info_player_start\"\r\n" +
        "\t\"origin\" \"0 0 64\"\r\n" +
        "}\r\n";

    [Fact]
    public async Task TopLevelChunksAreSiblingsNotChildrenOfARoot()
    {
        // A VMF has no root chunk: the reference implementation calls ReadChunk in a
        // loop until EOF.
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);

        Assert.Equal(["versioninfo", "world", "entity"], document.Chunks.Select(c => c.Name));
    }

    [Fact]
    public async Task KeysAreReadInFileOrder()
    {
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);
        VmfChunk versionInfo = Assert.Single(document.GetChunks("versioninfo"));

        Assert.Equal(["editorversion", "mapversion"], versionInfo.Keys.Select(k => k.Name));
    }

    [Fact]
    public async Task NestedChunksAreReachableByName()
    {
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);
        VmfChunk side = document.GetChunk("world")!.GetChunk("solid")!.GetChunk("side")!;

        Assert.Equal("BRICK/BRICKWALL001A", side.GetValue("material"));
    }

    [Fact]
    public async Task KeyLookupIsCaseInsensitive()
    {
        // Every key handler in the tree compares with stricmp, e.g.
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);

        Assert.Equal("worldspawn", document.GetChunk("WORLD")!.GetValue("ClassName"));
    }

    [Fact]
    public async Task ParseThenWriteIsByteIdentical()
    {
        // The Phase 1c fixed-point gate, in its strongest form: the bytes that
        // went in are the bytes that come out.
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);

        Assert.Equal(Encoding.Latin1.GetBytes(SampleVmf), document.ToBytes());
    }

    [Fact]
    public async Task WriteThenParseThenWriteIsStable()
    {
        VmfDocument first = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);
        byte[] once = first.ToBytes();

        VmfDocument second = await VmfDocument.ParseAsync(once, CancellationToken.None);

        Assert.Equal(once, second.ToBytes());
    }

    [Fact]
    public async Task ReadAsyncOverAStreamMatchesParse()
    {
        using MemoryStream stream = new(Encoding.Latin1.GetBytes(SampleVmf));
        VmfDocument document = await VmfDocument.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(Encoding.Latin1.GetBytes(SampleVmf), document.ToBytes());
    }

    [Fact]
    public async Task WriteAsyncProducesTheSameBytesAsToBytes()
    {
        VmfDocument document = await VmfDocument.ParseAsync(SampleVmf, CancellationToken.None);

        using MemoryStream stream = new();
        await document.WriteAsync(stream, CancellationToken.None);

        Assert.Equal(document.ToBytes(), stream.ToArray());
    }

    [Fact]
    public async Task InterleavedKeysAndChunksKeepTheirOrder()
    {
        // The reason keys and sub-chunks share one list. Hammer never writes
        // this, but the reference implementation accepts it and a hand-edited VMF can
        // contain it.
        const string interleaved =
            "world\r\n{\r\n\t\"a\" \"1\"\r\n\tsolid\r\n\t{\r\n\t}\r\n\t\"b\" \"2\"\r\n}\r\n";

        VmfDocument document = await VmfDocument.ParseAsync(interleaved, CancellationToken.None);

        Assert.Equal(Encoding.Latin1.GetBytes(interleaved), document.ToBytes());
    }

    [Fact]
    public void WriterIndentsWithOneTabPerLevel()
    {
        // BuildIndentString, the reference implementation.
        ChunkFileWriter writer = new();
        writer.BeginChunk("world");
        writer.BeginChunk("solid");
        writer.WriteKeyValue("id", "2");
        writer.EndChunk();
        writer.EndChunk();

        Assert.Equal(
            "world\r\n{\r\n\tsolid\r\n\t{\r\n\t\t\"id\" \"2\"\r\n\t}\r\n}\r\n",
            writer.ToString());
    }

    [Fact]
    public void WriterAlwaysEmitsCarriageReturnLineFeed()
    {
        // Writes the two bytes through fwrite, so it is CRLF
        // on Linux too -- not a text-mode translation.
        ChunkFileWriter writer = new();
        writer.WriteLine("x");

        Assert.Equal("x\r\n", writer.ToString());
    }

    [Fact]
    public void ChunkNameAndItsOpeningBraceSitAtTheOuterIndent()
    {
        // BeginChunk builds "%s\r\n%s{" with the CURRENT indent and increments
        // afterwards; EndChunk decrements FIRST
        ChunkFileWriter writer = new();
        writer.BeginChunk("outer");
        writer.BeginChunk("inner");
        writer.EndChunk();
        writer.EndChunk();

        Assert.Equal("outer\r\n{\r\n\tinner\r\n\t{\r\n\t}\r\n}\r\n", writer.ToString());
    }

    [Fact]
    public void UnbalancedEndChunkClampsAtDepthZeroRatherThanThrowing()
    {
        // "if (m_nCurrentDepth > 0)".
        ChunkFileWriter writer = new();
        writer.EndChunk();

        Assert.Equal(0, writer.CurrentDepth);
        Assert.Equal("}\r\n", writer.ToString());
    }

    [Fact]
    public async Task UnclosedChunkIsUnexpectedEndOfFile()
    {
        // EOF while the depth is not zero.
        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync("world\r\n{\r\n", CancellationToken.None));

        Assert.Equal(ChunkFileResult.UnexpectedEndOfFile, error.Result);
    }

    [Fact]
    public async Task KeyWithNoValueIsUnexpectedEndOfFile()
    {
        // A name token followed by EOF, even at depth
        // zero, where a name token alone would have been a clean EOF.
        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync("world\r\n{\r\n\t\"id\"\r\n", CancellationToken.None));

        Assert.Equal(ChunkFileResult.UnexpectedEndOfFile, error.Result);
    }

    [Fact]
    public async Task UnexpectedOperatorAfterANameIsAnUnexpectedSymbolNamingThatOperator()
    {
        // An operator that is not "{".
        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync("world\r\n{\r\n\t\"id\" [\r\n", CancellationToken.None));

        Assert.Equal(ChunkFileResult.UnexpectedSymbol, error.Result);
        Assert.Equal("[", error.ErrorToken);
    }

    [Fact]
    public void IntegerAfterAKeyFallsThroughAndReportsTheKeyName()
    {
        // THE FALL-THROUGH,. The inner switch has no case for
        // INTEGER and the outer case block has no break, so control reaches
        // `case OPERATOR:` -- which tests szNAME against "}". The
        // reported token is therefore the KEY, not the number.
        ChunkFileReader reader = new(new ChunkTokenReader("\"id\" 12"));

        ChunkFileResult result = reader.ReadNext(out _, out _, out _);

        Assert.Equal(ChunkFileResult.UnexpectedSymbol, result);
        Assert.Equal("id", reader.ErrorToken);
    }

    [Fact]
    public async Task StrayClosingBraceAtTopLevelIsAnUnexpectedSymbol()
    {
        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync("}\r\n", CancellationToken.None));

        Assert.Equal(ChunkFileResult.UnexpectedSymbol, error.Result);
    }

    [Fact]
    public async Task UnterminatedStringAcrossACrLfLineIsStringTooLong()
    {
        // Reaching the reference implementation.
        ChunkFileException error = await Assert.ThrowsAsync<ChunkFileException>(
            async () => await VmfDocument.ParseAsync("world\r\n{\r\n\t\"broken\r\n\"\r\n}\r\n", CancellationToken.None));

        Assert.Equal(ChunkFileResult.StringTooLong, error.Result);
    }

    [Fact]
    public void ReaderDepthGoesUpOnAnOpenAndDownOnAClose()
    {
        // The counter that decides whether EOF is
        // clean.
        ChunkFileReader reader = new(new ChunkTokenReader("world\r\n{\r\n}\r\n"));

        reader.ReadNext(out _, out _, out _);
        Assert.Equal(1, reader.CurrentDepth);

        reader.ReadNext(out _, out _, out _);
        Assert.Equal(0, reader.CurrentDepth);
    }

    [Fact]
    public async Task ValueIsTruncatedToTheCppBufferLength()
    {
        // Q_strncpy into szValue[MAX_KEYVALUE_LEN], so a
        // value keeps 1023 characters and a terminator.
        string longValue = new('v', ChunkFileReader.MaxKeyValueLength * 2);
        VmfDocument document = await VmfDocument.ParseAsync(
            $"world\r\n{{\r\n\t\"k\" \"{longValue}\"\r\n}}\r\n", CancellationToken.None);

        string? value = document.GetChunk("world")!.GetValue("k");
        Assert.Equal(ChunkFileReader.MaxKeyValueLength - 1, value!.Length);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        // A large document, cancelled before the parse begins.
        StringBuilder big = new();
        for (int i = 0; i < 20000; i++)
        {
            big.Append("entity\r\n{\r\n\t\"classname\" \"info_target\"\r\n}\r\n");
        }

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await VmfDocument.ParseAsync(big.ToString(), source.Token));
    }

    [Fact]
    public void ParseIsNotDependentOnAnyMutableStaticState()
    {
        // Two readers over two documents on two threads must not interfere: the
        // The reference tokenizer's line number, position and stuffed token are all
        // members, and so are these.
        ChunkTokenReader first = new("alpha\r\nbeta");
        ChunkTokenReader second = new("gamma");

        first.NextToken(out _);
        second.NextToken(out _);
        first.NextToken(out string token);

        Assert.Equal("beta", token);
        Assert.Equal(2, first.Line);
        Assert.Equal(1, second.Line);
    }
}
