using System.Buffers.Binary;
using System.Collections.Immutable;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Validation;

/// <summary>
/// The engine's load-time rule set, transcribed as code: everything
/// And the detail
/// prop system demand of a map before they will load it.
/// </summary>
/// <remarks>
/// <para>
/// This is the instrument the map-compiler port is judged with. The engine's
/// own answer to a broken map is <c>Host_Error</c> or <c>Sys_Error</c>: one
/// finding, then the process stops, so six broken rules cost six launches. Here
/// every rule runs and every rule that fails is reported, as data a host can key
/// on by <see cref="CompileDiagnostic.Code"/>.
/// </para>
/// <para>
/// A rule that fails on many elements of one lump reports ONCE, naming the first
/// offender and how many there were. All RULES are evaluated; it is the
/// repetition inside a rule that is summarised, so a map with a shredded face
/// lump produces one line rather than sixty thousand.
/// </para>
/// <para>
/// Nothing here throws on a bad map. An exception from this class is a bug in
/// this class -- the line the whole diagnostics design turns on.
/// </para>
/// </remarks>
public static partial class BspValidator
{
    /// <summary>
    /// Checks one already-parsed map against every rule.
    /// </summary>
    /// <param name="bsp">The map to check.</param>
    /// <param name="cancellationToken">Cancels between rules.</param>
    /// <returns>Every finding, and whether the map is clean.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <remarks>
    /// The ident gate (<see cref="BspRuleCodes.Ident"/>) is not reachable from a
    /// <see cref="BspData"/>, because a container only exists once
    /// <see cref="BspFile.LoadAsync"/> has accepted the ident. Use
    /// <see cref="CheckFileAsync"/> for the header gate as the engine sees it.
    /// </remarks>
    public static async Task<ValidationReport> CheckAsync(
        BspData bsp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        return await Task.Run(() => Check(bsp, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a map file, starting from the header gate the engine applies
    /// before it has parsed anything.
    /// </summary>
    /// <param name="stream">A readable, seekable stream over a whole BSP file.</param>
    /// <param name="cancellationToken">Cancels the read and the rules.</param>
    /// <returns>Every finding, and whether the map is clean.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <remarks>
    /// <para>
    /// <c>CMapLoadHelper::Init</c> reads the header, rejects a wrong ident, then
    /// rejects a version outside 19..20, and only then is there anything to
    /// Load. This method reproduces that
    /// order: a file that fails the gate is reported and NOT parsed, because
    /// every later rule would be reading a file the engine never opened.
    /// </para>
    /// <para>
    /// A file that passes the gate but cannot be parsed at all -- a lump whose
    /// offset runs past the end of the file, say -- is reported under
    /// <see cref="BspRuleCodes.SubLumpFraming"/> rather than thrown.
    /// </para>
    /// </remarks>
    public static async Task<ValidationReport> CheckFileAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "the header gate reads the first eight bytes and the lumps by absolute offset, "
                + "so the stream must be seekable",
                nameof(stream));
        }

        Findings findings = new();

        byte[] header = new byte[8];
        stream.Seek(0, SeekOrigin.Begin);
        int read = await stream.ReadAtLeastAsync(header, 8, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read < 8)
        {
            findings.Add(
                BspRuleCodes.Ident,
                $"the file is {read} bytes, too short for a BSP header; the engine reads a whole "
                + "dheader_t before it looks at anything");
            return findings.ToReport();
        }

        int ident = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (ident != BspData.Ident)
        {
            findings.Add(
                BspRuleCodes.Ident,
                $"the header's ident is 0x{ident:X8}, not 0x{BspData.Ident:X8} (\"VBSP\")");
            return findings.ToReport();
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        // The reference model loader itself stops at MINBSPVERSION..BSPVERSION,
        // 19..20. The
        // ceiling here is BspData.MaxVersion, 21: the branches of that era's
        // line (L4D2, Portal 2, Ep2) read 21, and the format presets write it,
        // so 19..21 is the range a legitimately produced map can carry. The
        // reader above enforces the same range; a cap here that disagrees with
        // it rejects maps this port itself writes.
        if (version is < BspData.MinVersion or > BspData.MaxVersion)
        {
            findings.Add(
                BspRuleCodes.FileVersion,
                $"the file version is {version}; the loaders read MINBSPVERSION..21, "
                + $"{BspData.MinVersion}..{BspData.MaxVersion}");
            return findings.ToReport();
        }

        BspData bsp;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            bsp = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidBspException ex)
        {
            findings.Add(
                BspRuleCodes.SubLumpFraming,
                $"the lump directory does not describe a file that can be read: {ex.Message}");
            return findings.ToReport();
        }

        return await CheckAsync(bsp, cancellationToken).ConfigureAwait(false);
    }

