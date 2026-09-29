//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

namespace SourceSharp.RoomContracts;

/// <summary>One class of the mod contract, as the entity budget counts it.</summary>
/// <param name="ClassName">The class name.</param>
/// <param name="Networked">Whether one entity of it takes an edict; false for a server-only class.</param>
public sealed record ModEntityClass(string ClassName, bool Networked);

/// <summary>
/// The contract's version and its worldspawn keys: what a linked map says
/// about the entities it was written with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules for every mod class.</b> A class is added here, with a
/// <see cref="Version"/> bump, before the linker emits it. Every mod class
/// has a stock fallback the linker can emit instead, so a map built without
/// <c>-mod-entities</c> runs on any Source game. Mod classes are server-only
/// unless stated otherwise, so they cost no edicts, and the entity budget's
/// class table lists them from <see cref="Classes"/>. Keys the linker fills
/// are linker-owned; the mod treats a missing key as its default, never as
/// an error.
/// </para>
/// <para>
/// <b>The worldspawn keys.</b> A map linked with <c>-mod-entities</c> carries
/// <see cref="EntitiesKey"/> = <see cref="Mod"/> and <see cref="VersionKey"/>
/// = <see cref="Version"/> on its worldspawn (and so does the flattened VMF,
/// so the whole-map compile carries them too), and a mod can refuse or warn
/// on a version it does not know. A map linked without the flag carries
/// neither key: its entities are stock, which is what a map without them
/// means, and a level that uses nothing of the contract links to exactly
/// the bytes it did before the contract existed. <see cref="Stock"/> is
/// the value the key would have, for a reader that wants to name the mode.
/// </para>
/// </remarks>
public static class ModEntityContract
{
    /// <summary>The contract's version: raised whenever a class, key, input or output is added or changed.</summary>
    public const int Version = 1;

    /// <summary>The worldspawn key naming the emission mode.</summary>
    public const string EntitiesKey = "ssmap_entities";

    /// <summary>The worldspawn key holding <see cref="Version"/>.</summary>
    public const string VersionKey = "ssmap_entities_version";

    /// <summary>The mode value of a map linked with the mod's classes.</summary>
    public const string Mod = "mod";

    /// <summary>The mode value of a map of stock entities only (the meaning of a map without the key).</summary>
    public const string Stock = "stock";

    /// <summary>Every class the linker can emit under <c>-mod-entities</c>, with what it costs.</summary>
    /// <remarks>
    /// <c>logic_level_transition</c> joined the list with the transition rooms
    /// without a version bump: the design's section 7, which version 1 names
    /// as the contract, specified it from the start, so a mod built to
    /// version 1 already knows it; the linker only began writing it later.
    /// </remarks>
    public static ImmutableArray<ModEntityClass> Classes { get; } =
    [
        new ModEntityClass(LogicRoom.ClassName, LogicRoom.Networked),
        new ModEntityClass(LevelTransition.ClassName, LevelTransition.Networked),
    ];
}
