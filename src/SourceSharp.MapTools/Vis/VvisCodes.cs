//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Vis;

/// <summary>Diagnostic codes <see cref="Vvis"/> reports.</summary>
public static class VvisCodes
{
    /// <summary>
    /// <see cref="Options.VvisOptions.FastFlow"/> is on: the PVS is an
    /// approximation, not the portal flow's answer
    /// (<see cref="Vvis.OptionWarnings"/>).
    /// </summary>
    public const string ApproximateFlow = "VVIS0701";
}
