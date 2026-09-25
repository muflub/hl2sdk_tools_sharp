using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// A tiny mounted content tree for the material facts: some VMTs, some VTFs,
/// no disk.
/// </summary>
/// <remarks>
/// Built on <see cref="InMemoryFileSystem"/> and a real
/// <see cref="DirectoryContentMount"/> rather than a stub content file system,
/// so the case folding and the resolution order under test are the production
/// ones. The stand-in is the CONTENT, not the layer.
/// </remarks>
internal sealed class MaterialContent
{
    private readonly InMemoryFileSystem _disk = new();

    /// <summary>Adds a material at <c>materials/&lt;name&gt;.vmt</c>.</summary>
    /// <param name="name">The material name, without prefix or extension.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>This, for chaining.</returns>
    public MaterialContent AddMaterial(string name, string text)
    {
        _disk.AddText($"materials/{name}.vmt", text);
        return this;
    }

    /// <summary>Adds a file verbatim, for the paths a patch names itself.</summary>
    /// <param name="path">The content path.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>This, for chaining.</returns>
    public MaterialContent AddFile(string path, string text)
    {
        _disk.AddText(path, text);
        return this;
    }

    /// <summary>
    /// Adds a texture at <c>materials/&lt;name&gt;.vtf</c> with a chosen size
    /// and header reflectivity.
    /// </summary>
    /// <param name="name">The texture name, as a <c>$basetexture</c> spells it.</param>
    /// <param name="width">The header's width.</param>
    /// <param name="height">The header's height.</param>
    /// <param name="reflectivity">The header's reflectivity, three floats.</param>
    /// <returns>This, for chaining.</returns>
    public MaterialContent AddTexture(
        string name,
        int width,
        int height,
        (float R, float G, float B) reflectivity)
    {
        byte[] bytes = MapFormats.Assets.VtfTests.Synthetic(
            ImageFormat.Dxt1, width, height, mips: 1, envMap: false);

        // the reference implementation's reflectivity is at byte 32, which VtfTests pins with its own
        // fact; writing it here rather than through a builder keeps the two
        // tests reading the same layout.
        Span<byte> span = bytes;
        MemoryMarshal.Write(span[32..], reflectivity.R);
        MemoryMarshal.Write(span[36..], reflectivity.G);
        MemoryMarshal.Write(span[40..], reflectivity.B);

        _disk.AddFile($"materials/{name}.vtf", bytes);
        return this;
    }

    /// <summary>Adds a file that is not a valid VTF at a texture's path.</summary>
    /// <param name="name">The texture name.</param>
    /// <returns>This, for chaining.</returns>
    public MaterialContent AddCorruptTexture(string name)
    {
        _disk.AddText($"materials/{name}.vtf", "this is not a texture");
        return this;
    }

    /// <summary>Mounts everything added so far.</summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The mounted content.</returns>
    public async Task<ContentFileSystem> MountAsync(CancellationToken cancellationToken = default)
    {
        DirectoryContentMount mount =
            await DirectoryContentMount.MountAsync(_disk, VPath.Empty, cancellationToken);

        return new ContentFileSystem([mount]);
    }
}
