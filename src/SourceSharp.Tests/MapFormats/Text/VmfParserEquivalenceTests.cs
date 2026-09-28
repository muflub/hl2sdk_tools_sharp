//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.Tests.MapFormats.Text.Legacy;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// The VMF tokenizer and document parse, run side by side with the frozen
/// copies in <see cref="LegacyVmfParser"/> on the same input: the rewritten
/// ones must produce the same tokens, at the same line numbers, the same tree,
/// and the same failure at the same place.
/// </summary>
/// <remarks>
/// <para>
/// The load-time work changed how the tokenizer finds a string's end (one
/// vectorised search instead of a character loop), how a token's text is
/// made (shared with a recent equal token instead of always cut fresh) and how
/// an operator's text is made (a literal instead of a new string). None of it
/// may change an answer, and "the same as before" is the specification, so the
/// before is kept and asked.
/// </para>
/// <para>
/// The edge cases are the malformed and awkward inputs the format's quirks
/// live in -- quotes, escapes, comments, unterminated blocks and strings,
/// duplicate keys, CRLF, a byte-order mark, empty values, the 1023-character
/// string chunk -- and then every VMF in the repository, which is the input
/// that matters most.
/// </para>
/// </remarks>
public class VmfParserEquivalenceTests
{
    /// <summary>The awkward inputs, each named for what it exercises.</summary>
    public static TheoryData<string, string> EdgeCases()
    {
        string longValue1022 = new('a', 1022);
        string longValue1023 = new('b', 1023);
        string longValue1024 = new('c', 1024);
        string longValue2046 = new('d', 2046);
        string longValue2047 = new('e', 2047);

        return new TheoryData<string, string>
        {
            { "empty file", "" },
            { "whitespace only", " \t\r\n\0 \n" },
            { "one chunk", "world\n{\n\t\"classname\" \"worldspawn\"\n}\n" },
            { "nested chunks", "world\n{\n\tsolid\n\t{\n\t\tside\n\t\t{\n\t\t\t\"id\" \"1\"\n\t\t}\n\t}\n}\n" },
            { "CRLF line endings", "world\r\n{\r\n\t\"a\" \"b\"\r\n\t\"c\" \"d\"\r\n}\r\n" },
            { "CR only", "world\r{\r\"a\" \"b\"\r}\r" },
            { "carriage return inside a string", "world\n{\n\"a\" \"b\rc\"\n}\n" },
            { "newline inside a string", "world\n{\n\"a\" \"b\nc\"\n}\n" },
            { "empty value", "world\n{\n\"a\" \"\"\n}\n" },
            { "empty key and value", "world\n{\n\"\" \"\"\n}\n" },
            { "duplicate keys", "world\n{\n\"a\" \"1\"\n\"a\" \"2\"\n\"A\" \"3\"\n}\n" },
            { "duplicate chunks", "world\n{\nsolid\n{\n}\nsolid\n{\n}\n}\nworld\n{\n}\n" },
            { "unterminated block", "world\n{\n\"a\" \"b\"\n" },
            { "unterminated nested block", "world\n{\nsolid\n{\n\"a\" \"b\"\n}\n" },
            { "unterminated string", "world\n{\n\"a\" \"never closed\n}\n" },
            { "unterminated key", "world\n{\n\"never closed\n" },
            { "name with no value at EOF", "world\n{\n\"a\"" },
            { "stray closing brace", "}\n" },
            { "extra closing brace", "world\n{\n}\n}\n" },
            { "key at depth zero", "\"a\" \"b\"\n" },
            { "line comment", "// leading comment\nworld // trailing\n{\n// inside\n\"a\" \"b\" // after\n}\n" },
            { "comment at EOF with no newline", "world\n{\n}\n// last" },
            { "lone slash is eaten", "world\n/{\n\"a\" / \"b\"\n}\n" },
            { "long comment over 1024 characters", "// " + new string('x', 1100) + " \"a\"\nworld\n{\n}\n" },
            { "comment of exactly 1024 characters", "//" + new string('y', 1022) + "\nworld\n{\n}\n" },
            { "escaped n", "world\n{\n\"a\" \"line\\nbreak\"\n}\n" },
            { "escaped t", "world\n{\n\"a\" \"tab\\there\"\n}\n" },
            { "escaped backslash", "world\n{\n\"a\" \"back\\\\slash\"\n}\n" },
            { "escaped quote", "world\n{\n\"a\" \"say \\\"hi\\\"\"\n}\n" },
            { "trailing backslash", "world\n{\n\"a\" \"end\\\\\"\n}\n" },
            { "forward-slash path", "world\n{\n\"material\" \"TOOLS/TOOLSNODRAW\"\n}\n" },
            { "plus joins strings", "world\n{\n\"a\" \"abc\" + \"def\"\n}\n" },
            { "plus across lines", "world\n{\n\"a\" \"abc\"\n+\n\"def\"\n}\n" },
            { "plus before a non-string", "world\n{\n\"a\" \"abc\" + b\n}\n" },
            { "identifier values", "world\n{\nclassname worldspawn\n}\n" },
            { "integer value falls through", "world\n{\n\"a\" 12\n}\n" },
            { "negative integer", "world\n{\n\"a\" -12\n}\n" },
            { "malformed number", "world\n{\n\"a\" 1-2\n}\n" },
            { "number glued to letters", "world\n{\n\"a\" 12abc\n}\n" },
            { "operators", "world\n{\n\"a\" = \"b\"\n}\n" },
            { "every operator", "@,!&*$.=:[](){}\\" },
            { "unknown characters", "world;#\n{\n}\n" },
            { "NUL bytes", "world\0\n{\0\"a\"\0\"b\"\n}\n" },
            { "Latin-1 bytes", "world\n{\n\"a\" \"caf\u00e9 \u00ff\"\n}\n" },
            { "string of 1022", "world\n{\n\"a\" \"" + longValue1022 + "\"\n}\n" },
            { "string of 1023", "world\n{\n\"a\" \"" + longValue1023 + "\"\n}\n" },
            { "string of 1024", "world\n{\n\"a\" \"" + longValue1024 + "\"\n}\n" },
            { "string of 2046", "world\n{\n\"a\" \"" + longValue2046 + "\"\n}\n" },
            { "string of 2047", "world\n{\n\"a\" \"" + longValue2047 + "\"\n}\n" },
            { "unterminated 1022 at EOF", "world\n{\n\"a\" \"" + longValue1022 },
            { "unterminated 1023 at EOF", "world\n{\n\"a\" \"" + longValue1023 },
            { "unterminated 1024 at EOF", "world\n{\n\"a\" \"" + longValue1024 },
            { "unterminated 2046 at EOF", "world\n{\n\"a\" \"" + longValue2046 },
            { "escape at a chunk boundary", "world\n{\n\"a\" \"" + new string('f', 1022) + "\\nxyz\"\n}\n" },
            { "CR after a chunk boundary", "world\n{\n\"a\" \"" + new string('g', 1030) + "\rz\"\n}\n" },
            { "long key", "world\n{\n\"" + new string('k', 1500) + "\" \"v\"\n}\n" },
            { "tokens just under the share limit", "world\n{\n\"" + new string('h', 32) + "\" \"" + new string('i', 32) + "\"\n}\n" },
            { "tokens just over the share limit", "world\n{\n\"" + new string('h', 33) + "\" \"" + new string('i', 33) + "\"\n}\n" },
            { "chunk name as a string", "\"world\"\n{\n}\n" },
            { "chunk open without a name", "{\n}\n" },
        };
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void TheTokenStreamIsUnchanged(string name, string text)
    {
        _ = name;
        AssertSameTokens(text);
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void TheParseIsUnchanged(string name, string text)
    {
        _ = name;
        AssertSameParse(text);
    }

    [Fact]
    public void AByteOrderMarkParsesTheSameWay()
    {
        // A UTF-8 BOM is three Latin-1 characters to this reader, none of them
        // letters it knows: whatever the old reader made of them, so must the
        // new one.
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "world\r\n{\r\n\t\"a\" \"b\"\r\n}\r\n"u8];

        AssertSameParse(bytes);
        AssertSameTokens(Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void AByteOrderMarkInsideAValueIsKept()
    {
        byte[] bytes = [.. "world\n{\n\"a\" \""u8, 0xEF, 0xBB, 0xBF, .. "b\"\n}\n"u8];

        AssertSameParse(bytes);
    }

    [Fact]
    public void ManyDistinctShortTokensSurviveTheTokenCache()
    {
        // More distinct short tokens than the cache has slots, interleaved
        // with repeats, so slots are hit, missed and overwritten many times:
        // every value must still come back as its own text.
        StringBuilder text = new("world\n{\n");
        for (int i = 0; i < ChunkTokenReader.RecentTokenSlots * 3; i++)
        {
            text.Append("\t\"k").Append(i % 97).Append("\" \"v").Append(i).Append("\"\n");
            text.Append("\t\"rotation\" \"0\"\n");
        }

        text.Append("}\n");

        AssertSameTokens(text.ToString());
        AssertSameParse(text.ToString());
    }

    [Fact]
    public async Task AStreamThatCannotSeekReadsTheSame()
    {
        byte[] bytes = "world\n{\n\t\"a\" \"b\"\n\tsolid\n\t{\n\t}\n}\n"u8.ToArray();
        using NonSeekableStream stream = new(bytes);

        VmfDocument document = await VmfDocument.ReadAsync(stream);

        AssertSameTree(LegacyVmfParser.Parse(bytes), document);
    }

    [Fact]
    public async Task AStreamPartlyReadIsParsedFromWhereItStands()
    {
        // The seekable path sizes its buffer from Length - Position; the
        // bytes before Position are not the caller's document.
        byte[] document = "world\n{\n\t\"a\" \"b\"\n}\n"u8.ToArray();
        byte[] bytes = [.. "junk"u8, .. document];
        using MemoryStream stream = new(bytes);
        stream.Position = 4;

        VmfDocument parsed = await VmfDocument.ReadAsync(stream);

        AssertSameTree(LegacyVmfParser.Parse(document), parsed);
    }

    [Fact]
    public async Task AnEmptyStreamIsAnEmptyDocument()
    {
        using MemoryStream stream = new();

        VmfDocument parsed = await VmfDocument.ReadAsync(stream);

        Assert.Empty(parsed.Chunks);
    }

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task EveryVmfInTheRepositoryParsesTheSameWay()
    {
        string root = RepositoryRoot()!;
        List<string> relatives =
        [
            .. Directory.EnumerateFiles(root, "*.vmf", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .Where(p => !IsBuildOrToolOutput(p))
                .Order(StringComparer.Ordinal),
        ];

        // Only a check if it has something to check: the sandbox map and the
        // full-size perf map at least.
        Assert.Contains("maps/ss_sandbox.vmf", relatives);
        Assert.Contains("maps/sdk_ctf_2fort.vmf", relatives);

        foreach (string relative in relatives)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, relative));

            AssertSameParse(bytes);
            AssertSameTokens(Encoding.Latin1.GetString(bytes));

            using MemoryStream stream = new(bytes);
            AssertSameTree(LegacyVmfParser.Parse(bytes), await VmfDocument.ReadAsync(stream));
        }
    }

    internal static void AssertSameTokens(string text)
    {
        LegacyChunkTokenReader legacy = new(text);
        ChunkTokenReader current = new(text);

        // Bounded, because a malformed input can stall on an error token.
        for (int step = 0; step < 5_000_000; step++)
        {
            ChunkTokenType expectedType = legacy.NextToken(out string expected);
            ChunkTokenType actualType = current.NextToken(out string actual);

            Assert.Equal(expectedType, actualType);
            Assert.Equal(expected, actual);
            Assert.Equal(legacy.Line, current.Line);
            Assert.Equal(legacy.EndOfFile, current.EndOfFile);

            if (expectedType == ChunkTokenType.EndOfFile)
            {
                return;
            }
        }
    }

    private static void AssertSameParse(byte[] bytes)
    {
        Outcome expected = Capture(() => LegacyVmfParser.Parse(bytes));
        Outcome actual = Capture(() => VmfDocument.ParseAsync(bytes).AsTask().GetAwaiter().GetResult());
        AssertSameOutcome(expected, actual);
    }

    internal static void AssertSameParse(string text)
    {
        Outcome expected = Capture(() => LegacyVmfParser.Parse(text));
        Outcome actual = Capture(() => VmfDocument.ParseAsync(text).AsTask().GetAwaiter().GetResult());
        AssertSameOutcome(expected, actual);
    }

    private static void AssertSameOutcome(Outcome expected, Outcome actual)
    {
        if (expected.Failure is ChunkFileException expectedFailure)
        {
            ChunkFileException actualFailure = Assert.IsType<ChunkFileException>(actual.Failure);
            Assert.Equal(expectedFailure.Result, actualFailure.Result);
            Assert.Equal(expectedFailure.Line, actualFailure.Line);
            Assert.Equal(expectedFailure.Message, actualFailure.Message);
            return;
        }

        Assert.Null(expected.Failure);
        Assert.Null(actual.Failure);
        AssertSameTree(expected.Document!, actual.Document!);
    }

    internal static void AssertSameTree(VmfDocument expected, VmfDocument actual)
    {
        Assert.Equal(expected.Chunks.Count, actual.Chunks.Count);
        for (int i = 0; i < expected.Chunks.Count; i++)
        {
            AssertSameNode(expected.Chunks[i], actual.Chunks[i]);
        }
    }

    private static void AssertSameNode(VmfNode expected, VmfNode actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Name, actual.Name);

        switch (expected)
        {
            case VmfKey key:
                Assert.Equal(key.Value, ((VmfKey)actual).Value);
                break;

            case VmfChunk chunk:
                VmfChunk other = (VmfChunk)actual;
                Assert.Equal(chunk.Children.Count, other.Children.Count);
                for (int i = 0; i < chunk.Children.Count; i++)
                {
                    AssertSameNode(chunk.Children[i], other.Children[i]);
                }

                break;
        }
    }

    private static Outcome Capture(Func<VmfDocument> parse)
    {
        try
        {
            return new Outcome(parse(), null);
        }
        catch (ChunkFileException exception)
        {
            return new Outcome(null, exception);
        }
    }

    private static string? RepositoryRoot()
    {
        string? sandbox = RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf");
        return sandbox is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(sandbox));
    }

    // Build output, tool state and worktrees hold copies, not the corpus.
    private static bool IsBuildOrToolOutput(string relative)
    {
        return relative.Split('/').Any(part =>
            part is "bin" or "obj" or ".git" or ".claude" or "node_modules");
    }

    private sealed record Outcome(VmfDocument? Document, ChunkFileException? Failure);

    // A stream that reads like a pipe: no length, no seeking.
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
