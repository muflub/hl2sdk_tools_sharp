namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One <c>VMF</c> entry of a <c>.vmm</c> manifest: a port of
/// <c>CManifestMap</c> (<c>src/utils/vbsp/manifest.cpp:36-60</c>).
/// </summary>
/// <param name="Name">
/// The <c>Name</c> key, a friendly label. vbsp READS AND DISCARDS it -- the
/// assignment at <c>manifest.cpp:40</c> is commented out.
/// </param>
/// <param name="File">
/// The <c>File</c> key: the VMF's path relative to the manifest
/// (<c>manifest.cpp:44</c>). One of the two keys vbsp actually acts on.
/// </param>
/// <param name="IsPrimary">
/// The <c>IsPrimary</c> key. Also discarded by vbsp
/// (<c>manifest.cpp:48</c> is commented out).
/// </param>
/// <param name="IsProtected">
/// The <c>IsProtected</c> key. Discarded too (<c>manifest.cpp:52</c>).
/// </param>
/// <param name="IsTopLevel">
/// The <c>TopLevel</c> key, compared as <c>atoi(value) == 1</c>
/// (<c>manifest.cpp:56</c>) -- so "2" is false, not true. The other key vbsp
/// acts on.
/// </param>
public sealed record VmfManifestMap(
    string Name,
    string File,
    bool IsPrimary,
    bool IsProtected,
    bool IsTopLevel);
