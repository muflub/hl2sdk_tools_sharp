//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Validation;

/// <summary>
/// Everything <see cref="BspValidator"/> found in one map.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this being data rather than an exception is that it holds
/// EVERY finding, not the first. The engine cannot do that -- <c>Host_Error</c>
/// and <c>Sys_Error</c> stop the load -- so a map with six broken rules takes
/// six engine launches to diagnose and one call to this.
/// </para>
/// <para>
/// Findings are <see cref="CompileDiagnostic"/>, so a host that already renders
/// compiler output renders these with no extra code.
/// </para>
/// </remarks>
public sealed class ValidationReport
{
    /// <summary>A report holding the findings given, in the order they were found.</summary>
    /// <param name="diagnostics">The findings.</param>
    public ValidationReport(ImmutableArray<CompileDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;

        int errors = 0;
        int warnings = 0;
        foreach (CompileDiagnostic diagnostic in Diagnostics)
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    break;
                case DiagnosticSeverity.Warning:
                    warnings++;
                    break;
                case DiagnosticSeverity.Info:
                default:
                    break;
            }
        }

        ErrorCount = errors;
        WarningCount = warnings;
    }

    /// <summary>Every finding, in the order the rules ran.</summary>
    public ImmutableArray<CompileDiagnostic> Diagnostics { get; }

    /// <summary>How many findings are <see cref="DiagnosticSeverity.Error"/>.</summary>
    public int ErrorCount { get; }

    /// <summary>How many findings are <see cref="DiagnosticSeverity.Warning"/>.</summary>
    public int WarningCount { get; }

    /// <summary>
    /// Whether the engine would load this map and show everything in it.
    /// </summary>
    /// <remarks>
    /// Warnings count against clean deliberately. A map whose static props are
    /// silently dropped because the <c>sprp</c> version is 3 loads perfectly and
    /// is missing every prop, which is exactly the failure this instrument
    /// exists to catch: "it loaded" is not the bar.
    /// </remarks>
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;

    /// <summary>Every finding reported under one code.</summary>
    /// <param name="code">A code from <see cref="BspRuleCodes"/>.</param>
    /// <returns>The findings, possibly none.</returns>
    public ImmutableArray<CompileDiagnostic> ForCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        ImmutableArray<CompileDiagnostic>.Builder matches = ImmutableArray.CreateBuilder<CompileDiagnostic>();
        foreach (CompileDiagnostic diagnostic in Diagnostics)
        {
            if (diagnostic.Code == code)
            {
                matches.Add(diagnostic);
            }
        }

        return matches.ToImmutable();
    }
}
