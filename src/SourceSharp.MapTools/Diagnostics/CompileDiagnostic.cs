namespace SourceSharp.MapTools.Diagnostics;

/// <summary>How much a diagnostic matters.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Worth knowing; the compile is unaffected.</summary>
    Info,

    /// <summary>The compile continued, but the map is probably not what was meant.</summary>
    Warning,

    /// <summary>
    /// The map cannot be compiled correctly. The compile may continue, so that
    /// a host can show every problem at once instead of one per run.
    /// </summary>
    Error,
}

/// <summary>
/// Somewhere in the source map, as precisely as the stage can say.
/// </summary>
/// <param name="EntityId">The VMF entity id, when the problem belongs to one.</param>
/// <param name="BrushId">The VMF solid id, when the problem belongs to one.</param>
/// <param name="Position">A world position, when there is one.</param>
/// <remarks>
/// All three are optional and often only one is known. The point of carrying
/// them as data rather than formatting them into the message is that a host can
/// select the brush in an editor, which a string cannot do.
/// </remarks>
public readonly record struct MapLocation(
    int? EntityId = null,
    int? BrushId = null,
    (float X, float Y, float Z)? Position = null);

/// <summary>
/// Something the compiler wants to say about the map it was given.
/// </summary>
/// <param name="Code">
/// A stable identifier such as <c>VBSP0004</c>. Stable is the whole point: a
/// host keys on this, so it may never be reused for a different meaning, and
/// the message text may be reworded freely.
/// </param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">Human-readable text, culture-invariant.</param>
/// <param name="Location">Where in the map, as far as is known.</param>
/// <remarks>
/// A bad map produces these; a bug in the compiler throws. Keeping that line
/// sharp is what lets the library run inside a long-lived service: stock's
/// <c>Error()</c> calls <c>exit()</c>, which is precisely why the RPG plan
/// needed one process per compile.
/// </remarks>
public sealed record CompileDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    MapLocation Location = default);
