namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A material: a KeyValues file whose root key names the shader.
/// </summary>
/// <remarks>
/// <para>
/// Nothing validates the root against a shader list at parse time -- the
/// material system simply calls <c>GetName()</c> on it later -- so a VMT is
/// exactly a KeyValues file, with <c>//</c> comments, optional quoting, and
/// escape sequences OFF (so a backslash in a texture path is literal).
/// </para>
/// <para>
/// A root named <c>patch</c> is the exception, and
/// <see cref="VmtPatchResolver"/> is where it is resolved.
/// </para>
/// </remarks>
public sealed class VmtDocument
{
    /// <summary>
    /// The root keyword that marks a patch material, matched without regard to
    /// case.
    /// </summary>
    public const string PatchKeyword = "patch";

    /// <summary>The key naming the material a patch is based on.</summary>
    public const string IncludeKey = "include";

    /// <summary>The section applied unconditionally.</summary>
    public const string InsertKey = "insert";

    /// <summary>The section applied only where the key already exists.</summary>
    public const string ReplaceKey = "replace";

    /// <summary>Creates a material around a parsed root.</summary>
    /// <param name="root">The root section.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public VmtDocument(KeyValuesNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
    }

    /// <summary>The root section, whose name is the shader.</summary>
    public KeyValuesNode Root { get; }

    /// <summary>The shader name, which is the root key's name.</summary>
    public string ShaderName => Root.Name;

    /// <summary>
    /// True when this file is a patch rather than a material.
    /// </summary>
    public bool IsPatch =>
        string.Equals(Root.Name, PatchKeyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path a patch's <c>include</c> names, or null.
    /// </summary>
    /// <remarks>
    /// Used VERBATIM as a VFS path -- the reference loader gets
    /// <c>LoadFromFile</c> with it unmodified. Nothing
    /// prepends <c>materials/</c> and nothing appends <c>.vmt</c>, which is why
    /// a real patch file reads
    /// <c>"include" "materials/nature/blendrocks.vmt"</c> in full.
    /// </remarks>
    public string? IncludePath => Root.GetString(IncludeKey);

    /// <summary>Parses a VMT from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file has no root section.</exception>
    public static async Task<VmtDocument> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        KeyValuesDocument document =
            await KeyValuesDocument.ReadAsync(stream, null, cancellationToken).ConfigureAwait(false);

        return FromDocument(document);
    }

    /// <summary>Parses a VMT already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed material.</returns>
    /// <exception cref="ChunkFileException">The file has no root section.</exception>
    public static async ValueTask<VmtDocument> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(bytes, null, cancellationToken).ConfigureAwait(false);

        return FromDocument(document);
    }

    /// <summary>Parses a VMT from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file has no root section.</exception>
    public static async ValueTask<VmtDocument> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        KeyValuesDocument document =
            await KeyValuesDocument.ParseAsync(text, null, cancellationToken).ConfigureAwait(false);

        return FromDocument(document);
    }

    /// <summary>
    /// The material's text, in the framing the reference writer uses when it
    /// packs a patched VMT into the pak.
    /// </summary>
    /// <returns>The serialised material.</returns>
    /// <remarks>
    /// <c>CreateMaterialPatch</c> builds a KeyValues tree and serialises it
    /// with <c>RecursiveSaveToFile</c> at indent level 0 -- there is not an
    /// <c>fprintf</c> anywhere in that path. So the output is tab-indented,
    /// LF-terminated, with the four-byte <c>"\t\t"</c> separator between a key
    /// and its value.
    /// </remarks>
    public string ToText()
    {
        KeyValuesDocument document = new();
        document.Roots.Add(Root);
        return document.ToText();
    }

    private static VmtDocument FromDocument(KeyValuesDocument document)
    {
        KeyValuesNode? root = document.Root;

        if (root is null)
        {
            throw new ChunkFileException("the material has no root section");
        }

        return new VmtDocument(root);
    }
}
