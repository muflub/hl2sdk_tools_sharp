using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// I2 of the port's plan §2: <c>ssmap diff A.bsp B.bsp</c>, a per-lump semantic
/// comparison of two maps.
/// </summary>
/// <remarks>
/// <para>
/// The instrument that says whether the port produced the same map. Its own
/// acceptance bar is the one the plan sets for every check here: it must be
/// able to FAIL. A diff that cannot distinguish one changed entity keyvalue,
/// one moved plane, one flipped PVS bit and one perturbed lightmap sample is
/// worth nothing, so there is a mutation fact for each.
/// </para>
/// <para>
/// Three comparison kinds, because "the same map" means three different things
/// across the 64 lumps -- see <see cref="DiffKind"/> -- and a fourth state for
/// the lumps this instrument has no semantic comparison for, so that the report
/// is total and a gap is visible as a gap.
/// </para>
/// <para>
/// <b>No thresholds are built in.</b> Every limit is a
/// <see cref="DiffOptions"/> field that defaults to null, and null produces a
/// <see cref="ThresholdVerdict.Unset"/> line rather than a pass. That follows
/// the plan's rule that a tolerance is frozen from the first measurement in the
/// phase that owns the lump, and Phase 0's finding that in the threaded regime
/// a per-sample lightmap maximum cannot be set at all.
/// </para>
/// </remarks>
public static class BspDiff
{
    /// <summary>
    /// Compares two maps, measuring everything and judging nothing.
    /// </summary>
    /// <param name="a">The first map.</param>
    /// <param name="b">The second map.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The report.</returns>
    public static Task<DiffReport> CompareAsync(BspData a, BspData b, CancellationToken cancellationToken) =>
        CompareAsync(a, b, DiffOptions.ReportOnly, cancellationToken);

    /// <summary>
    /// Compares two maps.
    /// </summary>
    /// <param name="a">The first map, called A in the report.</param>
    /// <param name="b">The second map, called B in the report.</param>
    /// <param name="options">
    /// The thresholds to judge against and the bounds on the report's size.
    /// Every threshold defaults to unset.
    /// </param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>One verdict per lump slot, plus the header fields.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static async Task<DiffReport> CompareAsync(
        BspData a,
        BspData b,
        DiffOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(options);

        List<DiffDifference> header = [];
        if (a.FileVersion != b.FileVersion)
        {
            header.Add(new DiffDifference(
                "BSP version",
                a.FileVersion.ToString(CultureInfo.InvariantCulture),
                b.FileVersion.ToString(CultureInfo.InvariantCulture)));
        }

        if (a.MapRevision != b.MapRevision)
        {
            header.Add(new DiffDifference(
                "map revision",
                a.MapRevision.ToString(CultureInfo.InvariantCulture),
                b.MapRevision.ToString(CultureInfo.InvariantCulture)));
        }

        ImmutableArray<LumpDiff>.Builder lumps =
            ImmutableArray.CreateBuilder<LumpDiff>(BspData.HeaderLumps);

        for (int slot = 0; slot < BspData.HeaderLumps; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lumps.Add(await CompareLumpAsync(a, b, slot, options, cancellationToken).ConfigureAwait(false));
        }

