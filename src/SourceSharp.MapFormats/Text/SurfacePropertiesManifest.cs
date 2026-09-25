namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The surfaceproperties manifest: the list of surfaceproperties files vbsp
/// loads (<c>src/utils/vbsp/textures.cpp:711-735</c>).
/// </summary>
/// <remarks>
/// <para>
/// A KeyValues file with one root section holding repeated <c>file</c> keys.
/// The ROOT'S NAME IS NEVER VALIDATED by either consumer -- vbsp constructs
/// its <c>KeyValues</c> with the manifest path as the name and
/// <c>LoadFromFile</c> overwrites it with whatever the file's first token is
/// (<c>textures.cpp:720-721</c>), and nothing ever compares it.
/// </para>
/// <para>
/// vbsp's reader is permissive and the game's is strict, which is worth knowing
/// because a mod that compiles fine can still fail to run: vbsp ignores a
/// non-<c>file</c> key, a missing manifest and a missing listed file, all in
/// silence (<c>textures.cpp:721-731</c>, <c>:691-696</c>), while the game warns
/// on the first and calls <c>Error</c> on the other two
/// (<c>src/game/shared/physics_shared.cpp:970,988-989,994</c>).
/// </para>
/// </remarks>
public sealed class SurfacePropertiesManifest
{
    /// <summary>
    /// The manifest's path, a hardcoded literal in both consumers
    /// (<c>src/utils/vbsp/textures.cpp:719</c>,
    /// <c>src/game/shared/physics_shared.cpp:61</c>).
    /// </summary>
    public const string ManifestPath = "scripts/surfaceproperties_manifest.txt";

    /// <summary>
    /// The key naming each surfaceproperties file, matched without regard to
    /// case (<c>textures.cpp:726</c> uses <c>Q_stricmp</c>).
    /// </summary>
    public const string FileKey = "file";

    /// <summary>
    /// The surface a material falls back to when its <c>$surfaceprop</c> does
    /// not resolve (<c>src/utils/vbsp/textures.cpp:359,381</c>).
    /// </summary>
    /// <remarks>
    /// Also written literally into the BSP's physics material table for a prop
    /// with no surface index (<c>src/utils/vbsp/ivp.cpp:1573</c>).
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
    /// The keys that were not <c>file</c>, which vbsp ignores and the game
    /// warns about.
    /// </summary>
    /// <remarks>
    /// Recorded rather than dropped so a caller can produce the game's
    /// diagnostic -- "Manifest '%s' with bogus file type '%s', expecting
    /// 'file'" (<c>src/game/shared/physics_shared.cpp:988-989</c>) -- at
    /// compile time, where it is cheap to fix, rather than at map load.
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
