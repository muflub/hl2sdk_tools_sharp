//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen;

/// <summary>One entity as a compiled map's entity lump declares it.</summary>
/// <param name="ClassName">The entity's <c>classname</c> key value.</param>
/// <param name="TargetName">Its <c>targetname</c>, empty when unnamed.</param>
/// <param name="KeyValues">Every key the map sets on it.</param>
public sealed record DeclaredEntity(
    string ClassName,
    string TargetName,
    IReadOnlyDictionary<string, string> KeyValues);
