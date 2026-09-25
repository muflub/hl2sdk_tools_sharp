using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// One entry of a <c>lights.rad</c> file, matching the reference's
/// <c>texlight_t</c>.
/// </summary>
/// <param name="Name">
/// The material name, stored with its original casing. The duplicate check
/// during parsing is case-SENSITIVE while the lookup is case-INSENSITIVE, so
/// the casing is observable.
/// </param>
/// <param name="Intensity">
/// The light, already converted from gamma to linear with an exponent of 2.2
/// and already multiplied by the global <c>-scale</c>.
/// </param>
/// <param name="SourceFile">
/// Which file defined it. The reference keeps this only so it can tell the
/// three override cases apart in its warnings.
/// </param>
/// <param name="ValueParsed">
/// Whether <c>LightForString</c> would have returned true. False for a line
/// whose value did not scan as 1, 3, 4 or 8 numbers -- and the entry is STORED
/// ANYWAY, because the reference loader ignores the return value.
/// </param>
public sealed record TexLight(
    string Name,
    Vec3 Intensity,
    string SourceFile,
    bool ValueParsed);
