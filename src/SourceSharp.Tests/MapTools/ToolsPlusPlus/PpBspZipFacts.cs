using System.Buffers.Binary;
using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.ToolsPlusPlus;

/// <summary>
/// bspzip++ compression interop (plan tools++ §T6): vbsp++ never writes a
/// compressed lump — only <c>bspzip++ -repack -compress</c> does — so the
/// pak/lump handling this port has is never exercised against a ++-compressed
/// map by the vbsp/vvis/vrad tiers. These facts close that gap from both ends:
/// our writer's output survives ++'s compressor, and what our loader can and
/// cannot observe of ++'s compressed container is pinned.
/// </summary>
/// <remarks>
/// <para>
/// The products come from <c>tools/t6-bspzip-interop.sh</c> (see its header);
/// the facts consume them from <c>&lt;PP_CATMAPS_DIR&gt;-t6/t6bz/</c> and name
/// the regeneration command when absent.
/// </para>
/// <para>
/// <b>Why lump contents, not file bytes.</b> Measured: <c>-repack</c> (with or
/// without <c>-compress</c>) re-lays the container and appends one all-zero
/// directory entry per pass to the game lump's nested directory (count
/// 2 → 3 on compress, → 4 on the decompress repack; the file grows
/// 1,003,608 → 1,004,088 bytes on this map). Every other byte of every other
/// lump is preserved exactly — that is the round-trip gate below, 63 lumps
/// byte-equal plus the header version, the game lump asserted entry-wise.
/// </para>
/// </remarks>
public sealed class PpBspZipFacts(ITestOutputHelper output)
{
    [PpBspZipFact]
    public void PlusPlusCompressIsDeterministic()
    {
        byte[] a = File.ReadAllBytes(Path.Combine(PpCrossToolHarness.ZipDir, "comp.bsp"));
        byte[] b = File.ReadAllBytes(Path.Combine(PpCrossToolHarness.ZipDir, "comp2.bsp"));
        Assert.True(a.AsSpan().SequenceEqual(b), "two ++ -repack -compress runs over identical input differ");
    }