    private static ValidationReport Check(BspData bsp, CancellationToken cancellationToken)
    {
        Findings findings = new();
        Counts counts = new(bsp);

        CheckHeader(bsp, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckLumpShapes(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckLumpVersions(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckGameLumps(bsp, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckIndices(bsp, counts, findings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        CheckContent(bsp, counts, findings, cancellationToken);

        return findings.ToReport();
    }

    /// <summary>
    /// The element count of every lump this validator indexes, worked out once
    /// so that a rule reads a number rather than a division.
    /// </summary>
    private sealed class Counts
    {
        public Counts(BspData bsp)
        {
            Planes = Of(bsp, BspLump.Planes);
            Vertexes = Of(bsp, BspLump.Vertexes);
            Nodes = Of(bsp, BspLump.Nodes);
            TexInfo = Of(bsp, BspLump.TexInfo);
            TexData = Of(bsp, BspLump.TexData);
            Faces = Of(bsp, BspLump.Faces);
            FacesHdr = Of(bsp, BspLump.FacesHdr);
            Leafs = Of(bsp, BspLump.Leafs);
            Edges = Of(bsp, BspLump.Edges);
            SurfEdges = Of(bsp, BspLump.SurfEdges);
            Models = Of(bsp, BspLump.Models);
            LeafFaces = Of(bsp, BspLump.LeafFaces);
            LeafBrushes = Of(bsp, BspLump.LeafBrushes);
            Brushes = Of(bsp, BspLump.Brushes);
            BrushSides = Of(bsp, BspLump.BrushSides);
            DispInfo = Of(bsp, BspLump.DispInfo);
            DispVerts = Of(bsp, BspLump.DispVerts);
            DispTris = Of(bsp, BspLump.DispTris);
            Cubemaps = Of(bsp, BspLump.Cubemaps);
            Overlays = Of(bsp, BspLump.Overlays);
            WaterOverlays = Of(bsp, BspLump.WaterOverlays);
            TexDataStringTable = Of(bsp, BspLump.TexDataStringTable);
            TexDataStringData = bsp[BspLump.TexDataStringData].Length;
        }

        public int Planes { get; }

        public int Vertexes { get; }

        public int Nodes { get; }

        public int TexInfo { get; }

        public int TexData { get; }

        public int Faces { get; }

        public int FacesHdr { get; }

        public int Leafs { get; }

        public int Edges { get; }

        public int SurfEdges { get; }

        public int Models { get; }

        public int LeafFaces { get; }

        public int LeafBrushes { get; }

        public int Brushes { get; }

        public int BrushSides { get; }

        public int DispInfo { get; }

        public int DispVerts { get; }

        public int DispTris { get; }

        public int Cubemaps { get; }

        public int Overlays { get; }

        public int WaterOverlays { get; }

        public int TexDataStringTable { get; }

        public int TexDataStringData { get; }

        /// <summary>
        /// The element count of one lump, rounding down.
        /// </summary>
        /// <remarks>
        /// Rounding down is what the engine does -- <c>count = LumpSize() /
        /// sizeof(*in)</c> after its modulus check
        /// -- so a lump that fails
        /// <see cref="BspRuleCodes.LumpElementSize"/> still yields a usable
        /// count here and the index rules keep running over it instead of the
        /// whole report stopping at the first ragged lump.
        /// </remarks>
        public static int Of(BspData bsp, BspLump lump)
        {
            BspLumpData data = bsp[lump];
            int? size = BspLumpLayout.ElementSize(lump, data.Version);
            return size is > 0 ? data.Length / size.GetValueOrDefault() : 0;
        }
    }

    /// <summary>
    /// The findings so far, with each rule's severity taken from the catalogue
    /// so that a rule's severity is stated in exactly one place.
    /// </summary>
    private sealed class Findings
    {
        private readonly ImmutableArray<CompileDiagnostic>.Builder _items =
            ImmutableArray.CreateBuilder<CompileDiagnostic>();

        public void Add(string code, string message) =>
            _items.Add(new CompileDiagnostic(code, BspRuleCatalog.ByCode(code).Severity, message));

        /// <summary>
        /// Reports a rule that failed on <paramref name="failures"/> elements,
        /// naming the first and the total.
        /// </summary>
        public void AddRepeated(string code, string firstMessage, int failures)
        {
            string message = failures <= 1
                ? firstMessage
                : $"{firstMessage} (and {failures - 1} more)";
            Add(code, message);
        }

        public ValidationReport ToReport() => new(_items.ToImmutable());
    }
}
