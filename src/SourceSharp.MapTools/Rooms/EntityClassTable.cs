//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What one entity of a class costs a running map.</summary>
/// <remarks>
/// The four kinds the rooms design names for the entity budget. Only
/// <see cref="Edict"/> and <see cref="SpawnTransient"/> count against the
/// 2048-edict cap; <see cref="ServerOnly"/> takes a slot in the server's
/// entity list and nothing else; <see cref="CompileOnly"/> never reaches the
/// linked map at all.
/// </remarks>
public enum EntityCost
{
    /// <summary>A networked entity: one edict for as long as it lives. The default for any class the table does not name.</summary>
    Edict = 0,

    /// <summary>A server-only ("logical") entity: a slot in the server's entity list, no edict.</summary>
    ServerOnly = 1,

    /// <summary>
    /// An entity that removes itself while the map spawns. It holds an edict
    /// only during the spawn, but the spawn's peak has to fit under the cap
    /// too, so the budget counts it as an edict (the safe direction).
    /// </summary>
    SpawnTransient = 2,

    /// <summary>An entity the tools consume: the linker strips it, so it costs nothing.</summary>
    CompileOnly = 3,
}

/// <summary>How sure a row of the class table is.</summary>
public enum EntityClassCertainty
{
    /// <summary>Read off this repository's code: the tools do exactly this with the class.</summary>
    Certain = 0,

    /// <summary>General Source knowledge that this repository cannot check (no game code, no FGD).</summary>
    Believed = 1,

    /// <summary>Supplied by the library or the owner's mod for its own classes.</summary>
    OwnerSupplied = 2,
}

/// <summary>One row of the entity class table.</summary>
/// <param name="ClassName">The class, matched exactly (ordinal), as vbsp matches the classes it consumes.</param>
/// <param name="Cost">What one entity of the class costs.</param>
/// <param name="Certainty">How sure the row is.</param>
/// <param name="Source">Where the row comes from, in words: the code that does it, or who supplied it.</param>
public sealed record EntityClassRow(string ClassName, EntityCost Cost, EntityClassCertainty Certainty, string Source);

/// <summary>
/// Which entity classes count against the engine's 2048-edict cap, and
/// which do not: the classification the entity budget of a linked level is
/// counted with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a table.</b> Whether a class takes an edict is decided by game
/// code, and this repository has none, nor an FGD (the bundled
/// <c>gameinfo.txt</c> files name one, but none is present and nothing reads
/// it). So the linker cannot derive the classification; it is given one.
/// The table maps a class name to an <see cref="EntityCost"/>, with each
/// row's certainty and source, and every class it does not name counts as
/// <see cref="EntityCost.Edict"/>. That default over-counts whatever the
/// game really does, which is the safe direction for a budget: a level the
/// link passes cannot be over the cap because of a class the table got
/// wrong.
/// </para>
/// <para>
/// <b>What ships.</b> Only rows this repository can vouch for: the classes
/// the tools consume at compile time (<see cref="EntityCost.CompileOnly"/>,
/// every one <see cref="EntityClassCertainty.Certain"/>, each with the code
/// that consumes it). vbsp clears them all, so a compiled room's entity
/// lump never holds one; the link strips any that a hand-made BSP does
/// carry, and never counts them. No <see cref="EntityCost.ServerOnly"/> or
/// <see cref="EntityCost.SpawnTransient"/> row ships, although the logic and
/// filter classes (believed server-only) and unnamed lights, AI nodes and
/// decals (believed to remove themselves at spawn) are the usual examples:
/// believing them would under-count if the game differs, and the rooms
/// design leaves those rows to the library or the owner's mod, whose
/// contract assembly declares each of its classes one way or the other.
/// <see cref="With"/> is how such rows are added; they override the
/// shipped rows of the same class.
/// </para>
/// <para>
/// <b>Not in the table yet.</b> <c>func_viscluster</c> is compile-only for
/// the tools (nothing reads it after vvis), but it carries a brush model,
/// and a room with a second model is refused by the link until brush
/// entities are linked; whether a game defines the class is also
/// uncertain. It counts as an edict until then.
/// </para>
/// <para>
/// A table is a value: two links in one process may use different tables,
/// and nothing is shared or cached between them.
/// </para>
/// </remarks>
public sealed class EntityClassTable
{
    /// <summary>The engine's edict cap: networked entity slots a running map has.</summary>
    /// <remarks>
    /// The owner's constraint (decision D7) and general Source knowledge, not
    /// something this repository can verify.
    /// </remarks>
    public const int EdictCap = 2048;

    /// <summary>
    /// The edicts left for the game at runtime when neither the link nor the
    /// library sets a reserve: players, bots, weapons, view models,
    /// projectiles, ragdolls, pickups, spawned NPCs.
    /// </summary>
    /// <remarks>
    /// The rooms design's recommendation (O17), accepted by the owner: a
    /// 32-slot server holds a few hundred edicts for its players and their
    /// gear, plus bursts of projectiles and effects, and 512 covers that
    /// with some margin. A guess per game, not a measurement; the mod should
    /// measure its peak and set <see cref="RoomLibraryOptions.EntityReserveKey"/>.
    /// </remarks>
    public const int DefaultReserve = 512;

