//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The switches an entry is meant to be compiled with, per tool.
///
/// <para>
/// STOCK COMMAND-LINE SWITCHES, as strings, rather than the
/// <c>VbspOptions</c>/<c>VvisOptions</c>/<c>VradOptions</c> records themselves.
/// Two reasons, and the second is the one that matters:
/// </para>
///
/// <para>
/// 1. `SourceSharp.MapGen` does not reference `SourceSharp.MapTools`, where
/// those records live, and making it do so would drag the compiler library into
/// a generator that only writes text.
/// </para>
///
/// <para>
/// 2. The switch is what a person types and what the stock tools are run with
/// under wine, so an entry declared in switches can be handed to STOCK and to
/// managed unchanged. The option records are the managed side's spelling of the
/// same thing, and the crossing is checked rather than assumed: a fact in
/// SourceSharp.Tests — which references both — parses every switch declared here
/// through <c>StockArgs</c> and requires it to be understood. A typo'd switch
/// would otherwise sit in the declaration for four phases and only be noticed
/// when somebody ran it by hand.
/// </para>
/// </summary>
public sealed record CatalogCompileOptions
{
    /// <summary>The stock defaults: no switches, on any tool.</summary>
    public static CatalogCompileOptions Default { get; } = new();

    /// <summary>Switches passed to vbsp, without the map path.</summary>
    public IReadOnlyList<string> Vbsp { get; init; } = [];

    /// <summary>Switches passed to vvis, without the map path.</summary>
    public IReadOnlyList<string> Vvis { get; init; } = [];

    /// <summary>Switches passed to vrad, without the map path.</summary>
    public IReadOnlyList<string> Vrad { get; init; } = [];

    /// <summary>Every switch this entry declares, on any tool, paired with its tool.</summary>
    public IEnumerable<(CompileTool Tool, IReadOnlyList<string> Args)> ByTool()
    {
        yield return (CompileTool.Vbsp, Vbsp);
        yield return (CompileTool.Vvis, Vvis);
        yield return (CompileTool.Vrad, Vrad);
    }
}
