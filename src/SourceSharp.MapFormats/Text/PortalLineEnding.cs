//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// Which line ending a <c>.prt</c> is written with.
/// </summary>
/// <remarks>
/// This is a real choice rather than a preference. The reference toolchain
/// opens the file in TEXT mode — <c>fopen(filename, "w")</c> — so a
/// <c>.prt</c> from stock vbsp on Windows has CRLF line endings, and one from
/// a Linux build has LF, from the same <c>fprintf(pf, "\n")</c>. Both load:
/// the reference portal reader's whitespace directives swallow either. A
/// byte-exact comparison against a stock file therefore has to know which
/// platform wrote it, so the caller says.
/// </remarks>
public enum PortalLineEnding
{
    /// <summary>A bare newline: what a Linux build of vbsp emits.</summary>
    Lf = 0,

    /// <summary>
    /// A carriage return and a newline: what the Windows toolset emits, since
    /// the C runtime translates on a text-mode handle.
    /// </summary>
    CrLf,
}
