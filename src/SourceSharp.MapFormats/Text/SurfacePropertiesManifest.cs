namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The surfaceproperties manifest: the list of surfaceproperties files the
/// compile-time loader reads.
/// </summary>
/// <remarks>
/// <para>
/// A KeyValues file with one root section holding repeated <c>file</c> keys.
/// The ROOT'S NAME IS NEVER VALIDATED by either consumer -- the compile-time
/// one constructs its <c>KeyValues</c> with the manifest path as the name and
/// <c>LoadFromFile</c> overwrites it with whatever the file's first token is,
/// and nothing ever compares it.
/// </para>
/// <para>
/// The reference compile-time reader is permissive and the game's is strict,
/// which is worth knowing because a mod that compiles fine can still fail to
/// run: the compile-time reader ignores a non-<c>file</c> key, a missing
/// manifest and a missing listed file, all in silence, while the game's reader
/// warns on the first and calls <c>Error</c> on the other two.
/// </para>
/// </remarks>
public sealed class SurfacePropertiesManifest
{
    /// <summary>
    /// The manifest's path, the same hardcoded literal for both readers.
    /// </summary>
    public const string ManifestPath = "scripts/surfaceproperties_manifest.txt";

    /// <summary>
    /// The key naming each surfaceproperties file; the reference reader
    /// matches it without regard to case.
    /// </summary>
    public const string FileKey = "file";

    /// <summary>
    /// The surface a material falls back to when its <c>$surfaceprop</c> does
    /// not resolve.
    /// </summary>
    /// <remarks>
    /// Also written literally into the BSP's physics material table for a prop
    /// with no surface index.
    /// </remarks>
    public const string DefaultSurfaceName = "default";

    /// <summary>
    /// The listed files, as game-relative paths, in the order they appeared.
    /// </summary>
    /// <remarks>
    /// The manifest does NOT prepend <c>scripts/</c>: the value is the path
    /// as-is, and real manifests spell it out in full
    /// (<c>"file" "scripts/surfaceproperties.txt"</c>). Order matters because
    /// later files add to and can shadow earlier surfaces.
    /// </remarks>
    public IList<string> Files { get; } = [];

    /// <summary>
    /// The keys that were not <c>file</c>, which the reference compile-time
    /// reader ignores and the game warns about.
    /// </summary>
    /// <remarks>
    /// Recorded rather than dropped so a caller can produce the game's
    /// diagnostic -- "Manifest '%s' with bogus file type '%s', expecting
    /// 'file'" -- at compile time, where it is cheap to fix, rather than at
    /// map load.
    /// </remarks>
    public IList<string> UnexpectedKeys { get; } = [];

    /// <summary>Reads a manifest from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async Task<SurfacePropertiesManifest> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        KeyValuesDocument document =
            await KeyValuesDocument.ReadAsync(stream, null, cancellationToken).ConfigureAwait(false);

        return FromKeyValues(document);
    }

    /// <summary>Parses a manifest from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static async ValueTask<SurfacePropertiesManifest> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(text, null, cancellationToken).ConfigureAwait(false);

        return FromKeyValues(document);
    }

    /// <summary>Builds the manifest from an already-parsed KeyValues tree.</summary>
    /// <param name="document">The parsed file.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static SurfacePropertiesManifest FromKeyValues(KeyValuesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        SurfacePropertiesManifest manifest = new();
        KeyValuesNode? root = document.Root;

        if (root is null)
        {
            return manifest;
        }

        foreach (KeyValuesNode child in root.Children)
        {
            if (string.Equals(child.Name, FileKey, StringComparison.OrdinalIgnoreCase))
            {
                if (child.Value is not null)
                {
                    manifest.Files.Add(child.Value);
                }

                continue;
            }

            manifest.UnexpectedKeys.Add(child.Name);
        }

        return manifest;
    }
}
