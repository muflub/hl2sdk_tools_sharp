using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Compile;

/// <summary>
/// Where a <see cref="MapCompiler"/> compile gets its map: a <c>.vmf</c> or
/// <c>.vmm</c> on a file system, or a VMF document already in memory.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>FromModel(MapFile)</c>. A loaded <see cref="MapFile"/> is not
/// self-contained: loading it fills the texinfo, texdata, material and
/// instance tables of the <see cref="VbspContext"/> it was loaded into
/// (<c>map.cpp</c>'s globals, now explicit), and the compile must run in that
/// same context. So the unit a host hands over is the document, and the
/// compile loads it into a context it owns. A generated map still needs no
/// VMF TEXT in between: build a <see cref="VmfDocument"/> in code and use
/// <see cref="FromDocument"/>.
/// </para>
/// </remarks>
public abstract class MapSource
{
    private protected MapSource(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>
    /// The map's name: the file name with its extension stripped
    /// (<c>vbsp.cpp:919-927</c>). It names the outputs (<c>name.bsp</c>,
    /// <c>name.prt</c>, ...), lower-cased it is vbsp's <c>mapbase</c>, and it
    /// is the level name vrad reads <c>name.rad</c> by.
    /// </summary>
    public string Name { get; }

    /// <summary>A map file on a file system.</summary>
    /// <param name="files">The file system the map, and any instances it pulls in, are read from.</param>
    /// <param name="path">The <c>.vmf</c> or <c>.vmm</c>.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> names no file.</exception>
    public static MapSource FromVmf(IFileSystem files, VPath path)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (path.IsEmpty)
        {
            throw new ArgumentException("a map path is required", nameof(path));
        }

        return new FileSource(files, path, StripExtension(path.FileName));
    }

    /// <summary>A VMF document already in memory, parsed or built in code.</summary>
    /// <param name="document">The document. The compile reads it and does not change it.</param>
    /// <param name="name">The map's name (see <see cref="Name"/>).</param>
    /// <param name="instanceFiles">
    /// Where <c>func_instance</c> maps are read from, or null for a document
    /// with none (an instance it names is then reported missing, as stock
    /// reports a file it cannot open).
    /// </param>
    /// <param name="basePath">The path instances resolve against, when <paramref name="instanceFiles"/> is given.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public static MapSource FromDocument(
        VmfDocument document,
        string name,
        IFileSystem? instanceFiles = null,
        VPath basePath = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new DocumentSource(document, name, instanceFiles, basePath);
    }

    /// <summary>Loads the map into a compile's context.</summary>
    internal abstract Task<MapFile> LoadAsync(VbspContext context, CancellationToken cancellationToken);

    private static string StripExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private sealed class FileSource(IFileSystem files, VPath path, string name) : MapSource(name)
    {
        internal override Task<MapFile> LoadAsync(VbspContext context, CancellationToken cancellationToken) =>
            new MapFileReader(context, files).LoadAsync(path, cancellationToken);
    }

    private sealed class DocumentSource(
        VmfDocument document, string name, IFileSystem? instanceFiles, VPath basePath) : MapSource(name)
    {
        internal override Task<MapFile> LoadAsync(VbspContext context, CancellationToken cancellationToken) =>
            new MapFileReader(context, instanceFiles ?? new InMemoryFileSystem())
                .LoadDocumentAsync(document, basePath, cancellationToken);
    }
}