    /// <summary>
    /// The server's entity list, which holds every entity (edict or not):
    /// believed to be 4096 handles in Source 2013; uncertain per game. The
    /// link warns past it rather than refusing.
    /// </summary>
    public const int EntityHandles = 4096;

    private static readonly ImmutableDictionary<string, EntityClassRow> Shipped = Build(
    [
        // Left out of every room by the library split: it describes a room
        // and is not part of one (RoomLibraryVmf.Split). Certain.
        CompileOnly(RoomLibraryVmf.RoomEntity, "the library split leaves it out of every room"),

        // Its brushes join the world and the entity is cleared (MapFileLoader). Certain.
        CompileOnly("func_detail", "vbsp moves its brushes to the world and clears it"),

        // Its sample is recorded for the cubemap lump and the entity cleared (MapFileLoader). Certain.
        CompileOnly("env_cubemap", "vbsp records the cubemap sample and clears it"),

        // An unnamed overlay is cleared; a named one becomes an
        // info_overlay_accessor, a different class that the table does not
        // name, so it counts (OverlaySet.AddFromEntity). Certain.
        CompileOnly("info_overlay", "vbsp clears it, or renames a named one to info_overlay_accessor"),

        // Cleared once its water overlay is recorded (MapFileLoader). Certain.
        CompileOnly("info_overlay_transition", "vbsp records the water overlay and clears it"),

        // Its sides are recorded and the entity cleared (MapFileLoader.HandleNoDynamicShadowsEntity). Certain.
        CompileOnly("info_no_dynamic_shadow", "vbsp records the sides and clears it"),

        // Cleared once the instance parameters are read (MapFileLoader). Certain.
        CompileOnly("func_instance_parms", "vbsp reads the instance parameters and clears it"),

        // Blanked once its instance is merged (MapFileReader.CheckForInstances). Certain.
        CompileOnly("func_instance", "vbsp merges the instance and blanks it"),

        // Into the static prop game lump, then cleared (StaticPropEmitter). Certain.
        CompileOnly("prop_static", "vbsp writes it to the static prop lump and clears it"),

        // Read as a static prop's lighting origin, then cleared (StaticPropEmitter). Certain.
        CompileOnly("info_lighting", "vbsp reads it as a lighting origin and clears it"),

        // Into the detail prop game lump, then cleared (DetailPropEmitter). Certain.
        CompileOnly("prop_detail", "vbsp writes it to the detail prop lump and clears it"),

        // Into the detail prop game lump, then cleared (DetailPropEmitter). Certain.
        CompileOnly("prop_detail_sprite", "vbsp writes it to the detail prop lump and clears it"),

        // A point of interest: the room compile takes it out of the room
        // before vbsp and carries it in the navigation (RoomPois.Extract),
        // so it costs no entity. Certain.
        CompileOnly(RoomPois.Entity, "the room compile moves it into the navigation and out of the map"),
    ]);

    private readonly ImmutableDictionary<string, EntityClassRow> _rows;

    private EntityClassTable(ImmutableDictionary<string, EntityClassRow> rows) => _rows = rows;

    /// <summary>The table as this repository ships it: the compile-only rows, and every other class an edict.</summary>
    public static EntityClassTable Default { get => new(Shipped); }

    /// <summary>Every row, by class name.</summary>
    public IReadOnlyDictionary<string, EntityClassRow> Rows => _rows;

    /// <summary>What one entity of a class costs: its row's, or <see cref="EntityCost.Edict"/> for a class with no row.</summary>
    /// <param name="className">The class as the entity lump spells it; an entity with no class is an empty string.</param>
    /// <returns>The cost.</returns>
    public EntityCost Classify(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        return _rows.TryGetValue(className, out EntityClassRow? row) ? row.Cost : EntityCost.Edict;
    }

    /// <summary>A table with further rows, each replacing any row of the same class.</summary>
    /// <param name="rows">The rows to add: the library's or the mod's classes.</param>
    /// <returns>The new table; this one is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/>, a row, or a row's class is null.</exception>
    public EntityClassTable With(IEnumerable<EntityClassRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ImmutableDictionary<string, EntityClassRow>.Builder builder = _rows.ToBuilder();
        foreach (EntityClassRow row in rows)
        {
            ArgumentNullException.ThrowIfNull(row, nameof(rows));
            ArgumentNullException.ThrowIfNull(row.ClassName, nameof(rows));
            builder[row.ClassName] = row;
        }

        return new EntityClassTable(builder.ToImmutable());
    }

    private static EntityClassRow CompileOnly(string className, string source) =>
        new(className, EntityCost.CompileOnly, EntityClassCertainty.Certain, source);

    private static ImmutableDictionary<string, EntityClassRow> Build(EntityClassRow[] rows) =>
        rows.ToImmutableDictionary(r => r.ClassName, StringComparer.Ordinal);
}
