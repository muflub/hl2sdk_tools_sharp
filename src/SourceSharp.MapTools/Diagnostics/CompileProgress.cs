//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Diagnostics;

/// <summary>
/// How far a compile has got.
/// </summary>
/// <param name="Stage">
/// The stage's name, for example <c>vvis.PortalFlow</c>. Stable enough to
/// switch on.
/// </param>
/// <param name="Done">Work items finished.</param>
/// <param name="Total">
/// Work items in total, or zero when the stage cannot say in advance.
/// </param>
/// <remarks>
/// Structured rather than a formatted string because the two consumers want
/// different things from it: <c>ssmap</c> renders stock's <c>0...1...2...</c>
/// pacifier so existing compile logs stay recognisable, and a service renders a
/// percentage. Neither can be recovered from the other's text.
/// </remarks>
public readonly record struct CompileProgress(string Stage, long Done, long Total)
{
    /// <summary>
    /// The fraction finished in 0..1, or null when <see cref="Total"/> is not
    /// known.
    /// </summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Where a compile's running commentary goes.
/// </summary>
/// <remarks>
/// The libraries have no <c>Console</c>. <c>ssmap</c> supplies an
/// implementation that writes to its injected <see cref="TextWriter"/> and to
/// the <c>.log</c> file stock tools produce; a service supplies one that writes
/// to its own logger. An implementation that discards everything is legitimate
/// and is what most facts use.
/// </remarks>
public interface ICompileLog
{
    /// <summary>Records a line of commentary.</summary>
    /// <param name="severity">How much it matters.</param>
    /// <param name="message">The text, already formatted, culture-invariant.</param>
    void Write(DiagnosticSeverity severity, string message);

    /// <summary>Records a structured diagnostic about the map.</summary>
    /// <param name="diagnostic">What the compiler wants to say.</param>
    void Report(CompileDiagnostic diagnostic);
}
