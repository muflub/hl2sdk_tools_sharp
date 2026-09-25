using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One cordon from a <c>.vmm_prefs</c> file: a port of <c>Cordon_t</c>
/// (<c>src/utils/vbsp/manifest.cpp:108-180</c>).
/// </summary>
/// <param name="Name">The cordon's name.</param>
/// <param name="Active">
/// The <c>active</c> key, read through <c>ReadKeyValueBool</c>
/// (<c>manifest.cpp:153</c>) -- so <c>atoi(value) &gt; 0</c>, and a value of
/// <c>-1</c> is FALSE.
/// </param>
/// <param name="Boxes">
/// The bounding boxes, whose <c>mins</c> and <c>maxs</c> are POINTS in the
/// parenthesised <c>(x y z)</c> spelling, not the bracketed vector one
/// (<c>manifest.cpp:126-133</c> calls <c>ReadKeyValuePoint</c>).
/// </param>
public sealed record VmfCordon(
    string Name,
    bool Active,
    IReadOnlyList<(Vec3 Mins, Vec3 Maxs)> Boxes);
