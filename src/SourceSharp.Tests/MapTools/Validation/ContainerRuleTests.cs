using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// One fact per container rule: break one field of the golden map and require
/// exactly that rule's code.
/// </summary>
public class ContainerRuleTests
{
    [Corrupts(BspRuleCodes.Ident)]
    public async Task AWrongIdentIsRejectedBeforeAnythingIsParsed()
    {
        // The ident gate happens before there
        // is a container at all, which is why it lives on CheckFileAsync.
        byte[] bytes = Corrupted.GoldenBytes();
        bytes[0] = (byte)'X';

        using MemoryStream stream = new(bytes);
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Corrupted.OnlyFires(report, BspRuleCodes.Ident);
        Severity.Is(report, BspRuleCodes.Ident, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AFileTooShortForAHeaderIsTheSameFinding()
    {
        using MemoryStream stream = new([0x56, 0x42]);
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Corrupted.OnlyFires(report, BspRuleCodes.Ident);
    }

    [Corrupts(BspRuleCodes.FileVersion)]
    public async Task AFileVersionBelow19IsRejected()
    {
        //, MINBSPVERSION..BSPVERSION.
        BspData bsp = await Corrupted.GoldenAsync();
        bsp.FileVersion = 18;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.FileVersion);
        Severity.Is(report, BspRuleCodes.FileVersion, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AFileVersionAbove21IsRejectedTooAndFromTheBytes()
    {
        // The ceiling moved from BSPVERSION (20) to 21 — the branch-era
        // loaders read 21 and the format presets write it — so the byte-level
        // gate now has to be probed above 21, not above 20.
        byte[] bytes = Corrupted.GoldenBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 22);

        using MemoryStream stream = new(bytes);
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Corrupted.OnlyFires(report, BspRuleCodes.FileVersion);
    }

    [Fact]
    public async Task AVersionTwentyOneFileValidatesRatherThanBeingRejected()
    {
        // The cap-raise fact: this is the exact byte-level shape that the
        // pre-T4 gate rejected (the ceiling was BspData.Version, 20), and the
        // 21 a preset-written map carries. The golden's own lumps are all
        // version-clean, so the whole rule set — header gate included — must
        // call it clean. The oracle-clean facts over ref/catmaps-pp pin the
        // same thing against real reference-tool output; this pins it without the corpus.
        byte[] bytes = Corrupted.GoldenBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 21);

        using MemoryStream stream = new(bytes);
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Assert.True(report.IsClean, string.Join("; ",
            report.Diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}")));
    }

    [Fact]
    public async Task AVersion21MapWithTheL4d2LumpDirectoryIsValidates()
    {
        // The other shape a preset legitimately writes: version 21 with the
        // L4D2 re-layout, where every 16-byte entry is the same fields slid
        // one dword right (version leads). Detection is the reader's parity
        // heuristic on dword@8 — the planes entry's version, 0 — so the
        // re-layout must be applied to the bytes, not just requested. The
        // The reference-tool oracle (l4d2/flag-l4d2, read clean through SaveAsync-shaped
        // bytes) pins the same claim against real output; this pins the
        // rule set's acceptance of the shape without the corpus.
        byte[] bytes = Corrupted.GoldenBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 21);
        for (int lump = 0; lump < 64; lump++)
        {
            Span<byte> entry = bytes.AsSpan(8 + (lump * 16), 16);
            // (ofs, len, ver, uncomp) -> (ver, ofs, len, uncomp)
            int first = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
            int second = BinaryPrimitives.ReadInt32LittleEndian(entry[4..8]);
            int third = BinaryPrimitives.ReadInt32LittleEndian(entry[8..12]);
            BinaryPrimitives.WriteInt32LittleEndian(entry[..4], third);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..8], first);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..12], second);
        }

        // dword@8 is now the planes version, the only thing the reader keys
        // the re-layout on.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        using MemoryStream stream = new(bytes);
        BspData reread = await BspFile.LoadAsync(stream);
        Assert.True(reread.SourceLumpsUseL4d2Layout);

        stream.Position = 0;
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Assert.True(report.IsClean, string.Join("; ",
            report.Diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}")));
    }

    [Fact]
    public async Task AL4d2OnlyLumpDirectoryBelowVersion21IsStillRejected()
    {
        // The negative half of the cap raise: the re-layout belongs to 21
        // alone (T0's matrix — every sub-21 reference-tool output is a standard
        // directory), and detection keys on the version as hard as on the
        // parity check. Stamping the same rotated directory at 20 leaves it
        // undetectable, the reader reads nonsense offsets straight, and the
        // rule set must catch the wreck — the raise did not open a door for
        // a map no tool writes.
        byte[] bytes = Corrupted.GoldenBytes();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 20);
        for (int lump = 0; lump < 64; lump++)
        {
            Span<byte> entry = bytes.AsSpan(8 + (lump * 16), 16);
            int first = BinaryPrimitives.ReadInt32LittleEndian(entry[..4]);
            int second = BinaryPrimitives.ReadInt32LittleEndian(entry[4..8]);
            int third = BinaryPrimitives.ReadInt32LittleEndian(entry[8..12]);
            BinaryPrimitives.WriteInt32LittleEndian(entry[..4], third);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..8], first);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..12], second);
        }

        // Rejection by either door counts: the reader's own bounds checks can
        // refuse the nonsense placements outright, or the container rules can
        // light up on the wrecked payloads. What must not happen is a clean
        // bill for a directory no tool writes below 21.
        using MemoryStream stream = new(bytes);
        ValidationReport? report = null;
        try
        {
            report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);
        }
        catch (SourceSharp.MapFormats.InvalidBspException)
        {
            // Rejected at the container gate.
        }

        Assert.True(report is null || !report.IsClean, "a v20 re-layout validated clean");
    }

    [Corrupts(BspRuleCodes.LumpElementSize)]
    public async Task ALumpThatIsNotAWholeNumberOfItsElementIsReported()
    {
        // The "funny lump size" gate.
        // VertNormals is picked because nothing else in the rule set reads it,
        // so the finding cannot be anything but this rule.
        BspData bsp = await Corrupted.GoldenAsync();
        ReadOnlySpan<byte> normals = bsp[BspLump.VertNormals].Data.Span;
        Corrupted.Replace(bsp, BspLump.VertNormals, normals[..^1].ToArray(), 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LumpElementSize);
        Severity.Is(report, BspRuleCodes.LumpElementSize, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.LumpCap)]
    public async Task ALumpAboveItsMaxMapCapIsReported()
    {
        //, "Map has too many areas".
        // MAX_MAP_AREAS is 256.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.Areas, new byte[257 * 8], 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LumpCap);
        Severity.Is(report, BspRuleCodes.LumpCap, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AVisibilityLumpAboveItsByteCapIsTheSameRule()
    {
        // The visibility lump is capped on BYTES, not elements
        //, so it needs its own path through
        // the same rule.
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] huge = new byte[0x1000004];
        BinaryPrimitives.WriteInt32LittleEndian(huge, 0);
        Corrupted.Replace(bsp, BspLump.Visibility, huge, 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.Fires(report, BspRuleCodes.LumpCap);
    }

    [Corrupts(BspRuleCodes.RequiredLumpEmpty)]
    public async Task ALumpTheCollisionLoaderRequiresMayNotBeEmpty()
    {
        //, "Map with no textures".
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.TexData, [], 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.RequiredLumpEmpty);
        Severity.Is(report, BspRuleCodes.RequiredLumpEmpty, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.PakFileLast)]
    public async Task ThePakFileMustBeTheLastLumpInTheFile()
    {
        // Moving the pak's
        // directory entry to the front of the file leaves every other lump
        // starting after it.
        byte[] bytes = Corrupted.GoldenBytes();
        int entry = 8 + ((int)BspLump.PakFile * 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), 1036);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 16);

        using MemoryStream stream = new(bytes);
        BspData bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PakFileLast);
        Severity.Is(report, BspRuleCodes.PakFileLast, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task APakFileRuleIsSkippedForAContainerThatWasNeverReadFromAFile()
    {
        // SourceLayout describes the FILE, and a container built in memory has
        // none. The rule must stay silent rather than guess, because lump order
        // is decided when it is written.
        BspData bsp = await Corrupted.GoldenAsync();
        BspData rebuilt = new() { FileVersion = bsp.FileVersion };
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            rebuilt[i] = bsp[i];
        }

        ValidationReport report = await Corrupted.CheckAsync(rebuilt);

        Assert.True(report.ForCode(BspRuleCodes.PakFileLast).IsEmpty);
    }

    [Corrupts(BspRuleCodes.SurfEdgeCount)]
    public async Task AMapWithNoSurfedgesIsRejected()
    {
        //, "bad surfedges count": the one
        // lump with a LOWER bound as well as a MAX_MAP_* one.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.SurfEdges, [], 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SurfEdgeCount);
        Severity.Is(report, BspRuleCodes.SurfEdgeCount, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.TexDataStringNul)]
    public async Task TheTexdataStringDataMustEndInANul()
    {
        // Every material name is read
        // out of this lump as a C string, so without the final NUL the last
        // one's strlen walks off the end of the lump.
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] strings = Corrupted.EditBytes(bsp, BspLump.TexDataStringData);
        strings[^1] = (byte)'X';

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.TexDataStringNul);
        Severity.Is(report, BspRuleCodes.TexDataStringNul, DiagnosticSeverity.Error);
    }
}
