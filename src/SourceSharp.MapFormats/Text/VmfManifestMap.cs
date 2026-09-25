namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One <c>VMF</c> entry of a <c>.vmm</c> manifest, matching the reference's
/// <c>CManifestMap</c>.
/// </summary>
/// <param name="Name">
/// The <c>Name</c> key, a friendly label. vbsp READS AND DISCARDS it -- the
/// reference reads the key but never stores it.
/// </param>
/// <param name="File">
/// The <c>File</c> key: the VMF's path relative to the manifest. One of the
/// two keys vbsp actually acts on.
/// </param>
/// <param name="IsPrimary">
/// The <c>IsPrimary</c> key. Also discarded by vbsp: the reference reads it
/// but never stores it.
/// </param>
/// <param name="IsProtected">
/// The <c>IsProtected</c> key. Discarded too, the same way.
/// </param>
/// <param name="IsTopLevel">
/// The <c>TopLevel</c> key, compared as <c>atoi(value) == 1</c> -- so "2" is
/// false, not true. The other key vbsp acts on.
/// </param>
public sealed record VmfManifestMap(
    string Name,
    string File,
    bool IsPrimary,
    bool IsProtected,
    bool IsTopLevel);