        return new DiffReport(lumps.MoveToImmutable(), [.. header], options);
    }

    /// <summary>
    /// Which kind a lump slot is compared by.
    /// </summary>
    /// <param name="lump">The slot, as a lump.</param>
    /// <returns>The kind.</returns>
    /// <remarks>
    /// A total function over the enum, exposed because the coverage of this
    /// instrument is itself worth asserting: a fact walks all 64 slots and
    /// requires each to have a kind and each kind to have members.
    /// </remarks>
    public static DiffKind KindOf(BspLump lump) => lump switch
    {
        // Exact: no float freedom anywhere in these.
        BspLump.Entities
            or BspLump.TexData
            or BspLump.TexInfo
            or BspLump.Brushes
            or BspLump.BrushSides
            or BspLump.Models
            or BspLump.TexDataStringData
            or BspLump.TexDataStringTable
            or BspLump.GameLump
            or BspLump.PakFile => DiffKind.Exact,

        // Canonicalised set: the element ORDER is vbsp's insertion sequence.
        BspLump.Planes
            or BspLump.Faces
            or BspLump.FacesHdr
            or BspLump.OriginalFaces
            or BspLump.Leafs => DiffKind.CanonicalSet,

        // Distributional: bit counts and error histograms.
        BspLump.Visibility
            or BspLump.Lighting
            or BspLump.LightingHdr
            or BspLump.LeafAmbientLighting
            or BspLump.LeafAmbientLightingHdr
            or BspLump.LeafAmbientIndex
            or BspLump.LeafAmbientIndexHdr => DiffKind.Distributional,

        _ => DiffKind.NotCompared,
    };

    private static async Task<LumpDiff> CompareLumpAsync(
        BspData a,
        BspData b,
        int slot,
        DiffOptions options,
        CancellationToken cancellationToken)
    {
        BspLumpData la = a[slot];
        BspLumpData lb = b[slot];
        BspLump lump = (BspLump)slot;
        DiffKind kind = Enum.IsDefined(typeof(BspLump), slot) ? KindOf(lump) : DiffKind.NotCompared;

        LumpDiffBuilder into = new(slot, kind, la.Length, lb.Length, options)
        {
            BytesIdentical = la.Data.Span.SequenceEqual(lb.Data.Span) && la.Version == lb.Version,
        };

        if (la.Version != lb.Version)
        {
            into.Add(
                "lump version",
                la.Version.ToString(CultureInfo.InvariantCulture),
                lb.Version.ToString(CultureInfo.InvariantCulture));
        }

        switch (kind)
        {
            case DiffKind.Exact:
                await CompareExactAsync(a, b, lump, into, cancellationToken).ConfigureAwait(false);
                break;

            case DiffKind.CanonicalSet:
                CompareCanonical(a, b, lump, into, options);
                break;

            case DiffKind.Distributional:
                CompareDistributional(a, b, lump, into, options);
                break;

            default:
                NotCompared(lump, la, lb, into);
                break;
        }

        return into.Build();
    }

    private static async Task CompareExactAsync(
        BspData a,
        BspData b,
        BspLump lump,
        LumpDiffBuilder into,
        CancellationToken cancellationToken)
    {
        switch (lump)
        {
            case BspLump.Entities:
                ExactLumpDiff.Entities(a, b, into);
                break;

            case BspLump.TexData:
                ExactLumpDiff.Elements<MapFormats.Bsp.Structs.DTexData>(
                    a[lump], b[lump], "texdata", into);
                break;

            case BspLump.TexInfo:
                ExactLumpDiff.Elements<MapFormats.Bsp.Structs.TexInfo>(
                    a[lump], b[lump], "texinfo", into);
                break;

            case BspLump.Brushes:
                ExactLumpDiff.Elements<MapFormats.Bsp.Structs.DBrush>(
                    a[lump], b[lump], "brush", into);
                break;

            case BspLump.BrushSides:
                ExactLumpDiff.Elements<MapFormats.Bsp.Structs.DBrushSide>(
                    a[lump], b[lump], "brushside", into);
                break;

            case BspLump.Models:
                ExactLumpDiff.Models(a, b, into);
                break;

            case BspLump.TexDataStringData:
            case BspLump.TexDataStringTable:
                into.Note =
                    "compared as the material NAME list the two lumps spell together, so an "
                    + "offset table packed differently around the same strings is not a difference";
                ExactLumpDiff.MaterialNames(a, b, into);
                break;

            case BspLump.GameLump:
                into.Note = "compared as the nested directory plus the sprp model dictionary";
                ExactLumpDiff.GameLumps(a, b, into);
                break;

            case BspLump.PakFile:
                into.Note = "compared as the file list: name, uncompressed size, CRC and method";
                await ExactLumpDiff.PakFileAsync(a, b, into, cancellationToken).ConfigureAwait(false);
                break;

            default:
                ExactLumpDiff.Bytes(a[lump], b[lump], lump.ToString(), into);
                break;
        }
    }

    private static void CompareCanonical(
        BspData a,
        BspData b,
        BspLump lump,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        switch (lump)
        {
            case BspLump.Planes:
                CanonicalLumpDiff.Planes(a, b, into, options);
                break;

            case BspLump.Leafs:
                CanonicalLumpDiff.LeafPartition(a, b, into, options);
                break;

            default:
                CanonicalLumpDiff.Faces(a, b, lump, into, options);
                break;
        }
    }

    private static void CompareDistributional(
        BspData a,
        BspData b,
        BspLump lump,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        switch (lump)
        {
            case BspLump.Visibility:
                DistributionalLumpDiff.Visibility(a, b, into, options);
                break;

            case BspLump.Lighting:
                DistributionalLumpDiff.Lighting(a, b, lump, BspLump.Faces, into, options);
                break;

            case BspLump.LightingHdr:
                // FACES_HDR is written only when the HDR faces differ from the
                // LDR ones, so an absent HDR face lump means the
                // HDR samples are addressed by the LDR faces' lightofs.
                DistributionalLumpDiff.Lighting(
                    a,
                    b,
                    lump,
                    a[BspLump.FacesHdr].IsEmpty ? BspLump.Faces : BspLump.FacesHdr,
                    into,
                    options);
                break;

            case BspLump.LeafAmbientIndex:
            case BspLump.LeafAmbientIndexHdr:
                DistributionalLumpDiff.LeafAmbientIndex(a, b, lump, into);
                break;

            default:
                DistributionalLumpDiff.LeafAmbient(a, b, lump, into, options);
                break;
        }
    }

    /// <summary>
    /// Says why a lump has no semantic comparison, and compares its bytes
    /// anyway.
    /// </summary>
    /// <remarks>
    /// Every one of these is a lump whose content is indices into lumps the
    /// canonical kinds already cover, an opaque blob, or a deprecated slot. A
    /// byte difference in one of them is still reported -- it just is not
    /// interpreted.
    /// </remarks>
    private static void NotCompared(BspLump lump, BspLumpData a, BspLumpData b, LumpDiffBuilder into)
    {
        into.Note = lump switch
        {
            BspLump.Vertexes or BspLump.Edges or BspLump.SurfEdges =>
                "no comparison of its own: this lump is the face lumps' vertex ring, and the "
                + "canonical face comparison already reads it by VALUE",
            BspLump.Nodes or BspLump.LeafFaces or BspLump.LeafBrushes =>
                "no comparison of its own: pure indices into lumps the canonical kinds cover",
            BspLump.PhysCollide or BspLump.PhysDisp or BspLump.PhysCollideSurface =>
                "cooked vphysics blobs; no managed reader exists and the engine validates none "
                + "of it",
            BspLump.Unused0 or BspLump.Unused1 or BspLump.Unused2 or BspLump.Unused3 =>
                "never assigned by bspfile.h; nothing is known about its contents",
            BspLump.XZipPakFile => "deprecated Xbox 1 xzip pak; never written",
            _ => "no semantic comparison in I2 yet; bytes compared",
        };

        into.Note += ".";

        // The bytes are still judged, so a difference here is never silent.
        ExactLumpDiff.Bytes(a, b, lump.ToString(), into);
    }
}