    [PpBspZipFact]
    public void OurWrittenMapSurvivesCompressDecompressAtLumpContentsLevel()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(PpCrossToolHarness.ZipDir, "input.bsp"));
        byte[] roundtrip = File.ReadAllBytes(Path.Combine(PpCrossToolHarness.ZipDir, "roundtrip.bsp"));
        Assert.Equal(ReadInt(input, 4), ReadInt(roundtrip, 4)); // header version

        for (int lump = 0; lump < 64; lump++)
        {
            if (lump == (int)BspLump.GameLump)
            {
                continue; // ++ pads the nested directory; asserted separately
            }

            ReadOnlySpan<byte> a = Lump(input, lump);
            ReadOnlySpan<byte> b = Lump(roundtrip, lump);
            Assert.True(a.SequenceEqual(b), $"lump {lump} differs after ++ compress/decompress");
        }

        // The game lump: same named entries, byte-equal payloads; the only
        // delta is the all-zero padding entries ++ appends per repack pass.
        List<(string Name, int Length)> ours = GameLumpEntries(input);
        List<(string Name, int Length)> theirs = GameLumpEntries(roundtrip);
        Assert.Equal(ours.Count, theirs.Count - 2); // one padding entry per pass
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(ours[i].Name, theirs[i].Name);
            Assert.Equal(ours[i].Length, theirs[i].Length);
        }

        for (int i = ours.Count; i < theirs.Count; i++)
        {
            Assert.Equal("\0\0\0\0", theirs[i].Name);
            Assert.Equal(0, theirs[i].Length);
        }
    }

    [PpBspZipFact]
    public async Task OurLoaderStopsAtPlusPlusGamelumpPadding_T6Debt()
    {
        // Pinned debt (T6 finding #2), not an endorsement: bspzip++ appends
        // zero-length unnamed directory entries to the game lump on every
        // repack pass, and our loader throws on an unnamed entry at LOAD time,
        // so it cannot yet read a ++-compressed or ++-repacked map at all —
        // contents listing included (bspzip++ round-trips these maps itself).
        // When the loader learns to tolerate zero-length unnamed entries this
        // fact FAILS, and its replacement is the full loader-path contents
        // gate (bzload over comp.bsp/roundtrip.bsp) beside the raw-path facts
        // below. A second gap sits behind this one: the compressed map's pak
        // lump is an LZMA container our reader stores undecoded (finding #3).
        string comp = Path.Combine(PpCrossToolHarness.ZipDir, "comp.bsp");
        await using FileStream f = File.OpenRead(comp);
        InvalidBspException ex = await Assert.ThrowsAsync<InvalidBspException>(
            () => BspFile.LoadAsync(f));
        output.WriteLine($"loader refuses the ++-compressed map: {ex.Message}");
        Assert.Contains("game lump", ex.Message, StringComparison.Ordinal);
    }

    [PpBspZipFact]
    public void PlusPlusOwnDirOfTheDecompressedRoundtripMatchesOurListing()
    {
        // Contents-level interop: bspzip++'s own `-dir` of the decompressed
        // roundtrip (it refuses the compressed map — pinned next) lists the
        // pak entries, and reading the same lump off the same bytes with our
        // ZipArchiveReader must name the same files at the same stored
        // lengths. The zip metadata inside the pak lump is masked by
        // comparing entry names + data, not zip bytes (corpus rule).
        string[] theirs = ReadNames(Path.Combine(PpCrossToolHarness.ZipDir, "dir.txt"));
        string[] ours = ReadNames(Path.Combine(PpCrossToolHarness.ZipDir, "list-roundtrip.txt"), tab: true);
        output.WriteLine($"++: {string.Join(", ", theirs)} | ours: {string.Join(", ", ours)}");
        Assert.NotEmpty(theirs);
        Assert.Equal(theirs, ours);
    }

    [PpBspZipFact]
    public void OurRawListingOfTheRoundtripMatchesOurLoaderListingOfTheInput()
    {
        // Decompressed side: the pak contents after ++'s compress→decompress
        // must be what our loader listed on OUR writer's original bytes.
        string[] input = ReadNames(Path.Combine(PpCrossToolHarness.ZipDir, "list-input.txt"), tab: true);
        string[] ours = ReadNames(Path.Combine(PpCrossToolHarness.ZipDir, "list-roundtrip.txt"), tab: true);
        Assert.NotEmpty(input);
        Assert.Equal(input, ours);
    }

    [PpBspZipFact]
    public void PlusPlusRefusesToReadItsOwnCompressedMap()
    {
        // Recorded ++ behaviour (bz-dircomp.out): even bspzip++'s -dir on a
        // compressed map fails with "Lump … is compressed. This is not
        // supported, decompress the map first". The interop contract for a
        // ++-compressed map is therefore decompress-first on every reader,
        // engine and tool included — context for why the loader debt above is
        // a debt and not a defect.
        string log = File.ReadAllText(Path.Combine(PpCrossToolHarness.ZipDir, "bz-dircomp.out"));
        output.WriteLine(log.Contains("decompress the map first", StringComparison.Ordinal)
            ? "++ -dir refused the compressed map as measured"
            : "++ -dir unexpectedly ACCEPTED the compressed map — re-measure");
        Assert.Contains("is compressed", log, StringComparison.Ordinal);
    }

    private static string[] ReadNames(string path, bool tab = false)
    {
        return [.. File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l.Contains('/') && l.Contains('.'))
            .Select(l => (tab ? l.Split('\t')[0] : l).ToLowerInvariant())
            .Distinct()
            .OrderBy(l => l, StringComparer.Ordinal)];
    }

    private static List<(string Name, int Length)> GameLumpEntries(byte[] file)
    {
        ReadOnlySpan<byte> lump = Lump(file, (int)BspLump.GameLump);
        int count = BinaryPrimitives.ReadInt32LittleEndian(lump);
        List<(string, int)> entries = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = lump.Slice(4 + (i * 16), 16);
            string name = System.Text.Encoding.ASCII.GetString(e.Slice(0, 4).ToArray());
            int length = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(12));
            entries.Add((name, length));
        }

        return entries;
    }

    private static ReadOnlySpan<byte> Lump(byte[] file, int lump)
    {
        int entry = 8 + (lump * 16);
        int ofs = ReadInt(file, entry);
        int len = ReadInt(file, entry + 4);
        return file.AsSpan(ofs, len);
    }

    private static int ReadInt(byte[] d, int off) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off));
}
