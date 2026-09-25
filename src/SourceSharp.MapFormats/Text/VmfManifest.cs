using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>.vmm</c> manifest: the list of VMFs a manifest map is assembled from
/// (<c>src/utils/vbsp/manifest.cpp:421-466</c>).
/// </summary>
/// <remarks>
/// <para>
/// A chunk file, not a KeyValues file -- it is read through
/// <c>CChunkFile</c>, so it has the VMF's tokenizer, its CRLF framing and its
/// comment rules. Only the file-level shape is modelled here; what vbsp does
/// with the VMFs afterwards (merging planes, brushes, sides, entities and
/// overlays through a transform) is compile work, not format work.
/// </para>
/// <para>
/// vbsp reads a sibling <c>&lt;username&gt;.vmm_prefs</c> alongside it
/// (<c>manifest.cpp:355-410</c>) for the cordoning preferences, which is why
/// two people compiling the same manifest can get different maps.
/// </para>
/// </remarks>
public sealed class VmfManifest
{
    /// <summary>
    /// The root chunk name (<c>src/utils/vbsp/manifest.cpp:435</c>).
    /// </summary>
    public const string MapsChunkName = "Maps";

    /// <summary>
    /// The per-map chunk name inside it (<c>manifest.cpp:90</c>).
    /// </summary>
    public const string VmfChunkName = "VMF";

    /// <summary>The maps, in file order.</summary>
    public IList<VmfManifestMap> Maps { get; } = [];

    /// <summary>Reads a manifest from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file is malformed.</exception>
    public static async Task<VmfManifest> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        VmfDocument document = await VmfDocument.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        return FromDocument(document);
    }

    /// <summary>Parses a manifest from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file is malformed.</exception>
    public static async ValueTask<VmfManifest> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        VmfDocument document = await VmfDocument.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        return FromDocument(document);
    }

    /// <summary>Builds the manifest from an already-parsed chunk tree.</summary>
    /// <param name="document">The parsed file.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static VmfManifest FromDocument(VmfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        VmfManifest manifest = new();

        foreach (VmfChunk maps in document.GetChunks(MapsChunkName))
        {
            foreach (VmfChunk vmf in maps.GetChunks(VmfChunkName))
            {
                // manifest.cpp:36-57. Five keys are recognised; only two are
                // acted on -- File at :44 and TopLevel at :56 -- and the other
                // three (Name, IsPrimary, IsProtected) have their handling
                // COMMENTED OUT. Parsed here because the file carries them and
                // a caller may want to show them.
                manifest.Maps.Add(new VmfManifestMap(
                    Name: vmf.GetValue("Name") ?? string.Empty,
                    File: vmf.GetValue("File") ?? string.Empty,

                    // manifest.cpp:48,56 -- both are `atoi(value) == 1`, so
                    // "2" and "-1" are both FALSE.
                    IsPrimary: VmfValue.ParseInt(vmf.GetValue("IsPrimary")) == 1,
                    IsProtected: VmfValue.ParseInt(vmf.GetValue("IsProtected")) == 1,
                    IsTopLevel: VmfValue.ParseInt(vmf.GetValue("TopLevel")) == 1));
            }
        }

        return manifest;
    }

    /// <summary>
    /// The cordons from a <c>.vmm_prefs</c> file.
    /// </summary>
    /// <param name="document">The parsed preferences file.</param>
    /// <returns>The cordons, in file order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <remarks>
    /// <c>manifest.cpp:108-227</c>: <c>cordoning</c> holds <c>cordons</c>
    /// holds repeated <c>cordon</c> chunks, each with an <c>active</c> key and
    /// repeated <c>box</c> chunks whose <c>mins</c> and <c>maxs</c> are POINTS
    /// -- parenthesised, read with <c>ReadKeyValuePoint</c> at
    /// <c>:128,132</c>, not the bracketed vector form.
    /// </remarks>
    public static IReadOnlyList<VmfCordon> ReadCordons(VmfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<VmfCordon> cordons = [];

        foreach (VmfChunk cordoning in document.GetChunks("cordoning"))
        {
            foreach (VmfChunk group in cordoning.GetChunks("cordons"))
            {
                foreach (VmfChunk cordon in group.GetChunks("cordon"))
                {
                    List<(Vec3 Mins, Vec3 Maxs)> boxes = [];

                    foreach (VmfChunk box in cordon.GetChunks("box"))
                    {
                        VmfValue.TryParsePoint(box.GetValue("mins"), out Vec3 mins);
                        VmfValue.TryParsePoint(box.GetValue("maxs"), out Vec3 maxs);
                        boxes.Add((mins, maxs));
                    }

                    cordons.Add(new VmfCordon(
                        cordon.GetValue("name") ?? string.Empty,
                        VmfValue.ParseBool(cordon.GetValue("active")),
                        boxes));
                }
            }
        }

        return cordons;
    }
}
